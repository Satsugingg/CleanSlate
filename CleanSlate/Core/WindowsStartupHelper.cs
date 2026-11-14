using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using Microsoft.Win32;

namespace CleanSlate.Core;

/// <summary>
/// Configures high-performance instant boot startup for CleanSlate via:
/// 1. Windows Task Scheduler 2.0 COM logon task (launches at user logon with Priority 4 Normal and 0 delay,
///    running concurrently with or before Explorer initialization, with no administrative elevation needed).
/// 2. Explorer Startup Delay elimination (HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize\StartupDelayInMSec = 0),
///    which disables Windows' default 10-second delay for startup applications.
/// 3. Traditional HKCU Run registry key fallback (synchronized with single-instance mutex).
/// </summary>
public static class WindowsStartupHelper
{
    public const string TaskName = "CleanSlate";
    public const string StartupValueName = "CleanSlate";
    private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string SerializeRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize";
    private const string StartupDelayValueName = "StartupDelayInMSec";

    public static void ApplyStartupRegistration(string exePath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        if (enabled)
        {
            RegisterLogonTask(exePath);
            SetExplorerStartupDelayBypass(enabled: true);
            SetRunRegistryKey(exePath, enabled: true);
        }
        else
        {
            UnregisterLogonTask();
            SetRunRegistryKey(exePath, enabled: false);
        }
    }

    public static bool RegisterLogonTask(string exePath)
    {
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return false;
            }

            dynamic scheduler = Activator.CreateInstance(schedulerType)!;
            scheduler.Connect();

            dynamic rootFolder = scheduler.GetFolder(@"\");
            dynamic taskDef = scheduler.NewTask(0);

            taskDef.RegistrationInfo.Description = "CleanSlate Instant Startup Task (Zero Delay)";
            taskDef.RegistrationInfo.Author = "CleanSlate";

            string currentUser = WindowsIdentity.GetCurrent().Name;
            taskDef.Principal.UserId = currentUser;
            taskDef.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
            taskDef.Principal.RunLevel = 0;  // TASK_RUNLEVEL_LUA (Standard non-admin token)

            dynamic trigger = taskDef.Triggers.Create(9); // TASK_TRIGGER_LOGON
            trigger.UserId = currentUser;
            trigger.Delay = "PT0S"; // Zero delay
            trigger.Enabled = true;

            dynamic action = taskDef.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = exePath;
            action.WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty;

            dynamic settings = taskDef.Settings;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S"; // Unlimited execution time
            settings.Priority = 4; // Normal priority

            rootFolder.RegisterTaskDefinition(
                TaskName,
                taskDef,
                6, // TASK_CREATE_OR_UPDATE
                null,
                null,
                3  // TASK_LOGON_INTERACTIVE_TOKEN
            );
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to register logon task: {ex.Message}");
            return false;
        }
    }

    public static bool UnregisterLogonTask()
    {
        try
        {
            Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
            if (schedulerType is null)
            {
                return false;
            }

            dynamic scheduler = Activator.CreateInstance(schedulerType)!;
            scheduler.Connect();

            dynamic rootFolder = scheduler.GetFolder(@"\");
            rootFolder.DeleteTask(TaskName, 0);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to unregister logon task: {ex.Message}");
            return false;
        }
    }

    public static void SetExplorerStartupDelayBypass(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(SerializeRegistryPath, writable: true);
                key?.SetValue(StartupDelayValueName, 0, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to configure Explorer startup delay bypass: {ex.Message}");
        }
    }

    public static void SetRunRegistryKey(string exePath, bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath);

            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                key.SetValue(StartupValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to configure Run registry key: {ex.Message}");
        }
    }
}
