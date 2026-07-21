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
/// lines of their ramp.
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
        // Radial
        Left, Right, Top, Bottom, Rotate,
        // Linear: the full-effect line, the zero-effect line, and its rotator.
        LinearFull, LinearZero, LinearRotate,
    }

    private Grip _grip = Grip.None;
    private MaskSettings? _dragMask;

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

        if (ShowTint && selected is not null) DrawTint(dc, selected);

        foreach (var mask in Masks)
        {
            if (!mask.IsEnabled || ReferenceEquals(mask, selected)) continue;
            if (mask.IsLinear) DrawLinear(dc, mask.Linear, selectedShape: false);
            else DrawRadialOutline(dc, mask.Radial, UnselectedPen, null);
        }

        if (selected is not null && selected.IsEnabled)
        {
            if (selected.IsLinear) DrawLinear(dc, selected.Linear, selectedShape: true);
            else DrawRadialSelected(dc, selected.Radial);
        }
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
        string key = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{mask.Kind}|{r.CenterX:F5},{r.CenterY:F5},{r.RadiusX:F5},{r.RadiusY:F5},{r.Rotation:F3},{r.Feather:F2},{r.Invert}|" +
            $"{l.CenterX:F5},{l.CenterY:F5},{l.Angle:F3},{l.Length:F5},{l.Invert}|{ImageWidth}x{ImageHeight}");
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

    // ── Hit testing ────────────────────────────────────────────────────────

    private Grip HitTest(MaskSettings mask, Point screen, bool includeHandles)
        => mask.IsLinear
            ? HitTestLinear(mask.Linear, screen, includeHandles)
            : HitTestRadial(mask.Radial, screen, includeHandles);

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
            // shapes the mask. Which grip that is depends on the kind it added.
            if (SelectedMask is { } created)
            {
                BeginDrag(created, created.IsLinear ? Grip.LinearZero : Grip.Right, screen);
                _creatingDrag = true;
                e.Handled = true;
            }
            return;
        }

        // Nothing here — leave the event unhandled so the viewer pans.
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

        if (_grip == Grip.None || _dragMask is null)
        {
            UpdateCursor(screen);
            return;
        }

        var image = ScreenToImage(screen);
        if (_dragMask.IsLinear) DragLinear(_dragMask.Linear, image);
        else DragRadial(_dragMask.Radial, image);

        InvalidateVisual();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
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
        _creatingDrag = false;
        ReleaseMouseCapture();
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

        Cursor = HitTest(selected, screen, includeHandles: true) switch
        {
            Grip.Rotate or Grip.LinearRotate => Cursors.Hand,
            Grip.Left or Grip.Right => Cursors.SizeWE,
            Grip.Top or Grip.Bottom => Cursors.SizeNS,
            Grip.LinearFull or Grip.LinearZero => Cursors.SizeNS,
            Grip.Move => Cursors.SizeAll,
            _ => IsCreating ? Cursors.Cross : Cursors.Arrow,
        };
    }
}
