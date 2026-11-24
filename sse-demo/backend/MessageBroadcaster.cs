using System.Collections.Concurrent;
using System.Threading.Channels;

namespace SseDemo.Api;

/// <summary>
/// Keeps one channel per connected SSE client and fans every published event out to all of them.
/// </summary>
public sealed class MessageBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<SseEvent>> _clients = new();

    public int ClientCount => _clients.Count;

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        // Bounded so a slow client can't grow memory forever: old events are dropped.
        var channel = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _clients[id] = channel;
        return new Subscription(channel.Reader, () =>
        {
            if (_clients.TryRemove(id, out var removed))
                removed.Writer.TryComplete();
        });
    }

    public void Publish(ServerMessage message) => Publish(new SseEvent(message.Type, message));

    public void Publish(SseEvent evt)
    {
        foreach (var channel in _clients.Values)
            channel.Writer.TryWrite(evt);
    }

    public sealed class Subscription(ChannelReader<SseEvent> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<SseEvent> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
