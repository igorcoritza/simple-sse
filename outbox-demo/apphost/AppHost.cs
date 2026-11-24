using OutboxDemo.Contracts;

// .NET Aspire AppHost: `dotnet run` starts everything locally and opens the Aspire dashboard.
var builder = DistributedApplication.CreateBuilder(args);

// One SQL Server container with a database per service. Each service owns its database and
// never reads the other one: they only talk through Service Bus (or, in the "naive" endpoint,
// through HTTP, to show why that's a bad idea).
var sql = builder.AddSqlServer("sql");
var ordersDb = sql.AddDatabase(Messaging.OrdersDb);
var shippingDb = sql.AddDatabase(Messaging.ShippingDb);

// Azure Service Bus, running locally as the official emulator container (no Azure account needed).
// The queue is declared here and Aspire creates it in the emulator at startup.
var serviceBus = builder.AddAzureServiceBus(Messaging.ServiceBus).RunAsEmulator();
serviceBus.AddServiceBusQueue(Messaging.OrderPlacedQueue)
    .WithProperties(queue =>
    {
        queue.MaxDeliveryCount = Messaging.MaxDeliveryCount; // then -> dead-letter queue
        queue.LockDuration = TimeSpan.FromSeconds(30);
        queue.DeadLetteringOnMessageExpiration = true;
    });

// Service B: Shipping, a plain HTTP API. It never touches Service Bus.
var shipping = builder.AddDotnetProject("shipping", "../shipping/OutboxDemo.Shipping.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(shippingDb).WaitFor(shippingDb);

// Service A: Orders API. Saves the order and an outbox row in one transaction. It doesn't know
// Service Bus exists; it only references Shipping for the "naive" comparison endpoint.
var orders = builder.AddDotnetProject("orders", "../orders/OutboxDemo.Orders.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(ordersDb).WaitFor(ordersDb)
    .WithReference(shipping)
    .WithUrlForEndpoint("http", url => url.DisplayText = "Demo UI");

// Outbox relay (part of service A): reads the outbox table and publishes to Service Bus.
builder.AddDotnetProject("outbox-relay", "../outbox-relay/OutboxDemo.OutboxRelay.csproj")
    .WithReference(ordersDb).WaitFor(orders) // Orders creates the schema at startup
    .WithReference(serviceBus).WaitFor(serviceBus);

// Dispatcher: receives from the queue and calls the Shipping API over HTTP.
var dispatcher = builder.AddDotnetProject("dispatcher", "../dispatcher/OutboxDemo.Dispatcher.csproj")
    .WithExternalHttpEndpoints()
    .WithReference(serviceBus).WaitFor(serviceBus)
    .WithReference(shipping);

orders.WithReference(dispatcher); // only so the demo UI can show the dead-letter queue

builder.Build().Run();
