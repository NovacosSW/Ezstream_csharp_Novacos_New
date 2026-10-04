// 시험 프로세스의 패킷 쓰기 두 번을 실패시켜 오류 보존과 바이트 집계를 검증한다.
using System.Reflection;
using System.Runtime.InteropServices;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Recording;
using FFmpeg.AutoGen;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderWriteFailureInjection : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteFrame(AVFormatContext* context, AVPacket* packet);

    private readonly SourceRecorder _recorder;
    private readonly FieldInfo _binding;
    private readonly object? _original;
    private readonly WriteFrame _nativeWrite;
    private int _writes;
    private long _expectedBytes;
    private string? _file;

    public RecorderWriteFailureInjection(SourceRecorder recorder, IntPtr library)
    {
        _recorder = recorder;
        _binding = typeof(ffmpeg).GetField("av_interleaved_write_frame_fptr", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("av_interleaved_write_frame_fptr");
        _original = _binding.GetValue(null);
        _nativeWrite = Marshal.GetDelegateForFunctionPointer<WriteFrame>(
            NativeLibrary.GetExport(library, "av_interleaved_write_frame"));
        var method = GetType().GetMethod(nameof(WriteControlled), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(WriteControlled));
        _binding.SetValue(null, Delegate.CreateDelegate(_binding.FieldType, this, method));
    }

    public static string FirstError => "write_frame: " + FfmpegLoader.ErrorString(ffmpeg.AVERROR_EXTERNAL);

    public void VerifyBytes()
    {
        if (_recorder.Snapshot().RecordedBytes != _expectedBytes)
            throw new InvalidOperationException("실패 패킷이 기록 바이트에 포함되었거나 정상 패킷 집계가 누락됐습니다.");
    }

    public void VerifyCompleted()
    {
        VerifyBytes();
        if (_writes != 5)
            throw new InvalidOperationException("쓰기 오류 이후 후속 세그먼트 기록을 확인하지 못했습니다.");
    }

    private int WriteControlled(AVFormatContext* context, AVPacket* packet)
    {
        var file = _recorder.Snapshot().CurrentFile;
        if (_file != file)
        {
            _file = file;
            _expectedBytes = 0;
        }
        VerifyBytes();
        _writes++;
        if (_writes is 2 or 3)
        {
            var error = _writes == 2 ? ffmpeg.AVERROR_EXTERNAL : ffmpeg.AVERROR_INVALIDDATA;
            ffmpeg.av_packet_unref(packet);
            return error;
        }
        var size = packet->size;
        var result = _nativeWrite(context, packet);
        if (result < 0) throw new InvalidOperationException("쓰기 오류 주입 외 정상 패킷 기록 실패");
        _expectedBytes += size;
        return result;
    }

    public void Dispose()
    {
        _binding.SetValue(null, _original);
        if (!ReferenceEquals(_binding.GetValue(null), _original))
            throw new InvalidOperationException("패킷 쓰기 바인딩 복원 실패");
    }
}
