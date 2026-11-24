using System.Text.Json;
using SseDemo.Shared;
using StackExchange.Redis;

namespace SseDemo.Api;

/// <summary>
/// Subscribes to the Redis pub/sub channel the Hangfire worker publishes to and forwards
/// each job event to every SSE client as a "job" event.
/// This is the bridge: Redis pub/sub (worker -> API) becomes SSE (API -> browser).
/// </summary>
public sealed class RedisJobEventsListener(
    IConnectionMultiplexer redis,
    MessageBroadcaster broadcaster,
    ILogger<RedisJobEventsListener> logger) : IHostedService
{
    private readonly RedisChannel _channel = RedisChannel.Literal(RedisConfig.JobEventsChannel);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await redis.GetSubscriber().SubscribeAsync(_channel, (_, value) =>
        {
            var evt = JsonSerializer.Deserialize<JobEvent>(value.ToString(), RedisConfig.Json);
            if (evt is null) return;

            logger.LogInformation("Job {JobId}: {Status} {Progress}%", evt.JobId, evt.Status, evt.Progress);
            broadcaster.Publish(new SseEvent("job", evt));
        });
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        redis.GetSubscriber().UnsubscribeAsync(_channel);
}
