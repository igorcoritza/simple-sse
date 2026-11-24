namespace SseDemo.Api;

public record ServerMessage(string Type, string Text, DateTimeOffset SentAt);

public record SendMessageRequest(string Text);

public record StartJobRequest(string Name, int Steps);

/// <summary>Anything pushed through the SSE stream: an event name plus its payload.</summary>
public record SseEvent(string EventType, object Data);
