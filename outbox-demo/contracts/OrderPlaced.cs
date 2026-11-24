namespace OutboxDemo.Contracts;

// Integration event: "an order was placed". Orders writes it to its outbox, the relay sends it
// to Service Bus, and Shipping reacts by creating a shipment.
public sealed record OrderPlaced(
    Guid OrderId,
    string Customer,
    string Product,
    int Quantity,
    DateTimeOffset PlacedOnUtc);
