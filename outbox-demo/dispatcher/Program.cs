using Azure.Messaging.ServiceBus;
using OutboxDemo.Contracts;
using OutboxDemo.Dispatcher;

// Dispatcher: Service Bus queue -> Shipping API (HTTP).
// The Azure SDK only emits OpenTelemetry spans for Service Bus behind this switch. With it, the
// Aspire dashboard shows the message hop as part of the same distributed trace.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient(Messaging.ServiceBus);
builder.Services.AddSingleton<DeliveryLog>();
builder.Services.AddSingleton<DispatcherChaos>();
builder.Services.AddHostedService<ShippingDispatcher>();

// HTTP client for Shipping, resolved by Aspire service discovery. Retries are Service Bus's job
// (redelivery + dead-letter), so this client doesn't retry on its own: one attempt per delivery.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is marked experimental
builder.Services.AddHttpClient("shipping", c =>
    {
        c.BaseAddress = new Uri("http://shipping");
        c.Timeout = TimeSpan.FromSeconds(10);
    })
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.MapDefaultEndpoints();
app.UseCors();

var api = app.MapGroup("/api");

// What the dispatcher did with each delivery (newest first).
api.MapGet("/deliveries", (DeliveryLog log) => log.Recent());

// Chaos switch: crash after Shipping did the work, before completing the message (first delivery only).
api.MapGet("/chaos", (DispatcherChaos chaos) => new { chaos.CrashAfterDelivery });
api.MapPut("/chaos", (DispatcherChaosSettings settings, DispatcherChaos chaos) =>
{
    chaos.CrashAfterDelivery = settings.CrashAfterDelivery;
    return new { chaos.CrashAfterDelivery };
});

// Dead-letter queue: peek (read without removing) the messages Service Bus gave up on.
api.MapGet("/dead-letters", async (ServiceBusClient serviceBus) =>
{
    await using var receiver = serviceBus.CreateReceiver(Messaging.OrderPlacedQueue,
        new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    var messages = await receiver.PeekMessagesAsync(maxMessages: 50);
    return messages.Select(m => new
    {
        m.MessageId,
        m.DeadLetterReason,
        m.DeadLetterErrorDescription,
        m.DeliveryCount,
        m.EnqueuedTime,
        Body = m.Body.ToString(),
    });
});

// Once the cause is fixed, move dead-lettered messages back to the main queue. They keep their
// MessageId, so Shipping's inbox still protects against duplicates.
api.MapPost("/dead-letters/resubmit", async (ServiceBusClient serviceBus) =>
{
    await using var receiver = serviceBus.CreateReceiver(Messaging.OrderPlacedQueue,
        new ServiceBusReceiverOptions { SubQueue = SubQueue.DeadLetter });
    await using var sender = serviceBus.CreateSender(Messaging.OrderPlacedQueue);

    var resubmitted = 0;
    while (true)
    {
        var batch = await receiver.ReceiveMessagesAsync(maxMessages: 20, maxWaitTime: TimeSpan.FromSeconds(1));
        if (batch.Count == 0) break;
        foreach (var dead in batch)
        {
            await sender.SendMessageAsync(new ServiceBusMessage(dead)); // copies body, MessageId, properties
            await receiver.CompleteMessageAsync(dead);
            resubmitted++;
        }
    }
    return new { resubmitted };
});

app.Run();

public sealed record DispatcherChaosSettings(bool CrashAfterDelivery);
