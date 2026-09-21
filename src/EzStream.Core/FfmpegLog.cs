using Microsoft.Extensions.Logging;

namespace EzStream.Core;

internal static partial class FfmpegLog
{
    [LoggerMessage(1, LogLevel.Information, "FFmpeg loaded. avformat version {Version}, root={Root}")]
    public static partial void Loaded(ILogger logger, uint version, string? root);

    [LoggerMessage(EventId = 2, Message = "[ffmpeg] {Line}")]
    public static partial void Line(ILogger logger, LogLevel level, string line);
}
