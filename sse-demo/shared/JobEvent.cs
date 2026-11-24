namespace SseDemo.Shared;

/// <summary>Progress update about a job, published by the worker on Redis pub/sub.</summary>
public record JobEvent(
    string JobId,
    string Name,
    JobStatus Status,
    int Progress,
    string Text,
    DateTimeOffset SentAt);

public enum JobStatus { Queued, Processing, Completed, Failed }
