using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CleanSlate.Core;
using CleanSlate.Settings.Services;

namespace CleanSlate.Settings;

public partial class MainWindow : Window
{
    public const string BringToFrontMessageName = "CleanSlate_Settings_Activate_Msg";

    private enum ShortcutCaptureTarget
    {
        None,
        Search,
        Summon
    }

    private enum CustomEditSource
    {
        None,
        ColsRows,
        Dimensions
    }

    private readonly SettingsStore _store = new();
    private readonly UpdateCheckerService _updater = new();
    private readonly DispatcherTimer _iconSizeSaveTimer;
    private readonly DispatcherTimer _customSizeSaveTimer;
    private DockSettingsModel _model = new();
    private UpdateCheckResult? _pendingUpdate;
    private bool _isInitializing = true;
    private bool _isUpdatingCustomSizeUi;
    private ShortcutCaptureTarget _captureTarget = ShortcutCaptureTarget.None;
    private CustomEditSource _lastCustomEditSource = CustomEditSource.None;
    private uint _activateMsg;

    public MainWindow()
    {
        InitializeComponent();
        UpdateStatusText.Text = $"Installed version: {UpdateCheckerService.CurrentVersionTag}";

        Rect work = SystemParameters.WorkArea;
        MaxHeight = Math.Min(700.0, Math.Max(340.0, work.Height - 24.0));
        MaxWidth = Math.Min(480.0, Math.Max(320.0, work.Width - 24.0));

        _iconSizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(90)
        };
        _iconSizeSaveTimer.Tick += (_, _) => FlushPendingIconSizeSave();

        _customSizeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _customSizeSaveTimer.Tick += (_, _) =>
        {
            _customSizeSaveTimer.Stop();
            ApplyCustomSizeCore();
        };

        Closing += (_, _) =>
        {
            FlushPendingIconSizeSave();
            if (_customSizeSaveTimer.IsEnabled)
            {
                _customSizeSaveTimer.Stop();
                ApplyCustomSizeCore();
            }
        };

        LoadCurrentSettings();

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            int dark = 1;
            _ = SettingsStore.NativeMethods.DwmSetWindowAttribute(
                hwnd,
                SettingsStore.NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE,
                ref dark,
                sizeof(int));

            _activateMsg = SettingsStore.NativeMethods.RegisterWindowMessageW(BringToFrontMessageName);
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
    }

    private void LoadCurrentSettings()
    {
        _isInitializing = true;
        try
        {
            _model = _store.Load();
            ShowSettingsButtonToggle.IsChecked = _model.ShowSettingsButton;
            MiniDesktopToggle.IsChecked = _model.MiniDesktopMode;
            ShowLabelsToggle.IsChecked = _model.ShowLabels;
            ShowCornerRecycleBinToggle.IsChecked = _model.ShowCornerRecycleBin;
            IconSizeSlider.Value = Math.Clamp(_model.IconSize, 24, 96);
            IconSizeValueText.Text = $"{_model.IconSize} px";
            EnableSearchToggle.IsChecked = _model.EnableSearch;
            LaunchOnStartupToggle.IsChecked = _model.AutoLaunchOnStartup;
            RefreshDockSizeUi();
            RefreshShortcutButtonsUi();
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void RefreshDockSizeUi()
    {
        DockSizeCard.IsEnabled = true;
        DockSizeCard.Opacity = 1.0;

        double workW = SystemParameters.WorkArea.Width;
        double workH = SystemParameters.WorkArea.Height;
        double defaultMiniW = Math.Round(workW * 0.5);
        double defaultMiniH = Math.Round(workH * 0.5);
        double largeMiniW = Math.Clamp(Math.Round(workW * 0.65), 800.0, workW - 48.0);
        double largeMiniH = Math.Clamp(Math.Round(workH * 0.65), 520.0, workH - 48.0);

        if (_model.MiniDesktopMode)
        {
            double curW = _model.MiniDesktopWidth >= 300 ? _model.MiniDesktopWidth : defaultMiniW;
            double curH = _model.MiniDesktopHeight >= 220 ? _model.MiniDesktopHeight : defaultMiniH;

            HighlightDockPreset(DockSizeSmallButton, 680, 440, curW, curH);
            bool isDefault = (_model.MiniDesktopWidth < 300 && _model.MiniDesktopHeight < 220) ||
                             (Math.Abs(curW - defaultMiniW) < 10 && Math.Abs(curH - defaultMiniH) < 10);
            SetPresetSelected(DockSizeDefaultButton, isDefault);
            HighlightDockPreset(DockSizeLargeButton, largeMiniW, largeMiniH, curW, curH);
        }
        else
        {
            double curW = _model.DockWidth;
            double curH = _model.DockHeight;

            HighlightDockPreset(DockSizeSmallButton, 440, 320, curW, curH);
            HighlightDockPreset(DockSizeDefaultButton, 520, 360, curW, curH);
            HighlightDockPreset(DockSizeLargeButton, 680, 440, curW, curH);
        }

        UpdateCustomSizeTextBoxes();
    }

    private void UpdateCustomSizeTextBoxes()
    {
        if (_isUpdatingCustomSizeUi || CustomColsTextBox is null || CustomRowsTextBox is null ||
            CustomWidthTextBox is null || CustomHeightTextBox is null)
        {
            return;
        }

        double activeW;
        double activeH;
        if (_model.MiniDesktopMode)
        {
            activeW = _model.MiniDesktopWidth >= 300
                ? _model.MiniDesktopWidth
                : Math.Round(SystemParameters.WorkArea.Width * 0.5);
            activeH = _model.MiniDesktopHeight >= 220
                ? _model.MiniDesktopHeight
                : Math.Round(SystemParameters.WorkArea.Height * 0.5);
        }
        else
        {
            activeW = _model.DockWidth;
            activeH = _model.DockHeight;
        }

        _isUpdatingCustomSizeUi = true;
        try
        {
            (int cols, int rows) = CalculateGridSize(
                activeW, activeH, _model.IconSize, _model.ShowLabels, _model.MiniDesktopMode);

            CustomColsTextBox.Text = cols.ToString();
            CustomRowsTextBox.Text = rows.ToString();
            CustomWidthTextBox.Text = Math.Round(activeW).ToString(CultureInfo.InvariantCulture);
            CustomHeightTextBox.Text = Math.Round(activeH).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isUpdatingCustomSizeUi = false;
        }
    }

    private static (int Cols, int Rows) CalculateGridSize(
        double width, double height, int iconSize, bool showLabels, bool isMiniDesktop)
    {
        double tileW = Math.Max(78.0, iconSize + 40.0);
        double tileH = showLabels ? iconSize + 56.0 : iconSize + 24.0;
        double contentW = Math.Max(tileW, width - 28.0);
        double contentH = Math.Max(tileH, height - 28.0);

        double gapX = isMiniDesktop ? 8.0 : 6.0;
        double gapY = isMiniDesktop ? 6.0 : 6.0;
        double stepX = tileW + gapX;
        double stepY = tileH + gapY;

        int cols = Math.Max(1, (int)Math.Floor((contentW + gapX + 0.5) / stepX));
        int rows = Math.Max(1, (int)Math.Floor((contentH + gapY + 0.5) / stepY));

        return (cols, rows);
    }

    private static (double Width, double Height) CalculateDimensions(
        int cols, int rows, int iconSize, bool showLabels, bool isMiniDesktop)
    {
        double tileW = Math.Max(78.0, iconSize + 40.0);
        double tileH = showLabels ? iconSize + 56.0 : iconSize + 24.0;

        double gapX = isMiniDesktop ? 8.0 : 6.0;
        double gapY = isMiniDesktop ? 6.0 : 6.0;

        double contentW = (cols * tileW) + ((cols - 1) * gapX);
        double contentH = (rows * tileH) + ((rows - 1) * gapY);

        return (Math.Max(300.0, contentW + 28.0), Math.Max(220.0, contentH + 28.0));
    }

    private static void SetPresetSelected(Button btn, bool selected)
    {
        if (selected)
        {
            btn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
            btn.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
        }
        else
        {
            btn.ClearValue(BorderBrushProperty);
            btn.ClearValue(ForegroundProperty);
        }
    }

    private static void HighlightDockPreset(Button btn, double presetW, double presetH, double currentW, double currentH)
    {
        bool selected = Math.Abs(currentW - presetW) < 8 && Math.Abs(currentH - presetH) < 8;
        SetPresetSelected(btn, selected);
    }

    private void RefreshShortcutButtonsUi()
    {
        string searchNormalized = KeybindHelper.Normalize(_model.SearchShortcut);
        _model.SearchShortcut = searchNormalized;
        UpdateShortcutButtonVisual(SearchShortcutButton, _captureTarget == ShortcutCaptureTarget.Search, searchNormalized);
        ResetSearchShortcutButton.Visibility =
            string.Equals(searchNormalized, KeybindHelper.DefaultSearchShortcut, StringComparison.Ordinal)
                ? Visibility.Collapsed
                : Visibility.Visible;

        SearchKeybindCard.IsEnabled = _model.EnableSearch;
        SearchKeybindCard.Opacity = _model.EnableSearch ? 1.0 : 0.45;

        string summonNormalized = KeybindHelper.NormalizeSummonShortcut(_model.SummonShortcut);
        _model.SummonShortcut = summonNormalized;
        UpdateShortcutButtonVisual(
            SummonShortcutButton,
            _captureTarget == ShortcutCaptureTarget.Summon,
            string.IsNullOrEmpty(summonNormalized) ? "Off" : summonNormalized);
        ClearSummonShortcutButton.Visibility =
            string.IsNullOrEmpty(summonNormalized) ? Visibility.Collapsed : Visibility.Visible;
        ResetSummonShortcutButton.Visibility =
            string.Equals(summonNormalized, KeybindHelper.DefaultSummonShortcut, StringComparison.Ordinal)
                ? Visibility.Collapsed
                : Visibility.Visible;
    }

    private static void UpdateShortcutButtonVisual(Button button, bool isCapturing, string displayText)
    {
        if (isCapturing)
        {
            button.Content = "Press key...";
            button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
            button.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
        }
        else
        {
            button.Content = displayText;
            button.ClearValue(BorderBrushProperty);
            button.ClearValue(ForegroundProperty);
        }
    }

    private void OnSearchShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        _captureTarget = _captureTarget == ShortcutCaptureTarget.Search
            ? ShortcutCaptureTarget.None
            : ShortcutCaptureTarget.Search;
        RefreshShortcutButtonsUi();
    }

    private void OnResetSearchShortcutClick(object sender, RoutedEventArgs e)
    {
        _captureTarget = ShortcutCaptureTarget.None;
        _model.SearchShortcut = KeybindHelper.DefaultSearchShortcut;
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
        RefreshShortcutButtonsUi();
    }

    private void OnSummonShortcutButtonClick(object sender, RoutedEventArgs e)
    {
        _captureTarget = _captureTarget == ShortcutCaptureTarget.Summon
            ? ShortcutCaptureTarget.None
            : ShortcutCaptureTarget.Summon;
        RefreshShortcutButtonsUi();
    }

    private void OnClearSummonShortcutClick(object sender, RoutedEventArgs e)
    {
        _captureTarget = ShortcutCaptureTarget.None;
        _model.SummonShortcut = string.Empty;
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
        RefreshShortcutButtonsUi();
    }

    private void OnResetSummonShortcutClick(object sender, RoutedEventArgs e)
    {
        _captureTarget = ShortcutCaptureTarget.None;
        _model.SummonShortcut = KeybindHelper.DefaultSummonShortcut;
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
        RefreshShortcutButtonsUi();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_captureTarget != ShortcutCaptureTarget.None)
        {
            _captureTarget = ShortcutCaptureTarget.None;
            RefreshShortcutButtonsUi();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_captureTarget == ShortcutCaptureTarget.None)
        {
            return;
        }

        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        Button activeButton = _captureTarget == ShortcutCaptureTarget.Summon
            ? SummonShortcutButton
            : SearchShortcutButton;

        if (actualKey == Key.Escape)
        {
            _captureTarget = ShortcutCaptureTarget.None;
            RefreshShortcutButtonsUi();
            e.Handled = true;
            return;
        }

        if (KeybindHelper.IsModifierKey(actualKey))
        {
            string liveMods = FormatLiveModifierHint(e.KeyboardDevice.Modifiers);
            if (!string.IsNullOrEmpty(liveMods))
            {
                activeButton.Content = liveMods;
            }
            e.Handled = true;
            return;
        }

        if (KeybindHelper.IsDisallowedCaptureKey(actualKey))
        {
            e.Handled = true;
            return;
        }

        string formatted = KeybindHelper.Format(actualKey, e.KeyboardDevice.Modifiers);
        if (_captureTarget == ShortcutCaptureTarget.Summon)
        {
            if (!KeybindHelper.TryParseGlobalHotKey(formatted, out _, out _))
            {
                activeButton.Content = "Add Ctrl/Alt/Shift...";
                e.Handled = true;
                return;
            }
            _model.SummonShortcut = formatted;
        }
        else
        {
            _model.SearchShortcut = formatted;
        }

        _captureTarget = ShortcutCaptureTarget.None;
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
        RefreshShortcutButtonsUi();
        e.Handled = true;
    }

    private static string FormatLiveModifierHint(ModifierKeys mods)
    {
        var list = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) list.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) list.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) list.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) list.Add("Win");
        return list.Count > 0 ? string.Join("+", list) + "+..." : "Press key...";
    }

    private void OnIconSizeSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IconSizeValueText is null)
        {
            return;
        }

        int size = (int)Math.Round(e.NewValue);
        IconSizeValueText.Text = $"{size} px";

        if (_isInitializing)
        {
            return;
        }

        _model.IconSize = Math.Clamp(size, 24, 96);
        UpdateCustomSizeTextBoxes();
        _iconSizeSaveTimer.Stop();
        _iconSizeSaveTimer.Start();
    }

    private void OnCustomColsRowsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingCustomSizeUi)
        {
            return;
        }

        _lastCustomEditSource = CustomEditSource.ColsRows;

        if (int.TryParse(CustomColsTextBox.Text.Trim(), out int cols) && cols >= 1 &&
            int.TryParse(CustomRowsTextBox.Text.Trim(), out int rows) && rows >= 1)
        {
            _isUpdatingCustomSizeUi = true;
            try
            {
                (double w, double h) = CalculateDimensions(
                    cols, rows, _model.IconSize, _model.ShowLabels, _model.MiniDesktopMode);
                CustomWidthTextBox.Text = Math.Round(w).ToString(CultureInfo.InvariantCulture);
                CustomHeightTextBox.Text = Math.Round(h).ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _isUpdatingCustomSizeUi = false;
            }

            _customSizeSaveTimer?.Stop();
            _customSizeSaveTimer?.Start();
        }
    }

    private void OnCustomWidthHeightTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isInitializing || _isUpdatingCustomSizeUi)
        {
            return;
        }

        _lastCustomEditSource = CustomEditSource.Dimensions;

        if (double.TryParse(CustomWidthTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w >= 200 &&
            double.TryParse(CustomHeightTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h >= 150)
        {
            _isUpdatingCustomSizeUi = true;
            try
            {
                (int cols, int rows) = CalculateGridSize(
                    w, h, _model.IconSize, _model.ShowLabels, _model.MiniDesktopMode);
                CustomColsTextBox.Text = cols.ToString();
                CustomRowsTextBox.Text = rows.ToString();
            }
            finally
            {
                _isUpdatingCustomSizeUi = false;
            }

            _customSizeSaveTimer?.Stop();
            _customSizeSaveTimer?.Start();
        }
    }

    private void OnCustomInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _customSizeSaveTimer?.Stop();
            ApplyCustomSizeCore();
            e.Handled = true;
        }
    }

    private void OnCustomInputLostFocus(object sender, RoutedEventArgs e)
    {
        _customSizeSaveTimer?.Stop();
        ApplyCustomSizeCore();
    }

    private void OnApplyCustomSizeClick(object sender, RoutedEventArgs e)
    {
        _customSizeSaveTimer?.Stop();
        ApplyCustomSizeCore();
    }

    private void ApplyCustomSizeCore()
    {
        if (_isInitializing)
        {
            return;
        }

        double activeW = _model.MiniDesktopMode
            ? (_model.MiniDesktopWidth >= 300 ? _model.MiniDesktopWidth : Math.Round(SystemParameters.WorkArea.Width * 0.5))
            : _model.DockWidth;
        double activeH = _model.MiniDesktopMode
            ? (_model.MiniDesktopHeight >= 220 ? _model.MiniDesktopHeight : Math.Round(SystemParameters.WorkArea.Height * 0.5))
            : _model.DockHeight;

        double targetW = activeW;
        double targetH = activeH;

        int cols = 0;
        int rows = 0;
        bool hasValidColsRows = int.TryParse(CustomColsTextBox.Text.Trim(), out cols) && cols >= 1 &&
                                int.TryParse(CustomRowsTextBox.Text.Trim(), out rows) && rows >= 1;

        double w = 0;
        double h = 0;
        bool hasValidDimensions = double.TryParse(CustomWidthTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w) && w >= 200 &&
                                  double.TryParse(CustomHeightTextBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out h) && h >= 150;

        if (_lastCustomEditSource == CustomEditSource.ColsRows && hasValidColsRows)
        {
            (targetW, targetH) = CalculateDimensions(
                cols, rows, _model.IconSize, _model.ShowLabels, _model.MiniDesktopMode);
        }
        else if (hasValidDimensions)
        {
            targetW = Math.Max(300.0, w);
            targetH = Math.Max(220.0, h);
        }
        else if (hasValidColsRows)
        {
            (targetW, targetH) = CalculateDimensions(
                cols, rows, _model.IconSize, _model.ShowLabels, _model.MiniDesktopMode);
        }
        else
        {
            return;
        }

        if (_model.MiniDesktopMode)
        {
            _model.MiniDesktopWidth = targetW;
            _model.MiniDesktopHeight = targetH;
        }
        else
        {
            _model.DockWidth = targetW;
            _model.DockHeight = targetH;
        }

        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
        RefreshDockSizeUi();
    }

    private void OnDockSizePresetClick(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || sender is not Button btn)
        {
            return;
        }

        double workW = SystemParameters.WorkArea.Width;
        double workH = SystemParameters.WorkArea.Height;

        if (_model.MiniDesktopMode)
        {
            if (ReferenceEquals(btn, DockSizeSmallButton))
            {
                _model.MiniDesktopWidth = 680;
                _model.MiniDesktopHeight = 440;
            }
            else if (ReferenceEquals(btn, DockSizeDefaultButton))
            {
                _model.MiniDesktopWidth = 0;
                _model.MiniDesktopHeight = 0;
            }
            else if (ReferenceEquals(btn, DockSizeLargeButton))
            {
                _model.MiniDesktopWidth = Math.Clamp(Math.Round(workW * 0.65), 800.0, workW - 48.0);
                _model.MiniDesktopHeight = Math.Clamp(Math.Round(workH * 0.65), 520.0, workH - 48.0);
            }
        }
        else
        {
            if (btn.Tag is string tag)
            {
                string[] parts = tag.Split(',');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                {
                    _model.DockWidth = w;
                    _model.DockHeight = h;
                }
            }
        }

        RefreshDockSizeUi();
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
    }

    private void OnSettingToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        _model.ShowBackground = false;
        _model.ShowSettingsButton = ShowSettingsButtonToggle.IsChecked == true;
        _model.MiniDesktopMode = MiniDesktopToggle.IsChecked == true;
        _model.ShowLabels = ShowLabelsToggle.IsChecked == true;
        _model.ShowCornerRecycleBin = ShowCornerRecycleBinToggle.IsChecked == true;
        _model.EnableSearch = EnableSearchToggle.IsChecked == true;
        if (!_model.EnableSearch && _captureTarget == ShortcutCaptureTarget.Search)
        {
            _captureTarget = ShortcutCaptureTarget.None;
        }
        RefreshDockSizeUi();
        RefreshShortcutButtonsUi();
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        bool enabled = LaunchOnStartupToggle.IsChecked == true;
        _model.AutoLaunchOnStartup = enabled;
        _store.SetWindowsStartup(enabled);
        _store.Save(_model);
    }

    private async void OnTransferDesktopToDockClick(object sender, RoutedEventArgs e)
    {
        TransferIconsButton.IsEnabled = false;
        TransferStatusText.Text = "Transferring desktop shortcuts and files into app dock...";
        TransferStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));

        try
        {
            TransferDockResult result = await Task.Run(() => _store.TransferAllDesktopIconsToDock(_model));
            string itemNoun = result.TransferredCount == 1 ? "item" : "items";
            if (result.TransferredCount > 0)
            {
                TransferStatusText.Text = $"Transferred {result.TransferredCount} {itemNoun} back into the app dock.";
                TransferStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
            }
            else
            {
                TransferStatusText.Text = "All desktop shortcuts are already managed in the app dock.";
                TransferStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
            }
        }
        catch (Exception ex)
        {
            TransferStatusText.Text = $"Transfer failed: {ex.Message}";
            TransferStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
        }
        finally
        {
            TransferIconsButton.IsEnabled = true;
        }
    }

    private async void OnResetDesktopIconsClick(object sender, RoutedEventArgs e)
    {
        RestoreIconsButton.IsEnabled = false;
        RestoreStatusText.Text = "Restoring icons and original desktop positions...";
        RestoreStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));

        RestoreDesktopResult result;
        try
        {
            result = await Task.Run(() => _store.RestoreAllDesktopIcons(_model));
        }
        finally
        {
            RestoreIconsButton.IsEnabled = true;
        }

        string itemNoun = result.RestoredCount == 1 ? "item" : "items";
        RestoreStatusText.Text = result.SkippedCount > 0
            ? $"Restored {result.RestoredCount} {itemNoun} & Recycle Bin ({result.SkippedCount} locked skipped)."
            : result.AutoArrangeEnabled
                ? $"Restored {result.RestoredCount} {itemNoun} & Recycle Bin (Windows Auto-Arrange is enabled)."
                : result.PositionsRestoredCount > 0
                    ? $"Restored {result.RestoredCount} {itemNoun} & Recycle Bin to their original positions."
                    : $"Restored {result.RestoredCount} {itemNoun} & Recycle Bin onto your desktop.";
        RestoreStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
    }

    private async void OnCheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;
        UpdateStatusText.Text = $"Checking GitHub Releases (current: {UpdateCheckerService.CurrentVersionTag})...";
        UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));

        try
        {
            UpdateCheckResult result = await _updater.CheckForUpdatesAsync();
            if (!result.IsUpdateAvailable)
            {
                UpdateStatusText.Text = $"You are up to date ({result.CurrentTag}).";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
                return;
            }

            if (string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                UpdateStatusText.Text = $"Release {result.LatestTag} found, but CleanSlate-Setup.exe is not attached yet.";
                UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
                return;
            }

            _pendingUpdate = result;
            double sizeMb = result.AssetSizeBytes > 0 ? result.AssetSizeBytes / (1024.0 * 1024.0) : 0;
            string sizeSuffix = sizeMb > 0 ? $" ({sizeMb:0.0} MB)" : string.Empty;
            UpdateStatusText.Text = $"Update available: {result.LatestTag}{sizeSuffix}. Click Install Update to upgrade automatically.";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
            InstallUpdateButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update check failed: {ex.Message}";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate?.DownloadUrl is not string downloadUrl)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = $"Downloading {_pendingUpdate.LatestTag}...";
        UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));

        Progress<double> progress = new(percent =>
        {
            UpdateStatusText.Text = $"Downloading {_pendingUpdate.LatestTag}... {percent:0}%";
        });

        try
        {
            await _updater.DownloadAndInstallAsync(downloadUrl, progress);
            UpdateStatusText.Text = "Installing update and restarting CleanSlate...";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = $"Update failed: {ex.Message}";
            UpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50));
            CheckUpdatesButton.IsEnabled = true;
            InstallUpdateButton.IsEnabled = true;
        }
    }

    public void FlushPendingIconSizeSave()
    {
        if (!_iconSizeSaveTimer.IsEnabled)
        {
            return;
        }

        _iconSizeSaveTimer.Stop();
        _store.Save(_model);
        _store.NotifyRunningCleanSlate();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_activateMsg != 0 && (uint)msg == _activateMsg)
        {
            if (_captureTarget == ShortcutCaptureTarget.None)
            {
                LoadCurrentSettings();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            SettingsStore.NativeMethods.SetForegroundWindow(hwnd);
            Activate();
            handled = true;
        }

        return IntPtr.Zero;
    }
}
