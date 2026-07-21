namespace Rawr.Develop;

/// <summary>
/// Lightroom-<i>like</i> white balance: Temperature and Tint resolved into
/// per-channel linear gains through real illuminant chromaticities rather than
/// the ad-hoc channel ramps this pipeline used to carry.
///
/// <para><b>What was wrong before.</b> The previous model was
/// <c>gainR = 1 + 0.45·t</c>, <c>gainB = 1 − 0.45·t</c>, <c>gainG = 1 − 0.30·tint</c>.
/// Three things make that not feel like Lightroom: the slider was linear in its
/// own units rather than in <i>mireds</i>, so the warm half of the travel did far
/// more than the cool half; the R/B ramp is not the chromaticity path of any real
/// illuminant, so the colour went pink/cyan rather than warm/cool; and moving Tint
/// changed the green gain without compensating, so the whole image got brighter or
/// darker as you dragged a slider that is supposed to be purely chromatic.</para>
///
/// <para><b>The model here.</b> Temperature names an <i>illuminant</i>: the slider
/// says "assume the scene was lit at T kelvin", and the render compensates, which
/// is why a higher number gives a warmer picture. T maps to a chromaticity on the
/// Planckian/daylight locus, Tint displaces it perpendicular to that locus (the
/// Duv axis — genuinely green↔magenta, the direction the locus does not already
/// cover), and the resulting white point becomes von Kries gains in the linear
/// sRGB working space. Gains are normalised to preserve Rec.709 luma, so both
/// sliders are purely chromatic.</para>
///
/// <para><b>Why gains and not a full CAT matrix.</b> A Bradford chromatic
/// adaptation transform is the more accurate operator, but it is a full 3×3 and
/// this pipeline needs white balance to stay <i>diagonal</i>:
/// <see cref="HighlightReconstruction"/> is handed each channel's clipping point
/// as that channel's gain, which only has meaning while the channels stay
/// independent. A full matrix would smear the three clip points together and the
/// reconstruction would no longer know where the sensor actually saturated. Von
/// Kries in the working space is what raw converters do for exactly this reason.</para>
///
/// <para><b>The as-shot anchor.</b> <see cref="Rawr.Raw.RawDecoder"/> bakes the
/// camera's own white balance in via <c>user_mul</c>, so the buffer that arrives
/// here is already neutral and carries sRGB primaries — its white point is D65.
/// That makes <see cref="AsShotKelvin"/> the correct anchor: at 6504 K the gains
/// are exactly (1, 1, 1) and the render is bit-identical to the untouched decode.
/// The Kelvin readout is therefore the <i>rendering</i> illuminant in the decoded
/// space, not the camera's recorded as-shot value — matching that number to
/// Lightroom's would need the camera's colour matrices, which LibRaw has already
/// consumed by this point.</para>
/// </summary>
public static class WhiteBalance
{
    /// <summary>The illuminant the decoded buffer is already neutral for (D65).
    /// Gains are exactly unity here, so this is the no-op point of both sliders.</summary>
    public const double AsShotKelvin = 6504.0;

    /// <summary>Slider bounds in Kelvin mode. Matches Lightroom's raw range.</summary>
    public const double MinKelvin = 2000.0;
    public const double MaxKelvin = 50000.0;

    /// <summary>Mireds of illuminant travel per unit of the relative Temperature
    /// slider. 1.0 puts ±100 at roughly 3940 K … 18590 K about the D65 anchor —
    /// a wide but still controllable range, and even-handed in both directions
    /// because mireds (not Kelvin) are what perceptual evenness lives in.</summary>
    public const double MiredsPerTempUnit = 1.0;

    /// <summary>Duv displacement off the locus at Tint ±100. Real illuminants sit
    /// within about ±0.02, so this gives the slider meaningful reach past the
    /// plausible range without the ends turning into pure green/magenta.</summary>
    public const double MaxTintDuv = 0.030;

    // Guard rail on the final gains. The 2000…50000 K range with ±0.03 Duv stays
    // far inside this; it exists so a malformed setting can't produce a gain that
    // overflows the 16-bit requantise in DevelopProcessor.
    private const double MinGain = 0.02;
    private const double MaxGain = 50.0;

    public static double ToMired(double kelvin) => 1e6 / kelvin;
    public static double FromMired(double mired) => 1e6 / mired;

    /// <summary>Relative Temperature slider (−100…+100) → illuminant Kelvin.
    /// Positive is warmer, i.e. <i>fewer</i> mireds and a higher Kelvin.</summary>
    public static double TemperatureToKelvin(double temperature)
    {
        double mired = ToMired(AsShotKelvin) - temperature * MiredsPerTempUnit;
        if (mired < ToMired(MaxKelvin)) mired = ToMired(MaxKelvin);
        return Math.Clamp(FromMired(mired), MinKelvin, MaxKelvin);
    }

    /// <summary>Inverse of <see cref="TemperatureToKelvin"/>. The result can fall
    /// outside ±100 — the Kelvin slider reaches illuminants the relative slider
    /// cannot — so callers switching modes must clamp.</summary>
    public static double KelvinToTemperature(double kelvin)
    {
        double k = Math.Clamp(kelvin, MinKelvin, MaxKelvin);
        return (ToMired(AsShotKelvin) - ToMired(k)) / MiredsPerTempUnit;
    }

    /// <summary>
    /// Kelvin → CIE 1931 xy on the blackbody / daylight locus, via Kim et al.'s
    /// cubic fit (valid 1667 K…25000 K). Outside that span the cubics diverge, so
    /// we extrapolate linearly <i>in mireds</i> along the locus instead: over the
    /// distance involved (25000 K → 50000 K is just 20 mireds) the locus is very
    /// nearly straight, and mired is the parameter it is straight in.
    /// </summary>
    public static (double x, double y) LocusXy(double kelvin)
    {
        const double loK = 1667.0, hiK = 25000.0;
        if (kelvin >= loK && kelvin <= hiK) return LocusXyCore(kelvin);

        double edge = kelvin > hiK ? hiK : loK;
        double inner = kelvin > hiK ? 24000.0 : 1800.0;
        var (xe, ye) = LocusXyCore(edge);
        var (xi, yi) = LocusXyCore(inner);
        double me = ToMired(edge), mi = ToMired(inner);
        double t = (ToMired(kelvin) - me) / (mi - me);
        return (xe + t * (xi - xe), ye + t * (yi - ye));
    }

    private static (double x, double y) LocusXyCore(double t)
    {
        double t2 = t * t, t3 = t2 * t;
        double x = t <= 4000.0
            ? -0.2661239e9 / t3 - 0.2343589e6 / t2 + 0.8776956e3 / t + 0.179910
            : -3.0258469e9 / t3 + 2.1070379e6 / t2 + 0.2226347e3 / t + 0.240390;

        double x2 = x * x, x3 = x2 * x;
        double y = t <= 2222.0
            ? -1.1063814 * x3 - 1.34811020 * x2 + 2.18555832 * x - 0.20219683
            : t <= 4000.0
                ? -0.9549476 * x3 - 1.37418593 * x2 + 2.09137015 * x - 0.16748867
                : 3.0817580 * x3 - 5.87338670 * x2 + 3.75112997 * x - 0.37001483;

        return (x, y);
    }

    // CIE 1960 UCS — the space Duv is defined in, and the one where "perpendicular
    // to the locus" is the green↔magenta axis people actually perceive.
    private static (double u, double v) XyToUv(double x, double y)
    {
        double d = -2.0 * x + 12.0 * y + 3.0;
        if (Math.Abs(d) < 1e-12) d = 1e-12;
        return (4.0 * x / d, 6.0 * y / d);
    }

    private static (double x, double y) UvToXy(double u, double v)
    {
        double d = 2.0 * u - 8.0 * v + 4.0;
        if (Math.Abs(d) < 1e-12) d = 1e-12;
        return (3.0 * u / d, 2.0 * v / d);
    }

    /// <summary>
    /// The illuminant chromaticity for a Temperature/Tint pair: the locus point
    /// for <paramref name="kelvin"/>, displaced by Tint along the locus normal.
    ///
    /// <para><b>Mind the inversion.</b> Positive Tint must <i>render</i> magenta
    /// (Lightroom's convention), and because the slider names the illuminant, the
    /// way to render magenta is to declare a <i>green</i> illuminant and let the
    /// von Kries division take it out. So +Tint moves to +Duv, the green side of
    /// the locus. Same trap as Temperature, where a higher Kelvin — a bluer
    /// stated illuminant — is what produces a warmer picture.</para>
    /// </summary>
    public static (double x, double y) IlluminantXy(double kelvin, double tint)
    {
        var (x0, y0) = LocusXy(kelvin);
        if (tint == 0.0) return (x0, y0);

        var (u0, v0) = XyToUv(x0, y0);

        // Locus tangent, differenced in mireds (its natural parameter) so the
        // step is even at both ends of the range.
        double m = ToMired(kelvin);
        var (xa, ya) = LocusXy(FromMired(m - 1.0));
        var (xb, yb) = LocusXy(FromMired(m + 1.0));
        var (ua, va) = XyToUv(xa, ya);
        var (ub, vb) = XyToUv(xb, yb);
        double du = ub - ua, dv = vb - va;
        double len = Math.Sqrt(du * du + dv * dv);
        if (len < 1e-12) return (x0, y0);
        du /= len; dv /= len;

        // Rotate the (warm-pointing) tangent a quarter turn to get the normal.
        // This orientation puts +Duv on the green side of the locus — which is
        // where positive Tint belongs, per the inversion noted above.
        double nu = -dv, nv = du;
        double duv = (Math.Clamp(tint, -100.0, 100.0) / 100.0) * MaxTintDuv;
        return UvToXy(u0 + nu * duv, v0 + nv * duv);
    }

    /// <summary>
    /// Per-channel linear gains for the given illuminant, in the linear sRGB
    /// working space, normalised so Rec.709 luma of a neutral is unchanged —
    /// Temperature and Tint move colour only, never brightness.
    ///
    /// <para>Returns exactly (1, 1, 1) at <see cref="AsShotKelvin"/> with Tint 0.
    /// That exactness is <i>constructed</i>, not hoped for: the locus fit at
    /// 6504 K lands a few thousandths off D65's true chromaticity, which would
    /// leave a small permanent cast on the neutral render and break the baseline
    /// parity the tone LUT is calibrated against. Dividing through by the anchor's
    /// own gains removes that, and is the more honest operator anyway — the
    /// decoded buffer is already neutral for the anchor, so what the slider owes
    /// is the <i>incremental</i> adaptation from it.</para>
    /// </summary>
    public static (double r, double g, double b) Gains(double kelvin, double tint)
    {
        if (kelvin == AsShotKelvin && tint == 0.0) return (1.0, 1.0, 1.0);

        var (gr, gg, gb) = RawGains(Math.Clamp(kelvin, MinKelvin, MaxKelvin), tint);
        gr /= AnchorGains.r; gg /= AnchorGains.g; gb /= AnchorGains.b;

        double luma = 0.2126 * gr + 0.7152 * gg + 0.0722 * gb;
        if (luma < 1e-9) return (1.0, 1.0, 1.0);
        gr /= luma; gg /= luma; gb /= luma;

        return (Math.Clamp(gr, MinGain, MaxGain),
                Math.Clamp(gg, MinGain, MaxGain),
                Math.Clamp(gb, MinGain, MaxGain));
    }

    private static readonly (double r, double g, double b) AnchorGains = RawGains(AsShotKelvin, 0.0);

    /// <summary>von Kries gains that neutralise an illuminant outright, before the
    /// anchor is divided out. Not useful on its own — see <see cref="Gains"/>.</summary>
    private static (double r, double g, double b) RawGains(double kelvin, double tint)
    {
        var (x, y) = IlluminantXy(kelvin, tint);
        if (y < 1e-6) return (1.0, 1.0, 1.0);

        // White point → XYZ (Y = 1) → linear sRGB.
        double bigX = x / y, bigY = 1.0, bigZ = (1.0 - x - y) / y;
        double lr = 3.2404542 * bigX - 1.5371385 * bigY - 0.4985314 * bigZ;
        double lg = -0.9692660 * bigX + 1.8760108 * bigY + 0.0415560 * bigZ;
        double lb = 0.0556434 * bigX - 0.2040259 * bigY + 1.0572252 * bigZ;

        const double floor = 1.0 / MaxGain;
        if (lr < floor) lr = floor;
        if (lg < floor) lg = floor;
        if (lb < floor) lb = floor;

        return (1.0 / lr, 1.0 / lg, 1.0 / lb);
    }
}
