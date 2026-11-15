using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CleanSlate.Core;
using CleanSlate.Models;
using CleanSlate.Services;
using CleanSlate.UI;
using Microsoft.VisualBasic.FileIO;

namespace CleanSlate;

public sealed class DockBoundsChangedEventArgs : EventArgs
{
    public DockBoundsChangedEventArgs(
        bool isVisible,
        IntPtr hwnd,
        ScreenBounds physicalBounds,
        bool isAnimating = false,
        bool isDragging = false)
    {
        IsVisible = isVisible;
        Hwnd = hwnd;
        PhysicalBounds = physicalBounds;
        IsAnimating = isAnimating;
        IsDragging = isDragging;
    }

    public bool IsVisible { get; }
    public IntPtr Hwnd { get; }
    public ScreenBounds PhysicalBounds { get; }
    public bool IsAnimating { get; }
    public bool IsDragging { get; }
}

public partial class MainWindow : Window
{
    public const double OuterShadowMarginDip = 24.0;
    public const double MiniDesktopCellGapX = 8.0;
    public const double MiniDesktopCellGapY = 6.0;
    private const string DragItemFormat = "CleanSlateItem";

    private readonly record struct MiniDesktopGridMetrics(
        int Columns,
        int Rows,
        double ContentWidth,
        double ContentViewportHeight,
        double OriginX,
        double OriginY,
        double CellStepX,
        double CellStepY);

    private static readonly Brush FrostedBackgroundBrush = CreateFrozenBrush(0xE6, 0x18, 0x18, 0x18);
    private static readonly Brush FrostedBorderBrush = CreateFrozenBrush(0x10, 0xFF, 0xFF, 0xFF);
    private static readonly Brush HitTestTransparentBrush = CreateFrozenBrush(0x01, 0x00, 0x00, 0x00);
    private static readonly Brush MiniDesktopGridCellBorderBrush = CreateFrozenBrush(0x38, 0xFF, 0xFF, 0xFF);
    private static readonly Brush MiniDesktopGridCellFillBrush = CreateFrozenBrush(0x08, 0xFF, 0xFF, 0xFF);
    private static readonly DropShadowEffect FrostedDropShadow = CreateFrozenDropShadow();

    public const string SpecialSettingsId = "__cleanslate_settings__";

    public static readonly DependencyProperty ShowDockBackgroundProperty =
        DependencyProperty.Register(nameof(ShowDockBackground), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowLabelsProperty =
        DependencyProperty.Register(nameof(ShowLabels), typeof(bool), typeof(MainWindow), new PropertyMetadata(true));

    public static readonly DependencyProperty ShowCornerRecycleBinProperty =
        DependencyProperty.Register(nameof(ShowCornerRecycleBin), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowSettingsButtonProperty =
        DependencyProperty.Register(nameof(ShowSettingsButton), typeof(bool), typeof(MainWindow), new PropertyMetadata(true, OnShowSettingsButtonChanged));

    public static readonly DependencyProperty MiniDesktopModeProperty =
        DependencyProperty.Register(nameof(MiniDesktopMode), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    public static readonly DependencyProperty MiniDesktopGridWidthProperty =
        DependencyProperty.Register(nameof(MiniDesktopGridWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(492.0));

    public static readonly DependencyProperty MiniDesktopGridHeightProperty =
        DependencyProperty.Register(nameof(MiniDesktopGridHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(332.0));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(MainWindow), new PropertyMetadata(48.0));

    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(88.0));

    public static readonly DependencyProperty TileHeightProperty =
        DependencyProperty.Register(nameof(TileHeight), typeof(double), typeof(MainWindow), new PropertyMetadata(104.0));

    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(nameof(LabelWidth), typeof(double), typeof(MainWindow), new PropertyMetadata(78.0));

    public static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.Register(
            nameof(AnimatedVerticalOffset),
            typeof(double),
            typeof(MainWindow),
            new PropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

    private readonly ConfigService _configService;
    private readonly IconExtractor _iconExtractor;

    private bool _allowClose;
    private bool _isShowing;
    private bool _isOpeningGenie;
    private bool _isClosingGenie;
    private long _lastRevealCompletedTimeMs;
    private int _animationGeneration;
    private bool _isDragging;
    private Point _dragStartPoint;
    private DispatcherTimer? _idleTrimTimer;
    private DockItem? _dragCandidateItem;
    private DockItem? _activeDropTarget;
    private DockItem? _keyboardSelectedItem;
    private DockItem? _renameTargetItem;
    private ContextMenu? _activeContextMenu;
    private DragIconOverlay? _dragIconOverlay;
    private ScreenBounds _dragDockPhysBounds;
    private bool _isOutsideDockDuringDrag;
    private double _smoothScrollTargetOffset;
    private bool _isSmoothScrolling;
    private int _smoothScrollSequence;
    private bool _edgeRefreshQueued;
    private ICollectionView? _itemsView;
    private DockItem? _autocompleteTargetItem;
    private bool _isSearchOpen;
    private int _searchAnimSeq;
    private bool _suppressNextTextInput;
    private LinearGradientBrush? _edgeFadeMaskBrush;
    private GradientStop? _topEdgeFadeStop;
    private GradientStop? _bottomEdgeFadeStop;
    private ScreenBounds _lastCapturePhysicalBounds;
    private ScreenBounds _lastCardPhysicalBounds;
    private NativeMethods.POINT _lastSummonCursorPhys;
    private GenieOverlayWindow? _activeGenieOverlay;
    private BitmapSource? _lastSnapshot;
    private double _lastSnapshotScale = 1.0;
    private int _cachedGridCols;
    private int _cachedGridRows;
    private int _cachedMiniDesktopTotalRows = 1;
    private int _cachedMiniDesktopVisibleRows = 1;
    private int _lastMiniDesktopColumns = 0;
    private int _lastMiniDesktopRows = 0;
    private double _lastMiniDesktopWidth = 0.0;
    private double _lastMiniDesktopHeight = 0.0;
    private double _cachedGridOriginX;
    private double _cachedGridOriginY;
    private double _cachedGridTileW;
    private double _cachedGridTileH;
    private bool _isMiniDesktopDragGridVisible;

    public event EventHandler<DockBoundsChangedEventArgs>? DockBoundsChanged;

    public MainWindow(ConfigService configService, IconExtractor iconExtractor)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _iconExtractor = iconExtractor ?? throw new ArgumentNullException(nameof(iconExtractor));

        InitializeComponent();
        ApplyVisualSettings();
        BindItems();
        _configService.Config.Items.CollectionChanged += (_, _) => OnConfigItemsCollectionChanged();
    }

    public bool ShowDockBackground
    {
        get => (bool)GetValue(ShowDockBackgroundProperty);
        set => SetValue(ShowDockBackgroundProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    public bool ShowCornerRecycleBin
    {
        get => (bool)GetValue(ShowCornerRecycleBinProperty);
        set => SetValue(ShowCornerRecycleBinProperty, value);
    }

    public bool ShowSettingsButton
    {
        get => (bool)GetValue(ShowSettingsButtonProperty);
        set => SetValue(ShowSettingsButtonProperty, value);
    }

    public bool MiniDesktopMode
    {
        get => (bool)GetValue(MiniDesktopModeProperty);
        set => SetValue(MiniDesktopModeProperty, value);
    }

    public double MiniDesktopGridWidth
    {
        get => (double)GetValue(MiniDesktopGridWidthProperty);
        set => SetValue(MiniDesktopGridWidthProperty, value);
    }

    public double MiniDesktopGridHeight
    {
        get => (double)GetValue(MiniDesktopGridHeightProperty);
        set => SetValue(MiniDesktopGridHeightProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    public double TileHeight
    {
        get => (double)GetValue(TileHeightProperty);
        set => SetValue(TileHeightProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public double AnimatedVerticalOffset
    {
        get => (double)GetValue(AnimatedVerticalOffsetProperty);
        set => SetValue(AnimatedVerticalOffsetProperty, value);
    }

    public bool AllowClose
    {
        get => _allowClose;
        set => _allowClose = value;
    }

    public void ApplyVisualSettings(int? cursorPhysX = null, int? cursorPhysY = null)
    {
        AppSettings s = _configService.Config.Settings;
        ShowDockBackground = false;
        MiniDesktopMode = s.MiniDesktopMode;
        ShowCornerRecycleBin = s.ShowCornerRecycleBin;
        ShowSettingsButton = s.ShowSettingsButton;

        if (!s.EnableSearch && _isSearchOpen)
        {
            CloseSearchBar(animate: false);
        }

        double iconSize = Math.Clamp(s.IconSize, 24, 96);
        IconSize = iconSize;
        ShowLabels = s.ShowLabels;

        double tileWidth = Math.Max(78.0, iconSize + 40.0);
        TileWidth = tileWidth;
        TileHeight = s.ShowLabels ? iconSize + 56.0 : iconSize + 24.0;
        LabelWidth = Math.Max(64.0, tileWidth - 10.0);

        (double cardWidth, double cardHeight) = GetDockLogicalSizeForCursor(cursorPhysX, cursorPhysY);
        DockRootBorder.Width = cardWidth;
        DockRootBorder.Height = cardHeight;

        // Keep app strictly without a background; alpha=1 (#01000000) delivers Win32 scroll/mouse hit-tests over empty gaps.
        DockRootBorder.Background = HitTestTransparentBrush;
        DockRootBorder.BorderBrush = Brushes.Transparent;
        DockRootBorder.BorderThickness = new Thickness(0);
        DockRootBorder.Effect = null;

        if (s.MiniDesktopMode)
        {
            ResetScrollToTop();
            SyncMiniDesktopGridLayout(persistIfAssigned: true, forceReflow: false);
        }
        else
        {
            HideMiniDesktopDragGrid(immediate: true);
            RecycleBinCornerButton.Margin = new Thickness(0, 4, 0, 0);
            ItemsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }

        if (s.ShowCornerRecycleBin)
        {
            UpdateRecycleBinCornerIcon();
        }

        UpdateEdgeEffects();
        SyncSettingsDockItem(s.ShowSettingsButton);
    }

    private void OnConfigItemsCollectionChanged()
    {
        if (_configService.IsReloading)
        {
            return;
        }

        if (MiniDesktopMode)
        {
            SyncMiniDesktopGridLayout(persistIfAssigned: true);
        }

        ScheduleEdgeEffectsRefresh();
    }

    private void BindItems()
    {
        _itemsView = CollectionViewSource.GetDefaultView(_configService.Config.Items);
        DockItemsControl.ItemsSource = _itemsView;
    }

    public void PreloadIcons()
    {
        foreach (DockItem item in _configService.Config.Items)
        {
            if (item.Icon is null)
            {
                if (string.Equals(item.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
                {
                    item.Icon = _iconExtractor.GetSettingsIcon();
                }
                else
                {
                    string iconSource = string.IsNullOrWhiteSpace(item.IconPath) ? item.Path : item.IconPath!;
                    item.Icon = _iconExtractor.GetIcon(iconSource);
                }
            }
        }
    }

    private static void OnShowSettingsButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow mw)
        {
            mw.SyncSettingsDockItem((bool)e.NewValue);
        }
    }

    public void SyncSettingsDockItem(bool show)
    {
        var existing = _configService.Config.Items.FirstOrDefault(i => string.Equals(i.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase));
        if (show)
        {
            if (existing is null)
            {
                var settingsItem = new DockItem
                {
                    Id = SpecialSettingsId,
                    Name = "Settings",
                    Path = "cleanslate://settings",
                    Icon = _iconExtractor.GetSettingsIcon(),
                    Arguments = string.Empty,
                    WorkingDirectory = string.Empty,
                    AddedAtUtc = DateTime.UtcNow
                };
                // Place at the very end (bottom of the dock) by default
                _configService.Config.Items.Add(settingsItem);
            }
            else
            {
                existing.Icon = _iconExtractor.GetSettingsIcon();
            }

            EnsureDefaultSettingsPosition();
        }
        else
        {
            if (existing is not null)
            {
                _configService.Config.Items.Remove(existing);
            }
        }
    }

    private void EnsureDefaultSettingsPosition()
    {
        var settingsItem = _configService.Config.Items.FirstOrDefault(i => string.Equals(i.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase));
        if (settingsItem is not null)
        {
            int idx = _configService.Config.Items.IndexOf(settingsItem);
            int lastIdx = _configService.Config.Items.Count - 1;
            // Always ensure Settings is at the very end (bottom of the dock) by default
            if (idx >= 0 && idx != lastIdx)
            {
                _configService.Config.Items.Move(idx, lastIdx);
            }
        }
    }

    public void RefreshCurrentView()
    {
        PreloadIcons();
        ScheduleEdgeEffectsRefresh();
    }

    public void ToggleAtCurrentCursor()
    {
        if (IsVisible && !_isOpeningGenie)
        {
            HideDock();
            return;
        }

        ShowAtCurrentCursor();
    }

    public void ShowAtCurrentCursor()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            ShowAtCursor(pt.X, pt.Y);
            return;
        }

        double primaryScale = NativeMethods.GetDpiScaleForPoint(0, 0);
        ShowAtCursor(
            (int)Math.Round((SystemParameters.PrimaryScreenWidth * 0.5) * primaryScale),
            (int)Math.Round((SystemParameters.PrimaryScreenHeight * 0.5) * primaryScale));
    }

    /// <summary>
    /// Reloads user settings from disk, centers the dock over the specified physical screen
    /// coordinates (clamped inside monitor work area), and plays the Genie bloom animation.
    /// </summary>
    public void ShowAtCursor(int cursorPhysX, int cursorPhysY)
    {
        if (_isOpeningGenie)
        {
            return;
        }

        _idleTrimTimer?.Stop();

        // Reload %LocalAppData%\CleanSlate\config.json on every summon so CleanSlate.Settings changes apply immediately.
        _configService.ReloadFromDisk();
        _lastSummonCursorPhys = new NativeMethods.POINT { X = cursorPhysX, Y = cursorPhysY };
        ApplyVisualSettings(cursorPhysX, cursorPhysY);

        int gen = ++_animationGeneration;
        _activeGenieOverlay?.CancelAndClose();
        _activeGenieOverlay = null;
        _isClosingGenie = false;

        if (IsVisible)
        {
            PreloadIcons();
            UpdateEdgeEffects();
            PositionAndClamp(cursorPhysX, cursorPhysY, out ScreenBounds captureBounds, out ScreenBounds visibleBounds);
            _lastCapturePhysicalBounds = captureBounds;
            _lastCardPhysicalBounds = visibleBounds;
            IntPtr visibleHwnd = new WindowInteropHelper(this).Handle;
            NotifyBoundsChanged(true, visibleHwnd, visibleBounds);
            NativeMethods.ForceForegroundWindow(visibleHwnd);
            this.Activate();
            this.Focus();
            Keyboard.Focus(_isSearchOpen ? SearchTextBox : this);
            return;
        }

        _isShowing = true;
        _isOpeningGenie = true;
        try
        {
            CloseActiveContextMenu();
            CloseSearchBar(animate: false);
            PreloadIcons();
            ResetScrollToTop();

            IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
            AcrylicHelper.EnableAcrylic(this, DockRootBorder, 14);

            ComputeClampedBounds(cursorPhysX, cursorPhysY, out int physX, out int physY, out int physW, out int physH, out double scale);

            var captureRect = new ScreenBounds(physX, physY, physX + physW, physY + physH);
            ScreenBounds visibleRect = ComputeVisibleCardRect(physX, physY, physW, physH, scale);
            _lastCapturePhysicalBounds = captureRect;
            _lastCardPhysicalBounds = visibleRect;
            NotifyBoundsChanged(true, hwnd, visibleRect, isAnimating: true);

            // Render snapshot off-screen without calling MainWindow.Show() yet so the window never flashes twice.
            BitmapSource? snapshot = CaptureDockSnapshotOffscreen(scale);
            _lastSnapshot = snapshot;
            _lastSnapshotScale = scale;

            if (snapshot is not null)
            {
                _activeGenieOverlay = new GenieOverlayWindow(
                    snapshot,
                    captureRect,
                    cursorPhysX,
                    cursorPhysY,
                    scale,
                    isOpening: true,
                    durationMs: 250,
                    () =>
                    {
                        if (_animationGeneration != gen)
                        {
                            return;
                        }

                        _activeGenieOverlay = null;
                        try
                        {
                            RevealMainWindowAt(hwnd, physX, physY, physW, physH, scale, visibleRect);
                        }
                        finally
                        {
                            _isOpeningGenie = false;
                            _isShowing = false;
                        }
                    });
                _activeGenieOverlay.Start();
            }
            else
            {
                try
                {
                    RevealMainWindowAt(hwnd, physX, physY, physW, physH, scale, visibleRect);
                }
                finally
                {
                    _isOpeningGenie = false;
                    _isShowing = false;
                }
            }
        }
        catch
        {
            _isOpeningGenie = false;
            _isShowing = false;
            throw;
        }
    }

    private void RevealMainWindowAt(
        IntPtr hwnd,
        int physX,
        int physY,
        int physW,
        int physH,
        double scale,
        ScreenBounds visibleRect)
    {
        this.Opacity = 1.0;
        this.ShowActivated = true;

        DockCaptureRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        DockCaptureRoot.Arrange(new Rect(DockCaptureRoot.DesiredSize));
        DockCaptureRoot.UpdateLayout();
        UpdateEdgeEffects();

        // Pre-position the native HWND onto the target monitor & DPI BEFORE reading TransformFromDevice or Show()
        // so cross-DPI multi-monitor jumps never render a frame at the previous monitor's scale.
        NativeMethods.SetWindowPos(
            hwnd,
            NativeMethods.HWND_TOPMOST,
            physX,
            physY,
            physW,
            physH,
            NativeMethods.SWP_NOACTIVATE);

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            Point topLeftDip = target.TransformFromDevice.Transform(new Point(physX, physY));
            Left = topLeftDip.X;
            Top = topLeftDip.Y;
        }
        else
        {
            Left = physX / scale;
            Top = physY / scale;
        }

        this.Show();
        UpdateLayout();
        UpdateEdgeEffects();
        ScheduleEdgeEffectsRefresh();

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT actualRect) ||
            actualRect.Left != physX ||
            actualRect.Top != physY ||
            (actualRect.Right - actualRect.Left) != physW ||
            (actualRect.Bottom - actualRect.Top) != physH)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                physX,
                physY,
                physW,
                physH,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        NotifyBoundsChanged(true, hwnd, visibleRect, isAnimating: false);
        _lastSnapshot = null;
        NativeMethods.ForceForegroundWindow(hwnd);
        this.Activate();
        this.Focus();
        Keyboard.Focus(this);
        _lastRevealCompletedTimeMs = Environment.TickCount64;
    }

    private void PositionAndClamp(
        int cursorPhysX,
        int cursorPhysY,
        out ScreenBounds captureRect,
        out ScreenBounds visibleCardRect)
    {
        IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
        ComputeClampedBounds(cursorPhysX, cursorPhysY, out int physX, out int physY, out int physW, out int physH, out double scale);
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            physX,
            physY,
            physW,
            physH,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } target)
        {
            Point topLeftDip = target.TransformFromDevice.Transform(new Point(physX, physY));
            Left = topLeftDip.X;
            Top = topLeftDip.Y;
        }
        else
        {
            Left = physX / scale;
            Top = physY / scale;
        }

        captureRect = new ScreenBounds(physX, physY, physX + physW, physY + physH);
        visibleCardRect = ComputeVisibleCardRect(physX, physY, physW, physH, scale);
    }

    public void RepositionAndResizeIfVisible()
    {
        if (!IsVisible)
        {
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ApplyVisualSettings(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y);

        PositionAndClamp(
            _lastSummonCursorPhys.X,
            _lastSummonCursorPhys.Y,
            out ScreenBounds captureRect,
            out ScreenBounds visibleCardRect);

        _lastCapturePhysicalBounds = captureRect;
        _lastCardPhysicalBounds = visibleCardRect;
        NotifyBoundsChanged(true, hwnd, visibleCardRect);
        UpdateLayout();
        UpdateEdgeEffects();
    }

    private static ScreenBounds ComputeVisibleCardRect(int physX, int physY, int physW, int physH, double scale)
    {
        int inset = (int)Math.Round(OuterShadowMarginDip * scale);
        return new ScreenBounds(
            physX + inset,
            physY + inset,
            physX + physW - inset,
            physY + physH - inset);
    }

    public static int ComputeGridCapacity(double dockWidthDip, double dockHeightDip, double tileWidth, double tileHeight)
    {
        double contentW = Math.Max(tileWidth, dockWidthDip - 28.0);
        double contentH = Math.Max(tileHeight, dockHeightDip - 28.0);
        double cellStepX = tileWidth + MiniDesktopCellGapX;
        double cellStepY = tileHeight + MiniDesktopCellGapY;
        int cols = Math.Max(1, (int)Math.Floor((contentW + MiniDesktopCellGapX) / cellStepX));
        int rows = Math.Max(1, (int)Math.Floor((contentH + MiniDesktopCellGapY) / cellStepY));
        return cols * rows;
    }

    public static (double Width, double Height) ComputeDockLogicalSizeForWorkArea(
        AppSettings settings,
        double workWidthDip,
        double workHeightDip,
        int itemCount,
        double tileWidth,
        double tileHeight)
    {
        double maxCardW = Math.Max(300.0, workWidthDip - (OuterShadowMarginDip * 2));
        double maxCardH = Math.Max(220.0, workHeightDip - (OuterShadowMarginDip * 2));

        if (!settings.MiniDesktopMode)
        {
            return (
                Math.Clamp(settings.DockWidth, 300.0, maxCardW),
                Math.Clamp(settings.DockHeight, 220.0, maxCardH));
        }

        // Mini Desktop Mode:
        // If user customized MiniDesktopWidth and MiniDesktopHeight (>= 300x220), use them clamped to maxCardW/H.
        // Otherwise (default/uncustomized < 300x220), default to 1/4-screen (50% work area width and height).
        double defaultMiniW = Math.Clamp(Math.Round(workWidthDip * 0.5), Math.Min(520.0, maxCardW), maxCardW);
        double defaultMiniH = Math.Clamp(Math.Round(workHeightDip * 0.5), Math.Min(360.0, maxCardH), maxCardH);

        double targetW = settings.MiniDesktopWidth >= 300.0
            ? settings.MiniDesktopWidth
            : defaultMiniW;
        double targetH = settings.MiniDesktopHeight >= 220.0
            ? settings.MiniDesktopHeight
            : defaultMiniH;

        return (
            Math.Clamp(targetW, 300.0, maxCardW),
            Math.Clamp(targetH, 220.0, maxCardH));
    }

    public static void ComputeClampedBoundsForWorkArea(
        AppSettings settings,
        int itemCount,
        double tileWidth,
        double tileHeight,
        ScreenBounds workPhys,
        double scale,
        int cursorPhysX,
        int cursorPhysY,
        out int clampedX,
        out int clampedY,
        out int physWidth,
        out int physHeight)
    {
        double workWidthDip = Math.Max(320.0, (workPhys.Right - workPhys.Left) / scale);
        double workHeightDip = Math.Max(220.0, (workPhys.Bottom - workPhys.Top) / scale);

        (double cardWidth, double cardHeight) = ComputeDockLogicalSizeForWorkArea(
            settings, workWidthDip, workHeightDip, itemCount, tileWidth, tileHeight);

        double totalLogicalWidth = cardWidth + (OuterShadowMarginDip * 2);
        double totalLogicalHeight = cardHeight + (OuterShadowMarginDip * 2);

        physWidth = (int)Math.Round(totalLogicalWidth * scale);
        physHeight = (int)Math.Round(totalLogicalHeight * scale);

        int inset = (int)Math.Round(OuterShadowMarginDip * scale);
        int tipGap = (int)Math.Round(8.0 * scale);

        int rawX = cursorPhysX - (physWidth / 2);
        // Position visible dock card above the tip of the mouse:
        // Visible bottom edge: physY + physHeight - inset = cursorPhysY - tipGap.
        int rawY = cursorPhysY - physHeight + inset - tipGap;

        clampedX = Math.Clamp(rawX, workPhys.Left, Math.Max(workPhys.Left, workPhys.Right - physWidth));
        clampedY = Math.Clamp(rawY, workPhys.Top, Math.Max(workPhys.Top, workPhys.Bottom - physHeight));
    }

    private (double Width, double Height) GetDockLogicalSizeForCursor(int? cursorPhysX, int? cursorPhysY)
    {
        AppSettings s = _configService.Config.Settings;
        int refX = cursorPhysX ?? _lastSummonCursorPhys.X;
        int refY = cursorPhysY ?? _lastSummonCursorPhys.Y;
        if (refX == 0 && refY == 0 && NativeMethods.GetCursorPos(out NativeMethods.POINT liveCursor))
        {
            refX = liveCursor.X;
            refY = liveCursor.Y;
        }

        GetMonitorWorkAreaMetrics(refX, refY, out _, out double workWidthDip, out double workHeightDip, out _);
        return ComputeDockLogicalSizeForWorkArea(
            s,
            workWidthDip,
            workHeightDip,
            _configService.Config.Items.Count,
            TileWidth,
            TileHeight);
    }

    private static void GetMonitorWorkAreaMetrics(
        int cursorPhysX,
        int cursorPhysY,
        out NativeMethods.RECT workPhys,
        out double workWidthDip,
        out double workHeightDip,
        out double scale)
    {
        var cursorPt = new NativeMethods.POINT { X = cursorPhysX, Y = cursorPhysY };
        IntPtr hMonitor = NativeMethods.MonitorFromPoint(cursorPt, NativeMethods.MONITOR_DEFAULTTONEAREST);

        uint dpiX = 96;
        if (hMonitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dx, out _) == 0 &&
            dx > 0)
        {
            dpiX = dx;
        }
        scale = dpiX / 96.0;

        var monitorInfo = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (hMonitor != IntPtr.Zero && NativeMethods.GetMonitorInfoW(hMonitor, ref monitorInfo))
        {
            workPhys = monitorInfo.rcWork;
            workWidthDip = Math.Max(320.0, (workPhys.Right - workPhys.Left) / scale);
            workHeightDip = Math.Max(220.0, (workPhys.Bottom - workPhys.Top) / scale);
        }
        else
        {
            Rect fallbackWork = SystemParameters.WorkArea;
            workWidthDip = Math.Max(320.0, fallbackWork.Width);
            workHeightDip = Math.Max(220.0, fallbackWork.Height);
            workPhys = new NativeMethods.RECT
            {
                Left = (int)Math.Round(fallbackWork.Left * scale),
                Top = (int)Math.Round(fallbackWork.Top * scale),
                Right = (int)Math.Round(fallbackWork.Right * scale),
                Bottom = (int)Math.Round(fallbackWork.Bottom * scale)
            };
        }
    }

    private void ComputeClampedBounds(
        int cursorPhysX,
        int cursorPhysY,
        out int clampedX,
        out int clampedY,
        out int physWidth,
        out int physHeight,
        out double scale)
    {
        GetMonitorWorkAreaMetrics(cursorPhysX, cursorPhysY, out NativeMethods.RECT work, out _, out _, out scale);
        ComputeClampedBoundsForWorkArea(
            _configService.Config.Settings,
            _configService.Config.Items.Count,
            TileWidth,
            TileHeight,
            new ScreenBounds(work.Left, work.Top, work.Right, work.Bottom),
            scale,
            cursorPhysX,
            cursorPhysY,
            out clampedX,
            out clampedY,
            out physWidth,
            out physHeight);
    }

    private BitmapSource? CaptureDockSnapshotOffscreen(double targetScale)
    {
        DockCaptureRoot.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        DockCaptureRoot.Arrange(new Rect(DockCaptureRoot.DesiredSize));
        DockCaptureRoot.UpdateLayout();
        UpdateEdgeEffects();
        DockCaptureRoot.UpdateLayout();

        double logicalW = DockCaptureRoot.ActualWidth > 0 ? DockCaptureRoot.ActualWidth : DockCaptureRoot.DesiredSize.Width;
        double logicalH = DockCaptureRoot.ActualHeight > 0 ? DockCaptureRoot.ActualHeight : DockCaptureRoot.DesiredSize.Height;
        if (logicalW <= 0 || logicalH <= 0)
        {
            return null;
        }

        double dpi = 96.0 * Math.Max(1.0, targetScale);
        int pixelW = Math.Max(1, (int)Math.Round(logicalW * targetScale));
        int pixelH = Math.Max(1, (int)Math.Round(logicalH * targetScale));

        var rtb = new RenderTargetBitmap(pixelW, pixelH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(DockCaptureRoot);
        rtb.Freeze();
        return rtb;
    }

    private BitmapSource? CaptureDockSnapshot(out double scale)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        scale = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;

        double logicalW = DockCaptureRoot.ActualWidth;
        double logicalH = DockCaptureRoot.ActualHeight;
        if (logicalW <= 0 || logicalH <= 0)
        {
            return null;
        }

        int pixelW = Math.Max(1, (int)Math.Round(logicalW * scale));
        int pixelH = Math.Max(1, (int)Math.Round(logicalH * dpi.DpiScaleY));

        var rtb = new RenderTargetBitmap(
            pixelW,
            pixelH,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        rtb.Render(DockCaptureRoot);
        rtb.Freeze();
        return rtb;
    }

    public void HideDock(bool animate = true)
    {
        if (_isClosingGenie && animate)
        {
            return;
        }

        _dragCandidateItem = null;
        CloseActiveContextMenu();
        CancelSmoothScrollAnimation();
        CloseDragOverlays();
        ClearDropInsertIndicator();
        CloseRenameOverlay(focusDock: false);
        ClearKeyboardSelection();
        NotifyBoundsChanged(false, IntPtr.Zero, default);

        bool wasOpening = _isOpeningGenie;
        int gen = ++_animationGeneration;
        _activeGenieOverlay?.CancelAndClose();
        _activeGenieOverlay = null;
        _isClosingGenie = false;
        _isOpeningGenie = false;

        if (!IsVisible && !wasOpening)
        {
            _lastSnapshot = null;
            CloseSearchBar(animate: false);
            this.Opacity = 1.0;
            return;
        }

        if (!animate)
        {
            _lastSnapshot = null;
            this.Opacity = 1.0;
            if (IsVisible)
            {
                this.Hide();
            }
            CloseSearchBar(animate: false);
            ScheduleIdleMemoryTrim();
            return;
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT liveHwndRect))
        {
            _lastCapturePhysicalBounds = new ScreenBounds(
                liveHwndRect.Left,
                liveHwndRect.Top,
                liveHwndRect.Right,
                liveHwndRect.Bottom);
        }

        UpdateLayout();

        double captureScale = _lastSnapshotScale;
        BitmapSource? snapshot = IsVisible
            ? CaptureDockSnapshot(out captureScale)
            : _lastSnapshot;
        _lastSnapshot = null;

        if (snapshot is null)
        {
            if (IsVisible)
            {
                this.Hide();
            }
            CloseSearchBar(animate: false);
            this.Opacity = 1.0;
            ScheduleIdleMemoryTrim();
            return;
        }

        NativeMethods.POINT tipPt = NativeMethods.GetCursorPos(out NativeMethods.POINT livePt)
            ? livePt
            : _lastSummonCursorPhys;

        _isClosingGenie = true;
        _activeGenieOverlay = new GenieOverlayWindow(
            snapshot,
            _lastCapturePhysicalBounds,
            tipPt.X,
            tipPt.Y,
            captureScale,
            isOpening: false,
            durationMs: 260,
            () =>
            {
                if (_animationGeneration == gen)
                {
                    _activeGenieOverlay = null;
                    _isClosingGenie = false;
                    ScheduleIdleMemoryTrim();
                }
            });

        _activeGenieOverlay.Start();

        if (IsVisible)
        {
            this.Hide();
        }
        CloseSearchBar(animate: false);
        this.Opacity = 1.0;
    }

    private void NotifyBoundsChanged(
        bool isVisible,
        IntPtr hwnd,
        ScreenBounds bounds,
        bool isAnimating = false,
        bool isDragging = false)
    {
        DockBoundsChanged?.Invoke(this, new DockBoundsChangedEventArgs(isVisible, hwnd, bounds, isAnimating, isDragging));
    }

    // --- Smooth Inertial Scrolling & Borderless Edge Fade / Soft Shrink ---

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MainWindow window)
        {
            window.ItemsScrollViewer.ScrollToVerticalOffset((double)e.NewValue);
        }
    }

    private void OnFallbackDockMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MiniDesktopMode && _cachedMiniDesktopTotalRows <= _cachedMiniDesktopVisibleRows)
        {
            e.Handled = true;
            return;
        }

        if (!e.Handled)
        {
            HandleSmoothMouseWheel(e);
        }
    }

    private void OnItemsScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (MiniDesktopMode && _cachedMiniDesktopTotalRows <= _cachedMiniDesktopVisibleRows)
        {
            e.Handled = true;
            return;
        }

        HandleSmoothMouseWheel(e);
    }

    private void OnItemsScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (MiniDesktopMode && _cachedMiniDesktopTotalRows <= _cachedMiniDesktopVisibleRows && ItemsScrollViewer.VerticalOffset > 0)
        {
            ItemsScrollViewer.ScrollToVerticalOffset(0);
            return;
        }

        if (!_isSmoothScrolling && Math.Abs(e.VerticalChange) > 0.01)
        {
            _smoothScrollTargetOffset = ItemsScrollViewer.VerticalOffset;
        }

        UpdateEdgeEffects();
    }

    private void OnItemsScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateEdgeEffects();
    }

    private void HandleSmoothMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;

        if (MiniDesktopMode && _cachedMiniDesktopTotalRows <= _cachedMiniDesktopVisibleRows)
        {
            return;
        }

        if (ItemsScrollViewer.ScrollableHeight <= 0.5 || e.Delta == 0)
        {
            return;
        }

        double currentOffset = ItemsScrollViewer.VerticalOffset;
        double baseTarget = _isSmoothScrolling ? _smoothScrollTargetOffset : currentOffset;
        double newTarget = Math.Clamp(baseTarget - e.Delta, 0.0, ItemsScrollViewer.ScrollableHeight);

        if (Math.Abs(newTarget - currentOffset) < 0.5)
        {
            _smoothScrollTargetOffset = newTarget;
            ItemsScrollViewer.ScrollToVerticalOffset(newTarget);
            return;
        }

        _smoothScrollTargetOffset = newTarget;
        _isSmoothScrolling = true;
        int seq = ++_smoothScrollSequence;

        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        AnimatedVerticalOffset = currentOffset;

        var scrollAnim = new DoubleAnimation
        {
            From = currentOffset,
            To = newTarget,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        scrollAnim.Completed += (_, _) =>
        {
            if (_smoothScrollSequence == seq)
            {
                _isSmoothScrolling = false;
            }
        };

        BeginAnimation(AnimatedVerticalOffsetProperty, scrollAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelSmoothScrollAnimation()
    {
        ++_smoothScrollSequence;
        _isSmoothScrolling = false;
        double current = ItemsScrollViewer.VerticalOffset;
        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        AnimatedVerticalOffset = current;
        _smoothScrollTargetOffset = current;
    }

    private void ResetScrollToTop()
    {
        ++_smoothScrollSequence;
        _isSmoothScrolling = false;
        BeginAnimation(AnimatedVerticalOffsetProperty, null);
        AnimatedVerticalOffset = 0.0;
        _smoothScrollTargetOffset = 0.0;
        ItemsScrollViewer.ScrollToTop();
    }

    private void ScheduleEdgeEffectsRefresh()
    {
        if (_edgeRefreshQueued)
        {
            return;
        }

        _edgeRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _edgeRefreshQueued = false;
            UpdateEdgeEffects();
        });
    }

    private void UpdateEdgeEffects()
    {
        if (_configService.Config.Settings.ShowBackground)
        {
            ItemsScrollViewer.OpacityMask = null;
            ResetEdgeItemEffects();
            return;
        }

        double viewportH = ItemsScrollViewer.ViewportHeight > 0
            ? ItemsScrollViewer.ViewportHeight
            : ItemsScrollViewer.ActualHeight;
        if (viewportH <= 1.0)
        {
            return;
        }

        double vOffset = ItemsScrollViewer.VerticalOffset;
        double scrollable = ItemsScrollViewer.ScrollableHeight;
        double topFadeFactor = Math.Clamp(vOffset / 16.0, 0.0, 1.0);
        double bottomFadeFactor = scrollable > 0.5
            ? Math.Clamp((scrollable - vOffset) / 16.0, 0.0, 1.0)
            : 0.0;

        EnsureEdgeFadeMaskBrush();
        if (_topEdgeFadeStop is not null && _bottomEdgeFadeStop is not null && _edgeFadeMaskBrush is not null)
        {
            _topEdgeFadeStop.Color = Color.FromArgb((byte)Math.Round(255.0 * (1.0 - topFadeFactor)), 0, 0, 0);
            _bottomEdgeFadeStop.Color = Color.FromArgb((byte)Math.Round(255.0 * (1.0 - bottomFadeFactor)), 0, 0, 0);
            if (!ReferenceEquals(ItemsScrollViewer.OpacityMask, _edgeFadeMaskBrush))
            {
                ItemsScrollViewer.OpacityMask = _edgeFadeMaskBrush;
            }
        }

        double viewportW = ItemsScrollViewer.ViewportWidth > 0
            ? ItemsScrollViewer.ViewportWidth
            : ItemsScrollViewer.ActualWidth;

        double edgeBand = viewportH * 0.10;
        double fullMargin = edgeBand + 8.0;
        const double vanishPastEdge = 32.0;
        double span = Math.Max(1.0, fullMargin + vanishPastEdge);

        int count = DockItemsControl.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (DockItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp ||
                cp.ActualHeight <= 0)
            {
                continue;
            }

            Point topLeft = cp.TranslatePoint(new Point(0, 0), ItemsScrollViewer);
            double centerY = topLeft.Y + (cp.ActualHeight * 0.5);

            double topFactor = topFadeFactor > 0.01
                ? Math.Clamp((centerY + vanishPastEdge) / span, 0.0, 1.0)
                : 1.0;
            double bottomFactor = bottomFadeFactor > 0.01
                ? Math.Clamp(((viewportH + vanishPastEdge) - centerY) / span, 0.0, 1.0)
                : 1.0;

            double effectiveTop = 1.0 - ((1.0 - topFactor) * topFadeFactor);
            double effectiveBottom = 1.0 - ((1.0 - bottomFactor) * bottomFadeFactor);
            double scrollFactor = Math.Min(effectiveTop, effectiveBottom);

            double scale = 0.7 + (0.3 * scrollFactor);
            double opacity = 0.35 + (0.65 * scrollFactor);

            ApplyContainerEdgeTransform(cp, scale, opacity);
        }
    }

    private void ResetEdgeItemEffects()
    {
        int count = DockItemsControl.Items.Count;
        for (int i = 0; i < count; i++)
        {
            if (DockItemsControl.ItemContainerGenerator.ContainerFromIndex(i) is ContentPresenter cp)
            {
                ApplyContainerEdgeTransform(cp, 1.0, 1.0);
            }
        }
    }

    private void EnsureEdgeFadeMaskBrush()
    {
        if (_edgeFadeMaskBrush is not null)
        {
            return;
        }

        _topEdgeFadeStop = new GradientStop(Colors.Black, 0.0);
        _bottomEdgeFadeStop = new GradientStop(Colors.Black, 1.0);
        _edgeFadeMaskBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops = new GradientStopCollection
            {
                _topEdgeFadeStop,
                new GradientStop(Colors.Black, 0.10),
                new GradientStop(Colors.Black, 0.90),
                _bottomEdgeFadeStop
            }
        };
    }

    private static void ApplyContainerEdgeTransform(ContentPresenter cp, double scale, double opacity)
    {
        cp.RenderTransformOrigin = new Point(0.5, 0.5);
        if (cp.RenderTransform is not ScaleTransform st || st.IsFrozen)
        {
            st = new ScaleTransform(scale, scale);
            cp.RenderTransform = st;
        }
        else
        {
            if (Math.Abs(st.ScaleX - scale) > 0.001)
            {
                st.ScaleX = scale;
            }

            if (Math.Abs(st.ScaleY - scale) > 0.001)
            {
                st.ScaleY = scale;
            }
        }

        if (Math.Abs(cp.Opacity - opacity) > 0.001)
        {
            cp.Opacity = opacity;
        }

        if (cp.Effect is not null)
        {
            cp.Effect = null;
        }
    }

    // --- Shortcut Tile Drag Source, Reordering & Drag-Out Recycle Bubble ---

    private void OnDockItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragCandidateItem = (sender as FrameworkElement)?.DataContext as DockItem;
    }

    private void OnDockItemPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidateItem is null || _isDragging)
        {
            return;
        }

        Point currentPos = e.GetPosition(null);
        Vector delta = currentPos - _dragStartPoint;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (sender is not DependencyObject dragSource)
        {
            return;
        }

        DockItem itemToDrag = _dragCandidateItem;
        _dragCandidateItem = null;
        _isDragging = true;
        _isOutsideDockDuringDrag = false;
        _dragDockPhysBounds = GetLiveDockCardPhysicalBounds();

        if (MiniDesktopMode)
        {
            ShowMiniDesktopDragGrid();
        }

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NotifyBoundsChanged(true, hwnd, _dragDockPhysBounds, isAnimating: false, isDragging: true);

        _dragIconOverlay = new DragIconOverlay(itemToDrag.Icon, _configService.Config.Settings.IconSize);
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT startCursorPhys))
        {
            _dragIconOverlay.UpdatePosition(startCursorPhys.X, startCursorPhys.Y);
        }

        DragDrop.AddQueryContinueDragHandler(dragSource, OnDockItemQueryContinueDrag);
        DragDrop.AddGiveFeedbackHandler(dragSource, OnDockItemGiveFeedback);

        try
        {
            if (sender is UIElement uiElem && uiElem.IsMouseCaptured)
            {
                uiElem.ReleaseMouseCapture();
            }

            var data = new DataObject();
            data.SetData(DragItemFormat, itemToDrag);

            string dropPath = itemToDrag.Path;
            if (string.Equals(Path.GetExtension(dropPath), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string? resolved = NativeMethods.ResolveShortcutTarget(dropPath);
                if (!string.IsNullOrWhiteSpace(resolved) && (File.Exists(resolved) || Directory.Exists(resolved)))
                {
                    dropPath = resolved;
                }
            }

            string actualFile = File.Exists(dropPath) || Directory.Exists(dropPath) ? dropPath : itemToDrag.Path;
            if (File.Exists(actualFile) || Directory.Exists(actualFile))
            {
                var files = new System.Collections.Specialized.StringCollection { actualFile };
                data.SetFileDropList(files);
                data.SetData(DataFormats.FileDrop, new[] { actualFile });
                data.SetData(DataFormats.UnicodeText, actualFile);
                data.SetData(DataFormats.Text, actualFile);
            }

            DragDrop.DoDragDrop(dragSource, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            DragDrop.RemoveQueryContinueDragHandler(dragSource, OnDockItemQueryContinueDrag);
            DragDrop.RemoveGiveFeedbackHandler(dragSource, OnDockItemGiveFeedback);

            _isOutsideDockDuringDrag = false;
            _isDragging = false;

            CloseDragOverlays();

            ClearDropInsertIndicator();
            HideGridSnapHighlight();
            NotifyBoundsChanged(IsVisible, hwnd, _lastCardPhysicalBounds, isAnimating: false, isDragging: false);
        }
    }

    private void CloseDragOverlays()
    {
        _dragIconOverlay?.CloseImmediate();
        _dragIconOverlay = null;
        HideGridSnapHighlight();
        HideMiniDesktopDragGrid(immediate: true);
    }

    private void OnDockItemQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed || e.KeyStates.HasFlag(DragDropKeyStates.RightMouseButton))
        {
            _isOutsideDockDuringDrag = false;
            _dragIconOverlay?.CloseImmediate();
            _dragIconOverlay = null;
            HideGridSnapHighlight();
            HideMiniDesktopDragGrid();
            e.Action = DragAction.Cancel;
            e.Handled = true;
            return;
        }

        bool hasCursor = NativeMethods.GetCursorPos(out NativeMethods.POINT cursorPhys);
        bool insideDock = !hasCursor || IsPointInsideBounds(cursorPhys, _dragDockPhysBounds);

        if (!e.KeyStates.HasFlag(DragDropKeyStates.LeftMouseButton))
        {
            _dragIconOverlay?.CloseImmediate();
            _dragIconOverlay = null;
            HideGridSnapHighlight();
            HideMiniDesktopDragGrid();

            // When left button is released, allow drop to complete on target (internal dock item or external app like Discord).
            e.Action = DragAction.Drop;
            e.Handled = true;
            return;
        }

        if (insideDock)
        {
            if (_isOutsideDockDuringDrag)
            {
                _isOutsideDockDuringDrag = false;
                if (MiniDesktopMode)
                {
                    ShowMiniDesktopDragGrid();
                }
                _dragIconOverlay?.SetVisibility(true);
            }
        }
        else if (!_isOutsideDockDuringDrag)
        {
            _isOutsideDockDuringDrag = true;
            HideGridSnapHighlight();
            HideMiniDesktopDragGrid();
            _dragIconOverlay?.SetVisibility(false);
        }

        if (hasCursor)
        {
            _dragIconOverlay?.UpdatePosition(cursorPhys.X, cursorPhys.Y);
        }

        e.Action = DragAction.Continue;
        e.Handled = true;
    }

    private void OnDockItemGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        if (_isOutsideDockDuringDrag)
        {
            e.UseDefaultCursors = true;
            e.Handled = true;
            return;
        }

        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.Arrow);
        e.Handled = true;
    }

    private ScreenBounds GetLiveDockCardPhysicalBounds()
    {
        try
        {
            if (IsVisible && DockRootBorder.ActualWidth > 0 && DockRootBorder.ActualHeight > 0)
            {
                Point topLeftPhys = DockRootBorder.PointToScreen(new Point(0, 0));
                Point bottomRightPhys = DockRootBorder.PointToScreen(
                    new Point(DockRootBorder.ActualWidth, DockRootBorder.ActualHeight));

                return new ScreenBounds(
                    (int)Math.Round(topLeftPhys.X),
                    (int)Math.Round(topLeftPhys.Y),
                    (int)Math.Round(bottomRightPhys.X),
                    (int)Math.Round(bottomRightPhys.Y));
            }
        }
        catch
        {
            // Fall back to last recorded card bounds if presentation source isn't attached.
        }

        return _lastCardPhysicalBounds;
    }

    private static bool IsPointInsideBounds(NativeMethods.POINT pt, ScreenBounds rect) =>
        pt.X >= rect.Left && pt.X <= rect.Right &&
        pt.Y >= rect.Top && pt.Y <= rect.Bottom;

    private void OnDockItemDragOver(object sender, DragEventArgs e)
    {
        AutoScrollItemsViewer(e);

        if (MiniDesktopMode)
        {
            ClearDropInsertIndicator();
            if (e.Data.GetDataPresent(DragItemFormat) &&
                e.Data.GetData(DragItemFormat) is DockItem)
            {
                ShowMiniDesktopDragGrid();
                UpdateMiniDesktopSnapHighlight(e);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                ShowMiniDesktopDragGrid();
                UpdateMiniDesktopSnapHighlight(e);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            return;
        }

        if (e.Data.GetDataPresent(DragItemFormat) &&
            e.Data.GetData(DragItemFormat) is DockItem draggedItem &&
            sender is FrameworkElement { DataContext: DockItem targetItem } tileElement)
        {
            if (ReferenceEquals(draggedItem, targetItem))
            {
                ClearDropInsertIndicator();
                e.Effects = DragDropEffects.Move;
            }
            else
            {
                bool insertAfter = e.GetPosition(tileElement).X >= (tileElement.ActualWidth / 2.0);
                SetDropInsertIndicator(targetItem, insertAfter);
                e.Effects = DragDropEffects.Move;
            }

            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ClearDropInsertIndicator();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDockItemDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockItem targetItem } &&
            ReferenceEquals(_activeDropTarget, targetItem))
        {
            ClearDropInsertIndicator();
        }
    }

    private void OnDockItemDrop(object sender, DragEventArgs e)
    {
        ClearDropInsertIndicator();
        HideGridSnapHighlight();
        HideMiniDesktopDragGrid();

        if (MiniDesktopMode)
        {
            (int dropCol, int dropRow) = ResolveDropGridCell(e);
            if (e.Data.GetDataPresent(DragItemFormat) &&
                e.Data.GetData(DragItemFormat) is DockItem gridDragged)
            {
                PlaceOrSwapItemAtGridCell(gridDragged, dropCol, dropRow);
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                HandleExternalFileDrop(e, dropCol, dropRow);
                e.Handled = true;
            }
            return;
        }

        if (e.Data.GetDataPresent(DragItemFormat) &&
            e.Data.GetData(DragItemFormat) is DockItem draggedItem &&
            sender is FrameworkElement { DataContext: DockItem targetItem } tileElement)
        {
            bool insertAfter = e.GetPosition(tileElement).X >= (tileElement.ActualWidth / 2.0);
            ReorderItemRelativeTo(draggedItem, targetItem, insertAfter);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            HandleExternalFileDrop(e);
            e.Handled = true;
        }
    }

    private void OnItemsAreaDragOver(object sender, DragEventArgs e)
    {
        AutoScrollItemsViewer(e);

        if (MiniDesktopMode)
        {
            ClearDropInsertIndicator();
            if (e.Data.GetDataPresent(DragItemFormat))
            {
                ShowMiniDesktopDragGrid();
                UpdateMiniDesktopSnapHighlight(e);
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
            else if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                ShowMiniDesktopDragGrid();
                UpdateMiniDesktopSnapHighlight(e);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            return;
        }

        if (e.Data.GetDataPresent(DragItemFormat))
        {
            ClearDropInsertIndicator();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            ClearDropInsertIndicator();
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnItemsAreaDragLeave(object sender, DragEventArgs e)
    {
        Point pos = e.GetPosition(DockRootBorder);
        bool outside = pos.X < 0 || pos.Y < 0 ||
                       pos.X > DockRootBorder.ActualWidth || pos.Y > DockRootBorder.ActualHeight;
        if (outside)
        {
            HideGridSnapHighlight();
            HideMiniDesktopDragGrid();
        }
    }

    private void OnItemsAreaDrop(object sender, DragEventArgs e)
    {
        ClearDropInsertIndicator();
        HideGridSnapHighlight();
        HideMiniDesktopDragGrid();

        if (MiniDesktopMode)
        {
            (int dropCol, int dropRow) = ResolveDropGridCell(e);
            if (e.Data.GetDataPresent(DragItemFormat) &&
                e.Data.GetData(DragItemFormat) is DockItem gridDragged)
            {
                PlaceOrSwapItemAtGridCell(gridDragged, dropCol, dropRow);
                e.Handled = true;
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                HandleExternalFileDrop(e, dropCol, dropRow);
                e.Handled = true;
            }
            return;
        }

        if (e.Data.GetDataPresent(DragItemFormat) &&
            e.Data.GetData(DragItemFormat) is DockItem draggedItem)
        {
            var items = _configService.Config.Items;
            int oldIndex = items.IndexOf(draggedItem);
            int lastIndex = items.Count - 1;
            if (oldIndex >= 0 && lastIndex >= 0 && oldIndex != lastIndex)
            {
                items.Move(oldIndex, lastIndex);
                _ = _configService.SaveAsync();
            }

            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            HandleExternalFileDrop(e);
            e.Handled = true;
        }
    }

    private MiniDesktopGridMetrics ComputeMiniDesktopGridMetrics()
    {
        GetMonitorWorkAreaMetrics(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y, out _, out double workW, out double workH, out _);
        double defaultMiniW = Math.Round(workW * 0.5);
        double defaultMiniH = Math.Round(workH * 0.5);

        double fallbackW = _configService.Config.Settings.MiniDesktopMode
            ? (_configService.Config.Settings.MiniDesktopWidth >= 300.0 ? _configService.Config.Settings.MiniDesktopWidth : defaultMiniW)
            : _configService.Config.Settings.DockWidth;
        double fallbackH = _configService.Config.Settings.MiniDesktopMode
            ? (_configService.Config.Settings.MiniDesktopHeight >= 220.0 ? _configService.Config.Settings.MiniDesktopHeight : defaultMiniH)
            : _configService.Config.Settings.DockHeight;

        double dockW = double.IsNaN(DockRootBorder.Width) || DockRootBorder.Width <= 0
            ? fallbackW
            : DockRootBorder.Width;
        double dockH = double.IsNaN(DockRootBorder.Height) || DockRootBorder.Height <= 0
            ? fallbackH
            : DockRootBorder.Height;

        double contentW = Math.Max(TileWidth, dockW - 28.0);
        bool searchBarTakingSpace = _isSearchOpen || SearchBarContainer?.Visibility == Visibility.Visible;
        double searchBarHeight = searchBarTakingSpace ? 46.0 : 0.0;
        double contentH = Math.Max(TileHeight, dockH - 28.0 - searchBarHeight);

        double cellStepX = Math.Max(32.0, TileWidth + MiniDesktopCellGapX);
        double cellStepY = Math.Max(32.0, TileHeight + MiniDesktopCellGapY);
        int cols = Math.Max(1, (int)Math.Floor((contentW + MiniDesktopCellGapX + 0.5) / cellStepX));
        int rows = Math.Max(1, (int)Math.Floor((contentH + MiniDesktopCellGapY + 0.5) / cellStepY));
        double usedW = (cols * TileWidth) + ((cols - 1) * MiniDesktopCellGapX);
        double usedH = (rows * TileHeight) + ((rows - 1) * MiniDesktopCellGapY);
        double originX = Math.Max(0.0, Math.Floor((contentW - usedW) * 0.5));
        double originY = Math.Max(0.0, Math.Floor((contentH - usedH) * 0.5));

        return new MiniDesktopGridMetrics(cols, rows, contentW, contentH, originX, originY, cellStepX, cellStepY);
    }

    private bool IsCornerRecycleBinReserved(MiniDesktopGridMetrics metrics) =>
        ShowCornerRecycleBin && metrics.Columns * metrics.Rows > 1;

    public void SyncMiniDesktopGridLayout(bool persistIfAssigned = true, bool forceReflow = false)
    {
        MiniDesktopGridMetrics metrics = ComputeMiniDesktopGridMetrics();
        _cachedMiniDesktopVisibleRows = metrics.Rows;

        bool columnsChanged = _lastMiniDesktopColumns > 0 && _lastMiniDesktopColumns != metrics.Columns;
        bool rowsChanged = _lastMiniDesktopRows > 0 && _lastMiniDesktopRows != metrics.Rows;
        bool sizeChanged = _lastMiniDesktopWidth > 0 && (Math.Abs(_lastMiniDesktopWidth - metrics.ContentWidth) > 1.0 || Math.Abs(_lastMiniDesktopHeight - metrics.ContentViewportHeight) > 1.0);

        int currentMaxCol = -1;
        int currentMaxRow = -1;
        int itemsWithValidGrid = 0;
        foreach (DockItem item in _configService.Config.Items)
        {
            if (item.GridColumn is int c && item.GridRow is int r && c >= 0 && r >= 0)
            {
                itemsWithValidGrid++;
                if (c > currentMaxCol) currentMaxCol = c;
                if (r > currentMaxRow) currentMaxRow = r;
            }
        }

        bool isCompressedHorizontally = itemsWithValidGrid > 0 &&
                                       currentMaxCol < (metrics.Columns - 1) &&
                                       currentMaxRow >= metrics.Rows;

        bool shouldReflow = !_isSearchOpen && (forceReflow || columnsChanged || rowsChanged || sizeChanged || isCompressedHorizontally);
        _lastMiniDesktopColumns = metrics.Columns;
        _lastMiniDesktopRows = metrics.Rows;
        _lastMiniDesktopWidth = metrics.ContentWidth;
        _lastMiniDesktopHeight = metrics.ContentViewportHeight;

        var occupied = new HashSet<(int Col, int Row)>();
        var needsVisualPlacement = new List<(DockItem Item, bool PersistAssignment)>();
        bool canPersistOutOfBounds = !_isSearchOpen;

        bool reserveCornerCell = IsCornerRecycleBinReserved(metrics);
        if (reserveCornerCell)
        {
            occupied.Add((metrics.Columns - 1, metrics.Rows - 1));
        }

        UpdateRecycleBinCornerMiniPosition(metrics);

        DockItem? settingsItem = _configService.Config.Items.FirstOrDefault(i => string.Equals(i.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase));
        bool placeSettingsAtBottom = false;

        if (shouldReflow)
        {
            foreach (DockItem item in _configService.Config.Items)
            {
                item.GridColumn = null;
                item.GridRow = null;
                if (ReferenceEquals(item, settingsItem))
                {
                    placeSettingsAtBottom = true;
                }
                else
                {
                    needsVisualPlacement.Add((item, true));
                }
            }
        }
        else
        {
            foreach (DockItem item in _configService.Config.Items)
            {
                if (item.GridColumn is int col &&
                    item.GridRow is int row &&
                    col >= 0 &&
                    row >= 0)
                {
                    if (col < metrics.Columns && (!reserveCornerCell || col != metrics.Columns - 1 || row != metrics.Rows - 1) && occupied.Add((col, row)))
                    {
                        item.CanvasX = metrics.OriginX + (col * metrics.CellStepX);
                        item.CanvasY = metrics.OriginY + (row * metrics.CellStepY);
                    }
                    else
                    {
                        bool inCurrentGrid = col < metrics.Columns;
                        if (ReferenceEquals(item, settingsItem))
                        {
                            placeSettingsAtBottom = true;
                        }
                        else
                        {
                            needsVisualPlacement.Add((item, canPersistOutOfBounds || inCurrentGrid));
                        }
                    }
                }
                else
                {
                    if (ReferenceEquals(item, settingsItem))
                    {
                        placeSettingsAtBottom = true;
                    }
                    else
                    {
                        needsVisualPlacement.Add((item, true));
                    }
                }
            }
        }

        int nextScanIndex = 0;
        bool anyPersisted = shouldReflow;
        foreach ((DockItem item, bool persistAssignment) in needsVisualPlacement)
        {
            while (true)
            {
                int col = nextScanIndex % metrics.Columns;
                int row = nextScanIndex / metrics.Columns;
                nextScanIndex++;

                if (reserveCornerCell && col == metrics.Columns - 1 && row == metrics.Rows - 1)
                {
                    continue;
                }

                if (!occupied.Add((col, row)))
                {
                    continue;
                }

                item.CanvasX = metrics.OriginX + (col * metrics.CellStepX);
                item.CanvasY = metrics.OriginY + (row * metrics.CellStepY);

                if (persistAssignment)
                {
                    item.GridColumn = col;
                    item.GridRow = row;
                    anyPersisted = true;
                }

                break;
            }
        }

        // Settings icon: by default placed at the very bottom row (or last available cell)
        if (placeSettingsAtBottom && settingsItem is not null)
        {
            int targetRow = Math.Max(0, metrics.Rows - 1);
            int assignedCol = -1;
            int assignedRow = -1;

            // Search bottom row from right to left
            for (int col = metrics.Columns - 1; col >= 0; col--)
            {
                if (reserveCornerCell && col == metrics.Columns - 1 && targetRow == metrics.Rows - 1)
                {
                    continue;
                }

                if (occupied.Add((col, targetRow)))
                {
                    assignedCol = col;
                    assignedRow = targetRow;
                    break;
                }
            }

            // If bottom row was fully occupied, find the next available cell
            if (assignedCol < 0)
            {
                while (true)
                {
                    int col = nextScanIndex % metrics.Columns;
                    int row = nextScanIndex / metrics.Columns;
                    nextScanIndex++;

                    if (reserveCornerCell && col == metrics.Columns - 1 && row == metrics.Rows - 1)
                    {
                        continue;
                    }

                    if (occupied.Add((col, row)))
                    {
                        assignedCol = col;
                        assignedRow = row;
                        break;
                    }
                }
            }

            settingsItem.CanvasX = metrics.OriginX + (assignedCol * metrics.CellStepX);
            settingsItem.CanvasY = metrics.OriginY + (assignedRow * metrics.CellStepY);
            settingsItem.GridColumn = assignedCol;
            settingsItem.GridRow = assignedRow;
            anyPersisted = true;
        }

        int maxAssignedRow = occupied.Count > 0 ? occupied.Max(p => p.Row) : 0;
        int totalGridRows = Math.Max(metrics.Rows, maxAssignedRow + 1);
        _cachedMiniDesktopTotalRows = totalGridRows;

        MiniDesktopGridWidth = metrics.ContentWidth;

        if (totalGridRows <= metrics.Rows)
        {
            MiniDesktopGridHeight = Math.Max(0.0, metrics.ContentViewportHeight - 2.0);
            ItemsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            ItemsScrollViewer.ScrollToVerticalOffset(0);
        }
        else
        {
            double totalContentHeight = metrics.OriginY + (totalGridRows * metrics.CellStepY) - MiniDesktopCellGapY + metrics.OriginY;
            MiniDesktopGridHeight = Math.Max(metrics.ContentViewportHeight, totalContentHeight);
            ItemsScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        }

        if (_isMiniDesktopDragGridVisible)
        {
            EnsureMiniDesktopGridLines(metrics, totalGridRows);
        }

        if (persistIfAssigned && anyPersisted)
        {
            _ = _configService.SaveAsync();
        }
    }

    private void UpdateRecycleBinCornerMiniPosition(MiniDesktopGridMetrics metrics)
    {
        if (RecycleBinCornerButton is null)
        {
            return;
        }

        if (!MiniDesktopMode)
        {
            RecycleBinCornerButton.Margin = new Thickness(0, 4, 0, 0);
            return;
        }

        double cellRight = metrics.OriginX + ((metrics.Columns - 1) * metrics.CellStepX) + TileWidth;
        double cellBottom = metrics.OriginY + ((metrics.Rows - 1) * metrics.CellStepY) + TileHeight;
        double rightGap = Math.Max(0.0, metrics.ContentWidth - cellRight);
        double bottomGap = Math.Max(0.0, metrics.ContentViewportHeight - cellBottom);

        double rightMargin = rightGap + Math.Max(0.0, (TileWidth - 38.0) * 0.5);
        double bottomMargin = bottomGap + Math.Max(0.0, (TileHeight - 38.0) * 0.5);
        RecycleBinCornerButton.Margin = new Thickness(0, 0, rightMargin, bottomMargin);
    }

    private (int Column, int Row) ResolveDropGridCell(DragEventArgs e)
    {
        MiniDesktopGridMetrics metrics = ComputeMiniDesktopGridMetrics();
        Point pt = e.GetPosition(GridOverlayCanvas);
        return ResolveMiniDesktopCell(pt, metrics);
    }

    private (int Column, int Row) ResolveMiniDesktopCell(Point pointInGridCanvas, MiniDesktopGridMetrics metrics)
    {
        int col = Math.Clamp(
            (int)Math.Floor((pointInGridCanvas.X - metrics.OriginX + (MiniDesktopCellGapX * 0.5)) / metrics.CellStepX),
            0,
            metrics.Columns - 1);
        int maxRow = Math.Max(metrics.Rows, _cachedMiniDesktopTotalRows);
        int rawRow = (int)Math.Floor((pointInGridCanvas.Y - metrics.OriginY + (MiniDesktopCellGapY * 0.5)) / metrics.CellStepY);
        int row = Math.Clamp(rawRow, 0, maxRow);

        if (IsCornerRecycleBinReserved(metrics) &&
            col == metrics.Columns - 1 &&
            row == metrics.Rows - 1)
        {
            col = metrics.Columns > 1 ? metrics.Columns - 2 : 0;
        }

        return (col, row);
    }

    public void ShowMiniDesktopDragGrid()
    {
        MiniDesktopGridMetrics metrics = ComputeMiniDesktopGridMetrics();
        EnsureMiniDesktopGridLines(metrics, Math.Max(metrics.Rows, _cachedMiniDesktopTotalRows));

        if (_isMiniDesktopDragGridVisible)
        {
            return;
        }

        _isMiniDesktopDragGridVisible = true;
        var anim = new DoubleAnimation(MiniDesktopGridLinesHost.Opacity, 1.0, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        MiniDesktopGridLinesHost.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    public void HideMiniDesktopDragGrid(bool immediate = false)
    {
        if (MiniDesktopGridLinesHost is null)
        {
            return;
        }

        _isMiniDesktopDragGridVisible = false;
        if (immediate)
        {
            MiniDesktopGridLinesHost.BeginAnimation(OpacityProperty, null);
            MiniDesktopGridLinesHost.Opacity = 0.0;
            MiniDesktopGridLinesHost.Children.Clear();
            _cachedGridCols = 0;
            return;
        }

        var anim = new DoubleAnimation(MiniDesktopGridLinesHost.Opacity, 0.0, TimeSpan.FromMilliseconds(100))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        anim.Completed += (_, _) =>
        {
            if (!_isMiniDesktopDragGridVisible && MiniDesktopGridLinesHost is not null)
            {
                MiniDesktopGridLinesHost.Children.Clear();
                _cachedGridCols = 0;
            }
        };
        MiniDesktopGridLinesHost.BeginAnimation(OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void EnsureMiniDesktopGridLines(MiniDesktopGridMetrics metrics)
    {
        EnsureMiniDesktopGridLines(metrics, Math.Max(metrics.Rows, _cachedMiniDesktopTotalRows));
    }

    private void EnsureMiniDesktopGridLines(MiniDesktopGridMetrics metrics, int totalRows)
    {
        bool reserveCornerCell = IsCornerRecycleBinReserved(metrics);
        int expectedOutlineCount = Math.Max(0, (metrics.Columns * totalRows) - (reserveCornerCell ? 1 : 0));
        if (_cachedGridCols == metrics.Columns &&
            _cachedGridRows == totalRows &&
            Math.Abs(_cachedGridOriginX - metrics.OriginX) < 0.1 &&
            Math.Abs(_cachedGridOriginY - metrics.OriginY) < 0.1 &&
            Math.Abs(_cachedGridTileW - TileWidth) < 0.1 &&
            Math.Abs(_cachedGridTileH - TileHeight) < 0.1 &&
            MiniDesktopGridLinesHost.Children.Count == expectedOutlineCount)
        {
            return;
        }

        _cachedGridCols = metrics.Columns;
        _cachedGridRows = totalRows;
        _cachedGridOriginX = metrics.OriginX;
        _cachedGridOriginY = metrics.OriginY;
        _cachedGridTileW = TileWidth;
        _cachedGridTileH = TileHeight;

        double totalGridHeight = Math.Max(
            metrics.ContentViewportHeight,
            metrics.OriginY + ((totalRows - 1) * metrics.CellStepY) + TileHeight + 14.0);

        MiniDesktopGridLinesHost.Width = metrics.ContentWidth;
        MiniDesktopGridLinesHost.Height = totalGridHeight;
        MiniDesktopGridLinesHost.Children.Clear();

        for (int r = 0; r < totalRows; r++)
        {
            double top = metrics.OriginY + (r * metrics.CellStepY);
            for (int c = 0; c < metrics.Columns; c++)
            {
                if (reserveCornerCell && r == metrics.Rows - 1 && c == metrics.Columns - 1)
                {
                    continue;
                }

                var cellOutline = new Border
                {
                    Width = TileWidth,
                    Height = TileHeight,
                    CornerRadius = new CornerRadius(12),
                    BorderBrush = MiniDesktopGridCellBorderBrush,
                    Background = MiniDesktopGridCellFillBrush,
                    BorderThickness = new Thickness(1),
                    SnapsToDevicePixels = true,
                    IsHitTestVisible = false
                };
                System.Windows.Controls.Canvas.SetLeft(cellOutline, metrics.OriginX + (c * metrics.CellStepX));
                System.Windows.Controls.Canvas.SetTop(cellOutline, top);
                MiniDesktopGridLinesHost.Children.Add(cellOutline);
            }
        }
    }

    private void UpdateMiniDesktopSnapHighlight(DragEventArgs e)
    {
        MiniDesktopGridMetrics metrics = ComputeMiniDesktopGridMetrics();
        (int col, int row) = ResolveMiniDesktopCell(e.GetPosition(GridOverlayCanvas), metrics);

        GridSnapHighlightBorder.Width = TileWidth;
        GridSnapHighlightBorder.Height = TileHeight;
        System.Windows.Controls.Canvas.SetLeft(GridSnapHighlightBorder, metrics.OriginX + (col * metrics.CellStepX));
        System.Windows.Controls.Canvas.SetTop(GridSnapHighlightBorder, metrics.OriginY + (row * metrics.CellStepY));
        GridSnapHighlightBorder.Visibility = Visibility.Visible;
    }

    private void HideGridSnapHighlight()
    {
        if (GridSnapHighlightBorder is not null)
        {
            GridSnapHighlightBorder.Visibility = Visibility.Collapsed;
        }
    }

    public bool PlaceOrSwapItemAtGridCell(DockItem draggedItem, int targetCol, int targetRow)
    {
        if (!_configService.Config.Items.Contains(draggedItem))
        {
            return false;
        }

        MiniDesktopGridMetrics metrics = ComputeMiniDesktopGridMetrics();
        int maxRow = Math.Max(metrics.Rows, _cachedMiniDesktopTotalRows);
        targetCol = Math.Clamp(targetCol, 0, metrics.Columns - 1);
        targetRow = Math.Clamp(targetRow, 0, maxRow);
        if (IsCornerRecycleBinReserved(metrics) &&
            targetCol == metrics.Columns - 1 &&
            targetRow == metrics.Rows - 1)
        {
            targetCol = metrics.Columns > 1 ? metrics.Columns - 2 : 0;
        }

        int? oldCol = draggedItem.GridColumn;
        int? oldRow = draggedItem.GridRow;
        if (oldCol == targetCol && oldRow == targetRow)
        {
            return false;
        }

        DockItem? occupant = _configService.Config.Items.FirstOrDefault(item =>
            !ReferenceEquals(item, draggedItem) &&
            item.GridColumn == targetCol &&
            item.GridRow == targetRow);

        draggedItem.GridColumn = targetCol;
        draggedItem.GridRow = targetRow;

        if (occupant is not null)
        {
            if (oldCol.HasValue && oldRow.HasValue &&
                oldCol.Value >= 0 && oldCol.Value < metrics.Columns &&
                oldRow.Value >= 0 && oldRow.Value <= maxRow)
            {
                occupant.GridColumn = oldCol;
                occupant.GridRow = oldRow;
            }
            else
            {
                occupant.GridColumn = null;
                occupant.GridRow = null;
            }
        }

        SyncMiniDesktopGridLayout(persistIfAssigned: false);
        _ = _configService.SaveAsync();
        ScheduleEdgeEffectsRefresh();
        return true;
    }

    public bool ReorderItemRelativeTo(DockItem draggedItem, DockItem targetItem, bool insertAfter)
    {
        var items = _configService.Config.Items;
        int oldIndex = items.IndexOf(draggedItem);
        int targetIndex = items.IndexOf(targetItem);

        if (oldIndex < 0 || targetIndex < 0 || ReferenceEquals(draggedItem, targetItem))
        {
            return false;
        }

        int insertIndex = insertAfter ? targetIndex + 1 : targetIndex;
        if (oldIndex < insertIndex)
        {
            insertIndex--;
        }

        insertIndex = Math.Clamp(insertIndex, 0, items.Count - 1);
        if (oldIndex == insertIndex)
        {
            return false;
        }

        items.Move(oldIndex, insertIndex);
        _ = _configService.SaveAsync();
        return true;
    }

    private void SetDropInsertIndicator(DockItem targetItem, bool insertAfter)
    {
        if (_activeDropTarget is not null && !ReferenceEquals(_activeDropTarget, targetItem))
        {
            _activeDropTarget.IsDropBefore = false;
            _activeDropTarget.IsDropAfter = false;
        }

        _activeDropTarget = targetItem;
        _activeDropTarget.IsDropBefore = !insertAfter;
        _activeDropTarget.IsDropAfter = insertAfter;
    }

    private void ClearDropInsertIndicator()
    {
        if (_activeDropTarget is not null)
        {
            _activeDropTarget.IsDropBefore = false;
            _activeDropTarget.IsDropAfter = false;
            _activeDropTarget = null;
        }
    }

    private void AutoScrollItemsViewer(DragEventArgs e)
    {
        Point viewerPos = e.GetPosition(ItemsScrollViewer);
        const double scrollEdgeDip = 26.0;
        const double scrollStepDip = 14.0;

        if (viewerPos.Y < scrollEdgeDip && ItemsScrollViewer.VerticalOffset > 0)
        {
            ItemsScrollViewer.ScrollToVerticalOffset(Math.Max(0, ItemsScrollViewer.VerticalOffset - scrollStepDip));
        }
        else if (viewerPos.Y > ItemsScrollViewer.ActualHeight - scrollEdgeDip &&
                 ItemsScrollViewer.VerticalOffset < ItemsScrollViewer.ScrollableHeight)
        {
            ItemsScrollViewer.ScrollToVerticalOffset(
                Math.Min(ItemsScrollViewer.ScrollableHeight, ItemsScrollViewer.VerticalOffset + scrollStepDip));
        }
    }

    // --- Tile Click & Context Menu Handlers ---

    private void OnDockItemClick(object sender, RoutedEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: DockItem item })
        {
            if (string.Equals(item.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
            {
                HideDock();
                TrayIconManager.LaunchSettingsApp();
                return;
            }

            bool runAsAdmin = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift);
            LaunchItem(item, runAsAdmin);
        }
    }

    private void OnDockItemPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: DockItem item })
        {
            e.Handled = true;
            if (string.Equals(item.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
            {
                HideDock();
                TrayIconManager.LaunchSettingsApp();
                return;
            }
            OpenFileLocation(item);
        }
    }

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        if (TryGetItemFromMenuItem(sender, out DockItem? item) && item is not null)
        {
            LaunchItem(item, runAsAdmin: true);
        }
    }

    private void OnOpenFileLocationClick(object sender, RoutedEventArgs e)
    {
        if (TryGetItemFromMenuItem(sender, out DockItem? item) && item is not null)
        {
            OpenFileLocation(item);
        }
    }

    private void OpenFileLocation(DockItem item)
    {
        CloseActiveContextMenu();
        HideDock();

        try
        {
            string targetToReveal = item.Path;
            if (string.Equals(Path.GetExtension(item.Path), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string? resolved = NativeMethods.ResolveShortcutTarget(item.Path);
                if (!string.IsNullOrWhiteSpace(resolved) && (File.Exists(resolved) || Directory.Exists(resolved)))
                {
                    targetToReveal = resolved;
                }
            }

            if (File.Exists(targetToReveal) || Directory.Exists(targetToReveal))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{targetToReveal}\"")
                {
                    UseShellExecute = true
                });
            }
            else if (Directory.Exists(_configService.ShortcutsDirectory))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_configService.ShortcutsDirectory}\"")
                {
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Open File Location failed for '{item.Path}': {ex.Message}");
        }
    }

    private void OnDockItemMouseEnter(object sender, MouseEventArgs e)
    {
        ClearKeyboardSelection();
    }

    private void OnRenameItemClick(object sender, RoutedEventArgs e)
    {
        if (TryGetItemFromMenuItem(sender, out DockItem? item) && item is not null)
        {
            BeginRenameItem(item);
        }
    }

    public void BeginRenameItem(DockItem item)
    {
        CloseActiveContextMenu();
        _renameTargetItem = item;
        RenameTextBox.Text = item.Name;
        RenameOverlayHost.Visibility = Visibility.Visible;
        RenameTextBox.Focus();
        Keyboard.Focus(RenameTextBox);
        RenameTextBox.SelectAll();
    }

    public bool RenameDockItem(DockItem item, string newName)
    {
        string trimmed = (newName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, item.Name, StringComparison.Ordinal))
        {
            return false;
        }

        string safeFileStem = SanitizeFileName(trimmed);
        if (string.IsNullOrWhiteSpace(safeFileStem))
        {
            safeFileStem = trimmed;
        }

        try
        {
            string fullShortcuts = Path.GetFullPath(_configService.ShortcutsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullItem = Path.GetFullPath(item.Path);

            if (fullItem.StartsWith(fullShortcuts, StringComparison.OrdinalIgnoreCase))
            {
                bool isDir = Directory.Exists(fullItem);
                if (isDir)
                {
                    string targetDir = Path.Combine(Path.GetDirectoryName(fullItem)!, safeFileStem);
                    if (!string.Equals(fullItem, targetDir, StringComparison.OrdinalIgnoreCase))
                    {
                        targetDir = BuildUniqueRenamePath(Path.GetDirectoryName(fullItem)!, safeFileStem, string.Empty);
                        Directory.Move(fullItem, targetDir);
                        _iconExtractor.Invalidate(fullItem);
                        item.Path = targetDir;
                    }
                }
                else if (File.Exists(fullItem))
                {
                    string ext = Path.GetExtension(fullItem);
                    string targetFile = Path.Combine(Path.GetDirectoryName(fullItem)!, safeFileStem + ext);
                    if (!string.Equals(fullItem, targetFile, StringComparison.OrdinalIgnoreCase))
                    {
                        targetFile = BuildUniqueRenamePath(Path.GetDirectoryName(fullItem)!, safeFileStem, ext);
                        File.Move(fullItem, targetFile);
                        _iconExtractor.Invalidate(fullItem);
                        item.Path = targetFile;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Disk rename skipped for '{item.Path}': {ex.Message}");
        }

        item.Name = trimmed;
        item.Icon ??= _iconExtractor.GetIcon(string.IsNullOrWhiteSpace(item.IconPath) ? item.Path : item.IconPath!);
        _ = _configService.SaveAsync();
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(ch => !invalid.Contains(ch)).ToArray()).Trim().TrimEnd('.', ' ');
    }

    private static string BuildUniqueRenamePath(string parentDir, string stem, string extension)
    {
        string candidate = Path.Combine(parentDir, stem + extension);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        int counter = 2;
        while (true)
        {
            candidate = Path.Combine(parentDir, $"{stem} ({counter}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
            counter++;
        }
    }

    private void CommitRenameOverlay()
    {
        if (_renameTargetItem is DockItem item)
        {
            RenameDockItem(item, RenameTextBox.Text);
        }

        CloseRenameOverlay();
    }

    private void CloseRenameOverlay(bool focusDock = true)
    {
        _renameTargetItem = null;
        if (RenameOverlayHost is not null)
        {
            RenameOverlayHost.Visibility = Visibility.Collapsed;
        }

        if (focusDock && IsVisible)
        {
            this.Focus();
            Keyboard.Focus(this);
        }
    }

    private void OnRenameSaveClick(object sender, RoutedEventArgs e) => CommitRenameOverlay();

    private void OnRenameCancelClick(object sender, RoutedEventArgs e) => CloseRenameOverlay();

    private void OnRenameScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        CloseRenameOverlay();
        e.Handled = true;
    }

    private void OnRenameCardMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnRenameTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRenameOverlay();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseRenameOverlay();
            e.Handled = true;
        }
    }

    private void OnSendToRecycleBinClick(object sender, RoutedEventArgs e)
    {
        if (TryGetItemFromMenuItem(sender, out DockItem? item) && item is not null)
        {
            RecycleDockItem(item);
        }
    }

    public bool RecycleDockItem(DockItem item)
    {
        try
        {
            if (File.Exists(item.Path))
            {
                FileSystem.DeleteFile(
                    item.Path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
            else if (Directory.Exists(item.Path))
            {
                FileSystem.DeleteDirectory(
                    item.Path,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin);
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to send '{item.Path}' to Recycle Bin: {ex.Message}");
            return false;
        }

        _iconExtractor.Invalidate(item.Path);
        _configService.Config.Items.Remove(item);
        _ = _configService.SaveAsync();
        UpdateRecycleBinCornerIcon();
        return true;
    }

    public void UpdateRecycleBinCornerIcon()
    {
        if (RecycleBinCornerImage is null)
        {
            return;
        }

        bool isFull = NativeMethods.QueryRecycleBin(out long numItems, out _) && numItems > 0;
        RecycleBinCornerImage.Source = _iconExtractor.GetRecycleBinIcon(isFull);
    }

    private void OnRecycleBinCornerClick(object sender, RoutedEventArgs e)
    {
        if (_isDragging)
        {
            return;
        }

        OpenRecycleBinFolder();
    }

    private void OnOpenRecycleBinMenuClick(object sender, RoutedEventArgs e)
    {
        OpenRecycleBinFolder();
    }

    private void OnEmptyRecycleBinMenuClick(object sender, RoutedEventArgs e)
    {
        try
        {
            NativeMethods.SHEmptyRecycleBinW(IntPtr.Zero, null, 0);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to empty Recycle Bin: {ex.Message}");
        }
        finally
        {
            UpdateRecycleBinCornerIcon();
        }
    }

    private void OnDockAreaContextMenuOpened(object sender, RoutedEventArgs e)
    {
        ToggleMiniDesktopMenuItem.IsChecked = MiniDesktopMode;

        GetMonitorWorkAreaMetrics(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y, out _, out double workW, out double workH, out _);
        double defaultMiniW = Math.Round(workW * 0.5);
        double defaultMiniH = Math.Round(workH * 0.5);
        double largeMiniW = Math.Clamp(Math.Round(workW * 0.65), 800.0, workW - 48.0);
        double largeMiniH = Math.Clamp(Math.Round(workH * 0.65), 520.0, workH - 48.0);

        if (MiniDesktopMode)
        {
            MenuSizeSmall.Header = "Small (680×440)";
            MenuSizeDefault.Header = "Default (1/4 Screen)";
            MenuSizeLarge.Header = $"Large ({Math.Round(largeMiniW)}×{Math.Round(largeMiniH)})";

            double curW = _configService.Config.Settings.MiniDesktopWidth >= 300.0
                ? _configService.Config.Settings.MiniDesktopWidth
                : defaultMiniW;
            double curH = _configService.Config.Settings.MiniDesktopHeight >= 220.0
                ? _configService.Config.Settings.MiniDesktopHeight
                : defaultMiniH;

            MenuSizeSmall.IsChecked = Math.Abs(curW - 680.0) < 10.0 && Math.Abs(curH - 440.0) < 10.0;
            MenuSizeDefault.IsChecked = (_configService.Config.Settings.MiniDesktopWidth < 300.0 && _configService.Config.Settings.MiniDesktopHeight < 220.0) ||
                                        (Math.Abs(curW - defaultMiniW) < 10.0 && Math.Abs(curH - defaultMiniH) < 10.0);
            MenuSizeLarge.IsChecked = Math.Abs(curW - largeMiniW) < 10.0 && Math.Abs(curH - largeMiniH) < 10.0;
        }
        else
        {
            MenuSizeSmall.Header = "Small (440×320)";
            MenuSizeDefault.Header = "Default (520×360)";
            MenuSizeLarge.Header = "Large (680×440)";

            double curW = _configService.Config.Settings.DockWidth;
            double curH = _configService.Config.Settings.DockHeight;

            MenuSizeSmall.IsChecked = Math.Abs(curW - 440.0) < 5.0 && Math.Abs(curH - 320.0) < 5.0;
            MenuSizeDefault.IsChecked = Math.Abs(curW - 520.0) < 5.0 && Math.Abs(curH - 360.0) < 5.0;
            MenuSizeLarge.IsChecked = Math.Abs(curW - 680.0) < 5.0 && Math.Abs(curH - 440.0) < 5.0;
        }
    }

    private void OnSetSizePresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item)
        {
            return;
        }

        GetMonitorWorkAreaMetrics(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y, out _, out double workW, out double workH, out _);

        if (MiniDesktopMode)
        {
            if (ReferenceEquals(item, MenuSizeSmall))
            {
                _configService.Config.Settings.MiniDesktopWidth = 680.0;
                _configService.Config.Settings.MiniDesktopHeight = 440.0;
            }
            else if (ReferenceEquals(item, MenuSizeDefault))
            {
                _configService.Config.Settings.MiniDesktopWidth = 0.0;
                _configService.Config.Settings.MiniDesktopHeight = 0.0;
            }
            else if (ReferenceEquals(item, MenuSizeLarge))
            {
                _configService.Config.Settings.MiniDesktopWidth = Math.Clamp(Math.Round(workW * 0.65), 800.0, workW - 48.0);
                _configService.Config.Settings.MiniDesktopHeight = Math.Clamp(Math.Round(workH * 0.65), 520.0, workH - 48.0);
            }
        }
        else
        {
            if (ReferenceEquals(item, MenuSizeSmall))
            {
                _configService.Config.Settings.DockWidth = 440.0;
                _configService.Config.Settings.DockHeight = 320.0;
            }
            else if (ReferenceEquals(item, MenuSizeDefault))
            {
                _configService.Config.Settings.DockWidth = 520.0;
                _configService.Config.Settings.DockHeight = 360.0;
            }
            else if (ReferenceEquals(item, MenuSizeLarge))
            {
                _configService.Config.Settings.DockWidth = 680.0;
                _configService.Config.Settings.DockHeight = 440.0;
            }
        }

        _ = _configService.SaveAsync();
        ApplyVisualSettings(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y);
        if (MiniDesktopMode)
        {
            SyncMiniDesktopGridLayout(persistIfAssigned: true, forceReflow: true);
        }
        RepositionAndResizeIfVisible();
    }

    private void OnSortAlphabeticalClick(object sender, RoutedEventArgs e) => SortItemsAlphabetically();

    public void SortItemsAlphabetically()
    {
        var settingsItem = _configService.Config.Items.FirstOrDefault(i => string.Equals(i.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase));
        var normalItems = _configService.Config.Items.Where(i => !string.Equals(i.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase));

        var sorted = normalItems
            .OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (settingsItem is not null)
        {
            sorted.Add(settingsItem);
        }

        for (int targetIndex = 0; targetIndex < sorted.Count; targetIndex++)
        {
            int currentIndex = _configService.Config.Items.IndexOf(sorted[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                _configService.Config.Items.Move(currentIndex, targetIndex);
            }
        }

        if (MiniDesktopMode)
        {
            foreach (DockItem item in _configService.Config.Items)
            {
                item.GridColumn = null;
                item.GridRow = null;
            }
            SyncMiniDesktopGridLayout(persistIfAssigned: true);
        }

        _ = _configService.SaveAsync();
        ScheduleEdgeEffectsRefresh();
    }

    private void OnToggleMiniDesktopMenuClick(object sender, RoutedEventArgs e)
    {
        bool next = !_configService.Config.Settings.MiniDesktopMode;
        _configService.Config.Settings.MiniDesktopMode = next;
        _ = _configService.SaveAsync();
        ApplyVisualSettings(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y);
        if (MiniDesktopMode)
        {
            SyncMiniDesktopGridLayout(persistIfAssigned: true, forceReflow: true);
        }
        RepositionAndResizeIfVisible();
    }

    private void OnDockItemContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && menu.DataContext is DockItem item)
        {
            bool isSettings = string.Equals(item.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase);
            if (menu.FindName("ContextRunAsAdmin") is MenuItem miAdmin) miAdmin.Visibility = isSettings ? Visibility.Collapsed : (item.IsDirectory ? Visibility.Collapsed : Visibility.Visible);
            if (menu.FindName("ContextOpenFileLoc") is MenuItem miLoc) miLoc.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;
            if (menu.FindName("ContextRename") is MenuItem miRename) miRename.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;
            if (menu.FindName("ContextSendToRb") is MenuItem miRb) miRb.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;
            if (menu.FindName("ContextSep") is Separator sep) sep.Visibility = isSettings ? Visibility.Collapsed : Visibility.Visible;
            if (menu.FindName("ContextOpenSettings") is MenuItem miSet) miSet.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
            if (menu.FindName("ContextHideSettings") is MenuItem miHide) miHide.Visibility = isSettings ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnHideSettingsFromDockClick(object sender, RoutedEventArgs e)
    {
        _configService.Config.Settings.ShowSettingsButton = false;
        _ = _configService.SaveAsync();
        ShowSettingsButton = false;
    }

    private void OnOpenSettingsMenuClick(object sender, RoutedEventArgs e)
    {
        HideDock();
        TrayIconManager.LaunchSettingsApp();
    }

    private void OnDockResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        GetMonitorWorkAreaMetrics(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y, out _, out double workW, out double workH, out _);
        double minW = MiniDesktopMode ? 300.0 : 360.0;
        double minH = MiniDesktopMode ? 220.0 : 260.0;
        double maxW = Math.Max(minW, workW - (OuterShadowMarginDip * 2));
        double maxH = Math.Max(minH, workH - (OuterShadowMarginDip * 2));

        double nextWidth = Math.Clamp(DockRootBorder.Width + e.HorizontalChange, minW, maxW);
        double nextHeight = Math.Clamp(DockRootBorder.Height + e.VerticalChange, minH, maxH);
        DockRootBorder.Width = nextWidth;
        DockRootBorder.Height = nextHeight;

        if (MiniDesktopMode)
        {
            _configService.Config.Settings.MiniDesktopWidth = nextWidth;
            _configService.Config.Settings.MiniDesktopHeight = nextHeight;
            SyncMiniDesktopGridLayout(persistIfAssigned: false);
        }
        else
        {
            _configService.Config.Settings.DockWidth = nextWidth;
            _configService.Config.Settings.DockHeight = nextHeight;
        }

        ScheduleEdgeEffectsRefresh();
    }

    private void OnDockResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (MiniDesktopMode)
        {
            SyncMiniDesktopGridLayout(persistIfAssigned: true);
        }

        _ = _configService.SaveAsync();
        if (IsVisible)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            double scale = NativeMethods.GetDpiScaleForPoint(_lastSummonCursorPhys.X, _lastSummonCursorPhys.Y);
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            {
                _lastCapturePhysicalBounds = new ScreenBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
                _lastCardPhysicalBounds = ComputeVisibleCardRect(
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    scale);
                NotifyBoundsChanged(true, hwnd, _lastCardPhysicalBounds);
            }
        }
    }

    public void OpenRecycleBinFolder()
    {
        HideDock();

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open Recycle Bin: {ex.Message}");
        }
    }

    private void LaunchItem(DockItem item, bool runAsAdmin)
    {
        CloseActiveContextMenu();
        HideDock();

        if (string.Equals(item.Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
        {
            TrayIconManager.LaunchSettingsApp();
            return;
        }

        try
        {
            if (Directory.Exists(item.Path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Path}\"")
                {
                    UseShellExecute = true
                });
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = item.Path,
                Arguments = item.Arguments ?? string.Empty,
                UseShellExecute = true
            };

            if (!string.IsNullOrWhiteSpace(item.WorkingDirectory) && Directory.Exists(item.WorkingDirectory))
            {
                startInfo.WorkingDirectory = item.WorkingDirectory;
            }
            else
            {
                string? dir = Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    startInfo.WorkingDirectory = dir;
                }
            }

            if (runAsAdmin)
            {
                startInfo.Verb = "runas";
            }

            Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User canceled UAC elevation prompt.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch '{item.Path}': {ex.Message}");
        }
    }

    // --- External File Drag and Drop Support ---

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !e.Data.GetDataPresent(DragItemFormat) && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        HandleExternalFileDrop(e);
    }

    private void HandleExternalFileDrop(DragEventArgs e, int? preferredCol = null, int? preferredRow = null)
    {
        if (e.Data.GetDataPresent(DragItemFormat) ||
            !e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] droppedPaths ||
            droppedPaths.Length == 0)
        {
            return;
        }

        bool addedAny = false;
        bool usedPreferredCell = false;

        string userDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        bool relocateDesktopDrops = _configService.Config.Settings.AutoIngestDesktop;

        foreach (string path in droppedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            bool isDir = Directory.Exists(path);
            bool isFile = !isDir && File.Exists(path);
            if (!isDir && !isFile)
            {
                continue;
            }

            if (_configService.Config.Items.Any(i =>
                    string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(i.OriginalDesktopPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string itemPath = path;
            string? originalDesktopPath = null;
            string? parentDir = Path.GetDirectoryName(Path.GetFullPath(path));
            bool isFromDesktop = !string.IsNullOrWhiteSpace(parentDir) &&
                                 (string.Equals(parentDir, userDesktopDir, StringComparison.OrdinalIgnoreCase) ||
                                  string.Equals(parentDir, commonDesktopDir, StringComparison.OrdinalIgnoreCase));

            if (relocateDesktopDrops && isFromDesktop)
            {
                try
                {
                    _configService.EnsureDirectories();
                    string stem = isDir
                        ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        : Path.GetFileNameWithoutExtension(path);
                    string ext = isDir ? string.Empty : Path.GetExtension(path);
                    string destPath = BuildUniqueRenamePath(_configService.ShortcutsDirectory, stem, ext);

                    if (isDir)
                    {
                        Directory.Move(path, destPath);
                    }
                    else
                    {
                        File.Move(path, destPath);
                    }

                    IntPtr itemPtr = Marshal.StringToHGlobalUni(path);
                    try
                    {
                        NativeMethods.SHChangeNotify(
                            isDir ? NativeMethods.SHCNE_RMDIR : NativeMethods.SHCNE_DELETE,
                            NativeMethods.SHCNF_PATHW | NativeMethods.SHCNF_FLUSHNOWAIT,
                            itemPtr,
                            IntPtr.Zero);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(itemPtr);
                    }

                    itemPath = destPath;
                    originalDesktopPath = path;
                }
                catch
                {
                    // Fall back to referencing original path if Windows denies moving a protected desktop entry.
                }
            }

            if (_configService.Config.Items.Any(i => string.Equals(i.Path, itemPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string name = isFile
                ? Path.GetFileNameWithoutExtension(itemPath)
                : Path.GetFileName(itemPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            if (string.IsNullOrWhiteSpace(name))
            {
                name = itemPath;
            }

            var item = new DockItem
            {
                Name = name,
                Path = itemPath,
                OriginalDesktopPath = originalDesktopPath,
                Icon = _iconExtractor.GetIcon(itemPath),
                AddedAtUtc = DateTime.UtcNow
            };

            if (MiniDesktopMode &&
                !usedPreferredCell &&
                preferredCol is int pCol &&
                preferredRow is int pRow &&
                !_configService.Config.Items.Any(existing => existing.GridColumn == pCol && existing.GridRow == pRow))
            {
                item.GridColumn = pCol;
                item.GridRow = pRow;
                usedPreferredCell = true;
            }

            int settingsIndex = -1;
            for (int si = 0; si < _configService.Config.Items.Count; si++)
            {
                if (string.Equals(_configService.Config.Items[si].Id, SpecialSettingsId, StringComparison.OrdinalIgnoreCase))
                {
                    settingsIndex = si;
                    break;
                }
            }

            if (settingsIndex >= 0 && settingsIndex == _configService.Config.Items.Count - 1)
            {
                _configService.Config.Items.Insert(settingsIndex, item);
            }
            else
            {
                _configService.Config.Items.Add(item);
            }
            addedAny = true;
        }

        if (addedAny)
        {
            _ = _configService.SaveAsync();
        }
    }

    // --- Search Bar Overlay & Keyboard Shortcut Filtering ---

    public bool IsSearchBarOpen => _isSearchOpen;

    public void OpenSearchBar()
    {
        if (!_configService.Config.Settings.EnableSearch)
        {
            if (_isSearchOpen)
            {
                CloseSearchBar(animate: false);
            }
            return;
        }

        int seq = ++_searchAnimSeq;
        _isSearchOpen = true;

        SearchBarContainer.BeginAnimation(OpacityProperty, null);
        SearchBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);

        SearchBarContainer.Visibility = Visibility.Visible;
        if (MiniDesktopMode)
        {
            SyncMiniDesktopGridLayout(persistIfAssigned: false);
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnim = new DoubleAnimation(SearchBarContainer.Opacity, 1.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = ease
        };
        var slideAnim = new DoubleAnimation(SearchBarTranslate.Y, 0.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = ease
        };
        _ = seq;

        SearchBarContainer.BeginAnimation(OpacityProperty, opacityAnim);
        SearchBarTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);

        UpdateSearchPlaceholderAndResultsVisibility();
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
        SearchTextBox.SelectAll();
    }

    public void CloseSearchBar(bool animate = true)
    {
        int seq = ++_searchAnimSeq;
        _isSearchOpen = false;
        _suppressNextTextInput = false;

        if (!string.IsNullOrEmpty(SearchTextBox.Text))
        {
            SearchTextBox.Text = string.Empty;
        }

        if (!animate || SearchBarContainer.Visibility != Visibility.Visible)
        {
            SearchBarContainer.BeginAnimation(OpacityProperty, null);
            SearchBarTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            SearchBarContainer.Opacity = 0.0;
            SearchBarTranslate.Y = -8.0;
            SearchBarContainer.Visibility = Visibility.Collapsed;
            ApplySearchFilter(string.Empty);
        }
        else
        {
            ApplySearchFilter(string.Empty);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var opacityAnim = new DoubleAnimation(SearchBarContainer.Opacity, 0.0, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = ease
            };
            var slideAnim = new DoubleAnimation(SearchBarTranslate.Y, -8.0, TimeSpan.FromMilliseconds(110))
            {
                EasingFunction = ease
            };
            opacityAnim.Completed += (_, _) =>
            {
                if (_searchAnimSeq == seq && !_isSearchOpen)
                {
                    SearchBarContainer.Visibility = Visibility.Collapsed;
                    if (MiniDesktopMode)
                    {
                        SyncMiniDesktopGridLayout(persistIfAssigned: false);
                    }
                }
            };

            SearchBarContainer.BeginAnimation(OpacityProperty, opacityAnim);
            SearchBarTranslate.BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        if (IsVisible)
        {
            this.Focus();
            Keyboard.Focus(this);
        }
    }

    private void OnSearchTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplySearchFilter(SearchTextBox.Text);
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Text = string.Empty;
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
    }

    private void OnSearchTextBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            if (MoveKeyboardSelection(e.Key))
            {
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (string.IsNullOrWhiteSpace(SearchTextBox.Text))
            {
                CloseSearchBar();
                e.Handled = true;
                return;
            }

            DockItem? match = (_keyboardSelectedItem is not null && IsItemVisible(_keyboardSelectedItem) ? _keyboardSelectedItem : null) ??
                              _autocompleteTargetItem ??
                              _itemsView?.Cast<object>().OfType<DockItem>().FirstOrDefault();
            if (match is not null)
            {
                bool runAsAdmin = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift);
                LaunchItem(match, runAsAdmin);
                e.Handled = true;
            }

            return;
        }

        if ((e.Key == Key.Tab ||
             (e.Key == Key.Right &&
              SearchTextBox.CaretIndex == SearchTextBox.Text.Length &&
              SearchTextBox.SelectionLength == 0)) &&
            _autocompleteTargetItem is not null &&
            !string.IsNullOrEmpty(AutocompleteSuffixRun.Text))
        {
            SearchTextBox.Text = AutocompleteTypedPrefixRun.Text + AutocompleteSuffixRun.Text;
            SearchTextBox.CaretIndex = SearchTextBox.Text.Length;
            e.Handled = true;
        }
    }

    private void ApplySearchFilter(string? query)
    {
        ClearKeyboardSelection();

        string raw = query ?? string.Empty;
        string trimmed = raw.Trim();

        if (_itemsView is not null)
        {
            _itemsView.Filter = string.IsNullOrEmpty(trimmed)
                ? null
                : obj => obj is DockItem item && MatchesSearchQuery(item, trimmed);
        }

        if (MiniDesktopMode)
        {
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                MiniDesktopGridMetrics metrics = ComputeMiniDesktopGridMetrics();
                var matches = _itemsView?.Cast<object>().OfType<DockItem>().ToList() ?? [];
                int totalSlots = Math.Max(1, metrics.Columns * metrics.Rows);
                for (int i = 0; i < matches.Count; i++)
                {
                    int slot = Math.Min(i, totalSlots - 1);
                    int col = slot % metrics.Columns;
                    int row = slot / metrics.Columns;
                    matches[i].CanvasX = metrics.OriginX + (col * metrics.CellStepX);
                    matches[i].CanvasY = metrics.OriginY + (row * metrics.CellStepY);
                }
            }
            else
            {
                SyncMiniDesktopGridLayout(persistIfAssigned: false);
            }
        }

        UpdateAutocompleteSuggestion(raw, trimmed);
        ResetScrollToTop();
        UpdateSearchPlaceholderAndResultsVisibility();
        ScheduleEdgeEffectsRefresh();
    }

    public DockItem? KeyboardSelectedItem => _keyboardSelectedItem;

    public void ClearKeyboardSelection()
    {
        if (_keyboardSelectedItem is not null)
        {
            _keyboardSelectedItem.IsKeyboardSelected = false;
            _keyboardSelectedItem = null;
        }
    }

    private bool IsItemVisible(DockItem item) =>
        _itemsView is null || _itemsView.Cast<object>().OfType<DockItem>().Contains(item);

    public bool MoveKeyboardSelection(Key direction)
    {
        if (direction is not (Key.Left or Key.Right or Key.Up or Key.Down))
        {
            return false;
        }

        var visible = _itemsView?.Cast<object>().OfType<DockItem>().ToList()
                      ?? _configService.Config.Items.ToList();
        if (visible.Count == 0)
        {
            ClearKeyboardSelection();
            return false;
        }

        DockItem nextItem;
        if (_keyboardSelectedItem is null || !visible.Contains(_keyboardSelectedItem))
        {
            if (MiniDesktopMode)
            {
                var ordered = visible.OrderBy(i => i.CanvasY).ThenBy(i => i.CanvasX);
                nextItem = direction is Key.Left or Key.Up ? ordered.Last() : ordered.First();
            }
            else
            {
                nextItem = visible[0];
            }
        }
        else if (MiniDesktopMode)
        {
            nextItem = FindSpatialNeighbor(_keyboardSelectedItem, visible, direction);
        }
        else
        {
            int idx = visible.IndexOf(_keyboardSelectedItem);
            int cols = ComputeWrapPanelColumnCount();
            int nextIdx = direction switch
            {
                Key.Right => Math.Min(visible.Count - 1, idx + 1),
                Key.Left => Math.Max(0, idx - 1),
                Key.Down => idx + cols < visible.Count ? idx + cols : visible.Count - 1,
                Key.Up => Math.Max(0, idx - cols),
                _ => idx
            };
            nextItem = visible[nextIdx];
        }

        if (_keyboardSelectedItem is not null)
        {
            _keyboardSelectedItem.IsKeyboardSelected = false;
        }

        _keyboardSelectedItem = nextItem;
        _keyboardSelectedItem.IsKeyboardSelected = true;

        if (!MiniDesktopMode &&
            DockItemsControl.ItemContainerGenerator.ContainerFromItem(nextItem) is FrameworkElement container)
        {
            container.BringIntoView();
            ScheduleEdgeEffectsRefresh();
        }

        return true;
    }

    private static DockItem FindSpatialNeighbor(DockItem current, List<DockItem> visible, Key direction)
    {
        DockItem? best = null;
        double bestScore = double.MaxValue;

        foreach (DockItem candidate in visible)
        {
            if (ReferenceEquals(candidate, current))
            {
                continue;
            }

            double dx = candidate.CanvasX - current.CanvasX;
            double dy = candidate.CanvasY - current.CanvasY;

            bool valid = direction switch
            {
                Key.Right => dx > 4.0,
                Key.Left => dx < -4.0,
                Key.Down => dy > 4.0,
                Key.Up => dy < -4.0,
                _ => false
            };
            if (!valid)
            {
                continue;
            }

            double primary = direction is Key.Left or Key.Right ? Math.Abs(dx) : Math.Abs(dy);
            double secondary = direction is Key.Left or Key.Right ? Math.Abs(dy) : Math.Abs(dx);
            double score = primary + (secondary * 2.5);
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best ?? current;
    }

    private int ComputeWrapPanelColumnCount()
    {
        double avail = ItemsScrollViewer.ViewportWidth > 0
            ? ItemsScrollViewer.ViewportWidth
            : Math.Max(TileWidth, DockRootBorder.Width - 28.0);
        double tileStride = TileWidth + 6.0; // Margin="3" each side on DockTileButtonStyle
        return Math.Max(1, (int)Math.Floor(avail / tileStride));
    }

    private void UpdateAutocompleteSuggestion(string rawText, string trimmedQuery)
    {
        _autocompleteTargetItem = null;
        AutocompleteTypedPrefixRun.Text = string.Empty;
        AutocompleteSuffixRun.Text = string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedQuery) ||
            string.IsNullOrEmpty(rawText) ||
            rawText.Contains('\n') ||
            _itemsView is null)
        {
            return;
        }

        DockItem? firstMatch = _itemsView
            .Cast<object>()
            .OfType<DockItem>()
            .FirstOrDefault();

        if (firstMatch is null)
        {
            return;
        }

        _autocompleteTargetItem = firstMatch;

        string candidate = !string.IsNullOrEmpty(firstMatch.Name) &&
                           firstMatch.Name.StartsWith(rawText, StringComparison.OrdinalIgnoreCase)
            ? firstMatch.Name
            : Path.GetFileNameWithoutExtension(firstMatch.Path);

        if (!string.IsNullOrEmpty(candidate) &&
            candidate.StartsWith(rawText, StringComparison.OrdinalIgnoreCase) &&
            rawText.Length < candidate.Length)
        {
            AutocompleteTypedPrefixRun.Text = rawText;
            AutocompleteSuffixRun.Text = candidate[rawText.Length..];
        }
    }

    private void UpdateSearchPlaceholderAndResultsVisibility()
    {
        bool hasText = !string.IsNullOrEmpty(SearchTextBox.Text);
        SearchPlaceholderText.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ClearSearchButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        EscHintBadge.Visibility = _isSearchOpen ? Visibility.Visible : Visibility.Collapsed;

        bool noMatches = _isSearchOpen &&
                         !string.IsNullOrWhiteSpace(SearchTextBox.Text) &&
                         _itemsView is not null &&
                         _itemsView.IsEmpty;
        NoSearchResultsText.Visibility = noMatches ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool MatchesSearchQuery(DockItem item, string query)
    {
        if (!string.IsNullOrEmpty(item.Name) &&
            item.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string fileStem = Path.GetFileNameWithoutExtension(item.Path);
        return !string.IsNullOrEmpty(fileStem) &&
               fileStem.StartsWith(query, StringComparison.OrdinalIgnoreCase);
    }

    // --- Window Lifecycle & Auto-Dismiss ---

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _dragCandidateItem = null;
        CloseActiveContextMenu();

        if (_isShowing || _isDragging || _isOpeningGenie || _isClosingGenie)
        {
            return;
        }

        // Suppress spurious deactivations during or right after dock summon/reveal (e.g. Genie handoff, OS focus arbitration)
        if ((Environment.TickCount64 - _lastRevealCompletedTimeMs) < 750)
        {
            return;
        }

        // Never hide while rename overlay is open.
        if (RenameOverlayHost.Visibility == Visibility.Visible)
        {
            return;
        }

        // Never hide if mouse is currently hovering over MainWindow.
        if (IsMouseOver)
        {
            return;
        }

        // Also check physical cursor position against native window bounds.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && NativeMethods.GetCursorPos(out NativeMethods.POINT pt))
        {
            if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT r))
            {
                if (pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom)
                {
                    return;
                }
            }
        }

        // If the newly activated foreground window belongs to CleanSlate, keep the dock open.
        IntPtr fgHwnd = NativeMethods.GetForegroundWindow();
        if (fgHwnd != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(fgHwnd, out uint fgPid);
            if (fgPid == (uint)Environment.ProcessId)
            {
                return;
            }
        }

        HideDock();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (RenameOverlayHost.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape)
            {
                CloseRenameOverlay();
                e.Handled = true;
            }
            return;
        }

        Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys mods = e.KeyboardDevice.Modifiers;
        bool searchEnabled = _configService.Config.Settings.EnableSearch;
        string configuredShortcut = _configService.Config.Settings.SearchShortcut;

        if (_isSearchOpen)
        {
            if (actualKey == Key.Escape ||
                (actualKey == Key.Enter && string.IsNullOrWhiteSpace(SearchTextBox.Text)))
            {
                CloseSearchBar();
                e.Handled = true;
                return;
            }

            if (searchEnabled && mods != ModifierKeys.None && KeybindHelper.Matches(configuredShortcut, actualKey, mods))
            {
                CloseSearchBar();
                e.Handled = true;
                return;
            }

            if (searchEnabled && !SearchTextBox.IsKeyboardFocusWithin && KeybindHelper.Matches(configuredShortcut, actualKey, mods))
            {
                _suppressNextTextInput = true;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _suppressNextTextInput = false);
                SearchTextBox.Focus();
                Keyboard.Focus(SearchTextBox);
                SearchTextBox.SelectAll();
                e.Handled = true;
                return;
            }

            if (!SearchTextBox.IsKeyboardFocusWithin &&
                actualKey is Key.Left or Key.Right or Key.Up or Key.Down &&
                MoveKeyboardSelection(actualKey))
            {
                e.Handled = true;
                return;
            }

            if (!SearchTextBox.IsKeyboardFocusWithin &&
                actualKey == Key.Enter &&
                _keyboardSelectedItem is not null &&
                IsItemVisible(_keyboardSelectedItem))
            {
                bool runAsAdmin = (mods & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift);
                LaunchItem(_keyboardSelectedItem, runAsAdmin);
                e.Handled = true;
                return;
            }

            if (!SearchTextBox.IsKeyboardFocusWithin &&
                actualKey == Key.F2 &&
                mods == ModifierKeys.None &&
                _keyboardSelectedItem is not null &&
                IsItemVisible(_keyboardSelectedItem))
            {
                BeginRenameItem(_keyboardSelectedItem);
                e.Handled = true;
                return;
            }

            return;
        }

        if (searchEnabled && KeybindHelper.Matches(configuredShortcut, actualKey, mods))
        {
            _suppressNextTextInput = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _suppressNextTextInput = false);
            OpenSearchBar();
            e.Handled = true;
            return;
        }

        if (actualKey is Key.Left or Key.Right or Key.Up or Key.Down && mods == ModifierKeys.None)
        {
            if (MoveKeyboardSelection(actualKey))
            {
                e.Handled = true;
            }
            return;
        }

        if (actualKey == Key.F2 &&
            mods == ModifierKeys.None &&
            _keyboardSelectedItem is not null &&
            IsItemVisible(_keyboardSelectedItem))
        {
            BeginRenameItem(_keyboardSelectedItem);
            e.Handled = true;
            return;
        }

        if (actualKey == Key.Enter &&
            _keyboardSelectedItem is not null &&
            IsItemVisible(_keyboardSelectedItem))
        {
            bool runAsAdmin = (mods & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift);
            LaunchItem(_keyboardSelectedItem, runAsAdmin);
            e.Handled = true;
            return;
        }

        if (actualKey == Key.Escape)
        {
            HideDock();
            e.Handled = true;
        }
    }

    private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (RenameOverlayHost.Visibility == Visibility.Visible)
        {
            return;
        }

        if (_suppressNextTextInput)
        {
            _suppressNextTextInput = false;
            e.Handled = true;
            return;
        }

        if (!_isSearchOpen &&
            _configService.Config.Settings.EnableSearch &&
            KeybindHelper.IsSlashTextTrigger(_configService.Config.Settings.SearchShortcut, e.Text, Keyboard.Modifiers))
        {
            OpenSearchBar();
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideDock();
            return;
        }

        CloseActiveContextMenu();
        _idleTrimTimer?.Stop();
        _lastSnapshot = null;
        ++_animationGeneration;
        CancelSmoothScrollAnimation();
        CloseDragOverlays();
        _activeGenieOverlay?.CancelAndClose();
        _activeGenieOverlay = null;
    }

    private void OnDockRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
    }

    private void OnDockRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidateItem = null;
    }

    private bool IsInteractiveDockChild(DependencyObject? source)
    {
        DependencyObject? current = source;
        while (current is not null && !ReferenceEquals(current, DockRootBorder))
        {
            if (current is ButtonBase or TextBoxBase or Thumb or System.Windows.Controls.ContextMenu or MenuItem ||
                ReferenceEquals(current, SearchBarContainer) ||
                ReferenceEquals(current, RenameOverlayHost))
            {
                return true;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ScheduleIdleMemoryTrim()
    {
        if (_idleTrimTimer is null)
        {
            _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            _idleTrimTimer.Tick += OnIdleTrimTimerTick;
        }

        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
    }

    private void OnIdleTrimTimerTick(object? sender, EventArgs e)
    {
        _idleTrimTimer?.Stop();

        if (IsVisible || _isOpeningGenie || _isClosingGenie || _activeGenieOverlay is not null)
        {
            return;
        }

        _lastSnapshot = null;
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        NativeMethods.TrimWorkingSet();
    }

    public bool HasOpenContextMenu => _activeContextMenu is { IsOpen: true };

    private void OnAnyContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            if (_activeContextMenu is not null &&
                !ReferenceEquals(_activeContextMenu, menu) &&
                _activeContextMenu.IsOpen)
            {
                _activeContextMenu.IsOpen = false;
            }

            _activeContextMenu = menu;
        }
    }

    private void OnAnyContextMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu && ReferenceEquals(_activeContextMenu, menu))
        {
            _activeContextMenu = null;
        }
    }

    public void CloseActiveContextMenu()
    {
        if (_activeContextMenu is { IsOpen: true } menu)
        {
            menu.IsOpen = false;
        }

        _activeContextMenu = null;
    }

    private static bool TryGetItemFromMenuItem(object sender, out DockItem? item)
    {
        item = (sender as MenuItem)?.DataContext as DockItem;
        if (item is not null)
        {
            return true;
        }

        if (sender is MenuItem { Parent: ContextMenu menu })
        {
            item = menu.DataContext as DockItem ?? (menu.PlacementTarget as FrameworkElement)?.DataContext as DockItem;
        }

        return item is not null;
    }

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static DropShadowEffect CreateFrozenDropShadow()
    {
        var shadow = new DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 4,
            Direction = 270,
            Opacity = 0.55,
            Color = Colors.Black
        };
        shadow.Freeze();
        return shadow;
    }
}
