using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

namespace EzStream.Core.Ipc;

/// <summary>
/// 서비스 ↔ 트레이 사이 Named Pipe 프로토콜.
/// 요청/응답 각각 한 줄의 JSON(개행 구분)으로 주고받는다.
/// </summary>
public static class IpcCommands
{
    public const string GetStatus = "GET_STATUS";
    public const string SetInterval = "SET_INTERVAL";   // { minutes }
    public const string SetConfig = "SET_CONFIG";       // { config }
    public const string GetConfig = "GET_CONFIG";
    public const string RunRetention = "RUN_RETENTION";
}

public sealed class IpcRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    /// <summary>SET_INTERVAL 용.</summary>
    [JsonPropertyName("minutes")]
    public int? Minutes { get; set; }

    /// <summary>SET_CONFIG 용. 전체 설정 JSON 문자열.</summary>
    [JsonPropertyName("configJson")]
    public string? ConfigJson { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, IpcJson.Options);
    public static IpcRequest? Parse(string line)
        => string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<IpcRequest>(line, IpcJson.Options);
}

public sealed class IpcResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>GET_STATUS 응답 페이로드.</summary>
    [JsonPropertyName("status")]
    public EngineStatus? Status { get; set; }

    /// <summary>GET_CONFIG 응답 페이로드(설정 JSON 문자열).</summary>
    [JsonPropertyName("configJson")]
    public string? ConfigJson { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, IpcJson.Options);
    public static IpcResponse? Parse(string line)
        => string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options);
}

/// <summary>엔진 전체 상태 스냅샷.</summary>
public sealed class EngineStatus
{
    [JsonPropertyName("documentRoot")]
    public string DocumentRoot { get; set; } = "";

    [JsonPropertyName("segmentMinutes")]
    public int SegmentMinutes { get; set; }

    [JsonPropertyName("sources")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<SourceStatus> Sources { get; } = [];
}

public sealed class SourceStatus
{
    [JsonPropertyName("url")]
    public Uri? Url { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>INIT / PREPARED / PLAYING / STOPPED.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "INIT";

    [JsonPropertyName("currentFile")]
    public string? CurrentFile { get; set; }

    [JsonPropertyName("segmentStartedAt")]
    public DateTimeOffset? SegmentStartedAt { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("recordedBytes")]
    public long RecordedBytes { get; set; }
}

internal static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}
