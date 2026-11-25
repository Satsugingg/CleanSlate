using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace CleanSlate.Core;

/// <summary>
/// Configures high-performance instant boot startup for CleanSlate via:
/// 1. Windows Task Scheduler 2.0 COM logon task (via direct IDispatch without loading Microsoft.CSharp DLR),
///    releasing all COM RCWs deterministically and skipping redundant writes if already up-to-date.
/// 2. Windows Startup folder shortcut fallback when Task Scheduler registration fails (without touching HKCU Run/Serialize keys).
/// </summary>
public static class WindowsStartupHelper
{
    public const string TaskName = "CleanSlate";
    private const string StartupArguments = "--startup";

    public static void ApplyStartupRegistration(string exePath, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return;
        }

        if (enabled)
        {
            bool taskReady = IsLogonTaskConfigured(exePath, StartupArguments) || RegisterLogonTask(exePath);
            SetStartupShortcut(exePath, enabled: !taskReady);
        }
        else
        {
            UnregisterLogonTask();
            SetStartupShortcut(exePath, enabled: false);
        }
    }

    public static bool IsStartupEnabled() => IsLogonTaskRegistered() || IsStartupShortcutPresent();

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
                SetProperty(principal, "LogonType", 3);
                SetProperty(principal, "RunLevel", 0);
            }

            object? triggers = GetProperty(taskDef, "Triggers", comRefs);
            object? trigger = triggers is null ? null : InvokeMethod(triggers, "Create", comRefs, 9);
            if (trigger is not null)
            {
                SetProperty(trigger, "UserId", currentUser);
                SetProperty(trigger, "Delay", "PT0S");
                SetProperty(trigger, "Enabled", true);
            }

            object? actions = GetProperty(taskDef, "Actions", comRefs);
            object? action = actions is null ? null : InvokeMethod(actions, "Create", comRefs, 0);
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
                6,
                null,
                null,
                3);

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

    private static string GetStartupShortcutPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CleanSlate.lnk");

    private static bool IsStartupShortcutPresent() => File.Exists(GetStartupShortcutPath());

    private static void SetStartupShortcut(string exePath, bool enabled)
    {
        string shortcutPath = GetStartupShortcutPath();
        var comRefs = new List<object?>(4);
        try
        {
            if (!enabled)
            {
                if (File.Exists(shortcutPath))
                {
                    File.Delete(shortcutPath);
                }
                return;
            }

            if (File.Exists(shortcutPath))
            {
                return;
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return;
            }

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }
            comRefs.Add(shell);

            object? shortcut = InvokeMethod(shell, "CreateShortcut", comRefs, shortcutPath);
            if (shortcut is null)
            {
                return;
            }

            SetProperty(shortcut, "TargetPath", exePath);
            SetProperty(shortcut, "Arguments", StartupArguments);
            SetProperty(shortcut, "WorkingDirectory", Path.GetDirectoryName(exePath) ?? string.Empty);
            SetProperty(shortcut, "Description", "CleanSlate Desktop Dock");
            SetProperty(shortcut, "IconLocation", $"{exePath},0");
            _ = InvokeMethod(shortcut, "Save", comRefs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to configure startup shortcut: {ex.Message}");
        }
        finally
        {
            ReleaseAllCom(comRefs);
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
                }
            }
        }
    }
}
