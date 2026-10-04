using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EzStream.CoverageTool;

internal sealed class CoverageTestRunner
{
    private const long RotationThreshold = 20L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly ConfigBackupStore _backup;
    private readonly string _workDirectory;
    private readonly string _sharedTestDirectory;
    private readonly string _configPath;
    private readonly string _logDirectory;

    public CoverageTestRunner()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _workDirectory = Path.Combine(localData, "EzStreamCoverageTool");
        _backup = new ConfigBackupStore(_workDirectory);
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dataDirectory = Path.Combine(commonData, "EzStream");
        _sharedTestDirectory = Path.Combine(dataDirectory, "coverage-tests");
        _configPath = Path.Combine(dataDirectory, "config.json");
        _logDirectory = Path.Combine(dataDirectory, "logs");
    }

    public bool HasPendingBackup => _backup.HasPendingBackup;

    public static async Task<TestResult> RunIpcErrorTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string[] requests =
        [
            "null",
            "not-json",
            "{}",
            "{\"command\":\"UNKNOWN\"}",
            "{\"command\":\"SET_INTERVAL\"}",
            "{\"command\":\"SET_INTERVAL\",\"minutes\":0}",
            "{\"command\":\"SET_CONFIG\",\"configJson\":\"{\"}",
        ];

        var received = 0;
        foreach (var request in requests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"전송: {request}");
            var response = await IpcProbeClient.SendRawAsync(request, cancellationToken).ConfigureAwait(false);
            progress.Report($"응답: {response}");
            if (!string.IsNullOrWhiteSpace(response))
                received++;
        }

        return new TestResult(received == requests.Length,
            $"비정상 요청 {requests.Length}건 전송, 응답 {received}건 수신");
    }

    public async Task<TestResult> RunExtendedIpcTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var originalConfig = ParseConfig(originalJson);
        var originalMinutes = GetIntOrDefault(originalConfig, "segmentMinutes", 10);
        var changedMinutes = originalMinutes == 1 ? 2 : 1;
        _backup.Save(originalJson);
        try
        {
            progress.Report("설정 내용이 누락된 요청을 전송합니다.");
            var missingConfig = await IpcProbeClient.SendCommandAsync("SET_CONFIG", cancellationToken)
                .ConfigureAwait(false);

            progress.Report($"저장 주기를 {changedMinutes}분으로 변경해 실시간 갱신 분기를 실행합니다.");
            var intervalResponse = await IpcProbeClient.SendCommandAsync(
                "SET_INTERVAL", cancellationToken, minutes: changedMinutes).ConfigureAwait(false);

            progress.Report("설정 파일을 잠근 상태에서 설정 저장 실패 분기를 실행합니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            using (var configLock = new FileStream(
                _configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = configLock.Length;
                await IpcProbeClient.SetConfigJsonAsync(originalJson, cancellationToken).ConfigureAwait(false);
            }

            progress.Report("빈 요청을 전송해 응답 없이 연결을 닫는 분기를 실행합니다.");
            var emptyHandled = await SendEmptyRequestAsync(cancellationToken).ConfigureAwait(false);

            var missingRejected = missingConfig["ok"]?.GetValue<bool>() == false;
            var intervalAccepted = intervalResponse["ok"]?.GetValue<bool>() == true;
            return new TestResult(
                emptyHandled && missingRejected && intervalAccepted,
                "빈 요청, 설정 누락, 주기 실시간 변경 및 설정 파일 저장 실패 분기를 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
        }
    }

    private static async Task<bool> SendEmptyRequestAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await IpcProbeClient.SendRawAsync(string.Empty, cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrEmpty(response);
        }
        catch (IOException)
        {
            // 서버는 빈 요청에 응답하지 않고 연결을 종료하는 것이 정상 동작이다.
            return true;
        }
    }

    public static async Task<TestResult> RunConfigBranchTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        return await HarnessCoverageRunner.RunAsync(
            "CONFIG_BRANCHES", "설정 생성·손상·저장 실패", progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TestResult> RunEngineRestartTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var idleConfig = ParseConfig(originalJson);
        GetSources(idleConfig).Clear();
        _backup.Save(originalJson);
        try
        {
            progress.Report("소스를 잠시 정지하고 녹화 엔진의 정상 종료·재시작 분기를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(idleConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            return new TestResult(true, "설정 실시간 재적용으로 녹화 엔진의 정상 종료·재시작을 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
        }
    }

    public async Task<TestResult> RunLogWriteFailureTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Directory.CreateDirectory(_logDirectory);
        var activeLog = Path.Combine(_logDirectory, $"ezstream-{DateTime.Now:yyyyMMdd}.log");
        if (!File.Exists(activeLog))
            await File.WriteAllTextAsync(activeLog, string.Empty, cancellationToken).ConfigureAwait(false);

        progress.Report("현재 서비스 로그를 잠그고 오류 로그 기록을 요청합니다.");
        string lockedResponse;
        using (var logLock = new FileStream(activeLog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _ = logLock.Length;
            lockedResponse = await IpcProbeClient.SendRawAsync("not-json", cancellationToken)
                .ConfigureAwait(false);
        }

        progress.Report("잠금을 해제한 뒤 정상 로그 기록도 실행합니다.");
        var recoveredResponse = await IpcProbeClient.SendRawAsync("not-json", cancellationToken)
            .ConfigureAwait(false);
        return new TestResult(
            !string.IsNullOrWhiteSpace(lockedResponse) && !string.IsNullOrWhiteSpace(recoveredResponse),
            "로그 기록 실패와 잠금 해제 후 정상 기록 분기를 실행했습니다.");
    }

    public async Task<TestResult> RunLogRotationFailureTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        Directory.CreateDirectory(_logDirectory);
        var activeLog = Path.Combine(_logDirectory,
            $"ezstream-{DateTime.Now:yyyyMMdd}.log");
        progress.Report("현재 로그를 20MB 회전 기준까지 확장합니다.");
        await ExpandLogAsync(activeLog, cancellationToken).ConfigureAwait(false);

        var collision = await CreateTimedCollisionAsync(progress, cancellationToken).ConfigureAwait(false);
        try
        {
            progress.Report("동일 이름의 회전 파일이 있는 상태에서 서비스 로그를 발생시킵니다.");
            var lengthBeforeRequest = new FileInfo(activeLog).Length;
            _ = await IpcProbeClient.SendRawAsync("not-json", cancellationToken).ConfigureAwait(false);
            var collisionRemained = File.Exists(collision) && new FileInfo(collision).Length == 0;
            if (!collisionRemained)
                return new TestResult(false, "회전 충돌 파일이 유지되지 않아 실패 분기 실행을 확인하지 못했습니다.");
            if (new FileInfo(activeLog).Length <= lengthBeforeRequest)
                return new TestResult(false, "서비스 로그 기록 증가를 확인하지 못했습니다.");
        }
        finally
        {
            DeleteEmptyFile(collision);
        }

        await WaitForNextSecondAsync(cancellationToken).ConfigureAwait(false);
        progress.Report("충돌 파일을 제거하고 정상 회전을 한 번 실행합니다.");
        _ = await IpcProbeClient.SendRawAsync("not-json", cancellationToken).ConfigureAwait(false);
        return new TestResult(true, "로그 회전 파일 이동 실패 조건과 이후 정상 회전을 실행했습니다.");
    }

    public async Task<TestResult> RunOutputPathFailureTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var testConfig = ParseConfig(originalJson);
        var sources = GetSources(testConfig);
        Directory.CreateDirectory(_workDirectory);
        Directory.CreateDirectory(_sharedTestDirectory);
        var blockingFile = Path.Combine(_workDirectory, "blocked-output-root");
        var videoPath = Path.Combine(_sharedTestDirectory, "output-failure-video.avi");
        var outputRoot = Path.Combine(_sharedTestDirectory, "blocked-output-files");
        DeleteFileOrDirectory(blockingFile);
        await File.WriteAllTextAsync(blockingFile,
            "이 파일은 디렉터리 생성을 막는 시험용 파일입니다.", Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        MjpegAviWriter.Create(videoPath);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = videoPath.Replace('\\', '/'),
            ["path"] = "coverage-output",
            ["filePrefix"] = "coverage_blocked_output",
        });

        _backup.Save(originalJson);
        try
        {
            testConfig["documentRoot"] = blockingFile;
            progress.Report("저장 경로를 시험용 차단 파일로 변경합니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var detected = await WaitForSourceErrorAsync(
                error => error.Contains("prepare_output", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(12), progress, cancellationToken).ConfigureAwait(false);
            progress.Report("MP4 파일 이름과 같은 디렉터리를 만들어 FFmpeg 파일 열기 실패를 실행합니다.");
            CreateBlockedOutputNames(outputRoot, "coverage-output", "coverage_blocked_output");
            testConfig["documentRoot"] = outputRoot;
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var fileOpenDetected = await WaitForSourceErrorAsync(
                error => error.Contains("avio_open", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15), progress, cancellationToken).ConfigureAwait(false);
            var succeeded = detected || fileOpenDetected;
            var executed = (detected, fileOpenDetected) switch
            {
                (true, true) => "저장 루트 준비 실패와 FFmpeg MP4 파일 열기 실패",
                (true, false) => "저장 루트 준비 실패",
                (false, true) => "FFmpeg MP4 파일 열기 실패",
                _ => "저장 경로 실패 미확인",
            };
            return new TestResult(succeeded, executed + " 분기를 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            DeleteFileOrDirectory(blockingFile);
            DeleteFile(videoPath);
            DeleteDirectory(outputRoot);
        }
    }

    public async Task<TestResult> RunAudioOnlyInputTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var testConfig = ParseConfig(originalJson);
        var audioPath = Path.Combine(_sharedTestDirectory, "audio-only-test.wav");
        Directory.CreateDirectory(_sharedTestDirectory);
        CreateSilentWaveFile(audioPath, TimeSpan.FromSeconds(10));

        var source = new JsonObject
        {
            ["url"] = audioPath.Replace('\\', '/'),
            ["path"] = "coverage-audio",
            ["filePrefix"] = "coverage_audio",
        };
        GetSources(testConfig).Add(source);

        _backup.Save(originalJson);
        try
        {
            progress.Report("오디오 전용 WAV 입력을 임시 소스로 추가합니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var detected = await WaitForAudioSourceErrorAsync(
                TimeSpan.FromSeconds(15), progress, cancellationToken).ConfigureAwait(false);
            return new TestResult(detected,
                detected ? "비디오 스트림 없음과 기록 가능한 스트림 없음 분기를 실행했습니다." : "제한 시간 안에 오디오 전용 입력 오류를 확인하지 못했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            DeleteFile(audioPath);
        }
    }

    public async Task<TestResult> RunUnsupportedCodecTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var gifPath = Path.Combine(_sharedTestDirectory, $"unsupported-{identity}.gif");
        var outputRoot = Path.Combine(_sharedTestDirectory, "unsupported-output-" + identity);
        Directory.CreateDirectory(_sharedTestDirectory);
        MjpegAviWriter.CreateGif(gifPath);

        var testConfig = ParseConfig(originalJson);
        testConfig["documentRoot"] = outputRoot;
        var sources = GetSources(testConfig);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = gifPath.Replace('\\', '/'),
            ["path"] = "coverage-unsupported",
            ["filePrefix"] = "coverage_unsupported",
        });

        _backup.Save(originalJson);
        try
        {
            progress.Report("MP4에서 지원하지 않는 GIF 코덱으로 헤더 작성 실패를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var detected = await WaitForSourceErrorAsync(
                error => error.Contains("write_header", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15), progress, cancellationToken).ConfigureAwait(false);
            return new TestResult(detected,
                detected ? "MP4 헤더 작성 실패 분기를 실행했습니다." : "헤더 작성 실패 상태를 확인하지 못했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            DeleteFile(gifPath);
            DeleteDirectory(outputRoot);
        }
    }

    public async Task<TestResult> RunMissingInputTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var missingPath = Path.Combine(_sharedTestDirectory, $"missing-input-{identity}.mp4");
        var testConfig = ParseConfig(originalJson);
        var sources = GetSources(testConfig);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = missingPath.Replace('\\', '/'),
            ["path"] = "coverage-missing-input",
            ["filePrefix"] = "coverage_missing_input",
        });

        _backup.Save(originalJson);
        try
        {
            progress.Report("존재하지 않는 영상 주소로 입력 열기 실패를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var detected = await WaitForSourceErrorAsync(
                error => error.Contains("open_input", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15), progress, cancellationToken).ConfigureAwait(false);
            sources[0]!["url"] = "rtspu://127.0.0.1:1/coverage";
            progress.Report("RTSP UDP 전송 주소의 입력 열기 실패 분기를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var udpDetected = await WaitForSourceErrorAsync(
                error => error.Contains("open_input", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15), progress, cancellationToken).ConfigureAwait(false);
            return new TestResult(detected && udpDetected,
                detected && udpDetected
                    ? "존재하지 않는 입력과 RTSP UDP 주소의 open_input 실패 분기를 실행했습니다."
                    : "일반 입력 또는 RTSP UDP 입력의 실패 상태를 확인하지 못했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
        }
    }

    public static async Task<TestResult> RunFfmpegLevelTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        return await HarnessCoverageRunner.RunAsync(
            "FFMPEG_BRANCHES", "FFmpeg 로그 레벨 9종과 기본값 매핑", progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<TestResult> RunPipeAcceptFailureTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "PIPE_ACCEPT_FAILURE",
            "IPC 연결 대기 취소·연결 중단 후 복구·재시도 지연 취소",
            progress,
            cancellationToken);

    public static Task<TestResult> RunMissingTimestampTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "MISSING_TIMESTAMP", "PTS/DTS가 모두 없는 패킷 건너뛰기", progress, cancellationToken);

    public static Task<TestResult> RunInvalidTimestampTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "INVALID_TIMESTAMP", "음수 및 역전 타임스탬프 보정", progress, cancellationToken);

    public static Task<TestResult> RunStreamInfoFailureTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "STREAM_INFO_FAILURE", "입력 열기 후 스트림 분석 실패·오류 로그·입력 정리", progress, cancellationToken);

    public static Task<TestResult> RunServiceLogLevelTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "SERVICE_LOG_LEVELS", "서비스 로그 Trace·Debug·Critical·기타 값", progress, cancellationToken);

    public static Task<TestResult> RunResidualBranchTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "RESIDUAL_BRANCHES", "생성 로그·엔진·보존·FFmpeg 잔여 분기", progress, cancellationToken);

    public static Task<TestResult> RunWorkerStartFailureTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "WORKER_START_FAILURE", "시험기 FFmpeg 누락 시 서비스 시작 실패", progress, cancellationToken);

    public static Task<TestResult> RunRetentionZeroTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "RETENTION_ZERO", "영상·로그 보존기간 0일 처리", progress, cancellationToken);

    public static Task<TestResult> RunRetentionMissingPathsTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "RETENTION_MISSING_PATHS", "존재하지 않는 영상·로그 경로 정리", progress, cancellationToken);

    public static Task<TestResult> RunRecorderLifecycleTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "RECORDER_LIFECYCLE", "SourceRecorder 중복 시작·종료", progress, cancellationToken);

    public static Task<TestResult> RunSameSegmentIntervalTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "SAME_SEGMENT_INTERVAL", "동일한 세그먼트 주기 재설정", progress, cancellationToken);

    public static Task<TestResult> RunEmptyPipeResponseTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunAsync(
            "EMPTY_PIPE_RESPONSE", "응답 없이 종료되는 IPC 서버", progress, cancellationToken);

    public async Task<TestResult> CreateOldRetentionFilesAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var fixture = await CreateRetentionFixtureAsync(progress, cancellationToken).ConfigureAwait(false);
        if (fixture.VideoRetentionDays <= 0 || fixture.LogRetentionDays <= 0)
        {
            return new TestResult(false,
                "파일은 생성됐지만 보존기간이 0 이하인 항목은 자동 삭제가 비활성화되어 있습니다.");
        }

        return new TestResult(true,
            "삭제 대상 파일 2개를 생성했습니다. 서비스를 재시작하고 30초 후 삭제 여부를 확인하십시오.");
    }

    public async Task<TestResult> RunRetentionTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var testConfig = ParseConfig(originalJson);
        testConfig["disuseTermDays"] = Math.Max(1, GetIntOrDefault(testConfig, "disuseTermDays", 30));
        testConfig["logRetentionDays"] = Math.Max(1, GetIntOrDefault(testConfig, "logRetentionDays", 30));
        _backup.Save(originalJson);
        RetentionFixture? fixture = null;
        try
        {
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            fixture = await CreateRetentionFixtureAsync(progress, cancellationToken).ConfigureAwait(false);
            progress.Report("오래된 영상과 로그를 잠근 상태에서 즉시 보존 정리를 실행합니다.");
            using (var videoLock = OpenExclusive(fixture.VideoPath))
            using (var logLock = OpenExclusive(fixture.LogPath))
            {
                _ = videoLock.Length + logLock.Length;
                await IpcProbeClient.RunRetentionAsync(cancellationToken).ConfigureAwait(false);
            }

            var failedDeletionCovered = File.Exists(fixture.VideoPath) && File.Exists(fixture.LogPath);
            progress.Report("파일 잠금을 해제하고 즉시 보존 정리를 다시 실행합니다.");
            await IpcProbeClient.RunRetentionAsync(cancellationToken).ConfigureAwait(false);
            var successfulDeletionCovered = !File.Exists(fixture.VideoPath) && !File.Exists(fixture.LogPath);
            return new TestResult(failedDeletionCovered && successfulDeletionCovered,
                "영상·로그 삭제 실패와 잠금 해제 후 정상 삭제 분기를 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            if (fixture is not null)
            {
                DeleteFile(fixture.VideoPath);
                DeleteFile(fixture.LogPath);
            }
        }
    }

    public async Task<TestResult> RunUdpNotificationTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var audioPath = Path.Combine(_sharedTestDirectory, "udp-notification-test.wav");
        Directory.CreateDirectory(_sharedTestDirectory);
        CreateSilentWaveFile(audioPath, TimeSpan.FromSeconds(5));
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint?)receiver.Client.LocalEndPoint
            ?? throw new InvalidOperationException("UDP 수신 포트를 만들지 못했습니다.");

        _backup.Save(originalJson);
        try
        {
            var invalidConfig = ParseConfig(originalJson);
            invalidConfig["udpNotificationIp"] = "잘못된-IP";
            invalidConfig["udpNotificationPort"] = endpoint.Port;
            progress.Report("잘못된 UDP 수신지 설정 분기를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(invalidConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);

            var unreachableConfig = ParseConfig(originalJson);
            unreachableConfig["udpNotificationIp"] = IPAddress.Loopback.ToString();
            unreachableConfig["udpNotificationPort"] = 9;
            GetSources(unreachableConfig).Add(new JsonObject
            {
                ["url"] = audioPath.Replace('\\', '/'),
                ["path"] = "coverage-udp-failure",
                ["filePrefix"] = new string('x', 70_000),
            });
            progress.Report("UDP 최대 크기를 넘는 시험 패킷으로 전송 실패 분기를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(unreachableConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            _ = await WaitForNamedSourceErrorAsync(
                "coverage-udp-failure", TimeSpan.FromSeconds(10), progress, cancellationToken)
                .ConfigureAwait(false);

            var receiveConfig = ParseConfig(originalJson);
            receiveConfig["udpNotificationIp"] = IPAddress.Loopback.ToString();
            receiveConfig["udpNotificationPort"] = endpoint.Port;
            GetSources(receiveConfig).Add(new JsonObject
            {
                ["url"] = audioPath.Replace('\\', '/'),
                ["path"] = "coverage-udp",
                ["filePrefix"] = "coverage_udp",
            });
            progress.Report($"로컬 UDP 수신기 127.0.0.1:{endpoint.Port}를 적용합니다.");
            await IpcProbeClient.SetConfigJsonAsync(receiveConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var payload = await ReceiveUdpNotificationAsync(
                receiver, "coverage_udp", progress, timeout.Token).ConfigureAwait(false);
            var success = payload["success"]?.GetValue<bool>();
            return new TestResult(success == false,
                "잘못된 UDP 설정과 실제 실패 패킷 송수신 분기를 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            DeleteFile(audioPath);
        }
    }

    public async Task<TestResult> RunLocalVideoRecordingTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var videoPath = Path.Combine(_sharedTestDirectory, $"local-video-{identity}.avi");
        var outputRoot = Path.Combine(_sharedTestDirectory, "recording-output-" + identity);
        Directory.CreateDirectory(_sharedTestDirectory);
        progress.Report("로컬 MJPEG 시험 영상을 생성합니다.");
        MjpegAviWriter.Create(videoPath);

        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint?)receiver.Client.LocalEndPoint
            ?? throw new InvalidOperationException("UDP 수신 포트를 만들지 못했습니다.");
        var testConfig = ParseConfig(originalJson);
        testConfig["documentRoot"] = outputRoot;
        testConfig["udpNotificationIp"] = IPAddress.Loopback.ToString();
        testConfig["udpNotificationPort"] = endpoint.Port;
        var sources = GetSources(testConfig);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = videoPath.Replace('\\', '/'),
            ["path"] = "coverage-local-video",
            ["filePrefix"] = "coverage_local_video",
        });

        _backup.Save(originalJson);
        try
        {
            progress.Report("정상 영상 입력·MP4 저장·종료·UDP 성공 통지를 실행합니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var payload = await ReceiveUdpNotificationAsync(
                receiver, "coverage_local_video", progress, timeout.Token).ConfigureAwait(false);
            var success = payload["success"]?.GetValue<bool>() == true;
            var recordedFile = Directory.Exists(outputRoot)
                ? Directory.EnumerateFiles(outputRoot, "*.mp4", SearchOption.AllDirectories)
                    .FirstOrDefault(static path => new FileInfo(path).Length > 0)
                : null;
            if (recordedFile is not null)
                progress.Report("정상 저장 파일: " + recordedFile);
            return new TestResult(success && recordedFile is not null,
                "정상 입력, 스트림 복사, MP4 기록·종료 및 UDP 성공 패킷을 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            DeleteFile(videoPath);
            DeleteDirectory(outputRoot);
        }
    }

    public async Task<TestResult> RunSegmentCutTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var videoPath = Path.Combine(_sharedTestDirectory, $"segment-cut-{identity}.jpg");
        var outputRoot = Path.Combine(_sharedTestDirectory, "segment-output-" + identity);
        Directory.CreateDirectory(_sharedTestDirectory);
        progress.Report("절단 시험용 MJPEG 프레임을 생성합니다.");
        MjpegAviWriter.CreateJpeg(videoPath);
        var server = SlowHttpFileServer.Start(videoPath);
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint?)receiver.Client.LocalEndPoint
            ?? throw new InvalidOperationException("UDP 수신 포트를 만들지 못했습니다.");

        var testConfig = ParseConfig(originalJson);
        testConfig["documentRoot"] = outputRoot;
        testConfig["segmentMinutes"] = 2;
        testConfig["udpNotificationIp"] = IPAddress.Loopback.ToString();
        testConfig["udpNotificationPort"] = endpoint.Port;
        var sources = GetSources(testConfig);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = server.Url.OriginalString,
            ["path"] = "coverage-segment-cut",
            ["filePrefix"] = "coverage_segment_cut",
        });

        _backup.Save(originalJson);
        try
        {
            progress.Report("천천히 공급되는 영상을 녹화하고 PLAYING 상태를 기다립니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            var playing = await WaitForNamedSourceStateAsync(
                "coverage-segment-cut", "PLAYING", TimeSpan.FromSeconds(25), progress, cancellationToken)
                .ConfigureAwait(false);
            if (!playing)
                return new TestResult(false, "제한 시간 안에 절단 시험 소스가 PLAYING 상태가 되지 않았습니다.");

            progress.Report("녹화 중 저장 주기를 변경해 다음 키프레임에서 즉시 절단합니다.");
            var intervalResponse = await IpcProbeClient.SendCommandAsync(
                "SET_INTERVAL", cancellationToken, minutes: 1).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var payload = await ReceiveUdpNotificationAsync(
                receiver, "coverage_segment_cut", progress, timeout.Token).ConfigureAwait(false);
            var accepted = intervalResponse["ok"]?.GetValue<bool>() == true;
            var success = payload["success"]?.GetValue<bool>() == true;
            return new TestResult(accepted && success,
                "녹화 중 주기 변경, 키프레임 절단, 첫 세그먼트 종료와 다음 세그먼트 시작을 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            DeleteFile(videoPath);
            DeleteDirectory(outputRoot);
        }
    }

    public async Task<TestResult> RunMixedMediaTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var mediaPath = Path.Combine(_sharedTestDirectory, $"mixed-media-{identity}.avi");
        var outputRoot = Path.Combine(_sharedTestDirectory, "mixed-output-" + identity);
        Directory.CreateDirectory(_sharedTestDirectory);
        progress.Report("영상과 PCM 오디오가 섞인 AVI 시험 파일을 생성합니다.");
        MjpegAviWriter.CreateWithAudio(mediaPath);
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint?)receiver.Client.LocalEndPoint
            ?? throw new InvalidOperationException("UDP 수신 포트를 만들지 못했습니다.");
        var testConfig = ParseConfig(originalJson);
        testConfig["documentRoot"] = outputRoot;
        testConfig["udpNotificationIp"] = IPAddress.Loopback.ToString();
        testConfig["udpNotificationPort"] = endpoint.Port;
        var sources = GetSources(testConfig);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = mediaPath.Replace('\\', '/'),
            ["path"] = "coverage-mixed-media",
            ["filePrefix"] = "coverage_mixed_media",
        });

        _backup.Save(originalJson);
        try
        {
            progress.Report("혼합 입력에서 영상만 MP4로 기록하고 오디오 패킷은 건너뜁니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var payload = await ReceiveUdpNotificationAsync(
                receiver, "coverage_mixed_media", progress, timeout.Token).ConfigureAwait(false);
            var succeeded = payload["success"]?.GetValue<bool>() == true;
            return new TestResult(succeeded,
                "영상·오디오 혼합 입력의 오디오 제외와 영상 저장 분기를 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            DeleteFile(mediaPath);
            DeleteDirectory(outputRoot);
        }
    }

    public async Task<TestResult> RunInterruptedStreamTestAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var originalJson = await IpcProbeClient.GetConfigJsonAsync(cancellationToken).ConfigureAwait(false);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var framePath = Path.Combine(_sharedTestDirectory, $"interrupted-{identity}.jpg");
        var outputRoot = Path.Combine(_sharedTestDirectory, "interrupted-output-" + identity);
        Directory.CreateDirectory(_sharedTestDirectory);
        MjpegAviWriter.CreateJpeg(framePath);
        var server = SlowHttpFileServer.Start(framePath, frameLimit: 20);
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint?)receiver.Client.LocalEndPoint
            ?? throw new InvalidOperationException("UDP 수신 포트를 만들지 못했습니다.");
        var testConfig = ParseConfig(originalJson);
        testConfig["documentRoot"] = outputRoot;
        testConfig["udpNotificationIp"] = IPAddress.Loopback.ToString();
        testConfig["udpNotificationPort"] = endpoint.Port;
        var sources = GetSources(testConfig);
        sources.Clear();
        sources.Add(new JsonObject
        {
            ["url"] = server.Url.OriginalString,
            ["path"] = "coverage-interrupted",
            ["filePrefix"] = "coverage_interrupted",
        });

        _backup.Save(originalJson);
        try
        {
            progress.Report("MJPEG 입력을 녹화 도중 강제로 끊습니다.");
            await IpcProbeClient.SetConfigJsonAsync(testConfig.ToJsonString(JsonOptions), cancellationToken)
                .ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            var payload = await ReceiveUdpNotificationAsync(
                receiver, "coverage_interrupted", progress, timeout.Token).ConfigureAwait(false);
            var succeeded = payload["success"]?.GetValue<bool>() == true;
            return new TestResult(succeeded,
                "기록 도중 입력 종료, 세그먼트 마감과 재연결 분기를 실행했습니다.");
        }
        finally
        {
            await RestorePendingCoreAsync(progress).ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            DeleteFile(framePath);
            DeleteDirectory(outputRoot);
        }
    }

    public Task<TestResult> RestorePendingAsync(IProgress<string> progress)
        => RestorePendingCoreAsync(progress);

    public async Task<TestResult> RunAllConnectedTestsAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var completed = 0;
        var failures = new List<string>();
        TestStep[] steps =
        [
            new("IPC 확장", RunExtendedIpcTestAsync),
            new("설정 파일 분기", RunConfigBranchTestAsync),
            new("엔진 정상 재시작", RunEngineRestartTestAsync),
            new("IPC 오류", RunIpcErrorTestAsync),
            new("로그 기록 실패", RunLogWriteFailureTestAsync),
            new("로그 회전 실패", RunLogRotationFailureTestAsync),
            new("보존 정리", RunRetentionTestAsync),
            new("저장 경로 실패", RunOutputPathFailureTestAsync),
            new("오디오 전용 입력", RunAudioOnlyInputTestAsync),
            new("미지원 코덱", RunUnsupportedCodecTestAsync),
            new("존재하지 않는 입력", RunMissingInputTestAsync),
            new("FFmpeg 로그 레벨", RunFfmpegLevelTestAsync),
            new("PTS/DTS 없는 패킷", RunMissingTimestampTestAsync),
            new("음수·역전 타임스탬프", RunInvalidTimestampTestAsync),
            new("스트림 정보 검색 실패", RunStreamInfoFailureTestAsync),
            new("로컬 정상 영상 저장", RunLocalVideoRecordingTestAsync),
            new("녹화 중 세그먼트 절단", RunSegmentCutTestAsync),
            new("영상·오디오 혼합 입력", RunMixedMediaTestAsync),
            new("기록 도중 입력 중단", RunInterruptedStreamTestAsync),
            new("서비스 로그 레벨", RunServiceLogLevelTestAsync),
            new("잔여 코드 분기", RunResidualBranchTestAsync),
            new("서비스 FFmpeg 시작 실패", RunWorkerStartFailureTestAsync),
            new("보존기간 0일", RunRetentionZeroTestAsync),
            new("존재하지 않는 보존 경로", RunRetentionMissingPathsTestAsync),
            new("녹화기 중복 시작·종료", RunRecorderLifecycleTestAsync),
            new("동일한 세그먼트 주기", RunSameSegmentIntervalTestAsync),
            new("IPC 무응답", RunEmptyPipeResponseTestAsync),
            new("UDP 송수신", RunUdpNotificationTestAsync),
            new("트레이 상태", TrayCoverageRunner.RunStatusAsync),
            new("트레이 설정 저장", TrayCoverageRunner.RunSettingsSaveAsync),
            new("트레이 설정 취소", TrayCoverageRunner.RunSettingsCancelAsync),
            new("트레이 로그", TrayCoverageRunner.RunLogViewerAsync),
            new("트레이 본체 동작", TrayCoverageRunner.RunAppActionsAsync),
            new("트레이 통신 예외", TrayCoverageRunner.RunProtocolAsync),
            new("트레이 중복 실행 방지", TrayCoverageRunner.RunSecondInstanceAsync),
            new("트레이 폴더 열기", TrayCoverageRunner.RunFolderButtonsAsync),
            new("트레이 경로 실패", TrayCoverageRunner.RunFolderFailureAsync),
            new("설정 저장 오류", TrayCoverageRunner.RunSettingsFailureAsync),
            new("로그 파일 장애", TrayCoverageRunner.RunLogFailureAsync),
            new("로그 폴더 없음", TrayCoverageRunner.RunMissingLogFolderAsync),
            new("트레이 전체 성공·실패", TrayCoverageRunner.RunAllConnectedBranchesAsync),
        ];
        foreach (var step in steps)
        {
            var result = await RunStepAsync(step.Name, step.Action, progress, cancellationToken)
                .ConfigureAwait(false);
            completed += result;
            if (result == 0)
                failures.Add(step.Name);
        }

        var succeeded = completed == steps.Length;
        var summary = $"전체 자동 시험 {steps.Length}종 중 {completed}종 실행 확인";
        if (failures.Count > 0)
        {
            summary += ". 실패 항목: " + string.Join(", ", failures);
            progress.Report(summary);
        }
        return new TestResult(succeeded, summary);
    }

    private async Task<RetentionFixture> CreateRetentionFixtureAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var config = await LoadLocalConfigAsync(cancellationToken).ConfigureAwait(false);
        var documentRoot = GetStringOrDefault(config, "documentRoot", @"C:\ezstream\data");
        var videoRetentionDays = GetIntOrDefault(config, "disuseTermDays", 60);
        var logRetentionDays = GetIntOrDefault(config, "logRetentionDays", 30);
        var identity = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8];
        var oldVideoTime = DateTime.Now.AddDays(-(Math.Max(videoRetentionDays, 1) + 2));
        var oldLogTime = DateTime.Now.AddDays(-(Math.Max(logRetentionDays, 1) + 2));
        var videoDirectory = Path.Combine(documentRoot, "coverage-retention", oldVideoTime.ToString(
            "yyyyMMdd", CultureInfo.InvariantCulture));
        var videoPath = Path.Combine(videoDirectory, $"coverage_old_{identity}.mp4");
        var logPath = Path.Combine(_logDirectory, $"ezstream-coverage-old-{identity}.log");

        Directory.CreateDirectory(videoDirectory);
        Directory.CreateDirectory(_logDirectory);
        await File.WriteAllBytesAsync(videoPath, new byte[1024], cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(logPath, "EzStream retention coverage test", Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        File.SetLastWriteTime(videoPath, oldVideoTime);
        File.SetLastWriteTime(logPath, oldLogTime);
        progress.Report($"오래된 영상: {videoPath}");
        progress.Report($"오래된 로그: {logPath}");
        return new RetentionFixture(videoPath, logPath, videoRetentionDays, logRetentionDays);
    }

    private async Task<TestResult> RestorePendingCoreAsync(IProgress<string> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!_backup.HasPendingBackup)
            return new TestResult(true, "복구할 설정 백업이 없습니다.");

        progress.Report("원래 서비스 설정을 복구합니다.");
        var originalJson = _backup.Load();
        await IpcProbeClient.SetConfigJsonAsync(originalJson, CancellationToken.None).ConfigureAwait(false);
        _backup.Delete();
        progress.Report("원래 설정 복구 완료");
        return new TestResult(true, "원래 설정을 복구했습니다.");
    }

    private static async Task<int> RunStepAsync(
        string name,
        Func<IProgress<string>, CancellationToken, Task<TestResult>> action,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        progress.Report($"----- {name} 시작 -----");
        try
        {
            var result = await action(progress, cancellationToken).ConfigureAwait(false);
            progress.Report($"{name}: {result.Summary}");
            return result.Succeeded ? 1 : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            progress.Report($"{name} 오류: {ex.Message}");
            return 0;
        }
    }

    private async Task<string> CreateTimedCollisionAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var target = DateTime.Now.AddSeconds(2);
        string collision;
        while (true)
        {
            collision = Path.Combine(_logDirectory,
                $"ezstream-{target:yyyyMMdd_HHmmss}.log");
            try
            {
                var stream = new FileStream(
                    collision, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await using (stream.ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (IOException)
            {
                target = target.AddSeconds(1);
            }
        }

        progress.Report($"회전 충돌 시간: {target:HH:mm:ss}");
        var delay = target.AddMilliseconds(80) - DateTime.Now;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return collision;
    }

    private static async Task ExpandLogAsync(string path, CancellationToken cancellationToken)
    {
        var line = Encoding.UTF8.GetBytes(
            "2000-01-01 00:00:00.000 [INF] [CoverageTool] rotation failure test data\r\n");
        var buffer = new byte[64 * 1024];
        for (var offset = 0; offset < buffer.Length; offset += line.Length)
        {
            var count = Math.Min(line.Length, buffer.Length - offset);
            line.AsSpan(0, count).CopyTo(buffer.AsSpan(offset, count));
        }

        var stream = new FileStream(
            path, FileMode.OpenOrCreate, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, buffer.Length, useAsync: true);
        await using (stream.ConfigureAwait(false))
        {
            stream.Seek(0, SeekOrigin.End);
            while (stream.Length < RotationThreshold)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, RotationThreshold - stream.Length);
                await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> WaitForAudioSourceErrorAsync(
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => await WaitForAudioSourceErrorAsync(
            "coverage-audio", timeout, progress, cancellationToken).ConfigureAwait(false);

    private static async Task<bool> WaitForAudioSourceErrorAsync(
        string sourcePath,
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await IpcProbeClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (FindSourceError(response, sourcePath, out var error))
            {
                progress.Report($"오디오 전용 입력 상태: {error}");
                return error.Contains("No recordable streams", StringComparison.OrdinalIgnoreCase);
            }
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForNamedSourceErrorAsync(
        string sourcePath,
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await IpcProbeClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (FindSourceError(response, sourcePath, out var error))
            {
                progress.Report($"시험 소스 오류: {error}");
                return true;
            }
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForNamedSourceStateAsync(
        string sourcePath,
        string expectedState,
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await IpcProbeClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (response["status"]?["sources"] is JsonArray sources)
            {
                foreach (var source in sources)
                {
                    if (!string.Equals(source?["path"]?.GetValue<string>(), sourcePath, StringComparison.Ordinal))
                        continue;
                    var state = source?["state"]?.GetValue<string>() ?? string.Empty;
                    progress.Report($"시험 소스 상태: {state}");
                    if (string.Equals(state, expectedState, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<bool> WaitForSourceErrorAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await IpcProbeClient.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            foreach (var error in EnumerateErrors(response))
            {
                progress.Report($"소스 오류: {error}");
                if (predicate(error))
                    return true;
            }
            await Task.Delay(700, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private static IEnumerable<string> EnumerateErrors(JsonObject response)
    {
        if (response["status"]?["sources"] is not JsonArray sources)
            yield break;

        foreach (var source in sources)
        {
            var error = source?["lastError"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
                yield return error;
        }
    }

    private static bool FindSourceError(JsonObject response, string path, out string error)
    {
        error = string.Empty;
        if (response["status"]?["sources"] is not JsonArray sources)
            return false;

        foreach (var source in sources)
        {
            if (!string.Equals(source?["path"]?.GetValue<string>(), path, StringComparison.Ordinal))
                continue;
            error = source?["lastError"]?.GetValue<string>() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(error);
        }
        return false;
    }

    private static JsonObject ParseConfig(string configJson)
        => JsonNode.Parse(configJson)?.AsObject()
            ?? throw new InvalidDataException("서비스 설정이 JSON 객체가 아닙니다.");

    private static JsonArray GetSources(JsonObject config)
        => config["sources"] as JsonArray
            ?? throw new InvalidDataException("서비스 설정에 sources 배열이 없습니다.");

    private async Task<JsonObject> LoadLocalConfigAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_configPath))
            return [];

        var configJson = await File.ReadAllTextAsync(_configPath, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);
        return ParseConfig(configJson);
    }

    private static string GetStringOrDefault(JsonObject config, string propertyName, string defaultValue)
        => config[propertyName] is JsonValue value && value.TryGetValue<string>(out var configured)
            && !string.IsNullOrWhiteSpace(configured)
                ? configured
                : defaultValue;

    private static int GetIntOrDefault(JsonObject config, string propertyName, int defaultValue)
        => config[propertyName] is JsonValue value && value.TryGetValue<int>(out var configured)
            ? configured
            : defaultValue;

    private static async Task WaitForNextSecondAsync(CancellationToken cancellationToken)
    {
        var next = DateTime.Now.AddSeconds(1);
        next = new DateTime(next.Year, next.Month, next.Day, next.Hour, next.Minute, next.Second,
            DateTimeKind.Local);
        var delay = next - DateTime.Now;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static void CreateSilentWaveFile(string path, TimeSpan duration)
    {
        const int sampleRate = 8000;
        const short channels = 1;
        const short bitsPerSample = 8;
        var dataLength = checked((int)(sampleRate * duration.TotalSeconds));

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        var silence = new byte[dataLength];
        Array.Fill(silence, (byte)128);
        writer.Write(silence);
    }

    private static void DeleteEmptyFile(string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length == 0)
            File.Delete(path);
    }

    private static void DeleteFile(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void DeleteFileOrDirectory(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static void CreateBlockedOutputNames(string root, string sourcePath, string prefix)
    {
        var now = DateTime.Now;
        for (var offset = -2; offset <= 20; offset++)
        {
            var timestamp = now.AddSeconds(offset);
            var dateDirectory = Path.Combine(root, sourcePath,
                timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var blockedFilePath = Path.Combine(dateDirectory,
                $"{prefix}_{timestamp:yyyyMMddHHmmss}.mp4");
            Directory.CreateDirectory(blockedFilePath);
        }
    }

    private static FileStream OpenExclusive(string path)
        => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    private static async Task<JsonObject> ReceiveUdpNotificationAsync(
        UdpClient receiver,
        string expectedPrefix,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await receiver.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(packet.Buffer);
            progress.Report("수신 JSON: " + json);
            var payload = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidDataException("UDP 패킷이 JSON 객체가 아닙니다.");
            if (payload["filePrefix"]?.GetValue<string>() == expectedPrefix)
                return payload;
        }
    }

    private sealed record RetentionFixture(
        string VideoPath,
        string LogPath,
        int VideoRetentionDays,
        int LogRetentionDays);

    private sealed record TestStep(
        string Name,
        Func<IProgress<string>, CancellationToken, Task<TestResult>> Action);
}
