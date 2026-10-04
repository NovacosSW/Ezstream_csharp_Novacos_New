// 실제 두 비디오 입력과 시험용 바인딩으로 절단 조건 및 출력 열기·종료 실패를 검증한다.
using System.Reflection;
using System.Runtime.InteropServices;
using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Notifications;
using EzStream.Core.Recording;
using EzStream.CoverageTool;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderCutCoverageScenario
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadFrame(AVFormatContext* context, AVPacket* packet);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int OpenOutput(AVIOContext** context, [MarshalAs(UnmanagedType.LPUTF8Str)] string url, int flags);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteTrailer(AVFormatContext* context);

    private readonly ReadFrame _nativeRead;
    private readonly SourceRecorder _recorder;
    private readonly bool _scheduled;
    private readonly OpenOutput _nativeOpen;
    private readonly WriteTrailer _nativeTrailer;
    private InvalidOperationException? _trailerException;
    private RecorderWriteFailureInjection? _writeFailure;
    private RecorderCloseFailureInjection? _closeFailure;
    private int _trailers;
    private int _opens;
    private int _reads;
    private string? _firstFile;
    private string? _secondFile;

    private RecorderCutCoverageScenario(ReadFrame nativeRead, OpenOutput nativeOpen, WriteTrailer nativeTrailer, SourceRecorder recorder, bool scheduled)
        => (_nativeRead, _nativeOpen, _nativeTrailer, _recorder, _scheduled) = (nativeRead, nativeOpen, nativeTrailer, recorder, scheduled);

    public static string Run(bool failWrite = false, bool failClose = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "ezstream-cut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input.avi");
            MjpegAviWriter.CreateWithTwoVideos(input);
            var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            FfmpegLoader.Initialize(nativeDirectory, "warning", NullLogger.Instance);
            var binding = typeof(ffmpeg).GetField("av_read_frame_fptr", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("av_read_frame_fptr");
            var openBinding = typeof(ffmpeg).GetField("avio_open_fptr", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("avio_open_fptr");
            var trailerBinding = typeof(ffmpeg).GetField("av_write_trailer_fptr", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("av_write_trailer_fptr");
            var library = NativeLibrary.Load(Path.Combine(nativeDirectory, "avformat-58.dll"));
            try
            {
                var nativeRead = Marshal.GetDelegateForFunctionPointer<ReadFrame>(
                    NativeLibrary.GetExport(library, "av_read_frame"));
                var nativeOpen = Marshal.GetDelegateForFunctionPointer<OpenOutput>(
                    NativeLibrary.GetExport(library, "avio_open"));
                var nativeTrailer = Marshal.GetDelegateForFunctionPointer<WriteTrailer>(
                    NativeLibrary.GetExport(library, "av_write_trailer"));
                foreach (var mode in new[]
                {
                    (Scheduled: false, FailOpen: false, FailTrailer: false, ThrowTrailer: false, Path: "requested"),
                    (Scheduled: true, FailOpen: false, FailTrailer: false, ThrowTrailer: false, Path: "scheduled"),
                    (Scheduled: false, FailOpen: true, FailTrailer: false, ThrowTrailer: false, Path: "open-failure"),
                    (Scheduled: false, FailOpen: false, FailTrailer: true, ThrowTrailer: false, Path: "trailer-failure"),
                    (Scheduled: false, FailOpen: false, FailTrailer: true, ThrowTrailer: true, Path: "trailer-exception"),
                }.Take(failWrite ? 1 : 5).Where(mode => !failClose || mode.Path is "requested" or "trailer-failure"))
                {
                    var original = binding.GetValue(null);
                    var originalOpen = openBinding.GetValue(null);
                    var originalTrailer = trailerBinding.GetValue(null);
                    var notices = new List<VideoSaveNotification>();
                    var logger = new TrailerLogger();
                    var recorder = new SourceRecorder(
                        new SourceConfig { Url = new Uri(input), Path = mode.Path, FilePrefix = "cut" },
                        new RecorderConfig { DocumentRoot = root, SegmentMinutes = 1 },
                        logger, notices.Add);
                    var scenario = new RecorderCutCoverageScenario(nativeRead, nativeOpen, nativeTrailer, recorder, mode.Scheduled);
                    using var writeFailure = failWrite ? new RecorderWriteFailureInjection(recorder, library) : null;
                    scenario._writeFailure = writeFailure;
                    using var closeFailure = failClose ? new RecorderCloseFailureInjection(library) : null;
                    scenario._closeFailure = closeFailure;
                    if (mode.ThrowTrailer)
                        scenario._trailerException = new InvalidOperationException("coverage-trailer-managed-exception");
                    try
                    {
                        var method = typeof(RecorderCutCoverageScenario).GetMethod(nameof(ReadControlled),
                            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(ReadControlled));
                        binding.SetValue(null, Delegate.CreateDelegate(binding.FieldType, scenario, method));
                        if (mode.FailOpen)
                        {
                            var openMethod = typeof(RecorderCutCoverageScenario).GetMethod(nameof(OpenControlled),
                                BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(OpenControlled));
                            openBinding.SetValue(null, Delegate.CreateDelegate(openBinding.FieldType, scenario, openMethod));
                        }
                        if (mode.FailTrailer)
                        {
                            var trailerMethod = typeof(RecorderCutCoverageScenario).GetMethod(nameof(TrailerControlled),
                                BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(TrailerControlled));
                            trailerBinding.SetValue(null, Delegate.CreateDelegate(trailerBinding.FieldType, scenario, trailerMethod));
                        }
                        SetField(recorder, "_running", true);
                        Invoke(recorder, "RunOnce");
                        if (mode.FailOpen)
                        {
                            var status = recorder.Snapshot();
                            Require(scenario._opens == 2 && scenario._reads == 5 && notices.Count == 2,
                                "두 번째 출력 실패 후 패킷 처리가 중단되지 않았습니다.");
                            Require(status.CurrentFile is null && status.LastError?.StartsWith("avio_open:", StringComparison.Ordinal) == true,
                                "새 출력 열기 실패 상태가 기록되지 않았습니다.");
                        }
                        else
                        {
                            Require(scenario._reads == 6 && notices.Count == 1,
                                "주 비디오 키프레임에서 정확히 한 번 절단되지 않았습니다.");
                        }
                    }
                    finally
                    {
                        binding.SetValue(null, original);
                        if (mode.FailOpen) openBinding.SetValue(null, originalOpen);
                        if (mode.FailTrailer) trailerBinding.SetValue(null, originalTrailer);
                        recorder.Stop();
                        try { Invoke(recorder, "CloseOutput", true); }
                        finally { Invoke(recorder, "CloseInput"); }
                    }
                    Require(ReferenceEquals(binding.GetValue(null), original), "읽기 바인딩 복원 실패");
                    if (mode.FailTrailer)
                        Require(ReferenceEquals(trailerBinding.GetValue(null), originalTrailer), "트레일러 바인딩 복원 실패");
                    if (mode.FailOpen)
                        Require(ReferenceEquals(openBinding.GetValue(null), originalOpen), "출력 열기 바인딩 복원 실패");
                    scenario.VerifySavedSegments(notices, logger, mode.FailOpen, mode.FailTrailer);
                }
            }
            finally { NativeLibrary.Free(library); }
            return "절단 조건·출력 실패 보고와 정리 확인";
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private void VerifySavedSegments(List<VideoSaveNotification> notices, TrailerLogger logger, bool failOpen, bool failTrailer)
    {
        var failed = failTrailer || _writeFailure is not null || _closeFailure is not null;
        Require(notices.Count == 2 && notices[0].Success == !failed && notices[0].FileSizeBytes > 0
            && CountPackets(_firstFile!) == (_writeFailure is null ? 4 : 2), "이전 MP4의 저장 결과를 확인하지 못했습니다.");
        if (failTrailer) VerifyTrailerFailure(notices[0], logger);
        if (_writeFailure is not null) VerifyWriteFailure(notices[0], logger);
        _closeFailure?.VerifyCompleted(notices[0], failTrailer);
        if (failed)
        {
            foreach (var fieldName in new[] { "_ic", "_oc" })
            {
                var field = typeof(SourceRecorder).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(fieldName);
                Require(Pointer.Unbox(field.GetValue(_recorder)!) == null, "종료 후 컨텍스트가 남았습니다.");
            }
            using var unlockedFirst = new FileStream(_firstFile!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var unlockedSecond = new FileStream(_secondFile!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        if (failOpen)
        {
            Require(!notices[1].Success && notices[1].FileSizeBytes == 0
                && notices[1].Error?.StartsWith("avio_open:", StringComparison.Ordinal) == true,
                "새 출력 열기 실패 알림이 예상과 다릅니다.");
        }
        else
        {
            Require(notices[1].Success && notices[1].FileSizeBytes > 0 && CountPackets(_secondFile!) == 1,
                "새 MP4의 정상 저장을 확인하지 못했습니다.");
        }
    }

    private void VerifyWriteFailure(VideoSaveNotification notice, TrailerLogger logger)
    {
        _writeFailure!.VerifyCompleted();
        Require(notice.Error == RecorderWriteFailureInjection.FirstError,
            "첫 패킷 쓰기 오류가 저장 실패 알림에 보존되지 않았습니다.");
        Require(logger.WriteErrors.Count == 2
            && logger.WriteErrors[0].Contains(FfmpegLoader.ErrorString(ffmpeg.AVERROR_EXTERNAL), StringComparison.Ordinal)
            && logger.WriteErrors[1].Contains(FfmpegLoader.ErrorString(ffmpeg.AVERROR_INVALIDDATA), StringComparison.Ordinal),
            "두 패킷 쓰기 실패 경고를 확인하지 못했습니다.");
    }

    private void VerifyTrailerFailure(VideoSaveNotification notice, TrailerLogger logger)
    {
        var error = _trailerException?.Message ?? FfmpegLoader.ErrorString(ffmpeg.AVERROR_EXTERNAL);
        Require(_trailers == 1 && notice.Error == "write_trailer: " + error,
            "트레일러 실패 알림을 확인하지 못했습니다.");
        if (_trailerException is not null)
        {
            Require(logger.ExceptionCount == 1 && logger.Count == 0
                && ReferenceEquals(logger.Exception, _trailerException),
                "트레일러 관리 예외 로그를 확인하지 못했습니다.");
        }
        else
        {
            Require(logger.Count == 1 && logger.ExceptionCount == 0
                && logger.Message?.Contains(error, StringComparison.Ordinal) == true,
                "트레일러 오류 반환 로그를 확인하지 못했습니다.");
        }
    }

    private int TrailerControlled(AVFormatContext* context)
    {
        Require(context != null && context->pb != null && _recorder.Snapshot().RecordedBytes > 0,
            "실제 패킷 기록 후 트레일러에 도달하지 못했습니다.");
        var result = _nativeTrailer(context);
        Require(result >= 0, "오류 주입 전 실제 트레일러 종료가 실패했습니다.");
        _trailers++;
        if (_trailerException is not null) throw _trailerException;
        return ffmpeg.AVERROR_EXTERNAL;
    }

    private int OpenControlled(AVIOContext** context, string url, int flags)
    {
        _opens++;
        return _opens == 2 ? ffmpeg.AVERROR_EXTERNAL : _nativeOpen(context, url, flags);
    }

    private int ReadControlled(AVFormatContext* context, AVPacket* packet)
    {
        _writeFailure?.VerifyBytes();
        _reads++;
        var current = _recorder.Snapshot().CurrentFile;
        if (_reads == 1) _firstFile = current;
        if (_reads <= 5) Require(current == _firstFile, "절단 가능 키프레임 전에 파일이 바뀌었습니다.");
        if (_reads == 6)
        {
            _secondFile = current;
            Require(current is not null && current != _firstFile, "키프레임에서 파일이 바뀌지 않았습니다.");
            return ffmpeg.AVERROR_EOF;
        }
        var result = _nativeRead(context, packet);
        Require(result >= 0 && context->nb_streams == 2 && packet->stream_index == (_reads - 1) % 2,
            "두 비디오 시험 입력의 패킷 순서가 예상과 다릅니다.");
        if (_reads == 2)
        {
            // 첫 정상 기록 이후 자동 절단의 대기만 단축하며 _cutNow는 설정하지 않는다.
            if (_scheduled) SetField(_recorder, "_segmentMillis", 1000L);
            // 파일명이 초 단위이므로 첫 세그먼트와 겹치지 않게 하고 자동 만료도 유도한다.
            Thread.Sleep(1200);
            if (!_scheduled) _recorder.UpdateSegmentMinutes(120000);
        }
        // 2·4번째 패킷은 다른 비디오의 키프레임, 3번째는 주 비디오의 비키프레임이다.
        if (_reads == 3) packet->flags &= ~ffmpeg.AV_PKT_FLAG_KEY;
        else packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;
        return result;
    }

    private static int CountPackets(string path)
    {
        AVFormatContext* context = null;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            Require(ffmpeg.avformat_open_input(&context, path, null, null) >= 0, "저장 MP4 열기 실패");
            var count = 0;
            int result;
            while ((result = ffmpeg.av_read_frame(context, packet)) >= 0)
            {
                count++;
                ffmpeg.av_packet_unref(packet);
            }
            Require(result == ffmpeg.AVERROR_EOF, "저장 MP4 읽기 실패");
            return count;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&context);
        }
    }

    private static void SetField(SourceRecorder recorder, string name, object value)
        => (typeof(SourceRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(recorder, value);

    private static void Invoke(SourceRecorder recorder, string name, params object[] arguments)
        => (typeof(SourceRecorder).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(name)).Invoke(recorder, arguments);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TrailerLogger : ILogger
    {
        public int Count { get; private set; }
        public string? Message { get; private set; }
        public int ExceptionCount { get; private set; }
        public Exception? Exception { get; private set; }
        public List<string> WriteErrors { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 37 && logLevel == LogLevel.Warning)
            {
                Count++;
                Message = formatter(state, exception);
            }
            if (eventId.Id == 35 && logLevel == LogLevel.Warning)
            {
                ExceptionCount++;
                Exception = exception;
            }
            if (eventId.Id == 36 && logLevel == LogLevel.Warning)
                WriteErrors.Add(formatter(state, exception));
        }
    }
}
