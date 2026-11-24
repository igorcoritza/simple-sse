namespace SseDemo.Api;

/// <summary>
/// Publishes a "tick" message every 5 seconds so you can see the server pushing data
/// without the client asking for it.
/// </summary>
public sealed class HeartbeatService(MessageBroadcaster broadcaster) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var count = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            count++;
            broadcaster.Publish(new ServerMessage(
                "tick",
                $"Tick #{count} from the server ({broadcaster.ClientCount} client(s) connected)",
                DateTimeOffset.UtcNow));
        }
    }
}
