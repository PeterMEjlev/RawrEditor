namespace Rawr.Develop;

/// <summary>Lightroom's eight Color Mixer bands, in the order the swatch row shows them.</summary>
public enum ColorBand
{
    Red = 0, Orange = 1, Yellow = 2, Green = 3,
    Aqua = 4, Blue = 5, Purple = 6, Magenta = 7
}

/// <summary>One band's three sliders, each −100 … +100 and neutral at 0.</summary>
public struct BandAdjustment
{
    public double Hue;
    public double Saturation;
    public double Luminance;

    public readonly bool IsNeutral => Hue == 0.0 && Saturation == 0.0 && Luminance == 0.0;
}

/// <summary>
/// The 8 × 3 grid of Color Mixer values. A class (not a struct) so the view-model
/// can hold one and mutate bands in place, with an explicit <see cref="Clone"/>
/// because <see cref="DevelopSettings.Clone"/> is a MemberwiseClone — a shallow
/// copy would hand the exporter the <i>live</i> band array and let a slider moved
/// mid-export change what gets written.
/// </summary>
public sealed class ColorMixerSettings
{
    private readonly BandAdjustment[] _bands = new BandAdjustment[ColorMixer.BandCount];

    /// <summary>By-reference so callers can write one slider: <c>mixer[ColorBand.Red].Hue = 20</c>.</summary>
    public ref BandAdjustment this[ColorBand band] => ref _bands[(int)band];

    public bool IsNeutral
    {
        get
        {
            for (int i = 0; i < _bands.Length; i++)
                if (!_bands[i].IsNeutral) return false;
            return true;
        }
    }

    public void Reset() => Array.Clear(_bands);

    public ColorMixerSettings Clone()
    {
        var copy = new ColorMixerSettings();
        Array.Copy(_bands, copy._bands, _bands.Length);
        return copy;
    }

    internal BandAdjustment[] Bands => _bands;
}

/// <summary>
/// Lightroom's Color Mixer: eight hue bands, each with Hue / Saturation /
/// Luminance. Runs in HSL on the display-referred RGB the render has already
/// recomposed, so it sits at the very end of the pipeline — after
/// <see cref="Presence"/>, immediately before dither and quantise.
///
/// <para><b>Why HSL and not the chroma plane.</b> The pipeline carries luma +
/// Rec.709 chroma right up to recompose, and band membership could be read
/// straight off the chroma angle for one <c>atan2</c>. This works in HSL instead
/// because that is the model Lightroom's panel is named for and behaves like:
/// its Saturation reaches a real 0 (a band goes properly grey, not just
/// low-chroma), and its Luminance moves HSL lightness rather than Rec.709 luma,
/// which is why pushing blue luminance in Lightroom lifts a sky without
/// desaturating it. The round trip is only RGB→HSL→RGB — the recompose to RGB
/// had already happened for the dither.</para>
///
/// <para><b>Band weighting.</b> The eight centres are not evenly spaced (see
/// <see cref="BandCenters"/>), so a fixed-width falloff per band would leave gaps
/// in the wide intervals and overlap in the narrow ones. Instead every hue is
/// bracketed by its two neighbouring centres and the slider values are blended
/// between them, which makes the weights a partition of unity: they sum to 1 at
/// every hue, so setting all eight bands to the same value is exactly a global
/// adjustment, with no ripple at the band boundaries.</para>
/// </summary>
public static class ColorMixer
{
    public const int BandCount = 8;

    /// <summary>
    /// Hue angle (degrees) at the centre of each band, matching Adobe's. The
    /// spacing is deliberately uneven — 30° apart through the reds and yellows
    /// where the eye separates hues finely, 60° through the greens and blues
    /// where it does not.
    /// </summary>
    public static readonly double[] BandCenters = [0.0, 30.0, 60.0, 120.0, 180.0, 240.0, 285.0, 315.0];

    /// <summary>Hue rotation at a full ±100. One band-step through the close-packed
    /// end of the wheel: Red at +100 lands on Orange, which is where Lightroom's
    /// travel puts it.</summary>
    public const double MaxHueShiftDegrees = 30.0;

    /// <summary>Fraction of the remaining headroom Luminance covers at ±100 —
    /// +100 moves a band halfway to white, −100 halfway to black. Full travel to
    /// the endpoints would make the slider unusable over most of its range.</summary>
    public const double MaxLuminanceShift = 0.5;

    /// <summary>Below this HSL saturation a pixel has no meaningful hue, so it
    /// belongs to no band and the mixer leaves it alone. This is what keeps greys
    /// grey however hard the bands are pushed.</summary>
    public const float AchromaticEpsilon = 1e-4f;

    /// <summary>Per-render constants, hoisted out of the per-pixel loop.</summary>
    public readonly struct Params
    {
        public readonly float[] HueShift;    // degrees
        public readonly float[] SatSlider;   // −1 … +1
        public readonly float[] LumSlider;   // −1 … +1
        public readonly bool IsActive;

        public Params(float[] hueShift, float[] satSlider, float[] lumSlider, bool isActive)
        {
            HueShift = hueShift;
            SatSlider = satSlider;
            LumSlider = lumSlider;
            IsActive = isActive;
        }
    }

    public static Params Build(ColorMixerSettings settings)
    {
        var bands = settings.Bands;
        var hue = new float[BandCount];
        var sat = new float[BandCount];
        var lum = new float[BandCount];

        for (int i = 0; i < BandCount; i++)
        {
            hue[i] = (float)(Math.Clamp(bands[i].Hue, -100.0, 100.0) / 100.0 * MaxHueShiftDegrees);
            sat[i] = (float)(Math.Clamp(bands[i].Saturation, -100.0, 100.0) / 100.0);
            lum[i] = (float)(Math.Clamp(bands[i].Luminance, -100.0, 100.0) / 100.0);
        }

        return new Params(hue, sat, lum, !settings.IsNeutral);
    }

    /// <summary>
    /// Apply the mixer to one pixel, in place. <paramref name="r"/>/<paramref name="g"/>/
    /// <paramref name="b"/> are the render's display-referred 0…255 floats.
    /// </summary>
    public static void Apply(in Params p, ref float r, ref float g, ref float b)
    {
        if (!p.IsActive) return;

        const float inv255 = 1f / 255f;
        // The tone pipeline leaves values unclamped so highlight detail survives,
        // but HSL is only defined on the unit cube. Clamping here costs nothing
        // real: everything above 255 clips to white at quantise regardless.
        float rr = Clamp01(r * inv255);
        float gg = Clamp01(g * inv255);
        float bb = Clamp01(b * inv255);

        RgbToHsl(rr, gg, bb, out float h, out float s, out float l);
        if (s <= AchromaticEpsilon) return;

        // Blend the two bracketing bands' slider values. Blending the *slider
        // values* and then applying the curves once — rather than applying each
        // band's curve and blending the results — is what makes eight equal bands
        // behave as one global adjustment.
        var (lo, hi, t) = Bracket(h);
        float wHi = t * t * (3f - 2f * t);   // smoothstep
        float wLo = 1f - wHi;

        float hueShift = p.HueShift[lo] * wLo + p.HueShift[hi] * wHi;
        float satSlider = p.SatSlider[lo] * wLo + p.SatSlider[hi] * wHi;
        float lumSlider = p.LumSlider[lo] * wLo + p.LumSlider[hi] * wHi;

        h += hueShift;
        h -= MathF.Floor(h * (1f / 360f)) * 360f;

        s = ScaleSaturation(s, satSlider);
        l = ShiftLuminance(l, lumSlider);

        HslToRgb(h, s, l, out rr, out gg, out bb);
        r = rr * 255f;
        g = gg * 255f;
        b = bb * 255f;
    }

    /// <summary>
    /// Which two band centres a hue falls between, and how far along it sits.
    /// <see cref="BandCenters"/> starts at 0 and hues arrive in [0, 360), so the
    /// final interval (Magenta → Red) is the one that wraps.
    /// </summary>
    public static (int lo, int hi, float t) Bracket(float hue)
    {
        for (int i = 0; i < BandCount; i++)
        {
            float a = (float)BandCenters[i];
            float b = i + 1 < BandCount ? (float)BandCenters[i + 1] : (float)BandCenters[0] + 360f;
            if (hue >= a && hue < b)
                return (i, (i + 1) % BandCount, (hue - a) / (b - a));
        }

        // Unreachable for a normalised hue; degrade to the wrap interval's start.
        return (BandCount - 1, 0, 0f);
    }

    /// <summary>
    /// Band saturation. Reuses <see cref="Presence.SaturationScale"/> so the two
    /// saturation controls in the app share one response curve — −100 reaches a
    /// true 0, +100 doubles — then soft-knees the increase toward 1 rather than
    /// letting it clip. Without the knee an already-vivid band flattens onto S = 1
    /// and loses the modelling inside it, the same "poster paint" failure
    /// <see cref="Presence.BoostRolloff"/> exists to prevent.
    /// </summary>
    public static float ScaleSaturation(float s, float slider)
    {
        float target = s * (float)Presence.SaturationScale(slider * 100.0);
        if (target <= s) return target < 0f ? 0f : target;

        float head = 1f - s;
        if (head <= 1e-6f) return s;

        // head·(1 − e^(−d/head)): unit slope at d = 0, asymptote at 1.
        return s + head * (1f - MathF.Exp(-(target - s) / head));
    }

    /// <summary>Band lightness: a fraction of the distance to white or to black,
    /// so the result can never leave [0, 1] however the slider is pushed.</summary>
    public static float ShiftLuminance(float l, float slider)
    {
        if (slider == 0f) return l;
        const float k = (float)MaxLuminanceShift;
        return slider > 0f
            ? l + (1f - l) * slider * k
            : l + l * slider * k;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>sRGB-encoded 0…1 → HSL, hue in degrees [0, 360).</summary>
    public static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        l = (max + min) * 0.5f;

        float d = max - min;
        if (d <= 1e-7f)
        {
            h = 0f;
            s = 0f;
            return;
        }

        float sum = max + min;
        s = l > 0.5f ? d / (2f - sum) : d / sum;

        if (max == r) h = (g - b) / d + (g < b ? 6f : 0f);
        else if (max == g) h = (b - r) / d + 2f;
        else h = (r - g) / d + 4f;

        h *= 60f;
    }

    /// <summary>HSL (hue in degrees) → sRGB-encoded 0…1.</summary>
    public static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
    {
        if (s <= 0f)
        {
            r = g = b = l;
            return;
        }

        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;
        float hk = h * (1f / 360f);

        r = HueToChannel(p, q, hk + 1f / 3f);
        g = HueToChannel(p, q, hk);
        b = HueToChannel(p, q, hk - 1f / 3f);
    }

    private static float HueToChannel(float p, float q, float t)
    {
        t -= MathF.Floor(t);
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 0.5f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}
