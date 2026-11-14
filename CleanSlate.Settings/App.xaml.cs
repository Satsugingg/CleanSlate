using System.Windows;
using CleanSlate.Settings.Services;

namespace CleanSlate.Settings;

public partial class App : Application
{
    private const string MutexName = @"Local\CleanSlate_Settings_Mutex";
    private Mutex? _mutex;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            uint msg = SettingsStore.NativeMethods.RegisterWindowMessageW(global::CleanSlate.Settings.MainWindow.BringToFrontMessageName);
            if (msg != 0)
            {
                SettingsStore.NativeMethods.PostMessageW(new IntPtr(0xFFFF), msg, IntPtr.Zero, IntPtr.Zero);
            }
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_mutex is not null)
        {
            if (_ownsMutex)
            {
                try { _mutex.ReleaseMutex(); } catch { }
            }
            _mutex.Dispose();
            _mutex = null;
        }

        base.OnExit(e);
    }
}
