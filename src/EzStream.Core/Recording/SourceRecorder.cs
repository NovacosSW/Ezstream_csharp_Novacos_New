using System.Globalization;
using System.Runtime.InteropServices;

using EzStream .Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Ipc;
using EzStream.Core.Notifications;

using FFmpeg.AutoGen;

using Microsoft.Extensions.Logging;

namespace EzStream.Core.Recording;

public enum RecorderState
{
    Init,      // 최초상태
    Prepared,  // 준비상태(입력 열림)
    Playing,   // 재생상태(패킷 기록 중)
    Stopped    // 정지상태
}

/// <summary>
/// 소스 1개를 담당하는 녹화기. 전용 스레드에서 RTSP(또는 파일) 입력을 열어
/// MP4로 스트림 카피(remux)하며 N분 주기로 파일을 잘라 저장한다.
/// 원본 EzStream 클래스의 prepare/play/세그먼트 절단 로직(EzStream.cpp:262,1356)에 대응.
/// </summary>
public sealed unsafe class SourceRecorder
{
    private readonly ILogger _logger;
    private readonly string _documentRoot;
    private readonly Action<VideoSaveNotification> _notifyVideoSave;
    private readonly Lock _stateLock = new();

    private readonly SourceConfig _source;
    private long _segmentMillis; // Interlocked로 접근
    private volatile bool _cutNow;
    private volatile bool _running;
    private Thread? _thread;

    // 상태(스냅샷용, _stateLock 보호)
    private RecorderState _state = RecorderState.Init;
    private string? _currentFile;
    private DateTimeOffset? _segmentStartedAt;
    private string? _lastError;
    private long _recordedBytes;
    private string? _segmentWriteError;

    // 네이티브 컨텍스트
    private AVFormatContext* _ic;
    private AVFormatContext* _oc;
    private int[] _outIndexByInput = [ ];  // 입력 스트림 index -> 출력 스트림 index(-1이면 미매핑)
    private int _videoInputIndex = -1;
    private bool _headerWritten;        // write_header 성공 여부(av_write_trailer 가드)
    private long _segmentStartUs;       // 이 세그먼트의 기준 타임스탬프(AV_TIME_BASE 단위). NOPTS면 미설정.
    private const long NoPts = long.MinValue;
    private static readonly AVRational TimeBaseQ = new() { num = 1, den = ffmpeg.AV_TIME_BASE };

    public SourceRecorder(SourceConfig source, RecorderConfig cfg, ILogger logger)
        : this(source, cfg, logger, static _ => { })
    {
    }

    internal SourceRecorder(
        SourceConfig source,
        RecorderConfig cfg,
        ILogger logger,
        Action<VideoSaveNotification> notifyVideoSave)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(notifyVideoSave);
        _source = source;
        _documentRoot = cfg.DocumentRoot;
        Interlocked.Exchange(ref _segmentMillis, cfg.SegmentMillis);
        _logger = logger;
        _notifyVideoSave = notifyVideoSave;
    }

    public void Start()
    {
        if (_thread != null) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = $"rec-{_source.SafePath}" };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(10));
        _thread = null;
    }

    /// <summary>세그먼트 주기(분) 실시간 변경. 즉시 현재 파일을 정리하고 새 주기로 시작한다.</summary>
    public void UpdateSegmentMinutes(long millis)
    {
        if (Interlocked.Read(ref _segmentMillis) == millis) return;
        Interlocked.Exchange(ref _segmentMillis, millis);
        _cutNow = true; // 다음 키프레임에서 즉시 절단
        CoreLog.RecorderIntervalChanged(_logger, _source.SafePath, millis);
    }

    public SourceStatus Snapshot()
    {
        lock (_stateLock)
        {
            return new SourceStatus
            {
                Url = _source.Url,
                Path = _source.SafePath,
                State = _state.ToString().ToUpperInvariant(),
                CurrentFile = _currentFile,
                SegmentStartedAt = _segmentStartedAt,
                LastError = _lastError,
                RecordedBytes = _recordedBytes,
            };
        }
    }

    private void SetState(RecorderState s)
    {
        lock (_stateLock) _state = s;
    }

    private void SetError(string? err)
    {
        lock (_stateLock) _lastError = err;
    }

    private void Run()
    {
        CoreLog.RecorderStarted(_logger, _source.SafePath, _source.Url);
        while (_running)
        {
            try
            {
                RunOnce();
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                CoreLog.RecorderLoopError(_logger, _source.SafePath, ex);
                SetError(ex.Message);
            }
            finally
            {
                CloseOutput(finalize: true);
                CloseInput();
            }

            if (_running)
            {
                SetState(RecorderState.Init);
                Thread.Sleep(2000); // 재접속 백오프
            }
        }
        SetState(RecorderState.Stopped);
        CoreLog.RecorderStopped(_logger, _source.SafePath);
    }

    private void RunOnce()
    {
        if (_source.Url is null)
        {
            SetError("Source url is empty");
            _running = false;
            return;
        }

        if (!OpenInput()) return;
        SetState(RecorderState.Prepared);

        if (!OpenNewSegment()) return;
        SetState(RecorderState.Playing);

        var pkt = ffmpeg.av_packet_alloc();
        try
        {
            var segStart = DateTime.UtcNow;
            while (_running)
            {
                int ret = ffmpeg.av_read_frame(_ic, pkt);
                if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }
                if (ret < 0)
                {
                    // EOF / 타임아웃 / 네트워크 오류 → 재접속
                    CoreLog.ReadEnded(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
                    SetError(FfmpegLoader.ErrorString(ret));
                    ffmpeg.av_packet_unref(pkt);
                    return;
                }

                int inIdx = pkt->stream_index;
                int outIdx = (inIdx >= 0 && inIdx < _outIndexByInput.Length) ? _outIndexByInput[inIdx] : -1;
                if (outIdx < 0)
                {
                    ffmpeg.av_packet_unref(pkt);
                    continue;
                }

                bool isVideo = inIdx == _videoInputIndex;
                bool isKey = (pkt->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

                // ---- 세그먼트 절단 판단 ----
                bool intervalElapsed = (DateTime.UtcNow - segStart).TotalMilliseconds >= Interlocked.Read(ref _segmentMillis);
                bool wantCut = intervalElapsed || _cutNow;
                bool canCut = _videoInputIndex < 0 || (isVideo && isKey); // 비디오가 있으면 키프레임에서만 절단
                if (wantCut && canCut)
                {
                    CoreLog.CuttingSegment(_logger, _source.SafePath,
                        (long)(DateTime.UtcNow - segStart).TotalMilliseconds, _cutNow);
                    _cutNow = false;
                    CloseOutput(finalize: true);
                    if (!OpenNewSegment())
                    {
                        ffmpeg.av_packet_unref(pkt);
                        return;
                    }
                    segStart = DateTime.UtcNow;
                }

                WritePacket(pkt, inIdx, outIdx);
                ffmpeg.av_packet_unref(pkt);
            }
        }
        finally
        {
            var p = pkt;
            ffmpeg.av_packet_free(&p);
        }
    }

    private bool OpenInput()
    {
        AVDictionary* opts = null;
        try
        {
            string url = _source.Url!.OriginalString;
            if (url.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
            {
                string prot = url.StartsWith("rtspu", StringComparison.OrdinalIgnoreCase) ? "udp" : "tcp";
                ffmpeg.av_dict_set(&opts, "rtsp_transport", prot, 0);
            }
            // 원본 EzStream::prepare의 입력 옵션 이식
            ffmpeg.av_dict_set(&opts, "buffer_size", "52428800", 0);
            ffmpeg.av_dict_set(&opts, "max_delay", "500000", 0);
            ffmpeg.av_dict_set(&opts, "stimeout", "20000000", 0); // 20s (microseconds)
            ffmpeg.av_dict_set(&opts, "rtbufsize", "52428800", 0);
            // 입력 단계에서는 모든 미디어 형식을 허용한다. 출력 스트림 생성 시 비디오만
            // 명시적으로 매핑하므로 오디오는 MP4에 기록되지 않으며, 오디오 전용 입력은
            // 아래의 "No recordable streams" 오류 처리 경로로 정상 진입한다.

            AVFormatContext* ic = ffmpeg.avformat_alloc_context();
            CoreLog.OpeningInput(_logger, _source.SafePath, url);
            int ret = ffmpeg.avformat_open_input(&ic, url, null, &opts);
            if (ret != 0)
            {
                _ic = null;
                SetError($"open_input: {FfmpegLoader.ErrorString(ret)}");
                CoreLog.CannotOpenInput(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
                return false;
            }
            _ic = ic;

            ret = ffmpeg.avformat_find_stream_info(_ic, null);
            if (ret < 0)
            {
                SetError($"find_stream_info: {FfmpegLoader.ErrorString(ret)}");
                CoreLog.CannotFindStreamInfo(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
                return false;
            }

            _videoInputIndex = -1;
            for (int i = 0; i < (int)_ic->nb_streams; i++)
            {
                var t = _ic->streams[i]->codecpar->codec_type;
                if (t == AVMediaType.AVMEDIA_TYPE_VIDEO && _videoInputIndex < 0)
                    _videoInputIndex = i;
            }
            if (_videoInputIndex < 0)
                CoreLog.NoVideoStream(_logger, _source.SafePath);

            SetError(null);
            return true;
        }
        finally
        {
            ffmpeg.av_dict_free(&opts);
        }
    }

    private void CloseInput()
    {
        if (_ic != null)
        {
            var ic = _ic;
            ffmpeg.avformat_close_input(&ic);
            _ic = null;
        }
        _videoInputIndex = -1;
    }

    private bool OpenNewSegment()
    {
        var startedAt = DateTimeOffset.Now;
        string filePath = _documentRoot;
        try
        {
            string date = startedAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string datetime = startedAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            string dir = Path.Combine(_documentRoot, _source.SafePath, date);
            filePath = Path.Combine(dir, $"{_source.SafePrefix}_{datetime}.mp4");
            Directory.CreateDirectory(dir);
            return OpenNewSegmentCore(filePath, startedAt);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            string error = "prepare_output: " + ex.Message;
            SetError(error);
            CoreLog.CannotOpenOutput(_logger, _source.SafePath, filePath, ex.Message);
            ReportSaveResult(startedAt, false, error, 0);
            return false;
        }
    }

    private bool OpenNewSegmentCore(string filePath, DateTimeOffset startedAt)
    {
        _headerWritten = false;
        AVFormatContext* oc = null;
        int ret = ffmpeg.avformat_alloc_output_context2(&oc, null, "mp4", filePath);
        // 실패 시 oc가 남아 있을 수 있으므로 즉시 소유권을 넘겨 CloseOutput이 해제하도록 한다.
        _oc = oc;
        if (ret < 0)
        {
            string error = $"alloc_output: {FfmpegLoader.ErrorString(ret)}";
            SetError(error);
            CoreLog.CannotAllocateOutput(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
            ReportSaveResult(startedAt, false, error, 0);
            return false;
        }

        // 입력 스트림 → 출력 스트림 매핑(스트림 카피)
        _outIndexByInput = new int[_ic->nb_streams];
        for (int i = 0; i < _outIndexByInput.Length; i++) _outIndexByInput[i] = -1;

        int outIndex = 0;
        for (int i = 0; i < (int)_ic->nb_streams; i++)
        {
            var inStream = _ic->streams[i];
            var t = inStream->codecpar->codec_type;
            if (t != AVMediaType.AVMEDIA_TYPE_VIDEO)
                continue;

            AVStream* outStream = ffmpeg.avformat_new_stream(_oc, null);
            if (outStream == null)
            {
                const string error = "new_stream failed";
                SetError(error);
                ReportSaveResult(startedAt, false, error, 0);
                return false;
            }
            int cp = ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar);
            if (cp < 0)
            {
                string error = $"parameters_copy: {FfmpegLoader.ErrorString(cp)}";
                SetError(error);
                ReportSaveResult(startedAt, false, error, 0);
                return false;
            }
            outStream->codecpar->codec_tag = 0;
            _outIndexByInput[i] = outIndex++;
        }

        if (outIndex == 0)
        {
            const string error = "No recordable streams";
            SetError(error);
            CoreLog.NoRecordableStreams(_logger, _source.SafePath);
            ReportSaveResult(startedAt, false, error, 0);
            return false;
        }

        ret = ffmpeg.avio_open(&_oc->pb, filePath, ffmpeg.AVIO_FLAG_WRITE);
        if (ret < 0)
        {
            string error = $"avio_open: {FfmpegLoader.ErrorString(ret)}";
            SetError(error);
            CoreLog.CannotOpenOutput(_logger, _source.SafePath, filePath, FfmpegLoader.ErrorString(ret));
            ReportSaveResult(startedAt, false, error, 0);
            return false;
        }

        AVDictionary* mopts = null;
        ffmpeg.av_dict_set(&mopts, "movflags", "+faststart", 0);
        ret = ffmpeg.avformat_write_header(_oc, &mopts);
        ffmpeg.av_dict_free(&mopts);
        if (ret < 0)
        {
            string error = $"write_header: {FfmpegLoader.ErrorString(ret)}";
            SetError(error);
            CoreLog.CannotWriteHeader(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
            ReportSaveResult(startedAt, false, error, 0);
            return false;
        }
        _headerWritten = true;

        _segmentStartUs = NoPts;
        lock (_stateLock)
        {
            _currentFile = filePath;
            _segmentStartedAt = startedAt;
            _recordedBytes = 0;
            _segmentWriteError = null;
        }
        CoreLog.RecordingTo(_logger, _source.SafePath, filePath);
        return true;
    }

    private void CloseOutput(bool finalize)
    {
        if (_oc == null) return;
        string? closeError = _segmentWriteError;
        try
        {
            // trailer는 write_header가 성공한 경우에만 호출해야 한다(그래야 moov 기록 → 재생 가능).
            if (finalize && _headerWritten && _oc->pb != null)
            {
                int ret = ffmpeg.av_write_trailer(_oc);
                if (ret < 0)
                {
                    closeError = $"write_trailer: {FfmpegLoader.ErrorString(ret)}";
                    CoreLog.WriteTrailerReturnedError(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
                }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            CoreLog.WriteTrailerFailed(_logger, _source.SafePath, ex);
            closeError = "write_trailer: " + ex.Message;
        }
        if (_oc->pb != null)
        {
            var pb = _oc->pb;
            int ret = ffmpeg.avio_closep(&pb);
            if (ret < 0 && closeError is null)
                closeError = $"avio_close: {FfmpegLoader.ErrorString(ret)}";
        }
        var oc = _oc;
        ffmpeg.avformat_free_context(oc);
        _oc = null;
        _headerWritten = false;
        ReportClosedSegment(finalize, closeError);
    }

    private void ReportClosedSegment(bool finalize, string? closeError)
    {
        string? filePath;
        DateTimeOffset? startedAt;
        lock (_stateLock)
        {
            filePath = _currentFile;
            startedAt = _segmentStartedAt;
            _currentFile = null;
            _segmentStartedAt = null;
            _segmentWriteError = null;
        }

        if (filePath is null || startedAt is null)
            return;

        long fileSize = GetFileSize(filePath, out string? fileError);
        closeError ??= fileError;
        bool success = finalize && closeError is null && fileSize > 0;
        string? error = success ? null : closeError ?? "Output file is empty or missing";
        ReportSaveResult(startedAt.Value, success, error, fileSize);
    }

    private static long GetFileSize(string filePath, out string? error)
    {
        error = null;
        try
        {
            return File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            error = "file_info: " + ex.Message;
            return 0;
        }
    }

    private void ReportSaveResult(
        DateTimeOffset startedAt,
        bool success,
        string? error,
        long fileSize)
    {
        var endedAt = DateTimeOffset.Now;
        _notifyVideoSave(new VideoSaveNotification
        {
            DurationMilliseconds = Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
            FilePrefix = _source.SafePrefix,
            FileSizeBytes = fileSize,
            Success = success,
            Error = error,
        });
    }

    private void WritePacket(AVPacket* pkt, int inIdx, int outIdx)
    {
        AVStream* inStream = _ic->streams[inIdx];
        AVStream* outStream = _oc->streams[outIdx];

        if (!PreparePacketForOutput(
            pkt, inStream->time_base, outStream->time_base, outIdx, ref _segmentStartUs))
            return;

        int size = pkt->size;
        int ret = ffmpeg.av_interleaved_write_frame(_oc, pkt);
        if (ret < 0)
        {
            string error = $"write_frame: {FfmpegLoader.ErrorString(ret)}";
            CoreLog.WriteFrameFailed(_logger, _source.SafePath, FfmpegLoader.ErrorString(ret));
            lock (_stateLock) _segmentWriteError ??= error;
            return;
        }
        lock (_stateLock) _recordedBytes += size;
    }

    internal static bool PreparePacketForOutput(
        AVPacket* packet,
        AVRational inputTimeBase,
        AVRational outputTimeBase,
        int outputIndex,
        ref long segmentStartUs)
    {
        // pts/dts가 모두 없는 패킷은 mp4 muxer가 거부(EINVAL)하므로 건너뛴다.
        if (packet->dts == ffmpeg.AV_NOPTS_VALUE && packet->pts == ffmpeg.AV_NOPTS_VALUE)
            return false;

        // 세그먼트 기준 타임스탬프 확정(모든 스트림에 동일 오프셋 적용 → A/V 동기 유지)
        long referenceTimestamp = packet->dts != ffmpeg.AV_NOPTS_VALUE ? packet->dts : packet->pts;
        if (segmentStartUs == NoPts && referenceTimestamp != ffmpeg.AV_NOPTS_VALUE)
            segmentStartUs = ffmpeg.av_rescale_q(referenceTimestamp, inputTimeBase, TimeBaseQ);
        if (segmentStartUs != NoPts)
        {
            long offset = ffmpeg.av_rescale_q(segmentStartUs, TimeBaseQ, inputTimeBase);
            if (packet->pts != ffmpeg.AV_NOPTS_VALUE) packet->pts -= offset;
            if (packet->dts != ffmpeg.AV_NOPTS_VALUE) packet->dts -= offset;
        }

        packet->stream_index = outputIndex;
        ffmpeg.av_packet_rescale_ts(packet, inputTimeBase, outputTimeBase);
        packet->pos = -1;
        if (packet->dts != ffmpeg.AV_NOPTS_VALUE && packet->dts < 0) packet->dts = 0;
        if (packet->pts != ffmpeg.AV_NOPTS_VALUE && packet->pts < 0) packet->pts = 0;
        if (packet->pts != ffmpeg.AV_NOPTS_VALUE && packet->dts != ffmpeg.AV_NOPTS_VALUE
            && packet->pts < packet->dts)
        {
            packet->pts = packet->dts;
        }
        return true;
    }
}
