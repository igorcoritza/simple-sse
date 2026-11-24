using Microsoft.EntityFrameworkCore;

namespace OutboxDemo.Shipping;

public sealed class Shipment
{
    public Guid Id { get; init; }
    public Guid OrderId { get; init; }
    public required string Product { get; init; }
    public int Quantity { get; init; }
    public DateTimeOffset CreatedOnUtc { get; init; }
    public required string CreatedVia { get; init; } // "servicebus" (via dispatcher) or "direct" (naive endpoint)
}

// Inbox (a.k.a. idempotent receiver / dedup table): one row per message already handled, keyed by
// the Service Bus MessageId (sent by the dispatcher as the Idempotency-Key header). It's written in
// the SAME transaction as the shipment, so "did the work" and "remember that I did it" can never
// disagree. The primary key makes a second insert of the same id fail.
public sealed class InboxMessage
{
    public required string MessageId { get; init; }
    public Guid ShipmentId { get; init; }
    public DateTimeOffset ProcessedOnUtc { get; init; }
}

public sealed class ShippingDbContext(DbContextOptions<ShippingDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Shipment>(e =>
        {
            e.ToTable("Shipments");
            e.Property(s => s.Product).HasMaxLength(100);
            e.Property(s => s.CreatedVia).HasMaxLength(20);
            e.HasIndex(s => s.OrderId);
        });

        model.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.HasKey(m => m.MessageId);
            e.Property(m => m.MessageId).HasMaxLength(100);
        });
    }
}
