using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CleanSlate.Core;
using Microsoft.Win32;

namespace CleanSlate.Uninstaller;

public static class UninstallerEngine
{
    public const string AppName = "CleanSlate";
    private const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string GetDefaultAppDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "CleanSlate", "app");
    }

    public static string GetUserDataDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "CleanSlate");
    }

    public static string ResolveTargetDirectory(string? overrideDir = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideDir));
            }
            catch { }
        }

        // 1. Check registry InstallLocation
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKey, writable: false);
            if (key?.GetValue("InstallLocation") is string regPath && !string.IsNullOrWhiteSpace(regPath))
            {
                if (Directory.Exists(regPath))
                {
                    return Path.GetFullPath(regPath);
                }
            }
        }
        catch { }

        // 2. Check sibling directory if CleanSlate.exe is next to us
        string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (File.Exists(Path.Combine(baseDir, "CleanSlate.exe")))
        {
            return baseDir;
        }

        // 3. Fall back to default location
        return GetDefaultAppDirectory();
    }

    public static void KillRunningProcesses()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("CleanSlate"))
            {
                try { p.Kill(); p.WaitForExit(1500); } catch { }
            }
            foreach (Process p in Process.GetProcessesByName("CleanSlate.Settings"))
            {
                try { p.Kill(); p.WaitForExit(1500); } catch { }
            }
        }
        catch { }
    }

    public static bool PerformUninstall(string targetDir, Action<string, double>? progress = null)
    {
        try
        {
            progress?.Invoke("Closing CleanSlate processes...", 10);
            KillRunningProcesses();
            Thread.Sleep(300);

            // 0. ALWAYS restore all managed shortcuts safely back to Windows Desktop first
            progress?.Invoke("Restoring desktop shortcuts and icon positions...", 25);
            RestoreDesktopRecycleBin();
            RestoreShortcutsToDesktop();

            string targetExe = Path.Combine(targetDir, "CleanSlate.exe");

            // 1. Remove startup registration
            progress?.Invoke("Removing startup tasks...", 40);
            RemoveStartupRegistration(targetExe);

            // 2. Remove Start Menu shortcuts
            progress?.Invoke("Removing Start Menu shortcuts...", 60);
            RemoveStartMenuShortcuts();

            // 3. Remove Programs & Features entry
            progress?.Invoke("Removing Programs & Features entry...", 75);
            UnregisterUninstallEntry();

            // 4. Remove application binaries
            progress?.Invoke("Removing application files...", 90);
            if (Directory.Exists(targetDir))
            {
                DeleteDirectorySafely(targetDir);
            }

            progress?.Invoke("CleanSlate has been uninstalled successfully.", 100);
            return true;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"Uninstall error: {ex.Message}", 0);
            throw;
        }
    }

    private static void RestoreShortcutsToDesktop()
    {
        try
        {
            string userDataDir = GetUserDataDirectory();
            string configPath = Path.Combine(userDataDir, "config.json");
            string shortcutsDir = Path.Combine(userDataDir, "Shortcuts");
            string userDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            var targetPositions = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
            var savedMetaByFileName = new Dictionary<string, (string? originalDesktopPath, DesktopIconPoint? position)>(StringComparer.OrdinalIgnoreCase);
            var savedMetaByManagedPath = new Dictionary<string, (string? originalDesktopPath, DesktopIconPoint? position)>(StringComparer.OrdinalIgnoreCase);

            // 1. Read config.json to recover original positions and paths
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("recycleBinPos", out var rbPos) &&
                        rbPos.TryGetProperty("x", out var rbx) &&
                        rbPos.TryGetProperty("y", out var rby) &&
                        rbx.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        rby.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        targetPositions[DesktopPositionHelper.RecycleBinParsingPath] =
                            new DesktopIconPoint(rbx.GetInt32(), rby.GetInt32());
                    }

                    if (root.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            string? path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                            string? origPath = item.TryGetProperty("originalDesktopPath", out var op) ? op.GetString() : null;
                            DesktopIconPoint? pt = null;
                            if (item.TryGetProperty("desktopX", out var dx) &&
                                item.TryGetProperty("desktopY", out var dy) &&
                                dx.ValueKind == System.Text.Json.JsonValueKind.Number &&
                                dy.ValueKind == System.Text.Json.JsonValueKind.Number)
                            {
                                pt = new DesktopIconPoint(dx.GetInt32(), dy.GetInt32());
                            }

                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                string fullPath = Path.GetFullPath(path);
                                savedMetaByManagedPath[fullPath] = (origPath, pt);
                                savedMetaByFileName[Path.GetFileName(path)] = (origPath, pt);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to parse config.json for desktop positions: {ex.Message}");
                }
            }

            // 2. Restore all shortcut files to desktop
            var refreshedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(userDesktopDir))
            {
                refreshedDirs.Add(userDesktopDir);
            }

            if (Directory.Exists(shortcutsDir))
            {
                foreach (string file in Directory.GetFiles(shortcutsDir, "*.*"))
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Contains(" (2).", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        string fullManaged = Path.GetFullPath(file);
                        (string? origPath, DesktopIconPoint? pos) meta = default;
                        if (!savedMetaByManagedPath.TryGetValue(fullManaged, out meta))
                        {
                            savedMetaByFileName.TryGetValue(fileName, out meta);
                        }

                        string targetDest = Path.Combine(userDesktopDir, fileName);
                        if (!string.IsNullOrWhiteSpace(meta.origPath))
                        {
                            try
                            {
                                string? parent = Path.GetDirectoryName(meta.origPath);
                                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                                {
                                    targetDest = meta.origPath;
                                }
                            }
                            catch { }
                        }

                        File.Copy(file, targetDest, overwrite: true);

                        string? targetDir = Path.GetDirectoryName(targetDest);
                        if (!string.IsNullOrWhiteSpace(targetDir))
                        {
                            refreshedDirs.Add(targetDir);
                        }

                        if (meta.pos.HasValue)
                        {
                            targetPositions[targetDest] = meta.pos.Value;
                            targetPositions[fileName] = meta.pos.Value;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed restoring shortcut '{file}': {ex.Message}");
                    }
                }
            }

            // 3. Notify shell of updates
            NotifyShellDesktopRefresh(refreshedDirs);

            // 4. Restore exact icon positions on Desktop ListView via Shell COM
            if (targetPositions.Count > 0)
            {
                DesktopPositionHelper.RestoreDesktopPositions(targetPositions, maxWaitMs: 3500);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RestoreShortcutsToDesktop error: {ex.Message}");
        }
    }

    private static void NotifyShellDesktopRefresh(IEnumerable<string> directories)
    {
        SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);

        foreach (string dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            IntPtr pathPtr = Marshal.StringToHGlobalUni(dir);
            try
            {
                SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, pathPtr, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }
    }

    private static void RestoreDesktopRecycleBin()
    {
        const string recycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
        string[] hideKeys =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"
        };

        foreach (string subKeyPath in hideKeys)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                key?.SetValue(recycleBinClsid, 0, RegistryValueKind.DWord);
            }
            catch { }
        }

        try
        {
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void RemoveStartupRegistration(string targetExe)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/delete /tn \"CleanSlate\" /f",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            proc.Start();
            proc.WaitForExit(3000);
        }
        catch { }

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            key?.DeleteValue("CleanSlate", throwOnMissingValue: false);
        }
        catch { }
    }

    private static void RemoveStartMenuShortcuts()
    {
        try
        {
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string lnkCleanSlate = Path.Combine(startMenu, "CleanSlate.lnk");
            string lnkSettings = Path.Combine(startMenu, "CleanSlate Settings.lnk");

            try { if (File.Exists(lnkCleanSlate)) File.Delete(lnkCleanSlate); } catch { }
            try { if (File.Exists(lnkSettings)) File.Delete(lnkSettings); } catch { }
        }
        catch { }
    }

    private static void UnregisterUninstallEntry()
    {
        try
        {
            const string parentKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(parentKeyPath, writable: true);
            key?.DeleteSubKeyTree("CleanSlate", throwOnMissingSubKey: false);
        }
        catch { }
    }

    private static void DeleteDirectorySafely(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return;

            // Delete all files inside first
            foreach (string file in Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch { }
            }

            Directory.Delete(dirPath, recursive: true);
        }
        catch
        {
            // If locked or busy, schedule delayed cmd deletion
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 2 /nobreak >nul & rd /s /q \"{dirPath}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }
    }
}
