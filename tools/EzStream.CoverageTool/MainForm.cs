namespace EzStream.CoverageTool;

internal sealed class MainForm : Form
{
    private static readonly string WindowTitle = string.Concat("EzStream", " 커버리지 시험 도구");
    private static readonly string PageTitle = string.Concat("EzStream", " 외부 장애 조건 시험");
    private static readonly string UsageDescription =
        string.Concat("Service는 EzStreamServiceLive, Tray는 EzStreamTrayLive 세션 ID로 동적검사를 시작하십시오. ",
            "버튼 시험은 해당 PowerShell 동적검사 세션에 직접 연결되어 기록됩니다.");
    private static readonly string RestoreCaption = string.Concat("원래 설정", " 복구");
    private static readonly string PendingRestoreState =
        string.Concat("복구되지 않은 설정이 있습니다. ", "먼저 원래 설정 복구를 실행하십시오.");
    private static readonly string CancelledState = string.Concat("시험", " 취소");
    private static readonly string RunErrorState = string.Concat("시험 실행", " 오류");
    private static readonly string RestoreErrorState = string.Concat("설정 복구", " 실패");
    private readonly CoverageTestRunner _runner = new();
    private readonly TcCoverageRunner _tcRunner;
    private readonly List<Button> _testButtons = [];
    private readonly TextBox _output;
    private readonly Label _state;
    private readonly Button _restoreButton;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    public MainForm()
    {
        _tcRunner = new TcCoverageRunner(_runner);
        Text = WindowTitle;
        Width = 900;
        Height = 790;
        MinimumSize = new Size(760, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("맑은 고딕", 9F);

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 62,
            Text = PageTitle,
            Font = new Font("맑은 고딕", 17F, FontStyle.Bold),
            Padding = new Padding(16, 16, 0, 0),
        };
        var description = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Text = UsageDescription,
            Padding = new Padding(18, 4, 12, 4),
            ForeColor = Color.DimGray,
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 350,
            Padding = new Padding(14, 8, 8, 8),
            AutoScroll = true,
            WrapContents = true,
        };
        AddTestButton(buttons, "★ TC-01~TC-19 전체 순차 실행", "종료 전 연결 상태 시험을 빠짐없이 실행",
            _tcRunner.RunConnectedRangeAsync);

        AddSectionLabel(buttons, "1단계  서비스 재시작 필요 시험");
        AddTcButton(buttons, 1, "설정 파일 오류 및 복구", "없음·빈 파일·손상·저장 실패");
        AddTcButton(buttons, 2, "서비스 및 FFmpeg 로그 수준", "로그 수준과 기본값 처리");
        AddTcButton(buttons, 3, "잔여 시작 및 Core 분기", "시작·로그·엔진·보존 잔여 분기");
        AddTcButton(buttons, 4, "FFmpeg 로드 실패 및 복구", "DLL 누락 시작 실패와 복구");

        AddSectionLabel(buttons, "2단계  서비스 연속 실행 시험");
        AddTcButton(buttons, 5, "IPC 요청 검증 및 오류 처리", "손상·누락·무응답 요청");
        AddTcButton(buttons, 6, "로그 회전 및 기록 장애", "20MB 회전·잠금·정상 복구");
        AddTcButton(buttons, 7, "로그 파일 장애 및 로그창", "삭제·폴더 없음·스크롤");
        AddTcButton(buttons, 8, "저장 경로 장애 및 폴더 열기", "출력 실패·폴더 기능");
        AddTcButton(buttons, 9, "오디오 전용 입력 처리", "비디오 스트림 없음");
        AddTcButton(buttons, 10, "정상 영상 저장 및 혼합 입력", "MP4 저장·오디오 패킷 제외");
        AddTcButton(buttons, 11, "엔진 수명주기 및 세그먼트", "중복 시작·주기 변경·절단");
        AddTcButton(buttons, 12, "미지원 코덱 출력 실패", "MP4 헤더 작성 실패");
        AddTcButton(buttons, 13, "입력 열기 및 스트림 분석 실패", "없는 주소·손상 스트림");
        AddTcButton(buttons, 14, "녹화 중 입력 중단 및 재연결", "강제 종료·세그먼트 마감");
        AddTcButton(buttons, 15, "패킷 타임스탬프 예외", "PTS/DTS 없음·음수·역전");
        AddTcButton(buttons, 16, "보존기간 비활성 및 없는 경로", "0일·없는 영상/로그 경로");
        AddTcButton(buttons, 17, "오래된 파일 생성 및 보존 정리", "영상·로그 생성 후 삭제");
        AddTcButton(buttons, 18, "UDP 영상 저장 결과 송수신", "설정 오류·JSON 수신");
        AddTcButton(buttons, 19, "트레이 설정 저장 및 취소", "저장·취소·전체 연결 분기");

        AddSectionLabel(buttons, "3단계  서비스 종료 후 트레이 시험");
        AddTcButton(buttons, 20, "상태 갱신 및 Service 최종 종료", "Service coverage 저장 후 종료");
        AddTcButton(buttons, 21, "서비스 미연결 트레이 기능", "미연결 상태·설정·경로 오류");
        AddTcButton(buttons, 22, "트레이 중복 실행 및 최종 종료", "Tray coverage 저장 후 종료");


        _restoreButton = new Button
        {
            Text = RestoreCaption,
            AutoSize = true,
            Enabled = _runner.HasPendingBackup,
            Margin = new Padding(18, 5, 0, 5),
        };
        _restoreButton.Click += async (_, _) => await RestoreAsync().ConfigureAwait(true);

        _state = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(18, 9, 0, 0),
            Text = _runner.HasPendingBackup
                ? PendingRestoreState
                : "대기 중",
            ForeColor = _runner.HasPendingBackup ? Color.DarkRed : Color.DarkGreen,
        };
        var restorePanel = new Panel { Dock = DockStyle.Top, Height = 42 };
        restorePanel.Controls.Add(_restoreButton);

        _output = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(28, 28, 28),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9F),
        };

        Controls.Add(_output);
        Controls.Add(_state);
        Controls.Add(restorePanel);
        Controls.Add(buttons);
        Controls.Add(description);
        Controls.Add(title);
        FormClosing += OnFormClosing;
    }

    private void AddTcButton(Control parent, int number, string title, string description)
        => AddTestButton(
            parent,
            $"TC-{number:00}  {title}",
            description,
            (progress, cancellationToken) => _tcRunner.RunAsync(number, progress, cancellationToken));

    private static void AddSectionLabel(Control parent, string text)
    {
        var label = new Label
        {
            Width = 820,
            Height = 30,
            Text = text,
            Font = new Font("맑은 고딕", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(31, 78, 121),
            Padding = new Padding(4, 7, 0, 0),
            Margin = new Padding(4, 8, 4, 2),
        };
        parent.Controls.Add(label);
    }

    private void AddTestButton(
        Control parent,
        string title,
        string description,
        Func<IProgress<string>, CancellationToken, Task<TestResult>> action)
    {
        var button = new Button
        {
            Width = 260,
            Height = 62,
            Text = title + Environment.NewLine + description,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 4, 0),
            Margin = new Padding(4),
        };
        button.Click += async (_, _) => await RunTestAsync(title, action).ConfigureAwait(true);
        _testButtons.Add(button);
        parent.Controls.Add(button);
    }

    private async Task RunTestAsync(
        string name,
        Func<IProgress<string>, CancellationToken, Task<TestResult>> action)
    {
        if (_busy)
            return;

        if (_runner.HasPendingBackup)
        {
            MessageBox.Show("복구되지 않은 설정이 있습니다. 먼저 원래 설정 복구를 실행하십시오.",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (RequiresConfirmation(name))
        {
            var answer = MessageBox.Show(
                "시험용 로그·영상 파일을 만들거나 서비스 설정을 일시적으로 변경합니다. 계속하시겠습니까?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;
        }

        SetBusy(true, name + " 실행 중");
        Append($"=== {name} 시작 ===");
        try
        {
            var progress = new Progress<string>(Append);
            var result = await action(progress, _lifetime.Token).ConfigureAwait(true);
            Append(result.Summary);
            Append(result.Succeeded ? "결과: 실행 확인" : "결과: 확인 필요");
            _state.Text = result.Summary;
            _state.ForeColor = result.Succeeded ? Color.DarkGreen : Color.DarkOrange;
        }
        catch (OperationCanceledException)
        {
            Append("시험이 취소되었습니다.");
            _state.Text = CancelledState;
            _state.ForeColor = Color.DarkOrange;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Append("오류: " + ex.Message);
            _state.Text = RunErrorState;
            _state.ForeColor = Color.DarkRed;
            MessageBox.Show(ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false, _state.Text);
        }
    }

    private static bool RequiresConfirmation(string name)
        => name.StartsWith("TC-", StringComparison.Ordinal)
            && !name.StartsWith("TC-23", StringComparison.Ordinal)
            || name.StartsWith('★');

    private async Task RestoreAsync()
    {
        if (_busy)
            return;

        SetBusy(true, "원래 설정 복구 중");
        try
        {
            var result = await _runner.RestorePendingAsync(new Progress<string>(Append))
                .ConfigureAwait(true);
            _state.Text = result.Summary;
            _state.ForeColor = Color.DarkGreen;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Append("설정 복구 오류: " + ex.Message);
            _state.Text = RestoreErrorState;
            _state.ForeColor = Color.DarkRed;
        }
        finally
        {
            SetBusy(false, _state.Text);
        }
    }

    private void SetBusy(bool busy, string state)
    {
        _busy = busy;
        foreach (var button in _testButtons)
            button.Enabled = !busy;
        _restoreButton.Enabled = !busy && _runner.HasPendingBackup;
        _state.Text = state;
    }

    private void Append(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Append(message));
            return;
        }

        _output.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        _output.SelectionStart = _output.TextLength;
        _output.ScrollToCaret();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_busy)
            return;

        var answer = MessageBox.Show("시험이 실행 중입니다. 종료하면 자동 복구가 완료되지 않을 수 있습니다. 종료하시겠습니까?",
            Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (answer == DialogResult.No)
        {
            e.Cancel = true;
            return;
        }
        _lifetime.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lifetime.Dispose();
            _restoreButton.Dispose();
            _state.Dispose();
            _output.Dispose();
        }
        base.Dispose(disposing);
    }
}
