using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

namespace EzStream.Core.Config;

/// <summary>
/// 로컬 JSON 설정 모델. 원본 configuration.json의 세그먼트 녹화 관련 항목을 축약 이식한 것.
/// </summary>
public sealed class RecorderConfig
{
    /// <summary>파일이 저장될 베이스 경로. 원본 document_root.</summary>
    [JsonPropertyName("documentRoot")]
    public string DocumentRoot { get; set; } = @"C:\ezstream\data";

    /// <summary>세그먼트 저장 주기(분). 실시간으로 변경 가능. 원본 destination.duration(초)에 대응.</summary>
    [JsonPropertyName("segmentMinutes")]
    public int SegmentMinutes { get; set; } = 10;

    /// <summary>보존 기간(일). 이 일수보다 오래된 파일은 삭제. 0 이하이면 비활성. 원본 disuse_term.</summary>
    [JsonPropertyName("disuseTermDays")]
    public int DisuseTermDays { get; set; } = 60;

    /// <summary>로그 보존 기간(일). 이 일수보다 오래된 로그 파일은 삭제. 0 이하이면 비활성.</summary>
    [JsonPropertyName("logRetentionDays")]
    public int LogRetentionDays { get; set; } = 30;

    /// <summary>ffmpeg av_log 레벨(quiet, fatal, error, warning, info, verbose, debug, trace).</summary>
    [JsonPropertyName("ffmpegLogLevel")]
    public string FfmpegLogLevel { get; set; } = "warning";

    /// <summary>영상 저장 결과를 받을 UDP IPv4/IPv6 주소. 비어 있으면 알림 비활성.</summary>
    [JsonPropertyName("udpNotificationIp")]
    public string UdpNotificationIp { get; set; } = "127.0.0.1";

    /// <summary>영상 저장 결과를 받을 UDP 포트. 0이면 알림 비활성.</summary>
    [JsonPropertyName("udpNotificationPort")]
    public int UdpNotificationPort { get; set; }

    /// <summary>영상 소스 목록.</summary>
    [JsonPropertyName("sources")]
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
    public Collection<SourceConfig> Sources { get; } = [];

    public static string DefaultDocumentRoot() => @"C:\ezstream\data";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static RecorderConfig FromJson(string json)
        => JsonSerializer.Deserialize<RecorderConfig>(json, JsonOptions) ?? new RecorderConfig();

    /// <summary>설정 파일을 읽는다. 없으면 기본값을 만들어 저장 후 반환.</summary>
    public static RecorderConfig LoadOrCreate(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(text))
                    return FromJson(text);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // 파싱 실패 시 기본값으로 진행(파일은 덮어쓰지 않음).
            return new RecorderConfig();
        }

        var cfg = new RecorderConfig();
        try
        {
            cfg.Save(path);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // 저장 실패는 무시(권한 등). 메모리 상 기본값으로 동작.
        }
        return cfg;
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // 원자적 저장: 임시 파일에 쓰고 교체.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    public RecorderConfig Clone() => FromJson(ToJson());

    /// <summary>세그먼트 주기를 밀리초로. 최소 5초 하한.</summary>
    public long SegmentMillis => Math.Max(5, SegmentMinutes * 60) * 1000L;
}

public sealed class SourceConfig
{
    /// <summary>영상을 받아오는 원본 위치(rtsp://... 등). 원본 source.url.</summary>
    [JsonPropertyName("url")]
    public Uri? Url { get; set; }

    /// <summary>documentRoot 이후의 저장 하위 디렉토리(예: b070). 원본 source.path.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>파일 접두사. [prefix_시간.mp4] 형태로 저장. 원본 filePrefix.</summary>
    [JsonPropertyName("filePrefix")]
    public string FilePrefix { get; set; } = "video";

    public string SafePath => string.IsNullOrWhiteSpace(Path) ? "default" : Path.Trim('/', '\\');
    public string SafePrefix => string.IsNullOrWhiteSpace(FilePrefix) ? "video" : FilePrefix;
}
