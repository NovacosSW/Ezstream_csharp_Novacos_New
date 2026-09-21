using System.Text.Json.Serialization;

namespace EzStream.Core.Notifications;

/// <summary>MP4 세그먼트 저장 완료 시 UDP로 전송되는 JSON 패킷.</summary>
public sealed class VideoSaveNotification
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = "videoSaveResult";

    [JsonPropertyName("sentAtUtc")]
    public DateTimeOffset SentAtUtc { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("durationMilliseconds")]
    public long DurationMilliseconds { get; init; }

    [JsonPropertyName("filePrefix")]
    public string FilePrefix { get; init; } = string.Empty;

    [JsonPropertyName("fileSizeBytes")]
    public long FileSizeBytes { get; init; }

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
