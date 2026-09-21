using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EzStream.CoverageTool;

internal sealed class SlowHttpFileServer : IAsyncDisposable
{
    private static readonly TimeSpan FrameDelay = TimeSpan.FromMilliseconds(100);
    private readonly string _framePath;
    private readonly int? _frameLimit;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _acceptTask;

    private SlowHttpFileServer(string framePath, int? frameLimit)
    {
        _framePath = framePath;
        _frameLimit = frameLimit;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        Url = new Uri($"http://127.0.0.1:{endpoint.Port.ToString(CultureInfo.InvariantCulture)}/stream.mjpg");
        _acceptTask = AcceptLoopAsync(_stopping.Token);
    }

    public Uri Url { get; }

    public static SlowHttpFileServer Start(string framePath, int? frameLimit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(framePath);
        return new SlowHttpFileServer(framePath, frameLimit);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _acceptTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _listener.Dispose();
        _stopping.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            await ServeAsync(client, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var network = client.GetStream();
                using var reader = new StreamReader(
                    network, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                    bufferSize: 1024, leaveOpen: true);
                await ReadRequestAsync(reader, cancellationToken).ConfigureAwait(false);
                var frame = await File.ReadAllBytesAsync(_framePath, cancellationToken).ConfigureAwait(false);
                await SendMjpegAsync(network, frame, _frameLimit, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static async Task SendMjpegAsync(
        NetworkStream network,
        byte[] frame,
        int? frameLimit,
        CancellationToken cancellationToken)
    {
        const string response = "HTTP/1.1 200 OK\r\n"
            + "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n"
            + "Cache-Control: no-cache\r\n"
            + "Connection: close\r\n\r\n";
        await network.WriteAsync(Encoding.ASCII.GetBytes(response), cancellationToken).ConfigureAwait(false);
        var partHeader = Encoding.ASCII.GetBytes(
            $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        var lineEnd = "\r\n"u8.ToArray();

        var frameCount = 0;
        while (!cancellationToken.IsCancellationRequested
            && (!frameLimit.HasValue || frameCount < frameLimit.Value))
        {
            await network.WriteAsync(partHeader, cancellationToken).ConfigureAwait(false);
            await network.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await network.WriteAsync(lineEnd, cancellationToken).ConfigureAwait(false);
            await network.FlushAsync(cancellationToken).ConfigureAwait(false);
            frameCount++;
            await Task.Delay(FrameDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReadRequestAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        while (!string.IsNullOrEmpty(
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
        }
    }
}
