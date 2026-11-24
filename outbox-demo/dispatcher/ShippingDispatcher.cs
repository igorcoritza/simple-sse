using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using OutboxDemo.Contracts;

namespace OutboxDemo.Dispatcher;

// Service Bus -> Shipping API. Service Bus doesn't make HTTP calls by itself: something has to
// receive the message and call the API. That's this dispatcher (in Azure it's often an Azure
// Function with a Service Bus trigger; the logic is the same).
//
// IDEMPOTENCY. Service Bus delivers at-least-once, so the same message WILL reach this handler more
// than once: the relay may publish it twice, a lock may expire, the process may crash after calling
// Shipping but before completing the message, or someone resubmits it from the dead-letter queue.
// The dispatcher is safe to run any number of times for the same message because:
//   1. It is stateless: every decision comes from the message itself and Shipping's answer.
//   2. It sends a STABLE key: Idempotency-Key = MessageId = the outbox row id. Every copy of the
//      message, whoever publishes or resubmits it, carries the same key.
//   3. Shipping stores that key in the same transaction as the shipment, so a repeated call does
//      nothing and answers "already processed" (200 + duplicate=true, or 409 Conflict).
//   4. The dispatcher treats "already processed" as SUCCESS and completes the message, instead of
//      retrying it until it ends in the dead-letter queue.
//   5. It completes the message only AFTER Shipping answered. Crash anywhere before that and the
//      message comes back; thanks to 1-4 the second run has no extra effect.
//
// Settlement rules:
// - 2xx / 409       -> Complete: the message leaves the queue.
// - other 4xx       -> Dead-letter now: Shipping rejected it, retrying won't change the answer.
// - 5xx / no answer -> Abandon: Service Bus redelivers it. After MaxDeliveryCount attempts it moves
//                      it to the dead-letter queue by itself (reason: MaxDeliveryCountExceeded).
public sealed class ShippingDispatcher(
    ServiceBusClient serviceBus,
    IHttpClientFactory httpClients,
    DeliveryLog deliveries,
    DispatcherChaos chaos,
    ILogger<ShippingDispatcher> logger) : IHostedService, IAsyncDisposable
{
    private ServiceBusProcessor? _processor;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _processor = serviceBus.CreateProcessor(Messaging.OrderPlacedQueue, new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false, // we settle each message explicitly
            MaxConcurrentCalls = 4,
        });
        _processor.ProcessMessageAsync += HandleAsync;
        _processor.ProcessErrorAsync += args =>
        {
            logger.LogWarning(args.Exception, "Service Bus error ({Source})", args.ErrorSource);
            return Task.CompletedTask;
        };
        await _processor.StartProcessingAsync(cancellationToken);
    }

    private async Task HandleAsync(ProcessMessageEventArgs args)
    {
        var message = args.Message;

        // A message we can't even read will never succeed ("poison message"): dead-letter it right
        // away instead of retrying it MaxDeliveryCount times.
        OrderPlaced? evt;
        try
        {
            evt = JsonSerializer.Deserialize<OrderPlaced>(message.Body, JsonSerializerOptions.Web);
            ArgumentNullException.ThrowIfNull(evt);
        }
        catch (Exception ex)
        {
            await args.DeadLetterMessageAsync(message, "InvalidPayload", ex.Message);
            deliveries.Add(message.MessageId, message.DeliveryCount, DeliveryOutcome.DeadLettered, "InvalidPayload");
            return;
        }

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shipments")
            {
                Content = JsonContent.Create(evt),
            };
            request.Headers.Add("Idempotency-Key", message.MessageId);
            response = await httpClients.CreateClient("shipping").SendAsync(request, args.CancellationToken);
        }
        catch (Exception ex) when (!args.CancellationToken.IsCancellationRequested)
        {
            // Shipping is down or didn't answer in time.
            await AbandonAsync(args, $"Shipping unreachable: {ex.Message}");
            return;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                // Did Shipping do the work now, or had it already done it for this key?
                var result = response.StatusCode == HttpStatusCode.Conflict
                    ? new ShipmentResult(null, Duplicate: true)
                    : await response.Content.ReadFromJsonAsync<ShipmentResult>(args.CancellationToken);
                var alreadyProcessed = result?.Duplicate == true;

                if (chaos.CrashAfterDelivery && message.DeliveryCount == 1)
                {
                    // Simulate the worst moment to crash: Shipping has committed the shipment, but
                    // we die before telling Service Bus. The message is NOT completed, so it comes
                    // back (here right away; after a real crash, when the lock expires).
                    logger.LogWarning("Simulated crash after delivering message {MessageId}: not completing it", message.MessageId);
                    deliveries.Add(message.MessageId, message.DeliveryCount, DeliveryOutcome.SimulatedCrash,
                        "Shipping did the work, message not completed");
                    await args.AbandonMessageAsync(message);
                    return;
                }

                await args.CompleteMessageAsync(message);

                if (alreadyProcessed)
                {
                    logger.LogInformation("Message {MessageId} (delivery {DeliveryCount}) was already processed by Shipping: completed, nothing done twice",
                        message.MessageId, message.DeliveryCount);
                    deliveries.Add(message.MessageId, message.DeliveryCount, DeliveryOutcome.AlreadyProcessed, $"shipment {result?.ShipmentId}");
                }
                else
                {
                    logger.LogInformation("Order {OrderId} delivered to Shipping (message {MessageId})", evt.OrderId, message.MessageId);
                    deliveries.Add(message.MessageId, message.DeliveryCount, DeliveryOutcome.Created, $"shipment {result?.ShipmentId}");
                }
            }
            else if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not HttpStatusCode.RequestTimeout and not HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync();
                await args.DeadLetterMessageAsync(message, $"Rejected{(int)response.StatusCode}", body);
                deliveries.Add(message.MessageId, message.DeliveryCount, DeliveryOutcome.DeadLettered, $"Shipping answered {(int)response.StatusCode}");
                logger.LogWarning("Shipping rejected message {MessageId} ({Status}): dead-lettered", message.MessageId, (int)response.StatusCode);
            }
            else
            {
                await AbandonAsync(args, $"Shipping answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
        }
    }

    private async Task AbandonAsync(ProcessMessageEventArgs args, string error)
    {
        logger.LogWarning("Delivery {DeliveryCount}/{Max} of message {MessageId} failed: {Error}",
            args.Message.DeliveryCount, Messaging.MaxDeliveryCount, args.Message.MessageId, error);
        deliveries.Add(args.Message.MessageId, args.Message.DeliveryCount, DeliveryOutcome.Retry, error);

        // Small pause so the retries are visible (and don't hammer a struggling service).
        // Production code would rather reschedule the message with a growing delay.
        await Task.Delay(TimeSpan.FromSeconds(2));
        await args.AbandonMessageAsync(args.Message, new Dictionary<string, object> { ["LastError"] = error });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null) await _processor.StopProcessingAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_processor is not null) await _processor.DisposeAsync();
    }
}

// Shipping's answer to POST /api/shipments.
internal sealed record ShipmentResult(Guid? ShipmentId, bool Duplicate);
