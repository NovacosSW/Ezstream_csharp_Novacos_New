using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Pipes;
using System.Resources;
using System.Reflection;
using System.Text;

using EzStream.Core;
using EzStream.Core.Config;
using EzStream.Core.Ipc;

namespace EzStream.Tray;

internal static class TrayCoverageScenarios
{
    public static Task<string> RunAsync(string command, CancellationToken cancellationToken)
        => command switch
        {
            "STATUS" => RunStatusAsync(cancellationToken),
            "SETTINGS_SAVE" => RunSettingsSaveAsync(cancellationToken),
            "SETTINGS_CANCEL" => RunSettingsCancelAsync(cancellationToken),
            "LOG" => RunLogViewerAsync(cancellationToken),
            "LOG_FAILURE" => RunLogFailureAsync(cancellationToken),
            "LOG_MISSING_FOLDER" => RunMissingLogFolderAsync(cancellationToken),
            "FOLDERS" => RunFolderButtonsAsync(cancellationToken),
            "FOLDER_FAILURE" => RunFolderFailureAsync(cancellationToken),
            "SETTINGS_FAILURE" => RunSettingsFailureAsync(cancellationToken),
            "DISCONNECTED" => RunDisconnectedAsync(cancellationToken),
            "ALL_CONNECTED" => RunAllConnectedAsync(cancellationToken),
            "ALL_CONNECTED_BRANCHES" => RunAllConnectedBranchesAsync(cancellationToken),
            _ => Task.FromResult("FAIL|알 수 없는 트레이 시험 명령입니다."),
        };

    private static async Task<string> RunStatusAsync(CancellationToken cancellationToken)
    {
        ExerciseMissingUiResource();
        using var inactiveIcon = IconFactory.CreateRecIcon(active: false);
        using var form = new StatusForm();
        form.Show();
        await Task.Delay(2300, cancellationToken).ConfigureAwait(true);
        var list = FindControl<ListView>(form);
        StatusListRenderer.Render(list, CreateSampleStatuses());
        FindButton(form, UiText.Get("ViewLog")).PerformClick();
        FindButton(form, UiText.Get("ViewLog")).PerformClick();
        ClickSettingsAndClose(form);
        await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        form.ResumeRefresh();
        await Task.Delay(2300, cancellationToken).ConfigureAwait(true);
        form.Close();
        return "OK|상태창 갱신과 모든 상태·용량 표시를 실행했습니다.";
    }

    private static async Task<string> RunSettingsSaveAsync(CancellationToken cancellationToken)
    {
        using var form = new SettingsForm();
        form.Show();
        await Task.Delay(2500, cancellationToken).ConfigureAwait(true);
        ExerciseSettingsControls(form);
        ExerciseSettingsMapper();
        FindButton(form, "저장(실시간 적용)").PerformClick();
        await Task.Delay(4500, cancellationToken).ConfigureAwait(true);
        return "OK|설정 불러오기, 입력 변환 및 저장을 실행했습니다.";
    }

    private static async Task<string> RunSettingsCancelAsync(CancellationToken cancellationToken)
    {
        using var form = new SettingsForm();
        form.Show();
        await Task.Delay(2500, cancellationToken).ConfigureAwait(true);
        FindButton(form, "취소").PerformClick();
        await Task.Delay(300, cancellationToken).ConfigureAwait(true);
        return "OK|설정 취소와 창 정리를 실행했습니다.";
    }

    private static async Task<string> RunLogViewerAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var firstPath = Path.Combine(AppPaths.LogDir, $"ezstream-ui-a-{identity}.log");
        var secondPath = Path.Combine(AppPaths.LogDir, $"ezstream-ui-b-{identity}.log");
        try
        {
            await WriteLargeLogAsync(firstPath, "A", cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTime(firstPath, DateTime.Now.AddMinutes(1));
            using var form = new LogViewerForm();
            form.Show();
            await Task.Delay(1600, cancellationToken).ConfigureAwait(true);

            var autoScroll = FindControl<CheckBox>(form);
            autoScroll.Checked = false;
            await WriteLargeLogAsync(secondPath, "B", cancellationToken).ConfigureAwait(true);
            File.SetLastWriteTime(secondPath, DateTime.Now.AddMinutes(2));
            await Task.Delay(1600, cancellationToken).ConfigureAwait(true);
            ExerciseLogViewportMove(form);

            await File.WriteAllTextAsync(secondPath, "short", Encoding.UTF8, cancellationToken)
                .ConfigureAwait(true);
            File.SetLastWriteTime(secondPath, DateTime.Now.AddMinutes(2));
            await Task.Delay(1300, cancellationToken).ConfigureAwait(true);
            autoScroll.Checked = true;
            FindButton(form, "화면 지우기").PerformClick();
            ExerciseLogViewportNoMove(form);
            form.Close();
            return "OK|로그 전환, 축소, 자동 스크롤 켜기·끄기 및 화면 지우기를 실행했습니다.";
        }
        finally
        {
            DeleteFile(firstPath);
            DeleteFile(secondPath);
        }
    }

    private static async Task<string> RunLogFailureAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.LogDir);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var path = Path.Combine(AppPaths.LogDir, $"ezstream-ui-lock-{identity}.log");
        await File.WriteAllTextAsync(path, "locked log", Encoding.UTF8, cancellationToken)
            .ConfigureAwait(true);
        File.SetLastWriteTime(path, DateTime.Now.AddMinutes(5));
        try
        {
            using var form = new LogViewerForm();
            form.Show();
            await Task.Delay(1200, cancellationToken).ConfigureAwait(true);
            using (var fileLock = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = fileLock.Length;
                await Task.Delay(1300, cancellationToken).ConfigureAwait(true);
            }
            File.Delete(path);
            await Task.Delay(1300, cancellationToken).ConfigureAwait(true);
            form.Close();
            return "OK|로그 파일 잠금과 실행 중 삭제 분기를 실행했습니다.";
        }
        finally
        {
            DeleteFile(path);
        }
    }

    private static async Task<string> RunMissingLogFolderAsync(CancellationToken cancellationToken)
    {
        var missingLogDir = Path.Combine(
            Path.GetTempPath(), "ezstream-missing-logs-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        try
        {
            using var form = new LogViewerForm(missingLogDir);
            form.Show();
            await Task.Delay(400, cancellationToken).ConfigureAwait(true);
            Directory.CreateDirectory(missingLogDir);
            await File.WriteAllTextAsync(
                Path.Combine(missingLogDir, "ezstream-late.log"),
                "late log",
                Encoding.UTF8,
                cancellationToken).ConfigureAwait(true);
            InvokeInstance(form, "Poll");
            await Task.Delay(1300, cancellationToken).ConfigureAwait(true);
            form.Close();

            using var invalidForm = new LogViewerForm("\0");
            InvokeInstance(invalidForm, "FindLatestLog");
            InvokeInstance(invalidForm, "OpenLogFolder");
            return "OK|없는 로그 폴더의 생성 전·후와 잘못된 경로 분기를 실행했습니다.";
        }
        finally
        {
            if (Directory.Exists(missingLogDir))
                Directory.Delete(missingLogDir, recursive: true);
        }
    }

    private static async Task<string> RunSettingsFailureAsync(CancellationToken cancellationToken)
    {
        var config = new RecorderConfig().ToJson();
        Task<IpcResponse> RejectLoad(IpcRequest _, int __)
            => Task.FromResult(new IpcResponse { Ok = false, Message = "coverage load rejected" });
        using (var loadRejectedForm = new SettingsForm(RejectLoad))
        {
            loadRejectedForm.Show();
            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
            loadRejectedForm.Close();
        }

        Task<IpcResponse> RejectSave(IpcRequest request, int _)
            => Task.FromResult(request.Command == IpcCommands.GetConfig
                ? new IpcResponse { Ok = true, ConfigJson = config }
                : new IpcResponse { Ok = false, Message = "coverage rejected" });
        using (var rejectedForm = new SettingsForm(RejectSave))
        {
            rejectedForm.Show();
            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
            FindButton(rejectedForm, UiText.Get("SaveLive")).PerformClick();
            await Task.Delay(500, cancellationToken).ConfigureAwait(true);
            rejectedForm.Close();
        }

        Task<IpcResponse> ThrowOnSave(IpcRequest request, int _)
            => request.Command == IpcCommands.GetConfig
                ? Task.FromResult(new IpcResponse { Ok = true, ConfigJson = config })
                : Task.FromException<IpcResponse>(new IOException("coverage connection failure"));
        using var exceptionForm = new SettingsForm(ThrowOnSave);
        exceptionForm.Show();
        await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        FindButton(exceptionForm, UiText.Get("SaveLive")).PerformClick();
        await Task.Delay(500, cancellationToken).ConfigureAwait(true);
        exceptionForm.Close();
        return "OK|설정 저장의 서비스 거부 응답과 통신 예외 분기를 실행했습니다.";
    }

    private static async Task<string> RunFolderButtonsAsync(CancellationToken cancellationToken)
    {
        using var fallbackForm = new StatusForm();
        FindButtonContaining(fallbackForm, "저장 경로").PerformClick();
        await Task.Delay(800, cancellationToken).ConfigureAwait(true);
        await InvokeStatusOpenPathAsync(fallbackForm, null, showDialog: false).ConfigureAwait(true);

        using var statusForm = new StatusForm();
        statusForm.Show();
        await Task.Delay(2300, cancellationToken).ConfigureAwait(true);
        FindButtonContaining(statusForm, "저장 경로").PerformClick();
        await Task.Delay(800, cancellationToken).ConfigureAwait(true);

        using var logForm = new LogViewerForm();
        logForm.Show();
        await Task.Delay(1200, cancellationToken).ConfigureAwait(true);
        FindButton(logForm, "로그 폴더 열기").PerformClick();
        await Task.Delay(800, cancellationToken).ConfigureAwait(true);
        return "OK|영상 저장 폴더와 로그 폴더 열기를 실행했습니다.";
    }

    private static async Task<string> RunFolderFailureAsync(CancellationToken cancellationToken)
    {
        var blockingFile = Path.Combine(Path.GetTempPath(),
            "ezstream-folder-block-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(blockingFile, "coverage", Encoding.UTF8, cancellationToken)
            .ConfigureAwait(true);
        try
        {
            using var form = new StatusForm();
            var method = typeof(StatusForm).GetMethod(
                "OpenSavePathCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(StatusForm).FullName, "OpenSavePathCoreAsync");
            var task = (Task)(method.Invoke(form, [Path.Combine(blockingFile, "child"), false])
                ?? throw new InvalidOperationException("저장 경로 시험 작업을 생성하지 못했습니다."));
            await task.ConfigureAwait(true);

            using var closeDialogTimer = new System.Windows.Forms.Timer { Interval = 300 };
            closeDialogTimer.Tick += (_, _) =>
            {
                closeDialogTimer.Stop();
                SendKeys.SendWait("{ENTER}");
            };
            closeDialogTimer.Start();
            await InvokeStatusOpenPathAsync(
                form, Path.Combine(blockingFile, "dialog-child"), showDialog: true).ConfigureAwait(true);
            return "OK|잘못된 저장 경로의 트레이 폴더 열기 실패 분기를 실행했습니다.";
        }
        finally
        {
            DeleteFile(blockingFile);
        }
    }

    private static async Task<string> RunDisconnectedAsync(CancellationToken cancellationToken)
    {
        if (await IsServiceAvailableAsync(cancellationToken).ConfigureAwait(true))
            return "FAIL|서비스가 연결되어 있습니다. 서비스를 종료한 뒤 다시 실행하십시오.";

        using var statusForm = new StatusForm();
        using var settingsForm = new SettingsForm();
        statusForm.Show();
        settingsForm.Show();
        await Task.Delay(3500, cancellationToken).ConfigureAwait(true);
        await InvokeStatusOpenPathAsync(statusForm, null, showDialog: false).ConfigureAwait(true);
        statusForm.Close();
        settingsForm.Close();

        var failureServer = ReplyWithFailureOnceAsync(cancellationToken);
        using (var rejectedStatusForm = new StatusForm())
        {
            rejectedStatusForm.Show();
            await failureServer.ConfigureAwait(true);
            await Task.Delay(300, cancellationToken).ConfigureAwait(true);
            rejectedStatusForm.Dispose();
        }

        return "OK|상태·설정 연결 실패, 저장 경로 기본값 및 서비스 실패 응답을 실행했습니다.";
    }

    private static async Task<string> RunAllConnectedAsync(CancellationToken cancellationToken)
        => await RunAllConnectedCoreAsync(
        [
            RunStatusAsync,
            RunSettingsSaveAsync,
            RunSettingsCancelAsync,
            RunLogViewerAsync,
        ], cancellationToken).ConfigureAwait(true);

    private static async Task<string> RunAllConnectedBranchesAsync(CancellationToken cancellationToken)
    {
        var connectedResult = await RunAllConnectedAsync(cancellationToken).ConfigureAwait(true);
        Func<CancellationToken, Task<string>>[] partialFailureScenarios =
        [
            static _ => Task.FromResult("OK|coverage success"),
            static _ => Task.FromException<string>(new IOException("coverage partial failure")),
            static _ => Task.FromResult("OK|coverage success"),
            static _ => Task.FromResult("OK|coverage success"),
        ];
        var failureResult = await RunAllConnectedCoreAsync(partialFailureScenarios, cancellationToken)
            .ConfigureAwait(true);
        return connectedResult.StartsWith("OK|", StringComparison.Ordinal)
            && failureResult.StartsWith("FAIL|", StringComparison.Ordinal)
                ? "OK|트레이 전체시험의 성공과 일부 실패 분기를 실행했습니다."
                : "FAIL|트레이 전체시험 성공·실패 분기를 모두 확인하지 못했습니다.";
    }

    private static async Task<string> RunAllConnectedCoreAsync(
        IEnumerable<Func<CancellationToken, Task<string>>> scenarios,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        foreach (var scenario in scenarios)
            results.Add(await RunSafelyAsync(scenario, cancellationToken).ConfigureAwait(true));
        return results.All(static result => result.StartsWith("OK|", StringComparison.Ordinal))
            ? "OK|트레이 연결 상태 시험 4종을 모두 실행했습니다."
            : "FAIL|일부 트레이 시험을 확인하지 못했습니다.";
    }

    private static async Task<string> RunSafelyAsync(
        Func<CancellationToken, Task<string>> scenario,
        CancellationToken cancellationToken)
    {
        try
        {
            return await scenario(cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return "FAIL|" + ex.Message;
        }
    }

    private static Collection<SourceStatus> CreateSampleStatuses()
        =>
        [
            CreateStatus("PLAYING", 512, true),
            CreateStatus("STOPPED", 2048, false),
            CreateStatus("INIT", 2L * 1024 * 1024, true),
            CreateStatus("UNKNOWN", 3L * 1024 * 1024 * 1024, false),
            CreateStatus("PLAYING", 0, false),
        ];

    private static SourceStatus CreateStatus(string state, long bytes, bool hasFile)
        => new()
        {
            Path = "coverage-" + state,
            State = state,
            CurrentFile = hasFile ? @"C:\coverage\sample.mp4" : null,
            SegmentStartedAt = hasFile ? DateTimeOffset.Now : null,
            RecordedBytes = bytes,
            LastError = state == "UNKNOWN" ? "coverage error" : null,
        };

    private static void ExerciseSettingsControls(SettingsForm form)
    {
        var numericControls = FindControls<NumericUpDown>(form).ToArray();
        if (numericControls.Length < 3)
            throw new InvalidOperationException("설정 숫자 입력 컨트롤을 찾지 못했습니다.");

        var retention = numericControls[1];
        var logRetention = numericControls[2];
        var originalRetention = retention.Value;
        var originalLogRetention = logRetention.Value;
        logRetention.Value = originalRetention;
        retention.Value = NextValue(retention);
        logRetention.Value = NextValue(logRetention);
        retention.Value = originalRetention;
        logRetention.Value = originalLogRetention;
    }

    private static void ExerciseSettingsMapper()
    {
        using var grid = new DataGridView { AllowUserToAddRows = true };
        grid.Columns.Add("url", "URL");
        grid.Columns.Add("path", "Path");
        grid.Columns.Add("prefix", "Prefix");
        var sample = new RecorderConfig { DocumentRoot = @"C:\coverage" };
        sample.Sources.Add(new SourceConfig
        {
            Url = new Uri("rtsp://127.0.0.1/sample"),
            Path = "sample",
            FilePrefix = "sample",
        });
        sample.Sources.Add(new SourceConfig
        {
            Url = null,
            Path = "no-url",
            FilePrefix = "fallback",
        });
        SettingsConfigMapper.PopulateSources(grid, sample);
        grid.Rows.Add(string.Empty, string.Empty, string.Empty);
        grid.Rows.Add("rtsp://127.0.0.1/default-prefix", string.Empty, string.Empty);
        var nullTextRow = grid.Rows[grid.Rows.Add(
            "rtsp://127.0.0.1/null-text", string.Empty, string.Empty)];
        nullTextRow.Cells["path"].Value = new NullTextValue();
        nullTextRow.Cells["prefix"].Value = new NullTextValue();
        _ = SettingsConfigMapper.Read(sample, 1, 0, 0, " ", grid);
    }

    private static decimal NextValue(NumericUpDown control)
        => control.Value < control.Maximum ? control.Value + 1 : control.Value - 1;

    private static void ExerciseLogViewportNoMove(LogViewerForm form)
    {
        FieldInfo textField = typeof(LogViewerForm).GetField(
            "_text", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(LogViewerForm).FullName, "_text");
        var text = textField.GetValue(form)
            ?? throw new InvalidOperationException("로그 텍스트 컨트롤을 찾지 못했습니다.");
        PropertyInfo firstVisibleLine = text.GetType().GetProperty(
            "FirstVisibleLine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(text.GetType().FullName, "FirstVisibleLine");
        MethodInfo restore = text.GetType().GetMethod(
            "RestoreFirstVisibleLine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(text.GetType().FullName, "RestoreFirstVisibleLine");
        var line = (int)(firstVisibleLine.GetValue(text) ?? 0);
        _ = restore.Invoke(text, [line]);
    }

    private static void ExerciseLogViewportMove(LogViewerForm form)
    {
        FieldInfo textField = typeof(LogViewerForm).GetField(
            "_text", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(LogViewerForm).FullName, "_text");
        var text = (TextBox)(textField.GetValue(form)
            ?? throw new InvalidOperationException("로그 텍스트 컨트롤을 찾지 못했습니다."));
        text.Select(Math.Max(0, text.TextLength - 80_000), 0);
        text.ScrollToCaret();

        MethodInfo append = typeof(LogViewerForm).GetMethod(
            "AppendText", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(LogViewerForm).FullName, "AppendText");
        var chunk = string.Concat(Enumerable.Repeat("viewport coverage line\r\n", 10_000));
        _ = append.Invoke(form, [chunk]);
    }

    private static object? InvokeInstance(object instance, string methodName)
    {
        MethodInfo method = instance.GetType().GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(instance.GetType().FullName, methodName);
        return method.Invoke(instance, null);
    }

    private static Task InvokeStatusOpenPathAsync(
        StatusForm form,
        string? path,
        bool showDialog)
    {
        MethodInfo method = typeof(StatusForm).GetMethod(
            "OpenSavePathCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(StatusForm).FullName, "OpenSavePathCoreAsync");
        return (Task)(method.Invoke(form, [path, showDialog])
            ?? throw new InvalidOperationException("저장 경로 시험 작업을 생성하지 못했습니다."));
    }

    private static void ExerciseMissingUiResource()
    {
        try
        {
            _ = UiText.Get("CoverageUnknownKey");
        }
        catch (MissingManifestResourceException)
        {
        }
    }

    private static void ClickSettingsAndClose(StatusForm form)
    {
        using var closeTimer = new System.Windows.Forms.Timer { Interval = 800 };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            Application.OpenForms.OfType<SettingsForm>().FirstOrDefault()?.Close();
        };
        closeTimer.Start();
        FindButton(form, UiText.Get("Settings")).PerformClick();
    }

    private static async Task<bool> IsServiceAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await PipeClient.SendAsync(new IpcRequest { Command = IpcCommands.GetStatus },
                timeoutMs: 1000, cancellationToken).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            return false;
        }
    }

    private static async Task ReplyWithFailureOnceAsync(CancellationToken cancellationToken)
    {
        using var server = new NamedPipeServerStream(
            AppPaths.PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(true);
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
        _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(true);
        var response = new IpcResponse { Ok = false, Message = "coverage rejected" };
        await writer.WriteLineAsync(response.Serialize().AsMemory(), cancellationToken).ConfigureAwait(true);
    }

    private static T FindControl<T>(Control root) where T : Control
        => FindControls<T>(root).FirstOrDefault()
            ?? throw new InvalidOperationException($"{typeof(T).Name} 컨트롤을 찾지 못했습니다.");

    private static IEnumerable<T> FindControls<T>(Control root) where T : Control
    {
        var pending = new Stack<Control>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is T matched)
                yield return matched;
            foreach (Control child in current.Controls)
                pending.Push(child);
        }
    }

    private static Button FindButton(Control root, string text)
        => FindControls<Button>(root).FirstOrDefault(button =>
            string.Equals(button.Text, text, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{text}' 버튼을 찾지 못했습니다.");

    private static Button FindButtonContaining(Control root, string text)
        => FindControls<Button>(root).FirstOrDefault(button =>
            button.Text.Contains(text, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"'{text}' 버튼을 찾지 못했습니다.");

    private static async Task WriteLargeLogAsync(
        string path,
        string marker,
        CancellationToken cancellationToken)
    {
        var line = string.Concat(marker, " coverage log line", Environment.NewLine);
        var builder = new StringBuilder(410_000);
        while (builder.Length < 410_000)
            _ = builder.Append(line);
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken)
            .ConfigureAwait(true);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private sealed class NullTextValue
    {
        public override string ToString() => null!;
    }
}
