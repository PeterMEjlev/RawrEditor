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

    // ── V2 (darktable-inspired Contrast) ────────────────────────────────────

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

    // ── V3 (edge-aware) ───────────────────────────────────────────────────
    //
    // Read evBase = log2(Y_region / MiddleGray) from <see cref="EdgeAwareLuma"/>,
    // so the mask reflects the *region* a pixel sits in, not the pixel itself.
    // A dark pixel inside a bright region gets the highlight treatment; a bright
    // detail pixel inside a shadow region gets the shadow treatment. That's the
    // bit that makes these sliders feel like Lightroom's: stronger gain (±3.5 EV
    // for H/S, ±2.5 for W/B) is safe to apply because the regional mask keeps
    // it from flattening the image or producing halos.

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

    // ── Soft knee: the shared shape for every top-end operator ─────────────

    /// <summary>
    /// Soft knee in EV space: identity for <paramref name="d"/> ≤ 0, tending to
    /// <paramref name="slope"/>·d for large d, with matching value <i>and</i> slope
    /// at d = 0 so there is no crease where the affected band begins.
    ///
    /// <para>d′ = s·d + (1−s)·w·(1 − e^(−d/w)). At d = 0 the derivative is
    /// s + (1−s) = 1 for any s, which is what buys the C¹ join; as d grows the
    /// exponential saturates and the derivative falls to s. Works unchanged for
    /// s &gt; 1 (expansion), where the correction term is negative.</para>
    ///
    /// <para>Highlights, Whites and the Exposure shoulder are all this one curve
    /// with different anchors and slopes — which is what makes them compose
    /// predictably instead of fighting each other.</para>
    /// </summary>
    public static double SoftKnee(double d, double slope, double width)
    {
        if (d <= 0.0) return d;
        if (width < 1e-6) width = 1e-6;
        return slope * d + (1.0 - slope) * width * (1.0 - Math.Exp(-d / width));
    }

    // ── V4: global top-end operators (Whites, Exposure shoulder) ───────────
    //
    // Both are deliberately GLOBAL — per-pixel luminance, no evBase. That is the
    // difference in feel between Whites and Highlights: Highlights is regional and
    // adaptive (a bright pixel in a dark region is left alone), whereas Whites is
    // an endpoint control that moves every bright pixel in the frame regardless of
    // what surrounds it. Reading evBase here, as v3 did, turned Whites into a
    // second Highlights and left isolated speculars untouched.
    //
    // Both are calibrated against the range this pipeline's output transform
    // actually has. DisplayCurve∘LightroomMatch renders middle grey at ~190/255 and
    // maps +2 EV to ~250/255, so the whole visible highlight range is about two
    // stops wide; constants tuned for a conventional curve (white at linear 1.0)
    // would move the render by a couple of code values and feel inert.

    // How far Whites can travel is set by the asymptote of the soft knee, which
    // sits at knee + (1−slope)·width. Anything the slider does has to fit between
    // that floor and where the tone started, and this output transform gives it
    // very little to work with: it maps linear 0.20…1.00 — two and a third stops —
    // into just 198…255. A knee at +0.5 EV with width 0.8 puts the floor at ~240,
    // so Whites −100 could only ever move the brightest tone by fifteen code
    // values out of 255, which reads as the slider doing nothing at all.
    //
    // These constants place the floor near 217/255 instead, giving the slider a
    // ~33-value reach at the top — comparable to Highlights, which is also their
    // relationship in Lightroom — while leaving everything below ~198/255 exactly
    // alone. The knee cannot go much higher without the floor swallowing the
    // slider's whole range again.

    /// <summary>Where Whites starts to act, in stops above middle grey. ≈195/255.</summary>
    public const double WhitesKneeEv   = 0.1;
    public const double WhitesWidthEv  = 0.42;
    /// <summary>Slope at Whites −100: pulls the extreme top down toward the knee.</summary>
    public const double WhitesMinSlope = 0.05;
    /// <summary>Slope at Whites +100: expands the top end and drives it into the clip.</summary>
    public const double WhitesMaxSlope = 2.2;

    /// <summary>
    /// Whites as a white-point control: a slope change on the top end, pivoting at
    /// <see cref="WhitesKneeEv"/> so midtones stay exactly put. Negative pulls the
    /// brightest tones back down the output range (≈255→222, tapering to nothing by
    /// ≈198); positive expands them and drives them into the clip.
    ///
    /// <para>Comparable in strength to Highlights but different in character, which
    /// is what keeps the two sliders worth having separately: Whites is global and
    /// flattens what it compresses, while Highlights is regional and splits detail
    /// off so it survives. Whites moves an isolated specular in a dark frame;
    /// Highlights, reading the region, leaves it alone.</para>
    ///
    /// <para>The slider interpolates the slope <i>geometrically</i>. Slope is a
    /// multiplicative quantity, so a linear ramp bunches almost all of the effect
    /// into the last third of the travel — at −50 a linear ramp gives a third of
    /// the movement a geometric one does.</para>
    /// </summary>
    public static void ApplyWhitesV4(ref double r, ref double g, ref double b, double whites)
    {
        if (whites == 0.0) return;
        double y = Luminance(r, g, b);
        if (y < LumaEps) return;

        double ev = Math.Log2(y / MiddleGray);
        double d = ev - WhitesKneeEv;
        if (d <= 0.0) return;                 // below the knee ⇒ bit-exact no-op

        double k = Math.Clamp(Math.Abs(whites) / 100.0, 0.0, 1.0);
        double slope = Math.Pow(whites < 0.0 ? WhitesMinSlope : WhitesMaxSlope, k);

        double y2 = MiddleGray * double.Exp2(WhitesKneeEv + SoftKnee(d, slope, WhitesWidthEv));
        double ratio = y2 / y;
        r *= ratio; g *= ratio; b *= ratio;
    }

    // ── A note on Exposure ────────────────────────────────────────────────
    //
    // Exposure stays exactly what it was: a true 2^EV multiply via ExposureGain,
    // with no shoulder or rolloff of its own. A highlight rolloff whose strength
    // scaled with the exposure being applied was tried and removed — it made the
    // slider non-monotonic. Because the compression grew faster than the extra
    // stop, +2 EV clipped *less* of the frame than +1 EV (2.1% vs 15.7% on the
    // reference scene) and very bright pixels came out darker as the slider went
    // up. Any schedule strong enough to preserve a gradient across the clip was
    // strong enough to invert the slider somewhere.
    //
    // The parity gap with Lightroom for Exposure was never the gain — it was that
    // pulling exposure down could not recover blown highlights, because the decoder
    // had already flattened them. HighlightReconstruction fixes that, and
    // DevelopProcessor runs it whenever Exposure is negative. What is left is a
    // physical stop, which is both what this codebase intended and what Lightroom's
    // Exposure behaves like; the top-end rolloff belongs to the output transform.

    // ── Dispatchers ──────────────────────────────────────────────────────
    //
    // Highlights is no longer here: it is a spatial, edge-aware local-tone
    // operator (see LocalHighlights) applied to whole RGB planes by
    // DevelopProcessor, not a per-pixel EV curve.

    public static void ApplyContrast(ref double r, ref double g, ref double b,
                                     double contrast)
        => ApplyContrastV2(ref r, ref g, ref b, contrast);

    public static void ApplyShadows(ref double r, ref double g, ref double b,
                                    double shadows, double evBase)
        => ApplyShadowsV3(ref r, ref g, ref b, shadows, evBase);

    /// <summary>Whites takes no evBase: v4 is global (see the section note above).</summary>
    public static void ApplyWhites(ref double r, ref double g, ref double b,
                                   double whites)
        => ApplyWhitesV4(ref r, ref g, ref b, whites);

    public static void ApplyBlacks(ref double r, ref double g, ref double b,
                                   double blacks, double evBase)
        => ApplyBlacksV3(ref r, ref g, ref b, blacks, evBase);
}
