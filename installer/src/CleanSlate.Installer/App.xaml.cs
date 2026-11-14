using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace CleanSlate.Installer;

public partial class App : Application
{
    public static bool IsUninstallMode { get; set; }

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, ev) =>
        {
            LogAndShowFatalError("AppDomain Unhandled Exception", ev.ExceptionObject as Exception);
        };

        DispatcherUnhandledException += (s, ev) =>
        {
            LogAndShowFatalError("Dispatcher Unhandled Exception", ev.Exception);
            ev.Handled = true;
        };
    }

    private static void LogAndShowFatalError(string prefix, Exception? ex)
    {
        string message = $"{prefix}: {ex?.ToString() ?? "Unknown error"}";
        try
        {
            string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanSlate");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "installer_crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch { }

        MessageBox.Show(message, "CleanSlate Setup Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool isSilent = e.Args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase));

        bool isUninstall = e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(a, "-u", StringComparison.OrdinalIgnoreCase));

        string? dirArg = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--dir", StringComparison.OrdinalIgnoreCase))
            {
                dirArg = e.Args[i + 1];
                break;
            }
        }
        string targetDir = string.IsNullOrWhiteSpace(dirArg)
            ? InstallerEngine.GetTargetAppDirectory()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(dirArg));

        if (isSilent)
        {
            if (isUninstall)
            {
                InstallerEngine.PerformUninstall();
            }
            else
            {
                InstallerEngine.PerformInstall(targetDir, autoLaunch: true, registerStartup: true, createShortcuts: true);
            }
            Shutdown(0);
            return;
        }

        if (isUninstall)
        {
            IsUninstallMode = true;
        }

        base.OnStartup(e);

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            LogAndShowFatalError("Window Creation Error", ex);
            Shutdown(1);
        }
    }
}
