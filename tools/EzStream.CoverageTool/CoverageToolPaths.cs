using System.Security;

namespace EzStream.CoverageTool;

internal static class CoverageToolPaths
{
    private const string CoverageExecutableName = "dotnet-coverage.exe";

    internal static string BaseDirectory { get; } =
        Path.GetFullPath(AppContext.BaseDirectory);

    internal static string SettingsPath =>
        Path.Combine(BaseDirectory, "Coverage.runsettings");

    internal static string ResultsDirectory { get; } = ResolveResultsDirectory();

    internal static string? FindProductExecutable(string projectName, string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        string[] bundledCandidates =
        [
            Path.Combine(BaseDirectory, "Harness", executableName),
            Path.Combine(BaseDirectory, executableName),
        ];
        var bundled = bundledCandidates.FirstOrDefault(File.Exists);
        return bundled ?? FindDevelopmentExecutable(projectName, executableName);
    }

    internal static string? FindHarnessExecutable(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);
        string[] bundledCandidates =
        [
            Path.Combine(BaseDirectory, "Harness", executableName),
            Path.Combine(BaseDirectory, executableName),
        ];
        var bundled = bundledCandidates.FirstOrDefault(File.Exists);
        return bundled ?? FindDevelopmentExecutable("EzStream.CoverageHarness", executableName);
    }

    internal static string? FindCoverageExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("EZSTREAM_DOTNET_COVERAGE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Path.GetFullPath(configured);
            if (File.Exists(configuredPath))
                return configuredPath;
        }

        string[] bundledCandidates =
        [
            Path.Combine(BaseDirectory, "Tools", CoverageExecutableName),
            Path.Combine(BaseDirectory, CoverageExecutableName),
        ];
        var bundled = bundledCandidates.FirstOrDefault(File.Exists);
        if (bundled is not null)
            return bundled;

        var installed = FindGlobalCoverageTool();
        if (installed is not null)
            return installed;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
            return null;

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0)
                continue;
            var candidate = Path.Combine(directory, CoverageExecutableName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? FindGlobalCoverageTool()
    {
        var candidates = new List<string>();
        AddGlobalToolCandidate(candidates,
            Environment.GetEnvironmentVariable("DOTNET_CLI_HOME"));
        AddGlobalToolCandidate(candidates,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(File.Exists);
    }

    private static void AddGlobalToolCandidate(List<string> candidates, string? home)
    {
        if (!string.IsNullOrWhiteSpace(home))
            candidates.Add(Path.Combine(home, ".dotnet", "tools", CoverageExecutableName));
    }

    internal static string CoverageExecutablePath =>
        FindCoverageExecutable() ?? CoverageExecutableName;

    internal static TestResult ValidateDeployment(IProgress<string> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var missing = new List<string>();
        var servicePath = FindProductExecutable("EzStream.Service", "EzStream.Service.exe");
        var trayPath = FindProductExecutable("EzStream.Tray", "EzStream.Tray.exe");
        var harnessPath = FindHarnessExecutable("EzStream.CoverageHarness.exe");
        var coveragePath = FindCoverageExecutable();

        if (servicePath is null)
            missing.Add(Path.Combine(BaseDirectory, "Harness", "EzStream.Service.exe"));
        if (trayPath is null)
            missing.Add(Path.Combine(BaseDirectory, "Harness", "EzStream.Tray.exe"));
        if (harnessPath is null)
            missing.Add(Path.Combine(BaseDirectory, "Harness", "EzStream.CoverageHarness.exe"));
        if (!File.Exists(SettingsPath))
            missing.Add(SettingsPath);
        if (coveragePath is null)
            missing.Add(Path.Combine(BaseDirectory, "Tools", CoverageExecutableName) +
                " 또는 Windows PATH의 dotnet-coverage.exe");

        if (missing.Count > 0)
        {
            return new TestResult(false,
                "배포 파일이 부족하여 실행을 시작하지 않았습니다. 누락: " +
                string.Join(", ", missing));
        }

        progress.Report($"Service 실행 경로: {servicePath}");
        progress.Report($"Tray 실행 경로: {trayPath}");
        progress.Report($"시험 Harness 경로: {harnessPath}");
        progress.Report($"dotnet-coverage 경로: {coveragePath}");
        progress.Report($"커버리지 결과 폴더: {ResultsDirectory}");
        return new TestResult(true, "배포 실행 파일과 커버리지 도구 경로를 확인했습니다.");
    }

    private static string? FindDevelopmentExecutable(string projectName, string executableName)
    {
        foreach (var startDirectory in new[] { BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(startDirectory);
            while (directory is not null)
            {
                foreach (var configuration in new[] { "Debug", "Release" })
                {
                    var candidate = Path.Combine(directory.FullName,
                        projectName == "EzStream.CoverageHarness" ? "tools" : "src",
                        projectName, "bin", configuration,
                        "net9.0-windows", "win-x64", executableName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                directory = directory.Parent;
            }
        }
        return null;
    }

    private static string ResolveResultsDirectory()
    {
        var portable = Path.Combine(BaseDirectory, "Results");
        if (CanWriteDirectory(portable))
            return portable;

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallback = Path.Combine(localData, "EzStreamCoverageTool", "Results");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static bool CanWriteDirectory(string directory)
    {
        var probePath = Path.Combine(directory, $".write-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (File.Create(probePath)) { }
            File.Delete(probePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }
}
