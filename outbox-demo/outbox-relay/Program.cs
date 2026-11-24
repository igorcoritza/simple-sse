using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using OutboxDemo.Contracts;
using OutboxDemo.Orders.Data;
using OutboxDemo.OutboxRelay;

// Outbox relay (part of service A): Orders DB outbox table -> Azure Service Bus.
// The Azure SDK only emits OpenTelemetry spans for Service Bus behind this switch. With it, the
// Aspire dashboard shows the message hop as part of the same distributed trace.
AppContext.SetSwitch("Azure.Experimental.EnableActivitySource", true);

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<OrdersDbContext>(Messaging.OrdersDb);
builder.AddAzureServiceBusClient(Messaging.ServiceBus, configureClientBuilder: client => client.ConfigureOptions(options =>
{
    // Fail fast when the broker is down: the outbox row stays pending and the relay retries it
    // with its own backoff, so there's no need for the SDK to keep a send blocked for minutes.
    options.RetryOptions = new ServiceBusRetryOptions { MaxRetries = 1, TryTimeout = TimeSpan.FromSeconds(5) };
}));
builder.Services.AddHostedService<OutboxRelayWorker>();

builder.Build().Run();
