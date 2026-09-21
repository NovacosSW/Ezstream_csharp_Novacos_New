using EzStream.Core;
using EzStream.Service;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// --console 로 실행하면 콘솔 앱처럼 동작(개발/디버그). 그 외에는 Windows 서비스로 동작.
bool consoleMode = args.Contains("--console", StringComparer.OrdinalIgnoreCase);

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "EzStreamRecorder");
builder.Services.AddHostedService(serviceProvider => new Worker(
    serviceProvider.GetRequiredService<ILogger<Worker>>(),
    serviceProvider.GetRequiredService<ILoggerFactory>()));

builder.Logging.ClearProviders();
using var fileLoggerProvider = new FileLoggerProvider(AppPaths.LogDir);
builder.Logging.AddProvider(fileLoggerProvider);
if (consoleMode)
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss.fff "; });
builder.Logging.SetMinimumLevel(LogLevel.Information);

var host = builder.Build();
host.Run();
