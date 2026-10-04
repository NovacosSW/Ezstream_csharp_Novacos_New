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
