using EzStream.Core;
using EzStream.Core.Ipc;

namespace EzStream.Tray;

/// <summary>트레이 아이콘 + 컨텍스트 메뉴. 아이콘 클릭 시 상태창을 연다.</summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;
    private StatusForm? _statusForm;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("상태 보기", null, (_, _) => ShowStatus());
        menu.Items.Add("저장 경로 열기", null, async (_, _) => await OpenSavePathAsync() . ConfigureAwait ( false ) );
        menu.Items.Add("설정", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Icon = IconFactory.CreateRecIcon(active: true),
            Text = UiText.Get("AppName"),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowStatus();
        };
    }

    private void ShowStatus()
    {
        if (_statusForm == null || _statusForm.IsDisposed)
            _statusForm = new StatusForm();
        _statusForm.Show();
        _statusForm.ResumeRefresh();
        _statusForm.WindowState = FormWindowState.Normal;
        _statusForm.Activate();
        _statusForm.BringToFront();
    }

    private static void ShowSettings()
    {
        using var dlg = new SettingsForm();
        dlg.ShowDialog();
    }

    private static async Task OpenSavePathAsync()
    {
        try
        {
            var resp = await PipeClient.SendAsync(new IpcRequest { Command = IpcCommands.GetStatus }) . ConfigureAwait ( false );
            var path = resp.Status?.DocumentRoot;
            if (string.IsNullOrWhiteSpace(path))
                path = EzStream.Core.Config.RecorderConfig.DefaultDocumentRoot();
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            MessageBox.Show("저장 경로를 열 수 없습니다: " + ex.Message, "EzStream",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ExitApp()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusForm?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
