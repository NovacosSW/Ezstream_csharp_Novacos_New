// 실제 IO 해제 후 닫기 오류를 주입하고 저장 실패 원인의 우선순위를 검증한다.
using System.Reflection;
using System.Runtime.InteropServices;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Notifications;
using FFmpeg.AutoGen;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed unsafe class RecorderCloseFailureInjection : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CloseOutput(AVIOContext** context);

    private readonly FieldInfo _binding;
    private readonly object? _original;
    private readonly CloseOutput _nativeClose;
    private int _closes;

    public RecorderCloseFailureInjection(IntPtr library)
    {
        _binding = typeof(ffmpeg).GetField("avio_closep_fptr", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("avio_closep_fptr");
        _original = _binding.GetValue(null);
        _nativeClose = Marshal.GetDelegateForFunctionPointer<CloseOutput>(NativeLibrary.GetExport(library, "avio_closep"));
        var method = GetType().GetMethod(nameof(CloseControlled), BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(CloseControlled));
        _binding.SetValue(null, Delegate.CreateDelegate(_binding.FieldType, this, method));
    }

    private int CloseControlled(AVIOContext** context)
    {
        if (context == null || *context == null)
            throw new InvalidOperationException("닫기 오류 주입 대상 IO가 없습니다.");
        var result = _nativeClose(context);
        if (result < 0 || *context != null)
            throw new InvalidOperationException("오류 주입 전 실제 IO 해제가 실패했습니다.");
        _closes++;
        return _closes == 1 ? ffmpeg.AVERROR_INVALIDDATA : result;
    }

    public void VerifyCompleted(VideoSaveNotification notice, bool failTrailer)
    {
        var expected = failTrailer
            ? "write_trailer: " + FfmpegLoader.ErrorString(ffmpeg.AVERROR_EXTERNAL)
            : "avio_close: " + FfmpegLoader.ErrorString(ffmpeg.AVERROR_INVALIDDATA);
        if (_closes != 2 || notice.Success || notice.Error != expected)
            throw new InvalidOperationException("닫기 오류 알림 또는 선행 트레일러 오류 보존 검증 실패");
    }

    public void Dispose()
    {
        _binding.SetValue(null, _original);
        if (!ReferenceEquals(_binding.GetValue(null), _original))
            throw new InvalidOperationException("IO 닫기 바인딩 복원 실패");
    }
}
