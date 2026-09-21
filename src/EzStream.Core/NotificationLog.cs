using System.Net;

using Microsoft.Extensions.Logging;

namespace EzStream.Core;

internal static partial class NotificationLog
{
    [LoggerMessage(50, LogLevel.Information, "UDP notification sent to {Endpoint}. prefix={Prefix}, success={Success}")]
    public static partial void Sent(ILogger logger, IPEndPoint endpoint, string prefix, bool success);

    [LoggerMessage(51, LogLevel.Warning, "Cannot send UDP notification to {Endpoint}. prefix={Prefix}")]
    public static partial void SendFailed(ILogger logger, IPEndPoint endpoint, string prefix, Exception exception);

    [LoggerMessage(52, LogLevel.Warning, "UDP notification disabled because the endpoint is invalid. ip={Ip}, port={Port}")]
    public static partial void InvalidEndpoint(ILogger logger, string ip, int port);

    [LoggerMessage(53, LogLevel.Information, "UDP notification endpoint configured: {Endpoint}")]
    public static partial void EndpointConfigured(ILogger logger, IPEndPoint endpoint);
}
