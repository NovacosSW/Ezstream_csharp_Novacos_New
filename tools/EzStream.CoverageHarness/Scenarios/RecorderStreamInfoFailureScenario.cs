// 실제 입력을 연 뒤 스트림 분석 실패를 주입해 오류 보고와 녹화 루프의 자원 정리를 검증한다.
using System.Reflection;
using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Recording;
using EzStream.CoverageTool;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderStreamInfoFailureScenario : ILogger
{
    private SourceRecorder? _recorder;
    private int _calls;
    private int _errorLogs;
    private string? _loggedError;

    public static bool Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezstream-stream-info-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input.avi");
            MjpegAviWriter.Create(input);
            var scenario = new RecorderStreamInfoFailureScenario();
            FfmpegLoader.Initialize(Path.Combine(AppContext.BaseDirectory, "ffmpeg"), "warning", scenario);
            var binding = typeof(ffmpeg).GetField("avformat_find_stream_info_fptr", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("avformat_find_stream_info_fptr");
            var original = binding.GetValue(null);
            var notifications = 0;
            var recorder = new SourceRecorder(
                new SourceConfig { Url = new Uri(input), Path = "recording", FilePrefix = "stream-info" },
                new RecorderConfig { DocumentRoot = root }, scenario, _ => notifications++);
            scenario._recorder = recorder;
            try
            {
                var method = typeof(RecorderStreamInfoFailureScenario).GetMethod(nameof(FailAnalysis),
                    BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(FailAnalysis));
                binding.SetValue(null, Delegate.CreateDelegate(binding.FieldType, scenario, method));
                Field("_running").SetValue(recorder, true);
                // 실제 Run의 finally에서 입력 컨텍스트가 정리되는지 확인한다.
                var run = typeof(SourceRecorder).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("Run");
                run.Invoke(recorder, null);
                var status = recorder.Snapshot();
                var error = FfmpegLoader.ErrorString(ffmpeg.AVERROR_INVALIDDATA);
                Require(scenario._calls == 1 && status.LastError == "find_stream_info: " + error,
                    "실제 스트림 분석 실패 상태를 확인하지 못했습니다.");
                Require(scenario._errorLogs == 1 && scenario._loggedError?.Contains(error, StringComparison.Ordinal) == true,
                    "스트림 분석 실패 로그를 확인하지 못했습니다.");
                Require(status.State == "STOPPED" && status.CurrentFile is null && status.RecordedBytes == 0
                    && notifications == 0 && !Directory.Exists(Path.Combine(root, "recording")),
                    "분석 실패 후 출력 생성이 중단되지 않았습니다.");
                Require(Pointer.Unbox(Field("_ic").GetValue(recorder)!) == null,
                    "녹화 루프 종료 후 입력 컨텍스트가 남았습니다.");
                // Windows 파일 공유 잠금도 해제되었는지 검사한다.
                using var unlocked = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            finally
            {
                binding.SetValue(null, original);
                recorder.Stop();
            }
            Require(ReferenceEquals(binding.GetValue(null), original), "분석 바인딩 복원 실패");
            return true;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private int FailAnalysis(AVFormatContext* context, AVDictionary** options)
    {
        // 첫 실패의 Run finally를 검사하고 자동 재시도만 중단한다.
        Field("_running").SetValue(_recorder, false);
        Require(context != null && context->pb != null && context->nb_streams > 0,
            "분석 실패 주입 전에 실제 입력이 열리지 않았습니다.");
        _calls++;
        return ffmpeg.AVERROR_INVALIDDATA;
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
        if (eventId.Id == 28 && logLevel == LogLevel.Error)
        {
            _errorLogs++;
            _loggedError = formatter(state, exception);
        }
    }
}
