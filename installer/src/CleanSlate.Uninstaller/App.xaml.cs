using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;

namespace CleanSlate.Uninstaller;

public partial class App : Application
{
    public static string TargetDirectory { get; set; } = string.Empty;
    public static string TempDirectoryPath { get; set; } = string.Empty;

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
        MessageBox.Show(message, "CleanSlate Uninstaller Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        bool isFromTemp = e.Args.Any(a => string.Equals(a, "--from-temp", StringComparison.OrdinalIgnoreCase));

        string? dirArg = null;
        for (int i = 0; i < e.Args.Length - 1; i++)
        {
            if (string.Equals(e.Args[i], "--dir", StringComparison.OrdinalIgnoreCase))
            {
                dirArg = e.Args[i + 1];
                break;
            }
        }

        string resolvedTarget = UninstallerEngine.ResolveTargetDirectory(dirArg);
        TargetDirectory = resolvedTarget;

        if (!isFromTemp)
        {
            // Spawn copy in %TEMP% so original folder can be completely deleted without file locks
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "CleanSlateUninstall_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                string baseDir = AppContext.BaseDirectory;
                foreach (string file in Directory.GetFiles(baseDir, "*.*"))
                {
                    string name = Path.GetFileName(file);
                    // Copy Uninstall binaries and necessary runtime files
                    if (name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(file, Path.Combine(tempDir, name), overwrite: true);
                    }
                }

                string tempExe = Path.Combine(tempDir, "Uninstall.exe");
                if (File.Exists(tempExe))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = tempExe,
                        Arguments = $"--from-temp --dir \"{resolvedTarget}\" " + string.Join(" ", e.Args),
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to spawn from temp: {ex.Message}");
                // Fall back to in-place execution if temp copy fails
            }
        }
        else
        {
            TempDirectoryPath = AppContext.BaseDirectory;
        }

        bool isSilent = e.Args.Any(a => string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            try
            {
                UninstallerEngine.PerformUninstall(TargetDirectory);
            }
            finally
            {
                CleanupTempFolderOnExit(TempDirectoryPath);
                Shutdown(0);
            }
            return;
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

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        CleanupTempFolderOnExit(TempDirectoryPath);
    }

    private static void CleanupTempFolderOnExit(string tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath) || !Directory.Exists(tempPath))
        {
            return;
        }

        try
        {
            string systemTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (tempPath.StartsWith(systemTemp, StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout /t 2 /nobreak >nul & rd /s /q \"{tempPath}\"",
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
        }
        catch { }
    }
}
