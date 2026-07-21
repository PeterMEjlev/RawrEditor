using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins Vibrance/Saturation: neutral parity (the baseline chroma the tone LUT
/// was calibrated against must survive bit-exact), the boost rolloff that keeps
/// strong colours off the gamut wall, and the skin protection that is most of
/// what distinguishes Vibrance from Saturation.
/// </summary>
public class PresenceTests
{
    private static (float cb, float cr) Run(double vibrance, double saturation, float cb, float cr)
    {
        var p = Presence.Build(vibrance, saturation);
        Presence.Apply(p, ref cb, ref cr);
        return (cb, cr);
    }

    private static float Mag(float cb, float cr) => MathF.Sqrt(cb * cb + cr * cr);

    // ── 1. Neutral is exactly the old baseline multiply ────────────────────
    // The Lightroom-match LUT was calibrated against a render that already had
    // BaselineChroma applied, so neutral must reproduce it to the bit — not
    // approximately, or the whole baseline calibration drifts.
    [Theory]
    [InlineData(10f, 20f)]
    [InlineData(-40f, 5f)]
    [InlineData(0f, 0f)]
    [InlineData(120f, -90f)]
    public void Neutral_IsExactlyBaselineChroma(float cb, float cr)
    {
        var (ocb, ocr) = Run(0.0, 0.0, cb, cr);
        Assert.Equal(cb * Presence.BaselineChroma, ocb);
        Assert.Equal(cr * Presence.BaselineChroma, ocr);
    }

    // ── 2. Saturation −100 is true greyscale ───────────────────────────────
    [Fact]
    public void SaturationMinus100_IsGreyscale()
    {
        var (cb, cr) = Run(0.0, -100.0, 42f, -17f);
        Assert.Equal(0f, cb, 5);
        Assert.Equal(0f, cr, 5);
    }

    [Fact]
    public void SaturationScale_IsMonotonic()
    {
        double prev = double.NegativeInfinity;
        for (double s = -100; s <= 100; s += 5)
        {
            double v = Presence.SaturationScale(s);
            Assert.True(v > prev, $"scale must rise; stalled at {s}");
            prev = v;
        }
        Assert.Equal(0.0, Presence.SaturationScale(-100), 12);
        Assert.Equal(1.0, Presence.SaturationScale(0), 12);
        Assert.Equal(2.0, Presence.SaturationScale(100), 12);
    }

    // ── 3. Direction ───────────────────────────────────────────────────────
    [Fact]
    public void PositiveSliders_Boost_NegativeSliders_Cut()
    {
        float cb = 30f, cr = 25f;
        float baseMag = Mag(cb, cr) * Presence.BaselineChroma;

        var up = Run(0.0, 50.0, cb, cr);
        var down = Run(0.0, -50.0, cb, cr);
        Assert.True(Mag(up.cb, up.cr) > baseMag);
        Assert.True(Mag(down.cb, down.cr) < baseMag);

        var vup = Run(50.0, 0.0, cb, cr);
        var vdown = Run(-50.0, 0.0, cb, cr);
        Assert.True(Mag(vup.cb, vup.cr) > baseMag);
        Assert.True(Mag(vdown.cb, vdown.cr) < baseMag);
    }

    // ── 4. Hue is preserved — these are chroma scalers, not hue rotators ───
    [Theory]
    [InlineData(60.0, 0.0)]
    [InlineData(0.0, 80.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(-70.0, 30.0)]
    public void Hue_IsPreserved(double vib, double sat)
    {
        float cb = 28f, cr = 44f;
        var (ocb, ocr) = Run(vib, sat, cb, cr);
        // Same direction ⇒ the cross product vanishes.
        float cross = cb * ocr - cr * ocb;
        Assert.Equal(0f, cross / (Mag(cb, cr) * Mag(ocb, ocr) + 1e-9f), 4);
    }

    // ── 5. Vibrance favours weak colour over strong ────────────────────────
    [Fact]
    public void Vibrance_BoostsWeakColoursProportionallyMore()
    {
        // Two colours on the same (non-skin) hue, one weak and one strong.
        const float dirCb = 0.6f, dirCr = -0.8f;   // opposite the skin direction
        float weakIn = 8f, strongIn = 90f;

        var weak = Run(60.0, 0.0, dirCb * weakIn, dirCr * weakIn);
        var strong = Run(60.0, 0.0, dirCb * strongIn, dirCr * strongIn);

        float weakGain = Mag(weak.cb, weak.cr) / (weakIn * Presence.BaselineChroma);
        float strongGain = Mag(strong.cb, strong.cr) / (strongIn * Presence.BaselineChroma);
        Assert.True(weakGain > strongGain,
            $"weak {weakGain:F3} should out-gain strong {strongGain:F3}");
    }

    // ── 6. Skin protection — the actual point of Vibrance ──────────────────
    [Fact]
    public void Vibrance_ProtectsSkinHues()
    {
        const float m = 30f;
        // On the skin axis vs. the opposite hue at identical magnitude.
        var skin = Run(80.0, 0.0, Presence.SkinCb * m, Presence.SkinCr * m);
        var other = Run(80.0, 0.0, -Presence.SkinCb * m, -Presence.SkinCr * m);

        Assert.True(Mag(skin.cb, skin.cr) < Mag(other.cb, other.cr),
            "skin hues must be boosted less than other hues at equal chroma");
    }

    [Fact]
    public void Saturation_DoesNotProtectSkin()
    {
        // Saturation is the blunt instrument by design — that is the whole
        // reason both sliders exist.
        const float m = 30f;
        var skin = Run(0.0, 80.0, Presence.SkinCb * m, Presence.SkinCr * m);
        var other = Run(0.0, 80.0, -Presence.SkinCb * m, -Presence.SkinCr * m);
        Assert.Equal(Mag(other.cb, other.cr), Mag(skin.cb, skin.cr), 3);
    }

    // ── 7. The boost rolloff keeps strong colour off the gamut wall ────────
    [Fact]
    public void Boost_RollsOff_InsteadOfRunningAway()
    {
        float cb = 100f, cr = 80f;
        float baseMag = Mag(cb, cr) * Presence.BaselineChroma;
        var (ocb, ocr) = Run(100.0, 100.0, cb, cr);

        float added = Mag(ocb, ocr) - baseMag;
        Assert.True(added > 0f, "a full boost must still increase chroma");
        Assert.True(added < Presence.BoostRolloff,
            $"added chroma {added:F1} must stay under the {Presence.BoostRolloff} asymptote");
    }

    [Fact]
    public void SmallBoost_IsEssentiallyUnrolled()
    {
        // Near neutral the knee has unit slope, so a gentle boost should arrive
        // almost intact — no visible step just past 0.
        float cb = 4f, cr = 3f;
        var (ocb, ocr) = Run(0.0, 4.0, cb, cr);
        float expected = Mag(cb, cr) * Presence.BaselineChroma * (float)Presence.SaturationScale(4.0);
        Assert.Equal(expected, Mag(ocb, ocr), 1);
    }

    // ── 8. Achromatic pixels stay achromatic, at any setting ───────────────
    [Theory]
    [InlineData(100.0, 100.0)]
    [InlineData(-100.0, -100.0)]
    [InlineData(50.0, -50.0)]
    public void Neutral_Pixels_Never_Gain_Colour(double vib, double sat)
    {
        var (cb, cr) = Run(vib, sat, 0f, 0f);
        Assert.Equal(0f, cb);
        Assert.Equal(0f, cr);
    }
}
