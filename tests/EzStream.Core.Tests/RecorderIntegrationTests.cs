using System.Text;

using EzStream.Core.Config;
using EzStream.Core.Recording;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;
using Xunit.Abstractions;

namespace EzStream.Core.Tests;

/// <summary>
/// 실제 RTSP 소스를 대상으로 하는 통합 + 메모리 안정성 테스트.
/// 환경변수 EZSTREAM_TEST_RTSP 가 없으면 건너뛴다(단위 테스트 CI 를 막지 않기 위함).
///   EZSTREAM_TEST_RTSP     : 녹화할 rtsp URL (예: rtsp://210.99.70.120:1935/live/cctv001.stream)
///   EZSTREAM_TEST_SECONDS  : 녹화 시간(기본 25). 90 이상이면 세그먼트 절단이 여러 번 일어나
///                            네이티브 리소스 반복 open/close 의 메모리 증가를 검사한다.
/// 실행 예:
///   set EZSTREAM_TEST_RTSP=rtsp://210.99.70.120:1935/live/cctv001.stream
///   set EZSTREAM_TEST_SECONDS=150
///   dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class RecorderIntegrationTests ( ITestOutputHelper output )
    {
    private readonly ITestOutputHelper _out = output;

    private static string? Url => Environment.GetEnvironmentVariable("EZSTREAM_TEST_RTSP");

    private static int Seconds =>
        int.TryParse(Environment.GetEnvironmentVariable("EZSTREAM_TEST_SECONDS"), out var s) && s > 0 ? s : 25;

    [Fact]
    public void RecordsSegmentsAndProducesValidMp4()
    {
        var url = Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            _out.WriteLine("EZSTREAM_TEST_RTSP 미설정 → 통합 테스트 건너뜀.");
            return; // opt-in skip
        }

        var root = Path.Combine(Path.GetTempPath(), "ezs_it_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cfg = new RecorderConfig
        {
            DocumentRoot = root,
            SegmentMinutes = 1,          // 최소 절단 주기(60초)
            DisuseTermDays = 0,
            FfmpegLogLevel = "error",
            Sources = { new SourceConfig { Url = new Uri(url!, UriKind.Absolute), Path = "cam", FilePrefix = "cam" } },
        };

        int seconds = Seconds;
        using var engine = new RecorderEngine(cfg, NullLogger.Instance);

        long wsBaseline = 0;
        try
        {
            engine.Start();

            // 워밍업(첫 세그먼트 오픈) 후 메모리 기준선
            Thread.Sleep(20_000);
            var st = engine.GetStatus();
            Assert.Single(st.Sources);
            _out.WriteLine($"warmup state = {st.Sources[0].State}, err={st.Sources[0].LastError}");

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            wsBaseline = CurrentWorkingSet();

            var remain = seconds - 20;
            if (remain > 0) Thread.Sleep(remain * 1000);
        }
        finally
        {
            engine.Stop(); // 각 소스의 현재 파일을 정상 종료(trailer)
        }

        // 결과 검증: 최소 1개 MP4, 완료된(마지막 제외) 파일은 moov 포함
        var files = Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories)
            .OrderBy(f => f).ToArray();
        _out.WriteLine($"produced {files.Length} file(s):");
        foreach (var f in files) _out.WriteLine($"  {new FileInfo(f).Length,12:N0}  {f}");

        Assert.NotEmpty(files);
        // Stop 이 마지막 파일도 finalize 하므로 모든 파일이 유효해야 한다.
        foreach (var f in files)
        {
            Assert.True(new FileInfo(f).Length > 1000, $"파일이 너무 작음: {f}");
            Assert.True(HasAtom(f, "moov"), $"moov 없음(미완료 파일): {f}");
            Assert.True(HasAtom(f, "ftyp"), $"ftyp 없음: {f}");
        }

        // 메모리 안정성: 충분히 길게 돌려 절단이 여러 번 일어난 경우에만 검사
        if (seconds >= 90 && files.Length >= 2)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long wsEnd = CurrentWorkingSet();
            long growthMb = (wsEnd - wsBaseline) / (1024 * 1024);
            _out.WriteLine($"working set: baseline={wsBaseline / 1024 / 1024}MB end={wsEnd / 1024 / 1024}MB growth={growthMb}MB");
            // 세그먼트 반복 open/close 에서 네이티브 리소스가 새면 수백 MB 로 증가한다.
            Assert.True(growthMb < 200, $"메모리가 과도하게 증가함: +{growthMb}MB (네이티브 누수 의심)");
        }

        try { Directory.Delete(root, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    private static long CurrentWorkingSet()
    {
        using var p = System.Diagnostics.Process.GetCurrentProcess();
        p.Refresh();
        return p.WorkingSet64;
    }

    /// <summary>MP4 파일 앞부분에서 특정 atom(fourcc) 존재 여부를 검사.</summary>
    private static bool HasAtom(string path, string fourcc)
    {
        var target = Encoding.ASCII.GetBytes(fourcc);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // 앞 256KB 안에서 탐색(faststart 라 moov 가 앞쪽에 온다)
        int len = (int)Math.Min(fs.Length, 256 * 1024);
        var buf = new byte[len];
        int read = fs.Read(buf, 0, len);
        for (int i = 0; i + target.Length <= read; i++)
        {
            bool match = true;
            for (int j = 0; j < target.Length; j++)
                if (buf[i + j] != target[j]) { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}
