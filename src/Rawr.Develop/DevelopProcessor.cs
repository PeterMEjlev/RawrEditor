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
///      base S, then the Lightroom-match calibration — is a pure function of
///      one channel value, baked once into a 65536-entry float LUT (three
///      lookups per pixel). At neutral the whole pipeline reduces to exactly
///      this composed LUT, so the baseline render matches Lightroom.
///   4. Colour (Vibrance/Saturation) needs cross-channel work, so pixels are
///      decomposed into luma + Rec.709 chroma. The chroma planes get the same
///      small box blur RAWR uses to kill high-ISO colour speckle, then the
///      saturation/vibrance scale, then recompose.
///   5. TPDF dither before the 8-bit round so the sRGB curve's compression of
///      the bright end doesn't band in skies and skin.
///
/// Same input → same output regardless of core count (per-row deterministic RNG).
///
/// The tone work (1–3) plus the luma/chroma decompose and chroma denoise (4,
/// pre-scale) are shared by the live preview and by full-resolution export via
/// <see cref="DecomposeToPlanes"/>; only the final colour-scale + quantise
/// stage differs (8-bit sRGB preview vs. 8/16-bit, sRGB/Adobe RGB export).
/// </summary>
public static class DevelopProcessor
{
    /// <summary>
    /// Render <paramref name="raw"/> with <paramref name="s"/> to a frozen BGR24
    /// <see cref="BitmapSource"/> ready to assign to an Image. Pass a cancellation
    /// token from the preview debouncer so a superseded render bails cheaply.
    ///
    /// This is the live-preview path; at neutral it reduces to the composed
    /// display LUT (camera transform → Lightroom-match), so the preview shows
    /// the same baseline the export writes. The final loop is order-/core-
    /// invariant (per-row deterministic dither) — keep it that way.
    /// </summary>
    public static BitmapSource Render(LinearRawImage raw, DevelopSettings s, CancellationToken ct = default)
    {
        var po = new ParallelOptions { CancellationToken = ct };
        var (w, h, luma, cb, cr) = DecomposeToPlanes(raw, s, po, ct);
        int stride = w * 3;
        byte[] bgr = new byte[h * stride];

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
    /// Full-quality export render. Runs the identical tone pipeline as the
    /// preview (<see cref="DecomposeToPlanes"/>) but quantises to 8- or 16-bit
    /// and, for <see cref="ExportColorSpace.AdobeRgb"/>, re-encodes each pixel
    /// from sRGB into Adobe RGB (1998). Returns a frozen Bgr24 (8-bit) or
    /// Rgb48 (16-bit) <see cref="BitmapSource"/>; the caller embeds the matching
    /// ICC profile.
    /// </summary>
    public static BitmapSource RenderExport(LinearRawImage raw, DevelopSettings s,
                                            bool sixteenBit, ExportColorSpace colorSpace,
                                            CancellationToken ct = default)
    {
        var po = new ParallelOptions { CancellationToken = ct };
        var (w, h, luma, cb, cr) = DecomposeToPlanes(raw, s, po, ct);

        float satMul = 1.12f * (1f + (float)(s.Saturation / 100.0));
        if (satMul < 0f) satMul = 0f;
        float vib = (float)(s.Vibrance / 100.0);

        bool adobe = colorSpace == ExportColorSpace.AdobeRgb;
        float max = sixteenBit ? 65535f : 255f;
        // TPDF amplitude is ±1 LSB *of the output*; in the 0..255 working space
        // a 16-bit LSB is 255/65535 of a unit, so the dither shrinks with depth.
        float ditherUnit = sixteenBit ? 255f / 65535f : 1f;

        // 8-bit → byte[] Bgr24 (matches preview layout); 16-bit → ushort[] Rgb48.
        int comps = w * h * 3;
        byte[]? bytes = sixteenBit ? null : new byte[h * (w * 3)];
        ushort[]? shorts = sixteenBit ? new ushort[comps] : null;

        Parallel.For(0, h, po, yy =>
        {
            uint rng = unchecked((uint)yy * 2654435761u + 0x12345678u);
            if (rng == 0) rng = 1;

            int rowI = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowI + x;
                float y = luma[i];
                float cbv = cb[i];
                float crv = cr[i];

                if (vib != 0f)
                {
                    float mag = MathF.Sqrt(cbv * cbv + crv * crv);
                    float weak = 1f / (1f + 0.012f * mag);
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

                // sRGB display value in 0..1 (the pipeline encodes to sRGB).
                float fr = lr * (1f / 255f); if (fr < 0f) fr = 0f; else if (fr > 1f) fr = 1f;
                float fg = lg * (1f / 255f); if (fg < 0f) fg = 0f; else if (fg > 1f) fg = 1f;
                float fb = lb * (1f / 255f); if (fb < 0f) fb = 0f; else if (fb > 1f) fb = 1f;

                if (adobe) SrgbToAdobeRgb(ref fr, ref fg, ref fb);

                // Same TPDF stream structure as the preview, scaled to the
                // target depth so 16-bit gets a correspondingly finer dither.
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ar = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float br = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dr = (ar + br) * ditherUnit;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ag = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bg = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dg = (ag + bg) * ditherUnit;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ab = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bb = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float db = (ab + bb) * ditherUnit;

                int R = Quant(fr, dr, max);
                int G = Quant(fg, dg, max);
                int B = Quant(fb, db, max);

                if (sixteenBit)
                {
                    int o = i * 3;
                    shorts![o]     = (ushort)R;
                    shorts[o + 1]  = (ushort)G;
                    shorts[o + 2]  = (ushort)B;
                }
                else
                {
                    int o = yy * (w * 3) + x * 3;
                    bytes![o]     = (byte)B;
                    bytes[o + 1]  = (byte)G;
                    bytes[o + 2]  = (byte)R;
                }
            }
        });

        ct.ThrowIfCancellationRequested();
        BitmapSource result = sixteenBit
            ? BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb48, null, shorts!, w * 3 * sizeof(ushort))
            : BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bytes!, w * 3);
        result.Freeze();
        return result;
    }

    /// <summary>Quantise a 0..1 display value to [0, <paramref name="max"/>]
    /// with the supplied (already depth-scaled, 0..255-space) TPDF dither.</summary>
    private static int Quant(float f01, float dither255, float max)
    {
        // Work in 0..255 units (where the dither lives), then scale to target.
        float v = f01 * 255f + dither255;
        int q = (int)(v * (max / 255f) + 0.5f);
        if (q < 0) q = 0; else if (q > (int)max) q = (int)max;
        return q;
    }

    // sRGB display value (0..1, sRGB primaries/TRC) → Adobe RGB (1998) display
    // value (0..1, Adobe RGB primaries, γ 2.19921875). Decode sRGB → linear,
    // rotate primaries (both D65, no chromatic adaptation), re-encode.
    private static void SrgbToAdobeRgb(ref float r, ref float g, ref float b)
    {
        double lr = SrgbToLinear(r);
        double lg = SrgbToLinear(g);
        double lb = SrgbToLinear(b);

        // linear sRGB → linear Adobe RGB (D65). Standard conversion matrix.
        double ar = 0.71516490 * lr + 0.28483510 * lg;
        double ag = lg;
        double ab = 0.04117138 * lg + 0.95882862 * lb;

        if (ar < 0.0) ar = 0.0; else if (ar > 1.0) ar = 1.0;
        if (ag < 0.0) ag = 0.0; else if (ag > 1.0) ag = 1.0;
        if (ab < 0.0) ab = 0.0; else if (ab > 1.0) ab = 1.0;

        const double invGamma = 1.0 / 2.19921875;
        r = (float)Math.Pow(ar, invGamma);
        g = (float)Math.Pow(ag, invGamma);
        b = (float)Math.Pow(ab, invGamma);
    }

    private static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>
    /// The shared tone pipeline: per-channel gain → EV-space H/S/C → endpoint
    /// remap → display LUT → luma/chroma decompose → chroma denoise. Returns
    /// the post-blur planes in 0..255 perceptual units (luma) and Rec.709
    /// chroma differences. Both <see cref="Render"/> and
    /// <see cref="RenderExport"/> finish from here, so preview and export run
    /// identical maths and differ only in the final colour-scale/quantise.
    /// </summary>
    private static (int w, int h, float[] luma, float[] cb, float[] cr)
        DecomposeToPlanes(LinearRawImage raw, DevelopSettings s, ParallelOptions po, CancellationToken ct)
    {
        int w = raw.Width;
        int h = raw.Height;
        int n = w * h;
        ushort[] src = raw.Pixels;

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
        //
        // Per-slider Version selects which BasicTone formula runs: v1 is RAWR's
        // original math, v2 the darktable-inspired global model, v3 the edge-
        // aware (Lightroom-feel) variant that reads a regional-luminance plane.
        // When every slider in a block is v1 we take the combined fast path
        // (one luminance / log / pow per pixel for HSL; one (lr−bk)/span for
        // ends) which is byte-identical to the original render. As soon as
        // any v2/v3 appears the block falls back to per-slider passes; v3 in
        // addition triggers the optional Pass 0 below.
        double hl = s.Highlights;
        double sh = s.Shadows;
        double co = s.Contrast;
        double wh = s.Whites;
        double bk = s.Blacks;
        int contrastV    = s.ContrastVersion;
        int highlightsV  = s.HighlightsVersion;
        int shadowsV     = s.ShadowsVersion;
        int whitesV      = s.WhitesVersion;
        int blacksV      = s.BlacksVersion;
        double contrastSlope = BasicTone.ContrastSlope(co);
        double whiteLin = BasicTone.WhiteLin(wh);
        double blackLin = BasicTone.BlackLin(bk);
        double invSpan = 1.0 / (whiteLin - blackLin);
        // Fast paths only fire when *every* slider in the block is v1 (the
        // original combined formula). As soon as one slider is on v2/v3 we
        // fall through to per-slider passes.
        bool combinedHsl  = highlightsV == 1 && shadowsV == 1 && contrastV == 1;
        bool combinedEnds = whitesV == 1 && blacksV == 1;
        bool doEv = hl != 0.0 || sh != 0.0 || co != 0.0;
        bool doEnds = wh != 0.0 || bk != 0.0;
        // Edge-aware/context path: only build the regional luminance plane when
        // a nonzero slider version will actually read it. This keeps neutral
        // renders cheap even though Highlights defaults to the calibrated v4.
        bool needsEvBase =
            (hl != 0.0 && (highlightsV == 3 || highlightsV == 4)) ||
            (sh != 0.0 && shadowsV == 3) ||
            (wh != 0.0 && whitesV == 3) ||
            (bk != 0.0 && blacksV == 3);
        const double inv65535 = 1.0 / 65535.0;

        // ── Display LUT: normalised linear → fractional 8-bit perceptual ──
        // Pure camera-matched output transform (sRGB → midtone match → base
        // S). No slider terms — those are the EV-space block above.
        float[] lut = BuildDisplayLut();

        // ── Pass 0 (conditional): regional luminance plane for v3 sliders ──
        // Build the post-gain Y plane, then run EdgeAwareLuma's guided filter
        // to get evBase[i] = log2(Y_region / MiddleGray). v3 H/S/W/B read this
        // for their masks; v4 Highlights uses it only for contextual recovery
        // inside clipped highlight regions. Skipped entirely when every active
        // relevant slider ignores evBase.
        float[]? evBase = null;
        double highlightDarkSceneBoost = 0.0;
        if (needsEvBase)
        {
            var yLinear = new float[n];
            Parallel.For(0, h, po, yy =>
            {
                int rowI = yy * w;
                int srcIdx = rowI * 3;
                for (int x = 0; x < w; x++)
                {
                    double lr = src[srcIdx]     * gainR * inv65535;
                    double lg = src[srcIdx + 1] * gainG * inv65535;
                    double lb = src[srcIdx + 2] * gainB * inv65535;
                    yLinear[rowI + x] = (float)BasicTone.Luminance(lr, lg, lb);
                    srcIdx += 3;
                }
            });
            if (hl != 0.0 && highlightsV == 4)
                highlightDarkSceneBoost = EstimateHighlightDarkSceneBoost(yLinear, lut);
            evBase = EdgeAwareLuma.BuildEvBase(yLinear, w, h, po);
        }

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

                // evB only used by v3 dispatchers; 0.0 is fine for v1/v2.
                double evB = evBase != null ? evBase[rowI + x] : 0.0;

                if (doEv)
                {
                    if (combinedHsl)
                    {
                        // Fast path: all of highlights/shadows/contrast on v1 ⇒
                        // RAWR's original combined EV-space ratio. Byte-identical
                        // to the pre-versioning render.
                        BasicTone.ApplyHighlightShadowContrast(
                            ref lr, ref lg, ref lb, hl, sh, contrastSlope);
                    }
                    else
                    {
                        // Mixed-version: per-slider passes, in pipeline order.
                        if (hl != 0.0) BasicTone.ApplyHighlights(
                            highlightsV, ref lr, ref lg, ref lb, hl, evB, highlightDarkSceneBoost);
                        if (sh != 0.0) BasicTone.ApplyShadows(shadowsV, ref lr, ref lg, ref lb, sh, evB);
                        if (co != 0.0) BasicTone.ApplyContrast(contrastV, ref lr, ref lg, ref lb, co, contrastSlope);
                    }
                }

                if (doEnds)
                {
                    if (combinedEnds)
                    {
                        // Fast path: both endpoints on v1 ⇒ original combined remap.
                        lr = (lr - blackLin) * invSpan;
                        lg = (lg - blackLin) * invSpan;
                        lb = (lb - blackLin) * invSpan;
                    }
                    else
                    {
                        if (wh != 0.0) BasicTone.ApplyWhites(whitesV, ref lr, ref lg, ref lb, wh, evB);
                        if (bk != 0.0) BasicTone.ApplyBlacks(blacksV, ref lr, ref lg, ref lb, bk, evB);
                    }
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

        return (w, h, luma, cb, cr);
    }

    /// <summary>
    /// 65536-entry table: normalised-linear value → fractional 8-bit
    /// perceptual [0..255]. It composes <see cref="BasicTone.DisplayCurve"/>
    /// (RAWR's original camera-matched transform: sRGB encode → midtone lift →
    /// gentle base S) with <see cref="BasicTone.LightroomMatch"/> (the
    /// empirical calibration that pulls the result onto Lightroom's default
    /// look). The slider tone work happens upstream in EV space, so this table
    /// is still a pure, settings-independent output transform — at neutral the
    /// whole pipeline reduces to exactly this composed curve, so the baseline
    /// render now matches Lightroom rather than the old camera look.
    /// Fractional output is what lets the dither resolve banding into gradient.
    /// </summary>
    private static float[] BuildDisplayLut()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
            lut[i] = (float)(BasicTone.LightroomMatch(BasicTone.DisplayCurve(i / 65535.0)) * 255.0);
        return lut;
    }

    /// <summary>
    /// Lightroom's Highlights slider becomes much more global on dark frames
    /// with substantial clipped/highlight content. The set-3 calibration case
    /// shows this clearly: even rendered near-black pixels move by ~0.45 EV at
    /// Highlights -100, while normal set-2 images only need the gentler broad
    /// v4 curve. Estimate that scene class from the neutral rendered luma
    /// distribution so the extra v4 dark-scene term only engages where the
    /// calibration data says LR behaves this way.
    /// </summary>
    private static double EstimateHighlightDarkSceneBoost(float[] yLinear, float[] displayLut)
    {
        int n = yLinear.Length;
        if (n == 0) return 0.0;

        int deepBlack = 0;
        double sumDisplayLin = 0.0;
        const double inv255 = 1.0 / 255.0;

        for (int i = 0; i < n; i++)
        {
            double y = yLinear[i];
            int iy = (int)(y * 65535.0 + 0.5);
            if (iy < 0) iy = 0; else if (iy > 65535) iy = 65535;

            // The comparison scripts linearise Adobe RGB exports with gamma
            // ≈2.2, so use the same style of display-linear proxy here.
            double yd = Math.Pow(displayLut[iy] * inv255, 2.19921875);
            sumDisplayLin += yd;
            if (yd <= 0.003) deepBlack++;
        }

        double blackFrac = (double)deepBlack / n;
        double meanDisplayLin = sumDisplayLin / n;

        double darkMass = BasicTone.SmoothStep(0.05, 0.45, blackFrac);
        double lowMean = 1.0 - BasicTone.SmoothStep(0.25, 0.50, meanDisplayLin);
        double boost = darkMass * lowMean;
        return boost < 0.0 ? 0.0 : boost > 1.0 ? 1.0 : boost;
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
