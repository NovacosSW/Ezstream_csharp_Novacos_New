# 조사 맥락

## 입력 인덱스 시뮬레이터 구현

- 사용자 승인에 따라 기존 RECORDER_EAGAIN 시험을 확장한다. 고정 단일 비디오 AVI에서 네이티브 읽기 성공 패킷 두 개의 인덱스를 -1과 nb_streams(매핑 길이)로 바꾼다. 실제 스트림 추가가 아닌 오류 주입 시험이다.
- 비정상 패킷 2개가 정상 저장 패킷 수에 포함되지 않고 이후 정상 녹화가 이어지는지 저장 MP4 재읽기로 검증한다. 제품 코드와 기존 사용자 변경은 유지한다.
- Harness와 Tool 각각 `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0`, `dotnet build tools/EzStream.CoverageTool/EzStream.CoverageTool.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 성공. 모두 경고/오류 0개.
- 별도 collector 서버 세션에서 `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RECORDER_EAGAIN` 통과. EAGAIN 1회와 인덱스 오류 2회 주입, 후속 정상 기록, 정상 패킷 수와 저장 MP4 재읽기 수 일치, 바인딩 복원을 시나리오에서 검증했다.
- artifacts/index-integration/result.xml을 XML로 파싱해 RunOnce 185/186/187/199/201/202/203행의 모든 range가 covered=yes임을 검사했다. 기존 199행 partial이 해소됐다.
- 제품 소스 및 기존 제품 DLL/PDB 등 53개 파일 SHA256 불변 확인. git diff --check 통과. TC-14 및 이를 호출하는 전체 UI 시험에 포함되며, 전체 UI 시험 자체는 이번에 재실행하지 않았다.

## 입력 스트림 인덱스 검토 시작

- 사용자는 SourceRecorder 199행의 삼항식 검토를 요청했다. 코드 변경 승인으로 확대하지 않고 현재 매핑 수명과 FFmpeg 계약을 검토한다.
- 결과.xml을 PowerShell XML로 읽어 RunOnce 199행 covered=partial, 201~203행 covered=yes를 확인했다. 이 보고서는 조건별 세부 결과를 제공하지 않으므로 음수/상한 분기 중 정확히 무엇이 미달성인지 단정하지 않는다.
- SourceRecorder 348행에서 _ic->nb_streams 크기로 매핑을 생성하고 비디오는 출력 인덱스, 나머지는 -1을 저장한다. 세그먼트 생성 전의 스냅샷이므로 나중에 추가된 스트림은 기존 매핑 상한을 벗어날 수 있다.
- FFmpeg n4.4 공식 avformat.h의 AVFormatContext.streams 주석은 AVFMTCTX_NOHEADER인 경우 av_read_frame 중 새 스트림이 나타날 수 있다고 명시한다. 근거 https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavformat/avformat.h . av_read_frame 성공 패킷은 AVStream을 식별하는 stream_index를 가진다. 정상 음수 인덱스는 기대하지 않는다.
- 상한 방어는 유지 권장하며 도달 불가능으로 제외할 근거가 없다. 음수 방어는 비정상 라이브러리 반환을 대비한 조건으로 별도 제외 검토가 가능하나, 오류 주입 검증이 가능하므로 우선 시험을 권장한다. 확률 수치는 운영 통계가 없어 산정하지 않는다.
- 앞의 ret 검사는 읽기 성공 여부이며 과거에 만든 매핑의 범위를 보장하지 않는다. 뒤의 outIdx<0 검사 역시 배열 접근 이후이므로 이 줄의 범위 검사를 대체하지 못한다. 범위 검사를 삭제하면 IndexOutOfRangeException 가능성이 생긴다.
- 기존 Harness 바인딩 교체 방식에서 네이티브 읽기 성공 후 패킷 인덱스를 -1과 매핑 길이로 각각 바꾸는 방식으로 본 코드 변경 없이 두 거짓 조건을 시험할 수 있다. 실제 스트림 추가를 재현하는 시험과는 구분해야 한다. 이번에는 검토만 수행했고 새 시험을 구현하거나 실행하지 않았다.

## EAGAIN 시뮬레이터 통합 검증 결과

- RECORDER_EAGAIN을 별도 Harness 프로세스에 구현하고 TC-14의 두 번째 단계로 연결했다. 기존 MjpegAviWriter를 소스 링크로 재사용한다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false` 성공, 경고/오류 0개.
- `dotnet build tools/EzStream.CoverageTool/EzStream.CoverageTool.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 성공, 경고/오류 0개.
- `dotnet-coverage collect --server-mode --background`로 별도 검증 세션을 시작하고 `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RECORDER_EAGAIN` 실행 결과 `OK|RECORDER_EAGAIN 완료`를 확인했다. 세션 종료 및 XML 변환도 성공했다.
- artifacts/eagain-integration/result.xml의 SourceRecorder.RunOnce 185/186/187행은 모두 covered=yes이다. 시나리오 내부에서 EAGAIN 정확히 1회, 이후 양수 기록 바이트 및 패킷, 바인딩 원복, 저장 MP4의 정상 EOF 및 패킷 수 일치를 검증한다.
- 시작 전후 SHA256 비교로 추적 src 파일과 기존 제품 DLL/PDB 등 53개 파일의 불변을 확인했다. 기존 사용자 변경은 유지했다. git diff --check 통과.
- 전체 UI 시험을 직접 재실행하지는 않았다. TC-01~19 전체 실행이 TC-14를 호출하는 기존 경로에 새 단계를 추가했으며, 실행에 사용하는 동일한 collector connect 방식으로 새 검사를 검증했다.
- 실제 네트워크 장애 시험과 별개로 FFmpeg 반환값을 시험 프로세스에서 제어한다. 비공개 바인딩 필드에 의존하므로 FFmpeg.AutoGen 버전 변경 시 재검증이 필요하다.

## EAGAIN 시뮬레이터 통합 시작

- 사용자 승인에 따라 TC-14 기존 입력 중단 검사와 함께 EAGAIN 재시도 검사를 실행하도록 연결한다. 따라서 TC-01~19 전체 순차 실행에도 포함된다.
- src/EzStream.Core/Ffmpeg/FfmpegLoader.cs 및 EzStream.sln의 기존 사용자 변경을 유지한다. 제품 소스/제품 DLL은 변경하지 않고 도구 빌드에 BuildProjectReferences=false를 지정한다.

## 본 코드 변경 없는 EAGAIN 검증 시작

- 사용자 요청은 시뮬레이터만으로 재현 가능한지 실제 확인이다. src 및 기존 제품 DLL은 수정/재빌드하지 않는다. 사용자 EzStream.sln/FfmpegLoader.cs 변경을 보존한다.
- 시험 프로세스에 로드된 FFmpeg.AutoGen의 함수 바인딩만 일시 교체할 수 있는지 조사하고, 가능하면 기존 SourceRecorder DLL로 검증한다.

## 본 코드 변경 없는 EAGAIN 검증 결과

- 설치된 FFmpeg.AutoGen 4.4.1.1의 ffmpeg.av_read_frame_fptr는 비공개 static delegate 필드이며 readonly가 아니다. reflection으로 시험 프로세스 메모리 안에서만 바인딩을 교체할 수 있음을 확인했다.
- artifacts/eagain-probe에 별도 .NET 9 검증 실행기를 만들었다. ProjectReference 대신 기존 제품 DLL을 Reference로 읽어 제품 소스 및 바이너리를 재빌드하지 않았다. 기존 MjpegAviWriter 소스를 링크해 80프레임 AVI를 생성했다.
- 실제 SourceRecorder.RunOnce를 호출하면서 첫 av_read_frame만 EAGAIN을 반환하고 이후 호출은 기존 avformat-58.dll의 실제 av_read_frame으로 전달했다. ffmpeg 라이브러리 자체나 제품 DLL을 패치하지 않았다. 바인딩은 finally에서 기존 값으로 복구한다.
- `dotnet build artifacts/eagain-probe/Probe.csproj -c Debug` 성공, 경고/오류 0개.
- `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/eagain-probe/eagain.coverage -f coverage artifacts/eagain-probe/bin/Debug/net9.0-windows/Probe.exe artifacts/eagain-probe/media ffmpeg` 성공.
- 관측 결과 EAGAIN 주입 1회, 후속 네이티브 읽기 81회, 정상 패킷 80개, 기록 바이트 276399. 바인딩 복원 후 실제 FFmpeg로 완성된 MP4를 다시 읽어 80개 패킷 확인.
- `dotnet-coverage merge artifacts/eagain-probe/eagain.coverage -o artifacts/eagain-probe/eagain.xml -f xml` 성공. 기존 미달성 RunOnce 185/186/187행 모두 covered=yes.
- 시작 전 기록한 git 추적 src 파일 전체와 사용 제품 Core DLL/PDB 및 FFmpeg.AutoGen.dll의 SHA256을 시험 후 비교해 모두 불변을 확인했다. 사용자 수정 EzStream.sln/FfmpegLoader.cs도 유지했다.
- 결론은 현재 바인딩 버전에서 시뮬레이터/Harness만 변경하여 EAGAIN 분기 재현 가능이다. 실제 네트워크 지연을 재현한 것이 아니라 FFmpeg의 허용 반환값을 시험 프로세스에서 제어한 오류 주입 시험이다.
- 이번 요청은 가능 여부 확인이므로 기존 시뮬레이터 TC에는 아직 연결하지 않았다. 통합할 경우 별도 Harness 프로세스에서 수행하고 바인딩 복원 및 후속 패킷/저장 검증을 유지해야 한다. 비공개 바인딩 필드를 사용하므로 FFmpeg.AutoGen 버전 변경 시 재확인이 필요하다.

## SourceRecorder 패킷 재시도 검토 시작

- 사용자 인용 블록은 RunOnce에 두 번 있다. 결과.xml에서 EAGAIN 블록(185~187행)은 미달성, outIdx < 0 블록(201~203행)은 달성이다. 따라서 EAGAIN을 주 대상으로 검토하고 둘의 차이를 설명한다.
- SourceRecorder.cs 체크섬은 결과.xml과 일치한다. 시작 시 EzStream.sln 및 FfmpegLoader.cs에 사용자 변경이 있어 보존한다.

## SourceRecorder 패킷 재시도 검토 결과

- XML 파싱 결과 RunOnce 블록 60/67(89.55%). EAGAIN 분기의 185~187행은 미달성이고 outIdx < 0의 201~203행은 달성이다. SourceRecorder SHA256은 `7903322A1214FDC1E59B5B21B14E9500A46EB377FDF7B9F04F34487CDB0FB108`로 XML과 일치한다.
- av_read_frame의 EAGAIN은 C# 예외가 아니라 현재 패킷을 반환하지 못했으니 다시 시도하라는 음수 반환값이다. continue가 이를 일반 ret < 0 처리와 구분한다. 블록 전체를 삭제하면 일시적 상태를 입력 종료/오류로 처리하여 출력 종료 및 재접속 백오프로 넘어갈 수 있다.
- FFmpeg 4.4 utils.c의 ff_read_packet/read_frame_internal/av_read_frame는 EAGAIN 반환을 전파하는 경로가 있다. 해당 값은 허용되는 API 결과이므로 null 버퍼 분기와 같은 논리적 도달 불가로 판단할 수 없다.
- OpenInput은 AVFMT_FLAG_NONBLOCK을 설정하지 않으며 rtsp는 TCP, rtspu 접두사는 UDP 옵션을 지정한다. 파일 입력 및 현재 기본 설정에서 EAGAIN이 자주 반환될 것으로 단정할 근거는 없다. 네트워크 지연이 FFmpeg 내부 재시도나 타임아웃으로 처리될 수 있어 송신 지연만으로 이 C# 분기가 달성된다고 보장할 수 없다. FFmpeg 내부 EAGAIN과 av_read_frame 외부 반환은 구분해야 한다.
- av_packet_unref는 패킷 참조/내용을 정리하고 재사용 가능한 상태로 돌리는 호출이며 pkt 객체 자체는 finally의 av_packet_free에서 해제한다. FFmpeg 내부도 오류 반환 전에 패킷을 정리하는 경로가 있으므로 이 한 호출을 제거하면 반드시 누수한다고 주장하지 않는다. 핵심은 EAGAIN 재시도 의미를 보존하는 것이다.
- 재현 전략은 실제 지원 입력에서 av_read_frame 반환값을 관측하는 통합 시험 또는 시험용 읽기 호출 대체로 EAGAIN을 한 번 반환한 뒤 정상 읽기를 계속하는 오류 주입 시험이다. 후자는 재시도 동작 검증이지 실제 카메라가 그 상태를 생성함을 증명하는 시험은 아니다. 현재 시뮬레이터에는 해당 강제 반환 시험이 없고 이번 검토에서는 새 재현을 실행하지 않았다.
- 권장 사항은 분기 유지 및 시험 보완이다. 현 자료만으로 검사 제외를 권장하지 않는다. 특정 지원 프로토콜·FFmpeg 빌드에서 외부 EAGAIN 반환이 불가능함을 입증한 경우에만 한정된 제외 근거를 검토할 수 있다. 운영 통계가 없어 발생 확률 수치는 제시하지 않는다.
- 수행 검증은 XML/소스 체크섬, 코드 및 FFmpeg 4.4 공식 소스 대조다. 제품 코드를 변경하지 않아 새 빌드/테스트는 실행하지 않았다.
- 공식 근거.
  - https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavformat/utils.c
  - https://ffmpeg.org/doxygen/4.4/rtsp_8c_source.html

## FFmpeg null 조건 제거 시작

- 사용자가 검토된 변경을 승인했다. 로그 변환의 ?.만 !.로 바꾸고 빈 문자열 처리는 유지한다. 기존 사용자 EzStream.sln 변경을 보존한다.

## FFmpeg null 조건 제거 검증 결과

- FfmpegLoader 로그 변환을 `Marshal.PtrToStringAnsi((IntPtr)lineBuffer)!.TrimEnd()`로 변경했다. stackalloc 버퍼 근거 주석을 추가했고 다음 줄의 빈 문자열 검사는 유지했다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore` 성공. Core/Service/Tray/Harness Debug 출력 갱신, 경고/오류 0개.
- `dotnet test tests/EzStream.Core.Tests/EzStream.Core.Tests.csproj -c Debug --no-restore` 성공. 통과 28, 실패 0, 건너뜀 0.
- `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/ffmpeg-null-update/residual.coverage -f coverage tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RESIDUAL_BRANCHES` 성공.
- 바이너리 결과를 residual.xml로 변환해 변경한 44행 covered=yes, 빈 문자열 반환 45행 covered=yes를 확인했다. 콜백 전체는 해당 단일 시나리오에서 블록 66.67%이며, 이번 확인은 변경 줄의 부분 달성 해소에 대한 것이다. 전체 콜백/전체 UI 100%를 재검증한 것은 아니다.
- Service/Tray/Harness의 Core DLL/PDB GUID가 각각 일치하고 세 폴더의 DLL/PDB 해시도 동일하다. 증거는 artifacts/ffmpeg-null-update/symbol-check.json이다.
- `git diff --check` 통과. 기존 결과.xml은 보존했고 개발 Debug 출력만 갱신했다. 새 Core 빌드는 모듈 식별자가 달라지므로 기존 수집 결과에 섞지 말고 새 전체시험 회차에서 검사한다.

## FFmpeg 로그 콜백 null 분기 검토 결과

- 결과.xml의 FfmpegLoader.cs SHA256은 현재 파일과 일치한다. 로그 콜백은 블록 16/17(94.12%), 줄 90.91%이고 43행만 partial이다. 초기화 본체는 100%다.
- 43행의 ?.는 PtrToStringAnsi가 null이면 TrimEnd를 건너뛰는 분기를 만든다. 입력 포인터는 직전 stackalloc byte[1024]로 확보한 버퍼이며 외부 입력 포인터가 아니다. 유효한 실행에서 이 포인터가 null/Win32 atom 값이 될 수 없으므로 null 반환 분기는 도달 불가로 판단한다.
- 공식 Marshal 구현은 null/Win32 atom 포인터에 null을 반환하고 그 외에는 해당 주소에서 문자열을 생성한다. 빈 C 문자열은 null이 아니라 string.Empty다. 네이티브 오류/메모리 손상을 null 반환으로 안전하게 처리해 주는 API가 아니므로 ?.를 그 방어책으로 해석하면 안 된다.
- FFmpeg 4.4 av_log_format_line은 av_log_format_line2를 호출하며 후자는 제공된 버퍼에 snprintf로 문자열을 기록한다. 버퍼 내용이 비어도 포인터 자체는 바뀌지 않는다.
- 기존 Harness의 ExerciseFfmpegCallbackBranches는 빈 format으로 콜백을 호출한다. XML에서 44행 빈 문자열 return 경로는 이미 달성이다. 빈 로그 추가로 43행 null 분기를 달성할 수 없다.
- 임시 .NET 9 실행기를 `artifacts/ffmpeg-null-review`에 작성해 현재 ffmpeg/avutil-56.dll로 같은 stackalloc → av_log_format_line → PtrToStringAnsi → TrimEnd 경로를 검증했다. 제품 코드는 변경하지 않았다.
- `dotnet build artifacts/ffmpeg-null-review/Probe.csproj -c Debug` 성공, 경고/오류 0개. `artifacts/ffmpeg-null-review/bin/Debug/net9.0-windows/Probe.exe ffmpeg` 성공. 빈 입력, 공백/개행, 일반 문구 모두 null=False이며 TrimEnd 결과는 예상과 일치했다. 대조군 IntPtr.Zero만 null을 반환했다.
- 권장 변경안은 근거 주석과 함께 `Marshal.PtrToStringAnsi((IntPtr)lineBuffer)!.TrimEnd()`로 불필요한 null 조건부 분기를 제거하는 것이다. !는 컴파일러 null 분석 표기이며 런타임 검사가 아니다. 다음 줄의 빈 문자열 검사는 유지한다. 같은 파일 ErrorString에도 stackalloc 버퍼의 변환 결과에 !를 사용하는 선례가 있다.
- 소스 변경을 원하지 않으면 해당 null 분기만 도달 불가 근거로 검사 예외를 검토할 수 있다. 전체 로그 콜백을 검사에서 제외하는 것은 부적절하다. 외부 검사 규정의 예외 승인은 별도이며 이 검토가 승인을 대신하지 않는다.
- 정상 호출 계약을 깨서 포인터를 0으로 바꾸거나 Marshal을 가짜 구현으로 대체하는 것은 현 제품 경로의 의미 있는 재현이 아니다. 제품 소스 및 검사 제외 설정은 변경하지 않았다.
- 공식 근거.
  - https://source.dot.net/System.Private.CoreLib/src/runtime/src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/Marshal.cs.html
  - https://ffmpeg.org/doxygen/4.4/log_8c_source.html

## Host 정상 종료 시험 구현 시작

- 사용자가 유사 시험과 함께 실행하도록 요청하여 TC-20에 정상 Host 종료 시나리오를 추가한다. 실제 서비스/비콘솔 시험 종료 후 실행해 파이프 충돌을 피한다.
- 실제 Program 엔트리포인트를 호출하고 HostBuilt 진단 이벤트로 정상 종료를 요청한다. Worker를 대체하거나 제품에 테스트 종료 옵션을 추가하지 않는다. 기존 서비스 세션으로 수집하므로 새 병합 파일은 필요하지 않다.

## Host 정상 종료 시험 구현 결과

- ServiceHostCoverageScenario가 Service 어셈블리의 실제 EntryPoint를 실행한다. HostBuilt에서 IHostApplicationLifetime을 얻고 ApplicationStarted 콜백에서 StopApplication을 요청한다. ApplicationStarted/ApplicationStopped 및 EntryPoint 정상 반환을 모두 확인해야 성공이다.
- 실행 제한은 15초이며 시간 초과 시에도 관찰한 Host에 정상 종료를 요청하고 실패 처리한다. 진단 이벤트 구독과 토큰 등록은 시험 종료 시 해제한다.
- TC-20의 기존 비콘솔 시험 다음, IPC 복구 시험 전에 SERVICE_HOST_LIFECYCLE을 실행한다. 기존 Service 세션에 연결되어 최종 service.coverage에 포함된다. Program/Worker 제품 코드는 변경하지 않았다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore` 성공, 경고/오류 0개.
- `dotnet build tools/EzStream.CoverageTool/EzStream.CoverageTool.csproj -c Debug --no-restore -p:CopyRetryCount=0` 성공, 경고/오류 0개.
- `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/host-lifecycle-update/host.coverage -f coverage tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe SERVICE_HOST_LIFECYCLE` 성공. Program 25/26행 모두 covered=yes.
- 고유 시험 세션을 `collect --server-mode --background`로 시작하고 `connect <session> <Harness.exe> SERVICE_HOST_LIFECYCLE`, `connect <session> <Harness.exe> PIPE_ACCEPT_FAILURE`를 실행했다. 둘 다 OK. snapshot 후 shutdown하여 시험 세션을 종료했다. snapshot.xml에서 Run 달성과 AcceptLoop 블록/줄 100% 확인.
- 기존 Results/service.coverage와 새 snapshot.coverage를 별도 artifacts/host-lifecycle-update/combined.coverage로 바이너리 병합 후 XML 변환했다. Program과 AcceptLoop 모두 블록 32/32 및 줄 100%다. 기존 결과 및 결과.xml은 변경하지 않았다.
- `git diff --check` 통과. 전체 TC-20 UI 흐름/SCM 서비스 중지는 직접 실행하지 않았다. 검증 당시 기존 설정의 sources는 0개였고 실제 Host는 시작 직후 정상 종료했다. 개발 Debug 실행파일을 갱신했으며 ZIP/배포본은 이번 대상이 아니다.

## Program Build/Run 검토

- 결과.xml의 Program.cs SHA256 `408DC3428961243E407262D9D3EE2830B45C3C921DFD40DD9B54D96A126A07FD`가 현재 소스와 일치한다. XML 파싱 및 Get-FileHash로 확인했다.
- 25행 builder.Build()는 covered=yes, 26행 host.Run()은 covered=partial이다. Main은 블록 31/32, 줄 92.86%다. Build 실패를 재현해야 한다는 뜻이 아니다.
- .NET v9.0.0 HostingAbstractionsHostExtensions 구현에서 Run은 RunAsync를 동기 대기한다. RunAsync는 StartAsync, WaitForShutdownAsync를 수행하고 finally에서 Host를 Dispose한다. 정상 종료는 StopApplication 또는 수명주기의 종료 신호를 통해 대기를 해제하고 StopAsync 및 반환까지 이어진다.
- ProductSessionLauncher.StopExistingProductAsync 225행은 Process.Kill(entireProcessTree:true)를 사용한다. 정상 종료 제어 흐름을 실행하지 않는다. RunServiceWithoutConsoleForCoverageAsync 역시 이 종료 함수를 사용한다.
- RunServiceLifecycle은 Worker/Engine/Pipe를 직접 구성하고 Worker.StopAsync를 호출하며 Program의 실제 host.Run을 실행하지 않는다. 따라서 해당 테스트를 실행해도 Program의 정상 반환 검증을 대체하지 못한다.
- 정상 종료 반환 경로 누락이 부분 달성의 유력 원인이다. XML에 IL 블록 오프셋이 없어 정확히 어떤 숨은 반환/정리 블록인지 단정하지 않는다. Program의 using var fileLoggerProvider 정리 코드도 정상 반환과 관련된다.
- 정상 종료는 실제 운영 가능한 필수 경로이므로 제외 근거가 없다. 실제 서비스로 수집 중이면 SCM Stop을, 콘솔 프로세스면 대상 콘솔의 Ctrl+C를 사용하고 실제 프로세스 정상 종료 후 수집 결과를 비교해야 한다. Harness가 실제 엔트리포인트를 실행하면서 Host를 관찰하고 IHostApplicationLifetime.StopApplication을 요청하는 방법도 가능하지만 구현 및 별도 검증이 필요하다.
- --console은 이 소스에서 콘솔 로거 추가 여부만 바꾼다. AddWindowsService는 실제 Windows Service 실행 문맥에서만 WindowsServiceLifetime을 등록한다. 옵션을 생략해 EXE를 직접 실행하는 것은 SCM 서비스 종료 시험과 같지 않다.
- 제품 코드 및 시뮬레이터는 이번 검토에서 수정하지 않았고 서비스 실행/종료 재현도 하지 않았다. 읽기 전용 소스·XML 검사 및 공식 .NET 9 구현 대조를 수행했다.
- 공식 근거.
  - https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/Microsoft.Extensions.Hosting.Abstractions/src/HostingAbstractionsHostExtensions.cs
  - https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/Microsoft.Extensions.Hosting.WindowsServices/src/WindowsServiceLifetimeHostBuilderExtensions.cs

- 요청은 시뮬레이터에서 Tray/Service 대비 Core 동적 검사 결과가 적은 이유 조사이다.
- 시작 시 `EzStream.sln`에 사용자 변경이 있어 수정하지 않는다.
- Core는 독립 실행 프로그램이 아닌 라이브러리이며 Harness는 Core/Service/Tray를 모두 참조한다.
- Coverage.runsettings는 Harness와 CoverageTool만 제외하며 Core 제외 규칙은 없다.
- Service와 Tray 세션을 별도로 수집하고 Core 전용 결과 파일은 생성하지 않는다. 실제 모듈별 결과를 추가 확인한다.

## 2026-10-04 조사 결과

- 오늘 15:33~15:34 생성된 `tools/EzStream.CoverageTool/bin/Debug/net9.0-windows/win-x64/Results` 결과를 원본 변경 없이 XML로 변환했다.
- Service 최종 결과의 블록 커버리지는 Service 99.07%, Core 11.47%다. Tray 최종 결과는 Tray 100%, Core 7.11%다.
- Service 체크포인트와 Live 결과에는 Core 모듈이 전혀 없다. 최종 Core 11.47%는 독립 실행한 `service_TC20_lifecycle.coverage`에서 들어온 값과 동일하다. SourceRecorder 21개 함수의 실행 블록 합계는 0이다.
- 개발 경로 Service 폴더의 Core DLL은 10월 1일 파일이고 PDB는 10월 2일 파일이다. PE CodeView GUID와 Portable PDB ID를 직접 비교해 불일치를 확인했다.
  - DLL이 요구하는 PDB GUID `3a9d5bc6-13a2-4396-960a-fe9316d28a44`.
  - 실제 Service 폴더 PDB GUID `55240f59-ffae-43ca-9f4e-5f0639a3c44c`.
  - Harness는 이전 DLL과 이전 PDB가 일치한다. Tray는 새 DLL과 새 PDB가 일치한다.
- 개발 실행 경로는 제품마다 Debug/Release 폴더에서 따로 찾으므로 서로 다른 빌드 산출물이 함께 사용될 수 있다. 배포 검증도 현재 DLL/PDB 일치 여부는 검사하지 않는다.

## 실제 검증

설치된 `dotnet-coverage` 18.8.0으로 다음 검증을 수행했다. 결과는 `artifacts/core-coverage-investigation`에 있다.

1. `dotnet-coverage merge <기존 결과.coverage> -o <조사 결과.xml> -f xml`로 Service/Tray 및 중간 결과 6개 변환 성공.
2. `System.Reflection.PortableExecutable.PEReader`와 `MetadataReaderProvider.FromPortablePdbStream`으로 Service/Tray/Harness Core DLL/PDB GUID 비교. Service만 불일치.
3. `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o <결과.xml> -f xml <Harness.exe> RETENTION_ZERO`를 정상 Harness와 별도 복사본의 PDB만 Service의 불일치 PDB로 바꾼 조건에서 각각 실행. 양쪽 모두 `OK|RETENTION_ZERO 완료`지만 정상 조건은 Core 모듈 1개 및 0.44%, 불일치 조건은 수집 모듈 0개. 이 비율은 단일 시나리오 최소 재현의 수치다.
4. 고유 세션 ID로 `collect --server-mode --background` → `connect <불일치 복사본> RETENTION_ZERO` → `connect <정상 Harness> RETENTION_ZERO` → `shutdown` 실행. 두 시나리오 성공에도 최종 수집 모듈 0개. 최초 불일치 모듈로 시작한 세션에서는 이후 정상 복사본을 실행해도 해당 모듈 수집이 회복되지 않는 현상을 재현했다. 조사 세션은 종료했다.

## 판단 및 권장 조치

Core 테스트 부족보다 DLL/PDB 불일치에 따른 주 세션 계측 누락이 이번 결과의 직접 원인이다. 독립 종료 시험은 새 수집 세션과 정상 Harness DLL/PDB를 사용하여 일부 Core 결과만 남는다.

Service/Tray/Harness를 같은 빌드에서 생성하고 Core DLL/PDB를 함께 배포한 뒤 기존 수집 세션을 종료하고 새 회차로 전체 시험을 실행해야 한다. 기존 결과를 다시 병합해도 기록되지 않은 실행 정보는 복구되지 않는다.

요청 범위는 조사이므로 제품 소스, 원본 실행 파일, 기존 커버리지 및 사용자 수정 `EzStream.sln`을 변경하지 않았다. 전체 시뮬레이터 재시험이나 산출물 복구는 수행하지 않았다. 불일치 파일이 생긴 구체적인 과거 복사/빌드 작업까지는 확인할 수 없다.

## 후속 빌드 시작

- 사용자가 빌드를 요청하여 기존 Debug 개발 실행 경로를 Rebuild한다. Harness 프로젝트 참조로 Core/Service/Tray를 함께 빌드하며 공유 출력 경로 충돌 방지를 위해 빌드 명령을 순서대로 실행한다.
- 시작 시 실행 중인 EzStream 프로세스는 없었다. 기존 결과 파일은 보존하고 검증 결과는 별도 artifacts 경로에 저장한다.

## 후속 빌드 검증 결과

- 최초 제한 환경 빌드는 Windows SDK 경로 접근 거부로 실패했고, 권한 확장 후 `--no-restore` 빌드는 기존 NuGet 자산의 패키지 누락으로 실패했다. 복원을 포함한 아래 명령으로 해결했다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug -t:Rebuild` 성공. Core/Service/Tray/Harness 모두 생성, 경고 0개 및 오류 0개.
- `dotnet build tools/EzStream.CoverageTool/EzStream.CoverageTool.csproj -c Debug -t:Rebuild` 성공. 경고 0개 및 오류 0개.
- `dotnet test tests/EzStream.Core.Tests/EzStream.Core.Tests.csproj -c Debug` 성공. 통과 28개, 실패 0개, 건너뜀 0개.
- Service/Tray/Harness의 Core DLL/PDB GUID가 모두 `4a850fd2-875c-43e4-af4c-1dbede7f4d65`로 일치하고 DLL/PDB 각각의 SHA256도 세 폴더에서 동일하다. 증거는 `artifacts/core-coverage-rebuild/symbol-check.json`이다.
- `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/core-coverage-rebuild/retention.xml -f xml tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RETENTION_ZERO` 성공. Core 6개 블록 수집을 확인했다. 0.44%는 이 최소 시나리오만의 값이다.
- 실행 파일은 `tools/EzStream.CoverageTool/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageTool.exe`이다. 기존 deliverables/ZIP은 이번 개발 빌드 대상이 아니다. 전체 시뮬레이터 시험은 아직 실행하지 않았으며 새로 빌드된 도구에서 새 전체시험 회차로 진행해야 한다.

## PipeServer 검토 시작

- 후속 요청으로 정식 시뮬레이터의 기존 유사 검사에 재현 시나리오를 통합한다. TC-20의 PIPE_ACCEPT_FAILURE를 확장하며 제품 방어 코드 및 검사 제외 설정은 유지한다.

## 시뮬레이터 IPC 검사 반영 결과

- TC-20의 기존 IPC 검사에서 cancel-wait, disconnect-retry, cancel-delay를 순서대로 실행한다. 새 버튼은 추가하지 않았다.
- 기존 reflection 기반 Harness 스타일에 맞춰 실제 AcceptLoop를 호출하고 Stop이 사용하는 토큰/Task를 연결했다. 이는 Task.Run 스케줄링 전에 취소되어 루프에 진입하지 않는 경쟁을 피한다.
- 로그 이벤트로 실제 파이프 오류를 확인한다. 복구 시험은 750ms 후 GET_STATUS 응답의 Ok와 Status를 확인한다. 지연 취소 시험은 오류 로그 콜백에서 토큰을 취소해 Task.Delay 취소 처리를 확정적으로 실행한다.
- 각 시험은 루프가 정상 완료했는지 직접 검사하므로 Stop에서 예외를 삼켜도 성공으로 오인하지 않는다. 사용하지 않게 된 PipeAcceptErrors 카운터는 제거했다.
- 초기 빌드의 CA2000 분석 오류는 각 시나리오의 IDisposable 수명을 별도 함수로 분리해 해결했다. 최종 Harness와 CoverageTool 빌드는 경고 0개, 오류 0개다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore` 통과.
- `dotnet build tools/EzStream.CoverageTool/EzStream.CoverageTool.csproj -c Debug --no-restore` 통과. 기존 실행 중인 시뮬레이터 창이 EXE를 잠가 최초 복사가 실패하여 정상 창 닫기를 요청한 후 같은 경로로 빌드했다. 강제 종료하지 않았다.
- `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/pipe-accept-update/pipe.coverage -f coverage tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe PIPE_ACCEPT_FAILURE` 통과.
- 바이너리 결과를 `dotnet-coverage merge artifacts/pipe-accept-update/pipe.coverage -o artifacts/pipe-accept-update/pipe.xml -f xml`로 변환한 결과 AcceptLoop 블록 32/32, 줄 100%를 확인했다. 기존 결과와 합치지 않은 단일 시나리오 결과다.
- `git diff --check` 통과. 제품 PipeServer 및 사용자 EzStream.sln 수정은 건드리지 않았다. 전체 TC-01~22 UI 시험은 재실행하지 않았다. 개발 Debug 빌드만 갱신했고 기존 ZIP/배포본은 갱신하지 않았다.

- 사용자가 루트 `결과.xml` 기준으로 AcceptLoop 미달성 예외 경로의 현실성, 제외 및 재현 가능성을 요청했다. 확률을 산출할 운영 통계는 없으므로 조건 기반 정성 평가로 설명한다.
- 기존 `EzStream.sln` 변경은 보존한다. 검사 제외나 제품 코드 변경은 이번 검토 범위에 포함하지 않는다.

## PipeServer 검토 결과

- `결과.xml`의 PipeServer 소스 SHA256은 현재 파일과 동일하다. `AcceptLoop` 상태 머신은 블록 29/32(90.63%), 줄 87.50%이며 미달성 줄은 49, 56, 61이다.
- 49행은 연결/요청 대기 중 취소를 정상 종료로 처리하는 경로다. `Stop()`이 토큰을 취소하고 Worker.StopAsync가 이를 호출한다. 정상 서비스 종료에서도 충분히 발생하므로 희귀 장애나 도달 불가 코드로 제외하는 것은 부적절하다.
- 56/61행은 별도의 예외가 아니라 일반 예외 이후 500ms 지연이 취소 없이 완료되어 재시도하는 경로의 닫는 중괄호 위치다. 클라이언트가 응답을 받기 전에 연결을 끊으면 실제 IOException이 발생한다. 장애 빈도는 통계 없이는 수치화할 수 없으나, 오류 후 서비스가 계속 동작하면 자연스럽게 실행되는 복구 경로다.
- 기존 `RunPipeAcceptFailure`는 로그에서 오류 확인 즉시 `pipe.Stop()`을 호출하여 지연 중 취소(57~59행)를 달성하지만, 지연 완료와 재시도는 검증하지 않는다. 이 테스트 설계가 해당 미달성과 일치한다.
- 50행 예외 필터, 52행 오류 로그, 55행 지연 진입, 57~59행 지연 중 취소, 64행 DisposeAsync는 기존 XML에서 이미 달성이다. 이번 미달성은 OOM/StackOverflow/AccessViolation 재현 문제가 아니다. 치명적 예외 필터의 모든 논리 분기가 검증되었다고 단정할 수는 없으며, 보고서 수치는 블록/줄 커버리지다.
- CreateServer는 try 밖이고 DisposeAsync는 finally 안이므로 여기에서 발생한 예외를 일반 catch가 처리한다고 해석하면 안 된다. 이번 미달성 원인과는 별개다.

## PipeServer 실제 재현 및 한계

- 제품 DLL을 참조하는 임시 실행기를 `artifacts/pipe-accept-review/Program.cs`에 작성했다. 비공개 AcceptLoop를 reflection으로 호출하되 실제 Named Pipe와 CancellationToken을 사용했으며 강제로 예외를 던지거나 제품 소스를 바꾸지 않았다. 녹화 엔진은 시작하지 않고 상태 조회만 했다.
- `dotnet build artifacts/pipe-accept-review/Probe.csproj -c Debug --no-restore -p:EnableNETAnalyzers=false` 성공, 경고/오류 0개. 임시 실행기의 스타일 분석만 끈 것으로 제품이나 커버리지 제외 설정과 무관하다.
- 처음 격리 환경 실행은 cancel-wait 통과 후 클라이언트 연결 권한 거부로 실패했다. 로컬 파이프 접근을 허용한 재실행은 아래 두 시나리오 모두 성공했다.
- `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/pipe-accept-review/reproduced.xml -f xml artifacts/pipe-accept-review/bin/Debug/net9.0-windows/Probe.exe` 성공.
  - `PASS cancel-wait`. 연결 대기 중 취소 후 루프가 정상 완료했다.
  - `Observed: IOException: Pipe is broken.` 이후 750ms 기다린 뒤 재접속하고 GET_STATUS 응답 OK를 검증했다. `PASS disconnect-retry`.
- 원본과 재현 XML의 Service 모듈 ID는 모두 `3815D4DD81B40E42B7CB94C5745E4F38D59C9592`다. 재현 결과에서 49/56/61행이 모두 covered=yes이며 AcceptLoop 블록은 31/32(96.88%)다. 재현 시험만으로는 기존 시험이 달성한 지연 중 취소 경로가 빠지므로 단독 100%는 아니다.
- `dotnet-coverage merge 결과.xml artifacts/pipe-accept-review/reproduced.xml -o artifacts/pipe-accept-review/combined.xml -f xml` 성공. 병합 결과 AcceptLoop 줄 커버리지 100%. XML 재입력 병합은 블록 수를 0/0으로 출력하므로 이 파일의 블록 백분율은 판단 근거로 쓰지 않는다. 원본 결과.xml은 보존했다.
- 권장 사항은 제외가 아니라 정식 Harness에 연결 대기 중 정상 종료 시험과 오류 후 재접속 성공 시험을 별도로 추가하는 것이다. 현재 검토에서는 정식 Harness 변경을 하지 않았다. 실제 운영 장애 확률 및 장시간 반복 안정성은 검증하지 않았다.
- 공식 API 근거.
  - https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeserverstream.waitforconnectionasync?view=net-9.0
  - https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-9.0
