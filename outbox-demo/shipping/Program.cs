using Microsoft.EntityFrameworkCore;
using OutboxDemo.Contracts;
using OutboxDemo.Shipping;

// Service B: Shipping. A plain HTTP API: it knows nothing about Service Bus or outboxes.
// The dispatcher calls it when an OrderPlaced message arrives.
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddSqlServerDbContext<ShippingDbContext>(Messaging.ShippingDb);
builder.Services.AddSingleton<Chaos>();

// The demo UI is served by Orders (another origin) and calls this API directly.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Demo shortcut: create the schema at startup. A real service would use EF Core migrations.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ShippingDbContext>().Database.EnsureCreatedAsync();
}

app.MapDefaultEndpoints();
app.UseCors();

var api = app.MapGroup("/api");

// Create a shipment for an order.
// - With an Idempotency-Key header (the dispatcher sends the Service Bus MessageId): idempotent.
//   The same key twice creates ONE shipment; the second call just gets 200 with the first result.
// - Without it (the naive direct call from Orders): no protection. If the caller retries after a
//   timeout, the same order can get two shipments.
api.MapPost("/shipments", async (
    OrderPlaced evt,
    [Microsoft.AspNetCore.Mvc.FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
    ShippingDbContext db,
    Chaos chaos,
    ILogger<Program> logger) =>
{
    if (chaos.FailProcessing)
        return Results.Problem("Chaos mode: Shipping is failing on purpose", statusCode: StatusCodes.Status503ServiceUnavailable);

    // 1. Seen this key before? The work is already done: answer with the original result.
    if (idempotencyKey is not null)
    {
        var seen = await db.InboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.MessageId == idempotencyKey);
        if (seen is not null)
        {
            logger.LogInformation("Duplicate {IdempotencyKey} ignored (order {OrderId})", idempotencyKey, evt.OrderId);
            return Results.Ok(new { shipmentId = seen.ShipmentId, duplicate = true });
        }
    }

    // 2. Do the work AND record the key, in one transaction (one SaveChanges).
    var shipment = new Shipment
    {
        Id = Guid.CreateVersion7(),
        OrderId = evt.OrderId,
        Product = evt.Product,
        Quantity = evt.Quantity,
        CreatedOnUtc = DateTimeOffset.UtcNow,
        CreatedVia = idempotencyKey is null ? "direct" : "servicebus",
    };
    db.Shipments.Add(shipment);
    if (idempotencyKey is not null)
    {
        db.InboxMessages.Add(new InboxMessage
        {
            MessageId = idempotencyKey,
            ShipmentId = shipment.Id,
            ProcessedOnUtc = DateTimeOffset.UtcNow,
        });
    }

    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException) when (idempotencyKey is not null)
    {
        // Two copies arrived at the same time and the other one won the race: the primary key
        // on InboxMessages rejected ours. Same outcome as a duplicate.
        var winner = await db.InboxMessages.AsNoTracking().FirstOrDefaultAsync(m => m.MessageId == idempotencyKey);
        if (winner is null) throw;
        return Results.Ok(new { shipmentId = winner.ShipmentId, duplicate = true });
    }

    logger.LogInformation("Shipment {ShipmentId} created for order {OrderId}", shipment.Id, evt.OrderId);
    return Results.Ok(new { shipmentId = shipment.Id, duplicate = false });
});

api.MapGet("/shipments", async (ShippingDbContext db) =>
    await db.Shipments.AsNoTracking().OrderByDescending(s => s.CreatedOnUtc).Take(50).ToListAsync());

api.MapGet("/inbox", async (ShippingDbContext db) =>
    await db.InboxMessages.AsNoTracking().OrderByDescending(m => m.ProcessedOnUtc).Take(50).ToListAsync());

// Chaos switch: make Shipping fail every request to see retries and dead-lettering.
api.MapGet("/chaos", (Chaos chaos) => new { chaos.FailProcessing });
api.MapPut("/chaos", (ChaosSettings settings, Chaos chaos) =>
{
    chaos.FailProcessing = settings.FailProcessing;
    return new { chaos.FailProcessing };
});

app.Run();

public sealed record ChaosSettings(bool FailProcessing);
