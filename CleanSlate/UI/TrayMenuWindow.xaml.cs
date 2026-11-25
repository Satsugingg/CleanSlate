using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CleanSlate.Core;

namespace CleanSlate.UI;

/// <summary>
/// Dark frosted custom popup menu anchored to the system tray cursor position.
/// </summary>
public partial class TrayMenuWindow : Window
{
    public bool AllowClose { get; set; }

    public event EventHandler? OpenCleanSlateRequested;
    public event EventHandler? OpenRecycleBinRequested;
    public event EventHandler? OpenShortcutsFolderRequested;
    public event EventHandler? OpenSettingsRequested;
    public event EventHandler? ToggleStartupRequested;
    public event EventHandler? ExitRequested;

    public TrayMenuWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    public void ShowAtCursor(int cursorPhysX, int cursorPhysY, bool isStartupEnabled)
    {
        StartupCheckGlyph.Visibility = isStartupEnabled ? Visibility.Visible : Visibility.Hidden;

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

        // Determine monitor metrics at cursor (using rcMonitor because tray clicks sit inside the taskbar area).
        var pt = new NativeMethods.POINT { X = cursorPhysX, Y = cursorPhysY };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        bool hasMonitorInfo = hMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo);

        uint dpiX = 96;
        if (hMonitor != IntPtr.Zero && NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 && dx > 0)
        {
            dpiX = dx;
        }
        double scale = dpiX / 96.0;

        // Pre-move hidden HWND onto taskbar monitor so WPF adopts target monitor DPI prior to Show/UpdateLayout.
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            cursorPhysX,
            cursorPhysY,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            Point ptDip = target.TransformFromDevice.Transform(new Point(cursorPhysX, cursorPhysY));
            Left = ptDip.X;
            Top = ptDip.Y;
        }
        else
        {
            Left = cursorPhysX / scale;
            Top = cursorPhysY / scale;
        }

        Show();
        UpdateLayout();

        int physW = (int)Math.Round(ActualWidth * scale);
        int physH = (int)Math.Round(ActualHeight * scale);
        int targetX = cursorPhysX - physW + (int)Math.Round(12 * scale);
        int targetY = cursorPhysY - physH;

        if (hasMonitorInfo)
        {
            NativeMethods.RECT bounds = monitorInfo.rcMonitor;
            targetX = Math.Clamp(targetX, bounds.Left, Math.Max(bounds.Left, bounds.Right - physW));
            targetY = Math.Clamp(targetY, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - physH));
        }

        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            targetX,
            targetY,
            physW,
            physH,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } finalTarget)
        {
            Point finalDip = finalTarget.TransformFromDevice.Transform(new Point(targetX, targetY));
            Left = finalDip.X;
            Top = finalDip.Y;
        }

        NativeMethods.ForceForegroundWindow(hwnd);
        Activate();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        Hide();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnOpenCleanSlateClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenCleanSlateRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenRecycleBinClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenRecycleBinRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenShortcutsFolderClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenShortcutsFolderRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOpenSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnToggleStartupClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ToggleStartupRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Hide();
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }
}
