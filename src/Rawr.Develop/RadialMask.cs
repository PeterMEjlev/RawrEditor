namespace Rawr.Develop;

/// <summary>
/// An axis-rotatable elliptical selection — Lightroom's <b>Radial Gradient</b>.
/// Pure geometry: it answers "how much does this mask apply at pixel (x, y)?"
/// and nothing about what the adjustment then does. <see cref="MaskSettings"/>
/// pairs it with the adjustments, and <see cref="DevelopProcessor"/> does the
/// compositing.
///
/// <para><b>Coordinates are normalised, and deliberately not uniformly so.</b>
/// The centre is a fraction of width/height, so (0.5, 0.5) is the middle of any
/// image and stays there whatever the resolution. The two radii are <i>both</i>
/// fractions of the <b>width</b>, which is what makes the shape isotropic:
/// with radii normalised per-axis, RadiusX == RadiusY would draw a circle on a
/// square sensor and a squashed ellipse on a 3:2 one, so a mask authored on the
/// 1920 px preview would change shape on export. Here RadiusX == RadiusY is a
/// circle everywhere, and every stored number survives a change of resolution
/// unchanged — which is the whole reason the preview and the full-resolution
/// export can share one mask definition.</para>
///
/// <para><b>Default sense is "inside".</b> The mask reads 1 at the centre and
/// falls to 0 at the ellipse edge, so the adjustments land on what the ellipse
/// encloses; <see cref="Invert"/> flips that to a vignette. This follows the
/// modern Masking panel rather than the old Radial Filter, which applied
/// outside by default and confused everyone who ever met it.</para>
/// </summary>
public sealed class RadialMask
{
    /// <summary>Centre X as a fraction of image width (0 = left, 1 = right).</summary>
    public double CenterX { get; set; } = 0.5;

    /// <summary>Centre Y as a fraction of image height (0 = top, 1 = bottom).</summary>
    public double CenterY { get; set; } = 0.5;

    /// <summary>Semi-axis along the mask's own X, as a fraction of image
    /// <i>width</i>. See the class remarks for why width and not height.</summary>
    public double RadiusX { get; set; } = 0.25;

    /// <summary>Semi-axis along the mask's own Y, also as a fraction of image
    /// <i>width</i>, so equal radii are a true circle at any aspect ratio.</summary>
    public double RadiusY { get; set; } = 0.25;

    /// <summary>Rotation of the ellipse, in degrees clockwise on screen.</summary>
    public double Rotation { get; set; }

    /// <summary>How much of the radius the falloff occupies, 0…100. At 0 the
    /// edge is hard; at 100 the mask ramps all the way from the centre, which is
    /// the softest a radial can be and Lightroom's default.</summary>
    public double Feather { get; set; } = 50.0;

    /// <summary>Apply outside the ellipse instead of inside — a vignette.</summary>
    public bool Invert { get; set; }

    /// <summary>Smallest radius, in width-fractions, that still rasterises to
    /// something. Guards the divisions in <see cref="Weights"/> against a mask
    /// the user has dragged shut.</summary>
    public const double MinRadius = 1e-4;

    public RadialMask Clone() => (RadialMask)MemberwiseClone();

    /// <summary>
    /// The axis-aligned pixel rectangle outside which this mask is exactly zero,
    /// clamped to the image. This is what lets the renderer re-run the pipeline
    /// over a fraction of the frame instead of all of it.
    ///
    /// <para>An <see cref="Invert"/>ed mask is non-zero everywhere outside the
    /// ellipse, so its bounds are the whole image — there is no saving to be had
    /// and pretending otherwise would clip the vignette to a box.</para>
    ///
    /// <para>The extents come from the standard rotated-ellipse support function:
    /// the half-width of the bounding box is √((rx·cos θ)² + (ry·sin θ)²), which
    /// is the farthest the ellipse reaches along X. Rounding outward by a pixel
    /// keeps the boundary row/column where the weight is still faintly non-zero
    /// inside the rect.</para>
    /// </summary>
    public PixelRect Bounds(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) return default;
        if (Invert) return new PixelRect(0, 0, imageWidth, imageHeight);

        double rx = Math.Max(RadiusX, MinRadius) * imageWidth;
        double ry = Math.Max(RadiusY, MinRadius) * imageWidth;
        double theta = Rotation * Math.PI / 180.0;
        double c = Math.Cos(theta), s = Math.Sin(theta);

        double halfW = Math.Sqrt(rx * rx * c * c + ry * ry * s * s);
        double halfH = Math.Sqrt(rx * rx * s * s + ry * ry * c * c);

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
    /// <para>Weight is a function of the normalised elliptical distance d — the
    /// radius scaled so d = 1 is the ellipse itself. Inside <c>1 − feather</c>
    /// the mask is fully on, past d = 1 fully off, and between the two it runs
    /// through a smoothstep rather than a straight ramp: a linear falloff leaves
    /// a visible crease at both ends of the transition (the derivative jumps),
    /// and on a big soft radial over a clear sky that crease is exactly the
    /// artefact a photographer would notice.</para>
    /// </summary>
    public float[] Weights(int imageWidth, int imageHeight, PixelRect rect)
    {
        var weights = new float[Math.Max(0, rect.Width) * Math.Max(0, rect.Height)];
        if (rect.IsEmpty || imageWidth <= 0 || imageHeight <= 0) return weights;

        double rx = Math.Max(RadiusX, MinRadius) * imageWidth;
        double ry = Math.Max(RadiusY, MinRadius) * imageWidth;
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
            // mask half a pixel up and left, which is invisible on a soft radial
            // and obvious on a hard-edged one.
            double dy = rect.Y + row + 0.5 - cy;
            int o = row * rect.Width;
            for (int col = 0; col < rect.Width; col++)
            {
                double dx = rect.X + col + 0.5 - cx;

                // Into the ellipse's own frame, then normalise each axis by its
                // radius so the level sets become concentric circles in d.
                double u = (dx * cos + dy * sin) / rx;
                double v = (-dx * sin + dy * cos) / ry;
                double d = Math.Sqrt(u * u + v * v);

                double weight;
                if (d >= 1.0) weight = 0.0;
                else if (feather <= 0.0) weight = 1.0;
                else if (d <= inner) weight = 1.0;
                else
                {
                    double t = (1.0 - d) / feather;   // 1 at the inner edge, 0 at d = 1
                    weight = t * t * (3.0 - 2.0 * t);
                }

                weights[o + col] = (float)(invert ? 1.0 - weight : weight);
            }
        }

        return weights;
    }
}
