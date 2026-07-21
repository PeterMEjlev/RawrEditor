using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Rawr.Develop;

// WPF has its own Geometry (a drawing path), so the crop/straighten maths needs
// naming explicitly wherever the two namespaces meet.
using ImageGeometry = Rawr.Develop.Geometry;

namespace Rawr.Editor.App.Controls;

/// <summary>Which guide grid the crop box draws inside itself.</summary>
public enum CropGuide
{
    None,
    Thirds,
    Grid,
    Golden,
    Diagonal,
}

/// <summary>
/// The on-canvas crop box: dims what is being cut away, draws the guide grid,
/// and handles the drags that move, resize and rotate the frame.
///
/// <para><b>The box is fixed and the photo turns underneath it.</b> While this
/// tool is up the viewer shows the whole un-straightened frame and applies the
/// straighten as a live WPF transform, so the box stays axis-aligned on screen
/// at any angle and dragging it stays plain rectangle arithmetic. Nothing here
/// re-renders — that is what makes rotating instant.</para>
///
/// <para><b>Coordinates are the extent's pixels</b>: the bounding box of the
/// rotated photo, in the same units as the preview buffer. See
/// <see cref="ImageGeometry.CropRectInFullExtent"/> for why the box is upright in
/// that frame even though the photo under it is not.</para>
///
/// <para>Like <see cref="MaskOverlay"/> it converts to screen space on every
/// render pass rather than inheriting the viewer's zoom transform, so the border
/// stays one pixel and the handles stay the same size at any magnification. It
/// paints nothing — and takes no mouse input — while inactive, which is what
/// leaves the mask overlay reachable underneath it.</para>
/// </summary>
public sealed class CropOverlay : FrameworkElement
{
    // Screen-space sizes, deliberately constant across zoom levels.
    private const double CornerArm = 20.0;
    private const double EdgeArm = 26.0;
    private const double HandleWeight = 3.0;
    private const double HitSlop = 11.0;
    private const double MinCropPx = 34.0;

    private static readonly Brush ScrimFill = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0x0A, 0x0B, 0x0E)));
    private static readonly Pen BorderPen = MakePen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), 1.0);
    private static readonly Pen GuidePen = MakePen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 1.0);
    private static readonly Brush HandleFill = Freeze(new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF4, 0xF7)));

    private static Pen MakePen(Color c, double thickness)
        => Freeze(new Pen(Freeze(new SolidColorBrush(c)), thickness));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private enum Grip
    {
        None, Move, Rotate,
        Left, Right, Top, Bottom,
        TopLeft, TopRight, BottomLeft, BottomRight,
    }

    private Grip _grip = Grip.None;
    private Point _dragStart;                       // extent pixels
    private Rect _dragStartRect;                    // extent pixels

    // Rotation is tracked against a pivot frozen at press time. The crop centre
    // moves on screen as the photo turns beneath it, and chasing it mid-gesture
    // would feed the rotation back into its own input.
    private Point _rotatePivot;                     // screen
    private double _rotateStartPointer;             // degrees
    private double _rotateStartAngle;               // degrees

    // ── Bound state ────────────────────────────────────────────────────────

    /// <summary>The live settings object, edited in place — the same instance
    /// the view-model hands the renderer.</summary>
    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.Register(nameof(Geometry), typeof(GeometrySettings), typeof(CropOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Dimensions of the uncropped preview buffer. Every calculation
    /// here is defined against these, not against the bitmap on screen.</summary>
    public static readonly DependencyProperty SourceWidthProperty =
        DependencyProperty.Register(nameof(SourceWidth), typeof(int), typeof(CropOverlay),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceHeightProperty =
        DependencyProperty.Register(nameof(SourceHeight), typeof(int), typeof(CropOverlay),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Absolute buffer-pixel → screen-pixel scale, including the shrink
    /// the viewer applies to keep a rotated frame on screen.</summary>
    public static readonly DependencyProperty ViewScaleProperty =
        DependencyProperty.Register(nameof(ViewScale), typeof(double), typeof(CropOverlay),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewOffsetXProperty =
        DependencyProperty.Register(nameof(ViewOffsetX), typeof(double), typeof(CropOverlay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewOffsetYProperty =
        DependencyProperty.Register(nameof(ViewOffsetY), typeof(double), typeof(CropOverlay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(CropOverlay),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GuideProperty =
        DependencyProperty.Register(nameof(Guide), typeof(CropGuide), typeof(CropOverlay),
            new FrameworkPropertyMetadata(CropGuide.Thirds, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Width ÷ height the box is held to while dragging. 0 leaves it
    /// free.</summary>
    public static readonly DependencyProperty AspectRatioProperty =
        DependencyProperty.Register(nameof(AspectRatio), typeof(double), typeof(CropOverlay),
            new PropertyMetadata(0.0));

    public GeometrySettings? Geometry
    {
        get => (GeometrySettings?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    public int SourceWidth { get => (int)GetValue(SourceWidthProperty); set => SetValue(SourceWidthProperty, value); }
    public int SourceHeight { get => (int)GetValue(SourceHeightProperty); set => SetValue(SourceHeightProperty, value); }
    public double ViewScale { get => (double)GetValue(ViewScaleProperty); set => SetValue(ViewScaleProperty, value); }
    public double ViewOffsetX { get => (double)GetValue(ViewOffsetXProperty); set => SetValue(ViewOffsetXProperty, value); }
    public double ViewOffsetY { get => (double)GetValue(ViewOffsetYProperty); set => SetValue(ViewOffsetYProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public CropGuide Guide { get => (CropGuide)GetValue(GuideProperty); set => SetValue(GuideProperty, value); }
    public double AspectRatio { get => (double)GetValue(AspectRatioProperty); set => SetValue(AspectRatioProperty, value); }

    /// <summary>The crop box was dragged. Raised continuously, so the handler is
    /// expected to debounce.</summary>
    public event EventHandler? CropChanged;

    /// <summary>A rotate drag produced a new straighten angle. Reported rather
    /// than written, because settling the crop against a new angle is the
    /// view-model's business — see its crop baseline.</summary>
    public event EventHandler<double>? StraightenChanged;

    // ── Coordinate mapping ─────────────────────────────────────────────────
    // Extent pixels and preview-buffer pixels are the same unit: the extent is
    // just the bounding box the buffer sweeps out as it turns. So the mapping to
    // screen is the viewer's plain scale-about-centre, with no ratio in it.

    private bool HasImage =>
        IsActive && Geometry is not null &&
        SourceWidth > 0 && SourceHeight > 0 && ViewScale > 0;

    private (double w, double h) ExtentSize()
    {
        var g = Geometry!;
        var (ew, eh) = ImageGeometry.OutputSize(
            ImageGeometry.FullExtent(g, SourceWidth, SourceHeight), SourceWidth, SourceHeight);
        return (ew, eh);
    }

    /// <summary>The crop box in extent pixels.</summary>
    private Rect CropRect()
    {
        var (x, y, w, h) = ImageGeometry.CropRectInFullExtent(Geometry!, SourceWidth, SourceHeight);
        return new Rect(x, y, Math.Max(w, 1e-6), Math.Max(h, 1e-6));
    }

    private Point ExtentToScreen(double ex, double ey)
    {
        var (ew, eh) = ExtentSize();
        double s = ViewScale;
        return new Point(
            ActualWidth / 2 + (ex - ew / 2.0) * s + ViewOffsetX,
            ActualHeight / 2 + (ey - eh / 2.0) * s + ViewOffsetY);
    }

    private Point ScreenToExtent(Point p)
    {
        var (ew, eh) = ExtentSize();
        double s = ViewScale;
        if (s <= 0) return new Point(0, 0);
        return new Point(
            (p.X - ActualWidth / 2 - ViewOffsetX) / s + ew / 2.0,
            (p.Y - ActualHeight / 2 - ViewOffsetY) / s + eh / 2.0);
    }

    /// <summary>One screen pixel measured in extent pixels — how the constant
    /// screen-space sizes above get into the drag maths.</summary>
    private double ScreenUnit() => ViewScale > 1e-9 ? 1.0 / ViewScale : 1.0;

    private Rect ScreenBox()
    {
        var r = CropRect();
        return new Rect(ExtentToScreen(r.Left, r.Top), ExtentToScreen(r.Right, r.Bottom));
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        if (!HasImage) return;

        // Only hittable while live — declared above the mask overlay, an
        // always-on hit-test surface here would swallow every mask gesture.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var box = ScreenBox();
        if (box.Width <= 0 || box.Height <= 0) return;

        DrawScrim(dc, box);
        DrawGuides(dc, box);
        dc.DrawRectangle(null, BorderPen, box);
        DrawHandles(dc, box);
    }

    /// <summary>
    /// Dim everything outside the box, as four rectangles around it rather than
    /// a single clipped fill — cheaper, and it leaves the crop itself completely
    /// untouched so the exposure inside can still be judged.
    /// </summary>
    private void DrawScrim(DrawingContext dc, Rect box)
    {
        double w = ActualWidth, h = ActualHeight;
        double l = Math.Clamp(box.Left, 0, w), rr = Math.Clamp(box.Right, 0, w);
        double t = Math.Clamp(box.Top, 0, h), b = Math.Clamp(box.Bottom, 0, h);

        dc.DrawRectangle(ScrimFill, null, new Rect(0, 0, w, t));
        dc.DrawRectangle(ScrimFill, null, new Rect(0, b, w, Math.Max(0, h - b)));
        dc.DrawRectangle(ScrimFill, null, new Rect(0, t, l, Math.Max(0, b - t)));
        dc.DrawRectangle(ScrimFill, null, new Rect(rr, t, Math.Max(0, w - rr), Math.Max(0, b - t)));
    }

    private void DrawGuides(DrawingContext dc, Rect box)
    {
        switch (Guide)
        {
            case CropGuide.Thirds:
                Fractions(new[] { 1.0 / 3.0, 2.0 / 3.0 });
                break;
            case CropGuide.Golden:
                Fractions(new[] { 0.381966, 0.618034 });
                break;
            case CropGuide.Grid:
                Fractions(new[] { 0.125, 0.25, 0.375, 0.5, 0.625, 0.75, 0.875 });
                break;
            case CropGuide.Diagonal:
                dc.DrawLine(GuidePen, box.TopLeft, box.BottomRight);
                dc.DrawLine(GuidePen, box.TopRight, box.BottomLeft);
                break;
        }

        void Fractions(double[] fractions)
        {
            foreach (double f in fractions)
            {
                double x = box.Left + box.Width * f;
                double y = box.Top + box.Height * f;
                dc.DrawLine(GuidePen, new Point(x, box.Top), new Point(x, box.Bottom));
                dc.DrawLine(GuidePen, new Point(box.Left, y), new Point(box.Right, y));
            }
        }
    }

    /// <summary>
    /// Corner brackets and edge bars, drawn just inside the border. They sit
    /// inside rather than straddling it so that on a crop pushed right up to the
    /// edge of the frame the grips are still fully visible.
    /// </summary>
    private void DrawHandles(DrawingContext dc, Rect box)
    {
        double arm = Math.Min(CornerArm, Math.Min(box.Width, box.Height) * 0.35);
        double edge = Math.Min(EdgeArm, Math.Min(box.Width, box.Height) * 0.5);
        double t = HandleWeight;

        // Corners: two bars each, laid along the border from the corner inward.
        Bar(box.Left, box.Top, arm, t);
        Bar(box.Left, box.Top, t, arm);
        Bar(box.Right - arm, box.Top, arm, t);
        Bar(box.Right - t, box.Top, t, arm);
        Bar(box.Left, box.Bottom - t, arm, t);
        Bar(box.Left, box.Bottom - arm, t, arm);
        Bar(box.Right - arm, box.Bottom - t, arm, t);
        Bar(box.Right - t, box.Bottom - arm, t, arm);

        // Edge midpoints.
        double cx = box.Left + box.Width / 2, cy = box.Top + box.Height / 2;
        Bar(cx - edge / 2, box.Top, edge, t);
        Bar(cx - edge / 2, box.Bottom - t, edge, t);
        Bar(box.Left, cy - edge / 2, t, edge);
        Bar(box.Right - t, cy - edge / 2, t, edge);

        void Bar(double x, double y, double w, double h)
            => dc.DrawRectangle(HandleFill, null, new Rect(x, y, w, h));
    }

    // ── Hit testing ────────────────────────────────────────────────────────

    /// <summary>
    /// Which grip a screen point lands on. Anything clear of the box is
    /// <see cref="Grip.Rotate"/>: in the crop tool the surrounding margin is the
    /// rotation gesture, so dragging vertically beside the photo or horizontally
    /// above it turns the frame, exactly as it does in Lightroom.
    /// </summary>
    private Grip HitTest(Point screen)
    {
        var box = ScreenBox();

        bool nearL = Math.Abs(screen.X - box.Left) <= HitSlop;
        bool nearR = Math.Abs(screen.X - box.Right) <= HitSlop;
        bool nearT = Math.Abs(screen.Y - box.Top) <= HitSlop;
        bool nearB = Math.Abs(screen.Y - box.Bottom) <= HitSlop;
        bool withinX = screen.X >= box.Left - HitSlop && screen.X <= box.Right + HitSlop;
        bool withinY = screen.Y >= box.Top - HitSlop && screen.Y <= box.Bottom + HitSlop;

        // Corners before edges, so the two-axis grip wins where they overlap.
        if (nearL && nearT) return Grip.TopLeft;
        if (nearR && nearT) return Grip.TopRight;
        if (nearL && nearB) return Grip.BottomLeft;
        if (nearR && nearB) return Grip.BottomRight;
        if (nearL && withinY) return Grip.Left;
        if (nearR && withinY) return Grip.Right;
        if (nearT && withinX) return Grip.Top;
        if (nearB && withinX) return Grip.Bottom;

        return box.Contains(screen) ? Grip.Move : Grip.Rotate;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!HasImage) return;

        var screen = e.GetPosition(this);
        _grip = HitTest(screen);

        if (_grip == Grip.Rotate)
        {
            var box = ScreenBox();
            _rotatePivot = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
            _rotateStartPointer = PointerAngle(screen);
            _rotateStartAngle = Geometry!.Straighten;
        }
        else
        {
            _dragStart = ScreenToExtent(screen);
            _dragStartRect = CropRect();
        }

        CaptureMouse();
        e.Handled = true;
    }

    private double PointerAngle(Point screen)
        => Math.Atan2(screen.Y - _rotatePivot.Y, screen.X - _rotatePivot.X) * 180.0 / Math.PI;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!HasImage) return;

        var screen = e.GetPosition(this);
        if (_grip == Grip.None)
        {
            Cursor = HitTest(screen) switch
            {
                Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
                Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
                Grip.Left or Grip.Right => Cursors.SizeWE,
                Grip.Top or Grip.Bottom => Cursors.SizeNS,
                Grip.Move => Cursors.SizeAll,
                Grip.Rotate => Cursors.Hand,
                _ => Cursors.Arrow,
            };
            return;
        }

        if (_grip == Grip.Rotate)
        {
            double delta = Normalise(PointerAngle(screen) - _rotateStartPointer);
            double angle = Math.Clamp(_rotateStartAngle + delta,
                                      -GeometrySettings.MaxStraighten, GeometrySettings.MaxStraighten);
            StraightenChanged?.Invoke(this, angle);
        }
        else
        {
            var p = ScreenToExtent(screen);
            if (TryApply(p.X - _dragStart.X, p.Y - _dragStart.Y))
            {
                InvalidateVisual();
                CropChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        e.Handled = true;
    }

    private static double Normalise(double degrees)
        => ((degrees + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_grip == Grip.None) return;

        bool wasRotating = _grip == Grip.Rotate;
        _grip = Grip.None;
        ReleaseMouseCapture();
        if (!wasRotating) CropChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>
    /// Resolve a drag delta into a new crop box and commit it if it is legal.
    ///
    /// <para><b>Illegal drags are dropped, not corrected.</b> The box is tested
    /// against the photo through <see cref="ImageGeometry.CropFits"/> and simply
    /// not committed when a corner would fall off — so pushing against the edge
    /// of a straightened frame stops dead there instead of the box sliding or
    /// shrinking out from under the cursor.</para>
    /// </summary>
    private bool TryApply(double dx, double dy)
    {
        var g = Geometry!;
        var r = _dragStartRect;
        double minSize = MinCropPx * ScreenUnit();
        double aspect = AspectRatio;

        double l = r.Left, t = r.Top, right = r.Right, b = r.Bottom;

        if (_grip == Grip.Move)
        {
            l += dx; right += dx; t += dy; b += dy;
        }
        else
        {
            if (_grip is Grip.Left or Grip.TopLeft or Grip.BottomLeft) l = Math.Min(l + dx, right - minSize);
            if (_grip is Grip.Right or Grip.TopRight or Grip.BottomRight) right = Math.Max(right + dx, l + minSize);
            if (_grip is Grip.Top or Grip.TopLeft or Grip.TopRight) t = Math.Min(t + dy, b - minSize);
            if (_grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight) b = Math.Max(b + dy, t + minSize);

            if (aspect > 0.0)
                (l, t, right, b) = ConstrainAspect(l, t, right, b, aspect, minSize);
        }

        var candidate = g.Clone();
        ImageGeometry.SetCropFromFullExtent(
            candidate, l, t, right - l, b - t, SourceWidth, SourceHeight);
        if (!ImageGeometry.CropFits(candidate, SourceWidth, SourceHeight)) return false;

        g.CropX = candidate.CropX;
        g.CropY = candidate.CropY;
        g.CropWidth = candidate.CropWidth;
        g.CropHeight = candidate.CropHeight;
        return true;
    }

    /// <summary>
    /// Force the box back onto the locked ratio, holding the edge or corner the
    /// user is <i>not</i> dragging. A corner drives the shape from whichever
    /// axis moved further, so the box tracks the diagonal rather than jumping
    /// between two solutions as the cursor crosses it.
    /// </summary>
    private (double l, double t, double r, double b) ConstrainAspect(
        double l, double t, double r, double b, double aspect, double minSize)
    {
        double w = r - l, h = b - t;
        bool drivenByWidth = _grip switch
        {
            Grip.Left or Grip.Right => true,
            Grip.Top or Grip.Bottom => false,
            _ => w / aspect >= h,
        };

        if (drivenByWidth) h = Math.Max(w / aspect, minSize);
        else w = Math.Max(h * aspect, minSize);

        // Grow away from the anchored side: the edge opposite the one held.
        bool anchorLeft = _grip is Grip.Right or Grip.TopRight or Grip.BottomRight;
        bool anchorTop = _grip is Grip.Bottom or Grip.BottomLeft or Grip.BottomRight;
        bool freeX = _grip is Grip.Top or Grip.Bottom;
        bool freeY = _grip is Grip.Left or Grip.Right;

        double cx = (l + r) / 2, cy = (t + b) / 2;
        if (freeX) { l = cx - w / 2; r = cx + w / 2; }
        else if (anchorLeft) r = l + w;
        else l = r - w;

        if (freeY) { t = cy - h / 2; b = cy + h / 2; }
        else if (anchorTop) b = t + h;
        else t = b - h;

        return (l, t, r, b);
    }
}
