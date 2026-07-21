using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Verification of the Exposure and Whites sliders against the behaviour their
/// Lightroom counterparts have.
///
/// <para>Assertions about what a change actually looks like are written in 8-bit
/// display terms rather than linear ones, via <see cref="Display8"/>. This
/// pipeline's output transform renders middle grey at ~190/255 and maps +2 EV to
/// ~250/255, so a linear number says very little about whether a viewer would see
/// the change — a slider that moves linear 1.0 to 0.5 has moved the render by
/// eleven code values out of 255.</para>
/// </summary>
public class ExposureAndWhitesTests
{
    private static double Display8(double linear)
        => BasicTone.LightroomMatch(BasicTone.DisplayCurve(linear)) * 255.0;

    private static (double r, double g, double b) Grey(double v) => (v, v, v);

    // ═══════════════════════════ Exposure ═══════════════════════════════════

    // ── 1. Exposure is a true physical stop, and stays one ──────────────────
    // The property the codebase deliberately built around, and the one Lightroom's
    // Exposure has too. A highlight rolloff on this slider was tried and removed
    // because any schedule strong enough to matter made it non-monotonic — see the
    // note in BasicTone. This pins the decision so it is not silently undone.
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 4.0)]
    [InlineData(-1.0, 0.5)]
    [InlineData(-2.0, 0.25)]
    public void Exposure_IsATruePhysicalStop(double ev, double expectedGain)
        => Assert.Equal(expectedGain, BasicTone.ExposureGain(ev), 12);

    // ── 2. Exposure is monotonic in the slider, for every tone ──────────────
    // The regression this file exists to prevent: with a strength-scaled rolloff,
    // very bright pixels got *darker* as the slider went up, and +2 EV clipped less
    // of the frame than +1 EV. Every tone must move one way as exposure increases.
    [Theory]
    [InlineData(0.02)]
    [InlineData(0.18)]
    [InlineData(0.60)]
    [InlineData(1.00)]
    [InlineData(3.00)]
    public void Exposure_IsMonotonicInTheSlider(double linear)
    {
        double prev = double.MinValue;
        for (double ev = -5.0; ev <= 5.0; ev += 0.1)
        {
            double v = linear * BasicTone.ExposureGain(ev);
            Assert.True(v > prev, $"tone {linear} must rise with exposure, stalled at {ev:F1} EV");
            prev = v;
        }
    }

    // ═══════════════════════════ Whites ═════════════════════════════════════

    // ── 8. Whites at 0 is an exact no-op ────────────────────────────────────
    [Fact]
    public void Whites_AtZero_IsExactNoOp()
    {
        double r = 0.42, g = 0.30, b = 0.66;
        BasicTone.ApplyWhites(ref r, ref g, ref b, 0.0);
        Assert.Equal(0.42, r, 12);
        Assert.Equal(0.30, g, 12);
        Assert.Equal(0.66, b, 12);
    }

    // ── 9. Whites is GLOBAL, not regional ───────────────────────────────────
    // The v3 behaviour this replaces read the regional evBase, so an isolated
    // specular highlight inside a dark region got a mask of ~0 and was left alone.
    // Lightroom's Whites moves every bright pixel regardless of its surroundings,
    // and that difference is most of why the old slider felt like a second
    // Highlights rather than a white point.
    [Fact]
    public void Whites_ActsOnBrightPixelsRegardlessOfSurroundings()
    {
        // Same bright pixel; under v4 there is no surrounding to consult at all.
        double r = 0.8, g = 0.8, b = 0.8;
        BasicTone.ApplyWhites(ref r, ref g, ref b, -100.0);
        Assert.True(r < 0.8, $"a bright pixel must move, got {r:F4}");

        // The old regional operator would have ignored it in a dark region.
        double r3 = 0.8, g3 = 0.8, b3 = 0.8;
        BasicTone.ApplyWhitesV3(ref r3, ref g3, ref b3, -100.0, evBase: -1.0);
        Assert.Equal(0.8, r3, 9);
    }

    // ── 10. Whites pivots: midtones and below stay exactly put ──────────────
    [Theory]
    [InlineData(0.02)]
    [InlineData(0.08)]
    [InlineData(0.15)]
    [InlineData(0.18)]
    public void Whites_LeavesMidtonesExact(double linear)
    {
        foreach (double amount in new[] { -100.0, -50.0, 50.0, 100.0 })
        {
            var (r, g, b) = Grey(linear);
            BasicTone.ApplyWhites(ref r, ref g, ref b, amount);
            Assert.Equal(linear, r, 12);
        }
    }

    // ── 11. Negative Whites pulls the top of the range down, visibly ────────
    // Graduated: strongest at the very top, tapering to nothing by the knee. The
    // magnitude assertion is the point — an earlier calibration put the soft
    // knee's asymptote at ~240/255, which capped the whole slider at fifteen code
    // values and made it feel like it did nothing.
    [Fact]
    public void NegativeWhites_PullsTheTopDown_TaperingToTheKnee()
    {
        double Move(double linear)
        {
            var (r, g, b) = Grey(linear);
            BasicTone.ApplyWhites(ref r, ref g, ref b, -100.0);
            return Display8(linear) - Display8(r);
        }

        double atClip = Move(1.0);     // renders 255
        double high = Move(0.5);       // renders ~245
        double nearKnee = Move(0.20);  // renders ~198, just above the knee

        Assert.True(atClip > 25.0, $"the extreme top must move visibly, dropped only {atClip:F1}");
        Assert.True(atClip > high, $"…more than the merely bright, {atClip:F1} vs {high:F1}");
        Assert.True(high > nearKnee, $"…which moves more than the knee, {high:F1} vs {nearKnee:F1}");
        Assert.True(nearKnee < 3.0, $"and it should taper out at the knee, {nearKnee:F1}");
    }

    // ── 11b. Half travel gives a real fraction of the effect ────────────────
    // Slope is multiplicative, so interpolating it linearly bunches the effect
    // into the last third of the slider and −50 feels like nothing happened.
    [Fact]
    public void Whites_AtHalfTravel_DoesAMeaningfulFractionOfTheWork()
    {
        double Move(double amount)
        {
            var (r, g, b) = Grey(1.0);
            BasicTone.ApplyWhites(ref r, ref g, ref b, amount);
            return Display8(1.0) - Display8(r);
        }

        double half = Move(-50.0), full = Move(-100.0);
        Assert.True(half > full * 0.4,
            $"−50 should be a real fraction of −100, got {half:F1} vs {full:F1}");
        Assert.True(half < full, "…but still less than full travel");
    }

    // ── 12. Positive Whites drives the top end into the clip ────────────────
    [Fact]
    public void PositiveWhites_PushesTheTopIntoTheClip()
    {
        var (r, g, b) = Grey(0.8);
        Assert.True(Display8(0.8) < 255.0, "0.8 linear should start below the clip");

        BasicTone.ApplyWhites(ref r, ref g, ref b, 100.0);
        Assert.True(r > 1.0, $"Whites +100 should drive a bright tone to the clip, got {r:F4}");
    }

    // ── 13. Whites and Highlights are comparable in strength ────────────────
    // They differ in character, not in authority: Whites is global and flattens
    // what it compresses, Highlights is regional and preserves detail. Both should
    // move a blown tone by a similar amount, as they do in Lightroom — if either
    // is a fraction of the other, one of the two sliders feels broken.
    [Fact]
    public void Whites_IsComparableInStrengthToHighlights()
    {
        const int w = 16, h = 16, n = w * h;

        var (wr, wg, wb) = Grey(1.0);
        BasicTone.ApplyWhites(ref wr, ref wg, ref wb, -100.0);
        double whitesDrop = Display8(1.0) - Display8(wr);

        var hr = new float[n]; var hg = new float[n]; var hb = new float[n];
        System.Array.Fill(hr, 1f); System.Array.Fill(hg, 1f); System.Array.Fill(hb, 1f);
        LocalHighlights.Apply(hr, hg, hb, w, h,
            new LocalHighlights.Options { Highlights = -100, Radius = 4 });
        double highlightsDrop = Display8(1.0) - Display8(hr[n / 2]);

        Assert.InRange(whitesDrop, highlightsDrop * 0.5, highlightsDrop * 2.0);
    }

    // ── 14. The two sliders keep their distinct character ───────────────────
    // Whites is global: it moves a bright pixel whatever surrounds it. Highlights
    // is regional, so an isolated specular in an otherwise dark frame is left
    // alone. That difference is the reason to have both.
    [Fact]
    public void Whites_MovesAnIsolatedSpecular_WhereHighlightsLeavesItAlone()
    {
        const int w = 64, h = 64, n = w * h;

        // A dark frame with a small bright speck in the middle.
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        System.Array.Fill(r, 0.02f); System.Array.Fill(g, 0.02f); System.Array.Fill(b, 0.02f);
        for (int y = 30; y < 34; y++)
            for (int x = 30; x < 34; x++)
            {
                int i = y * w + x;
                r[i] = 1.0f; g[i] = 1.0f; b[i] = 1.0f;
            }
        int c = 32 * w + 32;

        // Highlights reads the region, finds it dark, and barely touches the speck.
        var hr = (float[])r.Clone(); var hg = (float[])g.Clone(); var hb = (float[])b.Clone();
        LocalHighlights.Apply(hr, hg, hb, w, h, new LocalHighlights.Options { Highlights = -100 });

        // Whites has no region to consult and moves it like any other bright pixel.
        double wr2 = r[c], wg2 = g[c], wb2 = b[c];
        BasicTone.ApplyWhites(ref wr2, ref wg2, ref wb2, -100.0);

        double highlightsDrop = Display8(1.0) - Display8(hr[c]);
        double whitesDrop = Display8(1.0) - Display8(wr2);

        Assert.True(whitesDrop > highlightsDrop * 2.0,
            $"Whites should move an isolated specular far more than Highlights does: " +
            $"{whitesDrop:F1} vs {highlightsDrop:F1}");
    }

    // ── 14. Whites preserves colour and ordering ────────────────────────────
    [Theory]
    [InlineData(-100.0)]
    [InlineData(-40.0)]
    [InlineData(60.0)]
    [InlineData(100.0)]
    public void Whites_PreservesRatiosAndOrdering(double amount)
    {
        double r = 1.30, g = 0.90, b = 0.50;
        double rg = r / g, bg = b / g;
        BasicTone.ApplyWhites(ref r, ref g, ref b, amount);
        Assert.Equal(rg, r / g, 9);
        Assert.Equal(bg, b / g, 9);

        double prev = double.MinValue;
        for (double lin = 0.01; lin < 6.0; lin *= 1.05)
        {
            var (cr, cg, cb) = Grey(lin);
            BasicTone.ApplyWhites(ref cr, ref cg, ref cb, amount);
            Assert.True(cr > prev, $"must stay ordered at {lin:F3}");
            prev = cr;
        }
    }
}
