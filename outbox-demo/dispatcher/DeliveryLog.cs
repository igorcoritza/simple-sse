using System.Collections.Concurrent;

namespace OutboxDemo.Dispatcher;

// What the dispatcher did with each delivery, newest first. In memory and only for the demo
// (the UI shows it and the integration test asserts on it).
public sealed record Delivery(
    string MessageId,
    int DeliveryCount,
    string Outcome,          // see DeliveryOutcome
    string? Detail,
    DateTimeOffset AtUtc);

public static class DeliveryOutcome
{
    public const string Created = "created";                   // Shipping did the work now -> Complete
    public const string AlreadyProcessed = "already-processed"; // Shipping had done it before -> Complete
    public const string SimulatedCrash = "simulated-crash";     // work done, message NOT completed
    public const string Retry = "retry";                       // Shipping failed -> Abandon
    public const string DeadLettered = "dead-lettered";        // rejected / unreadable -> DeadLetter
}

public sealed class DeliveryLog
{
    private readonly ConcurrentQueue<Delivery> _entries = new();

    public void Add(string messageId, int deliveryCount, string outcome, string? detail = null)
    {
        _entries.Enqueue(new Delivery(messageId, deliveryCount, outcome, detail, DateTimeOffset.UtcNow));
        while (_entries.Count > 200 && _entries.TryDequeue(out _)) { }
    }

    public IEnumerable<Delivery> Recent(int count = 50) => _entries.Reverse().Take(count);
}

// Chaos switch for the dispatcher: simulate a crash AFTER Shipping has done the work but BEFORE
// the message is completed. Applied to the first delivery of each message only.
public sealed class DispatcherChaos
{
    public volatile bool CrashAfterDelivery;
}
