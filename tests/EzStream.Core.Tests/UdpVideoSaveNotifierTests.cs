using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using EzStream.Core.Notifications;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace EzStream.Core.Tests;

public sealed class UdpVideoSaveNotifierTests
{
    [Fact]
    public async Task SendsExpectedJsonPacket()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        var notifier = new UdpVideoSaveNotifier(NullLogger.Instance);
        notifier.UpdateEndpoint("127.0.0.1", port);

        notifier.Notify(new VideoSaveNotification
        {
            DurationMilliseconds = 300000,
            FilePrefix = "eo",
            FileSizeBytes = 12345,
            Success = true,
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        UdpReceiveResult received = await receiver.ReceiveAsync(timeout.Token);
        using JsonDocument json = JsonDocument.Parse(received.Buffer);
        JsonElement root = json.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("videoSaveResult", root.GetProperty("eventType").GetString());
        Assert.Equal("eo", root.GetProperty("filePrefix").GetString());
        Assert.False(root.TryGetProperty("videoStartedAt", out _));
        Assert.False(root.TryGetProperty("videoEndedAt", out _));
        Assert.False(root.TryGetProperty("filePath", out _));
        Assert.Equal(300000, root.GetProperty("durationMilliseconds").GetInt64());
        Assert.Equal(12345, root.GetProperty("fileSizeBytes").GetInt64());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
    }
}
