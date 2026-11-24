using Hangfire.Server;

namespace SseDemo.Shared;

/// <summary>
/// Contract shared by the API (which enqueues the job) and the worker (which runs it).
/// Hangfire stores only the interface + method + arguments in Redis, so the API never
/// needs the implementation.
/// </summary>
public interface IReportJob
{
    // PerformContext is filled in by Hangfire when the job runs; the API passes null.
    Task RunAsync(string name, int steps, PerformContext? context, CancellationToken cancellationToken);
}
