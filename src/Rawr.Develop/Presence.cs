namespace Rawr.Develop;

/// <summary>
/// Lightroom-<i>like</i> Vibrance and Saturation, operating on the Rec.709 chroma
/// differences <see cref="DevelopProcessor"/> already carries (Cb = B−Y, Cr = R−Y
/// in 0…255 perceptual units, post-display-transform and post-chroma-denoise).
///
/// <para>Both the preview and the export loop call <see cref="Apply"/> with the
/// same <see cref="Params"/>, so the two paths cannot drift — this used to be two
/// hand-copied blocks in <see cref="DevelopProcessor.Render"/> and
/// <see cref="DevelopProcessor.RenderExport"/>.</para>
///
/// <para><b>What makes it feel like Lightroom.</b> Three things the previous
/// straight chroma multiply lacked:</para>
/// <list type="number">
/// <item><b>A rolloff on the boost.</b> Scaling chroma linearly drives already-saturated
///       colours straight into the gamut wall, where they land on a flat clipped
///       plateau — the "poster paint" look. Here the <i>increase</i> in chroma goes
///       through a soft knee, so weak colours get the full boost and strong ones
///       asymptote instead of clipping. Applying the knee to the increase rather
///       than to the magnitude is what keeps neutral bit-exact: at 0 the increase
///       is 0, and a soft knee leaves 0 alone.</item>
/// <item><b>Skin protection on Vibrance.</b> That is most of what separates
///       Lightroom's Vibrance from a saturation slider with a weight on it — push
///       Vibrance up and faces stay put while the rest of the frame comes alive.
///       The skin band is found by cosine similarity to a fixed direction in the
///       (Cr, Cb) plane, which costs a dot product instead of the per-pixel
///       <c>atan2</c> a hue-angle test would need.</item>
/// <item><b>A saturation curve that isn't linear.</b> −100 reaches true greyscale,
///       and the positive half accelerates rather than ramping evenly, which is how
///       Lightroom's upper travel behaves.</item>
/// </list>
/// </summary>
public static class Presence
{
    /// <summary>Always-on chroma multiplier that is part of the neutral render's
    /// camera look. The Lightroom-match tone LUT in <see cref="BasicTone"/> was
    /// calibrated against a baseline export that already had this applied, so it
    /// is the <i>zero</i> of the Saturation slider, not something to fold away.</summary>
    public const float BaselineChroma = 1.12f;

    /// <summary>Chroma magnitude (0…255 units) at which Vibrance's weighting has
    /// fallen to half. Weak colours get the full boost, strong ones little.</summary>
    public const float VibranceKnee = 0.012f;

    /// <summary>Skin-tone direction in the (Cr, Cb) plane, as a unit vector.
    /// Skin runs R &gt; G &gt; B, so Cr is positive and Cb negative — about −46°.</summary>
    public const float SkinCr = 0.696f;
    public const float SkinCb = -0.718f;

    /// <summary>Cosine similarity to <see cref="SkinCr"/>/<see cref="SkinCb"/> at
    /// which skin protection starts fading in. 0.5 is a 60° half-angle around the
    /// skin direction — wide enough to hold a face through its shadow-to-highlight
    /// hue drift, narrow enough to leave foliage and sky fully boosted.</summary>
    public const float SkinCosineEdge = 0.5f;

    /// <summary>How much of Vibrance is withheld at the centre of the skin band.</summary>
    public const float SkinProtection = 0.7f;

    /// <summary>Asymptote of the chroma-boost soft knee, in 0…255 chroma units.
    /// A boost may add at most this much on top of a colour's original chroma
    /// however hard the sliders are pushed.</summary>
    public const float BoostRolloff = 60.0f;

    /// <summary>Per-render constants, hoisted out of the per-pixel loop.</summary>
    public readonly struct Params
    {
        public readonly float SatMul;      // includes BaselineChroma
        public readonly float Vibrance;    // −1 … +1
        public readonly bool DoVibrance;
        public readonly bool DoRolloff;    // false at/below neutral ⇒ bit-exact no-op

        public Params(float satMul, float vibrance, bool doVibrance, bool doRolloff)
        {
            SatMul = satMul;
            Vibrance = vibrance;
            DoVibrance = doVibrance;
            DoRolloff = doRolloff;
        }
    }

    /// <summary>
    /// Saturation slider → chroma multiplier, excluding <see cref="BaselineChroma"/>.
    /// −100 → 0 (true greyscale); 0 → 1; +100 → 2. The positive half uses the
    /// reciprocal form so the effect accelerates toward the end of the travel the
    /// way Lightroom's does, instead of an even ramp that feels strong early and
    /// inert late.
    /// </summary>
    public static double SaturationScale(double saturation)
    {
        double t = Math.Clamp(saturation / 100.0, -1.0, 1.0);
        return t <= 0.0 ? 1.0 + t : 1.0 / (1.0 - 0.5 * t);
    }

    public static Params Build(double vibrance, double saturation)
    {
        float satMul = BaselineChroma * (float)SaturationScale(saturation);
        if (satMul < 0f) satMul = 0f;
        float vib = (float)(Math.Clamp(vibrance, -100.0, 100.0) / 100.0);

        // The knee only exists to tame a boost. When neither slider is pushing
        // chroma up, skipping it keeps the arithmetic identical to a plain
        // multiply — which is what holds the neutral render bit-exact.
        bool doRolloff = satMul > BaselineChroma || vib > 0f;
        return new Params(satMul, vib, vib != 0f, doRolloff);
    }

    /// <summary>
    /// Apply Vibrance then Saturation to one pixel's chroma pair, in place.
    /// </summary>
    public static void Apply(in Params p, ref float cb, ref float cr)
    {
        float mag0 = MathF.Sqrt(cb * cb + cr * cr);

        if (p.DoVibrance && mag0 > 1e-6f)
        {
            float weak = 1f / (1f + VibranceKnee * mag0);

            // Hold skin back. Cosine similarity to the skin direction; no atan2.
            float cos = (cr * SkinCr + cb * SkinCb) / mag0;
            if (cos > SkinCosineEdge)
            {
                float u = (cos - SkinCosineEdge) / (1f - SkinCosineEdge);
                float skin = u * u * (3f - 2f * u);          // smoothstep
                weak *= 1f - SkinProtection * skin;
            }

            float vibMul = 1f + p.Vibrance * weak;
            if (vibMul < 0f) vibMul = 0f;
            cb *= vibMul;
            cr *= vibMul;
        }

        cb *= p.SatMul;
        cr *= p.SatMul;

        if (!p.DoRolloff) return;

        // Soft-knee whatever chroma the sliders just added, measured against where
        // this pixel would have sat with both sliders at neutral. That reference
        // has to include BaselineChroma: measuring from the raw magnitude instead
        // would treat the always-on 1.12 as part of the boost, so the first click
        // of Saturation would suddenly compress a 12% lift that was never the
        // slider's doing — a visible step right next to neutral.
        //
        // d′ = W·(1 − e^(−d/W)) has unit slope at d = 0 and asymptotes at W, so a
        // small boost passes through untouched and a large one bends over instead
        // of clipping flat at the gamut edge.
        float magBase = mag0 * BaselineChroma;
        float mag = MathF.Sqrt(cb * cb + cr * cr);
        float d = mag - magBase;
        if (d <= 0f || mag < 1e-6f) return;

        float limited = magBase + BoostRolloff * (1f - MathF.Exp(-d / BoostRolloff));
        float scale = limited / mag;
        cb *= scale;
        cr *= scale;
    }
}
