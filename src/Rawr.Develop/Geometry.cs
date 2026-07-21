using Rawr.Raw;

namespace Rawr.Develop;

/// <summary>
/// Resolves a <see cref="GeometrySettings"/> into pixels: crop, straighten and
/// 90° orientation applied to a linear sensor buffer, producing the buffer the
/// rest of the pipeline treats as "the photograph".
///
/// <para><b>It runs first, before any tone work.</b> Everything downstream is
/// defined against the picture rather than against the sensor — the regional
/// filters size their radius from the frame, Dehaze estimates one airlight for
/// it, masks are normalised to it — and after a crop that frame is the cropped
/// one. Running geometry last would leave all of those measuring a photograph
/// that is not the one being exported.</para>
///
/// <para><b>Two paths, and the common one does not resample.</b> With the
/// straighten at zero the whole transform is an index remap, so a plain crop or
/// a 90° turn copies pixels through untouched — no interpolation, no softening,
/// and a rotated JPEG is bit-identical to the unrotated one. Only a straighten
/// angle brings in the bilinear sampler.</para>
///
/// <para><b>The mapping is inverted, not composed forward.</b> Each destination
/// pixel asks where it came from and reads once, so a crop of a rotated flip
/// costs exactly one pass however many stages it names — no intermediate
/// buffers, and no compounding of the resample blur.</para>
/// </summary>
public static class Geometry
{
    /// <summary>Dimensions after the 90° orientation, before cropping.</summary>
    public static (int width, int height) OrientedSize(int width, int height, int quadrant)
        => ((((quadrant % 4) + 4) % 4) & 1) == 0 ? (width, height) : (height, width);

    /// <summary>Dimensions of what <see cref="Apply"/> will return.</summary>
    public static (int width, int height) OutputSize(GeometrySettings g, int width, int height)
    {
        var (ow, oh) = OrientedSize(width, height, g.Quadrant);
        return (Math.Max(1, (int)Math.Round(g.CropWidth * ow)),
                Math.Max(1, (int)Math.Round(g.CropHeight * oh)));
    }

    /// <summary>
    /// The straighten rotation in the sense the sampler needs it.
    ///
    /// <para>A single mirror reverses the handedness of the frame the rotation
    /// lands in, so the angle is negated when exactly one flip is on. Without
    /// this, ticking Flip Horizontal would quietly send the Straighten slider
    /// the other way.</para>
    /// </summary>
    private static double AngleRadians(GeometrySettings g)
    {
        double t = g.Straighten * Math.PI / 180.0;
        return g.FlipHorizontal ^ g.FlipVertical ? -t : t;
    }

    /// <summary>
    /// Apply <paramref name="g"/> to <paramref name="src"/>. Returns the input
    /// itself when the geometry is neutral, so the baseline render still runs
    /// on the buffer it was handed rather than on a copy of it.
    /// </summary>
    public static LinearRawImage Apply(LinearRawImage src, GeometrySettings? g)
    {
        if (g is null || g.IsNeutral) return src;
        return g.IsAxisAligned ? Remap(src, g) : Resample(src, g);
    }

    // ── Lossless path: crop + orientation as a pure index remap ──────────────

    private static LinearRawImage Remap(LinearRawImage src, GeometrySettings g)
    {
        int sw = src.Width, sh = src.Height;
        int q = g.NormalisedQuadrant;
        var (ow, oh) = OrientedSize(sw, sh, q);

        var (dw, dh) = OutputSize(g, sw, sh);
        dw = Math.Min(dw, ow);
        dh = Math.Min(dh, oh);

        // Snap the window to the pixel grid, then hold it on the image: a crop
        // stored a hair out of range must not walk off the end of the buffer.
        int ox0 = Math.Clamp((int)Math.Round(g.CropX * ow), 0, ow - dw);
        int oy0 = Math.Clamp((int)Math.Round(g.CropY * oh), 0, oh - dh);

        var dst = new ushort[dw * dh * 3];
        var s = src.Pixels;
        int srcStride = sw * 3;
        bool flipH = g.FlipHorizontal, flipV = g.FlipVertical;

        Parallel.For(0, dh, dy =>
        {
            int o = dy * dw * 3;
            for (int dx = 0; dx < dw; dx++)
            {
                var (sx, sy) = ToSource(ox0 + dx, oy0 + dy, ow, oh, sw, sh, q, flipH, flipV);
                int i = sy * srcStride + sx * 3;
                dst[o] = s[i];
                dst[o + 1] = s[i + 1];
                dst[o + 2] = s[i + 2];
                o += 3;
            }
        });

        return new LinearRawImage(dw, dh, dst);
    }

    /// <summary>
    /// Oriented pixel → sensor pixel. The forward transform is "turn, then
    /// mirror", so this undoes the mirror first and the turn second.
    /// </summary>
    private static (int x, int y) ToSource(int ox, int oy, int ow, int oh,
                                           int sw, int sh, int q, bool flipH, bool flipV)
    {
        if (flipH) ox = ow - 1 - ox;
        if (flipV) oy = oh - 1 - oy;
        return q switch
        {
            1 => (oy, sh - 1 - ox),
            2 => (sw - 1 - ox, sh - 1 - oy),
            3 => (sw - 1 - oy, ox),
            _ => (ox, oy),
        };
    }

    /// <summary>Continuous form of <see cref="ToSource(int,int,int,int,int,int,int,bool,bool)"/>,
    /// in pixel-centre coordinates (pixel <c>i</c> spans <c>[i, i+1)</c>).</summary>
    private static (double x, double y) ToSource(double ox, double oy, double ow, double oh,
                                                 double sw, double sh, int q, bool flipH, bool flipV)
    {
        if (flipH) ox = ow - ox;
        if (flipV) oy = oh - oy;
        return q switch
        {
            1 => (oy, sh - ox),
            2 => (sw - ox, sh - oy),
            3 => (sw - oy, ox),
            _ => (ox, oy),
        };
    }

    // ── Resampling path: a straighten angle is in play ───────────────────────

    private static LinearRawImage Resample(LinearRawImage src, GeometrySettings g)
    {
        int sw = src.Width, sh = src.Height;
        int q = g.NormalisedQuadrant;
        var (ow, oh) = OrientedSize(sw, sh, q);
        var (dw, dh) = OutputSize(g, sw, sh);

        double ocx = (g.CropX + g.CropWidth * 0.5) * ow;
        double ocy = (g.CropY + g.CropHeight * 0.5) * oh;

        // The straighten turns about the crop's centre, not the image's, so the
        // box stays put under the cursor while the content pivots inside it.
        double t = AngleRadians(g);
        double cos = Math.Cos(t), sin = Math.Sin(t);

        var dst = new ushort[dw * dh * 3];
        var s = src.Pixels;
        int srcStride = sw * 3;
        double halfW = dw * 0.5, halfH = dh * 0.5;
        bool flipH = g.FlipHorizontal, flipV = g.FlipVertical;

        Parallel.For(0, dh, dy =>
        {
            double vy = dy + 0.5 - halfH;
            int o = dy * dw * 3;
            for (int dx = 0; dx < dw; dx++)
            {
                double vx = dx + 0.5 - halfW;

                // Inverse of the clockwise straighten: where in the oriented
                // frame the content now under (dx, dy) actually lives.
                double gx = ocx + vx * cos + vy * sin;
                double gy = ocy - vx * sin + vy * cos;

                var (sx, sy) = ToSource(gx, gy, ow, oh, sw, sh, q, flipH, flipV);
                Sample(s, sw, sh, srcStride, sx, sy, dst, o);
                o += 3;
            }
        });

        return new LinearRawImage(dw, dh, dst);
    }

    /// <summary>
    /// Bilinear read at continuous source coordinates, writing three channels
    /// at <paramref name="o"/>.
    ///
    /// <para>Anything outside the sensor frame is black rather than a clamped
    /// edge pixel: a straightened crop that overhangs the image should show a
    /// corner the user can see and pull back from, not a smear of the last row
    /// that reads as a rendering fault.</para>
    /// </summary>
    private static void Sample(ushort[] src, int sw, int sh, int stride,
                               double sx, double sy, ushort[] dst, int o)
    {
        if (sx < 0.0 || sy < 0.0 || sx >= sw || sy >= sh)
        {
            dst[o] = dst[o + 1] = dst[o + 2] = 0;
            return;
        }

        double fx = sx - 0.5, fy = sy - 0.5;
        int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
        double tx = fx - x0, ty = fy - y0;
        int x1 = x0 + 1, y1 = y0 + 1;

        // The centre is known to be inside, so clamping the taps only ever
        // repeats an edge row or column for the half pixel that hangs over it —
        // and where it does, both taps collapse onto the same pixel and the
        // weights below cancel out.
        if (x0 < 0) x0 = 0;
        if (y0 < 0) y0 = 0;
        if (x1 >= sw) x1 = sw - 1;
        if (y1 >= sh) y1 = sh - 1;

        int i00 = y0 * stride + x0 * 3, i10 = y0 * stride + x1 * 3;
        int i01 = y1 * stride + x0 * 3, i11 = y1 * stride + x1 * 3;
        double w00 = (1.0 - tx) * (1.0 - ty), w10 = tx * (1.0 - ty);
        double w01 = (1.0 - tx) * ty, w11 = tx * ty;

        for (int c = 0; c < 3; c++)
        {
            double v = src[i00 + c] * w00 + src[i10 + c] * w10
                     + src[i01 + c] * w01 + src[i11 + c] * w11;
            int q = (int)(v + 0.5);
            dst[o + c] = (ushort)(q < 0 ? 0 : q > 65535 ? 65535 : q);
        }
    }

    // ── The crop tool's frame ────────────────────────────────────────────────

    /// <summary>
    /// The same geometry widened to the whole straightened frame — what the crop
    /// tool renders, so the box can be dragged over the picture it is cutting
    /// from rather than over the cut result.
    ///
    /// <para>The rectangle it returns deliberately runs outside 0…1: it is the
    /// bounding box of the <i>rotated</i> image, so its corners are the black
    /// wedges a straighten opens up. Seeing them is the point — they are where
    /// the crop is not allowed to go.</para>
    /// </summary>
    public static GeometrySettings FullExtent(GeometrySettings g, int width, int height)
    {
        var (ow, oh) = OrientedSize(width, height, g.Quadrant);
        double t = AngleRadians(g);
        double c = Math.Abs(Math.Cos(t)), s = Math.Abs(Math.Sin(t));

        var extent = g.Clone();
        extent.CropWidth = (ow * c + oh * s) / ow;
        extent.CropHeight = (ow * s + oh * c) / oh;
        extent.CropX = (1.0 - extent.CropWidth) * 0.5;
        extent.CropY = (1.0 - extent.CropHeight) * 0.5;
        return extent;
    }

    /// <summary>
    /// Where the crop box sits in the pixels <see cref="FullExtent"/> renders.
    ///
    /// <para>The two frames carry the same straighten and differ only in the
    /// point it turns about — the image centre for the extent, the crop centre
    /// for the crop — so in the extent's own axes the box is still upright, just
    /// displaced. That is what lets the on-canvas rectangle stay a plain
    /// axis-aligned rectangle at any angle.</para>
    /// </summary>
    public static (double x, double y, double width, double height)
        CropRectInFullExtent(GeometrySettings g, int width, int height)
    {
        var (ow, oh) = OrientedSize(width, height, g.Quadrant);
        var (ew, eh) = OutputSize(FullExtent(g, width, height), width, height);

        double dx = (g.CropX + g.CropWidth * 0.5) * ow - ow * 0.5;
        double dy = (g.CropY + g.CropHeight * 0.5) * oh - oh * 0.5;

        double t = AngleRadians(g);
        double cos = Math.Cos(t), sin = Math.Sin(t);
        double px = dx * cos - dy * sin;
        double py = dx * sin + dy * cos;

        double cw = g.CropWidth * ow;
        double ch = g.CropHeight * oh;
        return (ew * 0.5 + px - cw * 0.5, eh * 0.5 + py - ch * 0.5, cw, ch);
    }

    /// <summary>Inverse of <see cref="CropRectInFullExtent"/>: take a box the
    /// user dragged on the crop tool's canvas and write it back as a crop.</summary>
    public static void SetCropFromFullExtent(GeometrySettings g,
                                             double x, double y, double w, double h,
                                             int width, int height)
    {
        var (ow, oh) = OrientedSize(width, height, g.Quadrant);
        var (ew, eh) = OutputSize(FullExtent(g, width, height), width, height);

        double px = x + w * 0.5 - ew * 0.5;
        double py = y + h * 0.5 - eh * 0.5;

        double t = AngleRadians(g);
        double cos = Math.Cos(t), sin = Math.Sin(t);
        double dx = px * cos + py * sin;
        double dy = -px * sin + py * cos;

        g.CropWidth = w / ow;
        g.CropHeight = h / oh;
        g.CropX = (ow * 0.5 + dx) / ow - g.CropWidth * 0.5;
        g.CropY = (oh * 0.5 + dy) / oh - g.CropHeight * 0.5;
    }

    // ── Keeping the box on the picture ───────────────────────────────────────

    /// <summary>
    /// How much the crop box could be scaled about its own centre before a
    /// corner leaves the photograph. 1 or more means it already fits.
    ///
    /// <para>Solved rather than searched: the corners move linearly in the scale
    /// factor, so each corner and axis contributes one bound and the answer is
    /// the smallest of the eight.</para>
    /// </summary>
    public static double MaxCropScale(GeometrySettings g, int width, int height)
    {
        var (ow, oh) = OrientedSize(width, height, g.Quadrant);
        double ocx = (g.CropX + g.CropWidth * 0.5) * ow;
        double ocy = (g.CropY + g.CropHeight * 0.5) * oh;

        // A centre already off the picture has no scale that saves it.
        if (ocx <= 0.0 || ocy <= 0.0 || ocx >= ow || ocy >= oh) return 0.0;

        double t = AngleRadians(g);
        double cos = Math.Cos(t), sin = Math.Sin(t);
        double hw = g.CropWidth * ow * 0.5, hh = g.CropHeight * oh * 0.5;

        double best = double.PositiveInfinity;
        for (int i = 0; i < 4; i++)
        {
            double vx = (i & 1) == 0 ? -hw : hw;
            double vy = (i & 2) == 0 ? -hh : hh;
            double px = vx * cos + vy * sin;
            double py = -vx * sin + vy * cos;
            best = Math.Min(best, Limit(ocx, px, ow));
            best = Math.Min(best, Limit(ocy, py, oh));
        }
        return best;

        static double Limit(double centre, double delta, double extent)
        {
            if (delta > 0.0) return (extent - centre) / delta;
            if (delta < 0.0) return centre / -delta;
            return double.PositiveInfinity;
        }
    }

    /// <summary>True when every corner of the crop box lands on real pixels.</summary>
    public static bool CropFits(GeometrySettings g, int width, int height)
        => MaxCropScale(g, width, height) >= 1.0;

    /// <summary>
    /// Shrink the crop box about its centre until it fits. This is what keeps
    /// the Straighten slider usable at the default full-frame crop: without it
    /// the first degree of rotation would open black wedges in all four corners.
    /// </summary>
    public static void ConstrainCrop(GeometrySettings g, int width, int height)
    {
        double s = MaxCropScale(g, width, height);
        if (!double.IsFinite(s) || s >= 1.0 || s <= 0.0) return;

        double cx = g.CropX + g.CropWidth * 0.5;
        double cy = g.CropY + g.CropHeight * 0.5;
        g.CropWidth *= s;
        g.CropHeight *= s;
        g.CropX = cx - g.CropWidth * 0.5;
        g.CropY = cy - g.CropHeight * 0.5;
    }

    /// <summary>
    /// Turn the photo by quarter steps, carrying the crop box round with it so
    /// the same part of the picture stays framed.
    ///
    /// <para>The flip flags swap on every odd step. They are applied after the
    /// turn, and a quarter turn past a mirror is the other mirror past the same
    /// turn — so without the swap, rotating a flipped photo would un-flip it.</para>
    /// </summary>
    public static void Rotate(GeometrySettings g, int steps)
    {
        int n = ((steps % 4) + 4) % 4;
        for (int i = 0; i < n; i++)
        {
            (g.CropX, g.CropY, g.CropWidth, g.CropHeight) =
                (1.0 - g.CropY - g.CropHeight, g.CropX, g.CropHeight, g.CropWidth);
            (g.FlipHorizontal, g.FlipVertical) = (g.FlipVertical, g.FlipHorizontal);
        }
        g.Quadrant = ((g.Quadrant + n) % 4 + 4) % 4;
    }

    /// <summary>
    /// Re-frame to the largest centred box of the given width:height ratio.
    ///
    /// <para>Deliberately does <i>not</i> constrain: the caller decides whether
    /// this is the box to remember or the box to display, and those differ the
    /// moment a straighten is in play — see the crop baseline in the
    /// view-model.</para>
    /// </summary>
    public static void ApplyAspect(GeometrySettings g, double ratio, int width, int height)
    {
        if (!double.IsFinite(ratio) || ratio <= 0.0) return;

        var (ow, oh) = OrientedSize(width, height, g.Quadrant);
        double w = ow, h = ow / ratio;
        if (h > oh) { h = oh; w = oh * ratio; }

        g.CropWidth = w / ow;
        g.CropHeight = h / oh;
        g.CropX = (1.0 - g.CropWidth) * 0.5;
        g.CropY = (1.0 - g.CropHeight) * 0.5;
    }
}
