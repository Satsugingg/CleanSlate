using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CleanSlate.Core;
using CleanSlate.UI;

namespace CleanSlate.Services;

/// <summary>
/// Manages the persistent Windows notification area icon via Shell_NotifyIconW,
/// Explorer TaskbarCreated recovery, single-instance wakeup broadcast, and custom tray menu.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private const uint TrayIconId = 1001;
    private const int SummonHotkeyId = 9001;
    private const uint WmTrayCallback = NativeMethods.WM_USER + 1;
    public const string ShowDockBroadcastMessageName = "CleanSlate_ShowDock_Msg";
    public const string SyncSettingsMessageName = "CleanSlate_SyncSettings_Msg";

    private readonly ConfigService _configService;
    private readonly Action _openDockAction;
    private readonly Action _toggleDockAction;
    private readonly Action _openRecycleBinAction;
    private readonly Action _syncSettingsAction;
    private readonly Action _exitAction;
    private TrayMenuWindow? _menuWindow;

    private HwndSource? _hwndSource;
    private IntPtr _iconHandle;
    private uint _taskbarCreatedMsg;
    private uint _showDockBroadcastMsg;
    private uint _syncSettingsMsg;
    private string _registeredHotkey = string.Empty;
    private bool _hotkeyRegistered;
    private bool _iconAdded;
    private bool _disposed;
    private long _lastTraySummonTickMs;

    public TrayIconManager(
        ConfigService configService,
        Action openDockAction,
        Action syncSettingsAction,
        Action exitAction,
        Action? toggleDockAction = null,
        Action? openRecycleBinAction = null)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _openDockAction = openDockAction ?? throw new ArgumentNullException(nameof(openDockAction));
        _toggleDockAction = toggleDockAction ?? _openDockAction;
        _openRecycleBinAction = openRecycleBinAction ?? DefaultOpenRecycleBin;
        _syncSettingsAction = syncSettingsAction ?? throw new ArgumentNullException(nameof(syncSettingsAction));
        _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));
    }

    private TrayMenuWindow EnsureMenuWindow()
    {
        if (_menuWindow is not null)
        {
            return _menuWindow;
        }

        var menu = new TrayMenuWindow();
        menu.OpenCleanSlateRequested += (_, _) => _openDockAction();
        menu.OpenRecycleBinRequested += (_, _) => _openRecycleBinAction();
        menu.OpenShortcutsFolderRequested += (_, _) => OpenShortcutsFolder();
        menu.OpenSettingsRequested += (_, _) => LaunchSettingsApp();
        menu.ToggleStartupRequested += (_, _) => _configService.ToggleWindowsStartup();
        menu.ExitRequested += (_, _) => _exitAction();
        _menuWindow = menu;
        return menu;
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var parameters = new HwndSourceParameters("CleanSlate.TrayHost")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _showDockBroadcastMsg = NativeMethods.RegisterWindowMessageW(ShowDockBroadcastMessageName);
        _syncSettingsMsg = NativeMethods.RegisterWindowMessageW(SyncSettingsMessageName);
        _iconHandle = LoadTrayIconHandle();

        AddTrayIcon();
        ApplySummonHotkey(_configService.Config.Settings.SummonShortcut);
    }

    public void ApplySummonHotkey(string? shortcut)
    {
        if (_disposed || _hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return;
        }

        string normalized = KeybindHelper.NormalizeSummonShortcut(shortcut);
        if (_hotkeyRegistered && string.Equals(normalized, _registeredHotkey, StringComparison.Ordinal))
        {
            return;
        }

        if (_hotkeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, SummonHotkeyId);
            _hotkeyRegistered = false;
            _registeredHotkey = string.Empty;
        }

        if (string.IsNullOrEmpty(normalized))
        {
            return;
        }

        if (KeybindHelper.TryParseGlobalHotKey(normalized, out uint modifiers, out uint vk))
        {
            _hotkeyRegistered = NativeMethods.RegisterHotKey(_hwndSource.Handle, SummonHotkeyId, modifiers, vk);
            if (_hotkeyRegistered)
            {
                _registeredHotkey = normalized;
            }
        }
    }

    private static void DefaultOpenRecycleBin()
    {
        try
        {
            using Process? proc = Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open Recycle Bin: {ex.Message}");
        }
    }

    private IntPtr LoadTrayIconHandle()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
        {
            uint extracted = NativeMethods.ExtractIconExW(processPath, 0, out IntPtr large, out IntPtr small, 1);
            if (large != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(large);
            }
            if (extracted > 0 && small != IntPtr.Zero)
            {
                return small;
            }
        }

        // Fallback: load from embedded WPF resource via temporary file.
        try
        {
            var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CleanSlate.ico"));
            if (streamInfo is not null)
            {
                string tempIco = Path.Combine(Path.GetTempPath(), $"CleanSlate_{Environment.ProcessId}.ico");
                try
                {
                    using (streamInfo.Stream)
                    using (FileStream fs = File.Create(tempIco))
                    {
                        streamInfo.Stream.CopyTo(fs);
                    }

                    int cx = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSMICON);
                    int cy = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSMICON);
                    return NativeMethods.LoadImageW(
                        IntPtr.Zero,
                        tempIco,
                        NativeMethods.IMAGE_ICON,
                        cx > 0 ? cx : 16,
                        cy > 0 ? cy : 16,
                        NativeMethods.LR_LOADFROMFILE);
                }
                finally
                {
                    try { File.Delete(tempIco); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"LoadTrayIconHandle fallback failed: {ex.Message}");
        }

        return IntPtr.Zero;
    }

    private void AddTrayIcon()
    {
        if (_hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return;
        }

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwndSource.Handle,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = WmTrayCallback,
            hIcon = _iconHandle,
            szTip = "CleanSlate — Double-click desktop to open",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };

        _iconAdded = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded || _hwndSource is null || _hwndSource.Handle == IntPtr.Zero)
        {
            return;
        }

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _hwndSource.Handle,
            uID = TrayIconId
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
        _iconAdded = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        uint uMsg = (uint)msg;

        if (_taskbarCreatedMsg != 0 && uMsg == _taskbarCreatedMsg)
        {
            AddTrayIcon();
            handled = true;
            return IntPtr.Zero;
        }

        if (_showDockBroadcastMsg != 0 && uMsg == _showDockBroadcastMsg)
        {
            _openDockAction();
            handled = true;
            return IntPtr.Zero;
        }

        if (_syncSettingsMsg != 0 && uMsg == _syncSettingsMsg)
        {
            _syncSettingsAction();
            handled = true;
            return IntPtr.Zero;
        }

        if (uMsg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == SummonHotkeyId)
        {
            _toggleDockAction();
            handled = true;
            return IntPtr.Zero;
        }

        if (uMsg == WmTrayCallback && (uint)wParam.ToInt64() == TrayIconId)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg == NativeMethods.WM_LBUTTONUP || mouseMsg == NativeMethods.WM_LBUTTONDBLCLK)
            {
                long nowMs = Environment.TickCount64;
                if ((nowMs - _lastTraySummonTickMs) >= 250)
                {
                    _lastTraySummonTickMs = nowMs;
                    _openDockAction();
                }
                handled = true;
            }
            else if (mouseMsg == NativeMethods.WM_RBUTTONUP || mouseMsg == NativeMethods.WM_CONTEXTMENU)
            {
                ShowTrayContextMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        if (_hwndSource is not null)
        {
            NativeMethods.SetForegroundWindow(_hwndSource.Handle);
        }

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            return;
        }

        EnsureMenuWindow().ShowAtCursor(pt.X, pt.Y, _configService.IsWindowsStartupEnabled());
    }

    private void OpenShortcutsFolder()
    {
        try
        {
            _configService.EnsureDirectories();
            using Process? proc = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_configService.ShortcutsDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to open shortcuts folder: {ex.Message}");
        }
    }

    public static void LaunchSettingsApp()
    {
        try
        {
            string? settingsPath = ResolveSettingsExecutablePath();
            if (settingsPath is null)
            {
                MessageBox.Show(
                    "Could not locate CleanSlate.Settings.exe.",
                    "CleanSlate",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            using Process? proc = Process.Start(new ProcessStartInfo(settingsPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to launch CleanSlate.Settings.exe: {ex.Message}");
        }
    }

    private static string? ResolveSettingsExecutablePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string siblingCandidate = Path.Combine(baseDir, "CleanSlate.Settings.exe");
        if (File.Exists(siblingCandidate))
        {
            return siblingCandidate;
        }

        // Walk upward to resolve CleanSlate.Settings build output in the solution layout.
        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        for (int depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            string releaseCandidate = Path.Combine(
                dir.FullName,
                "CleanSlate.Settings",
                "bin",
                "Release",
                "net8.0-windows",
                "CleanSlate.Settings.exe");
            if (File.Exists(releaseCandidate))
            {
                return releaseCandidate;
            }

            string debugCandidate = Path.Combine(
                dir.FullName,
                "CleanSlate.Settings",
                "bin",
                "Debug",
                "net8.0-windows",
                "CleanSlate.Settings.exe");
            if (File.Exists(debugCandidate))
            {
                return debugCandidate;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hotkeyRegistered && _hwndSource is not null && _hwndSource.Handle != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, SummonHotkeyId);
            _hotkeyRegistered = false;
        }
        RemoveTrayIcon();

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }

        if (_menuWindow is not null)
        {
            _menuWindow.AllowClose = true;
            _menuWindow.Close();
            _menuWindow = null;
        }
    }
}
