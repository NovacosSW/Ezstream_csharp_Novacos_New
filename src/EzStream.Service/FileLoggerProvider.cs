using Microsoft .Extensions.Logging;

namespace EzStream.Service;

/// <summary>
/// 외부 의존성 없이 동작하는 최소 파일 로거. 날짜별 파일에 append 하고 크기 초과 시 회전.
/// 서비스는 콘솔이 없으므로 파일 로깅이 필요하다.
/// 오래된 로그 삭제는 설정(logRetentionDays)에 따라 RetentionCleaner가 담당한다.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly Lock _lock = new();
    private const long MaxBytes = 20L * 1024 * 1024; // 20MB

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() { }

    internal void Write(string category, LogLevel level, string message, Exception? ex)
    {
        var shortCat = category.Contains('.', StringComparison.Ordinal) ? category[(category.LastIndexOf('.') + 1)..] : category;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Short(level)}] [{shortCat}] {message}";
        if (ex != null) line += Environment.NewLine + ex;

        lock (_lock)
        {
            try
            {
                var path = Path.Combine(_dir, $"ezstream-{DateTime.Now:yyyyMMdd}.log");
                Rotate(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception ignored) when (ignored is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { /* 로깅 실패는 무시 */ }
        }
    }

    private void Rotate(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length >= MaxBytes)
            {
                var rolled = Path.Combine(_dir, $"ezstream-{DateTime.Now:yyyyMMdd_HHmmss}.log");
                fi.MoveTo(rolled);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    private static string Short(LogLevel l) => l switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger ( FileLoggerProvider provider , string category ) : ILogger
    {
        private readonly FileLoggerProvider _provider = provider;
        private readonly string _category = category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
    }
}
