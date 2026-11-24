using Aspire.Hosting.Kubernetes;

// .NET Aspire AppHost.
// - `dotnet run`      -> runs everything locally and opens the Aspire dashboard.
// - `aspire publish`  -> generates a Helm chart for Kubernetes (see k8s/ and the README).
var builder = DistributedApplication.CreateBuilder(args);
var isPublish = builder.ExecutionContext.IsPublishMode;

// Target for `aspire publish`: a Kubernetes environment rendered as a Helm chart.
var k8s = builder.AddKubernetesEnvironment("k8s")
    .WithHelm(helm => helm.WithChartName("sse-demo"));

// Redis is Hangfire's storage and the pub/sub channel between the worker and the API.
// - Locally: a container (redis image) that Aspire pulls, starts and stops, plus Redis Insight.
// - Published: an external Redis (Azure Managed Redis, created by Terraform). Aspire only
//   declares the "redis" connection string; its value is supplied when the chart is installed.
// The services don't care which one they get: both read ConnectionStrings__redis.
IResourceBuilder<IResourceWithConnectionString> redis = isPublish
    ? builder.AddConnectionString("redis")
    : builder.AddRedis("redis").WithRedisInsight();

// API: SSE stream + endpoints to send messages and enqueue jobs.
var api = builder.AddDotnetProject("api", "../backend/SseDemo.Api.csproj")
    .WithExternalHttpEndpoints() // reachable from the browser (via the ingress when published)
    .WithReference(redis)
    .WaitFor(redis);

// Hangfire worker: runs the jobs and publishes their progress on Redis.
builder.AddDotnetProject("worker", "../worker/SseDemo.Worker.csproj")
    .WithReference(redis)
    .WaitFor(redis);

if (isPublish)
{
    // Production: the Angular app is built into an nginx image (frontend/Dockerfile).
    var frontend = builder.AddDockerfile("frontend", "../frontend")
        .WithHttpEndpoint(targetPort: 8080)
        .WithExternalHttpEndpoints();

    // One ingress for both: /api -> API, everything else -> Angular. Same origin, so no CORS.
    // SSE needs response buffering off and a long read timeout, or nginx would hold the events
    // back and cut the stream after 60 seconds.
    k8s.AddIngress("web")
        .WithIngressClass("webapprouting.kubernetes.azure.com") // AKS application routing (managed nginx)
        .WithIngressAnnotation("nginx.ingress.kubernetes.io/proxy-buffering", "off")
        .WithIngressAnnotation("nginx.ingress.kubernetes.io/proxy-read-timeout", "3600")
        .WithPath("/api", api.GetEndpoint("http"), IngressPathType.Prefix)
        .WithPath("/", frontend.GetEndpoint("http"), IngressPathType.Prefix);
}
else
{
    // Local: `npm install` + `npm start` (ng serve on port 4200, which the API's CORS allows).
    builder.AddJavaScriptApp("frontend", "../frontend", "start")
        .WithNpm()
        .WithHttpEndpoint(port: 4200, isProxied: false)
        .WithExternalHttpEndpoints()
        .WithReference(api)
        .WaitFor(api);
}

builder.Build().Run();
