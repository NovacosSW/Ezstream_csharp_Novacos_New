using System.Reflection;

using EzStream.CoverageHarness.Scenarios;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Recording;
using EzStream.Service;
using EzStream.Tray;

using Microsoft.Extensions.Logging.Abstractions;

namespace EzStream.CoverageHarness;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var scenario = args.FirstOrDefault() ?? string.Empty;
        if (string.Equals(scenario, "TRAY_PROGRAM_EXIT", StringComparison.Ordinal))
            return RunTrayProgramExit();

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        return IsTrayScenario(scenario)
            ? RunTrayScenario(scenario)
            : RunCoreScenario(scenario);
    }

    private static int RunCoreScenario(string scenario)
    {
        try
        {
            var succeeded = scenario switch
            {
                "CONFIG_BRANCHES" => HasText(ServiceCoverageScenarios.RunConfigBranches()),
                "FFMPEG_BRANCHES" => HasText(ServiceCoverageScenarios.RunFfmpegBranches()),
                "MISSING_TIMESTAMP" => RecorderCoverageScenarios.RunMissingTimestamp(),
                "INVALID_TIMESTAMP" => RunInvalidTimestamp(),
                "STREAM_INFO_FAILURE" => RecorderStreamInfoFailureScenario.Run(),
                "ALLOC_OUTPUT_FAILURE" => RecorderAllocOutputFailureScenario.Run(),
                "NEW_STREAM_FAILURE" => RecorderAllocOutputFailureScenario.Run(failNewStream: true),
                "SERVICE_LOG_LEVELS" => HasText(ServiceCoverageScenarios.RunServiceLogLevels()),
                "RESIDUAL_BRANCHES" => HasText(ServiceCoverageScenarios.RunResidualBranches()),
                "PIPE_ACCEPT_FAILURE" => HasText(ServiceCoverageScenarios.RunPipeAcceptFailure()),
                "SERVICE_LIFECYCLE" => HasText(ServiceCoverageScenarios.RunServiceLifecycle()),
                "SERVICE_HOST_LIFECYCLE" => HasText(ServiceHostCoverageScenario.Run()),
                "WORKER_START_FAILURE" => HasText(ServiceCoverageScenarios.RunWorkerStartFailure()),
                "RETENTION_ZERO" => RecorderCoverageScenarios.RunRetentionZero(),
                "RETENTION_MISSING_PATHS" => RecorderCoverageScenarios.RunRetentionMissingPaths(),
                "RECORDER_LIFECYCLE" => RecorderCoverageScenarios.RunRecorderLifecycle(),
                "RECORDER_EAGAIN" => HasText(RecorderEagainCoverageScenario.Run()),
                "RECORDER_CUT_CONDITIONS" => HasText(RecorderCutCoverageScenario.Run()),
                "SAME_SEGMENT_INTERVAL" => RecorderCoverageScenarios.RunSameSegmentInterval(),
                "UNSUPPORTED_CODEC_HEADER" => RecorderCoverageScenarios.RunUnsupportedCodecHeader(),
                "EMPTY_PIPE_RESPONSE" => RecorderCoverageScenarios.RunEmptyPipeResponse(),
                _ => false,
            };
            return WriteResult(succeeded, succeeded ? $"{scenario} 완료" : $"{scenario} 실패");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return WriteResult(false, ex.Message);
        }
    }

    private static int RunTrayScenario(string scenario)
    {
        var result = string.Empty;
        using var host = new Form
        {
            ShowInTaskbar = false,
            Opacity = 0,
            Width = 1,
            Height = 1,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
        };
        host.Shown += async (_, _) =>
        {
            try
            {
                result = await ExecuteTrayScenarioAsync(scenario).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                result = "FAIL|" + ex.Message;
            }
            finally
            {
                host.Close();
            }
        };
        Application.Run(host);
        Console.WriteLine(result);
        return result.StartsWith("OK|", StringComparison.Ordinal) ? 0 : 1;
    }

    private static async Task<string> ExecuteTrayScenarioAsync(string scenario)
        => scenario switch
        {
            "APP_ACTIONS" => await RunTrayAppActionsAsync().ConfigureAwait(true),
            "EXIT" => RunTrayExit(),
            "PROTOCOL" => await RunTrayProtocolAsync().ConfigureAwait(true),
            _ => await TrayCoverageScenarios.RunAsync(scenario, CancellationToken.None).ConfigureAwait(true),
        };

    private static async Task<string> RunTrayAppActionsAsync()
    {
        using var app = new TrayApp();
        var notifyField = typeof(TrayApp).GetField(
            "_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(TrayApp).FullName, "_notifyIcon");
        var notifyIcon = (NotifyIcon)(notifyField.GetValue(app)
            ?? throw new InvalidOperationException("트레이 아이콘을 찾지 못했습니다."));
        var menu = notifyIcon.ContextMenuStrip
            ?? throw new InvalidOperationException("트레이 메뉴를 찾지 못했습니다.");

        menu.Items[0].PerformClick();
        InvokeInstance(app, "ShowStatus");
        InvokeInstance(app, "ShowStatus");
        var onMouseClick = typeof(NotifyIcon).GetMethod(
            "OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic);
        onMouseClick?.Invoke(notifyIcon, [new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0)]);
        onMouseClick?.Invoke(notifyIcon, [new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0)]);

        menu.Items[1].PerformClick();
        var openTask = (Task)(InvokeStatic(typeof(TrayApp), "OpenSavePathAsync")
            ?? throw new InvalidOperationException("저장 경로 열기 작업을 생성하지 못했습니다."));
        await openTask.ConfigureAwait(true);

        using (var closeTimer = new System.Windows.Forms.Timer { Interval = 500 })
        {
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                Application.OpenForms.OfType<SettingsForm>().FirstOrDefault()?.Close();
            };
            closeTimer.Start();
            menu.Items[2].PerformClick();
        }
        Application.OpenForms.OfType<StatusForm>().FirstOrDefault()?.Close();
        return "OK|트레이 본체의 메뉴·마우스·상태창·설정·저장 경로 동작을 실행했습니다.";
    }

    private static string RunTrayExit()
    {
        using var app = new TrayApp();
        var notifyField = typeof(TrayApp).GetField(
            "_notifyIcon", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(TrayApp).FullName, "_notifyIcon");
        var notifyIcon = (NotifyIcon)(notifyField.GetValue(app)
            ?? throw new InvalidOperationException("트레이 아이콘을 찾지 못했습니다."));
        var menu = notifyIcon.ContextMenuStrip
            ?? throw new InvalidOperationException("트레이 메뉴를 찾지 못했습니다.");
        menu.Items[4].PerformClick();
        return "OK|트레이 정상 종료를 실행했습니다.";
    }

    private static int RunTrayProgramExit()
    {
        using var exitTimer = new System.Threading.Timer(
            static _ => Application.Exit(),
            null,
            TimeSpan.FromMilliseconds(500),
            Timeout.InfiniteTimeSpan);
        InvokeStatic(typeof(EzStream.Tray.Program), "Main");
        return WriteResult(true, "Tray Program.Main 정상 반환 완료");
    }

    private static async Task<string> RunTrayProtocolAsync()
    {
        var empty = await TrayCoverageScenarios.RunAsync(string.Empty, CancellationToken.None)
            .ConfigureAwait(true);
        var unknown = await TrayCoverageScenarios.RunAsync("UNKNOWN", CancellationToken.None)
            .ConfigureAwait(true);
        return empty.StartsWith("FAIL|", StringComparison.Ordinal)
            && unknown.StartsWith("FAIL|", StringComparison.Ordinal)
                ? "OK|빈 명령과 알 수 없는 트레이 명령을 실행했습니다."
                : "FAIL|트레이 명령 오류 분기를 확인하지 못했습니다.";
    }

    private static bool RunInvalidTimestamp()
    {
        FfmpegLoader.Initialize(
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            "WARNING",
            NullLogger.Instance);
        return RecorderCoverageScenarios.RunInvalidTimestampOrder();
    }

    private static bool IsTrayScenario(string scenario)
        => scenario is "STATUS" or "SETTINGS_SAVE" or "SETTINGS_CANCEL" or "LOG"
            or "LOG_FAILURE" or "LOG_MISSING_FOLDER" or "FOLDERS" or "FOLDER_FAILURE"
            or "SETTINGS_FAILURE" or "DISCONNECTED" or "ALL_CONNECTED"
            or "ALL_CONNECTED_BRANCHES" or "APP_ACTIONS" or "EXIT" or "PROTOCOL"
            or "TRAY_PROGRAM_EXIT";

    private static bool HasText(string value) => !string.IsNullOrWhiteSpace(value);

    private static int WriteResult(bool succeeded, string message)
    {
        Console.WriteLine((succeeded ? "OK|" : "FAIL|") + message);
        return succeeded ? 0 : 1;
    }

    private static object? InvokeInstance(object instance, string methodName)
    {
        MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
        return method.Invoke(instance, null);
    }

    private static object? InvokeStatic(Type type, string methodName)
    {
        MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, methodName);
        return method.Invoke(null, null);
    }
}
