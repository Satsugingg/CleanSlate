using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace CleanSlate.Uninstaller;

public partial class MainWindow : Window
{
    private readonly string _targetDir;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        _targetDir = App.TargetDirectory;
        if (string.IsNullOrWhiteSpace(_targetDir))
        {
            _targetDir = UninstallerEngine.ResolveTargetDirectory();
        }

        TargetLocationText.Text = _targetDir;
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
        UninstallProgressBar.Visibility = Visibility.Visible;
        UninstallProgressBar.Value = 0;

        try
        {
            await Task.Run(() =>
            {
                UninstallerEngine.PerformUninstall(_targetDir, (msg, progress) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        StatusText.Text = msg;
                        UninstallProgressBar.Value = progress;
                    });
                });
            });

            StatusText.Text = "CleanSlate has been uninstalled successfully.";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
            ActionButton.Content = "Finished";
            ActionButton.Background = new SolidColorBrush(Color.FromRgb(0x38, 0x8E, 0x3C));
            ActionButton.IsEnabled = true;
            ActionButton.Click -= OnActionButtonClick;
            ActionButton.Click += (_, _) => Close();
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
