using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CleanSlate.Models;
using CleanSlate.Services;

namespace CleanSlate.Core;

/// <summary>
/// Ingests existing desktop files on startup and watches the user and public desktop
/// directories for new shortcuts, URLs, executables, and loose files, relocating them
/// into CleanSlate's managed storage and flat dock items list.
/// </summary>
public sealed class DesktopWatcher : IDisposable
{
    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".crdownload", ".part", ".partial", ".download", ".opdownload"
    };

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "desktop.ini", "thumbs.db", "$recycle.bin"
    };

    private static readonly string[] ShellDefaultNewPrefixes =
    {
        "New Text Document",
        "New Rich Text Document",
        "New Bitmap",
        "New Microsoft",
        "New folder",
        "New Compressed",
        "New Shortcut",
        "New ZIP"
    };

    private readonly record struct ExistingItemSnapshot(string Path, string FileName);
    private readonly record struct ExistingItemMetadataUpdate(string FileName, string DestPath, string EntryPath, DesktopIconPoint? Point);

    private readonly ConfigService _configService;
    private readonly IconExtractor _iconExtractor;
    private readonly Dispatcher _uiDispatcher;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _watcherLock = new();
    private readonly object _moveLock = new();
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler? ShortcutsIngested;

    public DesktopWatcher(
        ConfigService configService,
        IconExtractor iconExtractor,
        Dispatcher uiDispatcher)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _iconExtractor = iconExtractor ?? throw new ArgumentNullException(nameof(iconExtractor));
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_watcherLock)
        {
            if (_watchers.Count > 0)
            {
                return;
            }
        }

        IngestExistingDesktopFiles();

        lock (_watcherLock)
        {
            if (_disposed || _watchers.Count > 0)
            {
                return;
            }

            foreach (string dir in GetDesktopDirectories())
            {
                AttachWatcher(dir);
            }
        }
    }

    public void Stop()
    {
        lock (_watcherLock)
        {
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileCreated;
                watcher.Renamed -= OnFileRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }
            _watchers.Clear();
        }
    }

    private void RestartWatchers()
    {
        lock (_watcherLock)
        {
            if (_disposed)
            {
                return;
            }

            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnFileCreated;
                watcher.Renamed -= OnFileRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }
            _watchers.Clear();

            foreach (string dir in GetDesktopDirectories())
            {
                AttachWatcher(dir);
            }
        }
    }

    private List<ExistingItemSnapshot> CaptureExistingItemsSnapshot(out int nonSettingsItemCount)
    {
        int count = 0;
        List<ExistingItemSnapshot> BuildSnapshot()
        {
            var list = new List<ExistingItemSnapshot>(_configService.Config.Items.Count);
            foreach (DockItem item in _configService.Config.Items)
            {
                if (string.Equals(item.Id, MainWindow.SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                count++;
                string fileName = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetFileName(item.Path) : string.Empty;
                list.Add(new ExistingItemSnapshot(item.Path, fileName));
            }
            return list;
        }

        List<ExistingItemSnapshot> result = _uiDispatcher.CheckAccess() || _uiDispatcher.HasShutdownStarted
            ? BuildSnapshot()
            : _uiDispatcher.Invoke(BuildSnapshot);

        nonSettingsItemCount = count;
        return result;
    }

    /// <summary>
    /// Scans both user and public desktop directories on startup, safely moves all eligible
    /// files into CleanSlate storage, extracts icons, and persists them to config.json.
    /// </summary>
    public void IngestExistingDesktopFiles()
    {
        bool addedAny = false;
        bool configUpdated = false;
        var modifiedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsToAdd = new List<DockItem>();
        var existingUpdates = new List<ExistingItemMetadataUpdate>();
        _configService.EnsureDirectories();

        List<ExistingItemSnapshot> existingSnapshot = CaptureExistingItemsSnapshot(out int existingNonSettingsCount);

        Dictionary<string, DesktopIconPoint> capturedPositions = CaptureDesktopPositionsSafe();
        DesktopIconPoint? capturedRbPos = capturedPositions.TryGetValue(DesktopPositionHelper.RecycleBinParsingPath, out DesktopIconPoint rbPt)
            ? rbPt
            : null;
        if (capturedRbPos is not null)
        {
            configUpdated = true;
        }

        foreach (string dir in GetDesktopDirectories())
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to enumerate '{dir}': {ex.Message}");
                continue;
            }

            foreach (string entryPath in entries)
            {
                if (_disposed)
                {
                    break;
                }

                try
                {
                    if (!CanIngestEntry(entryPath))
                    {
                        continue;
                    }

                    bool isDirectory = Directory.Exists(entryPath);
                    DesktopIconPoint? desktopPoint = DesktopPositionHelper.TryLookupPosition(
                        capturedPositions, entryPath, out DesktopIconPoint pt)
                        ? pt
                        : null;

                    string fileName = Path.GetFileName(entryPath);
                    string destPath;
                    lock (_moveLock)
                    {
                        destPath = BuildUniqueDestinationPath(_configService.ShortcutsDirectory, fileName, isDirectory);
                        if (!TryMoveImmediate(entryPath, destPath, isDirectory))
                        {
                            continue;
                        }
                    }

                    NotifyShellEntryRemoved(entryPath, isDirectory, notifyDirectory: false);
                    modifiedDirs.Add(dir);

                    // Check if an item representing this shortcut already exists in itemsToAdd or existing Config.Items snapshot.
                    bool alreadyInNew = itemsToAdd.Any(i =>
                        string.Equals(i.Path, destPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(i.Path) && string.Equals(Path.GetFileName(i.Path), fileName, StringComparison.OrdinalIgnoreCase)));
                    bool alreadyInConfig = existingSnapshot.Any(i =>
                        string.Equals(i.Path, destPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(i.FileName) && string.Equals(i.FileName, fileName, StringComparison.OrdinalIgnoreCase)));

                    if (alreadyInNew || alreadyInConfig)
                    {
                        if (alreadyInConfig)
                        {
                            existingUpdates.Add(new ExistingItemMetadataUpdate(fileName, destPath, entryPath, desktopPoint));
                            configUpdated = true;
                        }
                        continue;
                    }

                    DockItem item = CreateDockItem(entryPath, destPath, isDirectory, desktopPoint);
                    item.Icon = _iconExtractor.GetIcon(item.Path);
                    itemsToAdd.Add(item);
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"Skipping '{entryPath}' during startup ingest: {ex.Message}");
                }
            }
        }

        // If the dock currently has 0 app shortcuts and the desktop was already clear,
        // sync any unmanaged shortcuts already present in ShortcutsDirectory.
        if (existingNonSettingsCount == 0 &&
            itemsToAdd.Count == 0 &&
            Directory.Exists(_configService.ShortcutsDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_configService.ShortcutsDirectory, "*.*"))
            {
                if (CanIngestEntry(file))
                {
                    try
                    {
                        string fileName = Path.GetFileName(file);
                        if (fileName.Contains(" (2)."))
                        {
                            continue;
                        }

                        if (!itemsToAdd.Any(i => string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase) ||
                                                 string.Equals(Path.GetFileName(i.Path), fileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            DockItem item = CreateDockItem(file, file, false, null);
                            item.Icon = _iconExtractor.GetIcon(item.Path);
                            itemsToAdd.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn($"Failed to sync existing shortcut '{file}': {ex.Message}");
                    }
                }
            }
        }

        if ((itemsToAdd.Count > 0 || existingUpdates.Count > 0 || capturedRbPos is not null) && !_uiDispatcher.HasShutdownStarted)
        {
            _uiDispatcher.Invoke(() =>
            {
                if (capturedRbPos is DesktopIconPoint rb)
                {
                    _configService.Config.RecycleBinPosition = new SavedIconPosition { X = rb.X, Y = rb.Y };
                }

                foreach (ExistingItemMetadataUpdate upd in existingUpdates)
                {
                    var existingItem = _configService.Config.Items.FirstOrDefault(i =>
                        string.Equals(i.Path, upd.DestPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(i.Path) && string.Equals(Path.GetFileName(i.Path), upd.FileName, StringComparison.OrdinalIgnoreCase)));
                    if (existingItem is not null)
                    {
                        existingItem.OriginalDesktopPath = upd.EntryPath;
                        if (upd.Point.HasValue)
                        {
                            existingItem.DesktopX = upd.Point.Value.X;
                            existingItem.DesktopY = upd.Point.Value.Y;
                        }
                    }
                }

                foreach (DockItem item in itemsToAdd)
                {
                    AddItemIfMissing(item);
                }
            });

            if (itemsToAdd.Count > 0)
            {
                addedAny = true;
            }
        }

        if (addedAny || configUpdated)
        {
            _ = _configService.SaveAsync();
        }

        if (addedAny)
        {
            foreach (string modDir in modifiedDirs)
            {
                NotifyShellDirectoryUpdated(modDir);
            }

            if (!_uiDispatcher.HasShutdownStarted)
            {
                _ = _uiDispatcher.BeginInvoke(() => ShortcutsIngested?.Invoke(this, EventArgs.Empty));
            }
        }
    }

    private static Dictionary<string, DesktopIconPoint> CaptureDesktopPositionsSafe()
    {
        try
        {
            // DesktopPositionHelper.CaptureDesktopPositions already marshals onto an STA thread when called off-UI.
            return DesktopPositionHelper.CaptureDesktopPositions();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"CaptureDesktopPositionsSafe failed: {ex.Message}");
            return new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> GetDesktopDirectories()
    {
        string userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        if (!string.IsNullOrWhiteSpace(userDesktop) && Directory.Exists(userDesktop))
        {
            yield return userDesktop;
        }

        if (!string.IsNullOrWhiteSpace(commonDesktop) &&
            Directory.Exists(commonDesktop) &&
            !string.Equals(commonDesktop, userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            yield return commonDesktop;
        }
    }

    private void AttachWatcher(string directoryPath)
    {
        try
        {
            var watcher = new FileSystemWatcher(directoryPath)
            {
                Filter = "*",
                IncludeSubdirectories = false,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            watcher.Created += OnFileCreated;
            watcher.Renamed += OnFileRenamed;
            watcher.Error += OnWatcherError;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DesktopWatcher failed on '{directoryPath}': {ex.Message}");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        AppLogger.Warn($"DesktopWatcher FileSystemWatcher error: {e.GetException().Message}");
        _ = Task.Run(() =>
        {
            RestartWatchers();
            IngestExistingDesktopFiles();
        });
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        _ = IngestNewFileAsync(e.FullPath);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        _ = IngestNewFileAsync(e.FullPath);
    }

    private async Task IngestNewFileAsync(string sourcePath)
    {
        if (_disposed || ShouldIgnorePath(sourcePath))
        {
            return;
        }

        if (!_inFlight.TryAdd(sourcePath, 0))
        {
            return;
        }

        try
        {
            // Wait initial 350ms for Explorer/installer/browser file locks to settle.
            await Task.Delay(350).ConfigureAwait(false);

            // Give the user a grace period while renaming a newly created shell item (e.g. "New Text Document.txt" / inline Edit box).
            if (IsShellDefaultNewName(sourcePath) || NativeMethods.IsDesktopInlineEditActive())
            {
                int waitedMs = 0;
                const int stepMs = 250;
                const int defaultNameGraceMs = 2400;
                const int maxInlineEditGraceMs = 15000;

                while (!_disposed && EntryExists(sourcePath))
                {
                    bool editing = NativeMethods.IsDesktopInlineEditActive();
                    bool stillDefault = IsShellDefaultNewName(sourcePath);

                    if (editing && waitedMs < maxInlineEditGraceMs)
                    {
                        await Task.Delay(stepMs).ConfigureAwait(false);
                        waitedMs += stepMs;
                        continue;
                    }

                    if (stillDefault && waitedMs < defaultNameGraceMs)
                    {
                        await Task.Delay(stepMs).ConfigureAwait(false);
                        waitedMs += stepMs;
                        continue;
                    }

                    break;
                }

                // Short extra settle after user commits inline rename or finishes default grace.
                if (!_disposed && EntryExists(sourcePath))
                {
                    await Task.Delay(250).ConfigureAwait(false);
                }
            }

            if (_disposed || !CanIngestEntry(sourcePath))
            {
                return;
            }

            bool isDirectory = Directory.Exists(sourcePath);

            DesktopIconPoint? desktopPoint = null;
            try
            {
                if (DesktopPositionHelper.TryGetItemPosition(sourcePath, out DesktopIconPoint pt))
                {
                    desktopPoint = pt;
                }
            }
            catch
            {
                // Best-effort only: still ingest even if Explorer hasn't registered the item's coordinates yet.
            }

            _configService.EnsureDirectories();
            string fileName = Path.GetFileName(sourcePath);
            string destPath = await TryMoveWithRetriesAsync(sourcePath, fileName, isDirectory).ConfigureAwait(false);
            if (string.IsNullOrEmpty(destPath))
            {
                return;
            }

            NotifyShellEntryRemoved(sourcePath, isDirectory, notifyDirectory: true);

            DockItem newItem = CreateDockItem(sourcePath, destPath, isDirectory, desktopPoint);
            newItem.Icon = _iconExtractor.GetIcon(newItem.Path);
            if (!_uiDispatcher.HasShutdownStarted)
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    AddItemIfMissing(newItem);
                });
            }

            await _configService.SaveAsync().ConfigureAwait(false);

            if (!_uiDispatcher.HasShutdownStarted)
            {
                _ = _uiDispatcher.BeginInvoke(() =>
                {
                    ShortcutsIngested?.Invoke(this, EventArgs.Empty);
                });
            }

            // Re-flush Explorer Desktop ListView slightly later so async Shell New-Item commits (e.g. Right-Click -> New -> Text Document) cannot leave a stale ghost icon.
            _ = Task.Run(async () =>
            {
                await Task.Delay(450).ConfigureAwait(false);
                if (!_disposed && !EntryExists(sourcePath))
                {
                    NotifyShellEntryRemoved(sourcePath, isDirectory, notifyDirectory: true);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DesktopWatcher ingest error for '{sourcePath}': {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(sourcePath, out _);
        }
    }

    private static DockItem CreateDockItem(string originalPath, string destPath, bool isDirectory, DesktopIconPoint? desktopPoint)
    {
        string displayName = isDirectory
            ? Path.GetFileName(destPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Path.GetFileNameWithoutExtension(originalPath);
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = Path.GetFileName(destPath);
        }

        return new DockItem
        {
            Name = displayName,
            Path = destPath,
            OriginalDesktopPath = originalPath,
            DesktopX = desktopPoint?.X,
            DesktopY = desktopPoint?.Y,
            AddedAtUtc = DateTime.UtcNow
        };
    }

    private void AddItemIfMissing(DockItem item)
    {
        item.Icon ??= _iconExtractor.GetIcon(item.Path);
        string itemFileName = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetFileName(item.Path) : string.Empty;
        var existing = _configService.Config.Items.FirstOrDefault(i =>
            string.Equals(i.Id, item.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(i.Path) && !string.IsNullOrWhiteSpace(itemFileName) &&
             string.Equals(Path.GetFileName(i.Path), itemFileName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(i.Name) && !string.IsNullOrWhiteSpace(item.Name) &&
             string.Equals(i.Name, item.Name, StringComparison.OrdinalIgnoreCase)));

        if (existing is null)
        {
            int settingsIndex = -1;
            for (int i = 0; i < _configService.Config.Items.Count; i++)
            {
                if (string.Equals(_configService.Config.Items[i].Id, MainWindow.SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
                {
                    settingsIndex = i;
                    break;
                }
            }

            if (settingsIndex >= 0)
            {
                // Always insert before the Settings icon so Settings stays at the very bottom by default
                _configService.Config.Items.Insert(settingsIndex, item);
            }
            else
            {
                _configService.Config.Items.Add(item);
            }
        }
        else
        {
            existing.Icon ??= item.Icon ?? _iconExtractor.GetIcon(existing.Path);
            if (item.Icon is not null && existing.Icon is null)
            {
                existing.Icon = item.Icon;
            }
            if (!string.IsNullOrWhiteSpace(item.OriginalDesktopPath))
            {
                existing.OriginalDesktopPath = item.OriginalDesktopPath;
            }
            if (item.DesktopX.HasValue && item.DesktopY.HasValue)
            {
                existing.DesktopX = item.DesktopX;
                existing.DesktopY = item.DesktopY;
            }
        }
    }

    private bool CanIngestEntry(string path)
    {
        if (ShouldIgnorePath(path) || !EntryExists(path))
        {
            return false;
        }

        // Never attempt to move the currently running CleanSlate executable or its storage folders.
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            string.Equals(path, processPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string storage = Path.GetFullPath(_configService.AppDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, storage, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(storage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            FileAttributes attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsShellDefaultNewName(string path)
    {
        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (string prefix in ShellDefaultNewPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldIgnorePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string fileName = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(fileName) ||
            IgnoredFileNames.Contains(fileName) ||
            fileName.StartsWith("~$", StringComparison.Ordinal))
        {
            return true;
        }

        string ext = Path.GetExtension(fileName);
        return IgnoredExtensions.Contains(ext);
    }

    private static string BuildUniqueDestinationPath(string targetDir, string entryName, bool isDirectory)
    {
        string candidate = Path.Combine(targetDir, entryName);
        if (!EntryExists(candidate))
        {
            return candidate;
        }

        string stem = isDirectory ? entryName : Path.GetFileNameWithoutExtension(entryName);
        string ext = isDirectory ? string.Empty : Path.GetExtension(entryName);
        int counter = 2;

        while (true)
        {
            candidate = Path.Combine(targetDir, $"{stem} ({counter}){ext}");
            if (!EntryExists(candidate))
            {
                return candidate;
            }
            counter++;
        }
    }

    private static bool TryMoveImmediate(string sourcePath, string destPath, bool isDirectory)
    {
        try
        {
            MoveEntry(sourcePath, destPath, isDirectory);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private async Task<string> TryMoveWithRetriesAsync(string sourcePath, string fileName, bool isDirectory)
    {
        int[] retryDelaysMs = [150, 250, 400, 600, 800, 1000, 1200];
        for (int attempt = 0; attempt <= retryDelaysMs.Length; attempt++)
        {
            if (_disposed || !EntryExists(sourcePath))
            {
                return string.Empty;
            }

            try
            {
                lock (_moveLock)
                {
                    if (_disposed || !EntryExists(sourcePath))
                    {
                        return string.Empty;
                    }

                    string destPath = BuildUniqueDestinationPath(_configService.ShortcutsDirectory, fileName, isDirectory);
                    MoveEntry(sourcePath, destPath, isDirectory);
                    return destPath;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
            catch (IOException) when (attempt < retryDelaysMs.Length)
            {
                await Task.Delay(retryDelaysMs[attempt]).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                AppLogger.Warn($"Move failed after retries for '{sourcePath}': {ex.Message}");
                return string.Empty;
            }
        }

        return string.Empty;
    }

    private static void MoveEntry(string sourcePath, string destPath, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(sourcePath, destPath);
        }
        else
        {
            File.Move(sourcePath, destPath, overwrite: false);
        }
    }

    private static void NotifyShellEntryRemoved(string removedPath, bool isDirectory, bool notifyDirectory)
    {
        if (string.IsNullOrWhiteSpace(removedPath))
        {
            return;
        }

        IntPtr itemPtr = Marshal.StringToHGlobalUni(removedPath);
        try
        {
            NativeMethods.SHChangeNotify(
                isDirectory ? NativeMethods.SHCNE_RMDIR : NativeMethods.SHCNE_DELETE,
                NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT,
                itemPtr,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(itemPtr);
        }

        if (notifyDirectory)
        {
            string? dir = Path.GetDirectoryName(removedPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                NotifyShellDirectoryUpdated(dir);
            }
        }
    }

    private static void NotifyShellDirectoryUpdated(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return;
        }

        IntPtr dirPtr = Marshal.StringToHGlobalUni(directoryPath);
        try
        {
            NativeMethods.SHChangeNotify(
                NativeMethods.SHCNE_UPDATEDIR,
                NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT,
                dirPtr,
                IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(dirPtr);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
