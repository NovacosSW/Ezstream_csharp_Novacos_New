<#
.SYNOPSIS
  다른 Windows PC에서 폴더 위치와 관계없이 실행할 수 있는 커버리지 시험 도구 ZIP을 만든다.
#>
param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$stage = Join-Path $root "artifacts\coverage-tool-package"
$buildArtifacts = Join-Path $root "artifacts\coverage-tool-build"
$output = Join-Path $root "deliverables\EzStreamCoverageToolPortable"
$zipPath = Join-Path $root "tools\EzStream.CoverageTool.zip"

foreach ($directory in @($stage, $buildArtifacts, $output)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Write-Host "Publishing coverage tool..."
dotnet publish (Join-Path $root "tools\EzStream.CoverageTool\EzStream.CoverageTool.csproj") `
    -c $Configuration -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none `
    --artifacts-path $buildArtifacts -o $stage
if ($LASTEXITCODE -ne 0) {
    throw "EzStream.CoverageTool publish failed with exit code $LASTEXITCODE."
}

$harnessDirectory = Join-Path $stage "Harness"
New-Item -ItemType Directory -Path $harnessDirectory -Force | Out-Null
foreach ($project in @(
    "tools\EzStream.CoverageHarness\EzStream.CoverageHarness.csproj",
    "src\EzStream.Service\EzStream.Service.csproj",
    "src\EzStream.Tray\EzStream.Tray.csproj"
)) {
    Write-Host "Publishing $project..."
    dotnet publish (Join-Path $root $project) `
        -c $Configuration -r win-x64 --self-contained false `
        -p:PublishSingleFile=false --artifacts-path $buildArtifacts `
        -o $harnessDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "$project publish failed with exit code $LASTEXITCODE."
    }
}

$readme = Join-Path $root "tools\EzStream.CoverageTool\배포본_사용방법.md"
Copy-Item -LiteralPath $readme -Destination $stage -Force
Copy-Item -Path (Join-Path $stage "*") -Destination $output -Recurse -Force

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stage,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host ""
Write-Host "Portable folder: $output" -ForegroundColor Green
Write-Host "Portable ZIP:    $zipPath" -ForegroundColor Green

