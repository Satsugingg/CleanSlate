using System.IO;
using System.Runtime.InteropServices;

namespace CleanSlate.Core;

public readonly record struct DesktopIconPoint(int X, int Y);

public sealed record DesktopPositionRestoreOutcome(
    int MatchedCount,
    int RepositionedCount,
    bool AutoArrangeEnabled);

/// <summary>
/// Captures and restores Windows Desktop icon coordinates via Explorer's Shell COM interfaces
/// (<c>IShellWindows</c> -> <c>IShellBrowser</c> -> <c>IFolderView</c>) without cross-process memory injection.
/// </summary>
public static class DesktopPositionHelper
{
    public const string RecycleBinParsingPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioAllView = 0x0;
    private const uint ShgdnForParsing = 0x8000;
    private const uint SvsiPositionItem = 0x0200;

    private static readonly Guid ClsidShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SidTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IidIShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid IidIShellFolder = new("000214E6-0000-0000-C000-000000000046");

    /// <summary>
    /// Reads current (X, Y) coordinates for all desktop items keyed by normalized parsing path
    /// (e.g. file path or <see cref="RecycleBinParsingPath"/>).
    /// </summary>
    public static Dictionary<string, DesktopIconPoint> CaptureDesktopPositions()
    {
        return RunOnSta(CaptureDesktopPositionsCore);
    }

    /// <summary>
    /// Looks up a captured desktop position for <paramref name="desktopFilePath"/> by exact normalized path,
    /// falling back to a unique filename match if present.
    /// </summary>
    public static bool TryLookupPosition(
        IReadOnlyDictionary<string, DesktopIconPoint> captured,
        string desktopFilePath,
        out DesktopIconPoint point)
    {
        if (string.IsNullOrWhiteSpace(desktopFilePath))
        {
            point = default;
            return false;
        }

        string normalized = NormalizeKey(desktopFilePath);
        if (captured.TryGetValue(normalized, out point))
        {
            return true;
        }

        string? fileName = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(fileName))
        {
            point = default;
            return false;
        }

        DesktopIconPoint? uniqueMatch = null;
        foreach ((string key, DesktopIconPoint value) in captured)
        {
            if (IsVirtualShellKey(key))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(key), fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (uniqueMatch.HasValue)
                {
                    point = default;
                    return false;
                }

                uniqueMatch = value;
            }
        }

        if (uniqueMatch.HasValue)
        {
            point = uniqueMatch.Value;
            return true;
        }

        point = default;
        return false;
    }

    /// <summary>
    /// Queries the live desktop position for a single desktop file path.
    /// </summary>
    public static bool TryGetItemPosition(string desktopFilePath, out DesktopIconPoint point)
    {
        Dictionary<string, DesktopIconPoint> captured = CaptureDesktopPositions();
        return TryLookupPosition(captured, desktopFilePath, out point);
    }

    /// <summary>
    /// Waits briefly for Explorer to populate newly restored desktop items (and Recycle Bin),
    /// then repositions all matching icons back to their saved coordinates.
    /// </summary>
    public static DesktopPositionRestoreOutcome RestoreDesktopPositions(
        IReadOnlyDictionary<string, DesktopIconPoint> targetPositions,
        int maxWaitMs = 3000)
    {
        if (targetPositions.Count == 0)
        {
            return new DesktopPositionRestoreOutcome(0, 0, false);
        }

        return RunOnSta(() => RestoreDesktopPositionsCore(targetPositions, maxWaitMs));
    }

    private static Dictionary<string, DesktopIconPoint> CaptureDesktopPositionsCore()
    {
        var result = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!TryGetDesktopViewAndFolder(out IFolderView? folderView, out IShellFolder? shellFolder) ||
                folderView is null || shellFolder is null)
            {
                return result;
            }

            int hrCount = folderView.ItemCount(SvgioAllView, out int count);
            if (hrCount != 0 || count <= 0)
            {
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                IntPtr pidl = IntPtr.Zero;
                try
                {
                    if (folderView.Item(i, out pidl) != 0 || pidl == IntPtr.Zero)
                    {
                        continue;
                    }

                    string? parsingName = GetParsingName(shellFolder, pidl);
                    if (string.IsNullOrWhiteSpace(parsingName))
                    {
                        continue;
                    }

                    if (folderView.GetItemPosition(pidl, out POINT pt) == 0)
                    {
                        string key = NormalizeKey(parsingName);
                        result[key] = new DesktopIconPoint(pt.X, pt.Y);
                    }
                }
                finally
                {
                    if (pidl != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(pidl);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed capturing desktop positions: {ex.Message}");
        }

        return result;
    }

    private static DesktopPositionRestoreOutcome RestoreDesktopPositionsCore(
        IReadOnlyDictionary<string, DesktopIconPoint> targetPositions,
        int maxWaitMs)
    {
        Dictionary<string, DesktopIconPoint> normalizedTargets = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DesktopIconPoint?> byFileName = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string rawKey, DesktopIconPoint pt) in targetPositions)
        {
            if (string.IsNullOrWhiteSpace(rawKey))
            {
                continue;
            }

            string key = NormalizeKey(rawKey);
            normalizedTargets[key] = pt;

            if (!IsVirtualShellKey(key))
            {
                string fileName = Path.GetFileName(key);
                if (!string.IsNullOrEmpty(fileName))
                {
                    if (byFileName.ContainsKey(fileName))
                    {
                        // Mark ambiguous filenames as null so fallback never assigns the wrong slot.
                        byFileName[fileName] = null;
                    }
                    else
                    {
                        byFileName[fileName] = pt;
                    }
                }
            }
        }

        if (normalizedTargets.Count == 0)
        {
            return new DesktopPositionRestoreOutcome(0, 0, false);
        }

        bool autoArrangeEnabled = false;
        HashSet<string> matchedKeys = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> positionedKeys = new(StringComparer.OrdinalIgnoreCase);

        long deadlineTicks = Environment.TickCount64 + Math.Max(200, maxWaitMs);
        bool allSeenOnce = false;

        while (true)
        {
            try
            {
                if (TryGetDesktopViewAndFolder(out IFolderView? folderView, out IShellFolder? shellFolder) &&
                    folderView is not null && shellFolder is not null)
                {
                    autoArrangeEnabled = folderView.GetAutoArrange() == 0;

                    ApplyMatchingPositionsPass(
                        folderView,
                        shellFolder,
                        normalizedTargets,
                        byFileName,
                        matchedKeys,
                        positionedKeys);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreDesktopPositions pass error: {ex.Message}");
            }

            bool remainingMissing = matchedKeys.Count < normalizedTargets.Count;
            if (!remainingMissing)
            {
                if (allSeenOnce)
                {
                    break;
                }

                // Wait briefly and do one confirmation pass in case Explorer settles layout right after SHChangeNotify.
                allSeenOnce = true;
                Thread.Sleep(250);
                continue;
            }

            if (Environment.TickCount64 >= deadlineTicks)
            {
                break;
            }

            Thread.Sleep(200);
        }

        return new DesktopPositionRestoreOutcome(matchedKeys.Count, positionedKeys.Count, autoArrangeEnabled);
    }

    private static void ApplyMatchingPositionsPass(
        IFolderView folderView,
        IShellFolder shellFolder,
        IReadOnlyDictionary<string, DesktopIconPoint> normalizedTargets,
        IReadOnlyDictionary<string, DesktopIconPoint?> byFileName,
        HashSet<string> matchedKeys,
        HashSet<string> positionedKeys)
    {
        if (folderView.ItemCount(SvgioAllView, out int count) != 0 || count <= 0)
        {
            return;
        }

        List<IntPtr> pidlsToFree = new();
        List<IntPtr> pidlsToPosition = new();
        List<POINT> pointsToApply = new();
        List<string> keysToMark = new();

        try
        {
            for (int i = 0; i < count; i++)
            {
                if (folderView.Item(i, out IntPtr pidl) != 0 || pidl == IntPtr.Zero)
                {
                    continue;
                }

                pidlsToFree.Add(pidl);

                string? parsingName = GetParsingName(shellFolder, pidl);
                if (string.IsNullOrWhiteSpace(parsingName))
                {
                    continue;
                }

                string key = NormalizeKey(parsingName);
                if (!TryResolveTargetPoint(key, normalizedTargets, byFileName, out DesktopIconPoint target))
                {
                    continue;
                }

                matchedKeys.Add(key);

                if (folderView.GetItemPosition(pidl, out POINT currentPt) == 0 &&
                    currentPt.X == target.X &&
                    currentPt.Y == target.Y)
                {
                    positionedKeys.Add(key);
                    continue;
                }

                pidlsToPosition.Add(pidl);
                pointsToApply.Add(new POINT { X = target.X, Y = target.Y });
                keysToMark.Add(key);
            }

            if (pidlsToPosition.Count > 0)
            {
                int hr = folderView.SelectAndPositionItems(
                    (uint)pidlsToPosition.Count,
                    pidlsToPosition.ToArray(),
                    pointsToApply.ToArray(),
                    SvsiPositionItem);

                if (hr == 0)
                {
                    foreach (string key in keysToMark)
                    {
                        positionedKeys.Add(key);
                    }
                }
            }
        }
        finally
        {
            foreach (IntPtr pidl in pidlsToFree)
            {
                if (pidl != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pidl);
                }
            }
        }
    }

    private static bool TryResolveTargetPoint(
        string normalizedItemKey,
        IReadOnlyDictionary<string, DesktopIconPoint> normalizedTargets,
        IReadOnlyDictionary<string, DesktopIconPoint?> byFileName,
        out DesktopIconPoint point)
    {
        if (normalizedTargets.TryGetValue(normalizedItemKey, out point))
        {
            return true;
        }

        if (!IsVirtualShellKey(normalizedItemKey))
        {
            string fileName = Path.GetFileName(normalizedItemKey);
            if (!string.IsNullOrEmpty(fileName) &&
                byFileName.TryGetValue(fileName, out DesktopIconPoint? fallback) &&
                fallback.HasValue)
            {
                point = fallback.Value;
                return true;
            }
        }

        point = default;
        return false;
    }

    private static bool TryGetDesktopViewAndFolder(out IFolderView? folderView, out IShellFolder? shellFolder)
    {
        folderView = null;
        shellFolder = null;

        Type? shellWindowsType = Type.GetTypeFromCLSID(ClsidShellWindows);
        if (shellWindowsType is null)
        {
            return false;
        }

        object? shellWindowsObj = Activator.CreateInstance(shellWindowsType);
        if (shellWindowsObj is not IShellWindows shellWindows)
        {
            return false;
        }

        object loc = null!;
        object root = null!;
        int hrFind = shellWindows.FindWindowSW(
            ref loc,
            ref root,
            SwcDesktop,
            out _,
            SwfoNeedDispatch,
            out object? desktopDisp);

        if (hrFind != 0 || desktopDisp is not IServiceProvider serviceProvider)
        {
            return false;
        }

        Guid sidTopLevel = SidTopLevelBrowser;
        Guid iidBrowser = IidIShellBrowser;
        int hrService = serviceProvider.QueryService(ref sidTopLevel, ref iidBrowser, out object? browserObj);
        if (hrService != 0 || browserObj is not IShellBrowser shellBrowser)
        {
            return false;
        }

        if (shellBrowser.QueryActiveShellView(out object? shellViewObj) != 0 ||
            shellViewObj is not IFolderView view)
        {
            return false;
        }

        Guid iidFolder = IidIShellFolder;
        if (view.GetFolder(ref iidFolder, out object? folderObj) != 0 ||
            folderObj is not IShellFolder folder)
        {
            return false;
        }

        folderView = view;
        shellFolder = folder;
        return true;
    }

    private static string? GetParsingName(IShellFolder shellFolder, IntPtr pidl)
    {
        IntPtr pStrRet = Marshal.AllocHGlobal(272);
        try
        {
            for (int offset = 0; offset < 272; offset += 8)
            {
                Marshal.WriteInt64(pStrRet, offset, 0);
            }

            int hr = shellFolder.GetDisplayNameOf(pidl, ShgdnForParsing, pStrRet);
            if (hr != 0)
            {
                return null;
            }

            if (StrRetToBSTR(pStrRet, pidl, out string? result) == 0)
            {
                return result;
            }

            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(pStrRet);
        }
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        string trimmed = key.Trim();
        if (IsVirtualShellKey(trimmed))
        {
            return trimmed;
        }

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch
        {
            return trimmed;
        }
    }

    private static bool IsVirtualShellKey(string key) =>
        key.StartsWith("::", StringComparison.Ordinal);

    private static T RunOnSta<T>(Func<T> func)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return func();
        }

        T? result = default;
        Exception? capturedException = null;

        Thread staThread = new(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        })
        {
            IsBackground = true,
            Name = "CleanSlate.ShellPositionSta"
        };

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (capturedException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(capturedException).Throw();
        }

        return result!;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrRetToBSTR(
        IntPtr pstr,
        IntPtr pidl,
        [MarshalAs(UnmanagedType.BStr)] out string? pbstr);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig] int get_Count(out int Count);
        [PreserveSig] int Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object? Folder);
        [PreserveSig] int _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object? ppunk);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int plCookie);
        [PreserveSig] int RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int plCookie);
        [PreserveSig] int Revoke(int lCookie);
        [PreserveSig] int OnNavigate(int lCookie, ref object pvarLoc);
        [PreserveSig] int OnActivated(int lCookie, bool fActive);
        [PreserveSig] int FindWindowSW(
            ref object pvarLoc,
            ref object pvarLocRoot,
            int swClass,
            out int phwnd,
            int swfwOptions,
            [MarshalAs(UnmanagedType.IDispatch)] out object? ppdispOut);
        [PreserveSig] int OnCreated(int lCookie, [MarshalAs(UnmanagedType.IUnknown)] object punk);
        [PreserveSig] int ProcessAttachDetach(bool fAttach);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(
            ref Guid guidService,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object? ppvObject);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(bool fEnterMode);

        // IShellBrowser
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
        [PreserveSig] int EnableModelessSB(bool fEnable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, out IntPtr lphwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? ppshv);
        [PreserveSig] int OnViewWindowActive([MarshalAs(UnmanagedType.Interface)] object pshv);
        [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView
    {
        [PreserveSig] int GetCurrentViewMode(out uint pViewMode);
        [PreserveSig] int SetCurrentViewMode(uint ViewMode);
        [PreserveSig] int GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? ppv);
        [PreserveSig] int Item(int iItemIndex, out IntPtr ppidl);
        [PreserveSig] int ItemCount(uint uFlags, out int pcItems);
        [PreserveSig] int Items(uint uFlags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetSelectionMarkedItem(out int piItem);
        [PreserveSig] int GetFocusedItem(out int piItem);
        [PreserveSig] int GetItemPosition(IntPtr pidl, out POINT ppt);
        [PreserveSig] int GetSpacing(ref POINT ppt);
        [PreserveSig] int GetDefaultSpacing(out POINT ppt);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int iItem, uint dwFlags);
        [PreserveSig] int SelectAndPositionItems(
            uint cidl,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] apidl,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] POINT[] apt,
            uint dwFlags);
    }

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
    }
}
