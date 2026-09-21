using EzStream .Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Ipc;
using EzStream.Core.Notifications;
using EzStream.Core.Retention;
using Microsoft.Extensions.Logging;

namespace EzStream.Core.Recording;

/// <summary>
/// 전체 녹화 오케스트레이션. 소스별 SourceRecorder를 생성/기동/정지하고,
/// 설정 변경을 실시간으로 반영한다. 원본 EzStreamExport.cpp의 메인 루프 대응.
/// </summary>
public sealed class RecorderEngine ( RecorderConfig config , ILogger logger ) : IDisposable
{
    private readonly ILogger _logger = logger;
    private readonly UdpVideoSaveNotifier _udpNotifier = new(logger);
    private readonly Lock _lock = new();
    private readonly List<SourceRecorder> _recorders = [];
    private RetentionCleaner? _retention;
    private RecorderConfig _config = config;
    private bool _started;

    public void Start()
    {
        lock (_lock)
        {
            if (_started) return;
            _udpNotifier.UpdateEndpoint(_config.UdpNotificationIp, _config.UdpNotificationPort);
            FfmpegLoader.Initialize(AppPaths.FfmpegDir, _config.FfmpegLogLevel, _logger);
            BuildRecorders_NoLock();
            _retention = new RetentionCleaner(() => _config, _logger);
            _retention.Start();
            _started = true;
            EngineLog.Started(_logger, _recorders.Count);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (!_started) return;
            _retention?.Dispose();
            _retention = null;
            foreach (var r in _recorders) r.Stop();
            _recorders.Clear();
            _started = false;
            EngineLog.Stopped(_logger);
        }
    }

    private void BuildRecorders_NoLock()
    {
        foreach (var r in _recorders) r.Stop();
        _recorders.Clear();
        foreach (var src in _config.Sources)
        {
            if (src.Url is null) continue;
            var rec = new SourceRecorder(src, _config, _logger, _udpNotifier.Notify);
            _recorders.Add(rec);
            rec.Start();
            Thread.Sleep(50); // 원본과 동일하게 동시 접속 부하 완화
        }
    }

    /// <summary>
    /// 새 설정을 반영한다.
    /// - 소스/저장경로가 바뀌면 녹화기를 재구성.
    /// - 주기(분)만 바뀌면 재시작 없이 실시간 반영(현재 파일 정리 후 새 주기로).
    /// </summary>
    public void Reload(RecorderConfig newConfig)
    {
        ArgumentNullException.ThrowIfNull(newConfig);
        lock (_lock)
        {
            bool structuralChange =
                RecorderConfigComparer.RequiresRecorderRebuild(newConfig, _config);

            bool intervalChange = newConfig.SegmentMinutes != _config.SegmentMinutes;

            _config = newConfig;
            _udpNotifier.UpdateEndpoint(_config.UdpNotificationIp, _config.UdpNotificationPort);

            if (!_started)
                return;

            if (structuralChange)
            {
                EngineLog.ConfigRebuild(_logger);
                FfmpegLoader.Initialize(AppPaths.FfmpegDir, _config.FfmpegLogLevel, _logger); // 최초 1회만 적용
                BuildRecorders_NoLock();
            }
            else if (intervalChange)
            {
                EngineLog.IntervalChanged(_logger, _config.SegmentMinutes);
                foreach (var r in _recorders)
                    r.UpdateSegmentMinutes(_config.SegmentMillis);
            }
        }
    }

    public EngineStatus GetStatus()
    {
        lock (_lock)
        {
            var status = new EngineStatus
            {
                DocumentRoot = _config.DocumentRoot,
                SegmentMinutes = _config.SegmentMinutes,
            };
            foreach (var recorder in _recorders)
                status.Sources.Add(recorder.Snapshot());
            return status;
        }
    }

    /// <summary>서비스가 실행 중이면 영상 및 로그 보존 정리를 즉시 수행한다.</summary>
    /// <returns>정리가 실행되었으면 <see langword="true"/>.</returns>
    public bool RunRetentionNow()
    {
        lock (_lock)
        {
            if (!_started || _retention is null)
                return false;
            _retention.RunNow();
            return true;
        }
    }

    public RecorderConfig CurrentConfig
    {
        get { lock (_lock) return _config.Clone(); }
    }

    public void Dispose() => Stop();
}
