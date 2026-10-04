// 실제 서비스 진입점의 Host 시작과 정상 종료 반환을 검증하는 시나리오.
using System.Diagnostics;

using EzStream.Service;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EzStream.CoverageHarness.Scenarios;

internal sealed class ServiceHostCoverageScenario :
    IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private IDisposable? _hostSubscription;
    private IHostApplicationLifetime? _lifetime;
    private CancellationTokenRegistration _startedRegistration;
    private CancellationTokenRegistration _stoppedRegistration;
    private bool _started;
    private bool _stopped;

    public static string Run()
    {
        var entry = typeof(Worker).Assembly.EntryPoint
            ?? throw new InvalidOperationException("Service 진입점을 찾지 못했습니다.");
        using var observer = new ServiceHostCoverageScenario();
        using var subscription = DiagnosticListener.AllListeners.Subscribe(observer);
        var execution = Task.Run(() => { _ = entry.Invoke(null, [Array.Empty<string>()]); });
        try
        {
            execution.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            observer._lifetime?.StopApplication();
            throw;
        }

        if (!observer._started || !observer._stopped)
            throw new InvalidOperationException("Service Host 시작 및 정상 종료 이벤트를 확인하지 못했습니다.");
        return "실제 Service Program의 Host 시작·정상 중지·Run 반환 완료";
    }

    public void OnNext(DiagnosticListener value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Name == "Microsoft.Extensions.Hosting")
        {
            _hostSubscription?.Dispose();
            _hostSubscription = value.Subscribe(this);
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key != "HostBuilt" || value.Value is not IHost host)
            return;

        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        _lifetime = lifetime;
        _stoppedRegistration = lifetime.ApplicationStopped.Register(() => _stopped = true);
        _startedRegistration = lifetime.ApplicationStarted.Register(() =>
        {
            _started = true;
            lifetime.StopApplication();
        });
    }

    public void OnCompleted() { }

    public void OnError(Exception error)
        => throw new InvalidOperationException("Host 진단 이벤트 수신에 실패했습니다.", error);

    public void Dispose()
    {
        _startedRegistration.Dispose();
        _stoppedRegistration.Dispose();
        _hostSubscription?.Dispose();
    }
}
