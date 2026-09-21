using EzStream.Core.Config;

using Xunit;

namespace EzStream.Core.Tests;

public class RecorderConfigTests
{
    [Fact]
    public void DefaultsMatchSpec()
    {
        var cfg = new RecorderConfig();
        Assert.Equal(@"C:\ezstream\data", cfg.DocumentRoot);
        Assert.Equal(10, cfg.SegmentMinutes);
        Assert.Equal(60, cfg.DisuseTermDays);   // 약 2달
        Assert.Empty(cfg.Sources);
    }

    [Fact]
    public void SegmentMillisHasFloorAndConverts()
    {
        Assert.Equal(10 * 60 * 1000L, new RecorderConfig { SegmentMinutes = 10 }.SegmentMillis);
        Assert.Equal(60 * 1000L, new RecorderConfig { SegmentMinutes = 1 }.SegmentMillis);
        // 0분이라도 최소 5초 하한
        Assert.Equal(5000L, new RecorderConfig { SegmentMinutes = 0 }.SegmentMillis);
    }

    [Fact]
    public void JsonRoundTripPreservesValues()
    {
        var cfg = new RecorderConfig
        {
            DocumentRoot = @"D:\rec",
            SegmentMinutes = 5,
            DisuseTermDays = 90,
            FfmpegLogLevel = "info",
            UdpNotificationIp = "192.0.2.10",
            UdpNotificationPort = 5010,
            Sources =
            {
                new SourceConfig { Url = new Uri("rtsp://cam/live1"), Path = "live1", FilePrefix = "a" },
                new SourceConfig { Url = new Uri("rtsp://cam/live2"), Path = "live2", FilePrefix = "b" },
            },
        };

        var restored = RecorderConfig.FromJson(cfg.ToJson());

        Assert.Equal(cfg.DocumentRoot, restored.DocumentRoot);
        Assert.Equal(cfg.SegmentMinutes, restored.SegmentMinutes);
        Assert.Equal(cfg.DisuseTermDays, restored.DisuseTermDays);
        Assert.Equal(cfg.FfmpegLogLevel, restored.FfmpegLogLevel);
        Assert.Equal(cfg.UdpNotificationIp, restored.UdpNotificationIp);
        Assert.Equal(cfg.UdpNotificationPort, restored.UdpNotificationPort);
        Assert.Equal(2, restored.Sources.Count);
        Assert.Equal(new Uri("rtsp://cam/live2"), restored.Sources[1].Url);
        Assert.Equal("b", restored.Sources[1].FilePrefix);
    }

    [Fact]
    public void FromJsonIsCaseInsensitive()
    {
        var cfg = RecorderConfig.FromJson("{ \"DOCUMENTROOT\": \"X\", \"SegmentMinutes\": 3 }");
        Assert.Equal("X", cfg.DocumentRoot);
        Assert.Equal(3, cfg.SegmentMinutes);
    }

    [Fact]
    public void CloneIsDeepAndIndependent()
    {
        var cfg = new RecorderConfig { SegmentMinutes = 7 };
        cfg.Sources.Add(new SourceConfig { Url = new Uri("rtsp://a"), Path = "a" });

        var clone = cfg.Clone();
        clone.SegmentMinutes = 99;
        clone.Sources[0].Url = new Uri("rtsp://changed");
        clone.Sources.Add(new SourceConfig { Url = new Uri("rtsp://b") });

        Assert.Equal(7, cfg.SegmentMinutes);          // 원본 불변
        Assert.Single(cfg.Sources);
        Assert.Equal(new Uri("rtsp://a"), cfg.Sources[0].Url);
    }

    [Theory]
    [InlineData("", "default")]
    [InlineData("  ", "default")]
    [InlineData("/live1/", "live1")]
    [InlineData("\\cam\\", "cam")]
    public void SourceConfigSafePathNormalizes(string input, string expected)
        => Assert.Equal(expected, new SourceConfig { Path = input }.SafePath);

    [Theory]
    [InlineData("", "video")]
    [InlineData("  ", "video")]
    [InlineData("cam1", "cam1")]
    public void SourceConfigSafePrefixDefaults(string input, string expected)
        => Assert.Equal(expected, new SourceConfig { FilePrefix = input }.SafePrefix);

    [Fact]
    public void LoadOrCreateWritesDefaultWhenMissingThenReads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezs_test_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "config.json");
        try
        {
            var created = RecorderConfig.LoadOrCreate(path);
            Assert.True(File.Exists(path));           // 없으면 만들어 저장
            Assert.Equal(10, created.SegmentMinutes);

            File.WriteAllText(path, "{ \"segmentMinutes\": 42 }");
            var reread = RecorderConfig.LoadOrCreate(path);
            Assert.Equal(42, reread.SegmentMinutes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        }
    }

    [Fact]
    public void SaveIsAtomicAndReadable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ezs_test_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "config.json");
        try
        {
            var cfg = new RecorderConfig { SegmentMinutes = 15 };
            cfg.Save(path);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));   // 임시 파일 정리됨
            Assert.Equal(15, RecorderConfig.FromJson(File.ReadAllText(path)).SegmentMinutes);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
        }
    }
}
