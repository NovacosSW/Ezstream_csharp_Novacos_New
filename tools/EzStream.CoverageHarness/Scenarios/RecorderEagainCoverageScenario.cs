// 시험 프로세스에서만 FFmpeg 읽기 바인딩을 교체해 EAGAIN과 잘못된 인덱스 이후 녹화 복구를 검증한다.
using System.Reflection;
using System.Runtime.InteropServices;

using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Recording;
using EzStream.CoverageTool;

using FFmpeg.AutoGen;

using Microsoft.Extensions.Logging.Abstractions;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderEagainCoverageScenario
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadFrame(AVFormatContext* context, AVPacket* packet);

    private readonly ReadFrame _nativeRead;
    private int _injected;
    private int _invalidIndices;
    private int _packets;

    private RecorderEagainCoverageScenario(ReadFrame nativeRead) => _nativeRead = nativeRead;

    public static string Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezstream-eagain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "input.avi");
            MjpegAviWriter.Create(input);
            var nativeDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            FfmpegLoader.Initialize(nativeDirectory, "warning", NullLogger.Instance);
            var binding = typeof(ffmpeg).GetField("av_read_frame_fptr", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(ffmpeg).FullName, "av_read_frame_fptr");
            var original = binding.GetValue(null);
            var library = NativeLibrary.Load(Path.Combine(nativeDirectory, "avformat-58.dll"));
            try
            {
                var nativeRead = Marshal.GetDelegateForFunctionPointer<ReadFrame>(
                    NativeLibrary.GetExport(library, "av_read_frame"));
                var scenario = new RecorderEagainCoverageScenario(nativeRead);
                var recorder = new SourceRecorder(
                    new SourceConfig { Url = new Uri(input), Path = "recording", FilePrefix = "eagain" },
                    new RecorderConfig { DocumentRoot = root }, NullLogger.Instance);
                string? output;
                try
                {
                    var method = typeof(RecorderEagainCoverageScenario).GetMethod(
                        nameof(ReadWithEagain), BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingMethodException(nameof(ReadWithEagain));
                    binding.SetValue(null, Delegate.CreateDelegate(binding.FieldType, scenario, method));
                    var running = typeof(SourceRecorder).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)
                        ?? throw new MissingFieldException(typeof(SourceRecorder).FullName, "_running");
                    running.SetValue(recorder, true);
                    InvokeRecorder(recorder, "RunOnce");
                    var status = recorder.Snapshot();
                    output = status.CurrentFile;
                    if (scenario._injected != 1 || scenario._invalidIndices != 2
                        || scenario._packets == 0 || status.RecordedBytes <= 0)
                        throw new InvalidOperationException("EAGAIN 및 인덱스 오류 이후 정상 패킷 기록을 확인하지 못했습니다.");
                }
                finally
                {
                    binding.SetValue(null, original);
                    recorder.Stop();
                    try { InvokeRecorder(recorder, "CloseOutput", true); }
                    finally { InvokeRecorder(recorder, "CloseInput"); }
                }

                if (!ReferenceEquals(binding.GetValue(null), original))
                    throw new InvalidOperationException("FFmpeg 읽기 바인딩 복원에 실패했습니다.");
                var savedPackets = CountSavedPackets(output
                    ?? throw new InvalidOperationException("저장된 MP4 경로가 없습니다."));
                if (savedPackets != scenario._packets)
                    throw new InvalidOperationException("잘못된 인덱스 패킷 제외 후 저장 MP4의 패킷 수가 일치하지 않습니다.");
                return $"EAGAIN 1회·인덱스 오류 2회 후 {savedPackets}개 패킷 녹화·MP4 재읽기·바인딩 복원 확인";
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private int ReadWithEagain(AVFormatContext* context, AVPacket* packet)
    {
        if (_injected == 0)
        {
            _injected++;
            return ffmpeg.AVERROR(ffmpeg.EAGAIN);
        }
        var result = _nativeRead(context, packet);
        if (result >= 0)
        {
            if (_invalidIndices < 2)
            {
                // 고정 AVI 입력의 스트림 수는 세그먼트 생성 시 매핑 길이와 같다.
                packet->stream_index = _invalidIndices == 0 ? -1 : checked((int)context->nb_streams);
                _invalidIndices++;
            }
            else
            {
                _packets++;
            }
        }
        return result;
    }

    private static int CountSavedPackets(string path)
    {
        AVFormatContext* context = null;
        var packet = ffmpeg.av_packet_alloc();
        try
        {
            if (ffmpeg.avformat_open_input(&context, path, null, null) < 0)
                throw new InvalidOperationException("저장 MP4를 다시 열지 못했습니다.");
            var count = 0;
            int result;
            while ((result = ffmpeg.av_read_frame(context, packet)) >= 0)
            {
                count++;
                ffmpeg.av_packet_unref(packet);
            }
            if (result != ffmpeg.AVERROR_EOF)
                throw new InvalidOperationException("저장 MP4 읽기가 EOF 이전에 실패했습니다.");
            return count;
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&context);
        }
    }

    private static void InvokeRecorder(SourceRecorder recorder, string name, params object[] arguments)
    {
        var method = typeof(SourceRecorder).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SourceRecorder).FullName, name);
        _ = method.Invoke(recorder, arguments);
    }
}
