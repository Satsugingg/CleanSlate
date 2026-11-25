using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace CleanSlate.Core;

public readonly record struct ScreenBounds(int Left, int Top, int Right, int Bottom);

public sealed class DesktopDoubleClickEventArgs : EventArgs
{
    public DesktopDoubleClickEventArgs(int x, int y)
    {
        X = x;
        Y = y;
    }

    public int X { get; }
    public int Y { get; }
}

/// <summary>
/// Installs a low-level Windows mouse hook (WH_MOUSE_LL) on a dedicated background message loop
/// to detect desktop double-clicks and dismiss the dock immediately when clicking outside it.
/// </summary>
public sealed class DesktopHook : IDisposable
{
    private const long MetricsCacheRefreshMs = 15_000;
    private const uint CrossProcessHitTestTimeoutMs = 20;

    private readonly Dispatcher _uiDispatcher;
    private readonly ManualResetEventSlim _startedEvent = new(false);
    private readonly object _dockStateLock = new();
    private readonly object _lifecycleLock = new();
    private readonly uint _currentProcessId = (uint)Environment.ProcessId;

    private Thread? _hookThread;
    private uint _hookThreadId;
    private IntPtr _hookHandle;
    private NativeMethods.LowLevelMouseProc? _hookProcDelegate;
    private bool _disposed;

    // Dock visibility & physical bounding rect (updated from UI thread, read on hook thread)
    private bool _isDockVisible;
    private bool _isDockAnimating;
    private bool _isDockDragging;
    private IntPtr _dockHwnd;
    private ScreenBounds _dockBounds;
    private Action? _dismissDockAction;

    // Double-click tracking & cached Win32 metrics (accessed exclusively on the hook thread)
    private bool _hasFirstClick;
    private uint _firstClickMsgTimeMs;
    private NativeMethods.POINT _firstClickPt;
    private bool _pendingDoubleClick;
    private NativeMethods.POINT _secondClickDownPt;
    private long _lastTriggerTimeMs;

    private uint _cachedDoubleClickTimeMs = 500;
    private long _lastMetricsRefreshMs;
    private IntPtr _cachedShellHwnd;
    private uint _cachedShellPid;
    private readonly Dictionary<IntPtr, (int MaxDx, int MaxDy, double Scale)> _monitorMetricsCache = new();

    public event EventHandler<DesktopDoubleClickEventArgs>? DesktopDoubleClicked;

    public DesktopHook(Dispatcher uiDispatcher)
    {
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
    }

    public void RegisterDismissHandler(Action dismissDockAction)
    {
        lock (_dockStateLock)
        {
            _dismissDockAction = dismissDockAction;
        }
    }

    public void UpdateDockBounds(
        bool isVisible,
        IntPtr dockHwnd,
        ScreenBounds physicalBounds,
        bool isAnimating = false,
        bool isDragging = false)
    {
        lock (_dockStateLock)
        {
            Volatile.Write(ref _isDockVisible, isVisible);
            _isDockAnimating = isAnimating;
            _isDockDragging = isDragging;
            _dockHwnd = dockHwnd;
            _dockBounds = physicalBounds;
        }
    }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hookThread is not null)
            {
                return;
            }

            _hookThread = new Thread(HookThreadMain)
            {
                Name = "CleanSlate.DesktopHook",
                IsBackground = true
            };
            _lastTriggerTimeMs = Environment.TickCount64;
            _hookThread.Start();
        }

        _startedEvent.Wait(TimeSpan.FromSeconds(3));
    }

    private void HookThreadMain()
    {
        uint tid = NativeMethods.GetCurrentThreadId();
        // Force creation of the thread message queue before publishing _hookThreadId so WM_QUIT never races.
        _ = NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);

        IntPtr hookHandle = IntPtr.Zero;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                _startedEvent.Set();
                return;
            }

            Volatile.Write(ref _hookThreadId, tid);
            _hookProcDelegate = HookCallback;

            IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
            hookHandle = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                _hookProcDelegate,
                moduleHandle,
                0);
            _hookHandle = hookHandle;
        }

        RefreshDoubleClickTimeIfNeeded();
        _startedEvent.Set();

        if (hookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            AppLogger.Warn($"SetWindowsHookExW failed with Win32 error {error} ({new Win32Exception(error).Message}).");
            return;
        }

        try
        {
            while (NativeMethods.GetMessageW(out NativeMethods.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            IntPtr toUnhook = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
            if (toUnhook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(toUnhook);
            }
            Volatile.Write(ref _hookThreadId, 0);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_LBUTTONDOWN ||
                msg == NativeMethods.WM_RBUTTONDOWN ||
                msg == NativeMethods.WM_LBUTTONUP)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // Ignore synthetic / injected mouse events (auto-clickers, remote macros, UI automation).
                if ((hookStruct.flags & (NativeMethods.LLMHF_INJECTED | NativeMethods.LLMHF_LOWER_IL_INJECTED)) == 0)
                {
                    NativeMethods.POINT pt = hookStruct.pt;

                    if (msg == NativeMethods.WM_LBUTTONDOWN || msg == NativeMethods.WM_RBUTTONDOWN)
                    {
                        if (Volatile.Read(ref _isDockVisible) && TryDismissIfClickedOutsideDock(pt))
                        {
                            _hasFirstClick = false;
                            _pendingDoubleClick = false;
                            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                        }

                        if (msg == NativeMethods.WM_LBUTTONDOWN)
                        {
                            HandleLeftButtonDown(pt, hookStruct.time);
                        }
                        else
                        {
                            // Right-click breaks any pending single left-click double-click sequence.
                            _hasFirstClick = false;
                            _pendingDoubleClick = false;
                        }
                    }
                    else
                    {
                        HandleLeftButtonUp(pt, hookStruct.time);
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private bool TryDismissIfClickedOutsideDock(NativeMethods.POINT pt)
    {
        bool isVisible;
        bool isAnimating;
        bool isDragging;
        IntPtr dockHwnd;
        ScreenBounds bounds;
        Action? dismissAction;

        lock (_dockStateLock)
        {
            isVisible = _isDockVisible;
            isAnimating = _isDockAnimating;
            isDragging = _isDockDragging;
            dockHwnd = _dockHwnd;
            bounds = _dockBounds;
            dismissAction = _dismissDockAction;
        }

        if (!isVisible || isAnimating || isDragging || dismissAction is null || dockHwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(dockHwnd))
        {
            return false;
        }

        // Fallback to live GetWindowRect if cached bounds are empty.
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            if (!NativeMethods.GetWindowRect(dockHwnd, out NativeMethods.RECT liveRect))
            {
                return false;
            }
            bounds = new ScreenBounds(liveRect.Left, liveRect.Top, liveRect.Right, liveRect.Bottom);
        }

        if (IsPointInBounds(pt, bounds))
        {
            return false;
        }

        // Allow clicks on CleanSlate's own popup ContextMenu windows even if they extend beyond dock edges.
        IntPtr hitHwnd = NativeMethods.WindowFromPoint(pt);
        if (hitHwnd != IntPtr.Zero && hitHwnd != dockHwnd)
        {
            NativeMethods.GetWindowThreadProcessId(hitHwnd, out uint hitPid);
            if (hitPid == _currentProcessId)
            {
                return false;
            }
        }

        if (!_uiDispatcher.HasShutdownStarted)
        {
            try
            {
                _ = _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, dismissAction);
            }
            catch
            {
                // Ignore dispatcher shutdown races.
            }
        }

        return true;
    }

    private bool IsPointInsideVisibleDock(NativeMethods.POINT pt)
    {
        if (!Volatile.Read(ref _isDockVisible))
        {
            return false;
        }

        lock (_dockStateLock)
        {
            if (!_isDockVisible || (!_isDockAnimating && (_dockHwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(_dockHwnd))))
            {
                return false;
            }

            return IsPointInBounds(pt, _dockBounds);
        }
    }

    private static bool IsPointInBounds(NativeMethods.POINT pt, ScreenBounds rect)
    {
        return pt.X >= rect.Left && pt.X <= rect.Right &&
               pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

    private void HandleLeftButtonDown(NativeMethods.POINT pt, uint eventTimeMs)
    {
        if (IsPointInsideVisibleDock(pt) ||
            !IsDesktopSurfaceAtPoint(pt, out IntPtr listViewHwnd, out uint desktopPid))
        {
            _hasFirstClick = false;
            _pendingDoubleClick = false;
            return;
        }

        // Do not start or complete a dock double-click sequence if the user clicked a desktop icon/label.
        if (listViewHwnd != IntPtr.Zero && IsPointOnDesktopIcon(pt, listViewHwnd, desktopPid))
        {
            _hasFirstClick = false;
            _pendingDoubleClick = false;
            return;
        }

        uint maxIntervalMs = RefreshDoubleClickTimeIfNeeded();
        (int maxDx, int maxDy, _) = GetDoubleClickMetricsForPoint(pt);
        uint elapsedMs = unchecked(eventTimeMs - _firstClickMsgTimeMs);

        if (_hasFirstClick &&
            elapsedMs <= maxIntervalMs &&
            Math.Abs(pt.X - _firstClickPt.X) <= maxDx &&
            Math.Abs(pt.Y - _firstClickPt.Y) <= maxDy)
        {
            // Arm the trigger for WM_LBUTTONUP so desktop focus settles before summoning the dock.
            _pendingDoubleClick = true;
            _secondClickDownPt = pt;
            _hasFirstClick = false;
        }
        else
        {
            _hasFirstClick = true;
            _firstClickMsgTimeMs = eventTimeMs;
            _firstClickPt = pt;
            _pendingDoubleClick = false;
        }
    }

    private void HandleLeftButtonUp(NativeMethods.POINT upPt, uint eventTimeMs)
    {
        if (!_pendingDoubleClick)
        {
            return;
        }

        _pendingDoubleClick = false;

        uint maxIntervalMs = RefreshDoubleClickTimeIfNeeded();
        uint sinceFirstClickMs = unchecked(eventTimeMs - _firstClickMsgTimeMs);

        // If the click was held or mouse-up took longer than the double-click window, it was a hold/drag.
        if (sinceFirstClickMs > (maxIntervalMs + 400))
        {
            return;
        }

        // Ignore marquee drag selections started immediately after a click (DPI-aware threshold).
        (_, _, double scale) = GetDoubleClickMetricsForPoint(upPt);
        int dragJitterThresholdPx = Math.Max(8, (int)Math.Round(12.0 * scale));
        if (Math.Abs(upPt.X - _secondClickDownPt.X) > dragJitterThresholdPx ||
            Math.Abs(upPt.Y - _secondClickDownPt.Y) > dragJitterThresholdPx)
        {
            return;
        }

        if (!IsDockCurrentlyActive() &&
            IsDesktopSurfaceAtPoint(upPt, out IntPtr listViewHwnd, out uint desktopPid) &&
            (listViewHwnd == IntPtr.Zero || !IsPointOnDesktopIcon(upPt, listViewHwnd, desktopPid)))
        {
            long nowMs = Environment.TickCount64;
            if ((nowMs - _lastTriggerTimeMs) >= 350)
            {
                _lastTriggerTimeMs = nowMs;
                RaiseDesktopDoubleClick(upPt.X, upPt.Y);
            }
        }
    }

    private uint RefreshDoubleClickTimeIfNeeded()
    {
        long nowMs = Environment.TickCount64;
        if (_lastMetricsRefreshMs == 0 || (nowMs - _lastMetricsRefreshMs) >= MetricsCacheRefreshMs)
        {
            uint interval = NativeMethods.GetDoubleClickTime();
            _cachedDoubleClickTimeMs = interval == 0 ? 500 : interval;
            _lastMetricsRefreshMs = nowMs;
            _monitorMetricsCache.Clear();
        }

        return _cachedDoubleClickTimeMs;
    }

    private (int MaxDx, int MaxDy, double Scale) GetDoubleClickMetricsForPoint(NativeMethods.POINT pt)
    {
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMonitor != IntPtr.Zero && _monitorMetricsCache.TryGetValue(hMonitor, out var cached))
        {
            return cached;
        }

        uint dpiX = 96;
        if (hMonitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 &&
            dx > 0)
        {
            dpiX = dx;
        }

        double scale = dpiX / 96.0;
        int maxDx = Math.Max(8, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXDOUBLECLK, dpiX));
        int maxDy = Math.Max(8, NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CYDOUBLECLK, dpiX));
        var computed = (maxDx, maxDy, scale);

        if (hMonitor != IntPtr.Zero)
        {
            _monitorMetricsCache[hMonitor] = computed;
        }

        return computed;
    }

    private bool IsDockCurrentlyActive()
    {
        lock (_dockStateLock)
        {
            return _isDockVisible && (_isDockAnimating || (_dockHwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_dockHwnd)));
        }
    }

    private void RaiseDesktopDoubleClick(int x, int y)
    {
        if (_uiDispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _ = _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                DesktopDoubleClicked?.Invoke(this, new DesktopDoubleClickEventArgs(x, y));
            });
        }
        catch
        {
            // Ignore dispatcher shutdown races.
        }
    }

    /// <summary>
    /// Fast-path verification that <paramref name="pt"/> sits over the Windows desktop shell surface
    /// (WorkerW, Progman, SHELLDLL_DefView) rather than an application, taskbar, or File Explorer window.
    /// Outputs the SysListView32 handle and owning explorer PID when available for icon hit-testing.
    /// </summary>
    private bool IsDesktopSurfaceAtPoint(
        NativeMethods.POINT pt,
        out IntPtr listViewHwnd,
        out uint desktopPid)
    {
        listViewHwnd = IntPtr.Zero;
        desktopPid = 0;

        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        // O(1) root fast-path: virtually all non-desktop clicks exit here without walking parents.
        IntPtr rootHwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        IntPtr rootToCheck = rootHwnd != IntPtr.Zero ? rootHwnd : hwnd;
        ReadOnlySpan<char> rootClass = NativeMethods.GetWindowClassNameSpan(rootToCheck);
        if (!IsDesktopElementClass(rootClass))
        {
            return false;
        }

        IntPtr desktopHwnd = NativeMethods.GetDesktopWindow();
        IntPtr current = hwnd;
        IntPtr topLevel = rootToCheck;
        bool sawDesktopClass = false;

        for (int depth = 0; depth < 16 && current != IntPtr.Zero && current != desktopHwnd; depth++)
        {
            ReadOnlySpan<char> className = NativeMethods.GetWindowClassNameSpan(current);
            if (IsIgnoredWindowClass(className))
            {
                return false;
            }

            if (listViewHwnd == IntPtr.Zero && className.Equals("SysListView32", StringComparison.Ordinal))
            {
                listViewHwnd = current;
            }
            else if (IsDesktopElementClass(className))
            {
                sawDesktopClass = true;
            }

            topLevel = current;
            IntPtr parent = NativeMethods.GetAncestor(current, NativeMethods.GA_PARENT);
            if (parent == IntPtr.Zero || parent == desktopHwnd || parent == current)
            {
                break;
            }

            current = parent;
        }

        if (!sawDesktopClass)
        {
            return false;
        }

        // Verify ownership by explorer.exe desktop shell host process.
        IntPtr shellHwnd = NativeMethods.GetShellWindow();
        if (shellHwnd != IntPtr.Zero)
        {
            if (shellHwnd != _cachedShellHwnd || _cachedShellPid == 0)
            {
                NativeMethods.GetWindowThreadProcessId(shellHwnd, out _cachedShellPid);
                _cachedShellHwnd = shellHwnd;
            }

            NativeMethods.GetWindowThreadProcessId(topLevel, out uint targetPid);
            if (_cachedShellPid != 0 && targetPid != _cachedShellPid)
            {
                return false;
            }

            desktopPid = targetPid;
        }
        else if (listViewHwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(listViewHwnd, out desktopPid);
        }

        return true;
    }

    /// <summary>
    /// Queries Explorer's SysListView32 via SendMessageTimeout (20ms bounded) to determine whether
    /// <paramref name="screenPt"/> lands directly on a desktop icon or label.
    /// </summary>
    private static bool IsPointOnDesktopIcon(
        NativeMethods.POINT screenPt,
        IntPtr listViewHwnd,
        uint explorerPid)
    {
        if (listViewHwnd == IntPtr.Zero || explorerPid == 0)
        {
            return false;
        }

        NativeMethods.POINT clientPt = screenPt;
        if (!NativeMethods.ScreenToClient(listViewHwnd, ref clientPt))
        {
            return false;
        }

        IntPtr hProcess = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_VM_OPERATION | NativeMethods.PROCESS_VM_READ | NativeMethods.PROCESS_VM_WRITE,
            false,
            explorerPid);
        if (hProcess == IntPtr.Zero)
        {
            return false;
        }

        int structSize = Marshal.SizeOf<NativeMethods.LVHITTESTINFO>();
        IntPtr localMem = IntPtr.Zero;
        IntPtr remoteMem = IntPtr.Zero;
        try
        {
            localMem = Marshal.AllocHGlobal(structSize);
            remoteMem = NativeMethods.VirtualAllocEx(
                hProcess,
                IntPtr.Zero,
                (UIntPtr)structSize,
                NativeMethods.MEM_COMMIT | NativeMethods.MEM_RESERVE,
                NativeMethods.PAGE_READWRITE);
            if (remoteMem == IntPtr.Zero)
            {
                return false;
            }

            var info = new NativeMethods.LVHITTESTINFO
            {
                pt = clientPt,
                flags = 0,
                iItem = -1,
                iSubItem = 0,
                iGroup = 0
            };
            Marshal.StructureToPtr(info, localMem, false);

            if (!NativeMethods.WriteProcessMemory(hProcess, remoteMem, localMem, (UIntPtr)structSize, out _))
            {
                return false;
            }

            IntPtr sent = NativeMethods.SendMessageTimeoutW(
                listViewHwnd,
                NativeMethods.LVM_HITTEST,
                IntPtr.Zero,
                remoteMem,
                NativeMethods.SMTO_ABORTIFHUNG | NativeMethods.SMTO_BLOCK,
                CrossProcessHitTestTimeoutMs,
                out IntPtr hitResult);

            if (sent == IntPtr.Zero || hitResult.ToInt64() < 0)
            {
                return false;
            }

            if (!NativeMethods.ReadProcessMemory(hProcess, remoteMem, localMem, (UIntPtr)structSize, out _))
            {
                return false;
            }

            info = Marshal.PtrToStructure<NativeMethods.LVHITTESTINFO>(localMem);
            return info.iItem >= 0 && (info.flags & NativeMethods.LVHT_ONITEM) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (remoteMem != IntPtr.Zero)
            {
                NativeMethods.VirtualFreeEx(hProcess, remoteMem, UIntPtr.Zero, NativeMethods.MEM_RELEASE);
            }
            if (localMem != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(localMem);
            }
            NativeMethods.CloseHandle(hProcess);
        }
    }

    private static bool IsDesktopElementClass(ReadOnlySpan<char> className)
    {
        return className.Equals("WorkerW", StringComparison.Ordinal) ||
               className.Equals("Progman", StringComparison.Ordinal) ||
               className.Equals("SHELLDLL_DefView", StringComparison.Ordinal);
    }

    private static bool IsIgnoredWindowClass(ReadOnlySpan<char> className)
    {
        return className.Equals("Shell_TrayWnd", StringComparison.Ordinal) ||
               className.Equals("Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
               className.Equals("CabinetWClass", StringComparison.Ordinal) ||
               className.Equals("ExploreWClass", StringComparison.Ordinal) ||
               className.Equals("#32770", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        Thread? threadToJoin;
        uint threadId;

        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            threadToJoin = _hookThread;
            threadId = Volatile.Read(ref _hookThreadId);
        }

        if (threadId != 0)
        {
            NativeMethods.PostThreadMessageW(threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        bool joined = true;
        if (threadToJoin is { IsAlive: true })
        {
            joined = threadToJoin.Join(TimeSpan.FromMilliseconds(1500));
        }

        // Fallback unhook in the rare case the message loop did not exit within 1.5s.
        IntPtr remainingHook = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
        if (remainingHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(remainingHook);
        }

        if (joined)
        {
            _startedEvent.Dispose();
        }
    }
}
