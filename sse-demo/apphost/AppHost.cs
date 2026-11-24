// .NET Aspire AppHost: starts and wires up every piece of the demo with one command
// (`dotnet run` in this folder) and opens the Aspire dashboard with logs, traces and metrics.
var builder = DistributedApplication.CreateBuilder(args);

// Redis runs in a container (redis image) that Aspire pulls, starts and stops for us.
// It's used as Hangfire storage and as the pub/sub channel between the worker and the API.
// WithRedisInsight adds a web UI to browse keys and watch the pub/sub channel.
var redis = builder.AddRedis("redis")
    .WithRedisInsight();

// API: SSE stream + endpoints to send messages and enqueue jobs.
var api = builder.AddDotnetProject("api", "../backend/SseDemo.Api.csproj")
    .WithReference(redis)
    .WaitFor(redis);

// Hangfire worker: runs the jobs and publishes their progress on Redis.
builder.AddDotnetProject("worker", "../worker/SseDemo.Worker.csproj")
    .WithReference(redis)
    .WaitFor(redis);

// Angular app: `npm install` + `npm start` (ng serve on port 4200, which the API's CORS allows).
builder.AddJavaScriptApp("frontend", "../frontend", "start")
    .WithNpm()
    .WithHttpEndpoint(port: 4200, isProxied: false)
    .WithExternalHttpEndpoints()
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
