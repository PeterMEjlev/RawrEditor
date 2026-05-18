using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.Develop;

/// <summary>
/// Renders a <see cref="DevelopSettings"/> over a 16-bit linear <see cref="LinearRawImage"/>
/// into a displayable BGR24 bitmap.
///
/// This is RAWR's <c>ExposureProcessor.Render</c> generalised from "exposure only"
/// to the full Basic panel. The architecture is unchanged because it was already
/// the right one:
///
///   1. White balance + exposure are per-channel <i>linear</i> gains, applied to
///      the 16-bit sensor value before anything else. Clipping at 65535 is the
///      real sensor ceiling — pulling exposure down recovers highlights that a
///      baked JPEG would have lost.
///   2. Everything that is a pure function of a single channel value (sRGB
///      encode, the camera-match midtone lift, and the Whites/Blacks/Shadows/
///      Highlights/Contrast tone curve) is baked once per render into a
///      65536-entry float LUT. Per pixel it is three table lookups.
///   3. Colour (Vibrance/Saturation) needs cross-channel work, so pixels are
///      decomposed into luma + Rec.709 chroma. The chroma planes get the same
///      small box blur RAWR uses to kill high-ISO colour speckle, then the
///      saturation/vibrance scale, then recompose.
///   4. TPDF dither before the 8-bit round so the sRGB curve's compression of
///      the bright end doesn't band in skies and skin.
///
/// Same input → same output regardless of core count (per-row deterministic RNG).
/// </summary>
public static class DevelopProcessor
{
    /// <summary>
    /// Render <paramref name="raw"/> with <paramref name="s"/> to a frozen BGR24
    /// <see cref="BitmapSource"/> ready to assign to an Image. Pass a cancellation
    /// token from the preview debouncer so a superseded render bails cheaply.
    /// </summary>
    public static BitmapSource Render(LinearRawImage raw, DevelopSettings s, CancellationToken ct = default)
    {
        int w = raw.Width;
        int h = raw.Height;
        int n = w * h;
        int stride = w * 3;
        byte[] bgr = new byte[h * stride];
        ushort[] src = raw.Pixels;
        var po = new ParallelOptions { CancellationToken = ct };

        // ── Per-channel linear gain: exposure × white balance ──
        double expGain = Math.Pow(2.0, s.Exposure);
        double t = Math.Clamp(s.Temperature / 100.0, -1.0, 1.0);
        double ti = Math.Clamp(s.Tint / 100.0, -1.0, 1.0);
        // Warm (+T) lifts red, drops blue. Tint +/- trades green against magenta.
        // Factors stay well inside (0, 2) for the full slider travel.
        double gainR = expGain * (1.0 + 0.45 * t);
        double gainB = expGain * (1.0 - 0.45 * t);
        double gainG = expGain * (1.0 - 0.30 * ti);

        // ── Tone LUT: 16-bit linear → fractional 8-bit perceptual ──
        float[] lut = BuildToneLut(s);

        // ── Pass 1: gain + clip + LUT, decompose to luma/chroma ──
        float[] luma = new float[n];
        float[] cb = new float[n]; // B − Y
        float[] cr = new float[n]; // R − Y

        Parallel.For(0, h, po, yy =>
        {
            int rowI = yy * w;
            int srcIdx = rowI * 3;
            for (int x = 0; x < w; x++)
            {
                int r = (int)(src[srcIdx]     * gainR);
                int g = (int)(src[srcIdx + 1] * gainG);
                int b = (int)(src[srcIdx + 2] * gainB);
                if (r > 65535) r = 65535; else if (r < 0) r = 0;
                if (g > 65535) g = 65535; else if (g < 0) g = 0;
                if (b > 65535) b = 65535; else if (b < 0) b = 0;

                float lr = lut[r];
                float lg = lut[g];
                float lb = lut[b];
                float y = 0.2126f * lr + 0.7152f * lg + 0.0722f * lb;
                int i = rowI + x;
                luma[i] = y;
                cb[i] = lb - y;
                cr[i] = lr - y;
                srcIdx += 3;
            }
        });

        // ── Chroma denoise: separable 5-tap box on the colour planes only ──
        BoxBlurSeparable(cb, w, h, 2, po);
        BoxBlurSeparable(cr, w, h, 2, po);

        // ── Colour scale: baseline pop + Saturation + chroma-aware Vibrance ──
        // 1.12 baseline keeps the neutral render's gentle camera-like saturation.
        float satMul = 1.12f * (1f + (float)(s.Saturation / 100.0));
        if (satMul < 0f) satMul = 0f;
        float vib = (float)(s.Vibrance / 100.0);

        Parallel.For(0, h, po, yy =>
        {
            // Per-row XorShift seed (Knuth constant + offset) — independent
            // deterministic dither stream per row, scheduling-invariant.
            uint rng = unchecked((uint)yy * 2654435761u + 0x12345678u);
            if (rng == 0) rng = 1;

            int row = yy * stride;
            int rowI = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowI + x;
                float y = luma[i];
                float cbv = cb[i];
                float crv = cr[i];

                // Vibrance: boost weak colours more than already-saturated ones.
                // Chroma magnitude is in 0..~180 (8-bit-ish luma units); the
                // 0.012 scale puts the rolloff knee around mid-saturation.
                if (vib != 0f)
                {
                    float mag = MathF.Sqrt(cbv * cbv + crv * crv);
                    float weak = 1f / (1f + 0.012f * mag);   // 1 → 0 as colour strengthens
                    float vibMul = 1f + vib * weak;
                    if (vibMul < 0f) vibMul = 0f;
                    cbv *= vibMul;
                    crv *= vibMul;
                }
                cbv *= satMul;
                crv *= satMul;

                float lr = y + crv;
                float lb = y + cbv;
                float lg = (y - 0.2126f * lr - 0.0722f * lb) * (1f / 0.7152f);

                // TPDF dither (sum of two uniforms) per channel, neutral-coloured.
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ar = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float br = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dr = ar + br;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ag = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bg = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dg = ag + bg;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ab = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bb = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float db = ab + bb;

                int rv = (int)(lr + dr + 0.5f);
                int gv = (int)(lg + dg + 0.5f);
                int bv = (int)(lb + db + 0.5f);

                int o = row + x * 3;
                bgr[o]     = (byte)(bv < 0 ? 0 : bv > 255 ? 255 : bv);
                bgr[o + 1] = (byte)(gv < 0 ? 0 : gv > 255 ? 255 : gv);
                bgr[o + 2] = (byte)(rv < 0 ? 0 : rv > 255 ? 255 : rv);
            }
        });

        ct.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgr, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// 65536-entry table: 16-bit linear value → fractional 8-bit perceptual
    /// [0..255]. Bakes, in Lightroom-ish order: sRGB encode → camera-match
    /// midtone lift → Blacks/Whites endpoints → Shadows/Highlights regional
    /// lifts → Contrast S-curve. Fractional output is what makes the dither
    /// resolve banding back into smooth gradient.
    /// </summary>
    private static float[] BuildToneLut(DevelopSettings s)
    {
        // Slider → working strength. 0.70 base midtone lift matches RAWR's
        // neutral preview (camera JPEGs lift midtones well above a pure sRGB
        // encode); without it RAW previews look markedly darker than the JPG.
        const double midtoneLift = 0.70;

        double kBl = s.Blacks / 100.0;       // − crush / + lift the toe
        double kW  = s.Whites / 100.0;       // − pull in / + extend the shoulder
        double kSh = s.Shadows / 100.0;      // broad lower-mid lift
        double kHl = s.Highlights / 100.0;   // broad upper-mid recover (−) / push (+)
        double kC  = s.Contrast / 100.0;     // S-curve strength about mid-grey

        // A light base S-curve is always present for the camera look; Contrast
        // scales it up, or flattens toward linear when negative.
        const double baseContrast = 0.18;
        double contrastBlend = kC >= 0
            ? baseContrast + (1.0 - baseContrast) * kC      // 0.18 → 1.0
            : baseContrast * (1.0 + kC);                    // 0.18 → 0.0
        const double tanhSlope = 2.0;
        double tanhNorm = Math.Tanh(tanhSlope);

        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
        {
            double linear = i / 65535.0;

            // 1. linear → sRGB
            double srgb = linear <= 0.0031308
                ? 12.92 * linear
                : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;

            // 2. camera-match midtone lift
            double p = Math.Pow(srgb, midtoneLift);

            // 3. Blacks — toe, weight strongest at p=0, gone by ~p=0.5
            double wBlk = Cube(1.0 - p);
            p += kBl * 0.30 * wBlk;

            // 4. Whites — shoulder, weight strongest at p=1
            double wWht = Cube(p);
            p += kW * 0.30 * wWht;

            // 5. Shadows — broad lower-mid lift. Bell-shaped weight anchored at
            // pure black: deep blacks stay black on +Shadows (no grey wash),
            // peak effect lands in the actual shadow region (~p=0.20), fades
            // by mid-grey. This is the Lightroom distinction vs Blacks: Blacks
            // moves the endpoint, Shadows is a regional curve that preserves it.
            p += kSh * 0.45 * ShadowsWeight(p);

            // 6. Highlights — broad upper-mid; − recovers, + pushes. Anchored
            // at pure white so spectral highlights aren't dragged to grey on
            // recovery; peak lands at ~p=0.78, fades by mid-grey.
            p += kHl * 0.45 * HighlightsWeight(p);

            // 7. Contrast — tanh S about 0.5
            if (contrastBlend > 0.0)
            {
                double tt = p * 2.0 - 1.0;
                double sCurve = (Math.Tanh(tt * tanhSlope) / tanhNorm + 1.0) * 0.5;
                p = p * (1.0 - contrastBlend) + sCurve * contrastBlend;
            }

            if (p < 0.0) p = 0.0; else if (p > 1.0) p = 1.0;
            lut[i] = (float)(p * 255.0);
        }
        return lut;
    }

    private static double Cube(double v) => v <= 0 ? 0 : v >= 1 ? 1 : v * v * v;

    /// <summary>
    /// Bell weight for the Shadows region: 0 at pure black, smoothsteps up to
    /// 1 at p≈0.20, smoothsteps back to 0 by p≈0.55. The anchor at p=0 is what
    /// keeps deep blacks from greying out when +Shadows is dialed up.
    /// </summary>
    private static double ShadowsWeight(double p)
    {
        const double peak = 0.20;
        const double hi = 0.55;
        if (p <= 0.0 || p >= hi) return 0.0;
        double u = p < peak ? p / peak : (hi - p) / (hi - peak);
        return u * u * (3.0 - 2.0 * u);
    }

    /// <summary>
    /// Bell weight for the Highlights region, mirror of <see cref="ShadowsWeight"/>:
    /// 0 below p=0.45, 1 at p≈0.78, 0 at pure white. The anchor at p=1 preserves
    /// spectral highlights when −Highlights is used to recover bright areas.
    /// </summary>
    private static double HighlightsWeight(double p)
    {
        const double lo = 0.45;
        const double peak = 0.78;
        if (p <= lo || p >= 1.0) return 0.0;
        double u = p < peak ? (p - lo) / (peak - lo) : (1.0 - p) / (1.0 - peak);
        return u * u * (3.0 - 2.0 * u);
    }

    // Sliding-window box blur, horizontal then vertical, edges clamp-extend.
    private static void BoxBlurSeparable(float[] plane, int w, int h, int radius, ParallelOptions po)
    {
        int taps = radius * 2 + 1;
        float inv = 1f / taps;
        var tmp = new float[plane.Length];

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
            {
                int xc = k < 0 ? 0 : k >= w ? w - 1 : k;
                sum += plane[row + xc];
            }
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                int addX = x + radius + 1;
                int subX = x - radius;
                if (addX > w - 1) addX = w - 1;
                if (subX < 0) subX = 0;
                sum += plane[row + addX] - plane[row + subX];
            }
        });

        Parallel.For(0, w, po, x =>
        {
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
            {
                int yc = k < 0 ? 0 : k >= h ? h - 1 : k;
                sum += tmp[yc * w + x];
            }
            for (int y = 0; y < h; y++)
            {
                plane[y * w + x] = sum * inv;
                int addY = y + radius + 1;
                int subY = y - radius;
                if (addY > h - 1) addY = h - 1;
                if (subY < 0) subY = 0;
                sum += tmp[addY * w + x] - tmp[subY * w + x];
            }
        });
    }
}
