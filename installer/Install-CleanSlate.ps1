# ==============================================================================
# CleanSlate Standalone PowerShell Installer
# Installs CleanSlate to %LocalAppData%\CleanSlate\app and configures shortcuts & startup.
# ==============================================================================

[CmdletBinding()]
param(
    [string]$TargetDir,
    [switch]$Silent,
    [switch]$NoLaunch,
    [switch]$NoStartup,
    [switch]$NoShortcuts
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$sourceDir = Join-Path $scriptDir "app"
if (-not (Test-Path -LiteralPath (Join-Path $sourceDir "CleanSlate.exe"))) {
    Write-Error "CleanSlate binaries not found in: $sourceDir"
    exit 1
}

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $targetDir = Join-Path $localAppData "CleanSlate\app"
} else {
    $targetDir = [System.IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($TargetDir))
}

Write-Host ">>> Stopping any existing CleanSlate instances..." -ForegroundColor Cyan
Get-Process CleanSlate*, CleanSlate.Settings* -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

Write-Host ">>> Installing CleanSlate to $targetDir..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null
Copy-Item -Path "$sourceDir\*" -Destination $targetDir -Recurse -Force

# Remove any old Setup.exe files from target directory
foreach ($ext in @(".exe", ".dll", ".runtimeconfig.json", ".deps.json", ".pdb")) {
    $legacy = Join-Path $targetDir ("Setup" + $ext)
    if (Test-Path -LiteralPath $legacy) {
        Remove-Item -Path $legacy -Force -ErrorAction SilentlyContinue
    }
}

$mainExe = Join-Path $targetDir "CleanSlate.exe"
$settingsExe = Join-Path $targetDir "CleanSlate.Settings.exe"
$uninstallExe = Join-Path $targetDir "Uninstall.exe"

# Create Start Menu Shortcuts
if (-not $NoShortcuts) {
    Write-Host ">>> Creating Start Menu shortcuts..." -ForegroundColor Cyan
    $startMenu = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $wsh = New-Object -ComObject WScript.Shell

    $dockLnk = $wsh.CreateShortcut((Join-Path $startMenu "CleanSlate.lnk"))
    $dockLnk.TargetPath = $mainExe
    $dockLnk.WorkingDirectory = $targetDir
    $dockLnk.Description = "CleanSlate Desktop Dock"
    $dockLnk.IconLocation = "$mainExe,0"
    $dockLnk.Save()

    $settingsLnk = $wsh.CreateShortcut((Join-Path $startMenu "CleanSlate Settings.lnk"))
    $settingsLnk.TargetPath = $settingsExe
    $settingsLnk.WorkingDirectory = $targetDir
    $settingsLnk.Description = "CleanSlate Settings"
    $settingsLnk.IconLocation = "$settingsExe,0"
    $settingsLnk.Save()
}

# Configure Instant Logon Startup
if (-not $NoStartup) {
    Write-Host ">>> Registering instant logon startup..." -ForegroundColor Cyan
    try {
        $taskName = "CleanSlate"
        $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>CleanSlate Instant Desktop Dock Startup</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <Delay>PT0S</Delay>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$mainExe</Command>
      <WorkingDirectory>$targetDir</WorkingDirectory>
    </Exec>
  </Actions>
</Task>
"@
        $tempXml = [System.IO.Path]::GetTempFileName()
        [System.IO.File]::WriteAllText($tempXml, $xml, [System.Text.Encoding]::Unicode)
        schtasks.exe /create /tn $taskName /xml $tempXml /f 2>&1 | Out-Null
        Remove-Item -Path $tempXml -Force -ErrorAction SilentlyContinue

        # Eliminate Explorer 10s startup delay
        $serializePath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize"
        if (-not (Test-Path $serializePath)) { New-Item -Path $serializePath -Force | Out-Null }
        Set-ItemProperty -Path $serializePath -Name "StartupDelayInMSec" -Value 0 -Type DWord

        # HKCU Run Key fallback
        Set-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "CleanSlate" -Value "`"$mainExe`""
    } catch {
        Write-Warning "Could not register Task Scheduler task: $_"
    }
}

# Windows Add/Remove Programs entry
Write-Host ">>> Registering in Windows Add/Remove Programs..." -ForegroundColor Cyan
$uninstallPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate"
if (-not (Test-Path $uninstallPath)) { New-Item -Path $uninstallPath -Force | Out-Null }
Set-ItemProperty -Path $uninstallPath -Name "DisplayName" -Value "CleanSlate"
Set-ItemProperty -Path $uninstallPath -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $uninstallPath -Name "Publisher" -Value "CleanSlate"
Set-ItemProperty -Path $uninstallPath -Name "DisplayIcon" -Value "$mainExe,0"
Set-ItemProperty -Path $uninstallPath -Name "InstallLocation" -Value $targetDir
Set-ItemProperty -Path $uninstallPath -Name "UninstallString" -Value "`"$uninstallExe`""
Set-ItemProperty -Path $uninstallPath -Name "QuietUninstallString" -Value "`"$uninstallExe`" --silent"
Set-ItemProperty -Path $uninstallPath -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $uninstallPath -Name "NoRepair" -Value 1 -Type DWord

Write-Host ">>> CleanSlate installed successfully!" -ForegroundColor Green

if (-not $NoLaunch) {
    Write-Host ">>> Launching CleanSlate..." -ForegroundColor Cyan
    Start-Process -FilePath $mainExe
}
