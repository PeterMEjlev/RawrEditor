using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

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
        }

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

        _isPanning = true;
        _panOrigin = e.GetPosition(ViewerHost);
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

    private void OnViewerMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
        // "100%" = one buffer pixel per screen pixel. Stretch=Uniform's fit
        // gives us a known fitScale (bitmap-px per viewer-px); we cancel it
        // out by setting userScale = 1/fitScale.
        double fitScale = ComputeFitScale() * _rotationFit;
        if (fitScale <= 0) return;
        _userScale = 1.0 / fitScale;
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
    }

    private void UpdateZoomLabel()
    {
        if (IsAtFit())
        {
            ZoomLabel.Text = "Fit";
            return;
        }
        // Display absolute scale (buffer-pixel : screen-pixel) so "100%" lines
        // up with the 1:1 button.
        double absScale = ComputeFitScale() * _rotationFit * _userScale;
        ZoomLabel.Text = absScale > 0 ? $"{absScale * 100:0}%" : "—";
    }
}
