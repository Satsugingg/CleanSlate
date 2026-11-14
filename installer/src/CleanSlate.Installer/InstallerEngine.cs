using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using CleanSlate.Core;
using Microsoft.Win32;

namespace CleanSlate.Installer;

public static class InstallerEngine
{
    public const string AppName = "CleanSlate";
    public const string AppVersion = "1.0.0";
    public const string Publisher = "CleanSlate";

    public static string GetTargetAppDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "CleanSlate", "app");
    }

    public static string GetUserDataDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "CleanSlate");
    }

    public static string? ResolveSourceAppDirectory()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. Direct "app" folder next to Setup.exe
        string candidate = Path.Combine(baseDir, "app");
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "CleanSlate.exe")))
        {
            return candidate;
        }

        // 2. Relative to build output (e.g. src\CleanSlate.Installer\bin\...)
        string buildParent = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\app"));
        if (Directory.Exists(buildParent) && File.Exists(Path.Combine(buildParent, "CleanSlate.exe")))
        {
            return buildParent;
        }

        // 3. Current directory ONLY if CleanSlate.exe is present (e.g. portable app root)
        if (File.Exists(Path.Combine(baseDir, "CleanSlate.exe")))
        {
            return baseDir;
        }

        // NEVER return baseDir if CleanSlate.exe is missing!
        return null;
    }

    private static bool TryExtractEmbeddedPayload(string targetDir, Action<string, double>? progress)
    {
        try
        {
            Assembly asm = typeof(InstallerEngine).Assembly;
            string? resourceName = null;
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = name;
                    break;
                }
            }

            if (string.IsNullOrEmpty(resourceName))
            {
                return false;
            }

            using Stream? stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                return false;
            }

            progress?.Invoke("Extracting CleanSlate components...", 30);
            using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read);
            int count = archive.Entries.Count;
            if (count == 0) return false;

            for (int i = 0; i < count; i++)
            {
                ZipArchiveEntry entry = archive.Entries[i];
                if (string.IsNullOrEmpty(entry.Name))
                {
                    string dirPath = Path.Combine(targetDir, entry.FullName);
                    Directory.CreateDirectory(dirPath);
                    continue;
                }

                string destinationPath = Path.Combine(targetDir, entry.FullName);
                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
                double p = 30.0 + ((i + 1.0) / count * 35.0);
                progress?.Invoke($"Extracting {entry.Name}...", p);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed extracting embedded payload: {ex.Message}");
            return false;
        }
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
        catch
        {
            // Ignore process enumeration errors
        }
    }

    public static string ResolveInstalledAppDirectory()
    {
        try
        {
            const string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate";
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(uninstallKeyPath, writable: false);
            if (key?.GetValue("InstallLocation") is string regPath && !string.IsNullOrWhiteSpace(regPath) && Directory.Exists(regPath))
            {
                return Path.GetFullPath(regPath);
            }
        }
        catch { }

        return GetTargetAppDirectory();
    }

    public static bool PerformInstall(string? targetDir, bool autoLaunch, bool registerStartup, bool createShortcuts, Action<string, double>? progress = null)
    {
        try
        {
            progress?.Invoke("Closing existing CleanSlate instances...", 10);
            KillRunningProcesses();
            Thread.Sleep(300);

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                targetDir = GetTargetAppDirectory();
            }
            targetDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(targetDir));

            progress?.Invoke("Preparing installation directory...", 25);
            Directory.CreateDirectory(targetDir);

            bool extracted = TryExtractEmbeddedPayload(targetDir, progress);
            if (!extracted)
            {
                string? sourceDir = ResolveSourceAppDirectory();
                if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir) || !File.Exists(Path.Combine(sourceDir, "CleanSlate.exe")))
                {
                    throw new FileNotFoundException(
                        "CleanSlate application payload was not found. " +
                        "The installer does not contain embedded files, and no valid 'CleanSlate.exe' was located alongside Setup.exe.");
                }

                // Copy all payload files (excluding any installer Setup files)
                string[] files = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                int total = files.Length;
                for (int i = 0; i < total; i++)
                {
                    string file = files[i];
                    string fileName = Path.GetFileName(file);
                    if (fileName.StartsWith("Setup.", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string relPath = Path.GetRelativePath(sourceDir, file);
                    string destPath = Path.Combine(targetDir, relPath);
                    string? destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    File.Copy(file, destPath, overwrite: true);
                    double p = 25.0 + ((i + 1.0) / total * 40.0);
                    progress?.Invoke($"Copying {fileName}...", p);
                }
            }

            // Remove any legacy Setup.exe files from target directory
            foreach (string legacyExt in new[] { ".exe", ".dll", ".runtimeconfig.json", ".deps.json", ".pdb" })
            {
                string legacySetup = Path.Combine(targetDir, "Setup" + legacyExt);
                try { if (File.Exists(legacySetup)) File.Delete(legacySetup); } catch { }
            }

            string targetCleanSlateExe = Path.Combine(targetDir, "CleanSlate.exe");
            string targetSettingsExe = Path.Combine(targetDir, "CleanSlate.Settings.exe");
            string targetUninstallExe = Path.Combine(targetDir, "Uninstall.exe");

            if (!File.Exists(targetCleanSlateExe))
            {
                throw new FileNotFoundException($"Installation failed: CleanSlate.exe was not created in '{targetDir}'.");
            }

            // Ensure Uninstall.exe exists; if not extracted from payload, copy current Setup.exe as Uninstall.exe
            if (!File.Exists(targetUninstallExe))
            {
                try
                {
                    string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (File.Exists(currentExe))
                    {
                        File.Copy(currentExe, targetUninstallExe, overwrite: true);
                    }
                }
                catch { }
            }

            // Create Start Menu Shortcuts
            if (createShortcuts)
            {
                progress?.Invoke("Creating Start Menu shortcuts...", 70);
                string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string lnkCleanSlate = Path.Combine(startMenu, "CleanSlate.lnk");
                string lnkSettings = Path.Combine(startMenu, "CleanSlate Settings.lnk");

                CreateShortcut(lnkCleanSlate, targetCleanSlateExe, "CleanSlate Desktop Dock");
                CreateShortcut(lnkSettings, targetSettingsExe, "CleanSlate Settings");
            }

            // Configure Windows Startup
            if (registerStartup)
            {
                progress?.Invoke("Configuring instant logon startup...", 85);
                CleanSlate.Core.WindowsStartupHelper.ApplyStartupRegistration(targetCleanSlateExe, enabled: true);
            }

            // Register in Windows Programs & Features with dedicated Uninstall.exe
            progress?.Invoke("Registering in Windows Add/Remove Programs...", 92);
            RegisterUninstallEntry(targetDir, targetCleanSlateExe, targetUninstallExe);

            progress?.Invoke("Installation completed!", 100);

            // Launch CleanSlate if requested
            if (autoLaunch && File.Exists(targetCleanSlateExe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = targetCleanSlateExe,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // Ignore launch error
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            progress?.Invoke($"Error: {ex.Message}", 0);
            throw;
        }
    }

    public static bool PerformUninstall(Action<string, double>? progress = null)
    {
        try
        {
            progress?.Invoke("Closing CleanSlate processes...", 10);
            KillRunningProcesses();
            Thread.Sleep(300);

            // Restore all managed desktop shortcuts back to Windows Desktop
            progress?.Invoke("Restoring desktop shortcuts and icon positions...", 20);
            RestoreDesktopRecycleBin();
            RestoreShortcutsToDesktop();

            string targetDir = ResolveInstalledAppDirectory();
            string targetExe = Path.Combine(targetDir, "CleanSlate.exe");

            // Remove startup
            progress?.Invoke("Removing startup tasks...", 35);
            CleanSlate.Core.WindowsStartupHelper.ApplyStartupRegistration(targetExe, enabled: false);

            // Remove Start Menu shortcuts
            progress?.Invoke("Removing Start Menu shortcuts...", 55);
            string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string lnkCleanSlate = Path.Combine(startMenu, "CleanSlate.lnk");
            string lnkSettings = Path.Combine(startMenu, "CleanSlate Settings.lnk");

            try { if (File.Exists(lnkCleanSlate)) File.Delete(lnkCleanSlate); } catch { }
            try { if (File.Exists(lnkSettings)) File.Delete(lnkSettings); } catch { }

            // Remove registry uninstall entry
            progress?.Invoke("Removing Programs & Features entry...", 75);
            UnregisterUninstallEntry();

            // Clean app directory
            progress?.Invoke("Cleaning application files...", 90);
            if (Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, recursive: true);
                }
                catch
                {
                    // Schedule delete on next reboot if files locked
                }
            }

            progress?.Invoke("Uninstalled successfully.", 100);
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

    public static void CreateShortcut(string shortcutPath, string targetPath, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
            shortcut.Description = description;
            shortcut.IconLocation = targetPath + ",0";
            shortcut.Save();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create shortcut {shortcutPath}: {ex.Message}");
        }
    }

    private static void RegisterUninstallEntry(string targetDir, string targetExe, string uninstallExe)
    {
        try
        {
            const string uninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CleanSlate";
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(uninstallKeyPath, writable: true);
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", AppVersion);
            key.SetValue("Publisher", Publisher);
            key.SetValue("DisplayIcon", $"{targetExe},0");
            key.SetValue("InstallLocation", targetDir);
            key.SetValue("UninstallString", $"\"{uninstallExe}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstallExe}\" --silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch
        {
            // Ignore registry write error
        }
    }

    private static void UnregisterUninstallEntry()
    {
        try
        {
            const string parentKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(parentKeyPath, writable: true);
            key?.DeleteSubKeyTree("CleanSlate", throwOnMissingSubKey: false);
        }
        catch
        {
            // Ignore registry error
        }
    }
}
