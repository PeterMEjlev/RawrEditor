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
/// <para><b>Calibrated for neutral parity.</b> The constants are chosen so
/// that with every slider at 0 the pipeline is a no-op in linear space and
/// the render is byte-identical to RAWR's original camera-matched look
/// (sRGB encode → midtone-match power → gentle base S-curve, all preserved
/// in <see cref="DisplayCurve"/>). Notably <see cref="WhiteLin"/> is 1.0 and
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
}
