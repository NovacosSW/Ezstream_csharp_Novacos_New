using System.ComponentModel;
using System.Diagnostics;

namespace EzStream.CoverageTool;

internal static class ProductSessionLauncher
{
    private const string ServiceSessionId = "EzStreamServiceLive";

    public static async Task<TestResult> EnsureServiceAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (await CanReachServiceAsync(cancellationToken).ConfigureAwait(false))
            return new TestResult(true, "실제 Service IPC 연결을 확인했습니다.");

        var servicePath = FindServicePath();
        if (servicePath is null)
            return new TestResult(false, "EzStream.Service.exe Debug 실행 파일을 찾지 못했습니다.");

        progress.Report("실제 Service가 없어 EzStreamServiceLive 세션에 자동으로 연결해 실행합니다.");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet-coverage",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("connect");
        startInfo.ArgumentList.Add(ServiceSessionId);
        startInfo.ArgumentList.Add(servicePath);
        startInfo.ArgumentList.Add("--background");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Service 연결 실행을 시작하지 못했습니다.");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                return new TestResult(false,
                    $"EzStreamServiceLive 세션 연결에 실패했습니다. 수집 세션을 먼저 시작하십시오. {detail}");
            }
        }
        catch (Win32Exception ex)
        {
            return new TestResult(false, "dotnet-coverage 실행 실패: " + ex.Message);
        }

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await CanReachServiceAsync(cancellationToken).ConfigureAwait(false))
                return new TestResult(true, "실제 Service를 자동 실행하고 IPC 연결을 확인했습니다.");
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return new TestResult(false, "Service를 실행했지만 15초 안에 IPC 연결을 확인하지 못했습니다.");
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
    {
        string[] candidates =
        [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "src", "EzStream.Service", "bin", "Debug",
                "net9.0-windows", "win-x64", "EzStream.Service.exe")),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),
                "src", "EzStream.Service", "bin", "Debug",
                "net9.0-windows", "win-x64", "EzStream.Service.exe")),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
