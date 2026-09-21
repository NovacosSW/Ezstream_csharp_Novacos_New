<#
.SYNOPSIS
  Service/Tray 를 self-contained(win-x64)로 게시하고 FFmpeg DLL·샘플 설정을 모아
  c#\publish 폴더를 구성한다. 이 폴더가 Inno Setup 설치 소스가 된다.
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$publish = Join-Path $root "publish"
$ffmpeg = Join-Path $root "ffmpeg"

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

Write-Host "Publishing EzStream.Service..."
dotnet publish (Join-Path $root "src\EzStream.Service\EzStream.Service.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $publish

Write-Host "Publishing EzStream.Tray..."
dotnet publish (Join-Path $root "src\EzStream.Tray\EzStream.Tray.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $publish

# FFmpeg DLL 포함
$ffmpegOut = Join-Path $publish "ffmpeg"
New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
if (Test-Path $ffmpeg) {
    Get-ChildItem -Path $ffmpeg -Filter *.dll | ForEach-Object {
        Copy-Item $_.FullName -Destination $ffmpegOut -Force
    }
    Write-Host "Copied FFmpeg DLLs into publish\ffmpeg"
} else {
    Write-Warning "c#\ffmpeg 폴더가 없습니다. 먼저 scripts\fetch-ffmpeg.ps1 을 실행하세요."
}

# 샘플 설정/라이선스
Copy-Item (Join-Path $root "installer\config.sample.json") -Destination $publish -Force
if (Test-Path (Join-Path $root "THIRD-PARTY-LICENSES.txt")) {
    Copy-Item (Join-Path $root "THIRD-PARTY-LICENSES.txt") -Destination $publish -Force
}

Write-Host ""
Write-Host "Publish 완료: $publish" -ForegroundColor Green
Write-Host "다음: Inno Setup 으로 installer\EzStream.iss 를 컴파일하세요 (iscc installer\EzStream.iss)."
