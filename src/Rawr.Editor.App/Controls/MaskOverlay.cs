using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Develop;

namespace Rawr.Editor.App.Controls;

/// <summary>
/// The on-canvas editor for masks: draws each mask's shape over the photo and
/// handles the drags that create, move, resize and rotate it. Radial gradients
/// draw as an ellipse with grab handles; linear gradients as the three parallel
/// lines of their ramp; a brush has no outline at all — a cursor ring shows where
/// the next dab lands and the red tint shows what has been painted.
///
/// <para><b>It draws in screen space, not image space.</b> The obvious
/// implementation — put the shape in image coordinates and let it inherit the
/// viewer's zoom transform — scales the stroke and the grab handles along with
/// the photo, so at 400% the handles become fat blobs and at Fit they shrink to
/// nothing. Here the geometry is converted to screen coordinates on every render
/// pass instead, which keeps the outline one pixel wide and the handles the same
/// comfortable size at every zoom level.</para>
///
/// <para><b>It only claims the mouse when it has something to do with it.</b>
/// Presses that miss every handle and every mask are left unhandled, so they
/// bubble to the viewer and pan the photo as usual. That is what lets masking
/// stay switched on while you navigate, instead of forcing a modal tool.</para>
///
/// <para>The control talks in <see cref="MaskSettings"/> — the model type, not a
/// view-model — so it carries no knowledge of the panel that owns the list. It
/// reports edits by raising <see cref="GeometryChanged"/>; scheduling a re-render
/// is the caller's business.</para>
/// </summary>
public sealed class MaskOverlay : FrameworkElement
{
    // Screen-space sizes, deliberately constant across zoom levels.
    private const double HandleRadius = 4.5;
    private const double HandleHitSlop = 9.0;
    private const double RotationHandleGap = 26.0;
    private const double LinearRotateOffset = 58.0;
    private const double MinRadiusPx = 6.0;
    private const double MinLengthPx = 4.0;

    /// <summary>How far the cursor must travel, as a fraction of the brush radius,
    /// before another point joins the stroke. The spine is rendered as a distance
    /// field rather than as stamped dabs, so this only trades stored points against
    /// how faithfully a tight curve is followed — it cannot bead the stroke.</summary>
    private const double StrokeSpacing = 0.15;

    /// <summary>Downsampled width of the red mask-tint bitmap. The tint is a soft
    /// falloff with no fine structure, so rasterising it at preview resolution
    /// would cost several megapixels per drag to show something a few hundred
    /// pixels wide could say just as well; WPF scales it up smoothly.</summary>
    private const int TintWidth = 360;

    private static readonly Pen OutlinePen = MakePen(Color.FromRgb(0xF2, 0xF4, 0xF7), 1.4);
    private static readonly Pen OutlineShadowPen = MakePen(Color.FromArgb(0x99, 0, 0, 0), 3.0);
    private static readonly Pen UnselectedPen = MakePen(Color.FromArgb(0x99, 0xF2, 0xF4, 0xF7), 1.2);
    private static readonly Pen FeatherPen = MakeDashedPen(Color.FromArgb(0xAA, 0xF2, 0xF4, 0xF7), 1.0);
    private static readonly Brush HandleFill = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF4, 0xF7)));
    private static readonly Brush CenterFill = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xF2, 0xF4, 0xF7)));
    private static readonly Pen HandleStroke = MakePen(Color.FromArgb(0xCC, 0x10, 0x12, 0x16), 1.0);

    private static Pen MakePen(Color c, double thickness)
        => Freeze(new Pen(Freeze(new SolidColorBrush(c)), thickness));

    private static Pen MakeDashedPen(Color c, double thickness)
    {
        var pen = new Pen(Freeze(new SolidColorBrush(c)), thickness)
        {
            DashStyle = new DashStyle(new double[] { 4, 4 }, 0),
        };
        return Freeze(pen);
    }

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    // A FrameworkElement is invisible to hit testing wherever it has not
    // painted, and a mask is drawn as an outline rather than a fill — so
    // OnRender lays down a transparent rectangle over the whole surface to make
    // the element hittable, and each press then decides for itself whether to
    // take the gesture or let it bubble to the viewer's pan handler.
    private static readonly Brush HitTestFill = Brushes.Transparent;

    /// <summary>Which part of a mask a press landed on.</summary>
    private enum Grip
    {
        None,
        Move,
        // Radial and rectangle
        Left, Right, Top, Bottom, Rotate,
        // Rectangle corners — resize both axes at once.
        TopLeft, TopRight, BottomLeft, BottomRight,
        // Linear: the full-effect line, the zero-effect line, and its rotator.
        LinearFull, LinearZero, LinearRotate,
        // Brush: the drag is a stroke rather than a change to any handle.
        Paint,
    }

    private Grip _grip = Grip.None;
    private MaskSettings? _dragMask;

    /// <summary>The stroke being laid down, or null when not painting. Held rather
    /// than looked up so a mouse move appends to the stroke this gesture started
    /// even if the selection changes underneath it.</summary>
    private BrushStroke? _stroke;

    /// <summary>Where the pointer is, for the brush cursor ring. Only meaningful
    /// while <see cref="_cursorInside"/>.</summary>
    private Point _cursor;
    private bool _cursorInside;

    /// <summary>Whether the drag in progress is the one that created the mask.
    /// Tracked here rather than read from <see cref="IsCreating"/> during the
    /// drag: the handler for <see cref="CreateRequested"/> disarms that flag as
    /// soon as the mask exists, which is halfway through this same gesture.</summary>
    private bool _creatingDrag;

    private Point _dragStartImage;
    private double _dragStartCx, _dragStartCy, _dragStartRx, _dragStartRy, _dragStartRotation;
    private double _dragAnchorD;   // linear: the line held fixed while the other moves

    private BitmapSource? _tint;
    private MaskSettings? _tintFor;
    private string _tintKey = "";

    // ── Bound state ────────────────────────────────────────────────────────

    public static readonly DependencyProperty MasksProperty =
        DependencyProperty.Register(nameof(Masks), typeof(IEnumerable<MaskSettings>), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectedMaskProperty =
        DependencyProperty.Register(nameof(SelectedMask), typeof(MaskSettings), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageWidthProperty =
        DependencyProperty.Register(nameof(ImageWidth), typeof(int), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ImageHeightProperty =
        DependencyProperty.Register(nameof(ImageHeight), typeof(int), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Absolute image-pixel → screen-pixel scale (fit × user zoom).</summary>
    public static readonly DependencyProperty ViewScaleProperty =
        DependencyProperty.Register(nameof(ViewScale), typeof(double), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewOffsetXProperty =
        DependencyProperty.Register(nameof(ViewOffsetX), typeof(double), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ViewOffsetYProperty =
        DependencyProperty.Register(nameof(ViewOffsetY), typeof(double), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Whether the overlay is live at all. Off, it neither draws nor
    /// takes any mouse input, so the viewer behaves exactly as it did before
    /// masks existed.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(nameof(IsActive), typeof(bool), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Armed by the panel's "new mask" buttons: the next press on empty
    /// canvas creates a mask and drags out its geometry.</summary>
    public static readonly DependencyProperty IsCreatingProperty =
        DependencyProperty.Register(nameof(IsCreating), typeof(bool), typeof(MaskOverlay),
            new PropertyMetadata(false));

    /// <summary>Paint the selected mask's weight field over the photo in red —
    /// Lightroom's "Show Overlay". The outline says where the shape is; only
    /// this says what the falloff is actually doing.</summary>
    public static readonly DependencyProperty ShowTintProperty =
        DependencyProperty.Register(nameof(ShowTint), typeof(bool), typeof(MaskOverlay),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<MaskSettings>? Masks
    {
        get => (IEnumerable<MaskSettings>?)GetValue(MasksProperty);
        set => SetValue(MasksProperty, value);
    }

    public MaskSettings? SelectedMask
    {
        get => (MaskSettings?)GetValue(SelectedMaskProperty);
        set => SetValue(SelectedMaskProperty, value);
    }

    public int ImageWidth { get => (int)GetValue(ImageWidthProperty); set => SetValue(ImageWidthProperty, value); }
    public int ImageHeight { get => (int)GetValue(ImageHeightProperty); set => SetValue(ImageHeightProperty, value); }
    public double ViewScale { get => (double)GetValue(ViewScaleProperty); set => SetValue(ViewScaleProperty, value); }
    public double ViewOffsetX { get => (double)GetValue(ViewOffsetXProperty); set => SetValue(ViewOffsetXProperty, value); }
    public double ViewOffsetY { get => (double)GetValue(ViewOffsetYProperty); set => SetValue(ViewOffsetYProperty, value); }
    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool IsCreating { get => (bool)GetValue(IsCreatingProperty); set => SetValue(IsCreatingProperty, value); }
    public bool ShowTint { get => (bool)GetValue(ShowTintProperty); set => SetValue(ShowTintProperty, value); }

    /// <summary>A mask's geometry was edited. Raised continuously during a drag,
    /// so the handler is expected to debounce.</summary>
    public event EventHandler? GeometryChanged;

    /// <summary>The user clicked an existing mask. The argument is the mask that
    /// should become selected.</summary>
    public event EventHandler<MaskSettings>? MaskPicked;

    /// <summary>The user began a create drag at the given normalised point. The
    /// handler must add a mask and set <see cref="SelectedMask"/> to it
    /// <i>synchronously</i> — the same gesture goes on to drag out its
    /// geometry, and the shape it drags depends on which kind was added.</summary>
    public event EventHandler<Point>? CreateRequested;

    // ── Coordinate mapping ─────────────────────────────────────────────────
    // Mirrors MainWindow's viewer transform exactly: the Image is centred in the
    // host by Stretch=Uniform, then scaled about the host centre and translated.
    // Anything that changes there has to change here or the outline drifts off
    // the photo it is supposed to be describing.

    private Point ImageToScreen(double px, double py)
    {
        double s = ViewScale;
        return new Point(
            ActualWidth / 2 + (px - ImageWidth / 2.0) * s + ViewOffsetX,
            ActualHeight / 2 + (py - ImageHeight / 2.0) * s + ViewOffsetY);
    }

    private Point ScreenToImage(Point p)
    {
        double s = ViewScale;
        if (s <= 0) return new Point(0, 0);
        return new Point(
            (p.X - ActualWidth / 2 - ViewOffsetX) / s + ImageWidth / 2.0,
            (p.Y - ActualHeight / 2 - ViewOffsetY) / s + ImageHeight / 2.0);
    }

    private bool HasImage => ImageWidth > 0 && ImageHeight > 0 && ViewScale > 0;

    /// <summary>Radial geometry in image pixels. Both radii are normalised to
    /// <i>width</i> (see <see cref="RadialMask"/>), which is what lets the screen
    /// projection stay a plain uniform scale with no aspect correction.</summary>
    private (double cx, double cy, double rx, double ry) PixelGeometry(RadialMask m) =>
        (m.CenterX * ImageWidth, m.CenterY * ImageHeight,
         Math.Max(m.RadiusX, RadialMask.MinRadius) * ImageWidth,
         Math.Max(m.RadiusY, RadialMask.MinRadius) * ImageWidth);

    /// <summary>Rectangle geometry in image pixels. Both half-extents are
    /// normalised to <i>width</i> (see <see cref="RectangleMask"/>), so the screen
    /// projection stays a plain uniform scale with no aspect correction.</summary>
    private (double cx, double cy, double hw, double hh) PixelGeometry(RectangleMask m) =>
        (m.CenterX * ImageWidth, m.CenterY * ImageHeight,
         Math.Max(m.HalfWidth, RectangleMask.MinExtent) * ImageWidth,
         Math.Max(m.HalfHeight, RectangleMask.MinExtent) * ImageWidth);

    /// <summary>Linear geometry in image pixels: centre, unit fade direction and
    /// half the full-to-zero distance.</summary>
    private (double cx, double cy, double dx, double dy, double half) PixelGeometry(LinearGradientMask m)
    {
        double t = m.Angle * Math.PI / 180.0;
        return (m.CenterX * ImageWidth, m.CenterY * ImageHeight,
                Math.Cos(t), Math.Sin(t),
                Math.Max(m.Length, LinearGradientMask.MinLength) * ImageWidth * 0.5);
    }

    /// <summary>Rotate an image-space vector into a frame rotated by the angle.</summary>
    private static Point ToLocal(double dx, double dy, double rotationDeg)
    {
        double t = rotationDeg * Math.PI / 180.0;
        double c = Math.Cos(t), s = Math.Sin(t);
        return new Point(dx * c + dy * s, -dx * s + dy * c);
    }

    // ── Rendering ──────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(HitTestFill, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (!IsActive || !HasImage || Masks is null) return;

        var selected = SelectedMask;

        // A stroke in progress always shows its tint, whatever the checkbox says:
        // a brush is the one mask you cannot place by watching an outline, so
        // painting with the overlay off would be painting blind.
        if (selected is not null && (ShowTint || _grip == Grip.Paint)) DrawTint(dc, selected);

        foreach (var mask in Masks)
        {
            if (!mask.IsEnabled || ReferenceEquals(mask, selected)) continue;
            // A brush has no outline to draw — its shape *is* the tint, which only
            // the selected mask shows.
            if (mask.IsBrush) continue;
            if (mask.IsLinear) DrawLinear(dc, mask.Linear, selectedShape: false);
            else if (mask.IsRectangle) DrawRectangleOutline(dc, mask.Rectangle, UnselectedPen, null);
            else DrawRadialOutline(dc, mask.Radial, UnselectedPen, null);
        }

        if (selected is not null && selected.IsEnabled)
        {
            if (selected.IsBrush) DrawBrushCursor(dc, selected.Brush);
            else if (selected.IsLinear) DrawLinear(dc, selected.Linear, selectedShape: true);
            else if (selected.IsRectangle) DrawRectangleSelected(dc, selected.Rectangle);
            else DrawRadialSelected(dc, selected.Radial);
        }
    }

    /// <summary>
    /// The brush's footprint under the pointer: an outer ring at the radius the
    /// next stroke will reach, and a dashed inner ring where its falloff begins —
    /// the same pairing the radial uses for its feather, and the only way to judge
    /// a brush size before committing a stroke to the photo.
    ///
    /// <para>Drawn from the live stroke's own size while one is in progress, so
    /// nudging the Size slider mid-drag does not resize the ring under a stroke it
    /// cannot affect.</para>
    /// </summary>
    private void DrawBrushCursor(DrawingContext dc, BrushMask brush)
    {
        if (!_cursorInside) return;

        double size = _stroke?.Size ?? brush.Size;
        double r = Math.Clamp(size, BrushMask.MinSize, BrushMask.MaxSize) * ImageWidth * ViewScale;
        if (r <= 0.5) return;

        dc.DrawEllipse(null, OutlineShadowPen, _cursor, r, r);
        dc.DrawEllipse(null, OutlinePen, _cursor, r, r);

        double core = r * BrushMask.CoreFraction;
        if (core > 2.0) dc.DrawEllipse(null, FeatherPen, _cursor, core, core);
    }

    private void DrawRadialOutline(DrawingContext dc, RadialMask m, Pen pen, Pen? shadow)
    {
        var (cx, cy, rx, ry) = PixelGeometry(m);
        var center = ImageToScreen(cx, cy);
        double s = ViewScale;

        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(m.Rotation));
        if (shadow is not null) dc.DrawEllipse(null, shadow, new Point(0, 0), rx * s, ry * s);
        dc.DrawEllipse(null, pen, new Point(0, 0), rx * s, ry * s);
        dc.Pop();
        dc.Pop();
    }

    private void DrawRadialSelected(DrawingContext dc, RadialMask m)
    {
        var (cx, cy, rx, ry) = PixelGeometry(m);
        var center = ImageToScreen(cx, cy);
        double s = ViewScale;
        double sx = rx * s, sy = ry * s;

        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(m.Rotation));

        dc.DrawEllipse(null, OutlineShadowPen, new Point(0, 0), sx, sy);
        dc.DrawEllipse(null, OutlinePen, new Point(0, 0), sx, sy);

        // The inner ring is where the falloff begins — the boundary of the
        // fully-applied core. Without it the feather is invisible until you
        // switch the tint on, and Feather is the slider people reach for most.
        double inner = 1.0 - Math.Clamp(m.Feather, 0.0, 100.0) / 100.0;
        if (inner > 0.02) dc.DrawEllipse(null, FeatherPen, new Point(0, 0), sx * inner, sy * inner);

        var stalkEnd = new Point(0, -(sy + RotationHandleGap));
        dc.DrawLine(OutlineShadowPen, new Point(0, -sy), stalkEnd);
        dc.DrawLine(OutlinePen, new Point(0, -sy), stalkEnd);
        dc.DrawEllipse(HandleFill, HandleStroke, stalkEnd, HandleRadius, HandleRadius);

        foreach (var p in new[]
                 {
                     new Point(-sx, 0), new Point(sx, 0),
                     new Point(0, -sy), new Point(0, sy),
                 })
            dc.DrawEllipse(HandleFill, HandleStroke, p, HandleRadius, HandleRadius);

        dc.DrawEllipse(CenterFill, HandleStroke, new Point(0, 0), HandleRadius, HandleRadius);

        dc.Pop();
        dc.Pop();
    }

    private void DrawRectangleOutline(DrawingContext dc, RectangleMask m, Pen pen, Pen? shadow)
    {
        var (cx, cy, hw, hh) = PixelGeometry(m);
        var center = ImageToScreen(cx, cy);
        double s = ViewScale;
        var box = new Rect(-hw * s, -hh * s, hw * s * 2, hh * s * 2);

        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(m.Rotation));
        if (shadow is not null) dc.DrawRectangle(null, shadow, box);
        dc.DrawRectangle(null, pen, box);
        dc.Pop();
        dc.Pop();
    }

    private void DrawRectangleSelected(DrawingContext dc, RectangleMask m)
    {
        var (cx, cy, hw, hh) = PixelGeometry(m);
        var center = ImageToScreen(cx, cy);
        double s = ViewScale;
        double sx = hw * s, sy = hh * s;
        var box = new Rect(-sx, -sy, sx * 2, sy * 2);

        dc.PushTransform(new TranslateTransform(center.X, center.Y));
        dc.PushTransform(new RotateTransform(m.Rotation));

        dc.DrawRectangle(null, OutlineShadowPen, box);
        dc.DrawRectangle(null, OutlinePen, box);

        // The inner rectangle is where the falloff begins — the boundary of the
        // fully-applied core, the rectangle's equivalent of the radial's inner
        // ring, and invisible otherwise until the tint is switched on.
        double inner = 1.0 - Math.Clamp(m.Feather, 0.0, 100.0) / 100.0;
        if (inner > 0.02)
            dc.DrawRectangle(null, FeatherPen, new Rect(-sx * inner, -sy * inner, sx * inner * 2, sy * inner * 2));

        var stalkEnd = new Point(0, -(sy + RotationHandleGap));
        dc.DrawLine(OutlineShadowPen, new Point(0, -sy), stalkEnd);
        dc.DrawLine(OutlinePen, new Point(0, -sy), stalkEnd);
        dc.DrawEllipse(HandleFill, HandleStroke, stalkEnd, HandleRadius, HandleRadius);

        // Resize handles: the four edge midpoints (one axis each, as the radial
        // has) plus the four corners, which drag both axes at once.
        foreach (var p in new[]
                 {
                     new Point(-sx, 0), new Point(sx, 0),
                     new Point(0, -sy), new Point(0, sy),
                     new Point(-sx, -sy), new Point(sx, -sy),
                     new Point(-sx, sy), new Point(sx, sy),
                 })
            dc.DrawEllipse(HandleFill, HandleStroke, p, HandleRadius, HandleRadius);

        dc.DrawEllipse(CenterFill, HandleStroke, new Point(0, 0), HandleRadius, HandleRadius);

        dc.Pop();
        dc.Pop();
    }

    /// <summary>
    /// The three lines of a linear ramp: full effect, midpoint, zero effect.
    ///
    /// <para>They are mathematically infinite, so they are drawn as segments long
    /// enough to leave the viewport in both directions and left to the host's
    /// clipping — which is both simpler and more honest than picking arbitrary
    /// endpoints that would imply the gradient stops there.</para>
    /// </summary>
    private void DrawLinear(DrawingContext dc, LinearGradientMask m, bool selectedShape)
    {
        var (cx, cy, dx, dy, half) = PixelGeometry(m);
        var center = ImageToScreen(cx, cy);
        double s = ViewScale;
        double halfScreen = half * s;

        // Along the lines is perpendicular to the fade direction.
        double px = -dy, py = dx;
        double reach = (ActualWidth + ActualHeight) * 2.0;

        void Line(double offset, Pen pen, Pen? shadow)
        {
            var mid = new Point(center.X + dx * offset, center.Y + dy * offset);
            var a = new Point(mid.X - px * reach, mid.Y - py * reach);
            var b = new Point(mid.X + px * reach, mid.Y + py * reach);
            if (shadow is not null) dc.DrawLine(shadow, a, b);
            dc.DrawLine(pen, a, b);
        }

        if (!selectedShape)
        {
            Line(0, UnselectedPen, null);
            return;
        }

        Line(-halfScreen, FeatherPen, OutlineShadowPen);
        Line(halfScreen, FeatherPen, OutlineShadowPen);
        Line(0, OutlinePen, OutlineShadowPen);

        // Rotation handle, offset along the centre line so it cannot be confused
        // with the move region that runs the length of that line.
        var rotate = new Point(center.X + px * LinearRotateOffset, center.Y + py * LinearRotateOffset);
        dc.DrawEllipse(HandleFill, HandleStroke, rotate, HandleRadius, HandleRadius);
        dc.DrawEllipse(CenterFill, HandleStroke, center, HandleRadius, HandleRadius);
    }

    /// <summary>
    /// Paint the mask's weight field as a red wash. Rasterised at
    /// <see cref="TintWidth"/> and cached against the geometry that produced it,
    /// so dragging a slider that does not change the shape does not rebuild it.
    /// </summary>
    private void DrawTint(DrawingContext dc, MaskSettings mask)
    {
        var topLeft = ImageToScreen(0, 0);
        var bottomRight = ImageToScreen(ImageWidth, ImageHeight);
        var rect = new Rect(topLeft, bottomRight);
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var bitmap = GetTint(mask);
        if (bitmap is not null) dc.DrawImage(bitmap, rect);
    }

    private BitmapSource? GetTint(MaskSettings mask)
    {
        var r = mask.Radial;
        var l = mask.Linear;
        var q = mask.Rectangle;
        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{mask.Kind}|{r.CenterX:F5},{r.CenterY:F5},{r.RadiusX:F5},{r.RadiusY:F5},{r.Rotation:F3},{r.Feather:F2},{r.Invert}|" +
            $"{l.CenterX:F5},{l.CenterY:F5},{l.Angle:F3},{l.Length:F5},{l.Invert}|" +
            $"{q.CenterX:F5},{q.CenterY:F5},{q.HalfWidth:F5},{q.HalfHeight:F5},{q.Rotation:F3},{q.Feather:F2},{q.Invert}|" +
            $"{BrushKey(mask.Brush)}|{ImageWidth}x{ImageHeight}");
        if (_tint is not null && ReferenceEquals(_tintFor, mask) && _tintKey == key) return _tint;

        int w = Math.Min(TintWidth, Math.Max(1, ImageWidth));
        int h = Math.Max(1, (int)Math.Round(w * (double)ImageHeight / ImageWidth));

        // The shapes are normalised, so rasterising against a smaller canvas of
        // the same aspect gives the identical field — no rescaling of the
        // geometry is needed, which is the point of storing it normalised.
        var weights = mask.Weights(w, h, PixelRect.Full(w, h));

        var pixels = new byte[w * h * 4];   // Bgra32, premultiplied
        for (int i = 0; i < weights.Length; i++)
        {
            // Cap the wash well below opaque: it has to read as "this region is
            // selected" while the photo underneath stays judgeable.
            byte a = (byte)(Math.Clamp(weights[i], 0f, 1f) * 110f);
            int o = i * 4;
            pixels[o] = 0;                    // B
            pixels[o + 1] = 0;                // G
            pixels[o + 2] = (byte)(a * 0.90); // R, premultiplied by alpha
            pixels[o + 3] = a;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        bmp.Freeze();

        _tint = bmp;
        _tintFor = mask;
        _tintKey = key;
        return bmp;
    }

    /// <summary>
    /// A cheap fingerprint of the painted strokes for the tint cache.
    ///
    /// <para>Stroke and point counts plus the newest point is enough because
    /// strokes are only ever <i>appended</i> to — every edit the overlay makes
    /// either adds a point to the live stroke or starts a new one, so any of those
    /// moves one of these three numbers. Hashing every point instead would cost
    /// more per mouse move than re-rasterising the 360 px tint it is guarding.</para>
    /// </summary>
    private static string BrushKey(BrushMask brush)
    {
        int points = 0;
        foreach (var stroke in brush.Strokes) points += stroke.Points.Count;

        var last = brush.Strokes.Count > 0 ? brush.Strokes[^1] : null;
        var tip = last is { Points.Count: > 0 } ? last.Points[^1] : default;

        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{brush.Strokes.Count},{points},{tip.X:F5},{tip.Y:F5},{brush.Invert}");
    }

    // ── Hit testing ────────────────────────────────────────────────────────

    private Grip HitTest(MaskSettings mask, Point screen, bool includeHandles)
        => mask.Kind switch
        {
            MaskKind.Linear => HitTestLinear(mask.Linear, screen, includeHandles),
            MaskKind.Rectangle => HitTestRectangle(mask.Rectangle, screen, includeHandles),
            // A brush has no grips: there is nothing to move, resize or rotate, and
            // a press on it is a stroke — which OnMouseLeftButtonDown decides
            // before it ever gets here. Never claiming a press also keeps a painted
            // mask from swallowing clicks meant for a shape lying underneath it.
            MaskKind.Brush => Grip.None,
            _ => HitTestRadial(mask.Radial, screen, includeHandles),
        };

    /// <summary>
    /// Which grip a screen point lands on for a radial. Handles are tested before
    /// the interior so the resize grips on the outline stay reachable rather than
    /// being swallowed by the move region behind them.
    /// </summary>
    private Grip HitTestRadial(RadialMask m, Point screen, bool includeHandles)
    {
        var (cx, cy, rx, ry) = PixelGeometry(m);
        double s = ViewScale;
        var center = ImageToScreen(cx, cy);
        double sx = rx * s, sy = ry * s;

        var d = ToLocal(screen.X - center.X, screen.Y - center.Y, m.Rotation);

        if (includeHandles)
        {
            if (Near(d, 0, -(sy + RotationHandleGap))) return Grip.Rotate;
            if (Near(d, -sx, 0)) return Grip.Left;
            if (Near(d, sx, 0)) return Grip.Right;
            if (Near(d, 0, -sy)) return Grip.Top;
            if (Near(d, 0, sy)) return Grip.Bottom;
        }

        if (sx <= 0 || sy <= 0) return Grip.None;
        double u = d.X / sx, v = d.Y / sy;
        return u * u + v * v <= 1.0 ? Grip.Move : Grip.None;

        static bool Near(Point p, double x, double y)
            => Math.Abs(p.X - x) <= HandleHitSlop && Math.Abs(p.Y - y) <= HandleHitSlop;
    }

    /// <summary>
    /// Which grip a screen point lands on for a rectangle. Mirrors the radial: the
    /// edge-midpoint resize handles and the rotate stalk are tested first, then the
    /// interior claims the move — but the interior test is the rectangle's own, a
    /// pair of half-extent bounds rather than the ellipse's radial distance.
    /// </summary>
    private Grip HitTestRectangle(RectangleMask m, Point screen, bool includeHandles)
    {
        var (cx, cy, hw, hh) = PixelGeometry(m);
        double s = ViewScale;
        var center = ImageToScreen(cx, cy);
        double sx = hw * s, sy = hh * s;

        var d = ToLocal(screen.X - center.X, screen.Y - center.Y, m.Rotation);

        if (includeHandles)
        {
            if (Near(d, 0, -(sy + RotationHandleGap))) return Grip.Rotate;
            // Corners first: they sit where two edges meet, so testing them ahead
            // of the midpoints keeps them from being swallowed by an edge grip.
            if (Near(d, -sx, -sy)) return Grip.TopLeft;
            if (Near(d, sx, -sy)) return Grip.TopRight;
            if (Near(d, -sx, sy)) return Grip.BottomLeft;
            if (Near(d, sx, sy)) return Grip.BottomRight;
            if (Near(d, -sx, 0)) return Grip.Left;
            if (Near(d, sx, 0)) return Grip.Right;
            if (Near(d, 0, -sy)) return Grip.Top;
            if (Near(d, 0, sy)) return Grip.Bottom;
        }

        if (sx <= 0 || sy <= 0) return Grip.None;
        return Math.Abs(d.X) <= sx && Math.Abs(d.Y) <= sy ? Grip.Move : Grip.None;

        static bool Near(Point p, double x, double y)
            => Math.Abs(p.X - x) <= HandleHitSlop && Math.Abs(p.Y - y) <= HandleHitSlop;
    }

    /// <summary>
    /// Which grip a screen point lands on for a linear gradient.
    ///
    /// <para>Unlike a radial there is no interior to grab: the shape is three
    /// lines, and anywhere else on the photo belongs to the viewer. Grabbing is
    /// by nearest line rather than by testing each in a fixed order, so a short
    /// gradient whose lines sit within a few pixels of each other still resolves
    /// to whichever one the user actually aimed at.</para>
    /// </summary>
    private Grip HitTestLinear(LinearGradientMask m, Point screen, bool includeHandles)
    {
        var (cx, cy, dx, dy, half) = PixelGeometry(m);
        var center = ImageToScreen(cx, cy);
        double s = ViewScale;
        double halfScreen = half * s;

        if (includeHandles)
        {
            double px = -dy, py = dx;
            var rotate = new Point(center.X + px * LinearRotateOffset, center.Y + py * LinearRotateOffset);
            if (Math.Abs(screen.X - rotate.X) <= HandleHitSlop &&
                Math.Abs(screen.Y - rotate.Y) <= HandleHitSlop)
                return Grip.LinearRotate;
        }

        // Signed distance from the centre line, along the fade direction.
        double d = (screen.X - center.X) * dx + (screen.Y - center.Y) * dy;

        double toCenter = Math.Abs(d);
        double toFull = Math.Abs(d + halfScreen);
        double toZero = Math.Abs(d - halfScreen);

        double nearest = Math.Min(toCenter, Math.Min(toFull, toZero));
        if (nearest > HandleHitSlop) return Grip.None;

        if (nearest == toCenter) return Grip.Move;
        if (!includeHandles) return Grip.Move;
        return nearest == toFull ? Grip.LinearFull : Grip.LinearZero;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsActive || !HasImage) return;

        var screen = e.GetPosition(this);

        // A selected brush owns the canvas. Once a paint tool is up every press is
        // a stroke — the alternative, letting a press near another mask's outline
        // select it instead, means the brush drops strokes at exactly the moment
        // the user is painting over an existing adjustment. Other masks are still
        // reachable from the list, and the middle button still pans.
        if (!IsCreating && SelectedMask is { IsEnabled: true, IsBrush: true } brushMask)
        {
            BeginStroke(brushMask, screen, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            e.Handled = true;
            return;
        }

        // The selected mask gets first refusal, including its handles — otherwise
        // an overlapping mask drawn later would steal its grips.
        var selected = SelectedMask;
        if (selected is not null && selected.IsEnabled)
        {
            var grip = HitTest(selected, screen, includeHandles: true);
            if (grip != Grip.None)
            {
                BeginDrag(selected, grip, screen);
                e.Handled = true;
                return;
            }
        }

        // Then any other mask — a click to select it.
        if (Masks is not null)
        {
            foreach (var mask in Masks)
            {
                if (!mask.IsEnabled || ReferenceEquals(mask, selected)) continue;
                if (HitTest(mask, screen, includeHandles: false) == Grip.None) continue;

                MaskPicked?.Invoke(this, mask);
                BeginDrag(mask, Grip.Move, screen);
                e.Handled = true;
                return;
            }
        }

        if (IsCreating)
        {
            var image = ScreenToImage(screen);
            CreateRequested?.Invoke(this,
                new Point(image.X / ImageWidth, image.Y / ImageHeight));

            // The handler is contracted to have set SelectedMask by now, and the
            // same gesture continues as a resize so one drag both places and
            // shapes the mask. Which grip that is depends on the kind it added —
            // for a brush there is no geometry to rubber-band, so the gesture goes
            // straight on as the mask's first stroke.
            if (SelectedMask is { } created)
            {
                if (created.IsBrush)
                {
                    BeginStroke(created, screen, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                }
                else
                {
                    BeginDrag(created, created.IsLinear ? Grip.LinearZero : Grip.Right, screen);
                    _creatingDrag = true;
                }
                e.Handled = true;
            }
            return;
        }

        // Nothing here — leave the event unhandled so the viewer pans.
    }

    /// <summary>
    /// Start a stroke on a brush mask, snapshotting the tool's current Size and
    /// Opacity into it — from here on this stroke is fixed, whatever the sliders
    /// do next.
    ///
    /// <para>The first point goes down immediately rather than on the first move,
    /// so a click without a drag paints a single dab. <paramref name="erase"/> (Alt
    /// held) makes the stroke subtract instead of add, which is the only way to
    /// take back a slip short of deleting the whole mask.</para>
    /// </summary>
    private void BeginStroke(MaskSettings mask, Point screen, bool erase)
    {
        var brush = mask.Brush;
        var image = ScreenToImage(screen);

        var stroke = new BrushStroke
        {
            Size = Math.Clamp(brush.Size, BrushMask.MinSize, BrushMask.MaxSize),
            Opacity = Math.Clamp(brush.Opacity, 0.0, 1.0),
            Erase = erase,
        };
        stroke.Points.Add(new BrushPoint(image.X / ImageWidth, image.Y / ImageHeight));
        brush.Strokes.Add(stroke);

        _dragMask = mask;
        _stroke = stroke;
        _grip = Grip.Paint;
        _cursor = screen;
        _cursorInside = true;

        CaptureMouse();
        InvalidateVisual();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Extend the live stroke, dropping samples that have barely moved. The spine
    /// is rasterised as a distance field, so thinning it costs fidelity on tight
    /// curves and nothing else — there is no dab spacing here to bead.
    /// </summary>
    private void AppendStrokePoint(Point image)
    {
        if (_stroke is null) return;

        double radiusPx = Math.Clamp(_stroke.Size, BrushMask.MinSize, BrushMask.MaxSize) * ImageWidth;
        double minStep = Math.Max(0.75, radiusPx * StrokeSpacing);

        var last = _stroke.Points[^1];
        double dx = image.X - last.X * ImageWidth;
        double dy = image.Y - last.Y * ImageHeight;
        if (dx * dx + dy * dy < minStep * minStep) return;

        _stroke.Points.Add(new BrushPoint(image.X / ImageWidth, image.Y / ImageHeight));
    }

    private void BeginDrag(MaskSettings mask, Grip grip, Point screen)
    {
        _dragMask = mask;
        _grip = grip;
        _dragStartImage = ScreenToImage(screen);

        if (mask.IsLinear)
        {
            var l = mask.Linear;
            _dragStartCx = l.CenterX;
            _dragStartCy = l.CenterY;
            _dragStartRotation = l.Angle;
            _dragStartRx = l.Length;

            // Remember where the *other* line sits, in image pixels along the
            // fade axis. Dragging one line moves it while that one stays put,
            // which is what makes the gesture feel like adjusting a ramp rather
            // than scaling a symmetric object about its centre.
            var (cx, cy, dx, dy, half) = PixelGeometry(l);
            double centreD = cx * dx + cy * dy;
            _dragAnchorD = grip switch
            {
                Grip.LinearFull => centreD + half,   // hold the zero line
                Grip.LinearZero => centreD - half,   // hold the full line
                _ => centreD,
            };
        }
        else if (mask.IsRectangle)
        {
            var m = mask.Rectangle;
            _dragStartCx = m.CenterX;
            _dragStartCy = m.CenterY;
            _dragStartRx = m.HalfWidth;
            _dragStartRy = m.HalfHeight;
            _dragStartRotation = m.Rotation;
        }
        else
        {
            var m = mask.Radial;
            _dragStartCx = m.CenterX;
            _dragStartCy = m.CenterY;
            _dragStartRx = m.RadiusX;
            _dragStartRy = m.RadiusY;
            _dragStartRotation = m.Rotation;
        }

        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsActive || !HasImage) return;

        var screen = e.GetPosition(this);
        _cursor = screen;
        _cursorInside = true;

        if (_grip == Grip.None || _dragMask is null)
        {
            UpdateCursor(screen);
            // The brush ring has to follow the pointer to be of any use. Only for
            // a brush: every other mask draws nothing that depends on where the
            // cursor is, and repainting them on every move would be pure waste.
            if (BrushSelected) InvalidateVisual();
            return;
        }

        var image = ScreenToImage(screen);
        if (_grip == Grip.Paint) AppendStrokePoint(image);
        else if (_dragMask.IsLinear) DragLinear(_dragMask.Linear, image);
        else if (_dragMask.IsRectangle) DragRectangle(_dragMask.Rectangle, image);
        else DragRadial(_dragMask.Radial, image);

        InvalidateVisual();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>Whether the brush tool is what a press on the canvas would use.</summary>
    private bool BrushSelected => IsActive && SelectedMask is { IsEnabled: true, IsBrush: true };

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_cursorInside) return;
        // Drop the ring when the pointer leaves, or it hangs at the edge of the
        // canvas suggesting a dab that would never land there.
        _cursorInside = false;
        if (BrushSelected) InvalidateVisual();
    }

    private void DragRadial(RadialMask m, Point image)
    {
        switch (_grip)
        {
            case Grip.Move:
                m.CenterX = Math.Clamp(_dragStartCx + (image.X - _dragStartImage.X) / ImageWidth, -0.5, 1.5);
                m.CenterY = Math.Clamp(_dragStartCy + (image.Y - _dragStartImage.Y) / ImageHeight, -0.5, 1.5);
                break;

            case Grip.Left:
            case Grip.Right:
            case Grip.Top:
            case Grip.Bottom:
            {
                // Resize is measured from the centre along the dragged axis, in
                // the mask's own frame, so a rotated ellipse resizes along its
                // own axes rather than the screen's.
                double cxPx = m.CenterX * ImageWidth;
                double cyPx = m.CenterY * ImageHeight;
                var local = ToLocal(image.X - cxPx, image.Y - cyPx, m.Rotation);
                double minRadius = MinRadiusPx / Math.Max(ViewScale, 1e-6);

                if (_creatingDrag)
                {
                    // Both axes follow the drag, so the shape you rubber-band out
                    // is the shape you get.
                    double rx = Math.Max(Math.Abs(local.X), minRadius);
                    double ry = Math.Max(Math.Abs(local.Y), minRadius);
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        double r = Math.Max(rx, ry);
                        rx = ry = r;
                    }
                    m.RadiusX = rx / ImageWidth;
                    m.RadiusY = ry / ImageWidth;
                }
                else if (_grip is Grip.Left or Grip.Right)
                {
                    m.RadiusX = Math.Max(Math.Abs(local.X), minRadius) / ImageWidth;
                }
                else
                {
                    m.RadiusY = Math.Max(Math.Abs(local.Y), minRadius) / ImageWidth;
                }
                break;
            }

            case Grip.Rotate:
            {
                double cxPx = m.CenterX * ImageWidth;
                double cyPx = m.CenterY * ImageHeight;
                // The handle sits on the mask's −Y axis, which is 90° behind the
                // +X axis the angle is measured from.
                double angle = Math.Atan2(image.Y - cyPx, image.X - cxPx) * 180.0 / Math.PI + 90.0;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    angle = Math.Round(angle / 15.0) * 15.0;
                m.Rotation = angle;
                break;
            }
        }
    }

    private void DragRectangle(RectangleMask m, Point image)
    {
        switch (_grip)
        {
            case Grip.Move:
                m.CenterX = Math.Clamp(_dragStartCx + (image.X - _dragStartImage.X) / ImageWidth, -0.5, 1.5);
                m.CenterY = Math.Clamp(_dragStartCy + (image.Y - _dragStartImage.Y) / ImageHeight, -0.5, 1.5);
                break;

            case Grip.Left:
            case Grip.Right:
            case Grip.Top:
            case Grip.Bottom:
            case Grip.TopLeft:
            case Grip.TopRight:
            case Grip.BottomLeft:
            case Grip.BottomRight:
            {
                // Resize is measured from the centre along the dragged axis, in the
                // rectangle's own frame, so a rotated rectangle resizes along its
                // own axes rather than the screen's — exactly the radial's logic
                // with half-extents in place of radii.
                double cxPx = m.CenterX * ImageWidth;
                double cyPx = m.CenterY * ImageHeight;
                var local = ToLocal(image.X - cxPx, image.Y - cyPx, m.Rotation);
                double minExtent = MinRadiusPx / Math.Max(ViewScale, 1e-6);

                // A corner (and the create drag) moves both axes; an edge midpoint
                // moves only its own.
                bool bothAxes = _creatingDrag ||
                    _grip is Grip.TopLeft or Grip.TopRight or Grip.BottomLeft or Grip.BottomRight;

                if (bothAxes)
                {
                    double hw = Math.Max(Math.Abs(local.X), minExtent);
                    double hh = Math.Max(Math.Abs(local.Y), minExtent);
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    {
                        double r = Math.Max(hw, hh);
                        hw = hh = r;
                    }
                    m.HalfWidth = hw / ImageWidth;
                    m.HalfHeight = hh / ImageWidth;
                }
                else if (_grip is Grip.Left or Grip.Right)
                {
                    m.HalfWidth = Math.Max(Math.Abs(local.X), minExtent) / ImageWidth;
                }
                else
                {
                    m.HalfHeight = Math.Max(Math.Abs(local.Y), minExtent) / ImageWidth;
                }
                break;
            }

            case Grip.Rotate:
            {
                double cxPx = m.CenterX * ImageWidth;
                double cyPx = m.CenterY * ImageHeight;
                // The handle sits on the rectangle's −Y axis, 90° behind the +X
                // axis the angle is measured from.
                double angle = Math.Atan2(image.Y - cyPx, image.X - cxPx) * 180.0 / Math.PI + 90.0;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    angle = Math.Round(angle / 15.0) * 15.0;
                m.Rotation = angle;
                break;
            }
        }
    }

    private void DragLinear(LinearGradientMask m, Point image)
    {
        switch (_grip)
        {
            case Grip.Move:
                m.CenterX = Math.Clamp(_dragStartCx + (image.X - _dragStartImage.X) / ImageWidth, -0.5, 1.5);
                m.CenterY = Math.Clamp(_dragStartCy + (image.Y - _dragStartImage.Y) / ImageHeight, -0.5, 1.5);
                break;

            case Grip.LinearFull:
            case Grip.LinearZero:
            {
                if (_creatingDrag)
                {
                    // A create drag defines the whole ramp: from where the press
                    // landed (full effect) to the cursor (no effect). That is the
                    // gesture Lightroom uses, and it sets direction and softness
                    // in one movement.
                    double vx = image.X - _dragStartImage.X;
                    double vy = image.Y - _dragStartImage.Y;
                    double len = Math.Sqrt(vx * vx + vy * vy);
                    double minLen = MinLengthPx / Math.Max(ViewScale, 1e-6);
                    if (len < minLen) return;

                    double angle = Math.Atan2(vy, vx) * 180.0 / Math.PI;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                        angle = Math.Round(angle / 15.0) * 15.0;

                    m.Angle = angle;
                    m.Length = len / ImageWidth;
                    m.CenterX = (_dragStartImage.X + image.X) * 0.5 / ImageWidth;
                    m.CenterY = (_dragStartImage.Y + image.Y) * 0.5 / ImageHeight;
                    return;
                }

                // Editing: move the grabbed line along the fade axis while the
                // opposite one stays where it was.
                double t = m.Angle * Math.PI / 180.0;
                double dx = Math.Cos(t), dy = Math.Sin(t);
                double d = image.X * dx + image.Y * dy;

                double lo = Math.Min(d, _dragAnchorD);
                double hi = Math.Max(d, _dragAnchorD);
                double length = Math.Max(hi - lo, MinLengthPx / Math.Max(ViewScale, 1e-6));

                // Rebuild the centre from the two lines: it sits between them,
                // displaced from the old centre only along the fade axis.
                double mid = (lo + hi) * 0.5;
                double cx = m.CenterX * ImageWidth;
                double cy = m.CenterY * ImageHeight;
                double currentMid = cx * dx + cy * dy;
                double shift = mid - currentMid;

                m.CenterX = (cx + dx * shift) / ImageWidth;
                m.CenterY = (cy + dy * shift) / ImageHeight;
                m.Length = length / ImageWidth;
                break;
            }

            case Grip.LinearRotate:
            {
                double cxPx = m.CenterX * ImageWidth;
                double cyPx = m.CenterY * ImageHeight;
                // The handle sits along the centre line, 90° from the fade axis.
                double angle = Math.Atan2(image.Y - cyPx, image.X - cxPx) * 180.0 / Math.PI - 90.0;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                    angle = Math.Round(angle / 15.0) * 15.0;
                m.Angle = angle;
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_grip == Grip.None) return;

        _grip = Grip.None;
        _dragMask = null;
        _stroke = null;
        _creatingDrag = false;
        ReleaseMouseCapture();
        InvalidateVisual();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void UpdateCursor(Point screen)
    {
        var selected = SelectedMask;
        if (selected is null || !selected.IsEnabled || !IsActive)
        {
            Cursor = IsCreating ? Cursors.Cross : Cursors.Arrow;
            return;
        }

        // The ring already says how big the brush is and where it will land; the
        // crosshair adds the exact centre without hiding the photo the way an
        // arrow pointer would.
        if (selected.IsBrush)
        {
            Cursor = Cursors.Cross;
            return;
        }

        Cursor = HitTest(selected, screen, includeHandles: true) switch
        {
            Grip.Rotate or Grip.LinearRotate => Cursors.Hand,
            Grip.Left or Grip.Right => Cursors.SizeWE,
            Grip.Top or Grip.Bottom => Cursors.SizeNS,
            Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
            Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
            Grip.LinearFull or Grip.LinearZero => Cursors.SizeNS,
            Grip.Move => Cursors.SizeAll,
            _ => IsCreating ? Cursors.Cross : Cursors.Arrow,
        };
    }
}
