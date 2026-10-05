using System.Runtime.InteropServices;

using FFmpeg .AutoGen;

using Microsoft.Extensions.Logging;

namespace EzStream.Core.Ffmpeg;

/// <summary>
/// FFmpeg.AutoGen 초기화. 네이티브 DLL 경로(ffmpeg.RootPath)를 지정하고
/// av_log 콜백을 로거로 연결한다. 원본 EzStreamExport.cpp의 av_log_set_callback 대응.
/// </summary>
public static class FfmpegLoader
{
    private static readonly Lock Gate = new();
    private static bool _initialized;
    private static av_log_set_callback_callback? _logCallback; // GC 방지용 보관

    public static unsafe void Initialize(string nativeDir, string logLevel, ILogger logger)
    {
        lock (Gate)
        {
            if (_initialized) return;

            if (!string.IsNullOrEmpty(nativeDir) && Directory.Exists(nativeDir))
            {
                ffmpeg.RootPath = nativeDir;
            }

            // 버전 조회로 로드 검증(실패 시 여기서 DllNotFoundException 발생).
            uint version = ffmpeg.avformat_version();
            FfmpegLog.Loaded(logger, version, ffmpeg.RootPath);

            ffmpeg.av_log_set_level(ToAvLogLevel(logLevel));

            _logCallback = (p0, level, format, vl) =>
            {
                if (level > ffmpeg.av_log_get_level()) return;
                const int lineSize = 1024;
                var lineBuffer = stackalloc byte[lineSize];
                int printPrefix = 1;
                ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
                var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer)!.TrimEnd();
                if (string.IsNullOrEmpty(line)) return;
                var mappedLevel = MapLevel(level);
                if (logger.IsEnabled(mappedLevel))
                    FfmpegLog.Line(logger, mappedLevel, line);
            };
            ffmpeg.av_log_set_callback(_logCallback);

            _initialized = true;
        }
    }

    public static int ToAvLogLevel(string level) => level?.Trim().ToUpperInvariant() switch
    {
        "QUIET" => ffmpeg.AV_LOG_QUIET,
        "PANIC" => ffmpeg.AV_LOG_PANIC,
        "FATAL" => ffmpeg.AV_LOG_FATAL,
        "ERROR" => ffmpeg.AV_LOG_ERROR,
        "WARNING" => ffmpeg.AV_LOG_WARNING,
        "INFO" => ffmpeg.AV_LOG_INFO,
        "VERBOSE" => ffmpeg.AV_LOG_VERBOSE,
        "DEBUG" => ffmpeg.AV_LOG_DEBUG,
        "TRACE" => ffmpeg.AV_LOG_TRACE,
        _ => ffmpeg.AV_LOG_WARNING,
    };

    private static LogLevel MapLevel(int avLevel)
    {
        if (avLevel <= ffmpeg.AV_LOG_ERROR) return LogLevel.Error;
        if (avLevel <= ffmpeg.AV_LOG_WARNING) return LogLevel.Warning;
        if (avLevel <= ffmpeg.AV_LOG_INFO) return LogLevel.Information;
        return LogLevel.Debug;
    }

    /// <summary>ffmpeg 정수 에러코드를 사람이 읽는 문자열로. 원본 getFfmpegError 대응.</summary>
    public static unsafe string ErrorString(int errnum)
    {
        const int bufSize = 1024;
        var buffer = stackalloc byte[bufSize];
        ffmpeg.av_strerror(errnum, buffer, bufSize);
        // stackalloc 버퍼 자체는 null일 수 없고 av_strerror가 항상 C 문자열을 기록한다.
        return Marshal.PtrToStringAnsi((IntPtr)buffer)!;
    }
}
