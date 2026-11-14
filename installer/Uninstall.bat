@echo off
setlocal
cd /d "%~dp0"
if exist "app\Uninstall.exe" (
    start "" "app\Uninstall.exe" %*
) else if exist "Setup.exe" (
    start "" "Setup.exe" --uninstall %*
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-CleanSlate.ps1" %*
)
