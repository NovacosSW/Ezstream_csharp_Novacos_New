using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using EzStream.Core;
using EzStream.Core.Config;
using EzStream.Core.Ipc;
using EzStream.Core.Recording;

using Microsoft.Extensions.Logging;

namespace EzStream.Service;

/// <summary>
/// Named Pipe 서버. 트레이(사용자 세션)로부터 상태 조회/설정 변경 요청을 받는다.
/// LocalSystem 서비스가 만든 파이프에 일반 사용자가 접속할 수 있도록 ACL을 설정한다.
/// </summary>
internal sealed class PipeServer ( RecorderEngine engine , string configPath , ILogger logger ) : IDisposable
    {
    private readonly RecorderEngine _engine = engine;
    private readonly string _configPath = configPath;
    private readonly ILogger _logger = logger;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var server = CreateServer();
            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                ServiceLog.PipeAcceptError(_logger, ex);
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        // Authenticated Users에게 읽기/쓰기 허용 → 사용자 세션 트레이가 접속 가능
        var security = new PipeSecurity();
        var authUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        security.AddAccessRule(new PipeAccessRule(authUsers,
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        security.AddAccessRule(new PipeAccessRule(system,
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            AppPaths.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line)) return;

        IpcResponse response;
        try
        {
            response = Handle(line);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            ServiceLog.IpcError(_logger, ex);
            response = new IpcResponse { Ok = false, Message = ex.Message };
        }
        await writer.WriteLineAsync(response.Serialize().AsMemory(), ct).ConfigureAwait(false);
    }

    private IpcResponse Handle(string line)
    {
        var req = IpcRequest.Parse(line);
        if (req == null)
            return new IpcResponse { Ok = false, Message = "Invalid request" };

        switch (req.Command)
        {
            case IpcCommands.GetStatus:
                return new IpcResponse { Ok = true, Status = _engine.GetStatus() };

            case IpcCommands.GetConfig:
                return new IpcResponse { Ok = true, ConfigJson = _engine.CurrentConfig.ToJson() };

            case IpcCommands.RunRetention:
                return _engine.RunRetentionNow()
                    ? new IpcResponse { Ok = true, Message = "retention completed" }
                    : new IpcResponse { Ok = false, Message = "service is not started" };

            case IpcCommands.SetInterval:
            {
                if (req.Minutes is not int m || m < 1)
                    return new IpcResponse { Ok = false, Message = "minutes must be >= 1" };
                var cfg = _engine.CurrentConfig;
                cfg.SegmentMinutes = m;
                Apply(cfg);
                return new IpcResponse { Ok = true, Message = $"interval set to {m} min" };
            }

            case IpcCommands.SetConfig:
            {
                if (string.IsNullOrWhiteSpace(req.ConfigJson))
                    return new IpcResponse { Ok = false, Message = "configJson required" };
                RecorderConfig cfg;
                try { cfg = RecorderConfig.FromJson(req.ConfigJson); }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return new IpcResponse { Ok = false, Message = "parse error: " + ex.Message }; }
                Apply(cfg);
                return new IpcResponse { Ok = true, Message = "config applied" };
            }

            default:
                return new IpcResponse { Ok = false, Message = $"unknown command: {req.Command}" };
        }
    }

    /// <summary>서비스가 설정 파일을 소유·저장하고 엔진에 실시간 반영한다.</summary>
    private void Apply(RecorderConfig cfg)
    {
        try { cfg.Save(_configPath); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { ServiceLog.CannotPersistConfig(_logger, ex); }
        _engine.Reload(cfg);
    }

    public void Dispose()
    {
        Stop();
        _engine.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
    }
