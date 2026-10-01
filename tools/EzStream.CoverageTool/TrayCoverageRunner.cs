using System.ComponentModel;
using System.Diagnostics;

namespace EzStream.CoverageTool;

internal static class TrayCoverageRunner
{
    public static Task<TestResult> RunStatusAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("STATUS", "트레이 상태", progress, cancellationToken);

    public static Task<TestResult> RunSettingsSaveAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("SETTINGS_SAVE", "트레이 설정 저장", progress, cancellationToken);

    public static Task<TestResult> RunSettingsCancelAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("SETTINGS_CANCEL", "트레이 설정 취소", progress, cancellationToken);

    public static Task<TestResult> RunLogViewerAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("LOG", "트레이 로그 보기", progress, cancellationToken);

    public static Task<TestResult> RunLogFailureAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("LOG_FAILURE", "로그 파일 잠금·삭제", progress, cancellationToken);

    public static Task<TestResult> RunMissingLogFolderAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("LOG_MISSING_FOLDER", "로그 폴더 없음", progress, cancellationToken);

    public static Task<TestResult> RunSettingsFailureAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("SETTINGS_FAILURE", "설정 저장 실패", progress, cancellationToken);

    public static Task<TestResult> RunFolderButtonsAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("FOLDERS", "트레이 폴더 열기", progress, cancellationToken);

    public static Task<TestResult> RunFolderFailureAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("FOLDER_FAILURE", "트레이 폴더 열기 실패", progress, cancellationToken);

    public static Task<TestResult> RunDisconnectedAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("DISCONNECTED", "트레이 서비스 미연결", progress, cancellationToken);

    public static Task<TestResult> RunAllConnectedAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("ALL_CONNECTED", "트레이 연결 시험 전체", progress, cancellationToken);

    public static Task<TestResult> RunAllConnectedBranchesAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("ALL_CONNECTED_BRANCHES", "트레이 전체 성공·일부 실패", progress, cancellationToken);

    public static Task<TestResult> RunAppActionsAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("APP_ACTIONS", "트레이 본체 동작", progress, cancellationToken);

    public static async Task<TestResult> RunExitAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var covered = await RunAsync("EXIT", "트레이 정상 종료", progress, cancellationToken)
            .ConfigureAwait(false);
        if (!covered.Succeeded)
            return covered;

        var stopped = await StopActualTrayAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!stopped.Succeeded)
            return stopped;

        return await RunAsync(
            "TRAY_PROGRAM_EXIT",
            "트레이 프로그램 정상 반환",
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    public static Task<TestResult> RunProtocolAsync(IProgress<string> progress, CancellationToken cancellationToken)
        => RunAsync("PROTOCOL", "트레이 명령 오류", progress, cancellationToken);

    public static async Task<TestResult> RunSecondInstanceAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string? trayPath = null;
        foreach (var tray in Process.GetProcessesByName("EzStream.Tray"))
        {
            using (tray)
            {
                try
                {
                    trayPath = tray.MainModule?.FileName;
                }
                catch (Win32Exception)
                {
                    // 접근 가능한 실제 Tray 프로세스를 계속 찾는다.
                }
            }
            if (!string.IsNullOrWhiteSpace(trayPath))
                break;
        }
        if (string.IsNullOrWhiteSpace(trayPath) || !File.Exists(trayPath))
            return new TestResult(false, "실행 중인 실제 EzStream.Tray.exe를 찾지 못했습니다.");

        var session = await CoverageSessionManager.EnsureAsync(
            CoverageSessionManager.TraySessionId,
            "tray.coverage",
            progress,
            cancellationToken).ConfigureAwait(false);
        if (!session.Succeeded)
            return session;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet-coverage",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("connect");
        startInfo.ArgumentList.Add(CoverageSessionManager.TraySessionId);
        startInfo.ArgumentList.Add(trayPath);
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add("15000");
        progress.Report("실행 중인 Tray와 같은 실행 파일을 한 번 더 시작합니다.");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("두 번째 Tray 실행을 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
            return new TestResult(false, "두 번째 Tray 실행 실패: " + detail);
        }
        return new TestResult(true, "두 번째 Tray가 중복 실행 방지 분기에서 즉시 종료됐습니다.");
    }

    public static async Task<TestResult> RunFinishAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var disconnected = await RunDisconnectedAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!disconnected.Succeeded)
            return disconnected;
        var serviceLifecycle = await HarnessCoverageRunner.RunAsync(
            "SERVICE_LIFECYCLE",
            "서비스 정상 종료·해제",
            progress,
            cancellationToken).ConfigureAwait(false);
        if (!serviceLifecycle.Succeeded)
            return serviceLifecycle;
        var exited = await RunExitAsync(progress, cancellationToken).ConfigureAwait(false);
        if (!exited.Succeeded)
            return exited;
        return await CoverageSessionFinalizer.FinalizeAsync(progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<TestResult> RunAsync(
        string scenario,
        string description,
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(scenario, description, progress, cancellationToken);

    private static async Task<TestResult> StopActualTrayAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName("EzStream.Tray"))
        {
            using (process)
            {
                try
                {
                    progress.Report($"실제 EzStream.Tray.exe 프로세스(PID {process.Id})를 종료합니다.");
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                    stopped++;
                }
                catch (InvalidOperationException)
                {
                    // 조회 직후 이미 종료된 프로세스는 정상 종료로 취급한다.
                }
                catch (Win32Exception ex)
                {
                    return new TestResult(false, "실제 Tray 종료 실패: " + ex.Message);
                }
            }
        }

        return new TestResult(true, stopped > 0
            ? $"트레이 종료 분기 실행 후 실제 Tray 프로세스 {stopped}개를 종료했습니다."
            : "트레이 종료 분기를 실행했으며 실제 Tray는 이미 종료된 상태입니다.");
    }
}
