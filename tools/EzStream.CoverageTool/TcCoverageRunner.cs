namespace EzStream.CoverageTool;

internal sealed class TcCoverageRunner
{
    private readonly Dictionary<int, TestCasePlan> _plans;

    public TcCoverageRunner(CoverageTestRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _plans = CreatePlans(runner);
    }

    public async Task<TestResult> RunAsync(
        int number,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!_plans.TryGetValue(number, out var plan))
            return new TestResult(false, $"TC-{number:00} 시험 계획을 찾지 못했습니다.");

        if (number <= 20)
        {
            var serviceReady = await ProductSessionLauncher.EnsureServiceAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(serviceReady.Summary);
            if (!serviceReady.Succeeded)
                return serviceReady;
        }

        var completed = 0;
        var failures = new List<string>();
        progress.Report($"TC-{number:00} {plan.Title}: {plan.Steps.Count}개 세부 시험을 시작합니다.");
        foreach (var step in plan.Steps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report($"[{step.Name}] 시작");
            try
            {
                var result = await step.Action(progress, cancellationToken).ConfigureAwait(false);
                progress.Report($"[{step.Name}] {result.Summary}");
                if (result.Succeeded)
                    completed++;
                else
                    failures.Add(step.Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                failures.Add(step.Name);
                progress.Report($"[{step.Name}] 오류: {ex.Message}");
            }
        }

        var succeeded = completed == plan.Steps.Count;
        var summary = $"TC-{number:00} {plan.Title}: {plan.Steps.Count}개 중 {completed}개 실행 확인";
        if (failures.Count > 0)
            summary += ". 확인 필요: " + string.Join(", ", failures);
        return new TestResult(succeeded, summary);
    }

    public async Task<TestResult> RunConnectedRangeAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report("전체시험을 새 회차로 시작하기 위해 기존 세션과 프로그램을 정리합니다.");
        var serviceReady = await ProductSessionLauncher.PrepareAllAsync(progress, cancellationToken)
            .ConfigureAwait(false);
        progress.Report(serviceReady.Summary);
        if (!serviceReady.Succeeded)
            return serviceReady;

        var completed = 0;
        var failures = new List<string>();
        for (var number = 1; number <= 19; number++)
        {
            var result = await RunAsync(number, progress, cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
                completed++;
            else
                failures.Add($"TC-{number:00}");
        }

        var lifecycle = await RunStandaloneServiceLifecycleAsync(
            "service_TC19_lifecycle.coverage", progress, cancellationToken).ConfigureAwait(false);
        progress.Report(lifecycle.Summary);
        if (!lifecycle.Succeeded)
            failures.Add("Service 종료 수명주기 독립 수집");

        var succeeded = completed == 19 && lifecycle.Succeeded;
        var summary = $"TC-01~TC-19 전체 시험: 19개 중 {completed}개 실행 확인";
        if (failures.Count > 0)
            summary += ". 재시험 필요: " + string.Join(", ", failures);
        return new TestResult(succeeded, summary);
    }

    private static Dictionary<int, TestCasePlan> CreatePlans(CoverageTestRunner runner)
        => new Dictionary<int, TestCasePlan>
        {
            [1] = Plan("설정 파일 오류 및 복구", Step("설정 없음·빈 파일·손상·저장 실패", CoverageTestRunner.RunConfigBranchTestAsync)),
            [2] = Plan("서비스 및 FFmpeg 로그 수준",
                Step("서비스 로그 수준", CoverageTestRunner.RunServiceLogLevelTestAsync),
                Step("FFmpeg 로그 수준", CoverageTestRunner.RunFfmpegLevelTestAsync)),
            [3] = Plan("잔여 시작 및 Core 분기", Step("잔여 시작·Core 분기", CoverageTestRunner.RunResidualBranchTestAsync)),
            [4] = Plan("FFmpeg 로드 실패 및 복구", Step("FFmpeg 누락 시작 실패", CoverageTestRunner.RunWorkerStartFailureTestAsync)),
            [5] = Plan("IPC 요청 검증 및 오류 처리",
                Step("비정상 IPC 요청", CoverageTestRunner.RunIpcErrorTestAsync),
                Step("IPC 확장 요청", runner.RunExtendedIpcTestAsync),
                Step("응답 없는 IPC", CoverageTestRunner.RunEmptyPipeResponseTestAsync),
                Step("Tray IPC 프로토콜", TrayCoverageRunner.RunProtocolAsync)),
            [6] = Plan("로그 회전 및 기록 장애",
                Step("로그 회전 실패·복구", runner.RunLogRotationFailureTestAsync),
                Step("로그 잠금·기록 복구", runner.RunLogWriteFailureTestAsync)),
            [7] = Plan("로그 파일 장애 및 로그창 기능",
                Step("로그 잠금·삭제", TrayCoverageRunner.RunLogFailureAsync),
                Step("로그 폴더 없음", TrayCoverageRunner.RunMissingLogFolderAsync),
                Step("로그 보기·스크롤·지우기", TrayCoverageRunner.RunLogViewerAsync)),
            [8] = Plan("저장 경로 장애 및 폴더 열기",
                Step("영상 저장 경로 실패", runner.RunOutputPathFailureTestAsync),
                Step("Tray 경로 열기 실패", TrayCoverageRunner.RunFolderFailureAsync),
                Step("Tray 폴더 열기", TrayCoverageRunner.RunFolderButtonsAsync)),
            [9] = Plan("오디오 전용 입력 처리", Step("오디오 전용 입력", runner.RunAudioOnlyInputTestAsync)),
            [10] = Plan("정상 영상 저장 및 혼합 입력",
                Step("로컬 정상 영상 저장", runner.RunLocalVideoRecordingTestAsync),
                Step("영상·오디오 혼합 입력", runner.RunMixedMediaTestAsync)),
            [11] = Plan("녹화 엔진 수명주기 및 세그먼트 절단",
                Step("엔진 재시작", runner.RunEngineRestartTestAsync),
                Step("녹화기 중복 시작·종료", CoverageTestRunner.RunRecorderLifecycleTestAsync),
                Step("동일 세그먼트 주기", CoverageTestRunner.RunSameSegmentIntervalTestAsync),
                Step("세그먼트 절단", runner.RunSegmentCutTestAsync)),
            [12] = Plan("미지원 코덱 출력 실패",
                Step("미지원 코덱", (progress, cancellationToken) =>
                    RunUnsupportedCodecWithFallbackAsync(runner, progress, cancellationToken))),
            [13] = Plan("입력 열기 및 스트림 분석 실패",
                Step("존재하지 않는 입력", runner.RunMissingInputTestAsync),
                Step("스트림 정보 검색 실패", CoverageTestRunner.RunStreamInfoFailureTestAsync)),
            [14] = Plan("녹화 중 입력 중단 및 재연결",
                Step("기록 중 입력 중단", runner.RunInterruptedStreamTestAsync),
                Step("일시적 입력 지연·패킷 인덱스 오류 후 녹화 복구", (progress, cancellationToken) =>
                    HarnessCoverageRunner.RunAsync("RECORDER_EAGAIN",
                        "EAGAIN 재시도·인덱스 범위 방어 및 녹화 복구", progress, cancellationToken))),
            [15] = Plan("패킷 타임스탬프 예외 처리",
                Step("PTS/DTS 없음", CoverageTestRunner.RunMissingTimestampTestAsync),
                Step("음수·역전 타임스탬프", CoverageTestRunner.RunInvalidTimestampTestAsync)),
            [16] = Plan("보존기간 비활성 및 없는 경로 처리",
                Step("보존기간 0일", CoverageTestRunner.RunRetentionZeroTestAsync),
                Step("없는 영상·로그 경로", CoverageTestRunner.RunRetentionMissingPathsTestAsync)),
            [17] = Plan("오래된 파일 생성 및 보존 정리",
                Step("오래된 영상·로그 생성", runner.CreateOldRetentionFilesAsync),
                Step("보존 정리 및 삭제 복구", runner.RunRetentionTestAsync)),
            [18] = Plan("UDP 영상 저장 결과 송수신", Step("UDP 설정·JSON 송수신", runner.RunUdpNotificationTestAsync)),
            [19] = Plan("트레이 설정 저장 및 취소",
                Step("Tray 본체 동작", TrayCoverageRunner.RunAppActionsAsync),
                Step("설정 저장", TrayCoverageRunner.RunSettingsSaveAsync),
                Step("설정 취소", TrayCoverageRunner.RunSettingsCancelAsync),
                Step("연결 상태 전체 분기", TrayCoverageRunner.RunAllConnectedBranchesAsync)),
            [20] = Plan("트레이 상태 갱신 및 서비스 최종 종료",
                Step("Tray 상태 갱신", TrayCoverageRunner.RunStatusAsync),
                Step("Service 정상 종료 수명주기", (progress, cancellationToken) =>
                    RunStandaloneServiceLifecycleAsync(
                        "service_TC20_lifecycle.coverage", progress, cancellationToken)),
                Step("Service 종료 전 결과 저장", CoverageSessionFinalizer.SnapshotServiceCheckpointAsync),
                Step("실제 Service 실행 종료", ProductSessionLauncher.StopServiceForFinalCoverageAsync),
                Step("Service 비콘솔 모드", ProductSessionLauncher.RunServiceWithoutConsoleForCoverageAsync),
                Step("Service Host 정상 종료 및 Run 반환", (progress, cancellationToken) =>
                    HarnessCoverageRunner.RunAsync("SERVICE_HOST_LIFECYCLE",
                        "실제 Service Program의 Host 정상 종료", progress, cancellationToken)),
                Step("IPC 연결 중단 복구 및 대기 취소", CoverageTestRunner.RunPipeAcceptFailureTestAsync),
                Step("Service 결과 저장·종료", CoverageSessionFinalizer.FinalizeServiceAsync)),
            [21] = Plan("서비스 미연결 상태의 트레이 기능",
                Step("서비스 미연결 표시", TrayCoverageRunner.RunDisconnectedAsync),
                Step("미연결 설정 오류", TrayCoverageRunner.RunSettingsFailureAsync),
                Step("미연결 경로 오류", TrayCoverageRunner.RunFolderFailureAsync)),
            [22] = Plan("트레이 중복 실행 및 최종 종료",
                Step("두 번째 Tray 실행 차단", TrayCoverageRunner.RunSecondInstanceAsync),
                Step("Tray 종료 분기", TrayCoverageRunner.RunExitAsync),
                Step("Tray 결과 저장·세션 종료", CoverageSessionFinalizer.FinalizeTrayAsync)),
        };

    private static TestCasePlan Plan(string title, params TestStep[] steps)
        => new(title, steps);

    private static TestStep Step(
        string name,
        Func<IProgress<string>, CancellationToken, Task<TestResult>> action)
        => new(name, action);

    private static async Task<TestResult> RunUnsupportedCodecWithFallbackAsync(
        CoverageTestRunner runner,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        var serviceResult = await runner.RunUnsupportedCodecTestAsync(progress, cancellationToken)
            .ConfigureAwait(false);
        if (serviceResult.Succeeded)
            return serviceResult;

        progress.Report("실제 Service에서 헤더 실패를 확인하지 못해 독립 시험 입력으로 같은 분기를 실행합니다.");
        return await HarnessCoverageRunner.RunStandaloneAsync(
            "UNSUPPORTED_CODEC_HEADER",
            "미지원 코덱 MP4 헤더 작성 실패",
            "service_TC12_unsupported.coverage",
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<TestResult> RunStandaloneServiceLifecycleAsync(
        string outputFileName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
        => HarnessCoverageRunner.RunStandaloneAsync(
            "SERVICE_LIFECYCLE",
            "Service 정상 종료·PipeServer 취소·Dispose",
            outputFileName,
            progress,
            cancellationToken);

    private sealed record TestCasePlan(string Title, IReadOnlyList<TestStep> Steps);

    private sealed record TestStep(
        string Name,
        Func<IProgress<string>, CancellationToken, Task<TestResult>> Action);
}
