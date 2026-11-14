using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CleanSlate.Core;
using Microsoft.Win32;

namespace CleanSlate.Settings.Services;

public sealed class DockSettingsModel
{
    public bool ShowBackground { get; set; } = false;
    public bool ShowLabels { get; set; } = true;
    public int IconSize { get; set; } = 48;
    public double DockWidth { get; set; } = 520;
    public double DockHeight { get; set; } = 360;
    public double MiniDesktopWidth { get; set; } = 0;
    public double MiniDesktopHeight { get; set; } = 0;
    public bool AutoLaunchOnStartup { get; set; } = true;
    public bool AutoIngestDesktop { get; set; } = true;
    public string SearchShortcut { get; set; } = KeybindHelper.DefaultSearchShortcut;
    public string SummonShortcut { get; set; } = KeybindHelper.DefaultSummonShortcut;
    public bool MiniDesktopMode { get; set; } = false;
    public bool EnableSearch { get; set; } = true;
    public bool ShowCornerRecycleBin { get; set; } = false;
    public bool ShowSettingsButton { get; set; } = true;
}

public readonly record struct RestoreDesktopResult(
    int RestoredCount,
    int SkippedCount,
    int PositionsRestoredCount = 0,
    bool AutoArrangeEnabled = false);

public readonly record struct TransferDockResult(
    int TransferredCount,
    int AlreadyManagedCount,
    int SkippedCount);

/// <summary>
/// Reads and updates %LocalAppData%\CleanSlate\config.json while preserving existing dock items,
/// manages HKCU Windows startup registration, syncs running CleanSlate.exe, and restores
/// both the Recycle Bin icon and all managed app shortcuts back onto the Windows desktop.
/// </summary>
public sealed class SettingsStore
{
    private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "CleanSlate";
    private const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string TrayHostWindowName = "CleanSlate.TrayHost";
    private const string SyncSettingsMessageName = "CleanSlate_SyncSettings_Msg";

    private static readonly string[] HideDesktopIconsKeys =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public string AppDataDirectory { get; }
    public string ConfigFilePath { get; }
    public string ShortcutsDirectory { get; }

    public SettingsStore()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataDirectory = Path.Combine(localAppData, "CleanSlate");
        ConfigFilePath = Path.Combine(AppDataDirectory, "config.json");
        ShortcutsDirectory = Path.Combine(AppDataDirectory, "Shortcuts");
    }

    public DockSettingsModel Load()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(ShortcutsDirectory);

        var model = new DockSettingsModel();

        if (File.Exists(ConfigFilePath))
        {
            try
            {
                string json = ReadAllTextShared(ConfigFilePath);
                JsonObject? root = JsonNode.Parse(json) as JsonObject;
                if (root?["settings"] is JsonObject settingsObj)
                {
                    if (settingsObj["showBackground"] is JsonValue sb && sb.TryGetValue<bool>(out bool showBg))
                    {
                        model.ShowBackground = showBg;
                    }

                    if (settingsObj["showLabels"] is JsonValue sl && sl.TryGetValue<bool>(out bool showLbl))
                    {
                        model.ShowLabels = showLbl;
                    }

                    if (settingsObj["iconSize"] is JsonValue ic && ic.TryGetValue<int>(out int iconSz))
                    {
                        model.IconSize = Math.Clamp(iconSz, 24, 96);
                    }

                    if (settingsObj["autoLaunchOnStartup"] is JsonValue al && al.TryGetValue<bool>(out bool autoLaunch))
                    {
                        model.AutoLaunchOnStartup = autoLaunch;
                    }

                    if (settingsObj["autoIngestDesktop"] is JsonValue ai && ai.TryGetValue<bool>(out bool autoIngest))
                    {
                        model.AutoIngestDesktop = autoIngest;
                    }

                    if (settingsObj["searchShortcut"] is JsonValue ss && ss.TryGetValue<string>(out string? searchShortcut))
                    {
                        model.SearchShortcut = KeybindHelper.Normalize(searchShortcut);
                    }

                    if (settingsObj["miniDesktopMode"] is JsonValue md && md.TryGetValue<bool>(out bool miniDesktop))
                    {
                        model.MiniDesktopMode = miniDesktop;
                    }

                    if (settingsObj["enableSearch"] is JsonValue es && es.TryGetValue<bool>(out bool enableSearch))
                    {
                        model.EnableSearch = enableSearch;
                    }

                    if (settingsObj["showCornerRecycleBin"] is JsonValue scr && scr.TryGetValue<bool>(out bool showCornerRb))
                    {
                        model.ShowCornerRecycleBin = showCornerRb;
                    }

                    if (settingsObj["showSettingsButton"] is JsonValue ssb && ssb.TryGetValue<bool>(out bool showSettingsBtn))
                    {
                        model.ShowSettingsButton = showSettingsBtn;
                    }

                    if (settingsObj["dockWidth"] is JsonValue dw && dw.TryGetValue<double>(out double dockW) && double.IsFinite(dockW))
                    {
                        model.DockWidth = Math.Clamp(dockW, 300, 3840);
                    }

                    if (settingsObj["dockHeight"] is JsonValue dh && dh.TryGetValue<double>(out double dockH) && double.IsFinite(dockH))
                    {
                        model.DockHeight = Math.Clamp(dockH, 220, 2160);
                    }

                    if (settingsObj["miniDesktopWidth"] is JsonValue mdw && mdw.TryGetValue<double>(out double miniW) && double.IsFinite(miniW))
                    {
                        model.MiniDesktopWidth = miniW > 0 ? Math.Clamp(miniW, 300, 3840) : 0;
                    }

                    if (settingsObj["miniDesktopHeight"] is JsonValue mdh && mdh.TryGetValue<double>(out double miniH) && double.IsFinite(miniH))
                    {
                        model.MiniDesktopHeight = miniH > 0 ? Math.Clamp(miniH, 220, 2160) : 0;
                    }

                    if (settingsObj["summonShortcut"] is JsonValue sum && sum.TryGetValue<string>(out string? summonShortcut))
                    {
                        model.SummonShortcut = KeybindHelper.NormalizeSummonShortcut(summonShortcut);
                    }
                }
            }
            catch
            {
                // Fall back to defaults when config.json is unreadable or malformed.
            }
        }

        model.AutoLaunchOnStartup = IsWindowsStartupEnabled(model.AutoLaunchOnStartup);
        return model;
    }

    public void Save(DockSettingsModel model)
    {
        Directory.CreateDirectory(AppDataDirectory);

        JsonObject root = LoadRootObjectOrCreate();
        ApplySettingsProperties(root, model);

        if (root["items"] is not JsonArray)
        {
            root["items"] = new JsonArray();
        }

        WriteRootObjectAtomic(root);
    }

    public bool IsWindowsStartupEnabled(bool fallback)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            return key?.GetValue(StartupValueName) is string val && !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return fallback;
        }
    }

    public void SetWindowsStartup(bool enabled)
    {
        try
        {
            string? exePath = ResolveCleanSlateExePath();
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                CleanSlate.Core.WindowsStartupHelper.ApplyStartupRegistration(exePath, enabled);
            }
        }
        catch
        {
            // Ignore startup registration errors gracefully.
        }
    }

    /// <summary>
    /// Pauses desktop auto-ingestion on any running CleanSlate instance, restores the native
    /// Windows Recycle Bin icon, moves all managed files from Shortcuts back onto the Desktop,
    /// cleans restored entries from config.json, refreshes Explorer, and restores original icon positions.
    /// </summary>
    public RestoreDesktopResult RestoreAllDesktopIcons(DockSettingsModel model)
    {
        // 1. Disable desktop auto-ingestion first and sync running CleanSlate.exe synchronously so
        //    its FileSystemWatcher stops before files are moved back onto the Desktop.
        model.AutoIngestDesktop = false;
        Save(model);
        NotifyRunningCleanSlate();

        // 2. Restore Windows Recycle Bin icon visibility in Explorer registry.
        foreach (string subKeyPath in HideDesktopIconsKeys)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                key?.SetValue(RecycleBinClsid, 0, RegistryValueKind.DWord);
            }
            catch
            {
                // Ignore registry access errors gracefully.
            }
        }

        // 3. Extract saved original paths and desktop coordinates BEFORE removing items from config.json.
        JsonObject root = LoadRootObjectOrCreate();
        ApplySettingsProperties(root, model);

        var targetPositions = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
        if (TryReadPoint(root["recycleBinPos"] as JsonObject, out DesktopIconPoint recycleBinPt))
        {
            targetPositions[DesktopPositionHelper.RecycleBinParsingPath] = recycleBinPt;
        }

        var savedMetaByManagedPath = BuildSavedItemMetadataMap(root["items"] as JsonArray);

        // 4. Move all managed files in %LocalAppData%\CleanSlate\Shortcuts\ back to the Desktop.
        string userDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var refreshedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(userDesktopDir))
        {
            refreshedDirs.Add(userDesktopDir);
        }

        int restoredCount = 0;
        int skippedCount = 0;

        if (!string.IsNullOrWhiteSpace(userDesktopDir) && Directory.Exists(ShortcutsDirectory))
        {
            Directory.CreateDirectory(userDesktopDir);
            IEnumerable<string> managedEntries;
            try
            {
                managedEntries = Directory.EnumerateFileSystemEntries(ShortcutsDirectory, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                managedEntries = Array.Empty<string>();
            }

            foreach (string srcPath in managedEntries)
            {
                try
                {
                    savedMetaByManagedPath.TryGetValue(NormalizeFullPath(srcPath), out SavedItemRestoreMeta meta);
                    string restoredPath = MoveBackToDesktop(srcPath, userDesktopDir, meta.OriginalDesktopPath);
                    restoredCount++;

                    string? restoredDir = Path.GetDirectoryName(restoredPath);
                    if (!string.IsNullOrWhiteSpace(restoredDir))
                    {
                        refreshedDirs.Add(restoredDir);
                    }

                    if (meta.Position.HasValue)
                    {
                        targetPositions[restoredPath] = meta.Position.Value;
                    }
                }
                catch
                {
                    skippedCount++;
                }
            }
        }

        // 5. Remove restored shortcuts from config.json items list while keeping any non-Shortcuts items.
        if (root["items"] is JsonArray itemsArray)
        {
            string normalizedShortcutsRoot = Path.GetFullPath(ShortcutsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            for (int i = itemsArray.Count - 1; i >= 0; i--)
            {
                string? itemPath = itemsArray[i]?["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(itemPath))
                {
                    itemsArray.RemoveAt(i);
                    continue;
                }

                bool isManagedShortcut = itemPath.StartsWith(normalizedShortcutsRoot, StringComparison.OrdinalIgnoreCase);
                if (isManagedShortcut && !File.Exists(itemPath) && !Directory.Exists(itemPath))
                {
                    itemsArray.RemoveAt(i);
                }
            }
        }

        // 6. Re-enable AutoIngestDesktop in config.json so on the NEXT launch CleanSlate will auto-organize again.
        // We do NOT notify the running CleanSlate here so its watcher remains paused for the rest of this session.
        model.AutoIngestDesktop = true;
        ApplySettingsProperties(root, model);
        WriteRootObjectAtomic(root);

        // 7. Refresh Explorer desktop and restore saved icon coordinates.
        NotifyShellDesktopRefresh(refreshedDirs);

        int positionsRestoredCount = 0;
        bool autoArrangeEnabled = false;
        if (targetPositions.Count > 0)
        {
            DesktopPositionRestoreOutcome posOutcome =
                DesktopPositionHelper.RestoreDesktopPositions(targetPositions, maxWaitMs: 3500);
            positionsRestoredCount = posOutcome.RepositionedCount;
            autoArrangeEnabled = posOutcome.AutoArrangeEnabled;
        }

        return new RestoreDesktopResult(restoredCount, skippedCount, positionsRestoredCount, autoArrangeEnabled);
    }

    /// <summary>
    /// Transfers all desktop shortcuts, URLs, files and unmanaged items back into the CleanSlate dock,
    /// re-enables auto-ingestion, hides the desktop Recycle Bin, and notifies CleanSlate and Explorer.
    /// </summary>
    public TransferDockResult TransferAllDesktopIconsToDock(DockSettingsModel model)
    {
        model.AutoIngestDesktop = true;
        Save(model);

        string userDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var searchDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(userDesktopDir) && Directory.Exists(userDesktopDir))
        {
            searchDirs.Add(userDesktopDir);
        }
        if (!string.IsNullOrWhiteSpace(commonDesktopDir) && Directory.Exists(commonDesktopDir) &&
            !string.Equals(commonDesktopDir, userDesktopDir, StringComparison.OrdinalIgnoreCase))
        {
            searchDirs.Add(commonDesktopDir);
        }

        Directory.CreateDirectory(ShortcutsDirectory);

        JsonObject root = LoadRootObjectOrCreate();
        ApplySettingsProperties(root, model);
        if (root["items"] is not JsonArray itemsArray)
        {
            itemsArray = new JsonArray();
            root["items"] = itemsArray;
        }

        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonNode? node in itemsArray)
        {
            if (node is JsonObject itemObj &&
                itemObj["path"] is JsonValue pv &&
                pv.TryGetValue(out string? p) &&
                !string.IsNullOrWhiteSpace(p))
            {
                existingPaths.Add(NormalizeFullPath(p));
            }
        }

        var desktopPositions = DesktopPositionHelper.CaptureDesktopPositions();
        if (desktopPositions.TryGetValue(DesktopPositionHelper.RecycleBinParsingPath, out DesktopIconPoint rbPt))
        {
            var rbObj = new JsonObject { ["x"] = rbPt.X, ["y"] = rbPt.Y };
            root["recycleBinPos"] = rbObj;
        }

        int transferredCount = 0;
        int alreadyManagedCount = 0;
        int skippedCount = 0;
        var refreshedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ignoredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "desktop.ini", "thumbs.db", "$recycle.bin" };
        var ignoredExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tmp", ".crdownload", ".part", ".partial", ".download" };

        foreach (string dir in searchDirs)
        {
            refreshedDirs.Add(dir);
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch
            {
                continue;
            }

            foreach (string entry in entries)
            {
                string fileName = Path.GetFileName(entry);
                string ext = Path.GetExtension(entry);
                if (ignoredNames.Contains(fileName) || ignoredExts.Contains(ext))
                {
                    continue;
                }

                try
                {
                    FileAttributes attrs = File.GetAttributes(entry);
                    if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                bool isDir = Directory.Exists(entry);
                string destPath = Path.Combine(ShortcutsDirectory, fileName);

                try
                {
                    if (isDir)
                    {
                        if (Directory.Exists(destPath))
                        {
                            Directory.Delete(destPath, recursive: true);
                        }
                        Directory.Move(entry, destPath);
                    }
                    else
                    {
                        File.Move(entry, destPath, overwrite: true);
                    }

                    desktopPositions.TryGetValue(entry, out DesktopIconPoint pt);

                    string displayName = isDir
                        ? Path.GetFileName(destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        : Path.GetFileNameWithoutExtension(entry);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = Path.GetFileName(destPath);
                    }

                    // Check if an item representing this shortcut already exists in itemsArray
                    JsonObject? existingCandidate = null;
                    foreach (JsonNode? node in itemsArray)
                    {
                        if (node is JsonObject cand)
                        {
                            string? candPath = cand["path"]?.GetValue<string>();
                            string? candName = cand["name"]?.GetValue<string>();
                            string candFile = !string.IsNullOrWhiteSpace(candPath) ? Path.GetFileName(candPath) : string.Empty;
                            if (string.Equals(candPath, destPath, StringComparison.OrdinalIgnoreCase) ||
                                (!string.IsNullOrWhiteSpace(candFile) && string.Equals(candFile, fileName, StringComparison.OrdinalIgnoreCase)) ||
                                (!string.IsNullOrWhiteSpace(candName) && string.Equals(candName, displayName, StringComparison.OrdinalIgnoreCase)))
                            {
                                existingCandidate = cand;
                                break;
                            }
                        }
                    }

                    if (existingCandidate is not null)
                    {
                        existingCandidate["path"] = destPath;
                        existingCandidate["originalDesktopPath"] = entry;
                        if (pt.X != 0 || pt.Y != 0)
                        {
                            existingCandidate["desktopX"] = pt.X;
                            existingCandidate["desktopY"] = pt.Y;
                        }
                        existingPaths.Add(NormalizeFullPath(destPath));
                        alreadyManagedCount++;
                        continue;
                    }

                    var itemObj = new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString("N"),
                        ["name"] = displayName,
                        ["path"] = destPath,
                        ["arguments"] = "",
                        ["workingDirectory"] = null,
                        ["iconPath"] = null,
                        ["originalDesktopPath"] = entry,
                        ["desktopX"] = pt.X != 0 || pt.Y != 0 ? pt.X : null,
                        ["desktopY"] = pt.X != 0 || pt.Y != 0 ? pt.Y : null,
                        ["addedAtUtc"] = DateTime.UtcNow.ToString("o"),
                        ["gridCol"] = null,
                        ["gridRow"] = null
                    };

                    int settingsIndex = -1;
                    for (int i = 0; i < itemsArray.Count; i++)
                    {
                        string? id = itemsArray[i]?["id"]?.GetValue<string>();
                        if (string.Equals(id, "__cleanslate_settings__", StringComparison.OrdinalIgnoreCase))
                        {
                            settingsIndex = i;
                            break;
                        }
                    }

                    if (settingsIndex >= 0)
                    {
                        itemsArray.Insert(settingsIndex, itemObj);
                    }
                    else
                    {
                        itemsArray.Add(itemObj);
                    }

                    existingPaths.Add(NormalizeFullPath(destPath));
                    transferredCount++;
                }
                catch
                {
                    skippedCount++;
                }
            }
        }

        // Also check if any existing managed files in ShortcutsDirectory are missing from config items
        try
        {
            foreach (string file in Directory.EnumerateFileSystemEntries(ShortcutsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string fName = Path.GetFileName(file);
                if (fName.Contains(" (2).", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (existingPaths.Contains(NormalizeFullPath(file)))
                {
                    alreadyManagedCount++;
                    continue;
                }

                bool isDir = Directory.Exists(file);
                string displayName = isDir
                    ? Path.GetFileName(file.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    : Path.GetFileNameWithoutExtension(file);

                bool alreadyInArray = false;
                foreach (JsonNode? node in itemsArray)
                {
                    if (node is JsonObject cand)
                    {
                        string? candPath = cand["path"]?.GetValue<string>();
                        string? candName = cand["name"]?.GetValue<string>();
                        string candFile = !string.IsNullOrWhiteSpace(candPath) ? Path.GetFileName(candPath) : string.Empty;
                        if (string.Equals(candPath, file, StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrWhiteSpace(candFile) && string.Equals(candFile, fName, StringComparison.OrdinalIgnoreCase)) ||
                            (!string.IsNullOrWhiteSpace(candName) && string.Equals(candName, displayName, StringComparison.OrdinalIgnoreCase)))
                        {
                            alreadyInArray = true;
                            break;
                        }
                    }
                }
                if (alreadyInArray)
                {
                    alreadyManagedCount++;
                    continue;
                }

                var itemObj = new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString("N"),
                    ["name"] = displayName,
                    ["path"] = file,
                    ["arguments"] = "",
                    ["workingDirectory"] = null,
                    ["iconPath"] = null,
                    ["originalDesktopPath"] = Path.Combine(userDesktopDir, Path.GetFileName(file)),
                    ["desktopX"] = null,
                    ["desktopY"] = null,
                    ["addedAtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["gridCol"] = null,
                    ["gridRow"] = null
                };

                int settingsIndex = -1;
                for (int i = 0; i < itemsArray.Count; i++)
                {
                    string? id = itemsArray[i]?["id"]?.GetValue<string>();
                    if (string.Equals(id, "__cleanslate_settings__", StringComparison.OrdinalIgnoreCase))
                    {
                        settingsIndex = i;
                        break;
                    }
                }

                if (settingsIndex >= 0)
                {
                    itemsArray.Insert(settingsIndex, itemObj);
                }
                else
                {
                    itemsArray.Add(itemObj);
                }

                existingPaths.Add(NormalizeFullPath(file));
                transferredCount++;
            }
        }
        catch { }

        // Hide desktop Recycle Bin in Explorer registry
        foreach (string subKeyPath in HideDesktopIconsKeys)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                key?.SetValue(RecycleBinClsid, 1, RegistryValueKind.DWord);
            }
            catch { }
        }

        WriteRootObjectAtomic(root);
        NotifyShellDesktopRefresh(refreshedDirs);
        NotifyRunningCleanSlate();

        return new TransferDockResult(transferredCount, alreadyManagedCount, skippedCount);
    }

    private static string GetUniqueDestinationPath(string dir, string originalName, bool isDir)
    {
        string nameWithoutExt = isDir ? originalName : Path.GetFileNameWithoutExtension(originalName);
        string ext = isDir ? "" : Path.GetExtension(originalName);
        int counter = 1;
        string candidate = Path.Combine(dir, originalName);
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt} ({counter}){ext}");
            counter++;
        }
        return candidate;
    }

    private readonly record struct SavedItemRestoreMeta(string? OriginalDesktopPath, DesktopIconPoint? Position);

    private static Dictionary<string, SavedItemRestoreMeta> BuildSavedItemMetadataMap(JsonArray? itemsArray)
    {
        var map = new Dictionary<string, SavedItemRestoreMeta>(StringComparer.OrdinalIgnoreCase);
        if (itemsArray is null)
        {
            return map;
        }

        foreach (JsonNode? node in itemsArray)
        {
            if (node is not JsonObject itemObj)
            {
                continue;
            }

            if (itemObj["path"] is not JsonValue pathVal || !pathVal.TryGetValue(out string? itemPath) ||
                string.IsNullOrWhiteSpace(itemPath))
            {
                continue;
            }

            string? origPath = itemObj["originalDesktopPath"] is JsonValue origVal && origVal.TryGetValue(out string? op)
                ? op
                : null;

            DesktopIconPoint? point = null;
            if (TryReadInt(itemObj["desktopX"], out int x) && TryReadInt(itemObj["desktopY"], out int y))
            {
                point = new DesktopIconPoint(x, y);
            }

            map[NormalizeFullPath(itemPath)] = new SavedItemRestoreMeta(origPath, point);
        }

        return map;
    }

    private static string MoveBackToDesktop(string srcPath, string fallbackUserDesktopDir, string? originalDesktopPath)
    {
        bool isDirectory = Directory.Exists(srcPath);
        string preferredFileName = !string.IsNullOrWhiteSpace(originalDesktopPath)
            ? Path.GetFileName(originalDesktopPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileName(srcPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (string.IsNullOrWhiteSpace(preferredFileName))
        {
            preferredFileName = Path.GetFileName(srcPath);
        }

        string? originalDir = !string.IsNullOrWhiteSpace(originalDesktopPath)
            ? Path.GetDirectoryName(originalDesktopPath)
            : null;

        if (!string.IsNullOrWhiteSpace(originalDir) &&
            !string.Equals(NormalizeFullPath(originalDir), NormalizeFullPath(fallbackUserDesktopDir), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(originalDir);
                string preferredDest = BuildUniqueDestinationPath(originalDir, preferredFileName, isDirectory);
                if (isDirectory)
                {
                    Directory.Move(srcPath, preferredDest);
                }
                else
                {
                    File.Move(srcPath, preferredDest);
                }
                return preferredDest;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Fall back to user desktop if CommonDesktop requires elevation or is unavailable.
            }
        }

        string userDest = BuildUniqueDestinationPath(fallbackUserDesktopDir, preferredFileName, isDirectory);
        if (isDirectory)
        {
            Directory.Move(srcPath, userDest);
        }
        else
        {
            File.Move(srcPath, userDest);
        }
        return userDest;
    }

    private static bool TryReadPoint(JsonObject? obj, out DesktopIconPoint point)
    {
        if (obj is not null && TryReadInt(obj["x"], out int x) && TryReadInt(obj["y"], out int y))
        {
            point = new DesktopIconPoint(x, y);
            return true;
        }

        point = default;
        return false;
    }

    private static bool TryReadInt(JsonNode? node, out int value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue(out value))
            {
                return true;
            }

            if (jsonValue.TryGetValue(out double d) && double.IsFinite(d))
            {
                value = (int)Math.Round(d);
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static string NormalizeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    public void NotifyRunningCleanSlate()
    {
        try
        {
            uint syncMsg = NativeMethods.RegisterWindowMessageW(SyncSettingsMessageName);
            if (syncMsg == 0)
            {
                return;
            }

            IntPtr trayHwnd = NativeMethods.FindWindowW(null, TrayHostWindowName);
            if (trayHwnd != IntPtr.Zero)
            {
                _ = NativeMethods.SendMessageTimeoutW(
                    trayHwnd,
                    syncMsg,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    1500,
                    out _);
            }
            else
            {
                NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, syncMsg, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch
        {
            // Ignore IPC sync errors if CleanSlate is not running.
        }
    }

    private static void NotifyShellDesktopRefresh(IEnumerable<string> directories)
    {
        NativeMethods.SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x0000 /* SHCNF_IDLIST */, IntPtr.Zero, IntPtr.Zero);

        foreach (string dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            IntPtr pathPtr = Marshal.StringToHGlobalUni(dir);
            try
            {
                NativeMethods.SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, pathPtr, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }
    }

    private static string BuildUniqueDestinationPath(string targetDir, string entryName, bool isDirectory)
    {
        string candidate = Path.Combine(targetDir, entryName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        string stem = isDirectory ? entryName : Path.GetFileNameWithoutExtension(entryName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(entryName);
        int counter = 2;

        while (true)
        {
            candidate = Path.Combine(targetDir, $"{stem} ({counter}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
            counter++;
        }
    }

    private static void ApplySettingsProperties(JsonObject root, DockSettingsModel model)
    {
        if (root["settings"] is not JsonObject settingsObj)
        {
            settingsObj = new JsonObject();
            root["settings"] = settingsObj;
        }

        settingsObj["showBackground"] = false;
        settingsObj["showLabels"] = model.ShowLabels;
        settingsObj["iconSize"] = Math.Clamp(model.IconSize, 24, 96);
        settingsObj["dockWidth"] = Math.Clamp(model.DockWidth, 300, 3840);
        settingsObj["dockHeight"] = Math.Clamp(model.DockHeight, 220, 2160);
        settingsObj["miniDesktopWidth"] = model.MiniDesktopWidth > 0 ? Math.Clamp(model.MiniDesktopWidth, 300, 3840) : 0;
        settingsObj["miniDesktopHeight"] = model.MiniDesktopHeight > 0 ? Math.Clamp(model.MiniDesktopHeight, 220, 2160) : 0;
        settingsObj["autoLaunchOnStartup"] = model.AutoLaunchOnStartup;
        settingsObj["autoIngestDesktop"] = model.AutoIngestDesktop;
        settingsObj["searchShortcut"] = KeybindHelper.Normalize(model.SearchShortcut);
        settingsObj["summonShortcut"] = KeybindHelper.NormalizeSummonShortcut(model.SummonShortcut);
        settingsObj["miniDesktopMode"] = model.MiniDesktopMode;
        settingsObj["enableSearch"] = model.EnableSearch;
        settingsObj["showCornerRecycleBin"] = model.ShowCornerRecycleBin;
        settingsObj["showSettingsButton"] = model.ShowSettingsButton;
    }

    private void WriteRootObjectAtomic(JsonObject root)
    {
        string output = root.ToJsonString(JsonWriteOptions);
        string tempPath = ConfigFilePath + ".settings.tmp";
        File.WriteAllText(tempPath, output);
        File.Move(tempPath, ConfigFilePath, overwrite: true);
    }

    private JsonObject LoadRootObjectOrCreate()
    {
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                string json = ReadAllTextShared(ConfigFilePath);
                if (JsonNode.Parse(json) is JsonObject parsed)
                {
                    return parsed;
                }
            }
            catch
            {
                // Recreate object if malformed.
            }
        }

        return new JsonObject
        {
            ["settings"] = new JsonObject(),
            ["items"] = new JsonArray()
        };
    }

    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    private static string? ResolveCleanSlateExePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string sibling = Path.Combine(baseDir, "CleanSlate.exe");
        if (File.Exists(sibling))
        {
            return sibling;
        }

        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        for (int depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            string releaseCandidate = Path.Combine(dir.FullName, "CleanSlate", "bin", "Release", "net8.0-windows", "CleanSlate.exe");
            if (File.Exists(releaseCandidate))
            {
                return releaseCandidate;
            }

            string debugCandidate = Path.Combine(dir.FullName, "CleanSlate", "bin", "Debug", "net8.0-windows", "CleanSlate.exe");
            if (File.Exists(debugCandidate))
            {
                return debugCandidate;
            }
        }

        return null;
    }

    internal static class NativeMethods
    {
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

        [DllImport("shell32.dll")]
        public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessageW(string lpString);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
