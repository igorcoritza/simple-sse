using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OutboxDemo.Contracts;
using OutboxDemo.Orders.Data;

// Service A: Orders.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<OrdersDbContext>(Messaging.OrdersDb);
// Note what's NOT here: no Service Bus client and no call to Shipping in the main flow. This API
// only writes to its own database. The outbox relay (another process) takes it from there.

// Only for the "naive" endpoint: a direct HTTP call to Shipping (resolved by service discovery).
// The standard resilience handler from ServiceDefaults retries transient failures, which makes
// the naive approach look better than it is... and still doesn't make it correct.
builder.Services.AddHttpClient("shipping", c => c.BaseAddress = new Uri("http://shipping"));

var app = builder.Build();

// Demo shortcut: create the schema at startup. A real service would use EF Core migrations.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();
app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot/index.html: the demo UI

var api = app.MapGroup("/api");

// ✅ The outbox way. Instead of calling Shipping, save the order AND an OrderPlaced event row.
// ONE SaveChanges = ONE database transaction: either both rows are committed or neither is.
// Nothing is sent anywhere here; the outbox relay publishes the event to Service Bus afterwards.
api.MapPost("/orders", async (PlaceOrder request, OrdersDbContext db) =>
{
    var order = NewOrder(request, createdVia: "outbox");
    var evt = new OrderPlaced(order.Id, order.Customer, order.Product, order.Quantity, order.PlacedOnUtc);

    db.Orders.Add(order);
    db.OutboxMessages.Add(new OutboxMessage
    {
        Id = Guid.CreateVersion7(),
        Type = nameof(OrderPlaced),
        Payload = JsonSerializer.Serialize(evt),
        OccurredOnUtc = order.PlacedOnUtc,
        TraceParent = Activity.Current?.Id,
    });
    await db.SaveChangesAsync();

    return TypedResults.Accepted($"/api/orders/{order.Id}", order);
});

// ❌ The naive way (the dual-write problem). Two writes to two systems with no shared
// transaction: commit the order, then call Shipping over HTTP. If Shipping is down, slow, or the
// process crashes between the two lines, the order exists and Shipping never hears about it.
// Calling Shipping BEFORE committing is no better: then a failed commit leaves a shipment for an
// order that doesn't exist.
api.MapPost("/orders/naive", async (PlaceOrder request, OrdersDbContext db, IHttpClientFactory http) =>
{
    var order = NewOrder(request, createdVia: "naive");
    db.Orders.Add(order);
    await db.SaveChangesAsync(); // write #1: committed, no way back

    try
    {
        var evt = new OrderPlaced(order.Id, order.Customer, order.Product, order.Quantity, order.PlacedOnUtc);
        var response = await http.CreateClient("shipping").PostAsJsonAsync("/api/shipments", evt); // write #2
        response.EnsureSuccessStatusCode();
        return Results.Ok(order);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Order saved, but Shipping was not notified",
            detail: $"The two systems are now inconsistent. {ex.Message}",
            statusCode: StatusCodes.Status502BadGateway);
    }
});

api.MapGet("/orders", async (OrdersDbContext db) =>
    await db.Orders.AsNoTracking().OrderByDescending(o => o.PlacedOnUtc).Take(50).ToListAsync());

api.MapGet("/outbox", async (OrdersDbContext db) =>
    await db.OutboxMessages.AsNoTracking().OrderByDescending(m => m.OccurredOnUtc).Take(50).ToListAsync());

// Demo: mark a message as pending again so the relay re-sends it. Simulates the classic
// at-least-once case (sent, then crashed before marking it processed). Shipping must ignore it.
// (In a real system this endpoint wouldn't exist.)
api.MapPost("/outbox/{id:guid}/replay", async (Guid id, OrdersDbContext db) =>
{
    var message = await db.OutboxMessages.FindAsync(id);
    if (message is null) return Results.NotFound();
    message.ProcessedOnUtc = null;
    message.NextAttemptUtc = null;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

// The UI needs to know where the other services live; Aspire injects them as services__<name>__http__0.
api.MapGet("/config", (IConfiguration config) => new
{
    shippingUrl = config["services:shipping:http:0"] ?? "http://localhost:5310",
    dispatcherUrl = config["services:dispatcher:http:0"] ?? "http://localhost:5320",
});

app.Run();

static Order NewOrder(PlaceOrder request, string createdVia) => new()
{
    Id = Guid.CreateVersion7(),
    Customer = request.Customer,
    Product = request.Product,
    Quantity = request.Quantity,
    PlacedOnUtc = DateTimeOffset.UtcNow,
    CreatedVia = createdVia,
};

public sealed record PlaceOrder(string Customer, string Product, int Quantity);
