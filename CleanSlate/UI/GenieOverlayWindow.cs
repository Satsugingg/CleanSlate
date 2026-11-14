using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using CleanSlate.Core;

namespace CleanSlate.UI;

/// <summary>
/// Renders a hardware-accelerated macOS Genie mesh warp into or out of the live mouse cursor tip
/// across a transparent, click-through overlay window so the warp never clips at dock bounds.
/// </summary>
public sealed class GenieOverlayWindow : Window
{
    private readonly int _gridDivisions;
    private readonly int _gridStride;

    private readonly Viewport3D _viewport;
    private readonly MeshGeometry3D _mesh;
    private readonly Point3D[] _vertices;
    private readonly Point3DCollection _meshPositions;
    private readonly ImageBrush _imageBrush;
    private readonly Model3DGroup _modelGroup;

    private readonly double _overlayWidthDip;
    private readonly double _overlayHeightDip;
    private readonly double _cardCenterX;
    private readonly double _cardCenterY;
    private readonly double _cardWidth;
    private readonly double _cardHeight;
    private readonly double _tipX;
    private readonly double _tipY;

    private readonly bool _isOpening;
    private readonly double _durationMs;
    private Stopwatch? _stopwatch;
    private Action? _onCompleted;
    private bool _closedOrCancelled;

    public GenieOverlayWindow(
        BitmapSource snapshot,
        ScreenBounds capturePhysBounds,
        int cursorPhysX,
        int cursorPhysY,
        double monitorScale,
        bool isOpening,
        double durationMs,
        Action onCompleted)
    {
        _isOpening = isOpening;
        _durationMs = Math.Max(80.0, durationMs);
        _onCompleted = onCompleted;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        FocusVisualStyle = null;

        int padPx = (int)Math.Round(56.0 * monitorScale);
        int ovLeftPx = Math.Min(capturePhysBounds.Left, cursorPhysX) - padPx;
        int ovTopPx = Math.Min(capturePhysBounds.Top, cursorPhysY) - padPx;
        int ovRightPx = Math.Max(capturePhysBounds.Right, cursorPhysX) + padPx;
        int ovBottomPx = Math.Max(capturePhysBounds.Bottom, cursorPhysY) + padPx;

        int ovWidthPx = Math.Max(64, ovRightPx - ovLeftPx);
        int ovHeightPx = Math.Max(64, ovBottomPx - ovTopPx);

        _overlayWidthDip = ovWidthPx / monitorScale;
        _overlayHeightDip = ovHeightPx / monitorScale;

        _cardWidth = (capturePhysBounds.Right - capturePhysBounds.Left) / monitorScale;
        _cardHeight = (capturePhysBounds.Bottom - capturePhysBounds.Top) / monitorScale;
        _cardCenterX = (((capturePhysBounds.Left + capturePhysBounds.Right) / 2.0) - ovLeftPx) / monitorScale;
        _cardCenterY = (((capturePhysBounds.Top + capturePhysBounds.Bottom) / 2.0) - ovTopPx) / monitorScale;
        _tipX = (cursorPhysX - ovLeftPx) / monitorScale;
        _tipY = (cursorPhysY - ovTopPx) / monitorScale;

        Width = _overlayWidthDip;
        Height = _overlayHeightDip;
        Left = ovLeftPx / monitorScale;
        Top = ovTopPx / monitorScale;

        _gridDivisions = 24;
        _gridStride = _gridDivisions + 1;
        _vertices = new Point3D[_gridStride * _gridStride];

        _mesh = CreateGridMesh(_gridDivisions, _gridStride);
        _meshPositions = _mesh.Positions;
        _imageBrush = new ImageBrush(snapshot)
        {
            Stretch = Stretch.Fill,
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox
        };
        RenderOptions.SetBitmapScalingMode(_imageBrush, BitmapScalingMode.HighQuality);

        // DiffuseMaterial + white AmbientLight preserves exact premultiplied alpha on AllowsTransparency windows.
        var material = new DiffuseMaterial(_imageBrush);
        var geometryModel = new GeometryModel3D(_mesh, material) { BackMaterial = material };

        _modelGroup = new Model3DGroup();
        _modelGroup.Children.Add(new AmbientLight(Colors.White));
        _modelGroup.Children.Add(geometryModel);

        _viewport = new Viewport3D
        {
            Width = _overlayWidthDip,
            Height = _overlayHeightDip,
            IsHitTestVisible = false,
            Camera = new OrthographicCamera(
                new Point3D(_overlayWidthDip / 2.0, _overlayHeightDip / 2.0, 500.0),
                new Vector3D(0, 0, -1),
                new Vector3D(0, 1, 0),
                _overlayWidthDip)
        };
        _viewport.Children.Add(new ModelVisual3D { Content = _modelGroup });
        Content = _viewport;
        Closed += (_, _) => ReleaseResources();

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            IntPtr exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            long newStyle = exStyle.ToInt64()
                | NativeMethods.WS_EX_TRANSPARENT
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newStyle));

            AcrylicHelper.EnableAcrylic(this);

            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                ovLeftPx,
                ovTopPx,
                ovWidthPx,
                ovHeightPx,
                NativeMethods.SWP_NOACTIVATE);
        };
    }

    public void Start()
    {
        if (_closedOrCancelled)
        {
            return;
        }

        UpdateMeshGeometry(_isOpening ? 1.0 : 0.0);
        Show();
        _stopwatch = Stopwatch.StartNew();
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    public void CancelAndClose()
    {
        if (_closedOrCancelled)
        {
            return;
        }

        _closedOrCancelled = true;
        CompositionTarget.Rendering -= OnRenderingFrame;
        _onCompleted = null;
        Close();
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        CompositionTarget.Rendering -= OnRenderingFrame;
        _onCompleted = null;
        _imageBrush.ImageSource = null;
        _viewport.Children.Clear();
        _modelGroup.Children.Clear();
        _mesh.Positions = null;
        _meshPositions.Clear();
        _mesh.TextureCoordinates.Clear();
        _mesh.TriangleIndices.Clear();
        Content = null;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (_closedOrCancelled || _stopwatch is null)
        {
            return;
        }

        double raw = Math.Clamp(_stopwatch.Elapsed.TotalMilliseconds / _durationMs, 0.0, 1.0);

        // Open runs the exact curved Genie trajectory cleanly in reverse (1.0 -> 0.0); Close runs forward (0.0 -> 1.0).
        double warpProgress = _isOpening
            ? 1.0 - EaseOutCubic(raw)
            : raw;

        UpdateMeshGeometry(warpProgress);

        if (raw >= 1.0)
        {
            FinishAnimation();
        }
    }

    private void FinishAnimation()
    {
        if (_closedOrCancelled)
        {
            return;
        }

        _closedOrCancelled = true;
        CompositionTarget.Rendering -= OnRenderingFrame;

        Action? callback = _onCompleted;
        _onCompleted = null;
        callback?.Invoke();

        if (_isOpening)
        {
            // Keep the final unwarped Genie frame live until MainWindow's Render pass commits to DWM,
            // preventing a 1-frame flicker/handoff jump while semi-transparent surfaces transition cleanly.
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                try
                {
                    Close();
                }
                catch
                {
                    // Ignore if already closed.
                }
            }));
            return;
        }

        // Hide immediately when closing so no ghost surface lingers.
        try
        {
            Close();
        }
        catch
        {
            // Ignore if already closed.
        }
    }

    private void UpdateMeshGeometry(double p)
    {
        double dx = _tipX - _cardCenterX;
        double dy = _tipY - _cardCenterY;
        double distCenter = Math.Sqrt((dx * dx) + (dy * dy));

        double halfW = _cardWidth / 2.0;
        double halfH = _cardHeight / 2.0;

        // Weight between centered Genie plume (when mouse sits inside/center of dock) and directional Genie funnel (off-center/outside).
        double directionalWeight = Math.Clamp((distCenter - 15.0) / 65.0, 0.0, 1.0);

        double ax = distCenter > 4.0 ? dx / distCenter : 0.0;
        double ay = distCenter > 4.0 ? dy / distCenter : 1.0;
        double bx = -ay;
        double by = ax;

        double absProj = (Math.Abs(ax) * halfW) + (Math.Abs(ay) * halfH);
        double sMin = -absProj;
        double sMax = absProj;
        double sSpanTotal = Math.Max(1.0, sMax - sMin);

        double funnelOpen = SmoothStep(Math.Clamp(p / 0.36, 0.0, 1.0));
        double sTip = distCenter;
        double funnelSpan = Math.Max(50.0, sTip - sMin);

        double openFrac = 1.0 - p;

        int idx = 0;
        for (int iy = 0; iy < _gridStride; iy++)
        {
            double v = iy / (double)_gridDivisions;
            double offY = (v - 0.5) * _cardHeight;

            for (int ix = 0; ix < _gridStride; ix++)
            {
                double u = ix / (double)_gridDivisions;
                double offX = (u - 0.5) * _cardWidth;

                // 1) Directional macOS Genie funnel (active when cursor is off-center or outside dock)
                double dirX = _cardCenterX + offX;
                double dirY = _cardCenterY + offY;
                if (directionalWeight > 0.001)
                {
                    double s0 = (offX * ax) + (offY * ay);
                    double w0 = (offX * bx) + (offY * by);
                    double rDir = Math.Clamp((s0 - sMin) / sSpanTotal, 0.0, 1.0);

                    double dirStart = 0.24 * (1.0 - rDir);
                    double dirEnd = 0.44 + (0.56 * (1.0 - rDir));
                    double dirLocal = Math.Clamp((p - dirStart) / Math.Max(0.001, dirEnd - dirStart), 0.0, 1.0);
                    double dirMove = Math.Clamp(Math.Pow(dirLocal, 1.65), 0.0, 1.0);

                    double sCur = s0 + ((sTip - s0) * dirMove);
                    double z = Math.Clamp((sCur - sMin) / funnelSpan, 0.0, 1.0);
                    double sCurve = z * z * (3.0 - (2.0 * z));
                    double funnelPinch = 1.0 - (funnelOpen * sCurve);
                    double tipCollapse = 1.0 - (dirMove * dirMove);
                    double widthScale = p >= 0.999
                        ? 0.0
                        : Math.Max(0.0, funnelPinch * (0.14 + (0.86 * tipCollapse)));

                    dirX = _cardCenterX + (sCur * ax) + (w0 * widthScale * bx);
                    dirY = _cardCenterY + (sCur * ay) + (w0 * widthScale * by);
                }

                // 2) Centered Genie plume (top blooms first while bottom neck tapers at mouse tip)
                double cenX = dirX;
                double cenY = dirY;
                if (directionalWeight < 0.999)
                {
                    double mStart = 0.16 * v;
                    double mEnd = 0.58 + (0.42 * v);
                    double mLocal = Math.Clamp((openFrac - mStart) / Math.Max(0.001, mEnd - mStart), 0.0, 1.0);
                    double moveOut = SmoothStep(mLocal);

                    double wStart = 0.20 * v;
                    double wEnd = 0.48 + (0.52 * v);
                    double wLocal = Math.Clamp((openFrac - wStart) / Math.Max(0.001, wEnd - wStart), 0.0, 1.0);
                    double wScale = SmoothStep(wLocal);

                    double targetX = _cardCenterX + offX;
                    double targetY = _cardCenterY + offY;
                    cenX = _tipX + ((targetX - _tipX) * wScale);
                    cenY = _tipY + ((targetY - _tipY) * moveOut);
                }

                double finalX = cenX + ((dirX - cenX) * directionalWeight);
                double finalY = cenY + ((dirY - cenY) * directionalWeight);

                _vertices[idx++] = new Point3D(finalX, _overlayHeightDip - finalY, 0.0);
            }
        }

        _mesh.Positions = null;
        for (int i = 0; i < _vertices.Length; i++)
        {
            _meshPositions[i] = _vertices[i];
        }
        _mesh.Positions = _meshPositions;

        if (p > 0.85)
        {
            double fadeFrac = Math.Clamp((1.0 - p) / 0.15, 0.0, 1.0);
            _viewport.Opacity = SmoothStep(fadeFrac);
        }
        else
        {
            _viewport.Opacity = 1.0;
        }
    }

    private static double SmoothStep(double t) => t * t * (3.0 - (2.0 * t));

    private static double EaseOutCubic(double t)
    {
        double inv = 1.0 - t;
        return 1.0 - (inv * inv * inv);
    }

    private static MeshGeometry3D CreateGridMesh(int divisions, int stride)
    {
        var texCoords = new PointCollection(stride * stride);
        var indices = new Int32Collection(divisions * divisions * 6);
        var positions = new Point3DCollection(stride * stride);

        for (int iy = 0; iy < stride; iy++)
        {
            double v = iy / (double)divisions;
            for (int ix = 0; ix < stride; ix++)
            {
                double u = ix / (double)divisions;
                texCoords.Add(new Point(u, v));
                positions.Add(new Point3D(0, 0, 0));
            }
        }

        for (int iy = 0; iy < divisions; iy++)
        {
            for (int ix = 0; ix < divisions; ix++)
            {
                int i0 = (iy * stride) + ix;
                int i1 = i0 + 1;
                int i2 = i0 + stride;
                int i3 = i2 + 1;

                indices.Add(i0);
                indices.Add(i3);
                indices.Add(i1);

                indices.Add(i0);
                indices.Add(i2);
                indices.Add(i3);
            }
        }

        return new MeshGeometry3D
        {
            Positions = positions,
            TextureCoordinates = texCoords,
            TriangleIndices = indices
        };
    }
}
