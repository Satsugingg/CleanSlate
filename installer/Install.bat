@echo off
setlocal
cd /d "%~dp0"
if exist "Setup.exe" (
    start "" "Setup.exe" %*
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CleanSlate.ps1" %*
)
