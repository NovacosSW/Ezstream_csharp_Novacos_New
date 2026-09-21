; EzStream Recorder 설치 스크립트 (Inno Setup 6+/7)
; 빌드 순서:
;   1) powershell -File scripts\fetch-ffmpeg.ps1      (FFmpeg DLL 취득)
;   2) powershell -File scripts\publish.ps1           (self-contained 게시 → publish\)
;   3) "C:\Program Files\Inno Setup 7\ISCC.exe" installer\EzStream.iss

#define AppName "EzStream Recorder"
#define AppVersion "1.0.0"
#define AppPublisher "Raontec"
#define ServiceName "EzStreamRecorder"
#define SourceDir "..\publish"

[Setup]
AppId={{7E2B9C10-4E5A-4C6B-9E1F-EZSTREAM0001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\EzStream
DefaultGroupName=EzStream
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=EzStreamSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
UninstallDisplayName={#AppName}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Files]
; 게시된 서비스/트레이 + FFmpeg DLL 전체
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Dirs]
Name: "{commonappdata}\EzStream"
Name: "{commonappdata}\EzStream\logs"

[Icons]
; 로그인 시 모든 사용자에게 트레이 자동 실행
Name: "{commonstartup}\EzStream Tray"; Filename: "{app}\EzStream.Tray.exe"
Name: "{group}\EzStream 상태"; Filename: "{app}\EzStream.Tray.exe"
Name: "{group}\{#AppName} 제거"; Filename: "{uninstallexe}"

[Run]
; 서비스 등록/시작은 config.json 생성 순서를 보장하기 위해 [Code]의 ssPostInstall 에서 수행한다.
; 트레이 즉시 실행(설치 실행자 권한으로)
Filename: "{app}\EzStream.Tray.exe"; Description: "트레이 상태 앱 실행"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im EzStream.Tray.exe"; Flags: runhidden; RunOnceId: "KillTray"
Filename: "{sys}\sc.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopSvc"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteSvc"

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\EzStream\logs"

[Code]
var
  SettingsPage: TInputQueryWizardPage;
  SourcesPage: TWizardPage;
  SourcesMemo: TNewMemo;

const
  DefaultDocRoot = 'C:\ezstream\data';
  DefaultMinutes = '10';
  DefaultRetention = '60';

var
  LastRetention: String;

{ 영상 보존 기간 변경 시, 로그 보존 기간이 이전 영상 보존 기간과 같으면(= 사용자가 따로 바꾸지 않았으면) 함께 변경 }
procedure RetentionEditChange(Sender: TObject);
begin
  if Trim(SettingsPage.Edits[3].Text) = Trim(LastRetention) then
    SettingsPage.Edits[3].Text := SettingsPage.Edits[2].Text;
  LastRetention := SettingsPage.Edits[2].Text;
end;

procedure InitializeWizard;
var
  lbl: TNewStaticText;
begin
  { 저장 경로 / 주기 / 보존기간 입력 페이지 }
  SettingsPage := CreateInputQueryPage(wpSelectDir,
    '녹화 설정', '저장 경로와 주기, 보존 기간을 설정하세요.',
    '설치 후에도 트레이의 설정 창에서 언제든지 변경할 수 있습니다.');
  SettingsPage.Add('저장 경로 (예: C:\ezstream\data):', False);
  SettingsPage.Add('저장 주기 (분):', False);
  SettingsPage.Add('보존 기간 (일, 0 = 무제한):', False);
  SettingsPage.Add('로그 보존 기간 (일, 0 = 무제한, 기본 = 영상 보존 기간):', False);
  SettingsPage.Values[0] := DefaultDocRoot;
  SettingsPage.Values[1] := DefaultMinutes;
  SettingsPage.Values[2] := DefaultRetention;
  SettingsPage.Values[3] := DefaultRetention;
  LastRetention := DefaultRetention;
  SettingsPage.Edits[2].OnChange := @RetentionEditChange;

  { 소스 목록 입력 페이지(멀티라인) }
  SourcesPage := CreateCustomPage(SettingsPage.ID, '소스 목록',
    'RTSP 소스를 한 줄에 하나씩 입력하세요.');

  lbl := TNewStaticText.Create(WizardForm);
  lbl.Parent := SourcesPage.Surface;
  lbl.Top := 0;
  lbl.Width := SourcesPage.SurfaceWidth;
  lbl.AutoSize := False;
  lbl.Height := ScaleY(34);
  lbl.WordWrap := True;
  lbl.Caption := '형식:  URL | 하위경로 | 파일접두사' + #13#10 +
                 '예:    rtsp://cam/live1 | live1 | live1';

  SourcesMemo := TNewMemo.Create(WizardForm);
  SourcesMemo.Parent := SourcesPage.Surface;
  SourcesMemo.Top := ScaleY(40);
  SourcesMemo.Left := 0;
  SourcesMemo.Width := SourcesPage.SurfaceWidth;
  SourcesMemo.Height := SourcesPage.SurfaceHeight - ScaleY(40);
  { 자동 줄바꿈이 켜져 있으면 Lines 가 화면상 줄 단위로 쪼개지므로 끈다 }
  SourcesMemo.WordWrap := False;
  SourcesMemo.ScrollBars := ssBoth;
  SourcesMemo.Lines.Clear;
  SourcesMemo.Lines.Add('rtsp://210.99.70.120:1935/live/cctv001.stream | live1 | live1');
  SourcesMemo.Lines.Add('rtsp://210.99.70.120:1935/live/cctv002.stream | live2 | live2');
end;

function IsPositiveInt(const S: String; var V: Integer): Boolean;
var
  i: Integer;
begin
  Result := (S <> '');
  for i := 1 to Length(S) do
    if (S[i] < '0') or (S[i] > '9') then Result := False;
  if Result then V := StrToIntDef(S, -1);
  if Result and (V < 0) then Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  v: Integer;
begin
  Result := True;
  if CurPageID = SettingsPage.ID then
  begin
    if Trim(SettingsPage.Values[0]) = '' then
    begin
      MsgBox('저장 경로를 입력하세요.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if not IsPositiveInt(Trim(SettingsPage.Values[1]), v) or (v < 1) then
    begin
      MsgBox('저장 주기는 1 이상의 정수(분)여야 합니다.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if not IsPositiveInt(Trim(SettingsPage.Values[2]), v) then
    begin
      MsgBox('보존 기간은 0 이상의 정수(일)여야 합니다.', mbError, MB_OK);
      Result := False; Exit;
    end;
    if not IsPositiveInt(Trim(SettingsPage.Values[3]), v) then
    begin
      MsgBox('로그 보존 기간은 0 이상의 정수(일)여야 합니다.', mbError, MB_OK);
      Result := False; Exit;
    end;
  end;
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

{ 'a | b | c' 형태의 한 줄에서 index 번째(0-based) 필드를 반환 }
function Field(const Line: String; Index: Integer): String;
var
  rest, part: String;
  p, i: Integer;
begin
  rest := Line;
  i := 0;
  Result := '';
  while True do
  begin
    p := Pos('|', rest);
    if p > 0 then
    begin
      part := Copy(rest, 1, p - 1);
      rest := Copy(rest, p + 1, Length(rest));
    end
    else
    begin
      part := rest;
      rest := '';
    end;
    if i = Index then
    begin
      Result := Trim(part);
      Exit;
    end;
    if p = 0 then Exit;
    i := i + 1;
  end;
end;

function BuildConfigJson: String;
var
  json, line, url, path, prefix, sources: String;
  lines: TStringList;
  i, count: Integer;
begin
  sources := '';
  count := 0;
  { 화면상 줄(Lines)이 아니라 실제 입력 텍스트를 개행 기준으로 나눠 읽는다 }
  lines := TStringList.Create;
  try
    lines.Text := SourcesMemo.Text;
    for i := 0 to lines.Count - 1 do
    begin
      line := lines.Strings[i];
      url := Field(line, 0);
      if url = '' then Continue;
      path := Field(line, 1);
      prefix := Field(line, 2);
      if path = '' then path := 'cam' + IntToStr(count + 1);
      if prefix = '' then prefix := path;
      if count > 0 then sources := sources + ',' + #13#10;
      sources := sources +
        '    { "url": "' + JsonEscape(url) + '", "path": "' + JsonEscape(path) +
        '", "filePrefix": "' + JsonEscape(prefix) + '" }';
      count := count + 1;
    end;
  finally
    lines.Free;
  end;

  json :=
    '{' + #13#10 +
    '  "documentRoot": "' + JsonEscape(Trim(SettingsPage.Values[0])) + '",' + #13#10 +
    '  "segmentMinutes": ' + Trim(SettingsPage.Values[1]) + ',' + #13#10 +
    '  "disuseTermDays": ' + Trim(SettingsPage.Values[2]) + ',' + #13#10 +
    '  "logRetentionDays": ' + Trim(SettingsPage.Values[3]) + ',' + #13#10 +
    '  "ffmpegLogLevel": "warning",' + #13#10 +
    '  "udpNotificationIp": "127.0.0.1",' + #13#10 +
    '  "udpNotificationPort": 0,' + #13#10 +
    '  "sources": [' + #13#10 +
    sources + #13#10 +
    '  ]' + #13#10 +
    '}' + #13#10;
  Result := json;
end;

procedure StopExisting;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im EzStream.Tray.exe', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { 업그레이드 설치 시 파일 잠금 방지를 위해 먼저 정지 }
  StopExisting();
  Result := '';
end;

procedure SetupService;
var
  sc, svcExe: String;
  rc: Integer;
begin
  sc := ExpandConstant('{sys}\sc.exe');
  svcExe := ExpandConstant('{app}\EzStream.Service.exe');
  { 기존 서비스 정리(오류 무시) }
  Exec(sc, 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(sc, 'delete {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(800);
  { 등록(자동 시작, LocalSystem) }
  Exec(sc, 'create {#ServiceName} binPath= "' + svcExe + '" start= auto DisplayName= "{#AppName}"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Exec(sc, 'description {#ServiceName} "RTSP 영상을 N분 주기로 MP4 세그먼트로 저장하는 녹화 서비스"', '', SW_HIDE, ewWaitUntilTerminated, rc);
  { 크래시 시 자동 재기동(5초 후, 반복) }
  Exec(sc, 'failure {#ServiceName} reset= 0 actions= restart/5000/restart/5000/restart/5000', '', SW_HIDE, ewWaitUntilTerminated, rc);
  { 시작 }
  Exec(sc, 'start {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, rc);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  cfgPath, docRoot, json: String;
  oldJson: AnsiString;
  writeCfg: Boolean;
begin
  if CurStep = ssPostInstall then
  begin
    cfgPath := ExpandConstant('{commonappdata}\EzStream\config.json');
    ForceDirectories(ExpandConstant('{commonappdata}\EzStream'));

    { 저장 경로 폴더 미리 생성 }
    docRoot := Trim(SettingsPage.Values[0]);
    if docRoot <> '' then
      ForceDirectories(docRoot);

    { 설정 파일 생성. 기존 설정이 있으면 덮어쓸지 묻는다(무인 설치 시 기존 설정 유지).
      덮어쓰는 경우 기존 파일은 config.json.bak 으로 백업 }
    writeCfg := True;
    if FileExists(cfgPath) then
    begin
      writeCfg := SuppressibleMsgBox('기존 설정 파일이 있습니다.' + #13#10 + cfgPath + #13#10#13#10 +
        '설치 중 입력한 설정(저장 경로, 주기, 보존 기간, 소스 목록)으로 덮어쓸까요?' + #13#10#13#10 +
        '예: 덮어쓰기 (기존 파일은 config.json.bak 으로 백업)' + #13#10 +
        '아니요: 기존 설정 유지',
        mbConfirmation, MB_YESNO, IDNO) = IDYES;
      if writeCfg and LoadStringFromFile(cfgPath, oldJson) then
        SaveStringToFile(cfgPath + '.bak', oldJson, False);
    end;
    if writeCfg then
    begin
      json := BuildConfigJson();
      if not SaveStringToFile(cfgPath, json, False) then
        MsgBox('설정 파일을 저장하지 못했습니다: ' + cfgPath, mbError, MB_OK);
    end;

    { config.json 이 준비된 뒤 서비스 등록/시작 }
    SetupService();
  end;
end;
