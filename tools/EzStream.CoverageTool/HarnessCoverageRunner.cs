using System.Diagnostics;
namespace EzStream.CoverageTool;

internal static class HarnessCoverageRunner
{
    private const string HarnessFileName = "EzStream.CoverageHarness.exe";
    public static async Task<TestResult> RunAsync(
        string scenario,
        string description,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentNullException.ThrowIfNull(progress);
        var harnessPath = CoverageToolPaths.FindHarnessExecutable(HarnessFileName);
        if (harnessPath is null)
            return new TestResult(false, "외부 시험 실행기 EzStream.CoverageHarness.exe를 찾지 못했습니다.");

        var isTrayScenario = IsTrayScenario(scenario);
        var sessionId = isTrayScenario
            ? CoverageSessionManager.TraySessionId
            : CoverageSessionManager.ServiceSessionId;
        var session = await CoverageSessionManager.EnsureAsync(
            sessionId,
            isTrayScenario ? "tray.coverage" : "service.coverage",
            progress,
            cancellationToken).ConfigureAwait(false);
        if (!session.Succeeded)
            return session;

        var startInfo = new ProcessStartInfo
        {
            FileName = CoverageToolPaths.CoverageExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("connect");
        startInfo.ArgumentList.Add(sessionId);
        startInfo.ArgumentList.Add(harnessPath);
        startInfo.ArgumentList.Add(scenario);
        startInfo.ArgumentList.Add("--timeout");
        startInfo.ArgumentList.Add("15000");

        progress.Report($"{description} 시험을 {sessionId} 동적검사 세션에서 실행합니다.");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("외부 시험 실행기를 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var resultLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("OK|", StringComparison.Ordinal)
                || line.StartsWith("FAIL|", StringComparison.Ordinal));
        var succeeded = process.ExitCode == 0
            && resultLine?.StartsWith("OK|", StringComparison.Ordinal) == true;
        var detail = resultLine switch
        {
            { } line when line.StartsWith("OK|", StringComparison.Ordinal) => line[3..],
            { } line when line.StartsWith("FAIL|", StringComparison.Ordinal) => line[5..],
            null => BuildConnectionFailure(sessionId, output, error),
            _ => description,
        };
        return new TestResult(succeeded, succeeded ? $"{detail} 세션: {sessionId}" : detail);
    }

    public static async Task<TestResult> RunStandaloneAsync(
        string scenario,
        string description,
        string outputFileName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFileName);
        ArgumentNullException.ThrowIfNull(progress);
        var harnessPath = CoverageToolPaths.FindHarnessExecutable(HarnessFileName);
        if (harnessPath is null)
            return new TestResult(false, "외부 시험 실행기 EzStream.CoverageHarness.exe를 찾지 못했습니다.");

        var settingsPath = CoverageToolPaths.SettingsPath;
        if (!File.Exists(settingsPath))
            return new TestResult(false, "Coverage.runsettings 파일을 찾지 못했습니다.");

        var outputPath = Path.Combine(CoverageSessionFinalizer.FindOutputDirectory(), outputFileName);
        File.Delete(outputPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = CoverageToolPaths.CoverageExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            "collect", "--settings", settingsPath, "--output", outputPath,
            "--output-format", "coverage", harnessPath, scenario,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        progress.Report($"{description} 독립 커버리지를 저장합니다: {outputPath}");
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("독립 커버리지 수집을 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var resultLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => line.StartsWith("OK|", StringComparison.Ordinal)
                || line.StartsWith("FAIL|", StringComparison.Ordinal));
        var succeeded = process.ExitCode == 0
            && resultLine?.StartsWith("OK|", StringComparison.Ordinal) == true
            && File.Exists(outputPath)
            && new FileInfo(outputPath).Length > 10;
        if (succeeded)
            return new TestResult(true, $"{description} 독립 결과 저장 완료: {outputPath}");

        var detail = resultLine is null
            ? (string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim())
            : resultLine;
        return new TestResult(false, $"{description} 독립 결과 저장 실패: {detail}");
    }

    private static string BuildConnectionFailure(string sessionId, string output, string error)
    {
        var diagnostic = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
        CoverageSessionManager.Forget(sessionId);
        return $"{sessionId} 동적검사 세션 연결에 실패했습니다. 자동 세션을 다시 시작한 뒤 재시험하십시오. {diagnostic}";
    }

    private static bool IsTrayScenario(string scenario)
        => scenario is "STATUS" or "SETTINGS_SAVE" or "SETTINGS_CANCEL" or "LOG"
            or "LOG_FAILURE" or "LOG_MISSING_FOLDER" or "FOLDERS" or "FOLDER_FAILURE"
            or "SETTINGS_FAILURE" or "DISCONNECTED" or "ALL_CONNECTED"
            or "ALL_CONNECTED_BRANCHES" or "APP_ACTIONS" or "EXIT" or "PROTOCOL"
            or "TRAY_PROGRAM_EXIT";

}
