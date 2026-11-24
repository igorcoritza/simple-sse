using System.Diagnostics;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using OutboxDemo.Contracts;
using OutboxDemo.Orders.Data;

namespace OutboxDemo.OutboxRelay;

// The relay ("message relay" / "outbox processor"): a separate worker that polls the outbox table
// of the Orders database and publishes pending messages to Service Bus. The Orders API never talks
// to Service Bus (or to Shipping): it only writes rows.
//
// Guarantees: AT-LEAST-ONCE. If the send succeeds but the process dies before the row is marked
// as processed, the message is sent again on the next pass. That's why the receiving side must be
// idempotent (Shipping keeps an inbox table keyed by the message id).
public sealed class OutboxRelayWorker(
    IServiceScopeFactory scopes,
    ServiceBusClient serviceBus,
    ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 20;

    private static readonly ActivitySource ActivitySource = new(typeof(OutboxRelayWorker).Assembly.GetName().Name!);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var sender = serviceBus.CreateSender(Messaging.OrderPlacedQueue);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(sender, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // e.g. the database is unreachable. Nothing is lost: try again on the next pass.
                logger.LogError(ex, "Outbox relay pass failed");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PublishPendingAsync(ServiceBusSender sender, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();

        // The SQL Server provider retries transient errors; a user transaction must run inside
        // the execution strategy so the whole unit can be retried.
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // UPDLOCK + READPAST: rows are locked by this transaction and other relay instances
            // skip them instead of waiting. So several replicas of Orders can run the relay
            // without publishing the same row twice at the same time.
            var now = DateTimeOffset.UtcNow;
            var batch = await db.OutboxMessages
                .FromSql($"""
                    SELECT TOP ({BatchSize}) * FROM OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE ProcessedOnUtc IS NULL AND (NextAttemptUtc IS NULL OR NextAttemptUtc <= {now})
                    ORDER BY OccurredOnUtc
                    """)
                .ToListAsync(ct);

            foreach (var message in batch)
            {
                // Continue the trace of the HTTP request that created the message, so the Aspire
                // dashboard shows POST /orders -> relay -> Service Bus -> dispatcher -> Shipping as one trace.
                ActivityContext.TryParse(message.TraceParent, null, out var parent);
                using var activity = ActivitySource.StartActivity("outbox publish", ActivityKind.Producer, parent);
                activity?.SetTag("outbox.message_id", message.Id);

                try
                {
                    await sender.SendMessageAsync(new ServiceBusMessage(message.Payload)
                    {
                        MessageId = message.Id.ToString(), // the consumer deduplicates on this
                        Subject = message.Type,
                        ContentType = "application/json",
                    }, ct);

                    message.ProcessedOnUtc = DateTimeOffset.UtcNow;
                    message.LastError = null;
                    logger.LogInformation("Published outbox message {MessageId} ({Type})", message.Id, message.Type);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Broker down, network error... Keep the row pending and back off
                    // exponentially (2, 4, 8 ... up to 60 seconds).
                    message.Attempts++;
                    message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    message.NextAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, message.Attempts)));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    logger.LogWarning("Could not publish outbox message {MessageId} (attempt {Attempts}): {Error}",
                        message.Id, message.Attempts, ex.Message);

                    // The broker is probably unavailable: don't hammer it with the rest of the batch.
                    break;
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }
}
