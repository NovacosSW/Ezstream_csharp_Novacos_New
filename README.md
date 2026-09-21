# EzStream Recorder (C# 포팅)

기존 C++ `ezstream`의 **세그먼트 녹화** 기능을 Windows용으로 이식한 버전.
RTSP(또는 영상 소스)를 받아 **N분 주기로 MP4 파일을 잘라 저장**하며, Windows 서비스 +
트레이 UI + 설치파일로 구성된다.

## 주요 기능
- 소스별 스레드로 RTSP 입력을 열어 **스트림 카피(remux)** 로 MP4 저장 (FFmpeg.AutoGen 사용, 재인코딩 없음)
- **N분 주기 세그먼트 절단**: 비디오 키프레임 경계에서 파일을 잘라 각 MP4가 독립 재생 가능
- **저장 주기 실시간 변경**: 트레이 설정에서 분을 바꾸면 **현재 저장 중이던 파일을 정상 종료(정리)하고 그 시각부터 새 주기로 저장**
- **Windows 서비스**로 등록되어 부팅 시 자동 시작, **크래시 시 자동 재기동**(SCM 복구 설정)
- **트레이 아이콘** 클릭 → 상태창(소스별 상태·현재 파일·기록량), **설정**·**저장 경로 열기**·**로그 보기**(실시간 tail) 버튼
- **설치 마법사에서 저장 경로·주기·보존기간·로그 보존기간·소스 목록 입력** (기본: `C:\ezstream\data` / 10분 / 60일 / 영상 보존기간과 동일)
  - 로그 보존기간은 영상 보존기간을 바꾸면 같은 값으로 따라 바뀌며(설치 마법사·트레이 설정창 공통), 따로 수정하면 그 값이 유지된다
  - 재설치 시 기존 `config.json` 이 있으면 입력값으로 덮어쓸지 묻는다(덮어쓰면 `config.json.bak` 으로 백업, 무인 설치는 기존 설정 유지)
- 네트워크 끊김/타임아웃 시 자동 재접속, 보존 기간(일) 경과 파일 자동 삭제
- 로그(`%ProgramData%\EzStream\logs\ezstream-*.log`)도 로그 보존 기간(`logRetentionDays`, 기본 30일, 0=무제한) 경과 시 자동 삭제
- 설정은 로컬 JSON(`%ProgramData%\EzStream\config.json`), 서비스가 소유·저장
- MP4 세그먼트가 마감되면 성공/실패 결과를 설정된 IP와 포트로 UDP JSON 전송 (`udpNotificationPort`가 0이면 비활성)

## 솔루션 구조
```
/
├─ EzStream.sln
├─ src/
│  ├─ EzStream.Core/     # 녹화 엔진, 설정, IPC 계약 (net9.0)
│  ├─ EzStream.Service/  # Windows 서비스 호스트 + Named Pipe 서버 (net9.0-windows)
│  └─ EzStream.Tray/     # 트레이 아이콘 + 상태창 + 설정창 (WinForms)
├─ installer/            # Inno Setup 스크립트, 기본 config
├─ scripts/              # FFmpeg 취득 / 게시 스크립트
└─ ffmpeg/               # FFmpeg 4.4 shared DLL (fetch-ffmpeg.ps1 로 채움, git 제외)
```

## 요구 사항
- .NET SDK 9.0 (또는 8.0으로 TargetFramework 조정)
- FFmpeg **4.4 shared** 네이티브 DLL (FFmpeg.AutoGen 4.4.x ABI). `scripts\fetch-ffmpeg.ps1` 로 취득
- 설치파일 빌드 시: [Inno Setup 6+](https://jrsoftware.org/isdl.php)

## 빌드 & 실행 (개발)
```powershell
# 1) FFmpeg DLL 취득 (c#\ffmpeg 에 저장)
powershell -ExecutionPolicy Bypass -File scripts\fetch-ffmpeg.ps1

# 2) 솔루션 빌드
dotnet build EzStream.sln -c Release

# 3) 콘솔 모드로 서비스 로직 실행 (서비스 등록 없이 디버그)
dotnet run --project src\EzStream.Service -c Release -- --console
```
콘솔 모드에서는 `%ProgramData%\EzStream\config.json` 을 읽어 녹화한다. 없으면 기본값이 생성된다.
개발 중에는 `bin\Release\net9.0\ffmpeg` 에 DLL이 필요하므로, `ffmpeg` 폴더의 DLL을
빌드 출력 옆으로 복사하거나 게시 폴더에서 실행한다.

### 테스트 소스
```
rtsp://210.99.70.120:1935/live/cctv001.stream
rtsp://210.99.70.120:1935/live/cctv002.stream
```
`installer\config.sample.json` 에 이 두 소스가 예시로 들어 있다.

## 설치파일 만들기
```powershell
powershell -ExecutionPolicy Bypass -File scripts\fetch-ffmpeg.ps1   # 최초 1회
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1        # publish\ 구성
iscc installer\EzStream.iss                                        # installer\Output\EzStreamSetup-1.0.0.exe
```
설치파일은 self-contained(대상에 .NET 런타임 불필요)로 게시된 바이너리 + FFmpeg DLL을 포함한다.

## 설치 후 동작
- 서비스 `EzStreamRecorder` 가 자동 시작(LocalSystem), 죽으면 5초 뒤 자동 재기동
- 로그인 시 트레이 앱 자동 실행 → 아이콘 클릭 시 상태창
- 설정창에서 저장 주기(분)·소스·저장 경로·보존 기간·로그 보존 기간을 편집하면 **재시작 없이 실시간 반영**
- 제거: 제어판 프로그램 제거 또는 시작 메뉴의 "EzStream Recorder 제거" → 서비스 정지·삭제, 트레이 종료

## UDP 영상 저장 결과 알림

`%ProgramData%\EzStream\config.json`에서 수신 주소를 지정한다. 이 항목은 트레이 설정창에 표시되지 않는다.

```json
"udpNotificationIp": "192.168.0.50",
"udpNotificationPort": 5000
```

MP4 마감 직후 아래 형식의 UTF-8 JSON 데이터그램을 한 번 전송한다. 전송 실패는 로그에만 남고 녹화에는 영향을 주지 않는다.

```json
{
  "schemaVersion": 1,
  "eventType": "videoSaveResult",
  "sentAtUtc": "2026-09-14T03:05:00.123Z",
  "durationMilliseconds": 300000,
  "filePrefix": "eo",
  "fileSizeBytes": 12345678,
  "success": true,
  "error": null
}
```

## 검증 체크리스트
1. 콘솔 모드로 실행 → `documentRoot\<path>\<날짜>\` 에 N분마다 MP4 생성, 각 파일 재생 확인
2. 트레이 설정에서 분 변경 → 현재 파일 즉시 종료 + 새 파일이 그 시각부터 시작
3. 설치 후 `taskkill /f /im EzStream.Service.exe` → SCM이 5초 내 재기동
4. 상태창 PLAYING 표시, "저장 경로 열기" 동작, 제거 후 서비스 삭제 확인
