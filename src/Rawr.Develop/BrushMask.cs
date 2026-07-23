namespace Rawr.Develop;

/// <summary>One sample on a stroke's spine. Normalised exactly as every other
/// mask coordinate is — X a fraction of image width, Y a fraction of image
/// height — so a stroke painted on the 1920 px preview lands in the same place
/// on the full-resolution export.</summary>
public readonly record struct BrushPoint(double X, double Y);

/// <summary>
/// One drag of the brush: the spine the cursor traced, plus the brush settings
/// that were live at the time.
///
/// <para><b>Size and Opacity are snapshotted per stroke, not stored once on the
/// mask.</b> They are tool settings — what the <i>next</i> stroke will paint with
/// — and every editor treats them that way, because the alternative is that
/// nudging Size after ten strokes silently redraws all ten at the new width.</para>
/// </summary>
public sealed class BrushStroke
{
    /// <summary>Brush radius as a fraction of image <i>width</i>, matching how
    /// every other mask normalises its extents. A circular dab therefore stays
    /// circular at any aspect ratio.</summary>
    public double Size { get; set; } = BrushMask.DefaultSize;

    /// <summary>How far toward fully-selected this one stroke paints, 0…1.
    /// Overlapping passes <i>within</i> a stroke do not stack past this — that is
    /// what keeps a stroke even where the cursor slowed down — but a second
    /// stroke over the same ground builds on top of it.</summary>
    public double Opacity { get; set; } = BrushMask.DefaultOpacity;

    /// <summary>Take weight away instead of adding it. The eraser is a stroke like
    /// any other so that it stays part of the same replayable, resolution
    /// independent record rather than needing a rasterised buffer to subtract
    /// from.</summary>
    public bool Erase { get; set; }

    public List<BrushPoint> Points { get; set; } = new();

    public BrushStroke Clone() => new()
    {
        Size = Size,
        Opacity = Opacity,
        Erase = Erase,
        Points = new List<BrushPoint>(Points),
    };
}

/// <summary>
/// A hand-painted selection — Lightroom's <b>Brush</b>. Like the other shapes it
/// is pure geometry: it answers "how much does this mask apply at pixel (x, y)?"
/// and nothing about what the adjustment then does, so
/// <see cref="DevelopProcessor"/> composites it through the same
/// <see cref="Bounds"/>/<see cref="Weights"/> pair it uses for a radial.
///
/// <para><b>Strokes are kept as vectors, not as a painted bitmap.</b> A weight
/// buffer would have to be authored at some fixed resolution and then resampled
/// for the export, which softens every edge the user deliberately placed and ties
/// the stored mask to the preview size it happened to be painted at. Replaying the
/// spine instead means the export rasterises the same stroke at full resolution —
/// the same argument that makes the radial store a normalised centre and radius
/// rather than a rasterised ellipse.</para>
///
/// <para><b>A stroke is the distance field of its polyline, not a row of stamped
/// dabs.</b> Stamping needs a spacing constant, and any spacing loose enough to be
/// cheap beads visibly when the cursor moves slowly. Measuring each pixel's
/// distance to the spine gives a stroke of exactly uniform width however densely
/// the mouse happened to sample, and costs the same.</para>
///
/// <para><b>Within a stroke coverage is a maximum; across strokes it
/// accumulates.</b> Taking the max along one stroke is what stops a stroke from
/// darkening where it crosses itself. Between strokes the new one is composited
/// over what is already there (<c>w + s·(1−w)</c>), so repeated passes at a low
/// Opacity build up toward — but never past — fully selected, and an
/// <see cref="BrushStroke.Erase"/> stroke scales back down by <c>w·(1−s)</c>.
/// That makes the list order-dependent, unlike the mask list itself.</para>
/// </summary>
public sealed class BrushMask
{
    /// <summary>Radius, as a fraction of image width, that a fresh brush paints
    /// with — about a twelfth of the frame, which is a comfortable "dodge this
    /// face" size on a normal photo.</summary>
    public const double DefaultSize = 0.06;

    public const double DefaultOpacity = 1.0;

    /// <summary>Smallest and largest brush radius, in width-fractions. The floor
    /// keeps a dab at least a pixel or so wide on a preview-sized buffer and
    /// guards the divisions in <see cref="Weights"/>; the ceiling is well past the
    /// point where a linear or radial mask is the better tool.</summary>
    public const double MinSize = 0.002;
    public const double MaxSize = 0.5;

    /// <summary>
    /// Fraction of the radius that is fully on before the edge starts to fall away.
    ///
    /// <para>Fixed rather than exposed as a Feather slider: it would have to be
    /// per-stroke to behave (see <see cref="BrushStroke"/>), and a half-hard dab is
    /// the value that both blends invisibly and still lets you work up to an edge.
    /// If it is ever wanted as a control it slots in beside Size and Opacity as one
    /// more snapshotted field.</para>
    /// </summary>
    public const double CoreFraction = 0.5;

    /// <summary>The strokes, oldest first. Order matters — see the class
    /// remarks.</summary>
    public List<BrushStroke> Strokes { get; set; } = new();

    /// <summary>Apply everywhere the brush did <i>not</i> paint.</summary>
    public bool Invert { get; set; }

    // ── Tool settings ───────────────────────────────────────────────────────
    // What the next stroke will be created with. They live on the mask rather
    // than on the view-model so they round-trip with it and the panel can bind
    // through the same SelectedMask path as everything else; changing them
    // cannot alter a stroke already laid down.

    public double Size { get; set; } = DefaultSize;

    public double Opacity { get; set; } = DefaultOpacity;

    /// <summary>True when this brush cannot select anything: nothing painted, and
    /// not inverted. An <i>inverted</i> empty brush covers the whole frame at full
    /// weight, which is a perfectly meaningful (if roundabout) global adjustment,
    /// so it is deliberately not blank.</summary>
    public bool IsBlank =>
        !Invert && !Strokes.Any(s => !s.Erase && s.Points.Count > 0 && s.Opacity > 0.0);

    public BrushMask Clone() => new()
    {
        // Deep, unlike the other shapes' MemberwiseClone: the exporter renders from
        // a cloned settings snapshot while the user keeps painting, and a shared
        // list would let those strokes land in the file being written.
        Strokes = Strokes.Select(s => s.Clone()).ToList(),
        Invert = Invert,
        Size = Size,
        Opacity = Opacity,
    };

    /// <summary>
    /// The axis-aligned pixel rectangle outside which this mask is exactly zero,
    /// clamped to the image — the union of the painted strokes' footprints, each
    /// grown by its own radius.
    ///
    /// <para>Erase strokes are left out. They can only ever take weight away, so
    /// including them would enlarge the crop the renderer works over without any
    /// pixel inside the addition being non-zero.</para>
    ///
    /// <para>An <see cref="Invert"/>ed brush is non-zero everywhere it did not
    /// paint, so its bounds are the whole image — as with the other shapes there is
    /// no saving to be had.</para>
    /// </summary>
    public PixelRect Bounds(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return default;
        if (Invert) return new PixelRect(0, 0, imageWidth, imageHeight);

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var stroke in Strokes)
        {
            if (stroke.Erase || stroke.Points.Count == 0) continue;
            if (Math.Clamp(stroke.Opacity, 0.0, 1.0) <= 0.0) continue;

            double radius = Radius(stroke, imageWidth);
            foreach (var p in stroke.Points)
            {
                double px = p.X * imageWidth, py = p.Y * imageHeight;
                if (px - radius < minX) minX = px - radius;
                if (py - radius < minY) minY = py - radius;
                if (px + radius > maxX) maxX = px + radius;
                if (py + radius > maxY) maxY = py + radius;
            }
            any = true;
        }

        if (!any) return default;

        Span<(double x, double y)> corners = [(minX, minY), (maxX, maxY)];
        return PixelRect.FromPoints(corners, imageWidth, imageHeight);
    }

    /// <summary>
    /// Rasterise the strokes over <paramref name="rect"/>, one weight in 0…1 per
    /// pixel, row-major.
    ///
    /// <para>Each stroke is first resolved into a coverage buffer — the maximum,
    /// over its segments, of a smoothstep on distance to the spine — and then
    /// composited at its Opacity onto the running result. The smoothstep is the
    /// same falloff the radial and rectangle use, and for the same reason: a linear
    /// ramp's derivative jumps at both ends of the transition, which over a clear
    /// sky is a visible crease exactly where a brushed edge is meant to disappear.</para>
    ///
    /// <para>Cost is proportional to the area actually painted, not to
    /// <paramref name="rect"/>: every loop is bounded by the segment's own
    /// bounding box. The coverage buffer is allocated once per call and only the
    /// touched sub-rectangle is cleared between strokes.</para>
    /// </summary>
    public float[] Weights(int imageWidth, int imageHeight, PixelRect rect)
    {
        int rw = Math.Max(0, rect.Width), rh = Math.Max(0, rect.Height);
        var weights = new float[rw * rh];
        if (rect.IsEmpty || imageWidth <= 0 || imageHeight <= 0) return weights;

        float[]? coverage = null;

        foreach (var stroke in Strokes)
        {
            if (stroke.Points.Count == 0) continue;
            double amount = Math.Clamp(stroke.Opacity, 0.0, 1.0);
            if (amount <= 0.0) continue;

            double radius = Radius(stroke, imageWidth);
            if (!Extent(stroke.Points, radius, imageWidth, imageHeight, rect,
                        0, 0, rw, rh, out int sx0, out int sy0, out int sx1, out int sy1))
                continue;

            coverage ??= new float[rw * rh];
            for (int row = sy0; row < sy1; row++)
                Array.Clear(coverage, row * rw + sx0, sx1 - sx0);

            Stamp(stroke, radius, imageWidth, imageHeight, rect, coverage,
                  sx0, sy0, sx1, sy1);

            bool erase = stroke.Erase;
            for (int row = sy0; row < sy1; row++)
            {
                int o = row * rw;
                for (int col = sx0; col < sx1; col++)
                {
                    float c = coverage[o + col];
                    if (c <= 0f) continue;

                    float s = (float)(amount * c);
                    // Composite over what is already selected rather than
                    // replacing it: that is what lets a low-opacity brush build
                    // up with repeated passes and stop cleanly at 1.
                    if (erase) weights[o + col] *= 1f - s;
                    else weights[o + col] += s * (1f - weights[o + col]);
                }
            }
        }

        if (Invert)
            for (int i = 0; i < weights.Length; i++) weights[i] = 1f - weights[i];

        return weights;
    }

    private static double Radius(BrushStroke stroke, int imageWidth)
        => Math.Clamp(stroke.Size, MinSize, MaxSize) * imageWidth;

    /// <summary>
    /// The rect-local column/row window a set of points can reach, grown by
    /// <paramref name="radius"/> and clipped to the given bounds. False when
    /// nothing survives, which is the common case for a stroke outside the crop.
    /// </summary>
    private static bool Extent(List<BrushPoint> points, double radius,
                               int imageWidth, int imageHeight, PixelRect rect,
                               int clipX0, int clipY0, int clipX1, int clipY1,
                               out int x0, out int y0, out int x1, out int y1)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            double px = p.X * imageWidth, py = p.Y * imageHeight;
            if (px < minX) minX = px;
            if (py < minY) minY = py;
            if (px > maxX) maxX = px;
            if (py > maxY) maxY = py;
        }

        x0 = Math.Max(clipX0, (int)Math.Floor(minX - radius) - rect.X);
        y0 = Math.Max(clipY0, (int)Math.Floor(minY - radius) - rect.Y);
        x1 = Math.Min(clipX1, (int)Math.Ceiling(maxX + radius) + 1 - rect.X);
        y1 = Math.Min(clipY1, (int)Math.Ceiling(maxY + radius) + 1 - rect.Y);
        return x1 > x0 && y1 > y0;
    }

    /// <summary>
    /// Lay one stroke into <paramref name="coverage"/> as the maximum, over its
    /// segments, of the dab falloff on distance to that segment. A single-point
    /// stroke — a click rather than a drag — is a degenerate segment, which the
    /// point-to-segment distance already handles as a plain circular dab.
    /// </summary>
    private static void Stamp(BrushStroke stroke, double radius,
                              int imageWidth, int imageHeight, PixelRect rect,
                              float[] coverage, int sx0, int sy0, int sx1, int sy1)
    {
        var points = stroke.Points;
        int rw = rect.Width;
        double core = radius * CoreFraction;
        double band = radius - core;
        double r2 = radius * radius, c2 = core * core;
        int segments = Math.Max(1, points.Count - 1);

        for (int i = 0; i < segments; i++)
        {
            var a = points[i];
            var b = points[Math.Min(i + 1, points.Count - 1)];
            double ax = a.X * imageWidth, ay = a.Y * imageHeight;
            double bx = b.X * imageWidth, by = b.Y * imageHeight;

            // Only this segment's own neighbourhood, so a long stroke costs its
            // length rather than the area of its bounding box.
            int x0 = Math.Max(sx0, (int)Math.Floor(Math.Min(ax, bx) - radius) - rect.X);
            int y0 = Math.Max(sy0, (int)Math.Floor(Math.Min(ay, by) - radius) - rect.Y);
            int x1 = Math.Min(sx1, (int)Math.Ceiling(Math.Max(ax, bx) + radius) + 1 - rect.X);
            int y1 = Math.Min(sy1, (int)Math.Ceiling(Math.Max(ay, by) + radius) + 1 - rect.Y);
            if (x1 <= x0 || y1 <= y0) continue;

            double vx = bx - ax, vy = by - ay;
            double len2 = vx * vx + vy * vy;

            for (int row = y0; row < y1; row++)
            {
                // Pixel centres, as in every other mask — sampling corners would
                // shift the whole stroke half a pixel up and left.
                double py = rect.Y + row + 0.5;
                int o = row * rw;
                for (int col = x0; col < x1; col++)
                {
                    double px = rect.X + col + 0.5;

                    // Closest point on the segment, clamped to its ends so the
                    // stroke has round caps rather than reaching past them.
                    double t = len2 > 0.0 ? ((px - ax) * vx + (py - ay) * vy) / len2 : 0.0;
                    if (t < 0.0) t = 0.0;
                    else if (t > 1.0) t = 1.0;

                    double dx = px - (ax + t * vx);
                    double dy = py - (ay + t * vy);
                    double d2 = dx * dx + dy * dy;
                    if (d2 >= r2) continue;

                    float w;
                    if (d2 <= c2)
                    {
                        w = 1f;
                    }
                    else
                    {
                        double u = (radius - Math.Sqrt(d2)) / band;   // 1 at the core, 0 at the rim
                        w = (float)(u * u * (3.0 - 2.0 * u));
                    }

                    if (w > coverage[o + col]) coverage[o + col] = w;
                }
            }
        }
    }
}
