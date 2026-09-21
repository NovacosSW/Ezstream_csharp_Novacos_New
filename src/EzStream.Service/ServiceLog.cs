using Microsoft.Extensions.Logging;

namespace EzStream.Service;

internal static partial class ServiceLog
{
    [LoggerMessage(101, LogLevel.Warning, "Pipe accept loop error.")]
    public static partial void PipeAcceptError(ILogger logger, Exception exception);

    [LoggerMessage(102, LogLevel.Warning, "IPC handling error.")]
    public static partial void IpcError(ILogger logger, Exception exception);

    [LoggerMessage(103, LogLevel.Warning, "Cannot persist config.")]
    public static partial void CannotPersistConfig(ILogger logger, Exception exception);

    [LoggerMessage(110, LogLevel.Information, "EzStream service starting. Config: {Path}")]
    public static partial void ServiceStarting(ILogger logger, string path);

    [LoggerMessage(111, LogLevel.Critical, "Failed to start recorder engine (FFmpeg load?). Service will keep running for IPC.")]
    public static partial void EngineStartFailed(ILogger logger, Exception exception);

    [LoggerMessage(112, LogLevel.Information, "EzStream service started.")]
    public static partial void ServiceStarted(ILogger logger);

    [LoggerMessage(113, LogLevel.Information, "EzStream service stopping.")]
    public static partial void ServiceStopping(ILogger logger);
}
