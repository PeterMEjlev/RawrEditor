using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins the Color Mixer: neutral must be a bit-exact no-op (the whole baseline
/// calibration rests on it), band weights must form a partition of unity so eight
/// equal bands read as one global adjustment, greys must never pick up colour,
/// and each slider must move the axis it names and only that axis.
/// </summary>
public class ColorMixerTests
{
    private static ColorMixerSettings Settings(Action<ColorMixerSettings> setup)
    {
        var s = new ColorMixerSettings();
        setup(s);
        return s;
    }

    private static (float r, float g, float b) Run(ColorMixerSettings settings,
                                                   float r, float g, float b)
    {
        var p = ColorMixer.Build(settings);
        ColorMixer.Apply(p, ref r, ref g, ref b);
        return (r, g, b);
    }

    private static (float h, float s, float l) Hsl(float r, float g, float b)
    {
        ColorMixer.RgbToHsl(r / 255f, g / 255f, b / 255f, out float h, out float s, out float l);
        return (h, s, l);
    }

    // ── 1. Neutral is bit-exact ────────────────────────────────────────────
    // Not "close": the tone LUT and every calibration in BasicTone were fitted
    // against a render with no mixer in it, so an untouched panel has to leave
    // the pixel alone to the bit.
    [Theory]
    [InlineData(200f, 40f, 30f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(255f, 255f, 255f)]
    [InlineData(17f, 200f, 130f)]
    public void Neutral_IsExactNoOp(float r, float g, float b)
    {
        var (or_, og, ob) = Run(new ColorMixerSettings(), r, g, b);
        Assert.Equal(r, or_);
        Assert.Equal(g, og);
        Assert.Equal(b, ob);
    }

    [Fact]
    public void Neutral_LeavesOverRangeHighlightsUntouched()
    {
        // The tone pipeline deliberately carries values above 255. An inactive
        // mixer must not clamp them — that is the export's decision, later.
        var (r, g, b) = Run(new ColorMixerSettings(), 300f, 280f, 260f);
        Assert.Equal(300f, r);
        Assert.Equal(280f, g);
        Assert.Equal(260f, b);
    }

    // ── 2. Greys belong to no band ─────────────────────────────────────────
    [Theory]
    [InlineData(0f)]
    [InlineData(64f)]
    [InlineData(128f)]
    [InlineData(255f)]
    public void AchromaticPixels_NeverGainColour(float v)
    {
        var settings = Settings(s =>
        {
            for (int i = 0; i < ColorMixer.BandCount; i++)
            {
                s[(ColorBand)i].Hue = 100;
                s[(ColorBand)i].Saturation = 100;
                s[(ColorBand)i].Luminance = 100;
            }
        });

        var (r, g, b) = Run(settings, v, v, v);
        Assert.Equal(v, r);
        Assert.Equal(v, g);
        Assert.Equal(v, b);
    }

    // ── 3. Band weights are a partition of unity ───────────────────────────
    // This is the property that makes the uneven band spacing safe. If the
    // weights did not sum to 1 the effect would pulse as hue swept across the
    // wheel, strongest at the centres and weakest between them.
    [Fact]
    public void BandWeights_SumToOne_AtEveryHue()
    {
        for (float hue = 0f; hue < 360f; hue += 0.5f)
        {
            var (lo, hi, t) = ColorMixer.Bracket(hue);
            float wHi = t * t * (3f - 2f * t);
            float wLo = 1f - wHi;

            Assert.InRange(t, 0f, 1f);
            Assert.Equal(1f, wLo + wHi, 5);
            Assert.NotEqual(lo, hi);
        }
    }

    [Fact]
    public void Bracket_LandsExactlyOnBandCentres()
    {
        for (int i = 0; i < ColorMixer.BandCount; i++)
        {
            var (lo, _, t) = ColorMixer.Bracket((float)ColorMixer.BandCenters[i]);
            Assert.Equal(i, lo);
            Assert.Equal(0f, t, 5);
        }
    }

    [Fact]
    public void AllBandsEqual_BehavesAsOneGlobalAdjustment()
    {
        // Eight bands at the same value must move every hue by the same amount.
        var settings = Settings(s =>
        {
            for (int i = 0; i < ColorMixer.BandCount; i++)
                s[(ColorBand)i].Hue = 50;
        });

        var p = ColorMixer.Build(settings);
        float expected = (float)(0.5 * ColorMixer.MaxHueShiftDegrees);

        for (float hue = 0f; hue < 360f; hue += 3f)
        {
            ColorMixer.HslToRgb(hue, 0.8f, 0.5f, out float r, out float g, out float b);
            r *= 255f; g *= 255f; b *= 255f;
            ColorMixer.Apply(p, ref r, ref g, ref b);

            float shifted = Hsl(r, g, b).h;
            float delta = shifted - hue;
            if (delta < -180f) delta += 360f;
            if (delta > 180f) delta -= 360f;

            Assert.Equal(expected, delta, 1);
        }
    }

    // ── 4. Each slider moves its own axis ──────────────────────────────────
    [Fact]
    public void Hue_RotatesHue_AndLeavesSaturationAndLightness()
    {
        // Pure red sits exactly on the Red band centre, so it gets the full shift.
        ColorMixer.HslToRgb(0f, 0.8f, 0.5f, out float r, out float g, out float b);
        var before = Hsl(r * 255f, g * 255f, b * 255f);

        var settings = Settings(s => s[ColorBand.Red].Hue = 100);
        var (or_, og, ob) = Run(settings, r * 255f, g * 255f, b * 255f);
        var after = Hsl(or_, og, ob);

        Assert.Equal((float)ColorMixer.MaxHueShiftDegrees, after.h, 1);
        Assert.Equal(before.s, after.s, 3);
        Assert.Equal(before.l, after.l, 3);
    }

    [Fact]
    public void Saturation_MinusHundred_TakesTheBandToGrey()
    {
        ColorMixer.HslToRgb(120f, 0.8f, 0.5f, out float r, out float g, out float b);
        var settings = Settings(s => s[ColorBand.Green].Saturation = -100);

        var (or_, og, ob) = Run(settings, r * 255f, g * 255f, b * 255f);
        Assert.Equal(0f, Hsl(or_, og, ob).s, 3);
    }

    [Fact]
    public void Saturation_Boost_RollsOffInsteadOfClipping()
    {
        // An already-vivid band must still gain, but must not pin flat at 1 —
        // that is where the modelling inside a strong colour disappears.
        float boosted = ColorMixer.ScaleSaturation(0.92f, 1.0f);
        Assert.True(boosted > 0.92f, "a full boost must still increase saturation");
        Assert.True(boosted < 1f, $"saturation {boosted:F4} must stay off the ceiling");
    }

    [Fact]
    public void Saturation_SmallBoost_IsEssentiallyUnrolled()
    {
        // Near neutral the knee has unit slope, so there is no visible step just
        // past 0 the way a hard limiter would give.
        float expected = 0.2f * (float)Presence.SaturationScale(4.0);
        Assert.Equal(expected, ColorMixer.ScaleSaturation(0.2f, 0.04f), 3);
    }

    [Fact]
    public void Luminance_MovesTowardWhiteAndBlack_WithoutLeavingRange()
    {
        for (float l = 0f; l <= 1f; l += 0.05f)
        {
            Assert.InRange(ColorMixer.ShiftLuminance(l, 1f), 0f, 1f);
            Assert.InRange(ColorMixer.ShiftLuminance(l, -1f), 0f, 1f);
        }

        Assert.True(ColorMixer.ShiftLuminance(0.5f, 1f) > 0.5f);
        Assert.True(ColorMixer.ShiftLuminance(0.5f, -1f) < 0.5f);
        Assert.Equal(0.5f, ColorMixer.ShiftLuminance(0.5f, 0f), 6);
    }

    [Fact]
    public void Luminance_LeavesHueAndSaturationAlone()
    {
        ColorMixer.HslToRgb(240f, 0.7f, 0.45f, out float r, out float g, out float b);
        var before = Hsl(r * 255f, g * 255f, b * 255f);

        var settings = Settings(s => s[ColorBand.Blue].Luminance = 80);
        var (or_, og, ob) = Run(settings, r * 255f, g * 255f, b * 255f);
        var after = Hsl(or_, og, ob);

        Assert.Equal(before.h, after.h, 2);
        Assert.Equal(before.s, after.s, 3);
        Assert.True(after.l > before.l);
    }

    // ── 5. Bands are actually selective ────────────────────────────────────
    [Fact]
    public void AdjustingOneBand_LeavesTheOppositeBandAlone()
    {
        // Red is 180° from Aqua and two whole bands from either neighbour, so a
        // Red-only push must not reach an aqua pixel at all.
        ColorMixer.HslToRgb(180f, 0.8f, 0.5f, out float r, out float g, out float b);
        var settings = Settings(s => s[ColorBand.Red].Saturation = 100);

        var (or_, og, ob) = Run(settings, r * 255f, g * 255f, b * 255f);
        Assert.Equal(r * 255f, or_, 2);
        Assert.Equal(g * 255f, og, 2);
        Assert.Equal(b * 255f, ob, 2);
    }

    [Fact]
    public void HueBetweenTwoCentres_GetsBothBandsBlended()
    {
        // 15° is halfway between Red (0°) and Orange (30°); with only Orange
        // pushed it must move, but by less than a pixel sitting on Orange itself.
        var settings = Settings(s => s[ColorBand.Orange].Luminance = 100);
        var p = ColorMixer.Build(settings);

        float Lift(float hue)
        {
            ColorMixer.HslToRgb(hue, 0.8f, 0.5f, out float r, out float g, out float b);
            r *= 255f; g *= 255f; b *= 255f;
            float before = Hsl(r, g, b).l;
            ColorMixer.Apply(p, ref r, ref g, ref b);
            return Hsl(r, g, b).l - before;
        }

        float onOrange = Lift(30f);
        float between = Lift(15f);
        float onRed = Lift(0f);

        Assert.True(onOrange > between, "the band centre must get the strongest lift");
        Assert.True(between > onRed, "a hue between centres must still get some lift");
        Assert.Equal(0f, onRed, 3);
    }

    // ── 6. HSL round trip ──────────────────────────────────────────────────
    /// <summary>Shortest angular distance, in degrees. Hue is circular, so a red
    /// that round-trips to 359.999° has not moved — comparing it to 0° linearly
    /// would fail on a colour that is bit-for-bit correct.</summary>
    private static float HueDistance(float a, float b)
    {
        float d = MathF.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    [Theory]
    [InlineData(0f, 0.8f, 0.5f)]
    [InlineData(30f, 1f, 0.25f)]
    [InlineData(120f, 0.5f, 0.75f)]
    [InlineData(240f, 0.33f, 0.5f)]
    [InlineData(315f, 0.9f, 0.6f)]
    [InlineData(200f, 0f, 0.4f)]
    public void HslRoundTrip_IsStable(float h, float s, float l)
    {
        ColorMixer.HslToRgb(h, s, l, out float r, out float g, out float b);
        ColorMixer.RgbToHsl(r, g, b, out float h2, out float s2, out float l2);

        Assert.Equal(l, l2, 4);
        Assert.Equal(s, s2, 4);
        if (s > 0f) Assert.True(HueDistance(h, h2) < 0.01f, $"hue {h} round-tripped to {h2}");
    }

    [Fact]
    public void HueWrap_PutsPureRedFullyInTheRedBand()
    {
        // Pure red can land either side of 0°/360° depending on which way the
        // float ties fall in HslToRgb. Both must weight Red alone — if the wrap
        // interval were mishandled, red would silently pick up Magenta's sliders.
        foreach (float hue in new[] { 0f, 359.999f })
        {
            var (lo, hi, t) = ColorMixer.Bracket(hue);
            float wHi = t * t * (3f - 2f * t);
            float redWeight = lo == (int)ColorBand.Red ? 1f - wHi
                            : hi == (int)ColorBand.Red ? wHi
                            : 0f;
            Assert.Equal(1f, redWeight, 3);
        }
    }

    // ── 7. Settings plumbing ───────────────────────────────────────────────
    [Fact]
    public void Clone_DoesNotShareBandsWithTheOriginal()
    {
        // DevelopSettings.Clone is a MemberwiseClone; if the mixer came across by
        // reference, a slider moved during an export would rewrite that export.
        var settings = new DevelopSettings();
        settings.ColorMixer[ColorBand.Blue].Saturation = 40;

        var snapshot = settings.Clone();
        settings.ColorMixer[ColorBand.Blue].Saturation = -90;

        Assert.Equal(40, snapshot.ColorMixer[ColorBand.Blue].Saturation);
    }

    [Fact]
    public void IsNeutral_TracksTheBands()
    {
        var settings = new DevelopSettings();
        Assert.True(settings.IsNeutral);

        settings.ColorMixer[ColorBand.Purple].Hue = 1;
        Assert.False(settings.IsNeutral);

        settings.Reset();
        Assert.True(settings.IsNeutral);
        Assert.True(settings.ColorMixer.IsNeutral);
    }
}
