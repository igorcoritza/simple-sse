using Microsoft.EntityFrameworkCore;

namespace OutboxDemo.Orders.Data;

public sealed class Order
{
    public Guid Id { get; init; }
    public required string Customer { get; init; }
    public required string Product { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset PlacedOnUtc { get; init; }

    // Only for the demo UI: which endpoint created it ("outbox" or "naive").
    public required string CreatedVia { get; init; }
}

// A message waiting to be published. It is written in the SAME transaction as the business
// data, so either both exist or neither does. The relay (OutboxPublisher) sends it later.
public sealed class OutboxMessage
{
    public Guid Id { get; init; }                     // also used as the Service Bus MessageId
    public required string Type { get; init; }        // e.g. "OrderPlaced"
    public required string Payload { get; init; }     // the event, serialized as JSON
    public DateTimeOffset OccurredOnUtc { get; init; }
    public string? TraceParent { get; init; }         // W3C trace context, to link the traces

    // Filled in by the relay.
    public DateTimeOffset? ProcessedOnUtc { get; set; }   // null = still pending
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }   // backoff after a failed send
    public string? LastError { get; set; }
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Order>(e =>
        {
            e.ToTable("Orders");
            e.Property(o => o.Customer).HasMaxLength(100);
            e.Property(o => o.Product).HasMaxLength(100);
            e.Property(o => o.CreatedVia).HasMaxLength(20);
        });

        model.Entity<OutboxMessage>(e =>
        {
            e.ToTable("OutboxMessages");
            e.Property(m => m.Type).HasMaxLength(200);
            e.Property(m => m.TraceParent).HasMaxLength(100);
            // The relay looks for pending messages in order: index exactly that.
            e.HasIndex(m => new { m.ProcessedOnUtc, m.OccurredOnUtc });
        });
    }
}
