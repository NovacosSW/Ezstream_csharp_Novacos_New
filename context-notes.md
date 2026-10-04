# 조사 맥락

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
