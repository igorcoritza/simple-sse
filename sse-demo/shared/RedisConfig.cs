using System.Text.Json;
using System.Text.Json.Serialization;

namespace SseDemo.Shared;

public static class RedisConfig
{
    /// <summary>Redis pub/sub channel the worker publishes job events to.</summary>
    public const string JobEventsChannel = "sse-demo:job-events";

    /// <summary>Key prefix for Hangfire's own data, so it doesn't mix with anything else in Redis.</summary>
    public const string HangfirePrefix = "sse-demo:hangfire:";

    /// <summary>Same JSON settings on both sides of the channel.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
