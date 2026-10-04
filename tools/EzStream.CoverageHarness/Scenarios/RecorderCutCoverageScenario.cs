// 실제 두 비디오 입력과 시험용 패킷 플래그로 절단 요청·보류·키프레임 절단을 검증한다.
using System.Reflection;
using System.Runtime.InteropServices;
using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Notifications;
using EzStream.Core.Recording;
using EzStream.CoverageTool;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderCutCoverageScenario
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadFrame(AVFormatContext* context, AVPacket* packet);

    private readonly ReadFrame _nativeRead;
    private readonly SourceRecorder _recorder;
    private readonly bool _scheduled;
    private int _reads;
    private string? _firstFile;
    private string? _secondFile;

    private RecorderCutCoverageScenario(ReadFrame nativeRead, SourceRecorder recorder, bool scheduled)
        => (_nativeRead, _recorder, _scheduled) = (nativeRead, recorder, scheduled);

    public static string Run()
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
            var library = NativeLibrary.Load(Path.Combine(nativeDirectory, "avformat-58.dll"));
            try
            {
                var nativeRead = Marshal.GetDelegateForFunctionPointer<ReadFrame>(
                    NativeLibrary.GetExport(library, "av_read_frame"));
                foreach (var scheduled in new[] { false, true })
                {
                    var original = binding.GetValue(null);
                    var notices = new List<VideoSaveNotification>();
                    var recorder = new SourceRecorder(
                        new SourceConfig { Url = new Uri(input), Path = scheduled ? "scheduled" : "requested", FilePrefix = "cut" },
                        new RecorderConfig { DocumentRoot = root, SegmentMinutes = 1 },
                        NullLogger.Instance, notices.Add);
                    var scenario = new RecorderCutCoverageScenario(nativeRead, recorder, scheduled);
                    try
                    {
                        var method = typeof(RecorderCutCoverageScenario).GetMethod(nameof(ReadControlled),
                            BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingMethodException(nameof(ReadControlled));
                        binding.SetValue(null, Delegate.CreateDelegate(binding.FieldType, scenario, method));
                        SetField(recorder, "_running", true);
                        Invoke(recorder, "RunOnce");
                        Require(scenario._reads == 6 && notices.Count == 1,
                            "주 비디오 키프레임에서 정확히 한 번 절단되지 않았습니다.");
                    }
                    finally
                    {
                        binding.SetValue(null, original);
                        recorder.Stop();
                        try { Invoke(recorder, "CloseOutput", true); }
                        finally { Invoke(recorder, "CloseInput"); }
                    }
                    Require(ReferenceEquals(binding.GetValue(null), original), "읽기 바인딩 복원 실패");
                    Require(notices.Count == 2 && notices.All(n => n.Success && n.FileSizeBytes > 0),
                        "두 세그먼트의 저장 완료를 확인하지 못했습니다.");
                    Require(CountPackets(scenario._firstFile!) == 4 && CountPackets(scenario._secondFile!) == 1,
                        "절단 전후 MP4의 패킷 수가 예상과 다릅니다.");
                }
            }
            finally { NativeLibrary.Free(library); }
            return "시간 만료·주기 변경 및 비키프레임·두 번째 비디오 절단 보류 확인";
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private int ReadControlled(AVFormatContext* context, AVPacket* packet)
    {
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
}
