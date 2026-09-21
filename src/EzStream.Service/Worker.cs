using EzStream.Core;
using EzStream.Core.Config;
using EzStream.Core.Recording;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EzStream.Service;

/// <summary>
/// 서비스 본체. 설정을 로드해 RecorderEngine을 기동하고 PipeServer로 제어를 받는다.
/// </summary>
internal sealed class Worker ( ILogger<Worker> logger , ILoggerFactory loggerFactory ) : BackgroundService
{
    private readonly ILogger<Worker> _logger = logger;
    private readonly ILogger _engineLogger = loggerFactory.CreateLogger("Engine");
    private readonly ILogger _ipcLogger = loggerFactory.CreateLogger("Ipc");
    private RecorderEngine? _engine;
    private PipeServer? _pipe;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ServiceLog.ServiceStarting(_logger, AppPaths.ConfigPath);

        var config = RecorderConfig.LoadOrCreate(AppPaths.ConfigPath);
        Directory.CreateDirectory(config.DocumentRoot);

        _engine = new RecorderEngine(config, _engineLogger);
        try
        {
            _engine.Start();
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            ServiceLog.EngineStartFailed(_logger, ex);
        }

        _pipe = new PipeServer(_engine, AppPaths.ConfigPath, _ipcLogger);
        _pipe.Start();

        ServiceLog.ServiceStarted(_logger);
        return Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        ServiceLog.ServiceStopping(_logger);
        _pipe?.Stop();
        _engine?.Stop();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public override void Dispose()
    {
        _pipe?.Dispose();
        _engine?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
