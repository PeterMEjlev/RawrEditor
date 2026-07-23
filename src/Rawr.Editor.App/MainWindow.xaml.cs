using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Rawr.Develop;

namespace Rawr.Editor.App;

/// <summary>
/// Photo-viewer interaction: zoom, pan, fit, 1:1.
///
/// The Image uses Stretch="Uniform" so WPF handles the fit calculation on
/// every layout pass — no manual ActualWidth/Height math, no timing windows.
/// On top of that we apply a user-controlled <c>ScaleTransform</c> (1.0 = fit)
/// and a <c>TranslateTransform</c> (0,0 = centred), giving cursor-targeted
/// zoom and drag-pan without touching layout.
/// </summary>
public partial class MainWindow : Window
{
    private double _userScale = 1.0;   // 1.0 = WPF fit; >1 zooms in, <1 zooms out
    private double _tx, _ty;            // pan offset on top of the fit
    private double _rotationFit = 1.0;  // extra shrink so a straightened frame still fits

    private int _lastImageWidth, _lastImageHeight;

    private bool _isPanning;
    private Point _panOrigin;
    private double _txAtPanStart, _tyAtPanStart;

    private const double MinUserScale = 0.2;
    private const double MaxUserScale = 32.0;
    private const double WheelZoomStep = 1.2;

    // ── Sharp detail tile ────────────────────────────────────────────────────
    // Once the sensor buffer is ready the visible window is rendered at the
    // resolution the screen needs for the current zoom — full sensor pixels at
    // 100%+, fewer as you zoom out — so it is sharp at every zoom, not just 100%,
    // while staying viewport-cheap (the tile is always about a viewport of pixels).
    // Requests are throttled; placement happens every frame.
    private readonly DispatcherTimer _detailTimer;
    private PixelRect _pendingRoi;
    private PixelRect _lastRequestedRoi;
    private double _pendingScale = 1.0;
    private double _lastRequestedScale;
    private bool _detailActiveRequest;

    private const int DetailMargin = 96;               // render a little past the viewport so small pans stay covered
    private const long DetailMaxPixels = 24_000_000;   // belt against a pathologically large tile

    public MainWindow()
    {
        InitializeComponent();

        // Track Image.Source changes so a new photo resets zoom but slider
        // re-renders preserve it (Image.Source is a DP, not a CLR property,
        // so we hook it via DependencyPropertyDescriptor).
        var sourceDesc = DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        sourceDesc.AddValueChanged(ViewerImage, OnViewerSourceChanged);

        // The mask overlay draws in screen space, so it needs the same fit ×
        // zoom × pan the Image is under. Routed through these three properties
        // rather than by sharing the RenderTransform, so the outline scales with
        // the photo while the grab handles do not.
        MaskOverlay.GeometryChanged += (_, _) => ViewModel?.NotifyMaskGeometryChanged();
        MaskOverlay.MaskPicked += (_, mask) => ViewModel?.SelectMaskByShape(mask);
        MaskOverlay.CreateRequested += (_, point) =>
            ViewModel?.CreateMaskAt(point.X, point.Y);

        // The crop box shares the same screen mapping. It edits the view-model's
        // GeometrySettings in place, so — like the masks — nothing it changes is
        // a bound property and the repaint has to be asked for explicitly.
        CropOverlay.CropChanged += (_, _) => ViewModel?.NotifyCropChanged();
        CropOverlay.StraightenChanged += (_, angle) => ViewModel?.NotifyStraightenDragged(angle);

        if (ViewModel is { } vm)
        {
            vm.MaskVisualsChanged += (_, _) => MaskOverlay.InvalidateVisual();
            vm.CropVisualsChanged += (_, _) =>
            {
                // The straighten lives in the viewer transform while the crop
                // tool is open, so a change to it is a transform update here —
                // not a re-render.
                ApplyTransform();
                CropOverlay.InvalidateVisual();
            };
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        // Coalesces the flood of transform changes a pan or wheel-zoom produces
        // into one 1:1 tile request per pause.
        _detailTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _detailTimer.Tick += OnDetailTimerTick;

        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    /// <summary>
    /// Window-level shortcuts, deliberately stood down while a text box has the
    /// keyboard. The panel's readouts are editable, and a shortcut that fired
    /// mid-edit would mean typing a value could silently trip a tool.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (ViewModel is not { } vm) return;

        switch (e.Key)
        {
            case Key.C:
                vm.OpenCropCommand.Execute(null);
                e.Handled = true;
                break;

            // Enter and Escape belong to the crop tool while it is open; left
            // unhandled otherwise so they keep their ordinary meaning.
            case Key.Enter when vm.IsCropActive:
                vm.CommitCropCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape when vm.IsCropActive:
                vm.CancelCropCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemPipe:
            case Key.OemBackslash:
                vm.ToggleBeforeCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private ViewModels.MainViewModel? ViewModel => DataContext as ViewModels.MainViewModel;

    private void OnViewerSourceChanged(object? sender, EventArgs e)
    {
        if (ViewerImage.Source is not BitmapSource bs) return;
        bool dimsChanged = bs.PixelWidth != _lastImageWidth || bs.PixelHeight != _lastImageHeight;
        _lastImageWidth = bs.PixelWidth;
        _lastImageHeight = bs.PixelHeight;
        MaskOverlay.ImageWidth = bs.PixelWidth;
        MaskOverlay.ImageHeight = bs.PixelHeight;
        // New photo → snap to Fit. Slider re-renders (same dimensions) keep
        // the user's current zoom/pan untouched. A crop, a straighten or a 90°
        // turn all change the dimensions, so those re-fit too — which is what
        // you want, since the frame they produce is a different shape.
        if (dimsChanged) FitToViewport();
        else ApplyTransform();
    }

    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Stretch="Uniform" handles fit automatically on resize; we only need
        // to update the label and clear pan so nothing drifts off-screen.
        if (IsAtFit()) UpdateZoomLabel();
        // The fit scale — and with it the shrink that keeps a straightened frame
        // on screen — is a function of the viewport, so the whole transform has
        // to be re-solved on resize even though no zoom or pan happened.
        ApplyTransform();
        ReportViewportToPreview();
    }

    /// <summary>
    /// Tell the view-model how many device pixels the viewport spans so it can size
    /// the preview buffer to the display rather than upscale a fixed 1920. Only
    /// while at Fit: rebuilding the preview changes its pixel dimensions, which
    /// snaps the view back to Fit, so doing it mid-zoom would fight the user.
    /// </summary>
    private void ReportViewportToPreview()
    {
        if (ViewModel is not { } vm || !IsAtFit()) return;
        if (ViewerHost.ActualWidth <= 0 || ViewerHost.ActualHeight <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double longEdgeDip = Math.Max(ViewerHost.ActualWidth, ViewerHost.ActualHeight);
        vm.SetPreviewLongEdge((int)Math.Ceiling(longEdgeDip * dpi));
    }

    /// <summary>
    /// Push the current absolute image→screen mapping onto both overlays.
    /// Absolute (fit × rotation shrink × user zoom) rather than the user scale
    /// alone, because they work in image pixels and know nothing about
    /// Stretch="Uniform".
    /// </summary>
    private void SyncOverlays()
    {
        double scale = ComputeFitScale() * _rotationFit * _userScale;
        MaskOverlay.ViewScale = scale;
        MaskOverlay.ViewOffsetX = _tx;
        MaskOverlay.ViewOffsetY = _ty;
        CropOverlay.ViewScale = scale;
        CropOverlay.ViewOffsetX = _tx;
        CropOverlay.ViewOffsetY = _ty;
    }

    /// <summary>
    /// How much to shrink so a photo turned by the crop tool's straighten still
    /// fits the viewport.
    ///
    /// <para>Stretch="Uniform" fits the <i>unrotated</i> bitmap, which is all
    /// WPF knows about; the rotated frame sweeps out a larger box, so the extra
    /// factor here is the ratio between fitting that box and fitting the bitmap.
    /// It is 1 whenever nothing is turned.</para>
    /// </summary>
    private double ComputeRotationFit()
    {
        if (ViewerImage.Source is not BitmapSource bs) return 1.0;
        var (ew, eh) = ViewModel?.ViewerExtent() ?? (0.0, 0.0);
        if (ew <= 0 || eh <= 0) return 1.0;
        if (ViewerHost.ActualWidth <= 0 || ViewerHost.ActualHeight <= 0) return 1.0;

        double bitmapFit = Math.Min(ViewerHost.ActualWidth / bs.PixelWidth,
                                    ViewerHost.ActualHeight / bs.PixelHeight);
        double extentFit = Math.Min(ViewerHost.ActualWidth / ew,
                                    ViewerHost.ActualHeight / eh);
        return bitmapFit > 0 ? Math.Min(1.0, extentFit / bitmapFit) : 1.0;
    }

    private void OnViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewerImage.Source is null) return;

        var pos = e.GetPosition(ViewerHost);
        double cx = ViewerHost.ActualWidth / 2;
        double cy = ViewerHost.ActualHeight / 2;

        // RenderTransformOrigin = (0.5, 0.5) on the Image, which fills the Grid.
        // So the transform pivots about (cx, cy). In that frame, the image-
        // relative point under the cursor (in pre-scale fit-units) is:
        double imgRelX = (pos.X - cx - _tx) / _userScale;
        double imgRelY = (pos.Y - cy - _ty) / _userScale;

        double factor = e.Delta > 0 ? WheelZoomStep : 1.0 / WheelZoomStep;
        double newScale = Math.Clamp(_userScale * factor, MinUserScale, MaxUserScale);
        if (Math.Abs(newScale - _userScale) < 1e-6) return;

        _userScale = newScale;
        // Re-solve translate so the same image point stays under the cursor.
        _tx = pos.X - cx - imgRelX * _userScale;
        _ty = pos.Y - cy - imgRelY * _userScale;

        ApplyTransform();
        e.Handled = true;
    }

    private void OnViewerMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewerImage.Source is null) return;

        // Second click of a double-click → snap to Fit, no pan started.
        if (e.ClickCount >= 2)
        {
            FitToViewport();
            return;
        }

        BeginPan(e.GetPosition(ViewerHost));
    }

    /// <summary>
    /// Middle-drag pans too.
    ///
    /// <para>Normally the left button is enough — presses the overlays do not want
    /// fall through to here. A selected brush mask is the exception: a paint tool
    /// has to claim every left press on the canvas, which would otherwise leave a
    /// zoomed-in photo with no way to move under the brush. The middle button is
    /// the one no tool takes.</para>
    /// </summary>
    private void OnViewerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || ViewerImage.Source is null) return;
        BeginPan(e.GetPosition(ViewerHost));
        e.Handled = true;
    }

    private void OnViewerMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        EndPan();
    }

    private void BeginPan(Point origin)
    {
        _isPanning = true;
        _panOrigin = origin;
        _txAtPanStart = _tx;
        _tyAtPanStart = _ty;
        ViewerHost.CaptureMouse();
        ViewerHost.Cursor = Cursors.SizeAll;
    }

    private void OnViewerMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var pos = e.GetPosition(ViewerHost);
        _tx = _txAtPanStart + (pos.X - _panOrigin.X);
        _ty = _tyAtPanStart + (pos.Y - _panOrigin.Y);
        ApplyTransform();
    }

    private void OnViewerMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPan();

    private void EndPan()
    {
        if (!_isPanning) return;
        _isPanning = false;
        ViewerHost.ReleaseMouseCapture();
        ViewerHost.Cursor = Cursors.Arrow;
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToViewport();

    private void OnCalibrationButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnOneToOneClick(object sender, RoutedEventArgs e)
    {
        // "100%" = one *sensor* pixel per *device* pixel — the real 1:1 the detail
        // tile serves. absOut = fit × rotation × userScale × (preview-px per
        // sensor-px); solve userScale so that product is 1/dpi, i.e. one output
        // pixel spans exactly one device pixel after WPF's dpi compositing. Until
        // the sensor buffer has decoded we fall back to buffer-1:1.
        double dpi = DpiScale();
        if (ViewModel is { DetailReady: true } vm
            && ViewerImage.Source is BitmapSource bs && vm.DetailFrameWidth > 0)
        {
            double fitPrev = ComputeFitScale() * _rotationFit;
            double outToPrev = bs.PixelWidth / (double)vm.DetailFrameWidth;
            if (fitPrev > 0 && outToPrev > 0)
            {
                _userScale = Math.Min(MaxUserScale, 1.0 / (fitPrev * outToPrev * dpi));
                _tx = 0;
                _ty = 0;
                ApplyTransform();
                return;
            }
        }

        double fitScale = ComputeFitScale() * _rotationFit;
        if (fitScale <= 0) return;
        _userScale = 1.0 / (fitScale * dpi);
        _tx = 0;
        _ty = 0;
        ApplyTransform();
    }

    private void FitToViewport()
    {
        _userScale = 1.0;
        _tx = 0;
        _ty = 0;
        ApplyTransform();
        // Back at Fit, a viewport enlargement deferred during the zoom can now be
        // honoured — size the preview up to the display if it grew.
        ReportViewportToPreview();
    }

    private double ComputeFitScale()
    {
        if (ViewerImage.Source is not BitmapSource bs
            || ViewerHost.ActualWidth <= 0
            || ViewerHost.ActualHeight <= 0)
            return 0;
        return Math.Min(ViewerHost.ActualWidth / bs.PixelWidth,
                        ViewerHost.ActualHeight / bs.PixelHeight);
    }

    private bool IsAtFit() => Math.Abs(_userScale - 1.0) < 1e-6 && _tx == 0 && _ty == 0;

    /// <summary>Device pixels per DIP for this window (1.0 at 100% scaling, 1.5 at
    /// 150%, …). Used to make "100%" a true one-sensor-pixel-per-device-pixel view
    /// and render the detail tile at the resolution the display actually has.</summary>
    private double DpiScale() => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void ApplyTransform()
    {
        // Straighten and the shrink that keeps it on screen are re-solved on
        // every pass: inside the crop tool they change with the drag, and the
        // whole point of putting them here is that neither costs a render.
        ViewerRotate.Angle = ViewModel?.ViewerStraighten ?? 0.0;
        _rotationFit = ComputeRotationFit();

        double scale = _userScale * _rotationFit;
        ViewerScale.ScaleX = scale;
        ViewerScale.ScaleY = scale;
        ViewerTranslate.X = _tx;
        ViewerTranslate.Y = _ty;
        SyncOverlays();
        UpdateZoomLabel();
        UpdateDetailView();
    }

    private void UpdateZoomLabel()
    {
        if (IsAtFit())
        {
            ZoomLabel.Text = "Fit";
            return;
        }
        // Report sensor pixels : *device* pixels, so "100%" is a true 1:1 view of the
        // RAW (matching the 1:1 button and the detail tile). Until the sensor buffer
        // decodes we can only report against the preview buffer.
        double dpi = DpiScale();
        double absPrev = ComputeFitScale() * _rotationFit * _userScale;
        double pct = absPrev * dpi;
        if (ViewModel is { DetailReady: true } vm
            && ViewerImage.Source is BitmapSource bs && vm.DetailFrameWidth > 0)
            pct = absPrev * (bs.PixelWidth / (double)vm.DetailFrameWidth) * dpi;
        ZoomLabel.Text = pct > 0 ? $"{pct * 100:0}%" : "—";
    }

    // ── Full-resolution 1:1 tile ────────────────────────────────────────────

    /// <summary>
    /// The image→screen mapping the tile needs: <paramref name="absPrev"/> is
    /// screen-DIP per preview pixel (the scale the preview is drawn at),
    /// <paramref name="outToPrev"/> is preview pixels per full-resolution output
    /// pixel, and (<paramref name="prevCx"/>, <paramref name="prevCy"/>) is the
    /// preview bitmap's centre. Returns false when there is nothing to map yet.
    /// </summary>
    private bool TryDetailMapping(out double absPrev, out double outToPrev,
                                  out double prevCx, out double prevCy,
                                  out int fullOutW, out int fullOutH)
    {
        absPrev = outToPrev = prevCx = prevCy = 0;
        fullOutW = fullOutH = 0;

        if (ViewModel is not { DetailReady: true } vm) return false;
        if (ViewerImage.Source is not BitmapSource bs) return false;
        if (ViewerHost.ActualWidth <= 0 || ViewerHost.ActualHeight <= 0) return false;

        fullOutW = vm.DetailFrameWidth;
        fullOutH = vm.DetailFrameHeight;
        if (fullOutW <= 0 || fullOutH <= 0) return false;

        double prevW = bs.PixelWidth, prevH = bs.PixelHeight;
        double fitPrev = Math.Min(ViewerHost.ActualWidth / prevW, ViewerHost.ActualHeight / prevH);
        absPrev = fitPrev * _rotationFit * _userScale;
        outToPrev = prevW / fullOutW;
        prevCx = prevW / 2;
        prevCy = prevH / 2;
        return absPrev > 0 && outToPrev > 0;
    }

    /// <summary>
    /// Decide whether the sharp tile should show, ask the view-model for the
    /// visible window (and the display resolution it needs for the current zoom),
    /// and keep whatever tile exists registered to the photo under the live
    /// pan/zoom. Called on every transform change.
    /// </summary>
    private void UpdateDetailView()
    {
        if (ViewModel is not { } vm) return;

        double absPrev = 0, outToPrev = 0, prevCx = 0, prevCy = 0;
        int fullOutW = 0, fullOutH = 0;
        // Before gets a sharp tile too, rendered from the neutral settings (see
        // MainViewModel.KickDetailRender); without one it would fall back to the
        // soft preview while After stayed crisp.
        bool wanted = !vm.IsCropActive
                      && TryDetailMapping(out absPrev, out outToPrev,
                                          out prevCx, out prevCy,
                                          out fullOutW, out fullOutH);

        PixelRect roi = default;
        double ts = 1.0;
        if (wanted)
        {
            double absOut = absPrev * outToPrev;   // screen-DIP per sensor pixel
            // Render the tile at the resolution the *device* has, not the DIP count:
            // on a scaled display one DIP is dpi device pixels, so sampling to DIPs
            // leaves WPF to upscale the tile (soft). ×dpi lands one tile pixel on one
            // device pixel; capped at 1:1 — never above sensor res.
            double absOutDevice = absOut * DpiScale();
            ts = Math.Min(1.0, absOutDevice);      // never sample above sensor 1:1
            if (ts <= 0.0)
            {
                wanted = false;
            }
            else
            {
                // Invert the transform at the viewport corners to get the visible
                // window in full-resolution output pixels.
                double cx = ViewerHost.ActualWidth / 2, cy = ViewerHost.ActualHeight / 2;
                double ax = ((0 - cx - _tx) / absPrev + prevCx) / outToPrev;
                double ay = ((0 - cy - _ty) / absPrev + prevCy) / outToPrev;
                double bx = ((ViewerHost.ActualWidth - cx - _tx) / absPrev + prevCx) / outToPrev;
                double by = ((ViewerHost.ActualHeight - cy - _ty) / absPrev + prevCy) / outToPrev;

                int rx0 = Math.Clamp((int)Math.Floor(Math.Min(ax, bx)) - DetailMargin, 0, fullOutW);
                int ry0 = Math.Clamp((int)Math.Floor(Math.Min(ay, by)) - DetailMargin, 0, fullOutH);
                int rx1 = Math.Clamp((int)Math.Ceiling(Math.Max(ax, bx)) + DetailMargin, 0, fullOutW);
                int ry1 = Math.Clamp((int)Math.Ceiling(Math.Max(ay, by)) + DetailMargin, 0, fullOutH);

                // The tile is rendered at ts, so its cost is the scaled window —
                // about a viewport at any zoom. Guard only against the pathological.
                long scaledPx = (long)((rx1 - rx0) * ts) * (long)((ry1 - ry0) * ts);
                if (rx1 <= rx0 || ry1 <= ry0 || scaledPx > DetailMaxPixels)
                    wanted = false;
                else
                    roi = new PixelRect(rx0, ry0, rx1 - rx0, ry1 - ry0);
            }
        }

        if (!wanted)
        {
            _detailTimer.Stop();
            if (_detailActiveRequest)
            {
                vm.SetDetailRequest(default, 1.0, false);
                _detailActiveRequest = false;
                _lastRequestedRoi = default;
                _lastRequestedScale = 0;
            }
            return;
        }

        // Keep the existing tile glued to the photo as the user drags (cheap).
        PlaceDetailTile(vm, absPrev, outToPrev, prevCx, prevCy);

        // Re-render when the window moved or the zoom crossed into a new sampling
        // resolution, throttled so a pan doesn't queue a tile per frame.
        if (roi != _lastRequestedRoi || Math.Abs(ts - _lastRequestedScale) > 0.01)
        {
            _pendingRoi = roi;
            _pendingScale = ts;
            _detailTimer.Stop();
            _detailTimer.Start();
        }
    }

    private void OnDetailTimerTick(object? sender, EventArgs e)
    {
        _detailTimer.Stop();
        if (ViewModel is not { } vm) return;
        vm.SetDetailRequest(_pendingRoi, _pendingScale, true);
        _lastRequestedRoi = _pendingRoi;
        _lastRequestedScale = _pendingScale;
        _detailActiveRequest = true;
    }

    /// <summary>
    /// Position the detail tile so its top-left output pixel lands exactly where
    /// the preview puts that same pixel, and scale it so its pixels cover the same
    /// screen span the preview would — so the tile overlays the preview in perfect
    /// register at whatever resolution it was rendered.
    /// </summary>
    private void PlaceDetailTile(ViewModels.MainViewModel vm,
                                 double absPrev, double outToPrev, double prevCx, double prevCy)
    {
        double tileScale = vm.DetailScaleValue;   // tile pixels per full-output pixel
        if (tileScale <= 0.0) return;

        double cx = ViewerHost.ActualWidth / 2, cy = ViewerHost.ActualHeight / 2;
        double absOut = absPrev * outToPrev;                 // screen-DIP per output pixel
        double screenPerTilePx = absOut / tileScale;         // screen-DIP per tile pixel
        double e = cx + (vm.DetailOriginX * outToPrev - prevCx) * absPrev + _tx;
        double f = cy + (vm.DetailOriginY * outToPrev - prevCy) * absPrev + _ty;
        DetailTransform.Matrix = new Matrix(screenPerTilePx, 0, 0, screenPerTilePx, e, f);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ViewModels.MainViewModel.DetailImage):
                // A fresh tile arrived (from a pan/zoom request or a slider edit).
                // Register it under the current transform.
                if (ViewModel is { } vm
                    && TryDetailMapping(out double ap, out double otp,
                                        out double pcx, out double pcy, out _, out _))
                    PlaceDetailTile(vm, ap, otp, pcx, pcy);
                break;

            case nameof(ViewModels.MainViewModel.DetailReady):
                // The sensor buffer finished decoding — the label and 1:1 button
                // switch to true sensor percentages, and a 100%+ view can light up.
                UpdateZoomLabel();
                UpdateDetailView();
                break;

            case nameof(ViewModels.MainViewModel.IsShowingBefore):
                // The tile now shows in both modes (neutral vs edited); re-place it
                // for the new state. The view-model kicks the actual re-render, since
                // the toggle changes neither the ROI nor the zoom the throttle keys on.
                UpdateDetailView();
                break;
        }
    }
}
