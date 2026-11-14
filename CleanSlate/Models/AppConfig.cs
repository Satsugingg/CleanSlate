using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace CleanSlate.Models;

/// <summary>
/// Root configuration schema serialized to %LocalAppData%\CleanSlate\config.json.
/// </summary>
public sealed class AppConfig
{
    [JsonPropertyName("settings")]
    public AppSettings Settings { get; set; } = new();

    [JsonPropertyName("items")]
    public ObservableCollection<DockItem> Items { get; set; } = new();

    [JsonPropertyName("recycleBinPos")]
    public SavedIconPosition? RecycleBinPosition { get; set; }

    /// <summary>
    /// Builds a clean default configuration with an empty flat items list.
    /// </summary>
    public static AppConfig CreateDefault()
    {
        return new AppConfig
        {
            Settings = new AppSettings(),
            Items = new ObservableCollection<DockItem>()
        };
    }
}

/// <summary>
/// User preferences and dock dimensions persisted across sessions.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("showBackground")]
    public bool ShowBackground { get; set; } = false;

    [JsonPropertyName("showLabels")]
    public bool ShowLabels { get; set; } = true;

    [JsonPropertyName("iconSize")]
    public int IconSize { get; set; } = 48;

    [JsonPropertyName("autoLaunchOnStartup")]
    public bool AutoLaunchOnStartup { get; set; } = true;

    [JsonPropertyName("autoIngestDesktop")]
    public bool AutoIngestDesktop { get; set; } = true;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "Dark";

    [JsonPropertyName("dockWidth")]
    public double DockWidth { get; set; } = 520;

    [JsonPropertyName("dockHeight")]
    public double DockHeight { get; set; } = 360;

    [JsonPropertyName("miniDesktopWidth")]
    public double MiniDesktopWidth { get; set; } = 0;

    [JsonPropertyName("miniDesktopHeight")]
    public double MiniDesktopHeight { get; set; } = 0;

    [JsonPropertyName("searchShortcut")]
    public string SearchShortcut { get; set; } = "/";

    [JsonPropertyName("miniDesktopMode")]
    public bool MiniDesktopMode { get; set; } = false;

    [JsonPropertyName("enableSearch")]
    public bool EnableSearch { get; set; } = true;

    [JsonPropertyName("showCornerRecycleBin")]
    public bool ShowCornerRecycleBin { get; set; } = false;

    [JsonPropertyName("showSettingsButton")]
    public bool ShowSettingsButton { get; set; } = true;

    [JsonPropertyName("summonShortcut")]
    public string SummonShortcut { get; set; } = "Ctrl+Alt+Space";
}

public sealed class SavedIconPosition
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}
