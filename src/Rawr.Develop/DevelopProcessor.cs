using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.Develop;

/// <summary>
/// Renders a <see cref="DevelopSettings"/> over a 16-bit linear <see cref="LinearRawImage"/>
/// into a displayable BGR24 bitmap.
///
/// This is RAWR's <c>ExposureProcessor.Render</c> generalised from "exposure
/// only" to the full Basic panel, with the Lightroom-like tone model in
/// <see cref="BasicTone"/>:
///
///   1. White balance + Exposure are per-channel <i>linear</i> gains applied
///      to the sensor value (Exposure = 2^EV). Values are kept unclamped from
///      here on so highlight detail above the old 65535 ceiling survives.
///   2. Highlights / Shadows / Contrast run in scene-linear log-EV space as a
///      single hue-preserving luminance ratio; Whites / Blacks then remap the
///      linear endpoints. All five are no-ops at neutral.
///   3. The display transform — sRGB encode, camera-match midtone lift, gentle
///      base S — is a pure function of one channel value, baked once into a
///      65536-entry float LUT (three lookups per pixel). At neutral the whole
///      pipeline reduces to exactly this LUT, byte-identical to the original.
///   4. Colour (Vibrance/Saturation) needs cross-channel work, so pixels are
///      decomposed into luma + Rec.709 chroma. The chroma planes get the same
///      small box blur RAWR uses to kill high-ISO colour speckle, then the
///      saturation/vibrance scale, then recompose.
///   5. TPDF dither before the 8-bit round so the sRGB curve's compression of
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
        double expGain = BasicTone.ExposureGain(s.Exposure);
        double t = Math.Clamp(s.Temperature / 100.0, -1.0, 1.0);
        double ti = Math.Clamp(s.Tint / 100.0, -1.0, 1.0);
        // Warm (+T) lifts red, drops blue. Tint +/- trades green against magenta.
        // Factors stay well inside (0, 2) for the full slider travel.
        double gainR = expGain * (1.0 + 0.45 * t);
        double gainB = expGain * (1.0 - 0.45 * t);
        double gainG = expGain * (1.0 - 0.30 * ti);

        // ── Highlights/Shadows/Contrast/Whites/Blacks: EV-space, linear ──
        // These five run in scene-linear space *before* the display curve,
        // exactly RAWR's recommended order (… → Exposure → Highlights/Shadows
        // → Contrast → Whites/Blacks endpoints → output transform). All slider
        // strengths are hoisted to per-render constants; at neutral every flag
        // is false so this is a true no-op and the render is byte-identical to
        // the original camera-matched look (which now lives wholly in the LUT).
        double hl = s.Highlights;
        double sh = s.Shadows;
        double contrastSlope = BasicTone.ContrastSlope(s.Contrast);
        double whiteLin = BasicTone.WhiteLin(s.Whites);
        double blackLin = BasicTone.BlackLin(s.Blacks);
        double invSpan = 1.0 / (whiteLin - blackLin);
        bool doEv = s.Highlights != 0.0 || s.Shadows != 0.0 || s.Contrast != 0.0;
        bool doEnds = s.Whites != 0.0 || s.Blacks != 0.0;
        const double inv65535 = 1.0 / 65535.0;

        // ── Display LUT: normalised linear → fractional 8-bit perceptual ──
        // Pure camera-matched output transform (sRGB → midtone match → base
        // S). No slider terms — those are the EV-space block above.
        float[] lut = BuildDisplayLut();

        // ── Pass 1: gain → EV tone → endpoints → LUT, decompose luma/chroma ──
        float[] luma = new float[n];
        float[] cb = new float[n]; // B − Y
        float[] cr = new float[n]; // R − Y

        Parallel.For(0, h, po, yy =>
        {
            int rowI = yy * w;
            int srcIdx = rowI * 3;
            for (int x = 0; x < w; x++)
            {
                // Scene-linear, normalised so 1.0 = sensor saturation. Kept
                // unclamped through the tone math so highlight detail above
                // the old 65535 ceiling survives until Whites decides it.
                double lr = src[srcIdx]     * gainR * inv65535;
                double lg = src[srcIdx + 1] * gainG * inv65535;
                double lb = src[srcIdx + 2] * gainB * inv65535;

                if (doEv)
                    BasicTone.ApplyHighlightShadowContrast(
                        ref lr, ref lg, ref lb, hl, sh, contrastSlope);

                if (doEnds)
                {
                    lr = (lr - blackLin) * invSpan;
                    lg = (lg - blackLin) * invSpan;
                    lb = (lb - blackLin) * invSpan;
                }

                int ir = (int)(lr * 65535.0 + 0.5);
                int ig = (int)(lg * 65535.0 + 0.5);
                int ib = (int)(lb * 65535.0 + 0.5);
                if (ir < 0) ir = 0; else if (ir > 65535) ir = 65535;
                if (ig < 0) ig = 0; else if (ig > 65535) ig = 65535;
                if (ib < 0) ib = 0; else if (ib > 65535) ib = 65535;

                float pr = lut[ir];
                float pg = lut[ig];
                float pb = lut[ib];
                float y = 0.2126f * pr + 0.7152f * pg + 0.0722f * pb;
                int i = rowI + x;
                luma[i] = y;
                cb[i] = pb - y;
                cr[i] = pr - y;
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
    /// 65536-entry table: normalised-linear value → fractional 8-bit
    /// perceptual [0..255], via <see cref="BasicTone.DisplayCurve"/> only
    /// (sRGB encode → camera-match midtone lift → gentle base S-curve). The
    /// slider tone work happens upstream in EV space, so this table is the
    /// pure output transform and is independent of the settings — at neutral
    /// the whole pipeline reduces to exactly this curve. Fractional output is
    /// what lets the dither resolve banding back into smooth gradient.
    /// </summary>
    private static float[] BuildDisplayLut()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
            lut[i] = (float)(BasicTone.DisplayCurve(i / 65535.0) * 255.0);
        return lut;
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
