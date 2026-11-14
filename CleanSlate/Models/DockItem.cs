using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace CleanSlate.Models;

/// <summary>
/// Represents an application shortcut, URL, or file entry displayed in the CleanSlate dock.
/// </summary>
public sealed class DockItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string _path = string.Empty;
    private string _arguments = string.Empty;
    private string? _workingDirectory;
    private string? _iconPath;
    private string? _originalDesktopPath;
    private int? _desktopX;
    private int? _desktopY;
    private DateTime _addedAtUtc = DateTime.UtcNow;
    private BitmapSource? _icon;
    private bool _isDropBefore;
    private bool _isDropAfter;
    private bool _isKeyboardSelected;
    private int? _gridColumn;
    private int? _gridRow;
    private double _canvasX;
    private double _canvasY;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    [JsonPropertyName("path")]
    public string Path
    {
        get => _path;
        set => SetField(ref _path, value);
    }

    [JsonPropertyName("arguments")]
    public string Arguments
    {
        get => _arguments;
        set => SetField(ref _arguments, value);
    }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory
    {
        get => _workingDirectory;
        set => SetField(ref _workingDirectory, value);
    }

    [JsonPropertyName("iconPath")]
    public string? IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    [JsonPropertyName("originalDesktopPath")]
    public string? OriginalDesktopPath
    {
        get => _originalDesktopPath;
        set => SetField(ref _originalDesktopPath, value);
    }

    [JsonPropertyName("desktopX")]
    public int? DesktopX
    {
        get => _desktopX;
        set => SetField(ref _desktopX, value);
    }

    [JsonPropertyName("desktopY")]
    public int? DesktopY
    {
        get => _desktopY;
        set => SetField(ref _desktopY, value);
    }

    [JsonPropertyName("addedAtUtc")]
    public DateTime AddedAtUtc
    {
        get => _addedAtUtc;
        set => SetField(ref _addedAtUtc, value);
    }

    /// <summary>
    /// Cached high-resolution icon decoded on the UI thread; excluded from JSON persistence.
    /// </summary>
    [JsonIgnore]
    public BitmapSource? Icon
    {
        get => _icon;
        set => SetField(ref _icon, value);
    }

    [JsonIgnore]
    public bool IsDropBefore
    {
        get => _isDropBefore;
        set => SetField(ref _isDropBefore, value);
    }

    [JsonIgnore]
    public bool IsDropAfter
    {
        get => _isDropAfter;
        set => SetField(ref _isDropAfter, value);
    }

    [JsonPropertyName("gridCol")]
    public int? GridColumn
    {
        get => _gridColumn;
        set => SetField(ref _gridColumn, value);
    }

    [JsonPropertyName("gridRow")]
    public int? GridRow
    {
        get => _gridRow;
        set => SetField(ref _gridRow, value);
    }

    [JsonIgnore]
    public double CanvasX
    {
        get => _canvasX;
        set => SetField(ref _canvasX, value);
    }

    [JsonIgnore]
    public double CanvasY
    {
        get => _canvasY;
        set => SetField(ref _canvasY, value);
    }

    [JsonIgnore]
    public bool IsKeyboardSelected
    {
        get => _isKeyboardSelected;
        set => SetField(ref _isKeyboardSelected, value);
    }

    [JsonIgnore]
    public bool IsDirectory => !string.IsNullOrWhiteSpace(Path) && Directory.Exists(Path);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
