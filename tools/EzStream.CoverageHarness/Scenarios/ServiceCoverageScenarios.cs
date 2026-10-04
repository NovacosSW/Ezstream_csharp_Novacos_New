using System.Reflection;
using System.Collections;
using System.IO.Pipes;
using System.Text;

using EzStream.Core;
using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Ipc;
using EzStream.Core.Notifications;
using EzStream.Core.Recording;
using EzStream.Core.Retention;
using EzStream.Service;

using FFmpeg.AutoGen;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Hosting;

namespace EzStream.CoverageHarness.Scenarios;

internal static class ServiceCoverageScenarios
{
    public static string RunConfigBranches()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezstream-config-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var configPath = Path.Combine(root, "config.json");
            _ = RecorderConfig.LoadOrCreate(configPath);

            File.WriteAllText(configPath, "{ invalid json");
            _ = RecorderConfig.LoadOrCreate(configPath);

            File.WriteAllText(configPath, string.Empty);
            _ = RecorderConfig.LoadOrCreate(configPath);

            var blockingFile = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blockingFile, "coverage");
            _ = RecorderConfig.LoadOrCreate(Path.Combine(blockingFile, "config.json"));

            _ = IpcRequest.Parse(string.Empty);
            _ = IpcResponse.Parse(string.Empty);
            _ = IpcRequest.Parse(new IpcRequest { Command = IpcCommands.GetStatus }.Serialize());
            _ = IpcResponse.Parse(new IpcResponse { Ok = true }.Serialize());
            return "temporary config branches completed";
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static string RunFfmpegBranches()
    {
        string[] levels =
        [
            "quiet", "panic", "fatal", "error", "warning",
            "info", "verbose", "debug", "trace", "unknown",
        ];
        var mapped = levels.Select(FfmpegLoader.ToAvLogLevel).Distinct().Count();
        return $"FFmpeg log level branches completed ({mapped})";
    }

    public static string RunServiceLogLevels()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-service-log-coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var provider = new FileLoggerProvider(root);
            provider.Write("Coverage.Trace", LogLevel.Trace, "trace", null);
            provider.Write("Coverage.Debug", LogLevel.Debug, "debug", null);
            provider.Write("Coverage.Critical", LogLevel.Critical, "critical", new IOException("coverage"));
            provider.Write("Coverage.None", LogLevel.None, "none", null);
            provider.Write("Coverage", LogLevel.Information, "category without separator", null);
            var logger = provider.CreateLogger("Coverage.Logger");
            using var scope = logger.BeginScope("coverage scope");
            logger.Log(
                LogLevel.Information,
                new EventId(9001, "Coverage"),
                "coverage logger branch",
                null,
                static (state, _) => state);
            logger.Log(
                LogLevel.Debug,
                new EventId(9002, "CoverageDisabled"),
                "disabled coverage logger branch",
                null,
                static (state, _) => state);
            MethodInfo rotate = typeof(FileLoggerProvider).GetMethod(
                "Rotate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(FileLoggerProvider).FullName, "Rotate");
            _ = rotate.Invoke(provider, ["\0"]);
            return "service log level branches completed";
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static string RunServiceLifecycle()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-service-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new RecorderConfig
        {
            DocumentRoot = root,
            UdpNotificationIp = string.Empty,
            UdpNotificationPort = 0,
        };
        var logger = new ExercisingLogger();
        using var engine = new RecorderEngine(config, logger);
        using var pipe = new PipeServer(engine, Path.Combine(root, "config.json"), logger);
        using var worker = new Worker(logger, NullLoggerFactory.Instance);
        try
        {
            engine.Start();
            pipe.Start();
            Thread.Sleep(150);
            SetWorkerField(worker, "_engine", engine);
            SetWorkerField(worker, "_pipe", pipe);
            worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            SetWorkerField(worker, "_engine", null);
            SetWorkerField(worker, "_pipe", null);
            worker.Dispose();
            return "service stop and dispose lifecycle completed";
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public static string RunWorkerStartFailure()
    {
        var ffmpegDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var disabledDirectory = ffmpegDirectory + ".coverage-disabled";
        if (!Directory.Exists(ffmpegDirectory) && Directory.Exists(disabledDirectory))
            Directory.Move(disabledDirectory, ffmpegDirectory);
        if (!Directory.Exists(ffmpegDirectory))
            throw new DirectoryNotFoundException("시험기 FFmpeg 폴더를 찾지 못했습니다.");

        Directory.Move(ffmpegDirectory, disabledDirectory);
        try
        {
            using var worker = new Worker(NullLogger<Worker>.Instance, NullLoggerFactory.Instance);
            MethodInfo execute = typeof(Worker).GetMethod(
                "ExecuteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(Worker).FullName, "ExecuteAsync");
            var task = (Task)(execute.Invoke(worker, [CancellationToken.None])
                ?? throw new InvalidOperationException("Worker 시작 작업을 만들지 못했습니다."));
            task.GetAwaiter().GetResult();
            worker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            return "worker FFmpeg load failure completed";
        }
        finally
        {
            if (Directory.Exists(disabledDirectory) && !Directory.Exists(ffmpegDirectory))
                Directory.Move(disabledDirectory, ffmpegDirectory);
        }
    }

    public static string RunResidualBranches()
    {
        var logger = new ExercisingLogger();
        ExerciseGeneratedLogs(logger);
        ExerciseFfmpegMapping();
        ExerciseFfmpegEmptyDirectory(logger);
        ExerciseFfmpegCallbackBranches();
        ExerciseFileLoggerException();
        ExerciseStartupOptions();
        ExerciseModelBranches();
        ExerciseRecorderReporting(logger);
        ExerciseStoppedPipeServer(logger);
        ExercisePipeStopFailure(logger);
        ExerciseWorkerCleanupBranches();
        ExerciseEngineBranches(logger);
        if (!RecorderCoverageScenarios.RunRecorderLoopException())
            throw new InvalidOperationException("녹화기 최상위 예외 복구 분기를 확인하지 못했습니다.");
        if (!RecorderCoverageScenarios.RunRtspUdpInputFailure())
            throw new InvalidOperationException("RTSP UDP 입력 실패 분기를 확인하지 못했습니다.");
        ExerciseRetentionFailure(logger);
        ExerciseRetentionDirectoryFailure(logger);
        ExerciseUdpEndpointBranches(logger);
        return $"residual branches completed ({logger.MessagesFormatted} messages)";
    }

    public static string RunPipeAcceptFailure()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-pipe-abort-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var scenario in new[] { "cancel-wait", "disconnect-retry", "cancel-delay" })
                ExercisePipeAcceptScenario(root, scenario);
            return "IPC 연결 대기 취소·연결 중단 후 상태 조회 복구·재시도 지연 취소 완료";
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ExercisePipeAcceptScenario(string root, string scenario)
    {
        MethodInfo accept = typeof(PipeServer).GetMethod(
            "AcceptLoop", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PipeServer).FullName, "AcceptLoop");
        using var cancellation = new CancellationTokenSource();
        using var errorObserved = new ManualResetEventSlim();
        var logger = new ExercisingLogger
        {
            OnPipeAcceptError = () =>
            {
                if (scenario == "cancel-delay")
                    cancellation.Cancel();
                errorObserved.Set();
            },
        };
        using var engine = new RecorderEngine(new RecorderConfig(), logger);
        using var pipe = new PipeServer(engine, Path.Combine(root, "config.json"), logger);
        // 연결 대기가 시작된 뒤 취소하도록 실제 비동기 루프를 직접 호출한다.
        var loop = (Task)(accept.Invoke(pipe, [cancellation.Token])
            ?? throw new InvalidOperationException("IPC 수신 루프를 시작하지 못했습니다."));
        SetField(pipe, "_cts", cancellation);
        SetField(pipe, "_loop", loop);

        if (scenario != "cancel-wait")
        {
            using (var client = new NamedPipeClientStream(
                ".", AppPaths.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                client.Connect(3000);
                var request = Encoding.UTF8.GetBytes(
                    new IpcRequest { Command = IpcCommands.GetStatus }.Serialize());
                client.Write(request);
                client.Flush();
            }

            if (!errorObserved.Wait(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("IPC 연결 중단 예외를 확인하지 못했습니다.");
            if (scenario == "disconnect-retry")
            {
                Thread.Sleep(750);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = PipeClient.SendAsync(
                    new IpcRequest { Command = IpcCommands.GetStatus }, ct: timeout.Token)
                    .GetAwaiter().GetResult();
                if (!response.Ok || response.Status is null)
                    throw new InvalidOperationException("IPC 연결 중단 후 상태 조회 복구를 확인하지 못했습니다.");
            }
        }

        pipe.Stop();
        loop.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (loop.Status != TaskStatus.RanToCompletion)
            throw new InvalidOperationException($"IPC {scenario} 정상 종료를 확인하지 못했습니다.");
    }

    private static void ExerciseGeneratedLogs(ILogger logger)
    {
        var failure = new IOException("coverage failure");
        CoreLog.RecorderLoopError(logger, "coverage", failure);
        CoreLog.CannotFindStreamInfo(logger, "coverage", "stream info");
        CoreLog.CannotAllocateOutput(logger, "coverage", "allocation");
        CoreLog.WriteTrailerFailed(logger, "coverage", failure);
        CoreLog.WriteFrameFailed(logger, "coverage", "frame");
        CoreLog.WriteTrailerReturnedError(logger, "coverage", "trailer");
        RetentionLog.Failed(logger, failure);
        EngineLog.Stopped(logger);
        FfmpegLog.Line(logger, LogLevel.Warning, "coverage ffmpeg line");
        ServiceLog.PipeAcceptError(logger, failure);
        ServiceLog.EngineStartFailed(logger, failure);
    }

    private static void ExerciseFfmpegMapping()
    {
        MethodInfo mapLevel = typeof(FfmpegLoader).GetMethod(
            "MapLevel", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(FfmpegLoader).FullName, "MapLevel");
        foreach (var level in new[] { ffmpeg.AV_LOG_ERROR, ffmpeg.AV_LOG_WARNING, ffmpeg.AV_LOG_INFO, ffmpeg.AV_LOG_TRACE })
            _ = mapLevel.Invoke(null, [level]);
        _ = FfmpegLoader.ToAvLogLevel(null!);
    }

    private static void ExerciseFfmpegEmptyDirectory(ILogger logger)
    {
        ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        FfmpegLoader.Initialize(string.Empty, "warning", logger);
    }

    private static unsafe void ExerciseFfmpegCallbackBranches()
    {
        FieldInfo callbackField = typeof(FfmpegLoader).GetField(
            "_logCallback", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(FfmpegLoader).FullName, "_logCallback");
        var callback = (av_log_set_callback_callback)(callbackField.GetValue(null)
            ?? throw new InvalidOperationException("FFmpeg 로그 콜백이 초기화되지 않았습니다."));

        callback(null, int.MaxValue, string.Empty, null);
        callback(null, ffmpeg.av_log_get_level(), string.Empty, null);
    }

    private static void ExerciseFileLoggerException()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-log-exception-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new FileLoggerProvider(root);
            provider.Write("Coverage.Exception", LogLevel.Error, "coverage", new IOException("coverage"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ExerciseStartupOptions()
    {
        Type callbacks = typeof(Worker).Assembly.GetType("Program+<>c")
            ?? throw new TypeLoadException("서비스 시작 옵션 콜백 형식을 찾지 못했습니다.");
        object callbackTarget = callbacks.GetField(
            "<>9", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null)
            ?? throw new MissingFieldException(callbacks.FullName, "<>9");
        MethodInfo windowsOptions = callbacks.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name.Contains("b__0_0", StringComparison.Ordinal));
        MethodInfo consoleOptions = callbacks.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method => method.Name.Contains("b__0_2", StringComparison.Ordinal));

        _ = windowsOptions.Invoke(callbackTarget, [new WindowsServiceLifetimeOptions()]);
        _ = consoleOptions.Invoke(callbackTarget, [new SimpleConsoleFormatterOptions()]);
    }

    private static void ExerciseModelBranches()
    {
        _ = RecorderConfig.FromJson("null");
        _ = new SourceConfig { FilePrefix = " " }.SafePrefix;

        var recorder = new SourceRecorder(new SourceConfig(), new RecorderConfig(), NullLogger.Instance);
        FieldInfo callbackField = typeof(SourceRecorder).GetField(
            "_notifyVideoSave", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SourceRecorder).FullName, "_notifyVideoSave");
        var callback = (Action<VideoSaveNotification>)(callbackField.GetValue(recorder)
            ?? throw new InvalidOperationException("기본 영상 알림 콜백을 찾지 못했습니다."));
        callback(new VideoSaveNotification());
    }

    private static void ExerciseRecorderReporting(ILogger logger)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-report-residual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var notifications = new List<VideoSaveNotification>();
            var recorder = new SourceRecorder(
                new SourceConfig { FilePrefix = "coverage" },
                new RecorderConfig { DocumentRoot = root },
                logger,
                notifications.Add);
            MethodInfo report = typeof(SourceRecorder).GetMethod(
                "ReportClosedSegment", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(SourceRecorder).FullName, "ReportClosedSegment");

            _ = report.Invoke(recorder, [true, null]);

            var existing = Path.Combine(root, "segment.mp4");
            File.WriteAllBytes(existing, [1, 2, 3, 4]);
            SetRecorderSegment(recorder, existing);
            _ = report.Invoke(recorder, [true, null]);

            SetRecorderSegment(recorder, Path.Combine(root, "missing.mp4"));
            _ = report.Invoke(recorder, [true, null]);

            SetRecorderSegment(recorder, existing);
            _ = report.Invoke(recorder, [false, "coverage forced close"]);

            var blockingRoot = Path.Combine(root, "blocked-root");
            File.WriteAllText(blockingRoot, "coverage");
            var blockedRecorder = new SourceRecorder(
                new SourceConfig { FilePrefix = "blocked" },
                new RecorderConfig { DocumentRoot = blockingRoot },
                logger,
                notifications.Add);
            MethodInfo openSegment = typeof(SourceRecorder).GetMethod(
                "OpenNewSegment", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(typeof(SourceRecorder).FullName, "OpenNewSegment");
            if ((bool)(openSegment.Invoke(blockedRecorder, null) ?? true))
                throw new InvalidOperationException("차단된 저장 경로가 성공으로 처리되었습니다.");

            if (notifications.Count != 4)
                throw new InvalidOperationException("영상 저장 결과 분기가 모두 실행되지 않았습니다.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void SetRecorderSegment(SourceRecorder recorder, string filePath)
    {
        SetField(recorder, "_currentFile", filePath);
        SetField(recorder, "_segmentStartedAt", DateTimeOffset.Now.AddSeconds(-1));
    }

    private static void ExerciseStoppedPipeServer(ILogger logger)
    {
        using var engine = new RecorderEngine(new RecorderConfig(), logger);
        using var pipe = new PipeServer(
            engine, Path.Combine(Path.GetTempPath(), "coverage-config.json"), logger);
        pipe.Stop();
        MethodInfo handle = typeof(PipeServer).GetMethod(
            "Handle", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PipeServer).FullName, "Handle");
        var request = new IpcRequest { Command = IpcCommands.RunRetention }.Serialize();
        var response = (IpcResponse)(handle.Invoke(pipe, [request])
            ?? throw new InvalidOperationException("IPC 응답을 받지 못했습니다."));
        if (response.Ok)
            throw new InvalidOperationException("중지된 엔진의 보존 요청이 성공으로 처리되었습니다.");
    }

    private static void ExercisePipeStopFailure(ILogger logger)
    {
        using var engine = new RecorderEngine(new RecorderConfig(), logger);
        using var cancellation = new CancellationTokenSource();
        using var pipe = new PipeServer(
            engine, Path.Combine(Path.GetTempPath(), "coverage-stop-config.json"), logger);
        SetField(pipe, "_cts", cancellation);
        SetField(pipe, "_loop", Task.FromException(new IOException("coverage pipe stop failure")));
        pipe.Stop();
    }

    private static void ExerciseWorkerCleanupBranches()
    {
        using (var emptyWorker = new Worker(NullLogger<Worker>.Instance, NullLoggerFactory.Instance))
            emptyWorker.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

        using var engine = new RecorderEngine(new RecorderConfig(), NullLogger.Instance);
        using var pipe = new PipeServer(
            engine, Path.Combine(Path.GetTempPath(), "coverage-worker-config.json"), NullLogger.Instance);
        using var worker = new Worker(NullLogger<Worker>.Instance, NullLoggerFactory.Instance);
        SetWorkerField(worker, "_engine", engine);
        SetWorkerField(worker, "_pipe", pipe);
    }

    private static void ExerciseEngineBranches(ILogger logger)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-engine-residual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var initial = new RecorderConfig
            {
                DocumentRoot = root,
                UdpNotificationIp = string.Empty,
                UdpNotificationPort = 0,
            };
            initial.Sources.Add(new SourceConfig());
            using var engine = new RecorderEngine(initial, logger);

            var beforeStart = initial.Clone();
            beforeStart.SegmentMinutes++;
            engine.Reload(beforeStart);
            _ = engine.RunRetentionNow();

            engine.Start();
            engine.Start();
            _ = engine.RunRetentionNow();

            var structural = engine.CurrentConfig;
            structural.DocumentRoot = Path.Combine(root, "changed");
            engine.Reload(structural);

            var interval = engine.CurrentConfig;
            interval.SegmentMinutes++;
            engine.Reload(interval);
            engine.Reload(interval.Clone());
            engine.Stop();
            engine.Stop();

            using var nullRetentionEngine = new RecorderEngine(initial.Clone(), logger);
            nullRetentionEngine.Start();
            FieldInfo retentionField = typeof(RecorderEngine).GetField(
                "_retention", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(RecorderEngine).FullName, "_retention");
            var detachedCleaner = (RetentionCleaner?)(retentionField.GetValue(nullRetentionEngine));
            retentionField.SetValue(nullRetentionEngine, null);
            nullRetentionEngine.Stop();
            detachedCleaner?.Dispose();

            var recorderConfig = initial.Clone();
            recorderConfig.Sources.Clear();
            recorderConfig.Sources.Add(new SourceConfig
            {
                Url = new Uri("file:///coverage-missing-input.mp4"),
                Path = "coverage-stop",
                FilePrefix = "coverage",
            });
            using var recorderEngine = new RecorderEngine(recorderConfig, logger);
            recorderEngine.Start();
            recorderEngine.Stop();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void ExerciseRetentionFailure(ILogger logger)
    {
        using var cleaner = new RetentionCleaner(
            static () => throw new IOException("coverage retention failure"), logger);
        cleaner.RunNow();
        cleaner.Stop();
    }

    private static void ExerciseRetentionDirectoryFailure(ILogger logger)
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-retention-directory-" + Guid.NewGuid().ToString("N"));
        var occupied = Path.Combine(root, "occupied");
        Directory.CreateDirectory(occupied);
        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(occupied);
            _ = RetentionCleaner.CleanOnce(root, 1, DateTime.Now, logger);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        var missing = Path.Combine(root, "missing");
        _ = RetentionCleaner.CleanOnce(" ", 1, DateTime.Now, logger);
        _ = RetentionCleaner.CleanLogs(" ", 1, DateTime.Now, logger);
        _ = RetentionCleaner.CleanOnce(missing, 1, DateTime.Now, logger);
        _ = RetentionCleaner.CleanLogs(missing, 1, DateTime.Now, logger);
    }

    private static void ExerciseUdpEndpointBranches(ILogger logger)
    {
        var notifier = new UdpVideoSaveNotifier(logger);
        notifier.UpdateEndpoint(null, 0);
        notifier.UpdateEndpoint(null, 1);
        notifier.UpdateEndpoint(" ", 1);
        notifier.UpdateEndpoint("not-an-address", 12345);
        notifier.UpdateEndpoint("127.0.0.1", -1);
        notifier.UpdateEndpoint("::1", 12345);
    }

    private static void SetWorkerField(Worker worker, string fieldName, object? value)
        => SetField(worker, fieldName, value);

    private static void SetField(object instance, string fieldName, object? value)
    {
        FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(instance.GetType().FullName, fieldName);
        field.SetValue(instance, value);
    }

    private sealed class ExercisingLogger : ILogger, ILogger<Worker>
    {
        public int MessagesFormatted { get; private set; }
        public Action? OnPipeAcceptError { get; init; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => EmptyScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = logLevel;
            _ = eventId;
            _ = formatter(state, exception);
            MessagesFormatted++;
            if (eventId.Id == 101)
                OnPipeAcceptError?.Invoke();

            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                _ = values.Count;
                for (var index = 0; index < values.Count; index++)
                    _ = values[index];
                try
                {
                    _ = values[-1];
                }
                catch (ArgumentOutOfRangeException)
                {
                    // LoggerMessage 생성 구조체의 잘못된 인덱스 분기도 확인한다.
                }
                catch (IndexOutOfRangeException)
                {
                    // 생성된 로거 구조체 구현에 따라 이 예외 형식을 사용한다.
                }
            }

            if (state is IEnumerable enumerable)
            {
                var enumerator = enumerable.GetEnumerator();
                try
                {
                    while (enumerator.MoveNext())
                        _ = enumerator.Current;
                }
                finally
                {
                    (enumerator as IDisposable)?.Dispose();
                }
            }
        }
    }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
