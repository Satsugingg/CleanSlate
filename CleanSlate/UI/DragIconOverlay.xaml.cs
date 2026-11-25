using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using CleanSlate.Core;

namespace CleanSlate.UI;

/// <summary>
/// Floating, click-through semi-transparent preview of the currently dragged dock icon
/// centered cleanly at the cursor.
/// </summary>
public partial class DragIconOverlay : Window
{
    private const double OverlaySizeDip = 176.0;
    private const double GhostOpacity = 0.62;

    private int _pendingPhysLeft;
    private int _pendingPhysTop;
    private int _pendingPhysSize = 176;

    public DragIconOverlay(BitmapSource? icon, double iconSizeDip)
    {
        InitializeComponent();
        GhostHost.Opacity = GhostOpacity;

        double size = Math.Clamp(iconSizeDip, 24.0, 96.0);
        if (icon is not null)
        {
            GhostImage.Source = icon;
            GhostImage.Width = size;
            GhostImage.Height = size;
            GhostImage.Visibility = Visibility.Visible;
            FallbackIconBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            GhostImage.Visibility = Visibility.Collapsed;
            FallbackIconBorder.Width = size;
            FallbackIconBorder.Height = size;
            FallbackIconBorder.Visibility = Visibility.Visible;
        }

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            long newStyle = exStyle.ToInt64()
                | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newStyle));

            AcrylicHelper.EnableAcrylic(this);

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                _pendingPhysLeft,
                _pendingPhysTop,
                _pendingPhysSize,
                _pendingPhysSize,
                NativeMethods.SWP_NOACTIVATE);
        };
    }

    public void UpdatePosition(int cursorPhysX, int cursorPhysY)
    {
        double scale = NativeMethods.GetDpiScaleForPoint(cursorPhysX, cursorPhysY);

        int physSize = Math.Max(1, (int)Math.Round(OverlaySizeDip * scale));
        int physLeft = cursorPhysX - (physSize / 2);
        int physTop = cursorPhysY - (physSize / 2);

        bool sizeChanged = _pendingPhysSize != physSize;
        _pendingPhysLeft = physLeft;
        _pendingPhysTop = physTop;
        _pendingPhysSize = physSize;

        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();

        if (!IsVisible)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                physLeft,
                physTop,
                physSize,
                physSize,
                NativeMethods.SWP_NOACTIVATE);
            Show();
        }
        else
        {
            uint flags = NativeMethods.SWP_NOACTIVATE | (sizeChanged ? 0u : NativeMethods.SWP_NOSIZE);
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                physLeft,
                physTop,
                physSize,
                physSize,
                flags);
        }

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            Point dipOrigin = target.TransformFromDevice.Transform(new Point(physLeft, physTop));
            Left = dipOrigin.X;
            Top = dipOrigin.Y;
        }
        else
        {
            Left = physLeft / scale;
            Top = physTop / scale;
        }

        if (sizeChanged)
        {
            Width = OverlaySizeDip;
            Height = OverlaySizeDip;
        }
    }

    public void SetVisibility(bool isVisible)
    {
        Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void CloseImmediate()
    {
        if (IsLoaded)
        {
            Close();
        }
    }
}
