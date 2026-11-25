[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$rootDir = [System.IO.Path]::GetFullPath((Join-Path $scriptDir ".."))
$stagingDir = Join-Path $scriptDir "staging\app"
$distDir = Join-Path $scriptDir "dist"
$installerProjectDir = Join-Path $scriptDir "src\CleanSlate.Installer"
$payloadZip = Join-Path $installerProjectDir "payload.zip"

$dotnet = "dotnet"
if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    $dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

Write-Host ">>> [1/5] Cleaning staging directories..." -ForegroundColor Cyan
if (Test-Path $stagingDir) { Remove-Item $stagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null
if (Test-Path $distDir) {
    Get-ChildItem $distDir -Force | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
} else {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
}

Write-Host ">>> [2/4] Publishing CleanSlate core & settings (ReadyToRun win-x64)..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $rootDir "CleanSlate\CleanSlate.csproj") -c $Configuration -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $stagingDir
& $dotnet publish (Join-Path $rootDir "CleanSlate.Settings\CleanSlate.Settings.csproj") -c $Configuration -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:DebugType=None -p:DebugSymbols=false -o $stagingDir

Write-Host ">>> [3/4] Archiving payload into payload.zip..." -ForegroundColor Cyan
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path "$stagingDir\*" -DestinationPath $payloadZip -CompressionLevel Optimal -Force

Write-Host ">>> [4/4] Building self-contained CleanSlate-Setup.exe with embedded payload..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $installerProjectDir "CleanSlate.Installer.csproj") -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $distDir

# Clean up temporary staging
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $scriptDir "staging") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ">>> Build successful! Output: $(Join-Path $distDir 'CleanSlate-Setup.exe')" -ForegroundColor Green
