// 출력 컨텍스트의 메모리 할당 실패를 주입해 오류 알림과 녹화 루프의 자원 정리를 검증한다.
using System.Reflection;
using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Notifications;
using EzStream.Core.Recording;
using EzStream.CoverageTool;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderAllocOutputFailureScenario : ILogger
{
    private SourceRecorder? _recorder;
    private int _calls;
    private int _errorLogs;
    private string? _loggedError;

    public static bool Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezstream-alloc-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input.avi");
            MjpegAviWriter.Create(input);
            var scenario = new RecorderAllocOutputFailureScenario();
            FfmpegLoader.Initialize(Path.Combine(AppContext.BaseDirectory, "ffmpeg"), "warning", scenario);
            var binding = typeof(ffmpeg).GetField("avformat_alloc_output_context2_fptr", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("avformat_alloc_output_context2_fptr");
            var original = binding.GetValue(null);
            var notices = new List<VideoSaveNotification>();
            var recorder = new SourceRecorder(
                new SourceConfig { Url = new Uri(input), Path = "recording", FilePrefix = "alloc-output" },
                new RecorderConfig { DocumentRoot = root }, scenario, notices.Add);
            scenario._recorder = recorder;
            try
            {
                var method = typeof(RecorderAllocOutputFailureScenario).GetMethod(nameof(FailAllocation),
                    BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(FailAllocation));
                binding.SetValue(null, Delegate.CreateDelegate(binding.FieldType, scenario, method));
                Field("_running").SetValue(recorder, true);
                var run = typeof(SourceRecorder).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("Run");
                run.Invoke(recorder, null);
                var status = recorder.Snapshot();
                var error = FfmpegLoader.ErrorString(ffmpeg.AVERROR(ffmpeg.ENOMEM));
                Require(scenario._calls == 1 && status.LastError == "alloc_output: " + error,
                    "출력 컨텍스트 할당 실패 상태를 확인하지 못했습니다.");
                Require(scenario._errorLogs == 1 && scenario._loggedError?.Contains(error, StringComparison.Ordinal) == true,
                    "출력 컨텍스트 할당 실패 로그를 확인하지 못했습니다.");
                Require(notices.Count == 1 && !notices[0].Success && notices[0].FileSizeBytes == 0
                    && notices[0].Error == status.LastError && notices[0].FilePrefix == "alloc-output",
                    "출력 컨텍스트 할당 실패 알림이 예상과 다릅니다.");
                Require(status.State == "STOPPED" && status.CurrentFile is null && status.RecordedBytes == 0
                    && !Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories).Any(),
                    "할당 실패 후 출력 생성이 중단되지 않았습니다.");
                Require(Pointer.Unbox(Field("_ic").GetValue(recorder)!) == null
                    && Pointer.Unbox(Field("_oc").GetValue(recorder)!) == null,
                    "녹화 루프 종료 후 입출력 컨텍스트가 남았습니다.");
                using var unlocked = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally
            {
                binding.SetValue(null, original);
                recorder.Stop();
            }
            Require(ReferenceEquals(binding.GetValue(null), original), "출력 할당 바인딩 복원 실패");
            return true;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private int FailAllocation(AVFormatContext** context, AVOutputFormat* outputFormat, string format, string filename)
    {
        // 첫 오류 이후 재시도만 중단하며 실제 Run의 finally로 정리한다.
        Field("_running").SetValue(_recorder, false);
        Require(context != null && outputFormat == null && format == "mp4" && !string.IsNullOrEmpty(filename),
            "출력 할당 함수의 호출 인자가 예상과 다릅니다.");
        Require(_recorder!.Snapshot().State == "PREPARED"
            && Pointer.Unbox(Field("_ic").GetValue(_recorder)!) != null,
            "출력 할당 전에 실제 입력이 준비되지 않았습니다.");
        _calls++;
        // FFmpeg 4.4의 실제 실패 계약대로 반환 포인터를 null로 유지한다.
        *context = null;
        return ffmpeg.AVERROR(ffmpeg.ENOMEM);
    }

    private static FieldInfo Field(string name)
        => typeof(SourceRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (eventId.Id == 30 && logLevel == LogLevel.Error)
        {
            _errorLogs++;
            _loggedError = formatter(state, exception);
        }
    }
}
