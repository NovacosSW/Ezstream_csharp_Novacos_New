# 조사 맥락

## TC-11 파일 크기 조회 경쟁 시험 구현

- 사용자 요청에 따라 시뮬레이터에 추가한다. 임시 파일을 두 이름 사이에서 이동해 존재 검사와 크기 조회 사이 실제 파일 소실을 유도한다. 5초 내 미재현은 명시적인 시험 실패이며 제품 결함으로 단정하지 않는다. 본 코드 수정이나 시스템 파일 API 가로채기는 사용하지 않는다.
- RecorderFileInfoCoverageScenario는 전용 4바이트 임시 파일로 정상 크기·누락 파일 알림을 먼저 확인한다. 파일 이동 작업과 실제 ReportClosedSegment를 병행해 file_info 오류, Success=false, FileSizeBytes=0 및 세그먼트 상태 초기화를 검사한다. finally에서 이동 작업을 종료·대기한 뒤 두 임시 경로와 전용 디렉터리를 정리한다. TC-11의 별도 단계로 연결해 기존 절단 시험 제한 시간에 더해지지 않도록 했다. 전체 TC01~19 순회에 포함되며 전체 UI 버튼 자체는 실행하지 않았다.
- Harness 첫 빌드는 CA1508 분석기가 리플렉션 호출의 콜백 대입을 추적하지 못해 실패했다. 기존 방식과 같은 알림 List 수집 후 Single로 검증하도록 수정했고 분석기 억제 없이 해결했다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 CoverageTool 동일 명령 모두 경고 0·오류 0으로 통과했다.
- `EzStream.CoverageHarness.exe RECORDER_FILE_INFO_RACE` 연속 5회, `dotnet-coverage connect <session> <Harness.exe> RECORDER_FILE_INFO_RACE` 연속 3회 모두 OK. 수집 XML `artifacts/file-info-integration/result.xml`에서 GetFileSize 491~493행 covered=yes 확인. 필터 제외 예외 3종은 시험하지 않는다.
- 제품 소스·DLL·PDB 53개 SHA256이 실행 전후 동일하며 git diff --check 통과. 테스트 성공률 8/8은 이 환경의 관측 결과이며 다른 환경에서 재현 보장이나 운영 장애 발생 확률을 의미하지 않는다. 루트 결과.xml은 덮어쓰지 않았다.

## 파일 크기 조회 예외 검토

- GetFileSize catch 검토 요청이다. 제품 및 시뮬레이터 코드는 수정하지 않는다.
- File.Exists와 new FileInfo(...).Length는 별도 조회다. Exists가 true인 뒤 삭제·이름 변경·접근권 변경·저장소 장애가 발생하면 Length에서 예외가 발생할 수 있다. 정상 로컬 저장에서는 드문 경쟁 조건으로 판단하되 수치 확률은 근거가 없다. Microsoft File.Exists 문서도 존재 검사와 후속 작업 사이 외부 변경 가능성을 명시한다. https://learn.microsoft.com/en-us/dotnet/api/system.io.file.exists?view=net-9.0 및 https://learn.microsoft.com/en-us/dotnet/api/system.io.fileinfo.length?view=net-9.0 확인.
- 미리 파일을 삭제하거나 잘못된 경로·디렉터리·접근 불가 경로를 전달하면 일반적으로 Exists가 false를 반환하여 catch가 아닌 정상 0 반환으로 끝난다. 파일 내용 독점 잠금만으로 메타데이터 Length 조회 실패를 보장하지 못한다.
- catch는 오류 메시지와 크기 0을 반환한다. ReportClosedSegment는 기존 closeError가 없을 때만 file_info를 채택하고 실패 저장 알림을 보낸다. 제거 시 예외가 보고 흐름을 중단하며 Run finally의 CloseOutput에서 발생하면 뒤 CloseInput도 건너뛸 수 있다. 코드 유지 권장, 도달 불가능 제외 근거 없음.
- 시뮬레이터 전용 임시 파일의 생성·삭제 또는 이름 변경을 병행하며 실제 GetFileSize를 반복 호출하는 경쟁 시험은 가능하지만 Exists와 Length 사이의 짧은 타이밍에 의존하므로 매 실행 성공을 보장할 수 없다. 기존 FFmpeg delegate 교체 방식으로 이 .NET 파일 API 사이에 직접 개입할 수는 없다. 결정적인 시험에는 별도 런타임/파일 API 가로채기 수단이 필요하며 현재 프로젝트에서 그 수단을 확인하거나 재현 실행한 것은 아니다. 제품 변경 금지 조건을 유지한다.
- PowerShell XML 파싱으로 결과.xml GetFileSize의 490~493행 미실행 확인. 이번에는 코드와 문서 검토만 수행했고 동적 재현·빌드는 실행하지 않았다. git diff --check로 문서 변경을 검증한다.

## 출력 IO 닫기 오류 검토

- 요청 범위는 avio_closep 음수 반환 및 closeError null 조건 검토다. 제품 및 시뮬레이터 코드는 수정하지 않는다.
- SourceRecorder 449~451행은 닫기 반환 오류를 확인하되 기존 _segmentWriteError 또는 트레일러 오류를 덮어쓰지 않는다. 이 검사를 제거하면 닫기만 실패하고 크기가 양수인 파일을 성공으로 보고할 수 있다. closeError null 조건만 제거하면 기존 원인이 닫기 오류로 교체된다. 유지·시험을 권장하며 도달 불가능 제외 근거는 없다.
- FFmpeg 4.4 공식 aviobuf.c 1169~1196행에서 avio_closep → avio_close → ffurl_close 반환 경로와 자원 해제·포인터 null 처리를 확인했다. https://ffmpeg.org/doxygen/4.4/aviobuf_8c_source.html . 이 버전 소스는 flush 뒤 s->error를 반환하지 않고 ffurl_close 결과를 반환하므로 디스크 용량 부족만으로 이 분기를 확실히 재현한다고 단정하지 않는다. 정상 로컬 파일 닫기에서 드문 경로로 판단하지만 확률 수치는 근거가 없다. 배포 DLL의 정확한 내부 구현까지 대조한 것은 아니다.
- PowerShell XML 파싱 결과 루트 결과.xml의 450행 partial, 451행 no를 확인했다. 현재 TC-11은 트레일러 바인딩만 주입하고 avio_closep 오류를 주입하지 않는다.
- 시뮬레이터만 수정해 avio_closep 관리 바인딩에서 원래 네이티브 닫기를 먼저 수행하고 오류 반환을 주입할 수 있다. 닫기 오류 단독이면 avio_close 실패 알림, 트레일러 오류와 동시 발생이면 기존 write_trailer 오류 보존을 검증한다. 정상 닫기와 합쳐 단락 평가 세 경로를 검사한다. 파일 잠금 해제·컨텍스트 정리·후속 세그먼트 저장·바인딩 복원도 확인해야 한다. 이는 분기 시험이며 실제 저장장치 장애 재현은 아니다.
- 이번에는 검토 문서만 변경했다. 코드 변경·새 동적 시험·빌드는 수행하지 않았으며 git diff --check로 문서 변경을 검증한다.

## TC-11 트레일러 관리 예외 검사 구현

- 사용자 승인에 따라 기존 TC-11에 관리 바인딩 예외 모드를 추가한다. 실제 네이티브 종료 성공 후 일반 예외를 던지며 제품 코드나 제외 예외 3종의 실제 장애는 건드리지 않는다.
- RecorderCutCoverageScenario에 다섯 번째 모드를 추가했다. 첫 네이티브 트레일러 성공 후 시험용 InvalidOperationException을 던지고 이벤트 35 Warning에 동일 예외 객체가 전달되는지, 저장 실패 알림에 메시지가 담기는지 확인한다. 바인딩 복원 후 두 번째 저장 성공, MP4 4+1 패킷 재읽기, 두 파일 독점 열기, 입력/출력 컨텍스트 null도 검증한다.
- 검증 명령 `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 CoverageTool의 동일 빌드가 각각 경고 0·오류 0으로 통과했다.
- `dotnet-coverage connect <session> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RECORDER_CUT_CONDITIONS`가 OK를 반환했다. 기존 4개 모드와 새 관리 예외 모드를 함께 실행했다. `artifacts/trailer-exception-integration/result.xml`에서 오류 반환 435~438행과 catch 본문 442~445행 모두 covered=yes를 확인했다. 예외 필터의 제외 형식별 분기를 모두 검증한 것은 아니다.
- 제품 소스·DLL·PDB 53개 SHA256 비교가 모두 동일했고 `git diff --check`도 통과했다. TC-11에 연결했으며 전체 UI 시험 버튼 자체는 이번에 실행하지 않았다. 실제 네이티브 장애 발생 확률이나 손상 파일 재현을 증명하는 시험은 아니다.

## 트레일러 관리 예외 catch 검토

- 사용자 요청은 WriteTrailerFailed를 호출하는 관리 예외 catch 검토다. 제품 및 시뮬레이터 코드는 변경하지 않는다.
- try에는 네이티브 호출의 관리 바인딩, 음수 반환 시 ErrorString 변환과 WriteTrailerReturnedError 로그 호출까지 포함된다. FFmpeg의 일반적인 디스크/메모리 오류는 음수 반환이며 catch를 실행하지 않는다. 정상 배포에서 관리 예외는 드문 방어 경로로 판단하되 확률을 수치화하지 않는다.
- 관리 바인딩/함수 해석 실패 또는 try 안의 ILogger 구현에서 던진 예외 등이 가능한 경로다. 초기 FfmpegLoader가 avformat_version으로 DLL을 확인하므로 일반적인 DLL 누락은 보통 앞에서 발견된다. 네이티브 로그 콜백 밖으로 예외를 던지는 방식은 안전한 일반 재현 경로로 취급하지 않는다.
- 필터는 OOM/StackOverflow/AccessViolation 세 형식을 이 catch에서 처리하지 않는다는 의미다. 실제 스택 고갈과 런타임/네이티브 접근 위반은 일반 catch로 복구할 수 없는 경우가 있다. https://learn.microsoft.com/en-us/dotnet/api/system.stackoverflowexception?view=net-9.0 및 https://learn.microsoft.com/en-us/dotnet/api/system.accessviolationexception?view=net-9.0 확인. 단순 new Exception 계열 주입은 실제 프로세스 손상을 재현하지 않는다.
- 보통의 관리 예외를 로그·closeError로 바꾸고 후속 파일/컨텍스트 정리와 실패 알림을 이어가는 목적이 있으므로 유지 및 시험을 권장한다. 코드 근거만으로 catch 전체를 도달 불가능 제외 처리할 수는 없다. 다만 정리가 finally에 있지는 않아 필터 제외 예외나 catch 내부의 로깅 자체가 다시 예외를 던지면 뒤 정리는 보장되지 않는다. 현재 동작의 한계이며 이번 요청에서 수정하지 않는다.
- 결과.xml에서 catch 본문 441~444행 미실행을 확인했다. 최근 추가한 트레일러 오류 시험은 음수 반환만 주입하므로 이 catch를 실행하지 않는다.
- 시뮬레이터만으로 일반 예외 경로를 시험할 수 있다. TC-11 Harness의 관리 av_write_trailer 바인딩에서 원래 네이티브 종료를 정상 수행한 뒤 InvalidOperationException을 던진다. 이는 제품 catch 분기 검증이며 실제 디스크/네이티브 장애 재현은 아니다. WriteTrailerFailed 로그, 예외 메시지가 담긴 실패 저장 알림, 파일 잠금 해제와 컨텍스트 정리를 검증해야 한다. 필터 제외 세 형식의 실제 장애 유발은 필요하지 않다.
- 이번 검증은 소스 및 XML 파싱과 .NET 공식 예외 문서 확인이며 코드 변경/새 시험/빌드는 수행하지 않았다.

## TC-11 트레일러 오류 반환 검사 구현

- 사용자가 승인한 트레일러 오류 반환 검사를 기존 절단 시험에 추가한다. 실제 파일 종료 후 반환값만 오류로 바꾸며 실제 디스크 장애나 손상 MP4 재현으로 해석하지 않는다. 본 코드는 유지한다.
- RECORDER_CUT_CONDITIONS에 네 번째 모드를 추가했다. 첫 절단에서 실제 av_write_trailer 성공을 확인한 뒤 AVERROR_EXTERNAL로 반환한다. 바인딩 복원 후 두 번째 세그먼트 종료는 정상 실행한다. 실패 로그 이벤트 37 Warning 1회, 첫 저장 알림 Success=false 및 write_trailer 오류, 두 번째 저장 알림 성공을 검증한다.
- 양쪽 MP4의 4/1패킷 재읽기, 입출력 컨텍스트 null, 두 파일 배타적 재열기를 확인했다. 첫 파일은 정상 종료 후 반환 오류만 주입했으므로 실제 재생 불가능 파일 검사는 아니다.
- 추가 후 CA1502/CA1506 분석기 제한에 걸려 저장 결과 검증을 VerifySavedSegments로 분리했다. 최종 `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션 CoverageTool 빌드 성공. 경고/오류 0개.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RECORDER_CUT_CONDITIONS` 통과. 기존 시간 만료·주기 변경·출력 열기 실패와 새 트레일러 오류 네 모드를 함께 실행했다. artifacts/trailer-failure-integration/result.xml에서 CloseOutput 435~438행 모두 covered=yes를 확인했다.
- 제품 파일 53개 SHA256 불변, git diff --check 통과. TC-11 및 전체 TC-01~19에 포함되지만 전체 UI 시험 자체는 재실행하지 않았다.

## 출력 트레일러 쓰기 실패 검토

- 사용자 요청은 CloseOutput의 av_write_trailer 음수 반환 블록 검토다. 제품·시뮬레이터 코드는 변경하지 않고 반환 오류와 후속 정리를 확인한다.
- FFmpeg n4.4 mux.c의 av_write_trailer는 남은 필터/인터리브 패킷 기록, muxer 트레일러, 출력 flush 및 pb->error를 통해 음수 오류를 반환할 수 있다. 네이티브 음수 반환은 C# 예외가 아니므로 뒤의 catch로 대체할 수 없다. https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavformat/mux.c .
- 제품은 movflags=+faststart를 설정한다. movenc.c에서 트레일러 중 moov 이동을 위한 추가 버퍼 할당 및 출력 파일 읽기 재열기를 수행하고 실패를 전파한다. 따라서 기존 패킷 쓰기가 성공했어도 종료 시 쓰기/장치 I/O 오류, 메모리 부족, faststart 재열기 실패가 가능하다. https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavformat/movenc.c . 정상 운영 발생 확률을 수치화할 자료는 없다.
- closeError 기록 후에도 avio_closep와 avformat_free_context로 정리를 계속하는 흐름은 적절하다. ReportClosedSegment가 closeError를 받아 파일 크기가 양수여도 Success=false 및 write_trailer 오류를 보고한다. 이 검사를 제거하면 파일이 존재한다는 이유로 저장 성공으로 잘못 알릴 수 있다. 유지 및 시험 권장, 도달 불가능 제외 대상 아님.
- 이 경로는 SetError를 호출하지 않으므로 SourceStatus.LastError 갱신은 하지 않는다. 실제 검증 기준은 WriteTrailerReturnedError 로그와 저장 실패 알림이어야 한다. closeError는 기존 _segmentWriteError보다 트레일러 오류를 우선하며 avio_close 오류는 closeError가 없을 때만 기록한다.
- 결과.xml에서 CloseOutput 435~438행 미실행을 확인했다. 기존 ServiceCoverageScenarios의 로그 함수 직접 호출은 이 블록 재현이 아니다.
- 시뮬레이터만으로 재현 가능하다. TC-11 녹화 종료/절단 시험에서 실제 MP4 기록을 수행한 뒤 별도 Harness의 av_write_trailer 관리 바인딩을 제어해 음수 반환을 주입한다. 원래 함수를 실행해 내부 종료 작업을 마친 뒤 성공 반환값을 오류로 바꾸는 방식은 제품 오류 처리 시험이며 실제 손상 파일/디스크 장애 재현과 구분한다. 로그·실패 알림·파일 잠금 해제·컨텍스트 정리 및 바인딩 복원을 확인하면 된다.
- 이번에는 제품 소스·FFmpeg 구현과 PowerShell XML 파싱으로 검토했으며 새 시험 또는 빌드는 수행하지 않았다.

## TC-12 코덱 파라미터 복사 실패 구현

- 사용자 승인에 따라 TC-12에 복사 실패를 추가한다. 제품은 유지하고 시험 프로세스의 복사 바인딩만 ENOMEM을 반환하도록 제어한다. 실제 메모리 고갈 재현은 아니다.
- 기존 출력 준비 실패 Harness에 Failure 열거형으로 세 모드를 명시하고 PARAMETERS_COPY_FAILURE를 TC-12에 추가했다. 콜백에서 실제 출력 스트림 1개, 대상/원본 codecpar 포인터 일치 및 비디오 입력을 검증한다. 새 대상에 기존 extradata가 없음을 확인하고 구조체 복사 후 extradata=null/크기=0으로 만들어 FFmpeg 4.4 실패 직후 상태를 모사한 뒤 ENOMEM을 반환한다. 실제 네이티브 복사 함수의 메모리 할당 실패를 실행한 것은 아니다.
- 오류 상태 parameters_copy, 실패 저장 알림 정확히 1회(오류 일치·파일 크기 0), MP4 미생성, STOPPED, 입출력 컨텍스트 null, 입력 파일 배타적 재열기 및 바인딩 복원을 검증했다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션 CoverageTool 빌드 성공. 모두 경고/오류 0개.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe PARAMETERS_COPY_FAILURE`와 NEW_STREAM_FAILURE, ALLOC_OUTPUT_FAILURE를 각각 실행하여 모두 통과했다. artifacts/parameters-copy-integration/result.xml에서 대상 369~373행 및 기존 두 실패 블록의 실행 range가 모두 covered=yes임을 확인했다.
- 제품 파일 53개 SHA256 불변, git diff --check 통과. TC-12 및 전체 TC-01~19 실행에 포함되며 전체 UI 시험 자체는 재실행하지 않았다.

## 코덱 파라미터 복사 실패 검토

- 사용자 요청은 parameters_copy 실패 블록 검토다. 제품 및 시뮬레이터 코드 수정 없이 구현과 커버리지를 확인한다.
- FFmpeg n4.4 codec_par.c의 avcodec_parameters_copy는 대상 초기화와 구조체 복사 후 extradata를 별도 할당해 복사한다. src->extradata가 있고 av_mallocz가 실패하면 AVERROR(ENOMEM)을 반환한다. 이 구현의 정상 포인터 조건에서 다른 음수 반환 경로는 없다. https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavcodec/codec_par.c . extradata가 없으면 해당 할당 없이 0을 반환하므로 입력마다 실제 실패 가능 조건이 다르다.
- 이전 출력 스트림 생성 성공은 extradata 복사용 추가 할당 성공을 보장하지 않는다. 일반 운영에서는 드문 네이티브 메모리 오류이며 통계 없이 확률을 수치화하지 않는다. 미지원 코덱/권한/디스크 용량을 이 함수의 직접 실패 원인으로 보지 않는다.
- 검사 제거 시 extradata가 빠진 불완전한 파라미터로 출력 준비를 계속할 수 있으므로 유지하며 검사 제외 대상으로 보지 않는다. SetError와 실패 알림, false 반환은 적절하다. 별도 전용 로그 호출은 없으며 오류 상태/저장 알림으로 보고한다.
- 실패 시 출력 컨텍스트와 생성된 스트림 및 codecpar는 상위 Run finally의 CloseOutput→avformat_free_context에서 해제된다. 실제 FFmpeg 실패 시 목적지 extradata는 null, 크기는 0이다. 입력도 CloseInput으로 정리한다.
- 결과.xml에서 369~373행 covered=no를 확인했다. 기존 NEW_STREAM_FAILURE는 두 번째 스트림 생성에서 먼저 반환하므로 이 오류 분기를 검사하지 않는다.
- TC-12에 복사 실패 모드를 추가해 실제 스트림 생성 이후 관리 바인딩 avcodec_parameters_copy에서 AVERROR(ENOMEM)을 반환하도록 하면 본 코드 수정 없이 제어 흐름을 시험할 수 있다. 이는 제품 실패 처리 주입 검증이며, FFmpeg 내부 extradata 할당 실패 자체를 재현했다는 의미는 아니다. 실제 FFmpeg 내부 실패까지 입증하려면 extradata가 있는 입력과 해당 할당 지점 제어가 별도로 필요하다.
- 이번 검증은 소스·공식 구현 대조와 PowerShell XML 파싱이다. 코드 변경이나 새 동적 실패 주입, 빌드는 수행하지 않았다.

## TC-12 출력 스트림 생성 실패 구현

- 사용자가 유사 검사에 추가하도록 승인했다. 기존 출력 할당 실패 Harness를 확장하고 두 번째 출력 스트림만 실패시키는 모드를 TC-12에 추가한다. 제품 코드는 유지한다.
- NEW_STREAM_FAILURE는 두 비디오 AVI를 사용하고 avformat_new_stream의 관리 바인딩만 교체한다. 첫 호출은 원본 avformat-58.dll 함수로 실행하여 스트림 1개 생성을 확인한다. 두 번째는 첫 비디오 codecpar 설정 및 출력 컨텍스트 동일성을 확인하고 null을 반환한다. 실패 이후 재시도만 중단하고 실제 Run finally를 실행한다.
- 오류 상태 new_stream failed, 실패 저장 알림 정확히 1개(크기 0), MP4 미생성, STOPPED, 입출력 컨텍스트 null, 입력 파일 배타적 재열기와 바인딩 복원을 검증한다. 두 번째 실패 직전 실제 첫 스트림 존재를 확인하고 제품의 avformat_free_context 경로를 거친다. 네이티브 메모리 누수 계측 도구를 추가로 실행한 것은 아니다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션 CoverageTool 빌드 성공. 모두 경고/오류 0개.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe NEW_STREAM_FAILURE`와 ALLOC_OUTPUT_FAILURE를 각각 실행하여 둘 다 통과했다. artifacts/new-stream-integration/result.xml에서 OpenNewSegmentCore 361/363/364/365행 및 기존 339~344행 모두 covered=yes를 확인했다. const 선언 362행은 실행 range가 없다.
- 제품 파일 53개 SHA256 불변, git diff --check 통과. 새 단계는 TC-12 및 전체 TC-01~19 실행에 포함되며 전체 UI 시험 자체는 재실행하지 않았다.

## 출력 스트림 생성 실패 검토

- 사용자가 outStream==null 블록 검토를 요청했다. 이번에는 제품·시뮬레이터 코드를 수정하지 않는다.
- FFmpeg n4.4 utils.c의 avformat_new_stream은 스트림 수 상한 도달 및 스트림 포인터 배열/AVStream/내부 정보/코덱 파라미터 등의 메모리 할당 실패 시 NULL을 반환한다. 출력 컨텍스트 할당 성공과 별도의 할당 단계이므로 앞의 alloc_output 검사로 대체할 수 없다. https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavformat/utils.c . 고정 MP4 여부도 이 실패를 제거하지 않는다.
- 현재 일반적인 소수 비디오 입력에서는 자원 부족이 주요 현실적 원인이며 발생 확률 수치는 산정하지 않는다. 단순히 파일이 손상되거나 권한/디스크 용량에 문제가 있다는 이유로 이 단계가 직접 실패하는 것은 아니다. 실제 파일 열기는 이후 단계다.
- null 검사 제거 시 바로 다음 outStream->codecpar 접근이 잘못된 네이티브 포인터 접근이 되므로 유지해야 하며 도달 불가능으로 제외할 수 없다.
- SetError 및 저장 실패 알림 후 false 반환은 적절하다. 이 블록은 별도 CoreLog 호출이 없고 LastError와 ReportSaveResult로 보고한다. 실패 원인을 특정하는 정수 오류 코드는 반환되지 않으므로 고정 문자열 사용은 합리적이다.
- 출력 컨텍스트와 먼저 만들어진 스트림은 _oc가 소유하고 상위 Run finally의 CloseOutput이 avformat_free_context로 함께 해제한다. 실패한 스트림 자체의 부분 할당은 FFmpeg 내부에서 정리한다. 헤더 작성 전이므로 trailer는 생략하며 입력도 CloseInput으로 정리한다. 미완성 출력의 _currentFile은 설정 전이어서 정리 시 성공 알림을 중복 생성하지 않는다.
- 결과.xml에서 361/363/364/365행 미실행을 확인했다. const 선언은 별도 실행 range가 없다. 기존 ALLOC_OUTPUT_FAILURE는 이 지점 이전에 반환하므로 해당 분기를 검사하지 않는다.
- 후속 시험은 TC-12에 별도 출력 스트림 생성 실패 모드를 추가하는 것이 적절하다. 실제 컨텍스트 생성은 성공시키고 Harness의 관리 바인딩 avformat_new_stream 호출만 null로 반환하여 오류 상태·실패 알림·출력 미생성·입출력 정리를 검증한다. 두 비디오 입력에서 첫 출력 스트림 성공 후 두 번째만 실패시키면 먼저 만들어진 스트림을 가진 컨텍스트의 정리 경로도 확인할 수 있다. 본 코드 변경이나 실제 메모리 고갈 없이 가능하나 이번에는 새 시험을 실행하지 않았다.
- 검증은 소스 대조, 공식 FFmpeg 구현 확인 및 PowerShell XML 파싱이다. 코드 변경이 없어 빌드는 수행하지 않았다.

## 출력 컨텍스트 할당 실패 시험 구현

- 사용자 승인에 따라 TC-12에 출력 컨텍스트 할당 실패 검사를 추가한다. 제품 코드와 기존 주석은 유지한다. 시험 프로세스에서만 함수 바인딩을 교체하며 실제 메모리를 고갈시키지 않는다.
- RecorderAllocOutputFailureScenario를 추가하고 ALLOC_OUTPUT_FAILURE를 TC-12 기존 미지원 코덱 단계 다음에 연결했다. 실제 AVI 입력 준비 및 PREPARED 상태 후 할당 함수 인자를 확인하고 *context=null, AVERROR(ENOMEM)을 반환한다. 첫 실패 뒤 _running=false로 재시도만 중단하며 실제 Run의 finally를 실행한다.
- LastError의 alloc_output 문자열, CannotAllocateOutput 이벤트 30 Error 로그 1회, 실패 저장 알림 1회 및 오류/파일 크기 0, MP4 미생성, STOPPED와 입출력 포인터 null, 입력 파일 배타적 재열기, 바인딩 복원을 검사했다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션의 CoverageTool 빌드 성공. 모두 경고/오류 0개.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe ALLOC_OUTPUT_FAILURE` 통과. artifacts/alloc-output-integration/result.xml에서 OpenNewSegmentCore 339~344행의 모든 range가 covered=yes임을 확인했다.
- 제품 소스 및 기존 제품 DLL/PDB 등 53개 SHA256 불변, git diff --check 통과. TC-12 및 전체 TC-01~19에 포함되지만 전체 UI 시험 자체는 재실행하지 않았다. 실제 메모리 고갈이나 2초 재시도는 검증하지 않았다.

## 출력 컨텍스트 할당 실패 검토

- 사용자 요청은 OpenNewSegmentCore의 alloc_output 오류 블록 검토다. 제품 및 시뮬레이터 코드를 변경하지 않고 FFmpeg 실제 구현과 비교한다.
- FFmpeg n4.4 libavformat/mux.c 127~182행을 확인했다. avformat_alloc_context, muxer private data 할당, filename 복제 실패는 AVERROR(ENOMEM), 출력 형식 탐색 실패는 AVERROR(EINVAL)을 반환한다. https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavformat/mux.c . 이 단계에는 실제 파일 열기/쓰기 작업이 없다.
- 제품은 oformat=null, format="mp4"를 고정 전달한다. 현재 MP4 기록이 가능한 동일 배포 DLL에서 형식 미지원 실패는 통상 발생하지 않지만, MP4 muxer가 빠진 DLL로 교체되면 가능하다. 네이티브 메모리 할당 실패는 여전히 가능하며 .NET OutOfMemoryException이 아니라 음수 오류 반환이다. 일반 운영 발생 확률을 수치화할 근거는 없다.
- ret 검사 제거 시 실패 결과 _oc=null로 다음 스트림 생성/네이티브 호출을 진행할 수 있으므로 유지해야 한다. 오류 상태·CannotAllocateOutput 로그·실패 저장 알림(FileSizeBytes=0)·false 반환은 일관적이다. 실행 중이면 상위 Run에서 입력을 정리하고 재시도한다.
- 사용 버전의 FFmpeg 함수는 처음에 *avctx=NULL로 설정하고 실패 시 내부 컨텍스트를 해제한 뒤 음수를 반환한다. 따라서 기존 "실패 시 oc가 남아 있을 수" 주석은 이 구현의 실제 실패 경로와 맞지 않는다. _oc=oc 대입은 성공 컨텍스트 소유권 이전에 필요하며 현재 코드 오류를 의미하지는 않는다. 주석/본 코드는 수정하지 않았다.
- 결과.xml의 OpenNewSegmentCore 339~344행 covered=no를 확인했다. 도구의 기존 CannotAllocateOutput 직접 로그 호출은 해당 실패 블록 재현이 아니다.
- 판정은 발생 가능한 네이티브 자원 오류이므로 도달 불가능으로 제외할 수 없으며 유지·오류 주입 시험을 권장한다. 별도 Harness에서 avformat_alloc_output_context2 바인딩만 *context=null 및 AVERROR(ENOMEM)으로 반환하도록 하면 본 코드 수정 없이 시험 가능하다. 실제 메모리 고갈 없이 상태·로그·실패 알림·출력 미생성·입력 정리를 확인하는 방법이다. TC-12의 출력 준비 실패 시험과 함께 연결 가능하다.
- 검증은 소스 및 공식 FFmpeg 구현 대조와 PowerShell XML 파싱이다. 이번에는 새 동적 재현·빌드를 하지 않았고 코드 변경은 없다.

## TC-13 실제 스트림 분석 실패 시험 구현

- 사용자 승인에 따라 STREAM_INFO_FAILURE를 실제 입력 열기 후 분석 실패 주입으로 교체한다. 제품 소스/제품 DLL은 수정하지 않고 Harness 및 연결 설명만 수정한다.
- RecorderStreamInfoFailureScenario에서 실제 AVI를 열고 avformat_find_stream_info 바인딩만 AVERROR_INVALIDDATA를 반환하도록 교체한다. 입력 컨텍스트/pb/스트림 존재를 확인한다. 첫 실패 후 자동 재시도만 중단하기 위해 시험 콜백에서 _running=false로 설정하고 실제 Run의 finally를 실행한다.
- LastError의 정확한 find_stream_info 오류, CannotFindStreamInfo 이벤트 28의 Error 로그 1회와 오류 문자열, 출력 디렉터리·파일·저장 알림 미생성, STOPPED, _ic=null 및 입력 파일의 배타적 재열기를 검증한다. 바인딩은 finally에서 복원하고 원본 동일성을 확인한다. 로그만 직접 부르던 두 기존 보조 메서드는 교체 후 삭제했다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션의 CoverageTool 빌드 성공. 경고/오류 0개.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe STREAM_INFO_FAILURE` 통과. artifacts/stream-info-integration/result.xml의 OpenInput 272/273/274/275행 모두 covered=yes를 검사했다.
- 제품 소스 및 기존 제품 DLL/PDB 등 53개 SHA256 불변 확인, git diff --check 통과. 기존 TC-13 경로를 교체했으므로 전체 TC-01~19 실행에 포함된다. 전체 UI 시험과 실제 손상 파일·네트워크 장애 자체의 재현은 이번 검증 범위가 아니다.

## 입력 스트림 정보 분석 실패 검토

- 사용자가 OpenInput의 find_stream_info 실패 블록 검토를 요청했다. 제품 및 시뮬레이터 소스 수정 없이 기존 시험과 결과를 확인한다.
- avformat_open_input은 입력 열기와 헤더 읽기, avformat_find_stream_info는 추가 패킷을 읽어 스트림 정보를 수집하는 별도 단계다. FFmpeg 4.4 문서는 후자의 성공값 >=0과 실패 AVERROR를 명시한다. https://ffmpeg.org/doxygen/4.4/group__lavf__decoding.html . 입력 열기 성공은 분석 성공을 보장하지 않으며 손상·불완전 입력 또는 자원 부족 등으로 실패할 수 있다. 모든 손상/연결 중단이 반드시 실패를 반환한다고 단정하지 않는다.
- 대상 블록은 오류 상태와 로그를 남기고 false를 반환해 미준비 스트림으로 출력 생성을 진행하지 않도록 하므로 유지해야 하며 검사 제외 대상이 아니다. _ic는 열린 컨텍스트로 유지되지만 정상 Run 호출의 finally에서 CloseInput으로 해제된다. 옵션은 OpenInput finally에서 별도로 해제되고, 실행 중이면 2초 뒤 재시도한다.
- 근본적인 기존 시험 공백을 확인했다. Program.RunStreamInfoFailure는 ReportStreamInfoFailure(NullLogger.Instance) 호출 후 무조건 true를 반환하며, ReportStreamInfoFailure는 CoreLog.CannotFindStreamInfo에 고정 문자열을 넘길 뿐 실제 OpenInput이나 FFmpeg 분석을 실행하지 않는다. TC-13에 연결되어 있지만 해당 실패 블록을 검증하지 않는다.
- 기존 결과.xml에서 272~275행은 covered=no였다. `dotnet-coverage collect --settings tools/EzStream.CoverageTool/Coverage.runsettings -o artifacts/stream-info-review/result.coverage -f coverage tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe STREAM_INFO_FAILURE` 실행 결과 OK였지만, merge로 만든 result.xml에서 OpenInput 270~275행이 모두 covered=no임을 재확인했다.
- 후속 수정 방안은 TC-13 기존 시나리오를 실제 입력 열기 성공 후 avformat_find_stream_info만 음수로 반환하는 Harness 바인딩 주입으로 교체하는 것이다. RunOnce 조기 반환, LastError, 활성 logger의 실패 로그, 출력 미생성 및 입력 정리, 바인딩 복원을 검증하면 된다. 본 코드 변경 없이 가능한 방식이며 이번에는 새 실패 주입을 구현하거나 실행하지 않았다.

## 절단 후 출력 열기 실패 시험 구현

- 사용자 승인에 따라 TC-11 기존 절단 시험을 확장한다. 시험 프로세스에서만 avio_open 바인딩을 교체하며 두 번째 호출에 오류를 반환한다. 본 코드와 기존 사용자 변경은 유지한다.
- RECORDER_CUT_CONDITIONS에 세 번째 모드를 추가했다. 첫 avio_open은 네이티브 함수로 성공시키고 두 번째에 AVERROR_EXTERNAL을 반환한다. 바인딩은 finally에서 복원한다. 사용 버전에 EACCES 상수가 없어 최초 빌드가 실패했고, 제공되는 AVERROR_EXTERNAL로 수정했다.
- 실패 시 출력 열기 2회/패킷 읽기 5회로 중단됨, CurrentFile=null, LastError의 avio_open 오류, 정상 종료 알림 1개와 출력 실패 알림 1개, 이전 MP4의 4패킷 재읽기를 검증한다. 기존 시간 만료/주기 변경 정상 절단 모드도 함께 실행한다. 서비스의 2초 재시도 루프 자체는 이 RunOnce 시험 범위가 아니다.
- `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션의 CoverageTool 빌드 성공. 최종 경고/오류 0개.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RECORDER_CUT_CONDITIONS` 성공. artifacts/cut-failure-integration/result.xml에서 RunOnce 211/219/220/221/222행 모든 range가 covered=yes임을 검사했다.
- 제품 파일 53개의 SHA256 불변 및 git diff --check 통과. TC-11 및 이를 포함한 전체 UI 시험에 연결되어 있으며 전체 UI 시험 자체는 재실행하지 않았다.

## 절단 후 새 출력 열기 실패 검토

- 사용자 요청은 절단 후 OpenNewSegment 실패 처리 검토다. 코드 변경이나 새 시험 구현 없이 실패 경로와 재현 방안을 확인한다.
- 결과.xml의 RunOnce 220~222행은 covered=no이다. 절단 성공 시험은 새 파일 생성 성공만 확인하며 초기 출력 실패 시험은 RunOnce 초반에서 반환하므로 이 블록을 실행하지 않는다.
- 첫 파일을 열었어도 다음 파일 생성 시 디렉터리 준비, avio_open, 헤더 쓰기가 새로 수행된다. 저장 장치 단절, 접근 권한 변경, 파일 충돌 또는 쓰기 실패 등이 발생하면 false를 반환할 수 있다. 정상 입력 코덱이 그대로인 상황에서 미지원 코덱을 대표 원인으로 삼는 것은 부적절하다. 발생 확률은 운영 자료 없이 수치화하지 않는다.
- return은 준비되지 않은 출력으로 WritePacket을 진행하지 않고 RunOnce를 종료하는 필수 동작이다. RunOnce finally의 av_packet_free와 Run finally의 CloseOutput/CloseInput이 자원을 정리한다. _running이면 2초 후 다시 입력 및 출력 준비를 시도한다.
- av_packet_unref는 이 반환 경로에서 바로 뒤 finally의 av_packet_free가 내부적으로 unref를 호출하므로 해제 관점에서는 중복이다. FFmpeg n4.4 avpacket.c 71~78행을 확인했다. https://github.com/FFmpeg/FFmpeg/blob/n4.4/libavcodec/avpacket.c . 이 중복은 실패 분기의 필요성과 별개이며 제품은 수정하지 않았다.
- 판정은 실제 발생 가능한 출력 장애 처리이므로 검사 제외 대상이 아니며 유지·재현 시험 추가를 권장한다.
- 시뮬레이터 재현 설계는 TC-11의 절단 시험에 연결한다. 별도 Harness에서 첫 avio_open은 원래 함수로 성공시키고 절단 후 두 번째 호출만 음수 오류를 반환하도록 바인딩을 교체할 수 있다. 이전 MP4 정상 종료, 실패 알림/LastError, 추가 패킷 기록 중단 및 대상 블록 커버리지를 확인하고 finally에서 원래 바인딩을 복원한다. 제품 소스/제품 DLL 변경이나 실제 저장장치 장애 없이 가능한 오류 주입 방식이며 이번 검토에서 새 시험은 실행하지 않았다.

## 절단 조건 시험 구현

- 사용자의 두 작업 진행 요청을 받아 시뮬레이터 시험을 추가한다. 단일 조건 수집 제외는 현재 설정에 없으므로 제품 유지 및 사유 문서화와 제품 조건 제거 중 선택을 비동기 질문했다. 답변 전 제품 코드는 수정하지 않는다.
- 사용자 답변은 시뮬레이터 수정과 제외 검토 사유 정리이며, 제품의 조건 삭제가 아니다. 제품과 수집 설정은 유지한다.

### 검사 제외 검토서 — 비디오가 없는 경우의 절단 허용 분기

- 대상은 src/EzStream.Core/Recording/SourceRecorder.cs 212행 `_videoInputIndex < 0` 비교 결과가 참인 분기만이다. 전체 canCut 식, 비교 결과가 거짓인 분기, `isVideo && isKey`는 제외 대상이 아니다.
- 전제는 현재 비디오 전용 출력 정책과 정상 녹화기 수명주기이다. OpenInput 278~283행에서 첫 비디오 인덱스를 설정한다. OpenNewSegmentCore 348~376행은 비디오만 출력 매핑에 등록하며, 등록 개수가 0이면 379~385행에서 실패한다. RunOnce 174행은 해당 실패 시 반환한다. 따라서 비디오가 없으면 212행에 도달하지 않는다.
- CloseInput은 비디오 인덱스를 -1로 되돌리지만 정상 실행에서 RunOnce 종료 후 정리 과정에 호출된다. 현재 정상 호출 경로에 녹화 중 인덱스를 -1로 바꾸는 동작은 없다.
- 판정은 현 구현에서 정상 도달 불가능한 참 분기에 대한 검사 제외 검토 대상으로 분류한다. 억지로 private 필드를 -1로 바꾸어 실행률을 채우는 시험은 추가하지 않는다.
- 관리 방식은 원본 coverage/XML을 보존하고 이 사유를 검토 근거로 첨부하는 것이다. 현재 수집 설정으로 한 식 안의 특정 분기만 제외하도록 구현하지 않았으며, 코드나 XML을 고쳐 달성으로 표시하지 않는다. 따라서 212행 partial은 남는다.
- 오디오 출력 지원, 매핑 정책 변경, 녹화 중 비디오 인덱스 갱신 또는 수명주기 변경 시 이 판정을 재검토해야 한다.

### 절단 조건 시험 검증

- TC-11의 기존 세그먼트 절단 시험 다음에 RECORDER_CUT_CONDITIONS를 연결했다. 전체 TC-01~19 실행에도 포함된다.
- 두 개의 실제 비디오 스트림을 가진 AVI를 만들고 원본 네이티브 읽기를 사용한다. 주 비디오 비키프레임 경로는 시험 프로세스에서 KEY 플래그만 지우는 제어 시험이며 실제 압축 영상 GOP 검증은 아니다.
- 요청 절단은 공개 UpdateSegmentMinutes(120000)를 호출한다. 자동 절단은 첫 패킷 기록 후 시험 프로세스의 _segmentMillis만 1000으로 단축하고 1200ms를 기다리며 _cutNow를 설정하지 않는다. 제품 소스 변경 없이 시간 비교 경로를 검증한다.
- 각 모드에서 첫 패킷 정상 기록, 두 번째 비디오 키프레임·주 비디오 비키프레임·두 번째 비디오 키프레임 보류, 주 비디오 키프레임에서 절단을 확인한다. 저장 알림 2개 성공 및 이전 MP4 4패킷/다음 MP4 1패킷 재읽기를 확인한다.
- 초기 실행에서 MP4 재읽기가 FFmpeg의 지연 바인딩을 초기화하여 마지막 복원 비교가 실패했다. 각 모드의 복원 직후, 재읽기 전에 원래 바인딩과 비교하도록 고쳤다. CA1303이 발생한 불필요한 직접 진단 출력도 제거했다.
- 최종 `dotnet build tools/EzStream.CoverageHarness/EzStream.CoverageHarness.csproj -c Debug --no-restore -p:BuildProjectReferences=false -p:CopyRetryCount=0` 및 동일 옵션의 CoverageTool 빌드는 경고/오류 0개로 성공했다.
- `dotnet-coverage connect <검증 세션 ID> tools/EzStream.CoverageHarness/bin/Debug/net9.0-windows/win-x64/EzStream.CoverageHarness.exe RECORDER_CUT_CONDITIONS` 최종 통과. 공유 AVI 생성기의 기존 RECORDER_EAGAIN 시험도 통과했다.
- artifacts/cut-integration/final.xml에서 210/211/213~218행 covered=yes, 212행 covered=partial을 확인했다. 제품 소스와 기존 제품 DLL/PDB 등 53개 SHA256은 변경 전과 같다. git diff --check 통과. 전체 UI 시험 자체는 재실행하지 않았다.

## 세그먼트 절단 조건 검토 시작

- wantCut과 canCut 검토 요청이며 제품 또는 시뮬레이터 수정은 하지 않는다. 결과.xml에서 211/212행 partial, 절단 본문 214~219행 yes를 확인했다.
- wantCut은 실제 벽시계 시간 경과 또는 주기 변경 시 요청(_cutNow)이다. UpdateSegmentMinutes는 주기가 달라질 때만 _cutNow=true로 설정한다. intervalElapsed=true면 단락 평가로 _cutNow를 읽지 않는다. 둘 다 유지해야 자동 절단과 설정 변경 절단이 동작한다.
- OpenInput에서 첫 비디오 인덱스를 찾는다. OpenNewSegmentCore는 비디오만 매핑하고 없으면 false로 종료한다. 따라서 정상 수명주기에서 이 줄까지 도달한 상태의 _videoInputIndex<0은 참이 될 수 없다. 비디오 없는 경우를 수용하는 기존 식의 항목은 현재 정책에서 제외 검토 대상이며 무조건 전체 canCut 제외는 부적절하다.
- isVideo는 모든 비디오 여부가 아니라 첫 비디오 인덱스와의 일치 여부다. 오디오는 앞의 outIdx<0에서 건너뛰므로 isVideo=false를 시험하려면 두 번째 비디오 스트림처럼 유효하게 매핑된 다른 스트림이 필요하다. isKey=false는 주 비디오의 비키프레임으로 시험할 수 있다. 다중 비디오에서는 첫 비디오 키프레임을 기준으로 자르는 현재 동작임을 유의한다.
- 기존 TC-11 RunSegmentCutTestAsync는 MJPEG JPEG 프레임을 느린 HTTP로 공급하고 SET_INTERVAL로 2분에서 1분으로 변경한다. 시간 만료 및 비키프레임에서 절단을 보류하는 경우를 독립적으로 확인하는 시험은 아니다. XML의 partial만으로 조건별 미달성 항목을 단정하지 않는다.
- 시뮬레이터만 변경하는 후속 시험은 TC-11에 연결하는 것이 적절하다. 시간 만료, 주기 변경 요청, 비키프레임 보류 후 키프레임 절단, 두 번째 비디오에서 보류를 각각 확인할 수 있다. 실제 파일 또는 Harness의 패킷 플래그 제어를 이용할 수 있으며 후자는 입력 제어 시험으로 명시한다. _videoInputIndex=-1 강제 주입은 정상 도달성 증명이 되지 않는다.
- 이번 검증은 PowerShell XML 파싱 및 rg/Get-Content 소스 대조이며 코드 변경이나 새 동적 시험은 수행하지 않았다. 제품 코드 및 기존 사용자 변경은 보존한다.

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
