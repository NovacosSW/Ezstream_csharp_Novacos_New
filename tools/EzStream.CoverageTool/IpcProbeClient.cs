using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzStream.CoverageTool;

internal static class IpcProbeClient
{
    private const string PipeName = "ezstream-recorder";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> SendRawAsync(string requestLine, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestLine);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        var client = new NamedPipeClientStream(
            ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await using (client.ConfigureAwait(false))
        {
            await client.ConnectAsync(5000, timeout.Token).ConfigureAwait(false);

            var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
            };
            await using (writer.ConfigureAwait(false))
            {
                using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
                await writer.WriteLineAsync(requestLine.AsMemory(), timeout.Token).ConfigureAwait(false);
                return await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) ?? string.Empty;
            }
        }
    }

    public static async Task<JsonObject> SendCommandAsync(
        string command,
        CancellationToken cancellationToken,
        int? minutes = null,
        string? configJson = null)
    {
        var request = new JsonObject
        {
            ["command"] = command,
        };
        if (minutes.HasValue)
            request["minutes"] = minutes.Value;
        if (configJson is not null)
            request["configJson"] = configJson;

        var responseLine = await SendRawAsync(
            request.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(responseLine)?.AsObject()
            ?? throw new InvalidDataException("서비스 응답이 JSON 형식이 아닙니다.");
    }

    public static async Task<string> GetConfigJsonAsync(CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync("GET_CONFIG", cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(response);
        return response["configJson"]?.GetValue<string>()
            ?? throw new InvalidDataException("서비스 응답에 configJson이 없습니다.");
    }

    public static async Task SetConfigJsonAsync(string configJson, CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync(
            "SET_CONFIG", cancellationToken, configJson: configJson).ConfigureAwait(false);
        EnsureSucceeded(response);
    }

    public static Task<JsonObject> GetStatusAsync(CancellationToken cancellationToken)
        => SendCommandAsync("GET_STATUS", cancellationToken);

    public static async Task RunRetentionAsync(CancellationToken cancellationToken)
    {
        var response = await SendCommandAsync("RUN_RETENTION", cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(response);
    }

    private static void EnsureSucceeded(JsonObject response)
    {
        if (response["ok"]?.GetValue<bool>() == true)
            return;

        var message = response["message"]?.GetValue<string>() ?? "알 수 없는 서비스 오류";
        throw new InvalidOperationException(message);
    }
}
