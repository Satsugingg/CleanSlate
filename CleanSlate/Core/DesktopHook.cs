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
    private readonly Dispatcher _uiDispatcher;
    private readonly ManualResetEventSlim _startedEvent = new(false);
    private readonly object _dockStateLock = new();
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

    // Double-click tracking state (accessed exclusively on the hook thread)
    private bool _hasFirstClick;
    private long _firstClickTimeMs;
    private NativeMethods.POINT _firstClickPt;
    private bool _pendingDoubleClick;
    private NativeMethods.POINT _secondClickDownPt;
    private long _lastTriggerTimeMs;

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
            _isDockVisible = isVisible;
            _isDockAnimating = isAnimating;
            _isDockDragging = isDragging;
            _dockHwnd = dockHwnd;
            _dockBounds = physicalBounds;
        }
    }

    public void Start()
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
        _hookThread.Start();
        _startedEvent.Wait(TimeSpan.FromSeconds(3));
    }

    private void HookThreadMain()
    {
        _hookThreadId = NativeMethods.GetCurrentThreadId();
        // Force creation of the thread message queue before signaling startup so WM_QUIT can never race.
        _ = NativeMethods.PeekMessageW(out _, IntPtr.Zero, 0, 0, 0);
        _hookProcDelegate = HookCallback;

        IntPtr moduleHandle = NativeMethods.GetModuleHandleW(null);
        _hookHandle = NativeMethods.SetWindowsHookExW(
            NativeMethods.WH_MOUSE_LL,
            _hookProcDelegate,
            moduleHandle,
            0);

        _startedEvent.Set();

        if (_hookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"SetWindowsHookExW failed with Win32 error {error} ({new Win32Exception(error).Message}).");
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
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();

            if (msg == NativeMethods.WM_LBUTTONDOWN || msg == NativeMethods.WM_RBUTTONDOWN)
            {
                NativeMethods.POINT pt = ResolveCursorPoint(lParam);
                if (TryDismissIfClickedOutsideDock(pt))
                {
                    _hasFirstClick = false;
                    _pendingDoubleClick = false;
                    return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
                }

                if (msg == NativeMethods.WM_LBUTTONDOWN)
                {
                    HandleLeftButtonDown(pt);
                }
            }
            else if (msg == NativeMethods.WM_LBUTTONUP)
            {
                HandleLeftButtonUp(lParam);
            }
            else if (msg == NativeMethods.WM_LBUTTONDBLCLK)
            {
                // Intentionally handled via WM_LBUTTONDOWN + WM_LBUTTONUP tracking so a double-click never triggers twice.
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

        if (!isVisible || isDragging || dismissAction is null || (!isAnimating && (dockHwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(dockHwnd))))
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

    private void HandleLeftButtonDown(NativeMethods.POINT pt)
    {
        if (IsPointInsideVisibleDock(pt) || !IsDesktopSurfaceAtPoint(pt))
        {
            _hasFirstClick = false;
            _pendingDoubleClick = false;
            return;
        }

        long nowMs = Environment.TickCount64;
        uint maxIntervalMs = NativeMethods.GetDoubleClickTime();
        if (maxIntervalMs == 0)
        {
            maxIntervalMs = 500;
        }

        int maxDx = Math.Max(8, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK));
        int maxDy = Math.Max(8, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK));

        if (_hasFirstClick &&
            (nowMs - _firstClickTimeMs) <= maxIntervalMs &&
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
            _firstClickTimeMs = nowMs;
            _firstClickPt = pt;
            _pendingDoubleClick = false;
        }
    }

    private void HandleLeftButtonUp(IntPtr lParam)
    {
        if (!_pendingDoubleClick)
        {
            return;
        }

        _pendingDoubleClick = false;
        NativeMethods.POINT upPt = ResolveCursorPoint(lParam);

        // Ignore marquee drag selections started immediately after a click.
        if (Math.Abs(upPt.X - _secondClickDownPt.X) > 12 ||
            Math.Abs(upPt.Y - _secondClickDownPt.Y) > 12)
        {
            return;
        }

        if (!IsDockCurrentlyActive() && IsDesktopSurfaceAtPoint(upPt))
        {
            long nowMs = Environment.TickCount64;
            if ((nowMs - _lastTriggerTimeMs) >= 350)
            {
                _lastTriggerTimeMs = nowMs;
                RaiseDesktopDoubleClick(upPt.X, upPt.Y);
            }
        }
    }

    private bool IsDockCurrentlyActive()
    {
        lock (_dockStateLock)
        {
            return _isDockVisible && (_isDockAnimating || (_dockHwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(_dockHwnd)));
        }
    }

    private static NativeMethods.POINT ResolveCursorPoint(IntPtr lParam)
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            return pt;
        }

        return Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam).pt;
    }

    private void RaiseDesktopDoubleClick(int x, int y)
    {
        _uiDispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            DesktopDoubleClicked?.Invoke(this, new DesktopDoubleClickEventArgs(x, y));
        });
    }

    /// <summary>
    /// Verifies that the window under the given screen coordinates belongs exclusively to the
    /// Windows desktop shell surface (WorkerW, Progman, SHELLDLL_DefView) and not an application,
    /// taskbar, or File Explorer window.
    /// </summary>
    private static bool IsDesktopSurfaceAtPoint(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr desktopHwnd = NativeMethods.GetDesktopWindow();
        IntPtr current = hwnd;
        IntPtr topLevel = hwnd;
        bool sawDesktopClass = false;
        string topLevelClass = string.Empty;

        for (int depth = 0; depth < 32 && current != IntPtr.Zero && current != desktopHwnd; depth++)
        {
            string className = NativeMethods.GetWindowClassName(current);
            if (IsIgnoredWindowClass(className))
            {
                return false;
            }

            if (IsDesktopElementClass(className))
            {
                sawDesktopClass = true;
            }

            topLevel = current;
            topLevelClass = className;

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

        if (!string.Equals(topLevelClass, "Progman", StringComparison.Ordinal) &&
            !string.Equals(topLevelClass, "WorkerW", StringComparison.Ordinal) &&
            !string.Equals(topLevelClass, "SHELLDLL_DefView", StringComparison.Ordinal))
        {
            return false;
        }

        // Verify ownership by the shell host process (explorer.exe desktop shell) when available.
        IntPtr shellHwnd = NativeMethods.GetShellWindow();
        if (shellHwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(shellHwnd, out uint shellPid);
            NativeMethods.GetWindowThreadProcessId(topLevel, out uint targetPid);
            if (shellPid != 0 && targetPid != shellPid)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDesktopElementClass(string className)
    {
        return string.Equals(className, "WorkerW", StringComparison.Ordinal) ||
               string.Equals(className, "Progman", StringComparison.Ordinal) ||
               string.Equals(className, "SHELLDLL_DefView", StringComparison.Ordinal);
    }

    private static bool IsIgnoredWindowClass(string className)
    {
        return string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) ||
               string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal) ||
               string.Equals(className, "CabinetWClass", StringComparison.Ordinal) ||
               string.Equals(className, "ExploreWClass", StringComparison.Ordinal) ||
               string.Equals(className, "#32770", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessageW(_hookThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        if (_hookThread is { IsAlive: true })
        {
            _hookThread.Join(TimeSpan.FromMilliseconds(1500));
        }

        _startedEvent.Dispose();
    }
}
