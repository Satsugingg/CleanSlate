using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace CleanSlate.Installer;

public partial class MainWindow : Window
{
    private readonly bool _isUninstall;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();
        _isUninstall = App.IsUninstallMode;

        string targetDir = InstallerEngine.GetTargetAppDirectory();
        InstallLocationTextBox.Text = targetDir;

        if (_isUninstall)
        {
            Title = "CleanSlate Uninstaller";
            HeroTitleText.Text = "Uninstall CleanSlate";
            HeroSubtitleText.Text = "Remove CleanSlate desktop dock from this PC.";
            InstallLocationPanel.Visibility = Visibility.Collapsed;
            InstallOptionsPanel.Visibility = Visibility.Collapsed;
            UninstallOptionsPanel.Visibility = Visibility.Visible;
            ActionButton.Content = "Uninstall";
            ActionButton.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            StatusText.Text = "Ready to uninstall CleanSlate.";
        }
    }

    private void OnBrowseLocationClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string current = InstallLocationTextBox.Text.Trim();
            string initial = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select CleanSlate Installation Folder",
                InitialDirectory = initial,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                InstallLocationTextBox.Text = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open folder picker: {ex.Message}", "CleanSlate Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        Close();
    }

    private async void OnActionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _isBusy = true;

        ActionButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        OptionsCard.IsEnabled = false;
        InstallProgressBar.Visibility = Visibility.Visible;
        InstallProgressBar.Value = 0;

        try
        {
            if (_isUninstall)
            {
                await Task.Run(() =>
                {
                    InstallerEngine.PerformUninstall((msg, progress) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = msg;
                            InstallProgressBar.Value = progress;
                        });
                    });
                });

                StatusText.Text = "CleanSlate has been uninstalled successfully.";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
                ActionButton.Content = "Finished";
                ActionButton.IsEnabled = true;
                ActionButton.Click -= OnActionButtonClick;
                ActionButton.Click += (_, _) => Close();
            }
            else
            {
                string rawTargetDir = InstallLocationTextBox.Text.Trim();
                string targetDir = string.IsNullOrWhiteSpace(rawTargetDir)
                    ? InstallerEngine.GetTargetAppDirectory()
                    : Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawTargetDir));

                bool autoLaunch = LaunchCheckBox.IsChecked == true;
                bool startup = StartupCheckBox.IsChecked == true;
                bool shortcuts = ShortcutsCheckBox.IsChecked == true;

                await Task.Run(() =>
                {
                    InstallerEngine.PerformInstall(targetDir, autoLaunch, startup, shortcuts, (msg, progress) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            StatusText.Text = msg;
                            InstallProgressBar.Value = progress;
                        });
                    });
                });

                StatusText.Text = "CleanSlate has been installed successfully!";
                StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
                ActionButton.Content = "Finished";
                ActionButton.IsEnabled = true;
                ActionButton.Click -= OnActionButtonClick;
                ActionButton.Click += (_, _) => Close();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed: {ex.Message}";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            ActionButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            OptionsCard.IsEnabled = true;
        }
        finally
        {
            _isBusy = false;
        }
    }
}
