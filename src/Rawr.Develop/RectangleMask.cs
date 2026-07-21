namespace Rawr.Develop;

/// <summary>
/// An axis-rotatable rectangular selection — Lightroom's <b>Rectangle</b> mask.
/// Like <see cref="RadialMask"/> it is pure geometry: it answers "how much does
/// this mask apply at pixel (x, y)?" and nothing about what the adjustment then
/// does. <see cref="MaskSettings"/> pairs it with the adjustments, and
/// <see cref="DevelopProcessor"/> does the compositing.
///
/// <para><b>Coordinates follow <see cref="RadialMask"/> exactly.</b> The centre is
/// a fraction of width/height so it stays put at any resolution, and both half
/// extents are fractions of the <b>width</b> so equal half-extents draw a square
/// on any aspect ratio rather than a shape that stretches with the sensor — the
/// same reason the radial normalises both radii to width. Every stored number
/// then survives a change of resolution unchanged, which is what lets the preview
/// and the full-resolution export share one mask definition.</para>
///
/// <para><b>The feather is separable and eats inward.</b> Each axis carries the
/// same smoothstep falloff the radial uses, applied independently and multiplied
/// together, so the interior is fully on, the edges ramp off, and the corners
/// round exactly as Lightroom's do. The mask is zero at the rectangle's edge and
/// beyond, so like the radial its <see cref="Bounds"/> is a genuine crop of the
/// frame — everything outside the (rotated) rectangle costs nothing.</para>
///
/// <para><b>Default sense is "inside".</b> The mask reads 1 in the middle and
/// falls to 0 at the edge, so the adjustments land on what the rectangle encloses;
/// <see cref="Invert"/> flips that to affect everything outside it.</para>
/// </summary>
public sealed class RectangleMask
{
    /// <summary>Centre X as a fraction of image width (0 = left, 1 = right).</summary>
    public double CenterX { get; set; } = 0.5;

    /// <summary>Centre Y as a fraction of image height (0 = top, 1 = bottom).</summary>
    public double CenterY { get; set; } = 0.5;

    /// <summary>Half the width, as a fraction of image <i>width</i>. See the class
    /// remarks for why width and not height.</summary>
    public double HalfWidth { get; set; } = 0.25;

    /// <summary>Half the height, also as a fraction of image <i>width</i>, so equal
    /// half-extents are a true square at any aspect ratio.</summary>
    public double HalfHeight { get; set; } = 0.25;

    /// <summary>Rotation of the rectangle, in degrees clockwise on screen.</summary>
    public double Rotation { get; set; }

    /// <summary>How much of each half-extent the falloff occupies, 0…100. At 0 the
    /// edge is hard; at 100 the mask ramps all the way from the centre.</summary>
    public double Feather { get; set; } = 50.0;

    /// <summary>Apply outside the rectangle instead of inside.</summary>
    public bool Invert { get; set; }

    /// <summary>Smallest half-extent, in width-fractions, that still rasterises to
    /// something. Guards the divisions in <see cref="Weights"/> against a mask the
    /// user has dragged shut.</summary>
    public const double MinExtent = 1e-4;

    public RectangleMask Clone() => (RectangleMask)MemberwiseClone();

    /// <summary>
    /// The axis-aligned pixel rectangle outside which this mask is exactly zero,
    /// clamped to the image. This is what lets the renderer re-run the pipeline
    /// over a fraction of the frame instead of all of it.
    ///
    /// <para>An <see cref="Invert"/>ed mask is non-zero everywhere outside the
    /// rectangle, so its bounds are the whole image — as with the radial there is
    /// no saving to be had.</para>
    ///
    /// <para>The extents are those of the rotated rectangle's bounding box: half
    /// its width is <c>|hw·cos θ| + |hh·sin θ|</c> and half its height the same
    /// with the sines and cosines swapped. Rounding outward by a pixel keeps the
    /// boundary row/column where the weight is still faintly non-zero inside.</para>
    /// </summary>
    public PixelRect Bounds(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return default;
        if (Invert) return new PixelRect(0, 0, imageWidth, imageHeight);

        double hw = Math.Max(HalfWidth, MinExtent) * imageWidth;
        double hh = Math.Max(HalfHeight, MinExtent) * imageWidth;
        double theta = Rotation * Math.PI / 180.0;
        double c = Math.Abs(Math.Cos(theta)), s = Math.Abs(Math.Sin(theta));

        double halfW = hw * c + hh * s;
        double halfH = hw * s + hh * c;

        double cx = CenterX * imageWidth;
        double cy = CenterY * imageHeight;

        int x0 = (int)Math.Floor(cx - halfW) - 1;
        int y0 = (int)Math.Floor(cy - halfH) - 1;
        int x1 = (int)Math.Ceiling(cx + halfW) + 1;
        int y1 = (int)Math.Ceiling(cy + halfH) + 1;

        x0 = Math.Clamp(x0, 0, imageWidth);
        y0 = Math.Clamp(y0, 0, imageHeight);
        x1 = Math.Clamp(x1, 0, imageWidth);
        y1 = Math.Clamp(y1, 0, imageHeight);

        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Rasterise the mask over <paramref name="rect"/> of an
    /// <paramref name="imageWidth"/>×<paramref name="imageHeight"/> image,
    /// returning one weight in 0…1 per pixel, row-major within the rect.
    ///
    /// <para>Weight is the product of two independent per-axis falloffs. On each
    /// axis the normalised distance from the centre runs 0 at the middle to 1 at
    /// the edge; inside <c>1 − feather</c> that axis is fully on, past 1 fully off,
    /// and between the two it runs through a smoothstep rather than a straight ramp
    /// — a linear falloff leaves a visible crease where the derivative jumps, which
    /// on a big soft mask over a clear sky is exactly the artefact a photographer
    /// would notice. Multiplying the two axes rounds the corners, matching how
    /// Lightroom feathers a rectangle.</para>
    /// </summary>
    public float[] Weights(int imageWidth, int imageHeight, PixelRect rect)
    {
        var weights = new float[Math.Max(0, rect.Width) * Math.Max(0, rect.Height)];
        if (rect.IsEmpty || imageWidth <= 0 || imageHeight <= 0) return weights;

        double hw = Math.Max(HalfWidth, MinExtent) * imageWidth;
        double hh = Math.Max(HalfHeight, MinExtent) * imageWidth;
        double theta = Rotation * Math.PI / 180.0;
        double cos = Math.Cos(theta), sin = Math.Sin(theta);
        double cx = CenterX * imageWidth;
        double cy = CenterY * imageHeight;

        double feather = Math.Clamp(Feather, 0.0, 100.0) / 100.0;
        double inner = 1.0 - feather;
        bool invert = Invert;

        for (int row = 0; row < rect.Height; row++)
        {
            // Pixel centres, not corners: sampling at the corner biases the whole
            // mask half a pixel up and left, obvious on a hard-edged rectangle.
            double dy = rect.Y + row + 0.5 - cy;
            int o = row * rect.Width;
            for (int col = 0; col < rect.Width; col++)
            {
                double dx = rect.X + col + 0.5 - cx;

                // Into the rectangle's own frame, then normalise each axis by its
                // half-extent so the level sets become nested rectangles.
                double u = Math.Abs((dx * cos + dy * sin) / hw);
                double v = Math.Abs((-dx * sin + dy * cos) / hh);

                double weight = AxisFalloff(u, feather, inner) * AxisFalloff(v, feather, inner);
                weights[o + col] = (float)(invert ? 1.0 - weight : weight);
            }
        }

        return weights;

        static double AxisFalloff(double a, double feather, double inner)
        {
            if (a >= 1.0) return 0.0;
            if (feather <= 0.0 || a <= inner) return 1.0;
            double t = (1.0 - a) / feather;   // 1 at the inner edge, 0 at the rectangle edge
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
