using System.Text;

using EzStream.Core;

namespace EzStream.Tray;

/// <summary>서비스 로그 파일을 실시간으로 이어 보여주는 뷰어(tail).</summary>
internal sealed class LogViewerForm : Form
{
    private readonly LogTextBox _text;
    private readonly Label _header;
    private readonly CheckBox _autoScroll;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly string _logDir;

    private string? _currentFile;
    private long _position;

    public LogViewerForm()
        : this(AppPaths.LogDir)
    {
    }

    internal LogViewerForm(string logDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDir);
        _logDir = logDir;
        Text = UiText.Get("LogTitle");
        Width = 900;
        Height = 520;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        _header = new Label { Dock = DockStyle.Top, Height = 24, Padding = new Padding(8, 4, 0, 0), ForeColor = Color.DimGray };

        _text = new LogTextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9F),
        };

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(6) };
        _autoScroll = new CheckBox { Text = UiText.Get("AutoScroll"), Checked = true, AutoSize = true, Anchor = AnchorStyles.Left };
        var openFolderBtn = new Button { Text = UiText.Get("OpenLogFolder"), AutoSize = true };
        openFolderBtn.Click += (_, _) => OpenLogFolder();
        var clearViewBtn = new Button { Text = UiText.Get("ClearView"), AutoSize = true };
        clearViewBtn.Click += (_, _) => _text.Clear();
        bottom.Controls.Add(_autoScroll);
        bottom.Controls.Add(openFolderBtn);
        bottom.Controls.Add(clearViewBtn);

        Controls.Add(_text);
        Controls.Add(bottom);
        Controls.Add(_header);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Poll();

        Load += (_, _) => { ResetToLatest(); Poll(); };
        Shown += (_, _) => _timer.Start();
        FormClosing += (_, _) => _timer.Stop();
    }

    private string? FindLatestLog()
    {
        try
        {
            var dir = new DirectoryInfo(_logDir);
            if (!dir.Exists) return null;
            return dir.GetFiles("ezstream-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return null; }
    }

    private void ResetToLatest()
    {
        _currentFile = FindLatestLog();
        _position = 0;
        _text.Clear();
        _header.Text = _currentFile == null
            ? $"로그 파일이 없습니다. ({_logDir})"
            : $"파일: {_currentFile}";
    }

    private void Poll()
    {
        // 더 최신 로그 파일이 생기면(회전/날짜 변경) 전환
        var latest = FindLatestLog();
        if (latest != null && !string.Equals(latest, _currentFile, StringComparison.OrdinalIgnoreCase))
        {
            _currentFile = latest;
            _position = 0;
            _text.Clear();
            _header.Text = $"파일: {_currentFile}";
        }

        // 이번 tick에서 찾지 못했으면 다음 tick에서 다시 찾는다. 같은 tick 안에서
        // 디렉터리를 두 번 조회해도 사용자에게 보이는 갱신 주기는 달라지지 않는다.
        if (_currentFile == null) return;

        try
        {
            using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _position) _position = 0; // 파일이 줄었으면(교체) 처음부터
            if (fs.Length == _position) return;
            fs.Seek(_position, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var chunk = sr.ReadToEnd();
            _position = fs.Position;
            AppendText(chunk);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // 읽기 실패는 다음 tick에서 재시도
        }
    }

    private void AppendText(string chunk)
    {
        var preserveViewport = !_autoScroll.Checked;
        var selectionStart = _text.SelectionStart;
        var selectionLength = _text.SelectionLength;
        var firstVisibleLine = preserveViewport ? _text.FirstVisibleLine : 0;
        var removedCharacters = 0;
        var removedLines = 0;

        if (preserveViewport)
            _text.SuspendDrawing();

        try
        {
            // 너무 커지면 앞부분을 잘라 메모리/렌더 부담 완화
            const int maxChars = 400_000;
            if (_text.TextLength + chunk.Length > maxChars)
            {
                var keep = _text.Text;
                var combined = keep + chunk;
                if (combined.Length > maxChars)
                {
                    removedCharacters = combined.Length - maxChars;
                    removedLines = CountLines(combined.AsSpan(0, removedCharacters));
                    combined = combined[^maxChars..];
                }
                _text.Text = combined;
            }
            else
            {
                _text.AppendText(chunk);
            }

            if (preserveViewport)
            {
                var adjustedStart = Math.Clamp(selectionStart - removedCharacters, 0, _text.TextLength);
                var adjustedLength = Math.Clamp(selectionLength, 0, _text.TextLength - adjustedStart);
                _text.Select(adjustedStart, adjustedLength);
                _text.RestoreFirstVisibleLine(Math.Max(0, firstVisibleLine - removedLines));
            }
            else
            {
                _text.SelectionStart = _text.TextLength;
                _text.ScrollToCaret();
            }
        }
        finally
        {
            if (preserveViewport)
                _text.ResumeDrawing();
        }
    }

    private static int CountLines(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == '\n') count++;
        }

        return count;
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_logDir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _timer.Dispose();
            _autoScroll.Dispose();
            _header.Dispose();
            _text.Dispose();
        }
    }

    private sealed class LogTextBox : TextBox
    {
        private const int EmGetFirstVisibleLine = 0x00CE;
        private const int EmLineScroll = 0x00B6;
        private const int WmSetRedraw = 0x000B;

        internal int FirstVisibleLine
        {
            get
            {
                var message = Message.Create(Handle, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero);
                DefWndProc(ref message);
                return message.Result.ToInt32();
            }
        }

        internal void RestoreFirstVisibleLine(int firstVisibleLine)
        {
            var lineDelta = firstVisibleLine - FirstVisibleLine;
            if (lineDelta == 0) return;

            var message = Message.Create(Handle, EmLineScroll, IntPtr.Zero, new IntPtr(lineDelta));
            DefWndProc(ref message);
        }

        internal void SuspendDrawing()
        {
            var message = Message.Create(Handle, WmSetRedraw, IntPtr.Zero, IntPtr.Zero);
            DefWndProc(ref message);
        }

        internal void ResumeDrawing()
        {
            var message = Message.Create(Handle, WmSetRedraw, new IntPtr(1), IntPtr.Zero);
            DefWndProc(ref message);
            Invalidate();
        }
    }
}
