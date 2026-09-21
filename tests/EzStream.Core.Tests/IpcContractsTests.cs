using EzStream.Core.Ipc;

using Xunit;

namespace EzStream.Core.Tests;

public class IpcContractsTests
{
    [Fact]
    public void RequestRoundTrip()
    {
        var req = new IpcRequest { Command = IpcCommands.SetInterval, Minutes = 12 };
        var parsed = IpcRequest.Parse(req.Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(IpcCommands.SetInterval, parsed!.Command);
        Assert.Equal(12, parsed.Minutes);
    }

    [Fact]
    public void RequestParseEmptyReturnsNull()
    {
        Assert.Null(IpcRequest.Parse(""));
        Assert.Null(IpcRequest.Parse("   "));
    }

    [Fact]
    public void ResponseWithStatusRoundTrip()
    {
        var resp = new IpcResponse
        {
            Ok = true,
            Status = new EngineStatus
            {
                DocumentRoot = @"C:\ezstream\data",
                SegmentMinutes = 10,
                Sources =
                {
                    new SourceStatus
                    {
                        Url = new Uri("rtsp://cam/live1"),
                        Path = "live1",
                        State = "PLAYING",
                        CurrentFile = @"C:\ezstream\data\live1\20260805\live1_x.mp4",
                        SegmentStartedAt = DateTimeOffset.Now,
                        RecordedBytes = 123456,
                    },
                },
            },
        };

        var parsed = IpcResponse.Parse(resp.Serialize());
        Assert.NotNull(parsed);
        Assert.True(parsed!.Ok);
        Assert.NotNull(parsed.Status);
        Assert.Equal(10, parsed.Status!.SegmentMinutes);
        Assert.Single(parsed.Status.Sources);
        Assert.Equal("PLAYING", parsed.Status.Sources[0].State);
        Assert.Equal(123456, parsed.Status.Sources[0].RecordedBytes);
    }

    [Fact]
    public void ResponseErrorSerializesMessage()
    {
        var resp = new IpcResponse { Ok = false, Message = "boom" };
        var parsed = IpcResponse.Parse(resp.Serialize());
        Assert.NotNull(parsed);
        Assert.False(parsed!.Ok);
        Assert.Equal("boom", parsed.Message);
        Assert.Null(parsed.Status);
    }

    [Fact]
    public void ResponseIsSingleLine()
    {
        // 프로토콜은 개행 구분이므로 직렬화 결과에 개행이 없어야 한다.
        var resp = new IpcResponse { Ok = true, Message = "line1" };
        Assert.DoesNotContain('\n', resp.Serialize());
        Assert.DoesNotContain('\r', resp.Serialize());
    }
}
