using Hangfire;
using Hangfire.Redis.StackExchange;
using SseDemo.Shared;
using SseDemo.Worker;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry (logs, traces, metrics), health checks, resilience.
builder.AddServiceDefaults();

// One Redis connection, used both as Hangfire storage and to publish job events.
// "ConnectionStrings:redis" comes from the Aspire AppHost, or from appsettings.json without Aspire.
builder.AddRedisClient("redis");

builder.Services.AddHangfire((services, config) => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseRedisStorage(services.GetRequiredService<IConnectionMultiplexer>(),
        new RedisStorageOptions { Prefix = RedisConfig.HangfirePrefix }));

// This process is the only one running a Hangfire server, so it's the one that executes jobs.
builder.Services.AddHangfireServer(options => options.WorkerCount = 2);

// When Hangfire asks for IReportJob, DI gives it the real implementation.
builder.Services.AddTransient<IReportJob, ReportJob>();

var app = builder.Build();
app.MapDefaultEndpoints(); // /health and /alive

// Hangfire dashboard: see queued, processing and finished jobs at /hangfire.
app.MapHangfireDashboard("/hangfire");
app.MapGet("/", () => Results.Redirect("/hangfire"));

app.Run();
