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
    }

    private void OnViewerSourceChanged(object? sender, EventArgs e)
    {
        if (ViewerImage.Source is not BitmapSource bs) return;
        bool dimsChanged = bs.PixelWidth != _lastImageWidth || bs.PixelHeight != _lastImageHeight;
        _lastImageWidth = bs.PixelWidth;
        _lastImageHeight = bs.PixelHeight;
        // New photo → snap to Fit. Slider re-renders (same dimensions) keep
        // the user's current zoom/pan untouched.
        if (dimsChanged) FitToViewport();
    }

    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Stretch="Uniform" handles fit automatically on resize; we only need
        // to update the label and clear pan so nothing drifts off-screen.
        if (IsAtFit()) UpdateZoomLabel();
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

    private void OnOneToOneClick(object sender, RoutedEventArgs e)
    {
        // "100%" = one buffer pixel per screen pixel. Stretch=Uniform's fit
        // gives us a known fitScale (bitmap-px per viewer-px); we cancel it
        // out by setting userScale = 1/fitScale.
        double fitScale = ComputeFitScale();
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
        ViewerScale.ScaleX = _userScale;
        ViewerScale.ScaleY = _userScale;
        ViewerTranslate.X = _tx;
        ViewerTranslate.Y = _ty;
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
        double absScale = ComputeFitScale() * _userScale;
        ZoomLabel.Text = absScale > 0 ? $"{absScale * 100:0}%" : "—";
    }
}
