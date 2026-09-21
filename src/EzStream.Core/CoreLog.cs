using Microsoft.Extensions.Logging;

namespace EzStream.Core;

internal static partial class CoreLog
{
    [LoggerMessage(20, LogLevel.Information, "[{Path}] Segment interval changed to {Milliseconds} ms (cut now).")]
    public static partial void RecorderIntervalChanged(ILogger logger, string path, long milliseconds);

    [LoggerMessage(21, LogLevel.Information, "[{Path}] Recorder thread started. url={Url}")]
    public static partial void RecorderStarted(ILogger logger, string path, Uri? url);

    [LoggerMessage(22, LogLevel.Error, "[{Path}] Recorder loop error.")]
    public static partial void RecorderLoopError(ILogger logger, string path, Exception exception);

    [LoggerMessage(23, LogLevel.Information, "[{Path}] Recorder thread stopped.")]
    public static partial void RecorderStopped(ILogger logger, string path);

    [LoggerMessage(24, LogLevel.Warning, "[{Path}] av_read_frame ended: {Error}. Reconnecting.")]
    public static partial void ReadEnded(ILogger logger, string path, string error);

    [LoggerMessage(25, LogLevel.Information, "[{Path}] Cutting segment (elapsed={Elapsed}ms cutNow={CutNow}).")]
    public static partial void CuttingSegment(ILogger logger, string path, long elapsed, bool cutNow);

    [LoggerMessage(26, LogLevel.Information, "[{Path}] Opening input: {Url}")]
    public static partial void OpeningInput(ILogger logger, string path, string url);

    [LoggerMessage(27, LogLevel.Error, "[{Path}] Cannot open input: {Error}")]
    public static partial void CannotOpenInput(ILogger logger, string path, string error);

    [LoggerMessage(28, LogLevel.Error, "[{Path}] Cannot find stream info: {Error}")]
    public static partial void CannotFindStreamInfo(ILogger logger, string path, string error);

    [LoggerMessage(29, LogLevel.Warning, "[{Path}] No video stream found; will segment on any packet.")]
    public static partial void NoVideoStream(ILogger logger, string path);

    [LoggerMessage(30, LogLevel.Error, "[{Path}] Cannot allocate output context: {Error}")]
    public static partial void CannotAllocateOutput(ILogger logger, string path, string error);

    [LoggerMessage(31, LogLevel.Error, "[{Path}] No recordable streams.")]
    public static partial void NoRecordableStreams(ILogger logger, string path);

    [LoggerMessage(32, LogLevel.Error, "[{Path}] Cannot open output file {File}: {Error}")]
    public static partial void CannotOpenOutput(ILogger logger, string path, string file, string error);

    [LoggerMessage(33, LogLevel.Error, "[{Path}] Cannot write header: {Error}")]
    public static partial void CannotWriteHeader(ILogger logger, string path, string error);

    [LoggerMessage(34, LogLevel.Information, "[{Path}] Recording to {File}")]
    public static partial void RecordingTo(ILogger logger, string path, string file);

    [LoggerMessage(35, LogLevel.Warning, "[{Path}] write_trailer failed.")]
    public static partial void WriteTrailerFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(36, LogLevel.Warning, "[{Path}] write_frame error: {Error}")]
    public static partial void WriteFrameFailed(ILogger logger, string path, string error);

    [LoggerMessage(37, LogLevel.Warning, "[{Path}] write_trailer error: {Error}")]
    public static partial void WriteTrailerReturnedError(ILogger logger, string path, string error);

}
