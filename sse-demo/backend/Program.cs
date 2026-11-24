using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Hangfire;
using Hangfire.Redis.StackExchange;
using SseDemo.Api;
using SseDemo.Shared;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry (logs, traces, metrics), health checks, resilience.
builder.AddServiceDefaults();

// Locally the Angular dev server runs on another origin, so the browser needs CORS
// permission to open the EventSource and to POST. In Kubernetes both sit behind the same
// ingress, so no extra origins are needed there.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins)
          .AllowAnyHeader()
          .AllowAnyMethod()));

// Same JSON settings as the Redis channel, so enums go out as "processing", not 1.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(RedisConfig.Json.Converters[0]));

// One Redis connection (IConnectionMultiplexer) for Hangfire storage (enqueue) and pub/sub (listen).
// Aspire's client integration reads "ConnectionStrings:redis" (injected by the AppHost, or from
// appsettings.json when running without Aspire) and adds health checks and tracing.
builder.AddRedisClient("redis");

// Hangfire *client* only: the API enqueues jobs but does not run them (no AddHangfireServer).
builder.Services.AddHangfire((services, config) => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseRedisStorage(services.GetRequiredService<IConnectionMultiplexer>(),
        new RedisStorageOptions { Prefix = RedisConfig.HangfirePrefix }));

builder.Services.AddSingleton<MessageBroadcaster>();
builder.Services.AddHostedService<HeartbeatService>();
builder.Services.AddHostedService<RedisJobEventsListener>();

var app = builder.Build();
app.UseCors();
app.MapDefaultEndpoints(); // /health and /alive

// GET /api/events -> the SSE stream. The connection stays open and every event
// published through the broadcaster is written to it.
app.MapGet("/api/events", (MessageBroadcaster broadcaster, CancellationToken ct) =>
    TypedResults.ServerSentEvents(Stream(broadcaster, ct)));

// POST /api/messages -> publishes a custom message to every connected client, directly.
app.MapPost("/api/messages", (SendMessageRequest request, MessageBroadcaster broadcaster) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest("Text is required.");

    var message = new ServerMessage("custom", request.Text.Trim(), DateTimeOffset.UtcNow);
    broadcaster.Publish(message);
    return Results.Accepted(value: message);
});

// POST /api/jobs -> enqueues a Hangfire job. The response returns right away;
// progress arrives later through SSE (worker -> Redis pub/sub -> this API -> browser).
app.MapPost("/api/jobs", (StartJobRequest request, IBackgroundJobClient jobs, MessageBroadcaster broadcaster) =>
{
    var name = string.IsNullOrWhiteSpace(request.Name) ? "Report" : request.Name.Trim();
    var steps = Math.Clamp(request.Steps, 1, 20);

    var jobId = jobs.Enqueue<IReportJob>(job => job.RunAsync(name, steps, null, CancellationToken.None));

    var queued = new JobEvent(jobId, name, JobStatus.Queued, 0, "Job queued in Hangfire", DateTimeOffset.UtcNow);
    broadcaster.Publish(new SseEvent("job", queued));
    return Results.Accepted(value: queued);
});

app.MapGet("/", () => "SSE demo API. Stream: GET /api/events · POST /api/messages · POST /api/jobs");

app.Run();

// Turns the client's channel into SSE items. The event type ("tick", "custom", "job"...)
// lets the browser listen to each kind with addEventListener.
static async IAsyncEnumerable<SseItem<object>> Stream(
    MessageBroadcaster broadcaster,
    [EnumeratorCancellation] CancellationToken ct)
{
    using var subscription = broadcaster.Subscribe();

    yield return new SseItem<object>(
        new ServerMessage("welcome", "Connected to the SSE stream", DateTimeOffset.UtcNow), "welcome");

    await foreach (var evt in subscription.Reader.ReadAllAsync(ct))
    {
        yield return new SseItem<object>(evt.Data, evt.EventType)
        {
            // Tells the browser how long to wait before reconnecting if the connection drops.
            ReconnectionInterval = TimeSpan.FromSeconds(3)
        };
    }
}
