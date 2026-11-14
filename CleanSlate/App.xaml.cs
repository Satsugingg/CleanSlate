using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CleanSlate.Core;
using CleanSlate.Models;
using CleanSlate.Services;

namespace CleanSlate;

public partial class App : Application
{
    private const string GlobalMutexName = @"Global\CleanSlate_App_Mutex";
    private const string LocalMutexName = @"Local\CleanSlate_App_Mutex";

    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private ConfigService? _configService;
    private IconExtractor? _iconExtractor;
    private MainWindow? _mainWindow;
    private DesktopHook? _desktopHook;
    private DesktopWatcher? _desktopWatcher;
    private TrayIconManager? _trayIconManager;
    private bool _isShuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireSingleInstanceMutex())
        {
            NotifyExistingInstanceAndExit();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        try
        {
            _configService = new ConfigService();
            _configService.AttachUiDispatcher(Dispatcher);
            await _configService.LoadAsync();

            // Desktop auto-organization is an active invariant of CleanSlate on every launch.
            _configService.Config.Settings.AutoIngestDesktop = true;

            _iconExtractor = new IconExtractor();
            _mainWindow = new MainWindow(_configService, _iconExtractor);

            _desktopWatcher = new DesktopWatcher(_configService, _iconExtractor, Dispatcher);
            _desktopWatcher.ShortcutsIngested += (_, _) =>
            {
                _mainWindow.RefreshCurrentView();
                if (_mainWindow.MiniDesktopMode)
                {
                    _mainWindow.SyncMiniDesktopGridLayout(persistIfAssigned: true, forceReflow: false);
                }
            };

            if (_configService.Config.Settings.AutoIngestDesktop)
            {
                // Start ingester before hiding Recycle Bin so DesktopPositionHelper captures
                // Recycle Bin & desktop file coordinates prior to Shell refresh.
                _desktopWatcher.Start();
                DesktopIconHelper.HideDesktopRecycleBin();
            }

            // Warm icon cache for any remaining configured items after startup.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _mainWindow.PreloadIcons());

            _desktopHook = new DesktopHook(Dispatcher);
            _desktopHook.RegisterDismissHandler(() => _mainWindow.HideDock());
            _mainWindow.DockBoundsChanged += (_, args) =>
            {
                _desktopHook.UpdateDockBounds(args.IsVisible, args.Hwnd, args.PhysicalBounds, args.IsAnimating, args.IsDragging);
            };
            _desktopHook.DesktopDoubleClicked += (_, args) => _mainWindow.ShowAtCursor(args.X, args.Y);
            _desktopHook.Start();

            _trayIconManager = new TrayIconManager(
                _configService,
                () => _mainWindow.ShowAtCurrentCursor(),
                SyncFromExternalSettingsChange,
                ShutdownApplication,
                () => _mainWindow.ToggleAtCurrentCursor(),
                () => _mainWindow.OpenRecycleBinFolder());
            _trayIconManager.Initialize();

            if (_configService.Config.Settings.AutoLaunchOnStartup)
            {
                string? currentExe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(currentExe))
                {
                    _ = Task.Run(() => WindowsStartupHelper.ApplyStartupRegistration(currentExe, enabled: true));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CleanSlate startup failed: {ex}");
            MessageBox.Show(
                $"CleanSlate failed to initialize:\n{ex.Message}",
                "CleanSlate",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShutdownApplication();
        }
    }

    private void SyncFromExternalSettingsChange()
    {
        if (_configService is null || _desktopWatcher is null || _mainWindow is null)
        {
            return;
        }

        _configService.ReloadFromDisk(force: true);
        if (_configService.Config.Settings.AutoIngestDesktop)
        {
            _desktopWatcher.Start();
            if (_configService.Config.RecycleBinPosition is null &&
                DesktopPositionHelper.CaptureDesktopPositions().TryGetValue(
                    DesktopPositionHelper.RecycleBinParsingPath,
                    out DesktopIconPoint rbPt))
            {
                _configService.Config.RecycleBinPosition = new SavedIconPosition { X = rbPt.X, Y = rbPt.Y };
                _ = _configService.SaveAsync();
            }

            DesktopIconHelper.HideDesktopRecycleBin();
        }
        else
        {
            _desktopWatcher.Stop();
        }

        _trayIconManager?.ApplySummonHotkey(_configService.Config.Settings.SummonShortcut);
        _mainWindow.ApplyVisualSettings();
        if (_mainWindow.MiniDesktopMode)
        {
            _mainWindow.SyncMiniDesktopGridLayout(persistIfAssigned: true, forceReflow: true);
        }
        _mainWindow.RefreshCurrentView();
        _mainWindow.RepositionAndResizeIfVisible();
    }

    private bool AcquireSingleInstanceMutex()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, GlobalMutexName, out bool createdNew);
            _ownsMutex = createdNew;
            return createdNew;
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            try
            {
                _instanceMutex = new Mutex(initiallyOwned: true, LocalMutexName, out bool createdNew);
                _ownsMutex = createdNew;
                return createdNew;
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
                return true;
            }
            catch
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AcquireSingleInstanceMutex exception: {ex.Message}");
            return true;
        }
    }

    private void NotifyExistingInstanceAndExit()
    {
        uint msg = NativeMethods.RegisterWindowMessageW(TrayIconManager.ShowDockBroadcastMessageName);
        if (msg != 0)
        {
            NativeMethods.PostMessageW(NativeMethods.HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
        }

        Shutdown(0);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"Unhandled UI exception: {e.Exception}");
        e.Handled = true;
    }

    public void ShutdownApplication()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        CleanupResources();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CleanupResources();
        base.OnExit(e);
    }

    private void CleanupResources()
    {
        _configService?.FlushPendingSave();

        _desktopHook?.Dispose();
        _desktopHook = null;

        _desktopWatcher?.Dispose();
        _desktopWatcher = null;

        _trayIconManager?.Dispose();
        _trayIconManager = null;

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
            _mainWindow = null;
        }

        if (_instanceMutex is not null)
        {
            if (_ownsMutex)
            {
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch
                {
                    // Ignore if already released on another thread.
                }
                _ownsMutex = false;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
        }
    }
}
