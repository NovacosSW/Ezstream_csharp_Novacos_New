using EzStream.Core.Config;

using Microsoft.Extensions.Logging;

namespace EzStream.Core.Retention;

/// <summary>
/// 보존 기간이 지난 파일과 빈 날짜 폴더를 주기적으로 삭제한다.
/// 원본 EzStreamExport.cpp:294-374(disuse_term 기반 삭제) 이식.
/// </summary>
public sealed class RetentionCleaner ( Func<RecorderConfig> configProvider , ILogger logger ) : IDisposable
    {
    private readonly Func<RecorderConfig> _configProvider = configProvider;
    private readonly ILogger _logger = logger;
    private Timer? _timer;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public void Start()
    {
        // 시작 30초 후 첫 실행, 이후 1시간 간격
        _timer = new Timer(_ => SafeRun(), null, TimeSpan.FromSeconds(30), Interval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void SafeRun()
    {
        try { Run(); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { RetentionLog.Failed(_logger, ex); }
    }

    private void Run()
    {
        var cfg = _configProvider();
        int deleted = CleanOnce(cfg.DocumentRoot, cfg.DisuseTermDays, DateTime.Now, _logger);
        if (deleted > 0)
            RetentionLog.Deleted(_logger, deleted, cfg.DisuseTermDays);

        int deletedLogs = CleanLogs(AppPaths.LogDir, cfg.LogRetentionDays, DateTime.Now, _logger);
        if (deletedLogs > 0)
            RetentionLog.LogsDeleted(_logger, deletedLogs, cfg.LogRetentionDays);
    }

    /// <summary>
    /// 로그 보존 정리 1회 실행. <paramref name="now"/> 기준 <paramref name="logRetentionDays"/>일보다
    /// 오래된 ezstream-*.log 를 삭제하고 삭제한 파일 수를 반환. 0 이하 보존기간이면 아무것도 안 함.
    /// </summary>
    internal static int CleanLogs(string logDir, int logRetentionDays, DateTime now, ILogger logger)
    {
        if (logRetentionDays <= 0) return 0;
        if (string.IsNullOrWhiteSpace(logDir) || !Directory.Exists(logDir)) return 0;

        var cutoff = now.AddDays(-logRetentionDays);
        int deleted = 0;

        foreach (var file in Directory.EnumerateFiles(logDir, "ezstream-*.log", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                RetentionLog.CannotDeleteLog(logger, file, ex);
            }
        }

        return deleted;
    }

    /// <summary>
    /// 보존 정리 1회 실행. 테스트 가능하도록 순수 로직으로 분리.
    /// <paramref name="now"/> 기준 <paramref name="disuseTermDays"/>일보다 오래된 *.mp4 를 삭제하고
    /// 빈 하위 폴더를 정리한다. 삭제한 파일 수를 반환. 0 이하 보존기간이면 아무것도 안 함.
    /// </summary>
    internal static int CleanOnce(string root, int disuseTermDays, DateTime now, ILogger logger)
    {
        if (disuseTermDays <= 0) return 0;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;

        var cutoff = now.AddDays(-disuseTermDays);
        int deleted = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    info.Delete();
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                RetentionLog.CannotDeleteFile(logger, file, ex);
            }
        }

        // 빈 날짜 폴더 정리(깊은 경로부터)
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { /* 무시 */ }
        }

        return deleted;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>현재 설정을 기준으로 보존 정리를 즉시 한 번 실행한다.</summary>
    public void RunNow() => SafeRun();
    }
