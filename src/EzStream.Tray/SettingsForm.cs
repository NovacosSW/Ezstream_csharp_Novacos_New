using EzStream.Core.Config;
using EzStream.Core.Ipc;

namespace EzStream.Tray;

/// <summary>설정창: 저장 주기(분)와 소스 목록을 편집하고 서비스에 실시간 반영한다.</summary>
internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown _minutes;
    private readonly NumericUpDown _retentionDays;
    private readonly NumericUpDown _logRetentionDays;
    private decimal _lastRetention;
    private readonly TextBox _documentRoot;
    private readonly DataGridView _grid;
    private readonly Button _saveBtn;
    private readonly Button _cancelBtn;
    private readonly Label _status;
    private readonly Func<IpcRequest, int, Task<IpcResponse>> _sendAsync;
    private RecorderConfig _loaded = new(); // 화면에 없는 항목(ffmpegLogLevel 등)을 저장 시 보존하기 위함

    public SettingsForm()
        : this(null)
    {
    }

    internal SettingsForm(Func<IpcRequest, int, Task<IpcResponse>>? sendAsync)
    {
        _sendAsync = sendAsync ?? ((request, timeout) => PipeClient.SendAsync(request, timeout));
        Text = UiText.Get("SettingsTitle");
        Width = 720;
        Height = 510;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = FormBorderStyle.Sizable;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(12),
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 저장 주기(분)
        layout.Controls.Add(new Label { Text = UiText.Get("SegmentMinutes"), Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 0);
        _minutes = new NumericUpDown { Minimum = 1, Maximum = 1440, Value = 10, Width = 100, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_minutes, 1, 0);

        // 보존 기간(일)
        layout.Controls.Add(new Label { Text = UiText.Get("RetentionDays"), Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 1);
        _retentionDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Value = 31, Width = 100, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_retentionDays, 1, 1);

        // 로그 보존 기간(일)
        layout.Controls.Add(new Label { Text = UiText.Get("LogRetentionDays"), Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 2);
        _logRetentionDays = new NumericUpDown { Minimum = 0, Maximum = 3650, Value = _retentionDays.Value, Width = 100, Anchor = AnchorStyles.Left };
        layout.Controls.Add(_logRetentionDays, 1, 2);
        // 로그 보존 기간이 영상 보존 기간과 같은 동안(= 따로 바꾸지 않았으면) 영상 보존 기간을 따라간다
        _lastRetention = _retentionDays.Value;
        _retentionDays.ValueChanged += (_, _) =>
        {
            if (_logRetentionDays.Value == _lastRetention)
                _logRetentionDays.Value = _retentionDays.Value;
            _lastRetention = _retentionDays.Value;
        };

        // 저장 경로
        layout.Controls.Add(new Label { Text = UiText.Get("DocumentRoot"), Anchor = AnchorStyles.Left, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 3);
        _documentRoot = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Width = 480 };
        layout.Controls.Add(_documentRoot, 1, 3);

        // 소스 그리드
        layout.Controls.Add(new Label { Text = UiText.Get("Sources"), Anchor = AnchorStyles.Left | AnchorStyles.Top, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, 4);
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            RowHeadersVisible = true,
            EditMode = DataGridViewEditMode.EditOnEnter,
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "url", HeaderText = UiText.Get("UrlHeader"), Width = 320 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = UiText.Get("PathHeader"), Width = 90 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "prefix", HeaderText = UiText.Get("PrefixHeader"), Width = 90 });
        layout.Controls.Add(_grid, 1, 4);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 버튼 영역
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Anchor = AnchorStyles.Right };
        _saveBtn = new Button { Text = UiText.Get("SaveLive"), AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        _cancelBtn = new Button { Text = UiText.Get("Cancel"), AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        _saveBtn.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        _cancelBtn.Click += (_, _) => Close();
        buttons.Controls.Add(_saveBtn);
        buttons.Controls.Add(_cancelBtn);
        layout.Controls.Add(buttons, 1, 5);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _status = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(12, 2, 0, 0) };

        Controls.Add(layout);
        Controls.Add(_status);

        Load += async (_, _) => await LoadConfigAsync().ConfigureAwait(true);
    }

    private async Task LoadConfigAsync()
    {
        try
        {
            var resp = await _sendAsync(new IpcRequest { Command = IpcCommands.GetConfig }, 2000)
                .ConfigureAwait(true);
            if (!resp.Ok || string.IsNullOrWhiteSpace(resp.ConfigJson))
            {
                _status.Text = "설정을 불러오지 못했습니다: " + (resp.Message ?? "");
                return;
            }
            var cfg = RecorderConfig.FromJson(resp.ConfigJson);
            _loaded = cfg;
            _minutes.Value = Math.Clamp(cfg.SegmentMinutes, (int)_minutes.Minimum, (int)_minutes.Maximum);
            _retentionDays.Value = Math.Clamp(cfg.DisuseTermDays, 0, (int)_retentionDays.Maximum);
            _logRetentionDays.Value = Math.Clamp(cfg.LogRetentionDays, 0, (int)_logRetentionDays.Maximum);
            _documentRoot.Text = cfg.DocumentRoot;
            SettingsConfigMapper.PopulateSources(_grid, cfg);
            _status.Text = UiText.Get("SettingsLoaded");
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _status.Text = "서비스에 연결할 수 없습니다: " + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        var cfg = SettingsConfigMapper.Read(
            _loaded,
            _minutes.Value,
            _retentionDays.Value,
            _logRetentionDays.Value,
            _documentRoot.Text,
            _grid);

        _saveBtn.Enabled = false;
        bool closeAfterSave = false;
        try
        {
            var resp = await _sendAsync(new IpcRequest
            {
                Command = IpcCommands.SetConfig,
                ConfigJson = cfg.ToJson(),
            }, 4000).ConfigureAwait(true);
            if (resp.Ok)
            {
                _status.Text = UiText.Get("SettingsSaved");
                closeAfterSave = true;
            }
            else
            {
                _status.Text = "저장 실패: " + (resp.Message ?? "");
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            _status.Text = "저장 실패: " + ex.Message;
        }
        finally
        {
            if (!IsDisposed)
                _saveBtn.Enabled = true;
        }

        if (closeAfterSave && !IsDisposed)
            Close();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _status.Dispose();
            _cancelBtn.Dispose();
            _saveBtn.Dispose();
            _grid.Dispose();
            _documentRoot.Dispose();
            _logRetentionDays.Dispose();
            _retentionDays.Dispose();
            _minutes.Dispose();
        }
    }
}
