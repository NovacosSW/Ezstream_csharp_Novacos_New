using System.Diagnostics;

namespace EzStream.CoverageTool;

internal static class CoverageSessionFinalizer
{
    private const string ServiceSessionId = "EzStreamServiceLive";
    private const string TraySessionId = "EzStreamTrayLive";

    public static async Task<TestResult> FinalizeAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var outputDirectory = FindOutputDirectory();
        var servicePath = Path.Combine(outputDirectory, "service.coverage");
        var trayPath = Path.Combine(outputDirectory, "tray.coverage");

        var traySaved = await SnapshotAsync(TraySessionId, trayPath, progress, cancellationToken)
            .ConfigureAwait(false);
        var serviceSaved = await SnapshotAsync(ServiceSessionId, servicePath, progress, cancellationToken)
            .ConfigureAwait(false);

        StopProductProcesses(progress);
        await ShutdownAllAsync(TraySessionId, progress, cancellationToken).ConfigureAwait(false);
        await ShutdownAllAsync(ServiceSessionId, progress, cancellationToken).ConfigureAwait(false);

        if (serviceSaved)
            serviceSaved = await MergeServiceCoverageAsync(servicePath, progress, cancellationToken)
                .ConfigureAwait(false);

        if (!traySaved)
            return new TestResult(false, "Tray 결과 저장에 실패했습니다. Tray Live 세션이 실행 중이었는지 확인하십시오.");

        var serviceText = serviceSaved ? $", Service 통합 결과: {servicePath}" : string.Empty;
        return new TestResult(true,
            $"실제 프로그램과 Live 세션을 모두 종료했습니다. Tray 결과: {trayPath}{serviceText}");
    }

    public static Task<TestResult> FinalizeServiceAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => FinalizeProductAsync(
            ServiceSessionId,
            "EzStream.Service",
            "service_TC05_TC20_live.coverage",
            "Service",
            progress,
            cancellationToken);

    public static async Task<TestResult> SnapshotServiceCheckpointAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var outputPath = Path.Combine(
            FindOutputDirectory(), "service_TC05_TC20_checkpoint.coverage");
        var saved = await SnapshotAsync(
            ServiceSessionId, outputPath, progress, cancellationToken).ConfigureAwait(false);
        return saved
            ? new TestResult(true, $"Service 종료 전 체크포인트 저장 완료: {outputPath}")
            : new TestResult(false, "Service 종료 전 체크포인트 저장에 실패했습니다.");
    }

    public static Task<TestResult> FinalizeTrayAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => FinalizeProductAsync(
            TraySessionId,
            "EzStream.Tray",
            "tray_TC05_TC22_live.coverage",
            "Tray",
            progress,
            cancellationToken);

    internal static void ClearTransientServiceCoverageFiles(IProgress<string> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var outputDirectory = FindOutputDirectory();
        string[] fileNames =
        [
            "service_TC12_unsupported.coverage",
            "service_TC19_lifecycle.coverage",
            "service_TC20_lifecycle.coverage",
            "service_TC05_TC20_checkpoint.coverage",
            "service_TC05_TC20_live.coverage",
        ];

        var deleted = 0;
        foreach (var fileName in fileNames)
        {
            var path = Path.Combine(outputDirectory, fileName);
            if (!File.Exists(path))
                continue;

            File.Delete(path);
            deleted++;
        }

        if (deleted > 0)
            progress.Report($"이전 시험 회차의 Service 중간 결과 {deleted}개를 정리했습니다.");
    }

    private static async Task<TestResult> FinalizeProductAsync(
        string sessionId,
        string processName,
        string fileName,
        string displayName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var outputPath = Path.Combine(FindOutputDirectory(), fileName);
        var saved = await SnapshotAsync(sessionId, outputPath, progress, cancellationToken)
            .ConfigureAwait(false);

        StopProcesses(processName, progress);
        await ShutdownAllAsync(sessionId, progress, cancellationToken).ConfigureAwait(false);

        var resultPath = outputPath;
        if (saved && sessionId == ServiceSessionId)
        {
            resultPath = Path.Combine(FindOutputDirectory(), "service.coverage");
            saved = await MergeServiceCoverageAsync(resultPath, progress, cancellationToken, outputPath)
                .ConfigureAwait(false);
        }

        return saved
            ? new TestResult(true, $"{displayName} 종료 및 통합 커버리지 저장 완료: {resultPath}")
            : new TestResult(false,
                $"{displayName}는 종료했지만 커버리지 저장에 실패했습니다. {sessionId} 세션을 확인하십시오.");
    }

    private static async Task<bool> MergeServiceCoverageAsync(
        string outputPath,
        IProgress<string> progress,
        CancellationToken cancellationToken,
        string? liveCoveragePath = null)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath) ?? FindOutputDirectory();
        var candidates = new[]
        {
            liveCoveragePath ?? outputPath,
            Path.Combine(outputDirectory, "service_TC12_unsupported.coverage"),
            Path.Combine(outputDirectory, "service_TC20_lifecycle.coverage"),
            Path.Combine(outputDirectory, "service_TC05_TC20_checkpoint.coverage"),
        };
        var inputs = candidates
            .Where(path => File.Exists(path) && new FileInfo(path).Length > 10)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inputs.Length == 0)
            return false;

        var temporaryPath = Path.Combine(outputDirectory, "service_combined.tmp.coverage");
        File.Delete(temporaryPath);
        var arguments = new List<string> { "merge" };
        arguments.AddRange(inputs);
        arguments.AddRange(["--output", temporaryPath, "--output-format", "coverage"]);
        progress.Report($"Service Live 결과와 독립 시험 {inputs.Length - 1}개를 하나로 통합합니다.");
        var result = await RunCoverageAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || !File.Exists(temporaryPath))
        {
            progress.Report($"Service 커버리지 통합 실패: {result.Message}");
            return false;
        }

        File.Move(temporaryPath, outputPath, overwrite: true);
        progress.Report($"Service 통합 결과 저장 완료: {outputPath}");
        return true;
    }

    private static async Task<bool> SnapshotAsync(
        string sessionId,
        string outputPath,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        File.Delete(outputPath);
        progress.Report($"{sessionId} 결과를 {outputPath}에 저장합니다.");
        var result = await RunCoverageAsync(
            ["snapshot", sessionId, "--output", outputPath, "--timeout", "5000"],
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            progress.Report($"{sessionId} 결과 저장 실패: {result.Message}");
        return result.Succeeded && File.Exists(outputPath) && new FileInfo(outputPath).Length > 10;
    }

    internal static string FindOutputDirectory()
    {
        string[] candidates =
        [
            Directory.GetCurrentDirectory(),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "src", "EzStream.Tray", "bin", "Debug", "net9.0-windows", "win-x64")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "src", "EzStream.Tray", "bin", "Debug", "net9.0-windows", "win-x64")),
        ];
        return candidates.FirstOrDefault(
            directory => File.Exists(Path.Combine(directory, "EzStream.Tray.exe")))
            ?? AppContext.BaseDirectory;
    }

    private static void StopProductProcesses(IProgress<string> progress)
    {
        StopProcesses("EzStream.Tray", progress);
        StopProcesses("EzStream.Service", progress);
    }

    private static void StopProcesses(string processName, IProgress<string> progress)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    progress.Report($"{processName}.exe PID {process.Id}를 종료합니다.");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
                catch (InvalidOperationException)
                {
                    // 조회 직후 종료된 프로세스는 이미 정리된 상태다.
                }
            }
        }
    }

    private static async Task ShutdownAllAsync(
        string sessionId,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = await RunCoverageAsync(
                ["shutdown", sessionId, "--timeout", "5000"],
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                break;
            progress.Report($"{sessionId} 수집 세션을 종료했습니다.");
            CoverageSessionManager.Forget(sessionId);
        }
    }

    private static async Task<CommandResult> RunCoverageAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet-coverage",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("dotnet-coverage를 시작하지 못했습니다.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
        return new CommandResult(process.ExitCode == 0, message);
    }

    private sealed record CommandResult(bool Succeeded, string Message);
}
