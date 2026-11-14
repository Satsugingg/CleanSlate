using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CleanSlate.Core;

namespace CleanSlate.UI;

/// <summary>
/// Configures Windows DWM window attributes cleanly without leaving static DWM borders or
/// unscaled blur regions behind animated AllowsTransparency WPF windows.
/// </summary>
public static class AcrylicHelper
{
    public static void EnableAcrylic(Window window, FrameworkElement? surfaceElement = null, int cornerRadiusDip = 14)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        int darkMode = 1;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
            ref darkMode,
            sizeof(int));

        // Disable Windows 11 default DWM open/close animations so custom Genie animations never double-animate.
        int disableTransitions = 1;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_TRANSITIONS_FORCEDISABLED,
            ref disableTransitions,
            sizeof(int));

        if (window.AllowsTransparency)
        {
            // Explicitly disable Win11 DWM border & outer rounding on layered transparent WPF windows;
            // WPF's own rounded Border paints the frosted surface and animates cleanly without static DWM ghost frames.
            int noRound = NativeMethods.DWMWCP_DONOTROUND;
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref noRound,
                sizeof(int));

            int noBorder = NativeMethods.DWMWA_COLOR_NONE;
            _ = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_BORDER_COLOR,
                ref noBorder,
                sizeof(int));
            return;
        }

        int cornerPreference = NativeMethods.DWMWCP_ROUND;
        _ = NativeMethods.DwmSetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
            ref cornerPreference,
            sizeof(int));

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
            if (NativeMethods.DwmSetWindowAttribute(
                    hwnd,
                    NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                    ref backdrop,
                    sizeof(int)) == 0)
            {
                return;
            }
        }

        ApplyWindowCompositionAcrylic(hwnd);
    }

    private static void ApplyWindowCompositionAcrylic(IntPtr hwnd)
    {
        var accent = new NativeMethods.AccentPolicy
        {
            AccentState = NativeMethods.AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = 0xE6181818, // ABGR dark semi-transparent tint
            AnimationId = 0
        };

        int size = Marshal.SizeOf(accent);
        IntPtr accentPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new NativeMethods.WindowCompositionAttributeData
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = size
            };
            _ = NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }
}
