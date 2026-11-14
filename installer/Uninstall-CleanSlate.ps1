# ==============================================================================
# CleanSlate Standalone PowerShell Uninstaller
# Uninstalls CleanSlate, removes shortcuts, startup registration, and app binaries.
# ==============================================================================


$ErrorActionPreference = "SilentlyContinue"

Write-Host ">>> Stopping CleanSlate processes..." -ForegroundColor Cyan
Get-Process CleanSlate*, CleanSlate.Settings* | Stop-Process -Force
Start-Sleep -Milliseconds 400

# Remove startup task
Write-Host ">>> Removing startup registration..." -ForegroundColor Cyan
schtasks.exe /delete /tn "CleanSlate" /f 2>&1 | Out-Null
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CleanSlate" -ErrorAction SilentlyContinue

# Remove Start Menu shortcuts
Write-Host ">>> Removing Start Menu shortcuts..." -ForegroundColor Cyan
$startMenu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
Remove-Item -Path (Join-Path $startMenu "CleanSlate.lnk") -Force
Remove-Item -Path (Join-Path $startMenu "CleanSlate Settings.lnk") -Force

# Delete app binaries
$regInstallLocation = (Get-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate" -ErrorAction SilentlyContinue).InstallLocation

# Remove Add/Remove Programs entry
Write-Host ">>> Removing Windows uninstall entry..." -ForegroundColor Cyan
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate" -Recurse -Force -ErrorAction SilentlyContinue

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$appDir = if (![string]::IsNullOrWhiteSpace($regInstallLocation) -and (Test-Path -LiteralPath $regInstallLocation)) {
    $regInstallLocation
} else {
    Join-Path $localAppData "CleanSlate\app"
}
if (Test-Path -LiteralPath $appDir) {
    Write-Host ">>> Removing application files ($appDir)..." -ForegroundColor Cyan
    Remove-Item -Path $appDir -Recurse -Force
}

# Restore desktop shortcuts from backup or CleanSlate/Shortcuts if needed
$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
$shortcutsDir = Join-Path $localAppData "CleanSlate\Shortcuts"
if (Test-Path -LiteralPath $shortcutsDir) {
    Get-ChildItem -Path $shortcutsDir -File | ForEach-Object {
        $dest = Join-Path $desktop $_.Name
        if (!(Test-Path -LiteralPath $dest)) {
            Copy-Item $_.FullName $dest -Force
        }
    }
}

Write-Host ">>> CleanSlate has been uninstalled." -ForegroundColor Green
