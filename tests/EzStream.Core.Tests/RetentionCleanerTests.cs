using EzStream.Core.Retention;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace EzStream.Core.Tests;

public class RetentionCleanerTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezs_ret_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MakeFile(string dir, string name, DateTime lastWrite)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "x");
        File.SetLastWriteTime(p, lastWrite);
        return p;
    }

    [Fact]
    public void DeletesOldFilesKeepsRecent()
    {
        var root = NewTempDir();
        try
        {
            var now = DateTime.Now;
            var oldDir = Path.Combine(root, "live1", "20200101");
            var newDir = Path.Combine(root, "live1", "20260101");
            var oldFile = MakeFile(oldDir, "a.mp4", now.AddDays(-90));
            var newFile = MakeFile(newDir, "b.mp4", now.AddDays(-1));

            int deleted = RetentionCleaner.CleanOnce(root, 60, now, NullLogger.Instance);

            Assert.Equal(1, deleted);
            Assert.False(File.Exists(oldFile));       // 60일 초과 → 삭제
            Assert.True(File.Exists(newFile));        // 최근 → 보존
            Assert.False(Directory.Exists(oldDir));   // 빈 폴더 정리
            Assert.True(Directory.Exists(newDir));
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { } }
    }

    [Fact]
    public void ZeroOrNegativeDaysDisabled()
    {
        var root = NewTempDir();
        try
        {
            var old = MakeFile(root, "old.mp4", DateTime.Now.AddYears(-5));
            Assert.Equal(0, RetentionCleaner.CleanOnce(root, 0, DateTime.Now, NullLogger.Instance));
            Assert.Equal(0, RetentionCleaner.CleanOnce(root, -1, DateTime.Now, NullLogger.Instance));
            Assert.True(File.Exists(old));            // 비활성 → 보존
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { } }
    }

    [Fact]
    public void OnlyMp4AffectedByCleanup()
    {
        var root = NewTempDir();
        try
        {
            var now = DateTime.Now;
            var mp4 = MakeFile(root, "old.mp4", now.AddDays(-100));
            var txt = MakeFile(root, "old.txt", now.AddDays(-100));

            RetentionCleaner.CleanOnce(root, 30, now, NullLogger.Instance);

            Assert.False(File.Exists(mp4));
            Assert.True(File.Exists(txt));            // mp4만 대상
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { } }
    }

    [Fact]
    public void MissingRootNoThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ezs_missing_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, RetentionCleaner.CleanOnce(missing, 30, DateTime.Now, NullLogger.Instance));
    }

    [Fact]
    public void LogsDeletesOldKeepsRecentAndOthers()
    {
        var dir = NewTempDir();
        try
        {
            var now = DateTime.Now;
            var oldLog = MakeFile(dir, "ezstream-20200101.log", now.AddDays(-40));
            var oldRolled = MakeFile(dir, "ezstream-20200101_120000.log", now.AddDays(-40));
            var newLog = MakeFile(dir, "ezstream-20260101.log", now.AddDays(-1));
            var other = MakeFile(dir, "other.log", now.AddDays(-40));

            int deleted = RetentionCleaner.CleanLogs(dir, 30, now, NullLogger.Instance);

            Assert.Equal(2, deleted);
            Assert.False(File.Exists(oldLog));        // 30일 초과 → 삭제
            Assert.False(File.Exists(oldRolled));     // 회전된 파일도 대상
            Assert.True(File.Exists(newLog));         // 최근 → 보존
            Assert.True(File.Exists(other));          // ezstream-*.log 만 대상
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { } }
    }

    [Fact]
    public void LogsZeroOrNegativeDaysDisabled()
    {
        var dir = NewTempDir();
        try
        {
            var old = MakeFile(dir, "ezstream-20200101.log", DateTime.Now.AddYears(-5));
            Assert.Equal(0, RetentionCleaner.CleanLogs(dir, 0, DateTime.Now, NullLogger.Instance));
            Assert.Equal(0, RetentionCleaner.CleanLogs(dir, -1, DateTime.Now, NullLogger.Instance));
            Assert.True(File.Exists(old));            // 비활성 → 보존
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { } }
    }

    [Fact]
    public void LogsMissingDirNoThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ezs_missing_" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, RetentionCleaner.CleanLogs(missing, 30, DateTime.Now, NullLogger.Instance));
    }
}
