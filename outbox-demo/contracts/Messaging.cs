namespace OutboxDemo.Contracts;

// Names shared by the AppHost and both services, so they can't drift apart.
public static class Messaging
{
    // Aspire resource / connection-string names.
    public const string ServiceBus = "servicebus";
    public const string OrdersDb = "ordersdb";
    public const string ShippingDb = "shippingdb";

    // The queue Orders publishes to and Shipping consumes from.
    public const string OrderPlacedQueue = "order-placed";

    // After this many failed deliveries Service Bus moves the message to the dead-letter queue.
    public const int MaxDeliveryCount = 5;
}
