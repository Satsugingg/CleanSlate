using Microsoft.Win32;

namespace CleanSlate.Core;

/// <summary>
/// Manages Windows virtual shell desktop icon visibility (such as the Recycle Bin GUID)
/// in the current user's Explorer registry configuration and notifies the Shell immediately.
/// </summary>
public static class DesktopIconHelper
{
    private const string RecycleBinClsid = "{645FF040-5081-101B-9F08-00AA002F954E}";

    private static readonly string[] HideDesktopIconsKeys =
    {
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel",
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\ClassicStartMenu"
    };

    /// <summary>
    /// Hides the physical desktop Recycle Bin icon for the current user and refreshes Explorer.
    /// </summary>
    public static void HideDesktopRecycleBin()
    {
        SetDesktopRecycleBinHidden(hidden: true);
    }

    /// <summary>
    /// Restores the physical desktop Recycle Bin icon visibility for the current user.
    /// </summary>
    public static void ShowDesktopRecycleBin()
    {
        SetDesktopRecycleBinHidden(hidden: false);
    }

    private static void SetDesktopRecycleBinHidden(bool hidden)
    {
        int targetValue = hidden ? 1 : 0;
        bool changed = false;

        foreach (string subKeyPath in HideDesktopIconsKeys)
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
                if (key is null)
                {
                    continue;
                }

                object? existing = key.GetValue(RecycleBinClsid);
                if (existing is int current && current == targetValue)
                {
                    continue;
                }

                key.SetValue(RecycleBinClsid, targetValue, RegistryValueKind.DWord);
                changed = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed updating desktop Recycle Bin registry key '{subKeyPath}': {ex.Message}");
            }
        }

        if (changed)
        {
            NativeMethods.SHChangeNotify(
                NativeMethods.SHCNE_ASSOCCHANGED,
                NativeMethods.SHCNF_IDLIST,
                IntPtr.Zero,
                IntPtr.Zero);
        }
    }
}
