using Microsoft.Extensions.Logging;

namespace EzStream.Core;

internal static partial class EngineLog
{
    [LoggerMessage(10, LogLevel.Information, "RecorderEngine started with {Count} source(s).")]
    public static partial void Started(ILogger logger, int count);

    [LoggerMessage(11, LogLevel.Information, "RecorderEngine stopped.")]
    public static partial void Stopped(ILogger logger);

    [LoggerMessage(12, LogLevel.Information, "Config structural change detected. Rebuilding recorders.")]
    public static partial void ConfigRebuild(ILogger logger);

    [LoggerMessage(13, LogLevel.Information, "Segment interval changed to {Minutes} min (live).")]
    public static partial void IntervalChanged(ILogger logger, int minutes);
}
