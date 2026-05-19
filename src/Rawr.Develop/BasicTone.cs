namespace Rawr.Develop;

/// <summary>
/// Lightroom-<i>like</i> Basic-panel tone math, expressed in scene-linear /
/// log-EV space. This is a clean reimplementation of the well-known ideas
/// (EV gain, log-luminance contrast about middle grey, EV-masked region
/// lifts, endpoint-driven white/black points) — it is NOT Adobe's
/// proprietary tone engine and copies no GPL code from darktable/RawTherapee.
///
/// Every function here is pure and UI-free so the slider behaviour can be
/// unit-tested against synthetic ramps without standing up WPF. <see
/// cref="DevelopProcessor"/> calls exactly these functions, so the tests
/// exercise the real preview/export path.
///
/// <para><b>Neutral parity.</b> The constants are chosen so that with every
/// slider at 0 the pipeline is a no-op in linear space; the displayed/exported
/// baseline is then <see cref="DisplayCurve"/> (RAWR's original camera-matched
/// sRGB encode → midtone-match power → gentle base S, kept here verbatim as
/// the reference) composed with <see cref="LightroomMatch"/>, the empirical
/// calibration that lands that baseline on Lightroom's default rendering.
/// Notably <see cref="WhiteLin"/> is 1.0 and
/// <see cref="BlackLin"/> is 0.0 at neutral, so the endpoint remap is the
/// identity — this is the deliberate deviation from a raw "baseWhiteEv=3.5 /
/// baseBlackEv=−8" formulation, which would shift the neutral image.</para>
/// </summary>
public static class BasicTone
{
    // ── Scene-referred anchor ──
    /// <summary>Scene-linear middle grey. ev = 0 here, so contrast (which
    /// scales ev) leaves a correctly-exposed midtone exactly put.</summary>
    public const double MiddleGray = 0.18;

    /// <summary>Floor for the luma → log conversion so pure black doesn't
    /// produce −∞; also the threshold below which the ratio step is skipped
    /// (near-black carries no usable hue to preserve anyway).</summary>
    public const double LumaEps = 1e-6;

    // ── Slider strengths (stops at ±100 before the region mask) ──
    // Halved from the first tuning so each increment is half as effective:
    // slider 100 now lands where 50 used to. Exposure is deliberately NOT
    // here — it stays true 2^EV (a physical stop, not an arbitrary scale).
    public const double HighlightStrength = 1.0;   // Highlights: ± up to 1 EV in the bright band
    public const double ShadowStrength    = 1.5;   // Shadows:    ± up to 1.5 EV in the dark band
    public const double ContrastStrength  = 0.375; // Contrast:   ev *= exp(0.375 · slider)
    public const double WhiteRangeStops   = 1.0;   // Whites:     ±1 EV of white-point travel
    public const double BlackPointRange   = 0.02;  // Blacks:     ±0.02 linear of black-point travel

    // ── Display transform (RAWR's original camera-matched look, unchanged) ──
    public const double MidtoneLift  = 0.70;       // camera JPEGs sit well above a pure sRGB encode
    public const double BaseContrast = 0.18;       // gentle always-on S-curve for the camera look
    public const double TanhSlope    = 2.0;

    /// <summary>Exposure as true EV gain: a linear multiplier of 2^stops,
    /// applied to scene-linear RGB before everything else.</summary>
    public static double ExposureGain(double exposureEv) => Math.Pow(2.0, exposureEv);

    /// <summary>Rec.709 luma of a linear RGB triplet.</summary>
    public static double Luminance(double r, double g, double b)
        => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    /// <summary>Hermite smoothstep, clamped, edge0 → edge1 ⇒ 0 → 1.</summary>
    public static double SmoothStep(double edge0, double edge1, double x)
    {
        if (edge1 == edge0) return x < edge0 ? 0.0 : 1.0;
        double u = (x - edge0) / (edge1 - edge0);
        u = u < 0.0 ? 0.0 : u > 1.0 ? 1.0 : u;
        return u * u * (3.0 - 2.0 * u);
    }

    /// <summary>Linear luma → stops relative to middle grey.</summary>
    public static double Ev(double luma)
        => Math.Log2((luma < LumaEps ? LumaEps : luma) / MiddleGray);

    /// <summary>Stops relative to middle grey → linear luma.</summary>
    public static double LumaFromEv(double ev) => MiddleGray * Math.Pow(2.0, ev);

    /// <summary>Per-render constant: the Contrast slider as a multiplicative
    /// slope on the EV distance from middle grey. 0 → 1 (no-op).</summary>
    public static double ContrastSlope(double contrast)
        => Math.Exp(ContrastStrength * contrast / 100.0);

    /// <summary>
    /// The Highlights + Shadows + Contrast block, in EV space. Highlights act
    /// only in the bright band (mask 0 at/below middle grey, ramping in by
    /// ev≈+3); Shadows only in the dark band (mask 1 in deep shadow, 0 by
    /// middle grey); Contrast then scales the result's distance from middle
    /// grey. At neutral (0,0,slope 1) this returns <paramref name="ev"/>
    /// unchanged, and ev=0 (middle grey) is always a fixed point of contrast.
    /// </summary>
    public static double AdjustedEv(double ev, double highlights, double shadows, double contrastSlope)
    {
        double maskHi = SmoothStep(0.0, 3.0, ev);
        double maskLo = 1.0 - SmoothStep(-4.0, 0.0, ev);
        double dHi = highlights / 100.0 * HighlightStrength * maskHi;
        double dLo = shadows    / 100.0 * ShadowStrength    * maskLo;
        return (ev + dHi + dLo) * contrastSlope;
    }

    /// <summary>
    /// Highlights/Shadows/Contrast applied to a linear RGB triplet as a single
    /// luminance ratio, which preserves hue and chroma. Returns the triplet
    /// unchanged for near-black input. <paramref name="contrastSlope"/> is the
    /// hoisted per-render constant from <see cref="ContrastSlope"/>.
    /// </summary>
    public static void ApplyHighlightShadowContrast(
        ref double r, ref double g, ref double b,
        double highlights, double shadows, double contrastSlope)
    {
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double ev = Math.Log2(y / MiddleGray);
        double newY = LumaFromEv(AdjustedEv(ev, highlights, shadows, contrastSlope));
        double ratio = newY / y;
        r *= ratio; g *= ratio; b *= ratio;
    }

    /// <summary>
    /// Linear value that the tone mapper drives to display white. Whites &gt; 0
    /// lowers it (more pixels clip to white); Whites &lt; 0 raises it
    /// (highlights protected). Exactly 1.0 at neutral ⇒ identity remap.
    /// </summary>
    public static double WhiteLin(double whites)
        => Math.Pow(2.0, -(whites / 100.0) * WhiteRangeStops);

    /// <summary>
    /// Linear value that the tone mapper drives to display black. Blacks &lt; 0
    /// raises it (more pixels clip to black); Blacks &gt; 0 makes it negative
    /// (shadows opened, less clipping). Exactly 0.0 at neutral ⇒ identity remap.
    /// </summary>
    public static double BlackLin(double blacks)
        => -(blacks / 100.0) * BlackPointRange;

    /// <summary>
    /// Endpoint remap: black point → 0, white point → 1. This is the
    /// Whites/Blacks "tone-map" stage; the result is fed to <see
    /// cref="DisplayCurve"/> (values are intentionally NOT clamped here —
    /// clipping happens at the display stage).
    /// </summary>
    public static double RemapEndpoints(double lin, double blackLin, double whiteLin)
    {
        double span = whiteLin - blackLin;
        if (span < 1e-9) span = 1e-9;
        return (lin - blackLin) / span;
    }

    /// <summary>
    /// RAWR's original camera-matched display transform, unchanged: sRGB OETF
    /// → midtone-match power → always-on gentle base S-curve. No slider terms
    /// live here any more (they moved to EV space), so at neutral the whole
    /// pipeline reduces to exactly this curve and the render is byte-identical
    /// to the original. Input/output are [0,1] (caller clamps).
    /// </summary>
    public static double DisplayCurve(double lin)
    {
        if (lin < 0.0) lin = 0.0; else if (lin > 1.0) lin = 1.0;

        double srgb = lin <= 0.0031308
            ? 12.92 * lin
            : 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055;

        double p = Math.Pow(srgb, MidtoneLift);

        double tt = p * 2.0 - 1.0;
        double tanhNorm = Math.Tanh(TanhSlope);
        double sCurve = (Math.Tanh(tt * TanhSlope) / tanhNorm + 1.0) * 0.5;
        p = p * (1.0 - BaseContrast) + sCurve * BaseContrast;

        return p < 0.0 ? 0.0 : p > 1.0 ? 1.0 : p;
    }

    // ── Lightroom baseline-match calibration ───────────────────────────────
    //
    // <see cref="DisplayCurve"/> above is RAWR's original camera-matched look
    // and is deliberately left untouched (it is the reference the unit tests
    // pin). This curve is the *delivery* calibration composed on top of it so
    // the neutral render lands on Lightroom's default rendering.
    //
    // It is the empirical RAWR→LR tone transfer, derived by per-scene
    // histogram (CDF) matching of a neutral baseline export pair
    // (2_Baseline_RAWR.tif vs 2_Baseline_LR.tif) — see
    // Compare/compare_baseline_tif.py, which regenerates these exact numbers.
    // CDF matching is alignment-insensitive (it matches output distributions,
    // not paired pixels) so the small sensor-crop/framing offset between the
    // two files doesn't pollute it. It is a single LUMA curve applied equally
    // to R/G/B — neutral-preserving by construction. A per-channel colour
    // match was measured too but NOT baked: from one scene that is partly
    // content bias and would over-fit. Re-run the script over more baseline
    // pairs and average to refine; replace the table below verbatim.
    //
    // 65 control points, evenly spaced over the input (RAWR display) 0..1,
    // anchored at (0,0) and (1,1) so deep shadows / extreme highlights follow
    // a gentle separation-preserving line rather than a one-scene flat.
    private static readonly double[] LrMatchLut =
    {
        0.000000, 0.004541, 0.009081, 0.013622, 0.018163, 0.022703, 0.027244, 0.031784,
        0.042006, 0.057890, 0.070311, 0.083424, 0.098665, 0.118914, 0.141383, 0.164969,
        0.189962, 0.216275, 0.243335, 0.270840, 0.296823, 0.323598, 0.350717, 0.377512,
        0.405154, 0.433715, 0.458124, 0.485849, 0.511346, 0.535160, 0.559946, 0.583656,
        0.607689, 0.631176, 0.654909, 0.678365, 0.700916, 0.722202, 0.743124, 0.764952,
        0.784913, 0.804290, 0.830618, 0.851641, 0.866516, 0.881291, 0.892721, 0.903191,
        0.912870, 0.921874, 0.930127, 0.940565, 0.952813, 0.957635, 0.961707, 0.966748,
        0.971063, 0.974976, 0.978781, 0.982341, 0.984641, 0.986756, 0.990113, 0.995056,
        1.000000,
    };

    /// <summary>
    /// Maps a RAWR display value (0..1, the output of <see cref="DisplayCurve"/>)
    /// to the Lightroom-matched value via the calibrated transfer LUT
    /// (piecewise-linear, monotonic, fixed points at 0 and 1). Composing this
    /// after <see cref="DisplayCurve"/> is what makes the neutral render land
    /// on Lightroom's default look; it has no effect on the slider math, which
    /// runs upstream in scene-linear EV space.
    /// </summary>
    public static double LightroomMatch(double v)
    {
        if (v <= 0.0) return LrMatchLut[0];
        if (v >= 1.0) return LrMatchLut[^1];
        int last = LrMatchLut.Length - 1;          // 64 → 65 points
        double s = v * last;
        int i = (int)s;
        if (i >= last) return LrMatchLut[last];
        double f = s - i;
        return LrMatchLut[i] * (1.0 - f) + LrMatchLut[i + 1] * f;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //                         VERSIONED SLIDER MATH
    // ═══════════════════════════════════════════════════════════════════════
    //
    // Each non-trivial tone slider exposes a SwitchVersion(int, ...) dispatcher
    // and per-version implementations. Version 1 is RAWR's original math (see
    // the V1 helpers below — they apply the slider in isolation, equivalent to
    // ApplyHighlightShadowContrast / endpoint remap when the other relevant
    // sliders are zero). Version 2 is the darktable-style alternative model.
    //
    // To add a v3 of any slider:
    //   1. Bump <Slider>VersionCount by one.
    //   2. Add an ApplyXxxV3 method below.
    //   3. Add a `case 3:` branch in the matching ApplyXxx dispatcher.
    // The UI auto-discovers the new version count via the DevelopSettings
    // helpers further down — no XAML changes needed.

    public const int ContrastVersionCount    = 2;
    public const int HighlightsVersionCount  = 4;
    public const int ShadowsVersionCount     = 3;
    public const int WhitesVersionCount      = 3;
    public const int BlacksVersionCount      = 3;

    // ── V1 "alone" helpers ────────────────────────────────────────────────
    // These run the v1 math for a single slider, used when another slider is
    // on a different version so the combined ApplyHighlightShadowContrast /
    // combined endpoint remap fast path no longer applies.

    public static void ApplyHighlightsV1(ref double r, ref double g, ref double b, double highlights)
    {
        if (highlights == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double ev = Math.Log2(y / MiddleGray);
        double mask = SmoothStep(0.0, 3.0, ev);
        double newEv = ev + highlights / 100.0 * HighlightStrength * mask;
        double ratio = LumaFromEv(newEv) / y;
        r *= ratio; g *= ratio; b *= ratio;
    }

    public static void ApplyShadowsV1(ref double r, ref double g, ref double b, double shadows)
    {
        if (shadows == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double ev = Math.Log2(y / MiddleGray);
        double mask = 1.0 - SmoothStep(-4.0, 0.0, ev);
        double newEv = ev + shadows / 100.0 * ShadowStrength * mask;
        double ratio = LumaFromEv(newEv) / y;
        r *= ratio; g *= ratio; b *= ratio;
    }

    public static void ApplyContrastV1(ref double r, ref double g, ref double b, double contrastSlope)
    {
        if (contrastSlope == 1.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double ev = Math.Log2(y / MiddleGray);
        double ratio = LumaFromEv(ev * contrastSlope) / y;
        r *= ratio; g *= ratio; b *= ratio;
    }

    /// <summary>v1 Whites in isolation: endpoint remap with the black point fixed at 0 ⇒ lr / WhiteLin.</summary>
    public static void ApplyWhitesV1(ref double r, ref double g, ref double b, double whites)
    {
        if (whites == 0.0) return;
        double inv = 1.0 / WhiteLin(whites);
        r *= inv; g *= inv; b *= inv;
    }

    /// <summary>v1 Blacks in isolation: endpoint remap with the white point fixed at 1 ⇒ (lr − BlackLin) / (1 − BlackLin).</summary>
    public static void ApplyBlacksV1(ref double r, ref double g, ref double b, double blacks)
    {
        if (blacks == 0.0) return;
        double bl = BlackLin(blacks);
        double inv = 1.0 / (1.0 - bl);
        r = (r - bl) * inv;
        g = (g - bl) * inv;
        b = (b - bl) * inv;
    }

    // ── V2 (darktable-inspired) ───────────────────────────────────────────

    /// <summary>
    /// Highlights v2: tone-equalizer-style EV pull gated by a smoothstep on
    /// luminance (full effect from y≈0.85 up, zero by y≈0.35). Negative slider
    /// pulls highlights down, positive pushes them up. Strength ±2 EV at
    /// slider extremes — meaningfully stronger than the v1 ±1 EV.
    /// </summary>
    public const double HighlightsV2Stops = 2.0;
    public const double HighlightsV2MaskLo = 0.35;
    public const double HighlightsV2MaskHi = 0.85;
    public static void ApplyHighlightsV2(ref double r, ref double g, ref double b, double highlights)
    {
        if (highlights == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double mask = SmoothStep(HighlightsV2MaskLo, HighlightsV2MaskHi, y);
        if (mask <= 0.0) return;
        double gain = Math.Pow(2.0, (highlights / 100.0) * HighlightsV2Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    /// <summary>
    /// Contrast v2: darktable's hue-preserving luminance power curve about
    /// middle grey. Y_out = pivot · (Y_in / pivot)^(1 + slider·k). Pivot is
    /// MiddleGray so middle grey is exactly a fixed point (matches v1).
    /// </summary>
    public const double ContrastV2Pivot = MiddleGray;
    public const double ContrastV2Range = 0.6;
    public static void ApplyContrastV2(ref double r, ref double g, ref double b, double contrast)
    {
        if (contrast == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double exponent = 1.0 + (contrast / 100.0) * ContrastV2Range;
        double y2 = ContrastV2Pivot * Math.Pow(y / ContrastV2Pivot, exponent);
        double ratio = y2 / y;
        r *= ratio; g *= ratio; b *= ratio;
    }

    /// <summary>
    /// Shadows v2: tone-equalizer-style EV lift gated by a smoothstep on
    /// luminance (full effect at y=0, zero by y=0.45). Positive opens shadows,
    /// negative crushes them. Strength ±2 EV at slider extremes.
    /// </summary>
    public const double ShadowsV2Stops = 2.0;
    public const double ShadowsV2MaskLo = 0.0;
    public const double ShadowsV2MaskHi = 0.45;
    public static void ApplyShadowsV2(ref double r, ref double g, ref double b, double shadows)
    {
        if (shadows == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double mask = 1.0 - SmoothStep(ShadowsV2MaskLo, ShadowsV2MaskHi, y);
        if (mask <= 0.0) return;
        double gain = Math.Pow(2.0, (shadows / 100.0) * ShadowsV2Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    /// <summary>
    /// Whites v2: smooth high-tone EV mask (no hard endpoint clip). Positive
    /// pushes the brightest band toward clipping; negative protects highlights.
    /// Mask ramps in over y=0.75..1.0, strength ±1.5 EV.
    /// </summary>
    public const double WhitesV2Stops = 1.5;
    public const double WhitesV2MaskLo = 0.75;
    public const double WhitesV2MaskHi = 1.0;
    public static void ApplyWhitesV2(ref double r, ref double g, ref double b, double whites)
    {
        if (whites == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double mask = SmoothStep(WhitesV2MaskLo, WhitesV2MaskHi, y);
        if (mask <= 0.0) return;
        double gain = Math.Pow(2.0, (whites / 100.0) * WhitesV2Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    /// <summary>
    /// Blacks v2: smooth low-tone EV mask (no hard black-point shift).
    /// Positive lifts the deepest band; negative crushes it. Mask falls from
    /// y=0 (full) to y=0.35 (none), strength ±1.5 EV.
    /// </summary>
    public const double BlacksV2Stops = 1.5;
    public const double BlacksV2MaskLo = 0.0;
    public const double BlacksV2MaskHi = 0.35;
    public static void ApplyBlacksV2(ref double r, ref double g, ref double b, double blacks)
    {
        if (blacks == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double mask = 1.0 - SmoothStep(BlacksV2MaskLo, BlacksV2MaskHi, y);
        if (mask <= 0.0) return;
        double gain = Math.Pow(2.0, (blacks / 100.0) * BlacksV2Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    // ── V3 (edge-aware) ───────────────────────────────────────────────────
    //
    // Read evBase = log2(Y_region / MiddleGray) from <see cref="EdgeAwareLuma"/>,
    // so the mask reflects the *region* a pixel sits in, not the pixel itself.
    // A dark pixel inside a bright region gets the highlight treatment; a bright
    // detail pixel inside a shadow region gets the shadow treatment. That's the
    // bit that makes these sliders feel like Lightroom's: stronger gain (±3.5 EV
    // for H/S, ±2.5 for W/B) is safe to apply because the regional mask keeps
    // it from flattening the image or producing halos.

    public const double HighlightsV3Stops    =  3.5;
    public const double HighlightsV3MaskLoEv = -0.5;
    public const double HighlightsV3MaskHiEv =  3.5;
    public static void ApplyHighlightsV3(ref double r, ref double g, ref double b,
                                         double highlights, double evBase)
    {
        if (highlights == 0.0) return;
        double mask = SmoothStep(HighlightsV3MaskLoEv, HighlightsV3MaskHiEv, evBase);
        if (mask <= 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double gain = Math.Pow(2.0, (highlights / 100.0) * HighlightsV3Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    public const double ShadowsV3Stops    =  3.5;
    public const double ShadowsV3MaskLoEv = -4.5;
    public const double ShadowsV3MaskHiEv =  0.5;
    public static void ApplyShadowsV3(ref double r, ref double g, ref double b,
                                      double shadows, double evBase)
    {
        if (shadows == 0.0) return;
        double mask = 1.0 - SmoothStep(ShadowsV3MaskLoEv, ShadowsV3MaskHiEv, evBase);
        if (mask <= 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double gain = Math.Pow(2.0, (shadows / 100.0) * ShadowsV3Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    public const double WhitesV3Stops    =  2.5;
    public const double WhitesV3MaskLoEv =  0.5;
    public const double WhitesV3MaskHiEv =  3.0;
    public static void ApplyWhitesV3(ref double r, ref double g, ref double b,
                                     double whites, double evBase)
    {
        if (whites == 0.0) return;
        double mask = SmoothStep(WhitesV3MaskLoEv, WhitesV3MaskHiEv, evBase);
        if (mask <= 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double gain = Math.Pow(2.0, (whites / 100.0) * WhitesV3Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    public const double BlacksV3Stops    =  2.5;
    public const double BlacksV3MaskLoEv = -6.0;
    public const double BlacksV3MaskHiEv = -1.0;
    public static void ApplyBlacksV3(ref double r, ref double g, ref double b,
                                     double blacks, double evBase)
    {
        if (blacks == 0.0) return;
        double mask = 1.0 - SmoothStep(BlacksV3MaskLoEv, BlacksV3MaskHiEv, evBase);
        if (mask <= 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;
        double gain = Math.Pow(2.0, (blacks / 100.0) * BlacksV3Stops * mask);
        r *= gain; g *= gain; b *= gain;
    }

    // ── V4 (Lightroom-calibrated highlights) ──────────────────────────────
    //
    // Calibrated against a measured LR-vs-RAWR battery. The decisive run was a
    // delta-vs-delta comparison on a well-exposed frame (set 2): LR's own
    // baseline→edit profile vs RAWR's, band by band in scene-linear EV. That
    // exposed the shape — not just the strength — of LR's Highlights:
    //
    //   1. Broad, per-pixel reach. On a normal image −100 bends the WHOLE
    //      tonal range (near-black −0.10 → lower-mid −0.24), not just the
    //      bright band. The v3-style *regional* evBase mask was wrong here and
    //      caused the largest error, so v4 keys off the pixel's own luminance.
    //   2. A gentle plateau plus a lights peak, then HARD top protection. LR
    //      holds a flat ~−0.22 (at −100) pull across shadows→mids and adds a
    //      hump that peaks in the lights (−0.43), then collapses to ~0 by
    //      near-white (−0.014). Modelled as (broad·rise + peak·gauss)·protect:
    //      a flat broad term, a Gaussian centred on the lights, and a sharp
    //      rolloff in the last ~0.2 EV before clipping. A single envelope
    //      over-pulled the mids by ~0.2 EV — the split tracks the measured
    //      plateau-then-hump shape.
    //   3. Linear in slider. −50 ≈ ½·−100, +50 ≈ ½·+100 — a plain (slider/100)
    //      scale, no curve-per-amount.
    //   4. Asymmetric. Negative is ~1.7× the positive push on this clean
    //      image; the ratio widens to 10×+ on dark/clipped scenes because the
    //      blown-recovery term (below) only adds to the negative side.
    //   5. Clipping-aware recovery. Genuinely blown pixels (very high ev) get
    //      a large extra pull — the −3…−4 EV the clipped scene needed — added
    //      *past* the protect rolloff and gated to clipped ev only, so it
    //      cannot lift the protected near-white of a non-clipped image.
    //      A second contextual term reads evBase: if a pixel sits inside a
    //      clipped highlight region but its own luma is not clipped, it still
    //      gets part of LR's recovery. This is what the dark/clipped calibration
    //      frames need; their recovered highlight regions land in displayed
    //      midtones, so a purely per-pixel display-luma mask misses them.
    //   6. Mild chroma coupling. Recovery slightly raises saturation, push
    //      slightly lowers it; tied to the (now bounded) EV move so it can no
    //      longer blow up at the top the way the first v4 did.
    //
    // Strength constants are pinned to set 2's high-confidence lights peak;
    // the blown/contextual recovery constants are carried from the
    // dark/clipped battery and should be refined as more re-exports are added.

    public const double HighlightsV4RiseLoEv     = -7.0;  // broad term ramps in from deep shadow…
    public const double HighlightsV4RiseHiEv     = -1.0;  // …flat plateau by the shadows and up
    public const double HighlightsV4ProtectLoEv  =  2.30; // top protection starts (~y 0.88)
    public const double HighlightsV4ProtectHiEv  =  2.48; // …complete just below clipping
    public const double HighlightsV4RecoverBroad =  0.22; // −100 flat plateau pull (shadows→mids)
    public const double HighlightsV4RecoverPeak  =  0.50; // −100 extra Gaussian hump at the lights
    public const double HighlightsV4PeakEv       =  1.95; // hump centre (the lights band)
    public const double HighlightsV4PeakWidthEv  =  0.55; // hump half-width (EV)
    public const double HighlightsV4PushStops    =  0.42; // +100 EV lift, still weaker than recovery
    public const double HighlightsV4BlowLoEv     =  2.4;  // blown-recovery boost engages here…
    public const double HighlightsV4BlowHiEv     =  4.5;  // …saturating for deeply clipped detail
    public const double HighlightsV4BlowStops    =  3.5;  // extra EV pull for genuinely clipped pixels
    public const double HighlightsV4ContextLoEv  =  2.1;  // regional clipped-highlight recovery starts…
    public const double HighlightsV4ContextHiEv  =  3.8;  // …and saturates in very bright regions
    public const double HighlightsV4ContextStops =  1.8;  // extra EV pull for non-clipped pixels in blown regions
    public const double HighlightsV4DarkRecoverBase = 0.45; // dark/clipped scenes: whole-frame base pull
    public const double HighlightsV4DarkRecoverLow  = 0.35; // extra pull into shadows/lower mids
    public const double HighlightsV4DarkRecoverHigh = 0.95; // strong pull through mids/highlights
    public const double HighlightsV4DarkRecoverTop  = 0.75; // final top-end recovery lift
    public const double HighlightsV4DarkPushBase    = 0.25; // dark/clipped scenes: whole-frame base lift
    public const double HighlightsV4DarkPushMid     = 0.55; // extra lift through shadows/lower mids
    public const double HighlightsV4DarkPushRollLo  = 0.65; // positive side rolls off before the top
    public const double HighlightsV4DarkPushRollHi  = 1.00;
    public const double HighlightsV4ChromaRecover=  0.12; // saturation gain per EV of recovery pull
    public const double HighlightsV4ChromaPush   =  0.10; // saturation loss per EV of positive push
    public const double HighlightsV4MaxChroma    =  1.80; // clamp on the chroma factor

    public static void ApplyHighlightsV4(ref double r, ref double g, ref double b,
                                         double highlights, double evBase,
                                         double darkSceneBoost = 0.0)
    {
        if (highlights == 0.0) return;

        double y = Luminance(r, g, b);
        if (y < LumaEps) return;

        double ev = Math.Log2(y / MiddleGray);

        // Broad envelope: ramps in from deep shadow to a flat plateau by the
        // shadows. Zero in the deep shadow ⇒ nothing to do.
        double rise = SmoothStep(HighlightsV4RiseLoEv, HighlightsV4RiseHiEv, ev);
        if (rise <= 0.0) return;
        double protect = 1.0 - SmoothStep(HighlightsV4ProtectLoEv, HighlightsV4ProtectHiEv, ev);

        double s = highlights / 100.0;             // −1 … +1, linear
        double deltaEv;
        double darkScene = darkSceneBoost < 0.0 ? 0.0 : darkSceneBoost > 1.0 ? 1.0 : darkSceneBoost;

        if (s < 0.0)
        {
            // Recovery = (flat plateau + Gaussian hump at the lights), top-
            // protected, PLUS an un-protected boost that only engages for
            // genuinely clipped pixels — so blown detail is pulled hard while
            // non-clipped near-white stays protected.
            double pk = (ev - HighlightsV4PeakEv) / HighlightsV4PeakWidthEv;
            double peak = Math.Exp(-pk * pk);
            double protectedStops =
                HighlightsV4RecoverBroad * rise + HighlightsV4RecoverPeak * peak;
            double blow = SmoothStep(HighlightsV4BlowLoEv, HighlightsV4BlowHiEv, ev);

            // Contextual recovery: only the portion not already explained by
            // the pixel's own clipped luma is added. It remains top-protected
            // for non-clipped pixels, while truly clipped pixels still use the
            // stronger unprotected local `blow` term above.
            double context = SmoothStep(HighlightsV4ContextLoEv, HighlightsV4ContextHiEv, evBase);
            double contextOnly = context > blow ? context - blow : 0.0;

            deltaEv = s * (
                protectedStops * protect
                + HighlightsV4BlowStops * blow
                + HighlightsV4ContextStops * contextOnly * protect);

            if (darkScene > 0.0)
            {
                // Image #3-style case: Lightroom's Highlights is not merely a
                // local top-end recovery. In dark frames with a strong clipped
                // highlight component it pulls the whole rendered tone scale,
                // increasingly hard toward the upper half. Use a display-
                // referred proxy for the mask because the exported calibration
                // curves line up with rendered luma bins, not raw scene EV.
                double yd = Math.Pow(LightroomMatch(DisplayCurve(y)), 2.19921875);
                double darkStops =
                    HighlightsV4DarkRecoverBase
                    + HighlightsV4DarkRecoverLow  * SmoothStep(0.01, 0.12, yd)
                    + HighlightsV4DarkRecoverHigh * SmoothStep(0.14, 0.55, yd)
                    + HighlightsV4DarkRecoverTop  * SmoothStep(0.82, 0.98, yd);
                deltaEv += s * darkScene * darkStops;
            }
        }
        else
        {
            // Push: the broad envelope only, weaker, fully top-protected
            // (positive Highlights must not drive near-white into clipping).
            deltaEv = s * HighlightsV4PushStops * rise * protect;

            if (darkScene > 0.0)
            {
                double yd = Math.Pow(LightroomMatch(DisplayCurve(y)), 2.19921875);
                double roll = 1.0 - SmoothStep(HighlightsV4DarkPushRollLo, HighlightsV4DarkPushRollHi, yd);
                double darkStops =
                    (HighlightsV4DarkPushBase
                     + HighlightsV4DarkPushMid * SmoothStep(0.015, 0.18, yd))
                    * roll;
                deltaEv += s * darkScene * darkStops;
            }
        }

        if (deltaEv == 0.0) return;

        double newY = LumaFromEv(ev + deltaEv);
        double ratio = newY / y;
        r *= ratio; g *= ratio; b *= ratio;

        // Chroma coupling, bounded by the EV move (recovery saturates, push
        // desaturates) — a plain luminance ratio holds std/mean fixed, but LR
        // measurably shifts saturation with the highlight move.
        double sat = deltaEv < 0.0
            ? 1.0 + HighlightsV4ChromaRecover * (-deltaEv)
            : 1.0 - HighlightsV4ChromaPush * deltaEv;
        if (sat > HighlightsV4MaxChroma) sat = HighlightsV4MaxChroma;
        if (sat < 0.0) sat = 0.0;
        if (sat != 1.0)
        {
            r = newY + (r - newY) * sat;
            g = newY + (g - newY) * sat;
            b = newY + (b - newY) * sat;
        }
    }

    /// <summary>True iff any H/S/W/B version can use the
    /// <see cref="EdgeAwareLuma"/> regional pre-pass. Callers should still gate
    /// this by nonzero slider values so neutral renders stay cheap.</summary>
    public static bool NeedsEvBase(int highlightsVersion, int shadowsVersion, int whitesVersion, int blacksVersion)
        => highlightsVersion == 3 || highlightsVersion == 4 || shadowsVersion == 3
        || whitesVersion == 3 || blacksVersion == 3;

    // ── Version dispatchers ──────────────────────────────────────────────
    //
    // evBase (regional log-luminance, in EV from middle grey) is consulted by
    // case 3 and by v4 Highlights' contextual recovery. v1/v2 ignore it.

    public static void ApplyHighlights(int version, ref double r, ref double g, ref double b,
                                       double highlights, double evBase,
                                       double darkSceneBoost = 0.0)
    {
        switch (version)
        {
            case 4: ApplyHighlightsV4(ref r, ref g, ref b, highlights, evBase, darkSceneBoost); break;
            case 3: ApplyHighlightsV3(ref r, ref g, ref b, highlights, evBase); break;
            case 2: ApplyHighlightsV2(ref r, ref g, ref b, highlights); break;
            default: ApplyHighlightsV1(ref r, ref g, ref b, highlights); break;
        }
    }

    public static void ApplyContrast(int version, ref double r, ref double g, ref double b,
                                     double contrast, double contrastSlope)
    {
        switch (version)
        {
            case 2: ApplyContrastV2(ref r, ref g, ref b, contrast); break;
            default: ApplyContrastV1(ref r, ref g, ref b, contrastSlope); break;
        }
    }

    public static void ApplyShadows(int version, ref double r, ref double g, ref double b,
                                    double shadows, double evBase)
    {
        switch (version)
        {
            case 3: ApplyShadowsV3(ref r, ref g, ref b, shadows, evBase); break;
            case 2: ApplyShadowsV2(ref r, ref g, ref b, shadows); break;
            default: ApplyShadowsV1(ref r, ref g, ref b, shadows); break;
        }
    }

    public static void ApplyWhites(int version, ref double r, ref double g, ref double b,
                                   double whites, double evBase)
    {
        switch (version)
        {
            case 3: ApplyWhitesV3(ref r, ref g, ref b, whites, evBase); break;
            case 2: ApplyWhitesV2(ref r, ref g, ref b, whites); break;
            default: ApplyWhitesV1(ref r, ref g, ref b, whites); break;
        }
    }

    public static void ApplyBlacks(int version, ref double r, ref double g, ref double b,
                                   double blacks, double evBase)
    {
        switch (version)
        {
            case 3: ApplyBlacksV3(ref r, ref g, ref b, blacks, evBase); break;
            case 2: ApplyBlacksV2(ref r, ref g, ref b, blacks); break;
            default: ApplyBlacksV1(ref r, ref g, ref b, blacks); break;
        }
    }
}
