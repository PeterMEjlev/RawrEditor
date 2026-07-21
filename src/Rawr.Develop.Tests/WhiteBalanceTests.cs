using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins the illuminant-based white balance model: the anchor is a true no-op,
/// the sliders move colour in the directions their labels promise, neither of
/// them moves brightness, and the Temperature slider is even-handed in mireds
/// rather than in Kelvin (which is what makes it feel like Lightroom's).
/// </summary>
public class WhiteBalanceTests
{
    private static double Luma((double r, double g, double b) gains)
        => 0.2126 * gains.r + 0.7152 * gains.g + 0.0722 * gains.b;

    // ── 1. The anchor is exactly identity ──────────────────────────────────
    [Fact]
    public void Anchor_IsExactlyUnityGain()
    {
        var g = WhiteBalance.Gains(WhiteBalance.AsShotKelvin, 0.0);
        Assert.Equal(1.0, g.r, 12);
        Assert.Equal(1.0, g.g, 12);
        Assert.Equal(1.0, g.b, 12);
    }

    [Fact]
    public void RelativeSliderAtZero_IsTheAnchor()
    {
        Assert.Equal(WhiteBalance.AsShotKelvin, WhiteBalance.TemperatureToKelvin(0.0), 9);
        var g = WhiteBalance.Gains(WhiteBalance.TemperatureToKelvin(0.0), 0.0);
        Assert.Equal(1.0, g.r, 12);
        Assert.Equal(1.0, g.b, 12);
    }

    // ── 2. Direction: higher Kelvin renders warmer ─────────────────────────
    // The slider names the illuminant, so claiming a bluer light means the
    // render compensates warm. This is the convention Lightroom uses.
    [Theory]
    [InlineData(2500.0)]
    [InlineData(4000.0)]
    [InlineData(9000.0)]
    [InlineData(20000.0)]
    public void HigherKelvin_IsWarmer(double kelvin)
    {
        var lo = WhiteBalance.Gains(kelvin, 0.0);
        var hi = WhiteBalance.Gains(kelvin * 1.2, 0.0);
        // Warmer = more red relative to blue.
        Assert.True(hi.r / hi.b > lo.r / lo.b,
            $"{kelvin * 1.2:F0}K should be warmer than {kelvin:F0}K");
    }

    [Fact]
    public void BelowAnchor_IsCool_AboveAnchor_IsWarm()
    {
        var cool = WhiteBalance.Gains(3000.0, 0.0);
        var warm = WhiteBalance.Gains(15000.0, 0.0);
        Assert.True(cool.b > cool.r, "3000K should render cool (blue-weighted)");
        Assert.True(warm.r > warm.b, "15000K should render warm (red-weighted)");
    }

    // ── 3. Tint moves green↔magenta and nothing else ───────────────────────
    [Fact]
    public void PositiveTint_IsMagenta_NegativeTint_IsGreen()
    {
        var magenta = WhiteBalance.Gains(WhiteBalance.AsShotKelvin, 100.0);
        var green = WhiteBalance.Gains(WhiteBalance.AsShotKelvin, -100.0);

        // Magenta = green pulled down relative to the R/B average; green = the reverse.
        Assert.True(magenta.g < (magenta.r + magenta.b) / 2.0, "+tint should read magenta");
        Assert.True(green.g > (green.r + green.b) / 2.0, "−tint should read green");
    }

    // ── 4. Both sliders are purely chromatic ───────────────────────────────
    // The old model failed this: gainG moved with Tint and nothing compensated,
    // so dragging a colour slider changed the image's brightness.
    [Theory]
    [InlineData(2000.0, 0.0)]
    [InlineData(2000.0, 100.0)]
    [InlineData(6504.0, -100.0)]
    [InlineData(12000.0, 60.0)]
    [InlineData(50000.0, -80.0)]
    public void Gains_PreserveNeutralLuma(double kelvin, double tint)
        => Assert.Equal(1.0, Luma(WhiteBalance.Gains(kelvin, tint)), 9);

    // ── 5. The slider is mired-linear, which is the Lightroom feel ─────────
    // Equal slider steps must be equal *mired* steps, not equal Kelvin steps.
    // A Kelvin-linear slider spends most of its travel in the range nobody
    // needs and crams 2000-4000K into the first few percent.
    [Fact]
    public void RelativeSlider_IsLinearInMireds()
    {
        double m0 = WhiteBalance.ToMired(WhiteBalance.TemperatureToKelvin(-60.0));
        double m1 = WhiteBalance.ToMired(WhiteBalance.TemperatureToKelvin(-20.0));
        double m2 = WhiteBalance.ToMired(WhiteBalance.TemperatureToKelvin(20.0));
        double m3 = WhiteBalance.ToMired(WhiteBalance.TemperatureToKelvin(60.0));

        Assert.Equal(m0 - m1, m1 - m2, 9);
        Assert.Equal(m1 - m2, m2 - m3, 9);
        // …and 40 slider units is 40 mireds at MiredsPerTempUnit = 1.
        Assert.Equal(40.0 * WhiteBalance.MiredsPerTempUnit, m0 - m1, 9);
    }

    [Theory]
    [InlineData(-100.0)]
    [InlineData(-33.0)]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(100.0)]
    public void KelvinConversion_RoundTrips(double temperature)
        => Assert.Equal(temperature,
            WhiteBalance.KelvinToTemperature(WhiteBalance.TemperatureToKelvin(temperature)), 6);

    // ── 6. Monotonic and finite across the whole exposed range ─────────────
    [Fact]
    public void Gains_AreMonotonicAndFinite_AcrossFullRange()
    {
        double prevRatio = double.NegativeInfinity;
        for (double k = WhiteBalance.MinKelvin; k <= WhiteBalance.MaxKelvin; k += 250.0)
        {
            var g = WhiteBalance.Gains(k, 0.0);
            Assert.True(double.IsFinite(g.r) && double.IsFinite(g.g) && double.IsFinite(g.b));
            Assert.True(g.r > 0 && g.g > 0 && g.b > 0);

            double ratio = g.r / g.b;
            Assert.True(ratio > prevRatio, $"R/B ratio must rise with Kelvin; stalled at {k:F0}K");
            prevRatio = ratio;
        }
    }

    [Fact]
    public void LocusExtrapolation_IsContinuousAtTheFitBoundary()
    {
        // 25000K is where the Kim et al. cubics stop and the mired-linear
        // extrapolation takes over; the seam must not be visible.
        //
        // Compared against a tolerance rather than with Assert.Equal's decimal
        // -place overload: that overload rounds each value before comparing, so
        // two chromaticities a millionth apart fail whenever they happen to
        // straddle a rounding boundary — which these do.
        var below = WhiteBalance.LocusXy(24999.0);
        var above = WhiteBalance.LocusXy(25001.0);
        Assert.True(Math.Abs(below.x - above.x) < 1e-5, $"x seam: {below.x} vs {above.x}");
        Assert.True(Math.Abs(below.y - above.y) < 1e-5, $"y seam: {below.y} vs {above.y}");
    }
}
