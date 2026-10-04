// 임시 파일 이름 변경 경쟁으로 실제 파일 크기 조회 예외와 저장 실패 알림을 검증한다.
using System.Diagnostics;
using System.Reflection;
using EzStream.Core.Config;
using EzStream.Core.Notifications;
using EzStream.Core.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace EzStream.CoverageHarness.Scenarios;

internal static class RecorderFileInfoCoverageScenario
{
    public static bool Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezstream-file-info-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            return RunRace(root);
        }
        finally
        {
            File.Delete(Path.Combine(root, "segment.mp4"));
            File.Delete(Path.Combine(root, "moved.mp4"));
            Directory.Delete(root);
        }
    }

    private static bool RunRace(string root)
    {
        var path = Path.Combine(root, "segment.mp4");
        var moved = Path.Combine(root, "moved.mp4");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var notices = new List<VideoSaveNotification>();
        var recorder = new SourceRecorder(
            new SourceConfig { Url = new Uri(path), Path = "file-info", FilePrefix = "probe" },
            new RecorderConfig { DocumentRoot = root, SegmentMinutes = 1 },
            NullLogger.Instance, notices.Add);
        var currentFile = GetField("_currentFile");
        var startedAt = GetField("_segmentStartedAt");
        var report = typeof(SourceRecorder).GetMethod("ReportClosedSegment", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("ReportClosedSegment");

        VideoSaveNotification Report(string file)
        {
            notices.Clear();
            currentFile.SetValue(recorder, file);
            startedAt.SetValue(recorder, DateTimeOffset.Now);
            report.Invoke(recorder, [true, null]);
            return notices.Single();
        }

        Require(Report(path) is { Success: true, FileSizeBytes: 4, Error: null }, "정상 파일 크기 보고 실패");
        Require(Report(moved) is { Success: false, FileSizeBytes: 0, Error: "Output file is empty or missing" },
            "누락 파일 보고 실패");

        using var stop = new CancellationTokenSource();
        var mover = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                File.Move(path, moved);
                File.Move(moved, path);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                var notice = Report(path);
                if (notice.Error?.StartsWith("file_info: ", StringComparison.Ordinal) != true)
                    continue;
                Require(!notice.Success && notice.FileSizeBytes == 0,
                    "파일 조회 예외가 실패·크기 0으로 보고되지 않았습니다.");
                Require(currentFile.GetValue(recorder) is null && startedAt.GetValue(recorder) is null,
                    "파일 조회 예외 후 세그먼트 상태가 남았습니다.");
                return true;
            }
            throw new InvalidOperationException("파일 조회 경쟁 예외 미재현(5초). 타이밍 의존 시험이므로 재시험이 필요합니다.");
        }
        finally
        {
            stop.Cancel();
            mover.GetAwaiter().GetResult();
        }
    }

    private static FieldInfo GetField(string name)
        => typeof(SourceRecorder).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
