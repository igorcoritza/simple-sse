namespace OutboxDemo.Shipping;

// A switch to simulate a bug or an unavailable dependency in Shipping. While it's on, every
// POST /api/shipments returns 503: the dispatcher abandons the message, Service Bus redelivers it
// up to MaxDeliveryCount times and then moves it to the dead-letter queue.
public sealed class Chaos
{
    public volatile bool FailProcessing;
}
