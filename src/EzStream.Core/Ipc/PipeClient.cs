using System.IO.Pipes;
using System.Text;

namespace EzStream.Core.Ipc;

/// <summary>트레이가 서비스에 접속해 요청/응답을 주고받는 클라이언트.</summary>
public static class PipeClient
{
    public static async Task<IpcResponse> SendAsync(IpcRequest request, int timeoutMs = 3000, CancellationToken ct = default)
        => await SendCoreAsync(request, AppPaths.PipeName, timeoutMs, ct).ConfigureAwait(false);

    private static async Task<IpcResponse> SendCoreAsync(
        IpcRequest request,
        string pipeName,
        int timeoutMs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);

        try
        {
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
            await writer.WriteLineAsync(request.Serialize().AsMemory(), ct).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            return IpcResponse.Parse(line ?? "")
                ?? new IpcResponse { Ok = false, Message = "empty response" };
        }
        catch (IOException)
        {
            return new IpcResponse { Ok = false, Message = "empty response" };
        }
    }
}
