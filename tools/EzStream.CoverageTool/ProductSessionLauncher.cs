using System.ComponentModel;
using System.Diagnostics;

namespace EzStream.CoverageTool;

internal static class ProductSessionLauncher
{
    private static bool _serviceConnectedByTool;
    private static bool _trayConnectedByTool;

    public static async Task<TestResult> PrepareAllAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var deployment = CoverageToolPaths.ValidateDeployment(progress);
        progress.Report(deployment.Summary);
        if (!deployment.Succeeded)
            return deployment;

        progress.Report("기존 Service·Tray 프로그램과 커버리지 수집 세션을 정리합니다.");
        var reset = await CoverageSessionManager.ResetAllAsync(progress, cancellationToken)
            .ConfigureAwait(false);
        progress.Report(reset.Summary);
        if (!reset.Succeeded)
            return reset;

        var serviceStopped = await StopExistingProductAsync(
            "EzStream.Service", "Service", progress, cancellationToken).ConfigureAwait(false);
        if (!serviceStopped.Succeeded)
            return serviceStopped;

        var trayStopped = await StopExistingProductAsync(
            "EzStream.Tray", "Tray", progress, cancellationToken).ConfigureAwait(false);
        if (!trayStopped.Succeeded)
            return trayStopped;

        _serviceConnectedByTool = false;
        _trayConnectedByTool = false;
        progress.Report("Service 커버리지 수집 세션과 실제 프로그램을 준비합니다.");
        var service = await EnsureServiceAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(service.Summary);
        if (!service.Succeeded)
            return service;

        progress.Report("Tray 커버리지 수집 세션과 실제 프로그램을 준비합니다.");
        var tray = await EnsureTrayAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(tray.Summary);
        if (!tray.Succeeded)
            return tray;

        return new TestResult(true,
            "Service·Tray 수집 세션 생성과 실제 프로그램 연결 실행을 완료했습니다.");
    }

    public static async Task<TestResult> EnsureServiceAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var servicePath = FindServicePath();
        if (servicePath is null)
            return new TestResult(false, "EzStream.Service.exe Debug 실행 파일을 찾지 못했습니다.");

        var session = await CoverageSessionManager.EnsureAsync(
            CoverageSessionManager.ServiceSessionId,
            "service.coverage",
            progress,
            cancellationToken).ConfigureAwait(false);
        progress.Report(session.Summary);
        if (!session.Succeeded)
            return session;

        var serviceIsReachable = await CanReachServiceAsync(cancellationToken).ConfigureAwait(false);
        if (_serviceConnectedByTool && serviceIsReachable)
            return new TestResult(true, "실제 Service IPC 연결을 확인했습니다.");

        if (serviceIsReachable)
        {
            var stopped = await StopExistingProductAsync(
                "EzStream.Service", "Service", progress, cancellationToken)
                .ConfigureAwait(false);
            if (!stopped.Succeeded)
                return stopped;
        }

        progress.Report("실제 Service가 없어 EzStreamServiceLive 세션에 자동으로 연결해 실행합니다.");
        var connected = StartConnectedProcess(
            CoverageSessionManager.ServiceSessionId,
            servicePath,
            "Service",
            "--console");
        if (!connected.Succeeded)
            return connected;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await CanReachServiceAsync(cancellationToken).ConfigureAwait(false))
            {
                _serviceConnectedByTool = true;
                return new TestResult(true, "실제 Service를 자동 실행하고 IPC 연결을 확인했습니다.");
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return new TestResult(false, "Service를 실행했지만 15초 안에 IPC 연결을 확인하지 못했습니다.");
    }

    public static async Task<TestResult> StopServiceForFinalCoverageAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var result = await StopExistingProductAsync(
            "EzStream.Service", "Service", progress, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
            _serviceConnectedByTool = false;
        return result;
    }

    public static async Task<TestResult> RunServiceWithoutConsoleForCoverageAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var servicePath = FindServicePath();
        if (servicePath is null)
            return new TestResult(false, "EzStream.Service.exe Debug 실행 파일을 찾지 못했습니다.");

        progress.Report("Service의 --console 미지정 시작 분기를 실행합니다.");
        var connected = StartConnectedProcess(
            CoverageSessionManager.ServiceSessionId,
            servicePath,
            "Service 비콘솔 모드");
        if (!connected.Succeeded)
            return connected;

        var reachable = false;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await CanReachServiceAsync(cancellationToken).ConfigureAwait(false))
            {
                reachable = true;
                break;
            }
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var stopped = await StopExistingProductAsync(
            "EzStream.Service", "Service 비콘솔 모드", progress, cancellationToken)
            .ConfigureAwait(false);
        _serviceConnectedByTool = false;
        if (!reachable)
            return new TestResult(false, "Service 비콘솔 모드가 15초 안에 IPC를 시작하지 못했습니다.");
        return stopped.Succeeded
            ? new TestResult(true, "Service 비콘솔 모드 시작·IPC 연결·종료를 확인했습니다.")
            : stopped;
    }

    private static async Task<TestResult> EnsureTrayAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var trayPath = CoverageToolPaths.FindProductExecutable("EzStream.Tray", "EzStream.Tray.exe");
        if (trayPath is null)
            return new TestResult(false, "EzStream.Tray.exe Debug 실행 파일을 찾지 못했습니다.");

        var session = await CoverageSessionManager.EnsureAsync(
            CoverageSessionManager.TraySessionId,
            "tray.coverage",
            progress,
            cancellationToken).ConfigureAwait(false);
        progress.Report(session.Summary);
        if (!session.Succeeded)
            return session;

        var trayIsRunning = IsProcessRunning("EzStream.Tray");
        if (_trayConnectedByTool && trayIsRunning)
            return new TestResult(true, "실제 Tray 실행 상태를 확인했습니다.");

        if (trayIsRunning)
        {
            var stopped = await StopExistingProductAsync(
                "EzStream.Tray", "Tray", progress, cancellationToken).ConfigureAwait(false);
            if (!stopped.Succeeded)
                return stopped;
        }

        progress.Report("실제 Tray를 EzStreamTrayLive 세션에 연결해 실행합니다.");
        var connected = StartConnectedProcess(
            CoverageSessionManager.TraySessionId,
            trayPath,
            "Tray");
        if (!connected.Succeeded)
            return connected;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessRunning("EzStream.Tray"))
            {
                _trayConnectedByTool = true;
                return new TestResult(true, "실제 Tray를 자동 실행하고 커버리지 연결을 확인했습니다.");
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return new TestResult(false, "Tray를 실행했지만 15초 안에 실행 상태를 확인하지 못했습니다.");
    }

    private static async Task<TestResult> StopExistingProductAsync(
        string processName,
        string displayName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    progress.Report($"기존 {displayName} PID {process.Id}를 종료하고 커버리지 세션 안에서 다시 실행합니다.");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // 조회 직후 이미 종료된 서비스는 정리된 상태다.
                }
                catch (Win32Exception ex)
                {
                    return new TestResult(false, $"기존 {displayName} 종료 실패: {ex.Message}");
                }
            }
        }

        return new TestResult(true, $"기존 {displayName} 프로세스를 정리했습니다.");
    }

    private static TestResult StartConnectedProcess(
        string sessionId,
        string executablePath,
        string displayName,
        params string[] applicationArguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CoverageToolPaths.CoverageExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "connect", sessionId, executablePath })
        {
            startInfo.ArgumentList.Add(argument);
        }
        foreach (var argument in applicationArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--background");
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add("15000");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"{displayName} 연결 실행을 시작하지 못했습니다.");
            return new TestResult(true, $"{displayName} 커버리지 연결 명령을 실행했습니다.");
        }
        catch (Win32Exception ex)
        {
            return new TestResult(false, "dotnet-coverage 실행 실패: " + ex.Message);
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Any(process => !process.HasExited);
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static async Task<bool> CanReachServiceAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var response = await IpcProbeClient.GetStatusAsync(timeout.Token).ConfigureAwait(false);
            return response["ok"]?.GetValue<bool>() == true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException
            or AccessViolationException or OperationCanceledException))
        {
            return false;
        }
    }

    private static string? FindServicePath()
        => CoverageToolPaths.FindProductExecutable("EzStream.Service", "EzStream.Service.exe");
}
