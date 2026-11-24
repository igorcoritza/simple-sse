using System.Text.Json;
using Hangfire.Server;
using SseDemo.Shared;
using StackExchange.Redis;

namespace SseDemo.Worker;

/// <summary>
/// Fake long-running job: it "processes" a report step by step and publishes its progress
/// to Redis pub/sub. The worker knows nothing about SSE or browsers; it just publishes.
/// </summary>
public sealed class ReportJob(IConnectionMultiplexer redis, ILogger<ReportJob> logger) : IReportJob
{
    public async Task RunAsync(string name, int steps, PerformContext? context, CancellationToken cancellationToken)
    {
        var jobId = context?.BackgroundJob.Id ?? "unknown";
        logger.LogInformation("Running job {JobId} ({Name}) with {Steps} steps", jobId, name, steps);

        await PublishAsync(jobId, name, JobStatus.Processing, 0, "Worker picked up the job");

        for (var step = 1; step <= steps; step++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken); // pretend to work
            var progress = step * 100 / steps;
            await PublishAsync(jobId, name, JobStatus.Processing, progress, $"Step {step}/{steps} done");
        }

        await PublishAsync(jobId, name, JobStatus.Completed, 100, "Report ready");
    }

    private Task PublishAsync(string jobId, string name, JobStatus status, int progress, string text)
    {
        var evt = new JobEvent(jobId, name, status, progress, text, DateTimeOffset.UtcNow);
        var json = JsonSerializer.Serialize(evt, RedisConfig.Json);
        return redis.GetSubscriber().PublishAsync(RedisChannel.Literal(RedisConfig.JobEventsChannel), json);
    }
}
