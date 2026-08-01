$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot "src\KsefMonitor\KsefMonitor.csproj"
$output = Join-Path $projectRoot "artifacts\win-x64"

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false

Write-Host "Gotowe: $output\KSeFMonitor.exe"
