using EzStream.Core.Config;
using EzStream.Core.Ipc;

namespace EzStream.Tray;

/// <summary>상태창: 소스별 녹화 상태 목록 + 설정 버튼 + 저장 경로 열기 버튼.</summary>
internal sealed class StatusForm : Form
{
    private readonly ListView _list;
    private readonly Label _summary;
    private readonly Button _settingsBtn;
    private readonly Button _openPathBtn;
    private readonly Button _logBtn;
    private readonly System.Windows.Forms.Timer _timer;
    private string _documentRoot = "";
    private LogViewerForm? _logForm;

    public StatusForm()
    {
        Text = UiText.Get("StatusTitle");
        Width = 760;
        Height = 380;
        MinimumSize = new Size(560, 260);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };

        _openPathBtn = new Button { Text = UiText.Get("OpenSavePath"), AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        _openPathBtn.Click += async (_, _) => await OpenSavePathAsync().ConfigureAwait(true);

        _settingsBtn = new Button { Text = UiText.Get("Settings"), AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        _settingsBtn.Click += (_, _) =>
        {
            using var dlg = new SettingsForm();
            dlg.ShowDialog(this);
            RefreshStatus();
        };

        _logBtn = new Button { Text = UiText.Get("ViewLog"), AutoSize = true, Padding = new Padding(6, 2, 6, 2) };
        _logBtn.Click += (_, _) => ShowLogViewer();

        top.Controls.Add(_openPathBtn);
        top.Controls.Add(_settingsBtn);
        top.Controls.Add(_logBtn);

        _summary = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(10, 5, 0, 0), Text = UiText.Get("Connecting") };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
        };
        _list.Columns.Add("경로", 90);
        _list.Columns.Add("상태", 90);
        _list.Columns.Add("현재 파일", 260);
        _list.Columns.Add("세그먼트 시작", 130);
        _list.Columns.Add("기록량", 80);
        _list.Columns.Add("오류", 200);

        Controls.Add(_list);
        Controls.Add(_summary);
        Controls.Add(top);

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => RefreshStatus();

        Load += (_, _) => RefreshStatus();
        Shown += (_, _) => _timer.Start();
        FormClosing += (_, e) =>
        {
            // 닫기 시 종료가 아니라 숨김
            e.Cancel = true;
            _timer.Stop();
            Hide();
        };
    }

    internal void ResumeRefresh()
    {
        _timer.Start();
        RefreshStatus();
    }

    private async void RefreshStatus()
    {
        try
        {
            var resp = await PipeClient.SendAsync(new IpcRequest { Command = IpcCommands.GetStatus }, timeoutMs: 1500).ConfigureAwait(true);
            if (!resp.Ok || resp.Status == null)
            {
                _summary.Text = "서비스 응답 없음: " + (resp.Message ?? "");
                return;
            }
            var s = resp.Status;
            _documentRoot = s.DocumentRoot;
            _summary.Text = $"저장 경로: {s.DocumentRoot}    |    저장 주기: {s.SegmentMinutes}분    |    소스: {s.Sources.Count}개";

            StatusListRenderer.Render(_list, s.Sources);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _summary.Text = "서비스에 연결할 수 없습니다. (서비스 실행 여부 확인) " + ex.Message;
        }
    }

    private void ShowLogViewer()
    {
        if (_logForm == null || _logForm.IsDisposed)
            _logForm = new LogViewerForm();
        _logForm.Show();
        _logForm.WindowState = FormWindowState.Normal;
        _logForm.Activate();
        _logForm.BringToFront();
    }

    private Task OpenSavePathAsync()
        => OpenSavePathCoreAsync(null, showDialog: true);

    private async Task OpenSavePathCoreAsync(string? overridePath, bool showDialog)
    {
        var path = overridePath ?? _documentRoot;
        if (string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var resp = await PipeClient.SendAsync(new IpcRequest { Command = IpcCommands.GetStatus }).ConfigureAwait(true);
                path = resp.Status?.DocumentRoot ?? RecorderConfig.DefaultDocumentRoot();
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                System.Diagnostics.Debug.WriteLine(ex);
                path = RecorderConfig.DefaultDocumentRoot();
            }
        }
        try
        {
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
            if (showDialog)
            {
                MessageBox.Show("저장 경로를 열 수 없습니다: " + ex.Message, "EzStream",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _logForm?.Dispose();
            _timer.Dispose();
            _logBtn.Dispose();
            _openPathBtn.Dispose();
            _settingsBtn.Dispose();
            _summary.Dispose();
            _list.Dispose();
        }
    }
}
