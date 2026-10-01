using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace EzStream.CoverageTool;

internal static class CoverageSessionManager
{
    internal const string ServiceSessionId = "EzStreamServiceLive";
    internal const string TraySessionId = "EzStreamTrayLive";

    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, byte> KnownSessions =
        new(StringComparer.Ordinal);

    public static async Task<TestResult> ResetAllAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        await SessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var sessionId in new[] { ServiceSessionId, TraySessionId })
            {
                KnownSessions.TryRemove(sessionId, out _);
                if (!await IsActiveAsync(sessionId, cancellationToken).ConfigureAwait(false))
                    continue;

                progress.Report($"기존 {sessionId} 수집 세션을 종료합니다.");
                if (!await ClearExistingSessionsAsync(sessionId, cancellationToken).ConfigureAwait(false))
                    return new TestResult(false, $"기존 {sessionId} 수집 세션을 종료하지 못했습니다.");
            }

            return new TestResult(true, "기존 Service·Tray 수집 세션을 모두 정리했습니다.");
        }
        finally
        {
            SessionGate.Release();
        }
    }

    public static async Task<TestResult> EnsureAsync(
        string sessionId,
        string outputFileName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFileName);
        ArgumentNullException.ThrowIfNull(progress);

        await SessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (KnownSessions.ContainsKey(sessionId))
                return new TestResult(true, $"{sessionId} 수집 세션이 준비되어 있습니다.");

            if (await IsActiveAsync(sessionId, cancellationToken).ConfigureAwait(false))
            {
                progress.Report($"이전에 남아 있는 {sessionId} 수집 세션을 정리합니다.");
                var cleared = await ClearExistingSessionsAsync(
                    sessionId, cancellationToken).ConfigureAwait(false);
                if (!cleared)
                    return new TestResult(false, $"기존 {sessionId} 수집 세션을 정리하지 못했습니다.");
            }

            var settingsPath = CoverageToolPaths.SettingsPath;
            if (!File.Exists(settingsPath))
                return new TestResult(false, "Coverage.runsettings 파일을 찾지 못했습니다.");

            var outputPath = Path.Combine(CoverageSessionFinalizer.FindOutputDirectory(), outputFileName);
            if (sessionId == ServiceSessionId)
                CoverageSessionFinalizer.ClearTransientServiceCoverageFiles(progress);
            File.Delete(outputPath);
            progress.Report($"{sessionId} 수집 세션이 없어 시험 도구에서 자동으로 시작합니다.");
            var started = StartBackgroundSession(
                [
                    "collect", "--session-id", sessionId, "--server-mode", "--background",
                    "--settings", settingsPath, "--output", outputPath,
                    "--output-format", "coverage", "--timeout", "5000",
                ]);
            if (!started.Succeeded)
                return new TestResult(false, $"{sessionId} 수집 세션 자동 시작 실패: {started.Message}");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await IsActiveAsync(sessionId, cancellationToken).ConfigureAwait(false))
                {
                    KnownSessions.TryAdd(sessionId, 0);
                    return new TestResult(true, $"{sessionId} 수집 세션을 자동으로 시작했습니다.");
                }
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            return new TestResult(false, $"{sessionId} 수집 세션을 시작했지만 연결을 확인하지 못했습니다.");
        }
        finally
        {
            SessionGate.Release();
        }
    }

    public static void Forget(string sessionId)
    {
        KnownSessions.TryRemove(sessionId, out _);
    }

    private static async Task<bool> IsActiveAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var probePath = Path.Combine(Path.GetTempPath(), $"ezstream-{sessionId}-{Guid.NewGuid():N}.coverage");
        try
        {
            var result = await RunCoverageAsync(
                ["snapshot", sessionId, "--output", probePath, "--timeout", "1500"],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            return result.Succeeded;
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private static async Task<bool> ClearExistingSessionsAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await RunCoverageAsync(
                ["shutdown", sessionId, "--timeout", "3000"],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                break;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            if (!await IsActiveAsync(sessionId, cancellationToken).ConfigureAwait(false))
                return true;
        }

        return !await IsActiveAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CoverageCommandResult> RunCoverageAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CoverageToolPaths.CoverageExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("dotnet-coverage를 시작하지 못했습니다.");
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                var output = await outputTask.ConfigureAwait(false);
                var error = await errorTask.ConfigureAwait(false);
                var message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                return new CoverageCommandResult(process.ExitCode == 0, message);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                return new CoverageCommandResult(false, $"{timeout.TotalSeconds:0}초 안에 응답하지 않았습니다.");
            }
        }
        catch (Win32Exception ex)
        {
            return new CoverageCommandResult(false, "dotnet-coverage 실행 실패: " + ex.Message);
        }
    }

    private static CoverageCommandResult StartBackgroundSession(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CoverageToolPaths.CoverageExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo);
            return process is null
                ? new CoverageCommandResult(false, "dotnet-coverage를 시작하지 못했습니다.")
                : new CoverageCommandResult(true, "백그라운드 수집기 시작 요청을 전송했습니다.");
        }
        catch (Win32Exception ex)
        {
            return new CoverageCommandResult(false, "dotnet-coverage 실행 실패: " + ex.Message);
        }
    }

    private sealed record CoverageCommandResult(bool Succeeded, string Message);
}
