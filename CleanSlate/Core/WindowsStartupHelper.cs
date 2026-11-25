using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace CleanSlate.Core;

/// <summary>
/// Configures high-performance instant boot startup for CleanSlate via:
/// 1. Windows Task Scheduler 2.0 COM logon task (via direct IDispatch without loading Microsoft.CSharp DLR),
///    releasing all COM RCWs deterministically and skipping redundant writes if already up-to-date.
/// 2. Explorer Startup Delay elimination (HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize\StartupDelayInMSec = 0).
/// 3. Traditional HKCU Run registry key fallback when Task Scheduler registration fails.
/// </summary>
public static class WindowsStartupHelper
{
    public const string TaskName = "CleanSlate";
    public const string StartupValueName = "CleanSlate";
    private const string StartupArguments = "--startup";
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
            bool taskReady = IsLogonTaskConfigured(exePath, StartupArguments) || RegisterLogonTask(exePath);
            SetExplorerStartupDelayBypass(enabled: true);

            if (taskReady)
            {
                // When Task Scheduler provides zero-delay logon startup, remove any HKCU Run duplicate.
                SetRunRegistryKey(exePath, enabled: false);
            }
            else
            {
                // Fallback to traditional Run registry key if Task Scheduler is unavailable.
                SetRunRegistryKey(exePath, enabled: true);
            }
        }
        else
        {
            UnregisterLogonTask();
            SetRunRegistryKey(exePath, enabled: false);
            SetExplorerStartupDelayBypass(enabled: false);
        }
    }

    public static bool IsStartupEnabled()
    {
        if (IsLogonTaskRegistered())
        {
            return true;
        }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            return key?.GetValue(StartupValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsLogonTaskRegistered() => IsLogonTaskConfigured(expectedExePath: null, expectedArgs: null);

    private static bool IsLogonTaskConfigured(string? expectedExePath, string? expectedArgs)
    {
        var comRefs = new List<object?>(8);
        try
        {
            object? scheduler = CreateAndConnectScheduler(comRefs);
            if (scheduler is null)
            {
                return false;
            }

            object? rootFolder = GetFolder(scheduler, @"\", comRefs);
            if (rootFolder is null)
            {
                return false;
            }

            object? task = InvokeMethod(rootFolder, "GetTask", comRefs, TaskName);
            if (task is null)
            {
                return false;
            }

            if (GetProperty(task, "Enabled") is not bool isEnabled || !isEnabled)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(expectedExePath))
            {
                return true;
            }

            object? definition = GetProperty(task, "Definition", comRefs);
            object? actions = definition is null ? null : GetProperty(definition, "Actions", comRefs);
            if (actions is null || GetProperty(actions, "Count") is not int count || count < 1)
            {
                return false;
            }

            object? action = GetProperty(actions, "Item", comRefs, 1);
            if (action is null)
            {
                return false;
            }

            string? path = GetProperty(action, "Path") as string;
            string? args = GetProperty(action, "Arguments") as string;

            bool pathMatches = string.Equals(
                Path.GetFullPath(path ?? string.Empty),
                Path.GetFullPath(expectedExePath),
                StringComparison.OrdinalIgnoreCase);
            bool argsMatch = string.Equals(args?.Trim() ?? string.Empty, expectedArgs ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            return pathMatches && argsMatch;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseAllCom(comRefs);
        }
    }

    public static bool RegisterLogonTask(string exePath)
    {
        var comRefs = new List<object?>(12);
        try
        {
            object? scheduler = CreateAndConnectScheduler(comRefs);
            if (scheduler is null)
            {
                return false;
            }

            object? rootFolder = GetFolder(scheduler, @"\", comRefs);
            object? taskDef = InvokeMethod(scheduler, "NewTask", comRefs, 0);
            if (rootFolder is null || taskDef is null)
            {
                return false;
            }

            object? regInfo = GetProperty(taskDef, "RegistrationInfo", comRefs);
            if (regInfo is not null)
            {
                SetProperty(regInfo, "Description", "CleanSlate Instant Startup Task (Zero Delay)");
                SetProperty(regInfo, "Author", "CleanSlate");
            }

            string currentUser = WindowsIdentity.GetCurrent().Name;
            object? principal = GetProperty(taskDef, "Principal", comRefs);
            if (principal is not null)
            {
                SetProperty(principal, "UserId", currentUser);
                SetProperty(principal, "LogonType", 3); // TASK_LOGON_INTERACTIVE_TOKEN
                SetProperty(principal, "RunLevel", 0);  // TASK_RUNLEVEL_LUA
            }

            object? triggers = GetProperty(taskDef, "Triggers", comRefs);
            object? trigger = triggers is null ? null : InvokeMethod(triggers, "Create", comRefs, 9); // TASK_TRIGGER_LOGON
            if (trigger is not null)
            {
                SetProperty(trigger, "UserId", currentUser);
                SetProperty(trigger, "Delay", "PT0S");
                SetProperty(trigger, "Enabled", true);
            }

            object? actions = GetProperty(taskDef, "Actions", comRefs);
            object? action = actions is null ? null : InvokeMethod(actions, "Create", comRefs, 0); // TASK_ACTION_EXEC
            if (action is null)
            {
                return false;
            }

            SetProperty(action, "Path", exePath);
            SetProperty(action, "Arguments", StartupArguments);
            SetProperty(action, "WorkingDirectory", Path.GetDirectoryName(exePath) ?? string.Empty);

            object? settings = GetProperty(taskDef, "Settings", comRefs);
            if (settings is not null)
            {
                SetProperty(settings, "DisallowStartIfOnBatteries", false);
                SetProperty(settings, "StopIfGoingOnBatteries", false);
                SetProperty(settings, "ExecutionTimeLimit", "PT0S");
                SetProperty(settings, "Priority", 4);
            }

            _ = InvokeMethod(
                rootFolder,
                "RegisterTaskDefinition",
                comRefs,
                TaskName,
                taskDef,
                6, // TASK_CREATE_OR_UPDATE
                null,
                null,
                3); // TASK_LOGON_INTERACTIVE_TOKEN

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to register logon task: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseAllCom(comRefs);
        }
    }

    public static bool UnregisterLogonTask()
    {
        var comRefs = new List<object?>(4);
        try
        {
            object? scheduler = CreateAndConnectScheduler(comRefs);
            if (scheduler is null)
            {
                return false;
            }

            object? rootFolder = GetFolder(scheduler, @"\", comRefs);
            if (rootFolder is null)
            {
                return false;
            }

            _ = InvokeMethod(rootFolder, "DeleteTask", comRefs, TaskName, 0);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to unregister logon task: {ex.Message}");
            return false;
        }
        finally
        {
            ReleaseAllCom(comRefs);
        }
    }

    public static void SetExplorerStartupDelayBypass(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using RegistryKey? existingKey = Registry.CurrentUser.OpenSubKey(SerializeRegistryPath, writable: false);
                if (existingKey?.GetValue(StartupDelayValueName) is int current && current == 0)
                {
                    return;
                }

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
            if (!enabled)
            {
                using RegistryKey? readKey = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
                if (readKey?.GetValue(StartupValueName) is null)
                {
                    return;
                }
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath);

            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                string desired = $"\"{exePath}\" {StartupArguments}";
                if (key.GetValue(StartupValueName) is string current &&
                    string.Equals(current, desired, StringComparison.Ordinal))
                {
                    return;
                }

                key.SetValue(StartupValueName, desired);
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

    private static object? CreateAndConnectScheduler(List<object?> comRefs)
    {
        Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null)
        {
            return null;
        }

        object? scheduler = Activator.CreateInstance(schedulerType);
        if (scheduler is null)
        {
            return null;
        }

        comRefs.Add(scheduler);
        _ = InvokeMethod(scheduler, "Connect", comRefs);
        return scheduler;
    }

    private static object? GetFolder(object scheduler, string path, List<object?> comRefs) =>
        InvokeMethod(scheduler, "GetFolder", comRefs, path);

    private static object? InvokeMethod(object target, string name, List<object?> comRefs, params object?[]? args)
    {
        object? res = target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        if (res is not null && Marshal.IsComObject(res))
        {
            comRefs.Add(res);
        }
        return res;
    }

    private static object? GetProperty(object target, string name, List<object?>? comRefs = null, params object?[]? args)
    {
        object? res = target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args);
        if (comRefs is not null && res is not null && Marshal.IsComObject(res))
        {
            comRefs.Add(res);
        }
        return res;
    }

    private static void SetProperty(object target, string name, object? value)
    {
        _ = target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);
    }

    private static void ReleaseAllCom(List<object?> comRefs)
    {
        for (int i = comRefs.Count - 1; i >= 0; i--)
        {
            object? obj = comRefs[i];
            if (obj is not null && Marshal.IsComObject(obj))
            {
                try
                {
                    Marshal.ReleaseComObject(obj);
                }
                catch
                {
                    // Ignore COM release errors.
                }
            }
        }
    }
}
