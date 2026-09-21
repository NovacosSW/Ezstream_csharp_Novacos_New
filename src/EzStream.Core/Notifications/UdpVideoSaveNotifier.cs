using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace EzStream.Core.Notifications;

internal sealed class UdpVideoSaveNotifier(ILogger logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILogger _logger = logger;
    private readonly Lock _endpointLock = new();
    private IPEndPoint? _endpoint;

    public void UpdateEndpoint(string? ipAddress, int port)
    {
        IPEndPoint? endpoint = null;
        if (!string.IsNullOrWhiteSpace(ipAddress)
            && port is > 0 and <= IPEndPoint.MaxPort
            && IPAddress.TryParse(ipAddress.Trim(), out var address))
        {
            endpoint = new IPEndPoint(address, port);
        }

        lock (_endpointLock)
            _endpoint = endpoint;

        if (endpoint is not null)
        {
            NotificationLog.EndpointConfigured(_logger, endpoint);
        }
        else if (!string.IsNullOrWhiteSpace(ipAddress) || port != 0)
        {
            NotificationLog.InvalidEndpoint(_logger, ipAddress ?? string.Empty, port);
        }
    }

    public void Notify(VideoSaveNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var endpoint = GetEndpoint();
        if (endpoint is null)
            return;

        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(notification, JsonOptions);
            using var client = new UdpClient(endpoint.AddressFamily);
            client.Send(payload, endpoint);
            NotificationLog.Sent(_logger, endpoint, notification.FilePrefix, notification.Success);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            NotificationLog.SendFailed(_logger, endpoint, notification.FilePrefix, ex);
        }
    }

    private IPEndPoint? GetEndpoint()
    {
        lock (_endpointLock)
            return _endpoint;
    }
}
