namespace Rawr.Develop;

/// <summary>
/// A directional ramp between two parallel lines — Lightroom's <b>Linear
/// Gradient</b>. Full effect on one side, none on the other, a smooth transition
/// between: the operator you reach for to hold back a sky.
///
/// <para><b>The shape is a band, not a region.</b> Unlike a radial, a linear
/// gradient is unbounded: everything past the "full" line is at weight 1 no
/// matter how far away it is, so the affected area is a half-plane rather than
/// something that closes. <see cref="Bounds"/> therefore clips the image
/// rectangle against that half-plane instead of measuring a shape, and returns
/// the whole frame whenever the ramp does not cut across it.</para>
///
/// <para>Coordinates follow <see cref="RadialMask"/>: the centre is a fraction
/// of width/height, and <see cref="Length"/> is a fraction of the <b>width</b>
/// on both axes, so the gradient keeps its geometry at any resolution and the
/// screen projection stays a plain uniform scale.</para>
/// </summary>
public sealed class LinearGradientMask
{
    /// <summary>Midpoint of the transition, as a fraction of image width.</summary>
    public double CenterX { get; set; } = 0.5;

    /// <summary>Midpoint of the transition, as a fraction of image height.</summary>
    public double CenterY { get; set; } = 0.5;

    /// <summary>
    /// Direction the effect <i>fades out</i> along, in degrees clockwise on
    /// screen. At 90° the gradient runs downward — full at the top, fading to
    /// nothing below, which is the sky case and the usual default.
    /// </summary>
    public double Angle { get; set; } = 90.0;

    /// <summary>Distance from the full-effect line to the zero-effect line, as a
    /// fraction of image width. Small is an abrupt transition, large a gentle
    /// one — this is the linear gradient's equivalent of Feather, which is why
    /// it carries no separate feather control.</summary>
    public double Length { get; set; } = 0.35;

    /// <summary>Swap which side gets the effect.</summary>
    public bool Invert { get; set; }

    /// <summary>Below this the transition is a hard edge; guards the divisions.</summary>
    public const double MinLength = 1e-4;

    public LinearGradientMask Clone() => (LinearGradientMask)MemberwiseClone();

    /// <summary>Unit vector the effect fades along, in image space.</summary>
    private (double dx, double dy) Direction()
    {
        double t = Angle * Math.PI / 180.0;
        return (Math.Cos(t), Math.Sin(t));
    }

    /// <summary>
    /// Signed distance, in pixels, from the transition's midline along the fade
    /// direction. −L/2 is the full-effect line, +L/2 the zero-effect line.
    /// </summary>
    private double SignedDistance(double px, double py, int imageWidth, int imageHeight,
                                  double dx, double dy)
    {
        double cx = CenterX * imageWidth;
        double cy = CenterY * imageHeight;
        return (px - cx) * dx + (py - cy) * dy;
    }

    /// <summary>
    /// The rectangle outside which this gradient is exactly zero.
    ///
    /// <para>Computed by clipping the image rectangle against the half-plane
    /// <c>d &lt; +L/2</c> (or <c>d &gt; −L/2</c> when inverted) and taking the
    /// bounding box of what survives — a Sutherland–Hodgman clip of four corners
    /// against one edge, which yields at most five vertices. A gradient angled
    /// across a corner then costs only that corner, where measuring the band's
    /// own extent would have returned the whole frame.</para>
    /// </summary>
    public PixelRect Bounds(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return default;

        var (dx, dy) = Direction();
        double half = Math.Max(Length, MinLength) * imageWidth * 0.5;

        // Keep the side that can be non-zero. Uninverted, weight is > 0 while
        // d < +half; inverted, while d > −half, which is the same test with the
        // direction reversed.
        double sign = Invert ? -1.0 : 1.0;
        double limit = half;

        Span<(double x, double y)> rect =
        [
            (0, 0), (imageWidth, 0), (imageWidth, imageHeight), (0, imageHeight)
        ];

        Span<(double x, double y)> kept = stackalloc (double, double)[8];
        int count = 0;

        for (int i = 0; i < rect.Length; i++)
        {
            var a = rect[i];
            var b = rect[(i + 1) % rect.Length];

            double da = sign * SignedDistance(a.x, a.y, imageWidth, imageHeight, dx, dy) - limit;
            double db = sign * SignedDistance(b.x, b.y, imageWidth, imageHeight, dx, dy) - limit;

            if (da < 0) kept[count++] = a;
            if ((da < 0) != (db < 0))
            {
                double t = da / (da - db);
                kept[count++] = (a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t);
            }
        }

        if (count == 0) return default;
        return PixelRect.FromPoints(kept[..count], imageWidth, imageHeight);
    }

    /// <summary>
    /// Rasterise the ramp over <paramref name="rect"/>, one weight in 0…1 per
    /// pixel, row-major.
    ///
    /// <para>Smoothstep rather than a straight ramp, for the reason it matters
    /// most here: a linear interpolation has a derivative discontinuity at both
    /// ends of the transition, and across a clear sky — exactly what this tool is
    /// for — those two lines are plainly visible as banding. The smoothstep meets
    /// the flat regions with zero slope, so there is nothing to see.</para>
    /// </summary>
    public float[] Weights(int imageWidth, int imageHeight, PixelRect rect)
    {
        var weights = new float[Math.Max(0, rect.Width) * Math.Max(0, rect.Height)];
        if (rect.IsEmpty || imageWidth <= 0 || imageHeight <= 0) return weights;

        var (dx, dy) = Direction();
        double length = Math.Max(Length, MinLength) * imageWidth;
        double half = length * 0.5;
        bool invert = Invert;

        for (int row = 0; row < rect.Height; row++)
        {
            // Pixel centres, as in RadialMask — sampling corners would shift the
            // whole gradient half a pixel.
            double py = rect.Y + row + 0.5;
            int o = row * rect.Width;
            for (int col = 0; col < rect.Width; col++)
            {
                double px = rect.X + col + 0.5;
                double d = SignedDistance(px, py, imageWidth, imageHeight, dx, dy);

                double weight;
                if (d <= -half) weight = 1.0;
                else if (d >= half) weight = 0.0;
                else
                {
                    double t = (half - d) / length;      // 1 at the full line, 0 at the zero line
                    weight = t * t * (3.0 - 2.0 * t);
                }

                weights[o + col] = (float)(invert ? 1.0 - weight : weight);
            }
        }

        return weights;
    }
}
