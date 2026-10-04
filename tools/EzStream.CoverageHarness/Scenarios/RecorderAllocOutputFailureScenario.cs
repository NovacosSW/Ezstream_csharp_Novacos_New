// 출력 컨텍스트·스트림 생성·코덱 복사 실패를 주입해 오류 알림과 자원 정리를 검증한다.
using System.Reflection;
using System.Runtime.InteropServices;
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
    internal enum Failure { Allocation, NewStream, ParametersCopy }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate AVStream* NewStream(AVFormatContext* context, AVCodec* codec);

    private NewStream? _nativeNewStream;
    private SourceRecorder? _recorder;
    private int _calls;
    private int _errorLogs;
    private string? _loggedError;

    public static bool Run(Failure failure = Failure.Allocation)
    {
        var failNewStream = failure == Failure.NewStream;
        var root = Path.Combine(Path.GetTempPath(), "ezstream-alloc-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        nint library = 0;
        try
        {
            var input = Path.Combine(root, "input.avi");
            if (failNewStream) MjpegAviWriter.CreateWithTwoVideos(input);
            else MjpegAviWriter.Create(input);
            var scenario = new RecorderAllocOutputFailureScenario();
            FfmpegLoader.Initialize(Path.Combine(AppContext.BaseDirectory, "ffmpeg"), "warning", scenario);
            if (failNewStream)
            {
                library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "avformat-58.dll"));
                scenario._nativeNewStream = Marshal.GetDelegateForFunctionPointer<NewStream>(
                    NativeLibrary.GetExport(library, "avformat_new_stream"));
            }
            var bindingName = failure switch
            {
                Failure.NewStream => "avformat_new_stream_fptr",
                Failure.ParametersCopy => "avcodec_parameters_copy_fptr",
                _ => "avformat_alloc_output_context2_fptr",
            };
            var binding = typeof(ffmpeg).GetField(bindingName, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(bindingName);
            var original = binding.GetValue(null);
            var notices = new List<VideoSaveNotification>();
            var recorder = new SourceRecorder(
                new SourceConfig { Url = new Uri(input), Path = "recording", FilePrefix = "alloc-output" },
                new RecorderConfig { DocumentRoot = root }, scenario, notices.Add);
            scenario._recorder = recorder;
            try
            {
                var methodName = failure switch
                {
                    Failure.NewStream => nameof(FailSecondStream),
                    Failure.ParametersCopy => nameof(FailParametersCopy),
                    _ => nameof(FailAllocation),
                };
                var method = typeof(RecorderAllocOutputFailureScenario).GetMethod(methodName,
                    BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(methodName);
                binding.SetValue(null, Delegate.CreateDelegate(binding.FieldType, scenario, method));
                Field("_running").SetValue(recorder, true);
                var run = typeof(SourceRecorder).GetMethod("Run", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("Run");
                run.Invoke(recorder, null);
                var status = recorder.Snapshot();
                var error = FfmpegLoader.ErrorString(ffmpeg.AVERROR(ffmpeg.ENOMEM));
                var expectedError = failure switch
                {
                    Failure.NewStream => "new_stream failed",
                    Failure.ParametersCopy => "parameters_copy: " + error,
                    _ => "alloc_output: " + error,
                };
                Require(scenario._calls == (failNewStream ? 2 : 1)
                    && status.LastError == expectedError,
                    "출력 준비 실패 상태를 확인하지 못했습니다.");
                if (failure == Failure.Allocation)
                    Require(scenario._errorLogs == 1 && scenario._loggedError?.Contains(error, StringComparison.Ordinal) == true,
                        "출력 컨텍스트 할당 실패 로그를 확인하지 못했습니다.");
                Require(notices.Count == 1 && !notices[0].Success && notices[0].FileSizeBytes == 0
                    && notices[0].Error == status.LastError && notices[0].FilePrefix == "alloc-output",
                    "출력 준비 실패 알림이 예상과 다릅니다.");
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
        finally
        {
            if (library != 0) NativeLibrary.Free(library);
            Directory.Delete(root, recursive: true);
        }
    }

    private int FailParametersCopy(AVCodecParameters* destination, AVCodecParameters* source)
    {
        Field("_running").SetValue(_recorder, false);
        var output = (AVFormatContext*)Pointer.Unbox(Field("_oc").GetValue(_recorder)!);
        var input = (AVFormatContext*)Pointer.Unbox(Field("_ic").GetValue(_recorder)!);
        Require(output != null && output->nb_streams == 1 && input != null && input->nb_streams > 0
            && destination != null && destination == output->streams[0]->codecpar
            && source != null && source == input->streams[0]->codecpar
            && source->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO,
            "실제 출력 스트림 생성 이후의 코덱 복사 호출이 아닙니다.");
        Require(destination->extradata == null, "새 출력 스트림에 예상하지 않은 추가 데이터가 있습니다.");
        _calls++;
        // FFmpeg 4.4의 실패 직후 상태를 모사하며 원본 extradata 소유권은 복사하지 않는다.
        *destination = *source;
        destination->extradata = null;
        destination->extradata_size = 0;
        return ffmpeg.AVERROR(ffmpeg.ENOMEM);
    }

    private AVStream* FailSecondStream(AVFormatContext* context, AVCodec* codec)
    {
        // 입력/출력 준비는 계속 실행하되 실패 후 자동 재시도만 중단한다.
        Field("_running").SetValue(_recorder, false);
        Require(context != null && context->oformat != null
            && context == (AVFormatContext*)Pointer.Unbox(Field("_oc").GetValue(_recorder)!),
            "실제 출력 컨텍스트가 준비되지 않았습니다.");
        _calls++;
        if (_calls == 1)
        {
            Require(context->nb_streams == 0, "첫 출력 스트림 생성 전 상태가 예상과 다릅니다.");
            var stream = _nativeNewStream!(context, codec);
            Require(stream != null && context->nb_streams == 1, "첫 출력 스트림 실제 생성 실패");
            return stream;
        }
        Require(_calls == 2 && context->nb_streams == 1
            && context->streams[0]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO,
            "첫 비디오 스트림 설정 후 두 번째 생성에 도달하지 못했습니다.");
        return null;
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
