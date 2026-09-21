using Microsoft.Extensions.Logging;

namespace EzStream.Core;

internal static partial class RetentionLog
{
    [LoggerMessage(40, LogLevel.Error, "Retention cleanup failed.")]
    public static partial void Failed(ILogger logger, Exception exception);

    [LoggerMessage(41, LogLevel.Information, "Retention: deleted {Count} file(s) older than {Days} day(s).")]
    public static partial void Deleted(ILogger logger, int count, int days);

    [LoggerMessage(42, LogLevel.Information, "Retention: deleted {Count} log file(s) older than {Days} day(s).")]
    public static partial void LogsDeleted(ILogger logger, int count, int days);

    [LoggerMessage(43, LogLevel.Warning, "Cannot delete log {File}")]
    public static partial void CannotDeleteLog(ILogger logger, string file, Exception exception);

    [LoggerMessage(44, LogLevel.Warning, "Cannot delete {File}")]
    public static partial void CannotDeleteFile(ILogger logger, string file, Exception exception);
}
