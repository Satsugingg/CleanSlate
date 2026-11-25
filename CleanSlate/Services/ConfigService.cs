using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using CleanSlate.Core;
using CleanSlate.Models;
using Microsoft.Win32;

namespace CleanSlate.Services;

/// <summary>
/// Manages persistent JSON state in %LocalAppData%\CleanSlate\config.json,
/// the managed Shortcuts folder, and Windows HKCU startup registration.
/// </summary>
public sealed class ConfigService : IDisposable
{
    private const string RunRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "CleanSlate";

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _disposed;
    private Dispatcher? _uiDispatcher;
    private int _pendingSaves;
    private long _saveSequence;
    private long _lastWrittenSequence;
    private DateTime _lastKnownDiskWriteUtc;
    private long _lastKnownDiskLength = -1;

    public string AppDataDirectory { get; }
    public string ConfigFilePath { get; }
    public string ShortcutsDirectory { get; }
    public AppConfig Config { get; private set; } = AppConfig.CreateDefault();
    public bool IsReloading { get; private set; }

    public void AttachUiDispatcher(Dispatcher uiDispatcher) =>
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

    public ConfigService()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataDirectory = Path.Combine(localAppData, "CleanSlate");
        ConfigFilePath = Path.Combine(AppDataDirectory, "config.json");
        ShortcutsDirectory = Path.Combine(AppDataDirectory, "Shortcuts");
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDataDirectory);
        Directory.CreateDirectory(ShortcutsDirectory);
    }

    public async Task LoadAsync()
    {
        EnsureDirectories();

        if (!File.Exists(ConfigFilePath))
        {
            Config = AppConfig.CreateDefault();
            ApplyWindowsStartupSetting(Config.Settings.AutoLaunchOnStartup);
            await SaveAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            string json = await File.ReadAllTextAsync(ConfigFilePath).ConfigureAwait(false);
            AppConfig? loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);

            if (loaded is null)
            {
                Config = AppConfig.CreateDefault();
                await SaveAsync().ConfigureAwait(false);
                return;
            }

            loaded.Settings ??= new AppSettings();
            loaded.Items ??= new();

            NormalizeSettings(loaded.Settings);
            bool deduplicated = DeduplicateItems(loaded.Items);

            bool migratedLegacyGroups = false;
            if (loaded.Items.Count == 0)
            {
                migratedLegacyGroups = TryMigrateLegacyGroups(json, loaded);
            }

            Config = loaded;
            RecordDiskSnapshotStamp();
            if (migratedLegacyGroups || deduplicated)
            {
                await SaveAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            try
            {
                string backupPath = ConfigFilePath + ".bak";
                File.Copy(ConfigFilePath, backupPath, overwrite: true);
            }
            catch
            {
                // Ignore backup errors when recovering from corrupt config.
            }

            Config = AppConfig.CreateDefault();
            await SaveAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Synchronously reloads settings (and reconciles any external item edits) from disk so changes
    /// saved by CleanSlate.Settings apply immediately when the dock is summoned.
    /// </summary>
    public void ReloadFromDisk(bool force = false)
    {
        if (!File.Exists(ConfigFilePath))
        {
            return;
        }

        IsReloading = true;
        try
        {
            var info = new FileInfo(ConfigFilePath);
            if (!force &&
                _lastKnownDiskWriteUtc != default &&
                info.LastWriteTimeUtc == _lastKnownDiskWriteUtc &&
                info.Length == _lastKnownDiskLength)
            {
                return;
            }

            string json;
            using (var fs = new FileStream(ConfigFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                json = reader.ReadToEnd();
            }

            AppConfig? loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
            if (loaded is null)
            {
                return;
            }

            loaded.Settings ??= new AppSettings();
            NormalizeSettings(loaded.Settings);

            Config.Settings.ShowBackground = false;
            Config.Settings.ShowLabels = loaded.Settings.ShowLabels;
            Config.Settings.IconSize = loaded.Settings.IconSize;
            Config.Settings.AutoLaunchOnStartup = loaded.Settings.AutoLaunchOnStartup;
            Config.Settings.AutoIngestDesktop = loaded.Settings.AutoIngestDesktop;
            Config.Settings.Theme = loaded.Settings.Theme;
            Config.Settings.DockWidth = loaded.Settings.DockWidth;
            Config.Settings.DockHeight = loaded.Settings.DockHeight;
            Config.Settings.MiniDesktopWidth = loaded.Settings.MiniDesktopWidth;
            Config.Settings.MiniDesktopHeight = loaded.Settings.MiniDesktopHeight;
            Config.Settings.SearchShortcut = loaded.Settings.SearchShortcut;
            Config.Settings.MiniDesktopMode = loaded.Settings.MiniDesktopMode;
            Config.Settings.EnableSearch = loaded.Settings.EnableSearch;
            Config.Settings.ShowCornerRecycleBin = loaded.Settings.ShowCornerRecycleBin;
            Config.Settings.ShowSettingsButton = loaded.Settings.ShowSettingsButton;
            Config.Settings.SummonShortcut = loaded.Settings.SummonShortcut;
            Config.RecycleBinPosition = loaded.RecycleBinPosition;

            if (loaded.Items is not null && Volatile.Read(ref _pendingSaves) == 0)
            {
                ReconcileItems(loaded.Items);
            }

            _lastKnownDiskWriteUtc = info.LastWriteTimeUtc;
            _lastKnownDiskLength = info.Length;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"ReloadFromDisk skipped: {ex.Message}");
        }
        finally
        {
            IsReloading = false;
        }
    }

    private void ReconcileItems(IEnumerable<DockItem> diskItems)
    {
        if (_uiDispatcher is not null && !_uiDispatcher.CheckAccess() && !_uiDispatcher.HasShutdownStarted)
        {
            _uiDispatcher.Invoke(() => ReconcileItems(diskItems));
            return;
        }

        var existingByPath = Config.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Path))
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var diskList = diskItems.ToList();
        bool differs = diskList.Count != Config.Items.Count;
        if (!differs)
        {
            for (int i = 0; i < diskList.Count; i++)
            {
                if (!string.Equals(diskList[i].Path, Config.Items[i].Path, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(diskList[i].Name, Config.Items[i].Name, StringComparison.Ordinal) ||
                    diskList[i].GridColumn != Config.Items[i].GridColumn ||
                    diskList[i].GridRow != Config.Items[i].GridRow)
                {
                    differs = true;
                    break;
                }
            }
        }

        if (!differs)
        {
            return;
        }

        DockItem? existingSettings = Config.Items.FirstOrDefault(i => string.Equals(i.Id, "__cleanslate_settings__", StringComparison.OrdinalIgnoreCase));

        Config.Items.Clear();
        foreach (DockItem diskItem in diskList)
        {
            if (existingByPath.TryGetValue(diskItem.Path, out DockItem? existing))
            {
                diskItem.Icon = existing.Icon;
                diskItem.CanvasX = existing.CanvasX;
                diskItem.CanvasY = existing.CanvasY;
            }
            Config.Items.Add(diskItem);
        }

        if (existingSettings is not null && !Config.Items.Any(i => string.Equals(i.Id, "__cleanslate_settings__", StringComparison.OrdinalIgnoreCase)))
        {
            Config.Items.Add(existingSettings);
        }

        DeduplicateItems(Config.Items);
    }

    public static bool DeduplicateItems(System.Collections.ObjectModel.ObservableCollection<DockItem> items)
    {
        if (items is null || items.Count <= 1)
        {
            return false;
        }

        bool removedAny = false;
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            DockItem item = items[i];
            if (string.Equals(item.Id, "__cleanslate_settings__", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string fileName = !string.IsNullOrWhiteSpace(item.Path) ? Path.GetFileName(item.Path) : string.Empty;
            string normKey = fileName.Replace(" (2).", ".");
            if (string.IsNullOrWhiteSpace(normKey))
            {
                normKey = item.Name?.Replace(" (2)", "") ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(normKey) || !seenKeys.Add(normKey))
            {
                items.RemoveAt(i);
                i--;
                removedAny = true;
            }
        }

        return removedAny;
    }

    private static void NormalizeSettings(AppSettings settings)
    {
        if (settings.DockWidth < 320)
        {
            settings.DockWidth = 520;
        }

        if (settings.DockHeight < 220)
        {
            settings.DockHeight = 360;
        }

        if (settings.MiniDesktopWidth < 0)
        {
            settings.MiniDesktopWidth = 0;
        }

        if (settings.MiniDesktopHeight < 0)
        {
            settings.MiniDesktopHeight = 0;
        }

        settings.IconSize = settings.IconSize <= 0
            ? 48
            : Math.Clamp(settings.IconSize, 24, 96);

        settings.SearchShortcut = Core.KeybindHelper.Normalize(settings.SearchShortcut);
        settings.SummonShortcut = Core.KeybindHelper.NormalizeSummonShortcut(settings.SummonShortcut);
    }

    private static bool TryMigrateLegacyGroups(string json, AppConfig target)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("groups", out JsonElement groupsElem) ||
            groupsElem.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool added = false;

        foreach (JsonElement groupElem in groupsElem.EnumerateArray())
        {
            if (!groupElem.TryGetProperty("items", out JsonElement itemsElem) ||
                itemsElem.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement itemElem in itemsElem.EnumerateArray())
            {
                DockItem? item = itemElem.Deserialize(AppConfigJsonContext.Default.DockItem);
                if (item is not null &&
                    !string.IsNullOrWhiteSpace(item.Path) &&
                    seenPaths.Add(item.Path))
                {
                    target.Items.Add(item);
                    added = true;
                }
            }
        }

        return added;
    }

    public async Task SaveAsync()
    {
        EnsureDirectories();
        Interlocked.Increment(ref _pendingSaves);
        long seq = Interlocked.Increment(ref _saveSequence);
        try
        {
            string json = _uiDispatcher is not null && !_uiDispatcher.CheckAccess() && !_uiDispatcher.HasShutdownStarted
                ? await _uiDispatcher.InvokeAsync(() => SerializeConfigSnapshot(Config)).Task.ConfigureAwait(false)
                : SerializeConfigSnapshot(Config);

            await _saveLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (seq < Volatile.Read(ref _lastWrittenSequence))
                {
                    return;
                }

                string tempPath = ConfigFilePath + ".tmp";
                await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);
                File.Move(tempPath, ConfigFilePath, overwrite: true);
                Volatile.Write(ref _lastWrittenSequence, seq);
                RecordDiskSnapshotStamp();
            }
            finally
            {
                _saveLock.Release();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save config.json: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _pendingSaves);
        }
    }

    /// <summary>
    /// Synchronously flushes in-memory config to disk before process exit or shutdown.
    /// </summary>
    public void FlushPendingSave()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            EnsureDirectories();
            if (_saveLock.Wait(2500))
            {
                try
                {
                    string json = _uiDispatcher is not null && !_uiDispatcher.CheckAccess() && !_uiDispatcher.HasShutdownStarted
                        ? _uiDispatcher.Invoke(() => SerializeConfigSnapshot(Config))
                        : SerializeConfigSnapshot(Config);

                    string tempPath = ConfigFilePath + ".tmp";
                    File.WriteAllText(tempPath, json);
                    File.Move(tempPath, ConfigFilePath, overwrite: true);
                    RecordDiskSnapshotStamp();
                }
                finally
                {
                    _saveLock.Release();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"FlushPendingSave failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushPendingSave();
        _disposed = true;
        _saveLock.Dispose();
    }

    private void RecordDiskSnapshotStamp()
    {
        try
        {
            var info = new FileInfo(ConfigFilePath);
            if (info.Exists)
            {
                _lastKnownDiskWriteUtc = info.LastWriteTimeUtc;
                _lastKnownDiskLength = info.Length;
            }
        }
        catch
        {
            // Ignore metadata stamp read errors.
        }
    }

    public bool IsWindowsStartupEnabled()
    {
        return WindowsStartupHelper.IsStartupEnabled();
    }

    public bool ToggleWindowsStartup()
    {
        bool nextState = !IsWindowsStartupEnabled();
        ApplyWindowsStartupSetting(nextState);
        Config.Settings.AutoLaunchOnStartup = nextState;
        _ = SaveAsync();
        return nextState;
    }

    public void ApplyWindowsStartupSetting(bool enabled)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                WindowsStartupHelper.ApplyStartupRegistration(exePath, enabled);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to update Windows startup: {ex.Message}");
        }
    }

    private static string SerializeConfigSnapshot(AppConfig config)
    {
        var clone = new AppConfig
        {
            Settings = config.Settings,
            RecycleBinPosition = config.RecycleBinPosition,
            Items = new ObservableCollection<DockItem>(
                config.Items.Where(i => !string.Equals(i.Id, "__cleanslate_settings__", StringComparison.OrdinalIgnoreCase)))
        };
        return JsonSerializer.Serialize(clone, AppConfigJsonContext.Default.AppConfig);
    }
}
