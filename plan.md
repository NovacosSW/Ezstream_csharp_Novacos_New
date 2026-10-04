# Core 동적 검사 조사 계획

시뮬레이터는 `tools/EzStream.CoverageTool`을 의미한다고 가정한다. Core의 결과가 적은 원인을 실행 경로, 수집 설정, 배포 파일 및 기존 결과로 구분해 조사한다. 현재 요청은 원인 조사이며 근거 없이 제품 코드를 변경하지 않는다.

1. Service/Tray/Core 실행 및 결과 병합 경로를 비교한다.
2. 기존 결과와 DLL/PDB 배포 상태를 확인한다.
3. 가능한 최소 재현으로 수집 동작을 검증하고 원인과 한계를 기록한다.

사용자가 변경한 `EzStream.sln`은 유지한다.

## 후속 빌드

사용자의 빌드 요청에 따라 Debug 개발 실행 경로를 다시 생성한다.
1. Harness 프로젝트와 참조 Core/Service/Tray를 Rebuild하고 CoverageTool도 Rebuild한다.
2. Service/Tray/Harness의 Core DLL/PDB 식별자와 해시 일치를 확인한다.
3. Core 단위 테스트와 새 세션의 최소 커버리지 수집을 검증한다.

## PipeServer 미달성 검토

`결과.xml`의 AcceptLoop 미달성 범위를 소스와 대조하고 운영상 발생 조건, 재현 가능성, 검사 제외 타당성을 판단한다. 기존 Harness와 공식 .NET 구현을 확인하고 안전한 기존 시나리오로 검증한다. 제품 코드나 검사 제외 설정은 변경하지 않는다.

## 시뮬레이터 IPC 검사 보완

기존 TC-20의 PIPE_ACCEPT_FAILURE 안에서 정상 취소 종료와 연결 오류 후 재접속을 함께 검증한다. 기존 지연 중 취소 검사도 유지한다. Harness/시뮬레이터를 빌드하고 실제 커버리지 수집으로 AcceptLoop 경로 달성을 확인한다. 제품 PipeServer는 변경하지 않는다.
