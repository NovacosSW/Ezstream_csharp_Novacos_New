using FFmpeg.AutoGen;

using EzStream.Core;
using EzStream.Core.Config;
using EzStream.Core.Ffmpeg;
using EzStream.Core.Ipc;
using EzStream.Core.Recording;
using EzStream.Core.Retention;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Drawing.Imaging;

namespace EzStream.CoverageHarness.Scenarios;

internal static class RecorderCoverageScenarios
{
    public static unsafe bool RunMissingTimestamp()
    {
        var packet = new AVPacket
        {
            pts = ffmpeg.AV_NOPTS_VALUE,
            dts = ffmpeg.AV_NOPTS_VALUE,
        };
        var segmentStart = long.MinValue;
        var timeBase = new AVRational { num = 1, den = 1000 };
        return !SourceRecorder.PreparePacketForOutput(
            &packet, timeBase, timeBase, 0, ref segmentStart)
            && !string.IsNullOrEmpty(RecorderEagainCoverageScenario.Run(missingTimestamp: true));
    }

    public static unsafe bool RunInvalidTimestampOrder()
    {
        var timeBase = new AVRational { num = 1, den = 1000 };
        var segmentStart = 0L;
        var negative = new AVPacket { pts = -20, dts = -10 };
        var negativeAccepted = SourceRecorder.PreparePacketForOutput(
            &negative, timeBase, timeBase, 0, ref segmentStart);

        segmentStart = 0;
        var reversed = new AVPacket { pts = 5, dts = 10 };
        var reversedAccepted = SourceRecorder.PreparePacketForOutput(
            &reversed, timeBase, timeBase, 0, ref segmentStart);
        return negativeAccepted && reversedAccepted && RunTimestampReferenceSelection();
    }

    private static unsafe bool RunTimestampReferenceSelection()
    {
        var timeBase = new AVRational { num = 1, den = 1000 };
        foreach (var test in new[]
        {
            (Pts: 1000L, Dts: ffmpeg.AV_NOPTS_VALUE, ExpectedPts: 0L, ExpectedDts: ffmpeg.AV_NOPTS_VALUE),
            (Pts: ffmpeg.AV_NOPTS_VALUE, Dts: 1000L, ExpectedPts: ffmpeg.AV_NOPTS_VALUE, ExpectedDts: 0L),
            (Pts: 1200L, Dts: 1000L, ExpectedPts: 200L, ExpectedDts: 0L),
        })
        {
            var segmentStart = ffmpeg.AV_NOPTS_VALUE;
            var packet = new AVPacket { pts = test.Pts, dts = test.Dts, stream_index = 0, pos = 123 };
            var accepted = SourceRecorder.PreparePacketForOutput(
                &packet, timeBase, timeBase, 2, ref segmentStart);
            if (!accepted || segmentStart != 1_000_000L
                || packet.pts != test.ExpectedPts || packet.dts != test.ExpectedDts
                || !HasOutputPacketLocation(packet))
                throw new InvalidOperationException("PTS 단독·DTS 단독·DTS 우선 기준 시각 선택 검증 실패");
        }
        return true;
    }

    private static bool HasOutputPacketLocation(AVPacket packet)
        => packet.stream_index == 2 && packet.pos == -1;

    public static bool RunRetentionZero()
    {
        var missingRoot = Path.Combine(Path.GetTempPath(), "ezstream-retention-zero");
        var logger = NullLogger.Instance;
        return RetentionCleaner.CleanOnce(missingRoot, 0, DateTime.Now, logger) == 0
            && RetentionCleaner.CleanLogs(missingRoot, 0, DateTime.Now, logger) == 0;
    }

    public static bool RunRetentionMissingPaths()
    {
        var missingRoot = Path.Combine(
            Path.GetTempPath(), "ezstream-retention-missing-" + Guid.NewGuid().ToString("N"));
        var logger = NullLogger.Instance;
        return RetentionCleaner.CleanOnce(missingRoot, 1, DateTime.Now, logger) == 0
            && RetentionCleaner.CleanLogs(missingRoot, 1, DateTime.Now, logger) == 0;
    }

    public static bool RunRecorderLifecycle()
    {
        var config = new RecorderConfig();
        var source = new SourceConfig { Path = "coverage-lifecycle", FilePrefix = "coverage" };
        var recorder = new SourceRecorder(source, config, NullLogger.Instance);
        recorder.Start();
        recorder.Start();
        for (var attempt = 0; attempt < 50 && recorder.Snapshot().State != "STOPPED"; attempt++)
            Thread.Sleep(10);
        recorder.Stop();
        recorder.Stop();
        return recorder.Snapshot().State == "STOPPED";
    }

    public static bool RunSameSegmentInterval()
    {
        var config = new RecorderConfig();
        var source = new SourceConfig { Path = "coverage-interval", FilePrefix = "coverage" };
        var recorder = new SourceRecorder(source, config, NullLogger.Instance);
        recorder.UpdateSegmentMinutes(config.SegmentMillis);
        return recorder.Snapshot().State == "INIT";
    }

    public static bool RunRecorderLoopException()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-recorder-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputPath = Path.Combine(root, "input.gif");
        var blockingRoot = Path.Combine(root, "blocked-root");
        using (var bitmap = new Bitmap(32, 32))
            bitmap.Save(inputPath, ImageFormat.Gif);
        File.WriteAllText(blockingRoot, "coverage");

        const string marker = "coverage notification failure";
        FfmpegLoader.Initialize(
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            "warning",
            NullLogger.Instance);
        var recorder = new SourceRecorder(
            new SourceConfig
            {
                Url = new Uri(inputPath),
                Path = "coverage-loop",
                FilePrefix = "coverage_loop",
            },
            new RecorderConfig { DocumentRoot = blockingRoot },
            NullLogger.Instance,
            static _ => throw new IOException(marker));
        try
        {
            recorder.Start();
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (string.Equals(recorder.Snapshot().LastError, marker, StringComparison.Ordinal))
                    return true;
                Thread.Sleep(50);
            }
            return false;
        }
        finally
        {
            recorder.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    public static bool RunRtspUdpInputFailure()
    {
        FfmpegLoader.Initialize(
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            "warning",
            NullLogger.Instance);
        var recorder = new SourceRecorder(
            new SourceConfig
            {
                Url = new Uri("rtspu://127.0.0.1:1/coverage"),
                Path = "coverage-rtsp-udp",
                FilePrefix = "coverage_rtsp_udp",
            },
            new RecorderConfig(),
            NullLogger.Instance);
        try
        {
            InvokeRecorder(recorder, "RunOnce");
            return recorder.Snapshot().LastError?.Contains(
                "open_input", StringComparison.OrdinalIgnoreCase) == true;
        }
        finally
        {
            InvokeRecorder(recorder, "CloseInput");
        }
    }

    public static bool RunUnsupportedCodecHeader()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "ezstream-unsupported-codec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var inputPath = Path.Combine(root, "unsupported.gif");
        using (var bitmap = new Bitmap(64, 64))
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.DarkBlue);
            bitmap.Save(inputPath, ImageFormat.Gif);
        }

        FfmpegLoader.Initialize(
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            "warning",
            NullLogger.Instance);
        var logger = new EnabledLogger();
        var recorder = new SourceRecorder(
            new SourceConfig
            {
                Url = new Uri(inputPath),
                Path = "coverage-unsupported",
                FilePrefix = "coverage_unsupported",
            },
            new RecorderConfig { DocumentRoot = root },
            logger);
        try
        {
            InvokeRecorder(recorder, "RunOnce");
            var error = recorder.Snapshot().LastError;
            return error?.Contains("write_header", StringComparison.OrdinalIgnoreCase) == true;
        }
        finally
        {
            InvokeRecorder(recorder, "CloseOutput", false);
            InvokeRecorder(recorder, "CloseInput");
            Directory.Delete(root, recursive: true);
        }
    }

    public static bool RunEmptyPipeResponse()
        => RunEmptyPipeResponseCoreAsync().GetAwaiter().GetResult();

    private static async Task<bool> RunEmptyPipeResponseCoreAsync()
    {
        var pipeName = "ezstream-empty-response-" + Guid.NewGuid().ToString("N");
        var serverTask = AcceptRequestAndCloseAsync(pipeName);
        IpcResponse emptyResponse;
        try
        {
            emptyResponse = await SendToNamedPipeAsync(
                new IpcRequest { Command = IpcCommands.GetStatus }, pipeName).ConfigureAwait(false);
        }
        finally
        {
            await serverTask.ConfigureAwait(false);
        }

        var abortedPipeName = "ezstream-aborted-response-" + Guid.NewGuid().ToString("N");
        var abortedServerTask = AcceptAndAbortAsync(abortedPipeName);
        IpcResponse abortedResponse;
        try
        {
            abortedResponse = await SendToNamedPipeAsync(
                new IpcRequest { Command = IpcCommands.GetStatus }, abortedPipeName).ConfigureAwait(false);
        }
        finally
        {
            await abortedServerTask.ConfigureAwait(false);
        }
        return !emptyResponse.Ok
            && string.Equals(emptyResponse.Message, "empty response", StringComparison.Ordinal)
            && !abortedResponse.Ok
            && string.Equals(abortedResponse.Message, "empty response", StringComparison.Ordinal);
    }

    private static async Task AcceptRequestAndCloseAsync(string pipeName)
    {
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().ConfigureAwait(false);
        using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
        _ = await reader.ReadLineAsync().ConfigureAwait(false);
    }

    private static async Task AcceptAndAbortAsync(string pipeName)
    {
        using var server = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync().ConfigureAwait(false);
        server.Disconnect();
    }

    private static Task<IpcResponse> SendToNamedPipeAsync(IpcRequest request, string pipeName)
    {
        var method = typeof(PipeClient).GetMethod(
            "SendCoreAsync", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(PipeClient).FullName, "SendCoreAsync");
        return (Task<IpcResponse>)(method.Invoke(
            null, [request, pipeName, 3000, CancellationToken.None])
            ?? throw new InvalidOperationException("IPC 시험 작업을 생성하지 못했습니다."));
    }

    private static void InvokeRecorder(SourceRecorder recorder, string methodName, params object?[] arguments)
    {
        var method = typeof(SourceRecorder).GetMethod(
            methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(SourceRecorder).FullName, methodName);
        _ = method.Invoke(recorder, arguments);
    }

    private sealed class EnabledLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

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
        }
    }
}
