<#
.SYNOPSIS
  FFmpeg 4.4 공유(shared) 빌드에서 필요한 네이티브 DLL만 c#\ffmpeg 로 내려받는다.
  FFmpeg.AutoGen 4.4.x 는 FFmpeg 4.4 ABI(avcodec-58 등)를 요구한다.

.NOTES
  기본 소스는 gyan.dev 아카이브의 ffmpeg-4.4.1 shared 빌드.
  다른 미러/버전을 쓰려면 -Url 파라미터로 shared 빌드 zip 을 지정한다.
#>
param(
    [string]$Url = "https://github.com/GyanD/codexffmpeg/releases/download/4.4.1/ffmpeg-4.4.1-full_build-shared.zip",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\ffmpeg")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$tmp = Join-Path $env:TEMP ("ffmpeg_" + [System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$zip = Join-Path $tmp "ffmpeg.zip"

Write-Host "Downloading FFmpeg shared build..."
Write-Host "  $Url"
Invoke-WebRequest -Uri $Url -OutFile $zip

Write-Host "Extracting..."
Expand-Archive -Path $zip -DestinationPath $tmp -Force

# shared 빌드의 bin 폴더에 있는 *.dll 을 수집
$dlls = Get-ChildItem -Path $tmp -Recurse -Filter *.dll |
    Where-Object { $_.FullName -match "\\bin\\" }

if (-not $dlls) {
    # 일부 빌드는 bin 없이 루트에 dll 을 둔다
    $dlls = Get-ChildItem -Path $tmp -Recurse -Filter *.dll
}

if (-not $dlls) {
    throw "다운로드한 zip 에서 DLL 을 찾지 못했습니다. -Url 로 shared 빌드를 지정하세요."
}

Write-Host "Copying $($dlls.Count) DLL(s) to $OutDir"
foreach ($d in $dlls) {
    Copy-Item -Path $d.FullName -Destination (Join-Path $OutDir $d.Name) -Force
}

Remove-Item -Recurse -Force $tmp

Write-Host "Done. FFmpeg DLLs:"
Get-ChildItem -Path $OutDir -Filter *.dll | Select-Object Name, Length | Format-Table -AutoSize

Write-Host ""
Write-Host "필수 DLL(대략): avformat-58, avcodec-58, avutil-56, swscale-5, swresample-3, avfilter-7, avdevice-58" -ForegroundColor Cyan
