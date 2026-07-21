using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Synthetic-ramp verification of the Lightroom-like Basic tone model. These
/// hit the exact <see cref="BasicTone"/> functions the preview/export path
/// calls, so they pin slider behaviour and — crucially — neutral parity with
/// RAWR's original camera-matched look.
/// </summary>
public class BasicToneTests
{
    private const double Eps = 1e-9;

    // ── 1. Exposure: true EV gain ──────────────────────────────────────────
    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(1.0, 2.0)]
    [InlineData(2.0, 4.0)]
    [InlineData(-1.0, 0.5)]
    public void Exposure_IsTrueEvGain(double ev, double expectedGain)
    {
        Assert.Equal(expectedGain, BasicTone.ExposureGain(ev), 12);
        // …and the gain doubles a linear value at +1 EV, before tone mapping.
        Assert.Equal(0.2, 0.1 * BasicTone.ExposureGain(1.0), 12);
    }

    // ── 2. Contrast pivots on middle grey (it stays put) ───────────────────
    [Theory]
    [InlineData(-100)]
    [InlineData(-40)]
    [InlineData(50)]
    [InlineData(100)]
    public void Contrast_LeavesMiddleGrey_Stable(double contrast)
    {
        double r = BasicTone.MiddleGray, g = BasicTone.MiddleGray, b = BasicTone.MiddleGray;
        BasicTone.ApplyHighlightShadowContrast(
            ref r, ref g, ref b, 0.0, 0.0, BasicTone.ContrastSlope(contrast));

        Assert.Equal(BasicTone.MiddleGray, r, 9);
        Assert.Equal(BasicTone.MiddleGray, g, 9);
        Assert.Equal(BasicTone.MiddleGray, b, 9);
    }

    // ── 3. Highlights act on bright tones only ─────────────────────────────
    [Fact]
    public void Highlights_AffectBrightTones_NotMidOrShadow()
    {
        (double r, double g, double b) Apply(double v, double hl)
        {
            double r = v, g = v, b = v;
            BasicTone.ApplyHighlightShadowContrast(ref r, ref g, ref b, hl, 0.0, 1.0);
            return (r, g, b);
        }

        var dark = Apply(0.02, -100);
        var mid = Apply(BasicTone.MiddleGray, -100);
        var bright = Apply(0.85, -100);

        Assert.Equal(0.02, dark.r, 6);                 // shadow untouched
        Assert.Equal(BasicTone.MiddleGray, mid.r, 6);  // midtone untouched
        Assert.True(0.85 - bright.r > 0.3,             // highlight strongly recovered
            $"expected strong highlight pull, got {bright.r:F3}");
    }

    // ── 4. Shadows act on dark tones only ──────────────────────────────────
    [Fact]
    public void Shadows_AffectDarkTones_NotMidOrBright()
    {
        (double r, double g, double b) Apply(double v, double sh)
        {
            double r = v, g = v, b = v;
            BasicTone.ApplyHighlightShadowContrast(ref r, ref g, ref b, 0.0, sh, 1.0);
            return (r, g, b);
        }

        var dark = Apply(0.03, 100);
        var mid = Apply(BasicTone.MiddleGray, 100);
        var bright = Apply(0.70, 100);

        Assert.True(dark.r > 0.03 * 2.0,               // shadow strongly lifted
            $"expected strong shadow lift, got {dark.r:F3}");
        Assert.Equal(BasicTone.MiddleGray, mid.r, 6);  // midtone untouched
        Assert.Equal(0.70, bright.r, 6);               // highlight untouched
    }

    // ── 5. Whites moves the highlight clipping point ───────────────────────
    [Fact]
    public void Whites_ChangesHighlightClipping()
    {
        Assert.Equal(1.0, BasicTone.WhiteLin(0), 12);  // neutral ⇒ identity endpoint
        double black0 = BasicTone.BlackLin(0);

        double neutral = BasicTone.RemapEndpoints(0.6, black0, BasicTone.WhiteLin(0));
        double pushed = BasicTone.RemapEndpoints(0.6, black0, BasicTone.WhiteLin(100));
        double protectedHi = BasicTone.RemapEndpoints(0.95, black0, BasicTone.WhiteLin(-100));

        Assert.Equal(0.6, neutral, 12);                // Whites 0: no change
        Assert.True(pushed >= 1.0,                     // Whites +: 0.6 now clips white
            $"expected clip, got {pushed:F3}");
        Assert.True(protectedHi < 0.95,                // Whites −: highlight pulled in
            $"expected protected highlight, got {protectedHi:F3}");
    }

    // ── 6. Blacks moves the shadow clipping point ──────────────────────────
    [Fact]
    public void Blacks_ChangesBlackClipping()
    {
        Assert.Equal(0.0, BasicTone.BlackLin(0), 12);  // neutral ⇒ identity endpoint
        double white0 = BasicTone.WhiteLin(0);

        const double deepShadow = 0.005;               // well below any black point
        double neutral = BasicTone.RemapEndpoints(deepShadow, BasicTone.BlackLin(0), white0);
        double crushed = BasicTone.RemapEndpoints(deepShadow, BasicTone.BlackLin(-100), white0);
        double opened = BasicTone.RemapEndpoints(deepShadow, BasicTone.BlackLin(100), white0);

        Assert.Equal(deepShadow, neutral, 12);         // Blacks 0: no change
        Assert.True(crushed < 0.0,                     // Blacks −: deep shadow clips to black
            $"expected clip to black, got {crushed:F3}");
        Assert.True(opened > deepShadow,               // Blacks +: shadow opened
            $"expected lifted shadow, got {opened:F3}");
    }

    // ── 7. Neutral parity: every slider at 0 is a true no-op ───────────────
    [Fact]
    public void Neutral_IsIdentity_InLinearSpace()
    {
        Assert.Equal(1.0, BasicTone.ExposureGain(0), 12);
        Assert.Equal(1.0, BasicTone.ContrastSlope(0), 12);

        for (int e = -5; e <= 5; e++)
            Assert.Equal(e, BasicTone.AdjustedEv(e, 0, 0, 1.0), 12);

        double r = 0.42, g = 0.30, b = 0.66;
        BasicTone.ApplyHighlightShadowContrast(ref r, ref g, ref b, 0, 0, 1.0);
        Assert.Equal(0.42, r, 12);
        Assert.Equal(0.30, g, 12);
        Assert.Equal(0.66, b, 12);

        for (int i = 0; i <= 64; i++)
        {
            double v = i / 64.0;
            Assert.Equal(v,
                BasicTone.RemapEndpoints(v, BasicTone.BlackLin(0), BasicTone.WhiteLin(0)), 12);
        }
    }

    // ── 7b. Neutral parity: display curve is exactly the original look ─────
    [Fact]
    public void DisplayCurve_MatchesOriginalCameraMatchedFormula()
    {
        for (int i = 0; i <= 256; i++)
        {
            double lin = i / 256.0;

            double srgb = lin <= 0.0031308
                ? 12.92 * lin
                : 1.055 * Math.Pow(lin, 1.0 / 2.4) - 0.055;
            double p = Math.Pow(srgb, 0.70);
            double tt = p * 2.0 - 1.0;
            double sCurve = (Math.Tanh(tt * 2.0) / Math.Tanh(2.0) + 1.0) * 0.5;
            double expected = p * (1.0 - 0.18) + sCurve * 0.18;

            Assert.Equal(expected, BasicTone.DisplayCurve(lin), 12);
        }
    }

    // ── 7c. Lightroom-match calibration: monotone, anchored, brightens mids ─
    // It must be a well-behaved transfer (the engine bakes it into the display
    // LUT): fixed at the endpoints, monotonic, in-range, and — per the
    // measured RAWR→LR curve — it lifts the midtones (RAWR rendered darker).
    [Fact]
    public void LightroomMatch_IsMonotoneAnchoredAndLiftsMidtones()
    {
        Assert.Equal(0.0, BasicTone.LightroomMatch(0.0), 9);
        Assert.Equal(1.0, BasicTone.LightroomMatch(1.0), 9);
        Assert.Equal(0.0, BasicTone.LightroomMatch(-0.5), 9);   // clamps low
        Assert.Equal(1.0, BasicTone.LightroomMatch(1.5), 9);    // clamps high

        double prev = -1.0;
        for (int i = 0; i <= 512; i++)
        {
            double v = i / 512.0;
            double y = BasicTone.LightroomMatch(v);
            Assert.InRange(y, 0.0, 1.0);
            Assert.True(y >= prev - 1e-9, $"must be monotone, dipped at {v:F3}");
            prev = y;
        }

        // Measured transfer pulls RAWR up through the mids (it exported dark).
        Assert.True(BasicTone.LightroomMatch(0.5) > 0.55,
            $"expected midtone lift, got {BasicTone.LightroomMatch(0.5):F3}");
    }

    // ── 8. SmoothStep behaves ──────────────────────────────────────────────
    [Fact]
    public void SmoothStep_ClampsAndIsSymmetric()
    {
        Assert.Equal(0.0, BasicTone.SmoothStep(0, 3, -1), Eps);
        Assert.Equal(0.0, BasicTone.SmoothStep(0, 3, 0), Eps);
        Assert.Equal(1.0, BasicTone.SmoothStep(0, 3, 3), Eps);
        Assert.Equal(1.0, BasicTone.SmoothStep(0, 3, 9), Eps);
        Assert.Equal(0.5, BasicTone.SmoothStep(0, 3, 1.5), Eps);

        double prev = -1;
        for (int i = 0; i <= 30; i++)
        {
            double y = BasicTone.SmoothStep(0, 3, i / 10.0);
            Assert.True(y >= prev, "smoothstep must be monotonic");
            prev = y;
        }
    }

    // ── 9. Edge-aware sliders at 0 are identity regardless of evBase ────────
    [Theory]
    [InlineData(-3.0)]
    [InlineData(0.0)]
    [InlineData(3.0)]
    public void EdgeAware_AtZero_IsIdentity(double evBase)
    {
        double r = 0.42, g = 0.30, b = 0.66;
        BasicTone.ApplyShadows   (ref r, ref g, ref b, 0.0, evBase);
        BasicTone.ApplyWhites    (ref r, ref g, ref b, 0.0);   // v4 is global
        BasicTone.ApplyBlacks    (ref r, ref g, ref b, 0.0, evBase);

        Assert.Equal(0.42, r, 12);
        Assert.Equal(0.30, g, 12);
        Assert.Equal(0.66, b, 12);
    }

    // ── 10. V3 sliders gate on evBase, NOT on the per-pixel luminance ──────
    [Fact]
    public void V3_Shadows_GateOnRegionalDarkness()
    {
        // mid-grey pixel inside a dark region ⇒ Shadows act
        double r = BasicTone.MiddleGray, g = BasicTone.MiddleGray, b = BasicTone.MiddleGray;
        BasicTone.ApplyShadowsV3(ref r, ref g, ref b, 100.0, evBase: -3.0);
        Assert.True(r > BasicTone.MiddleGray * 1.1,
            $"expected strong shadow lift, got {r:F3}");

        // mid-grey pixel inside a bright region ⇒ Shadows silent
        double r2 = BasicTone.MiddleGray, g2 = BasicTone.MiddleGray, b2 = BasicTone.MiddleGray;
        BasicTone.ApplyShadowsV3(ref r2, ref g2, ref b2, 100.0, evBase: 3.0);
        Assert.Equal(BasicTone.MiddleGray, r2, 9);
    }

    [Fact]
    public void V3_Whites_GateOnHighEv()
    {
        double r = 0.4, g = 0.4, b = 0.4;
        BasicTone.ApplyWhitesV3(ref r, ref g, ref b, 100.0, evBase: 2.5);
        Assert.True(r > 0.5, $"expected push in bright region, got {r:F3}");

        double r2 = 0.4, g2 = 0.4, b2 = 0.4;
        BasicTone.ApplyWhitesV3(ref r2, ref g2, ref b2, 100.0, evBase: -1.0);
        Assert.Equal(0.4, r2, 9);
    }

    [Fact]
    public void V3_Blacks_GateOnLowEv()
    {
        double r = 0.05, g = 0.05, b = 0.05;
        BasicTone.ApplyBlacksV3(ref r, ref g, ref b, 100.0, evBase: -4.0);
        Assert.True(r > 0.08, $"expected lift in dark region, got {r:F3}");

        double r2 = 0.05, g2 = 0.05, b2 = 0.05;
        BasicTone.ApplyBlacksV3(ref r2, ref g2, ref b2, 100.0, evBase: 0.0);
        Assert.Equal(0.05, r2, 9);
    }

    // ── 12. Dispatchers agree with the underlying VN implementations ──────────
    [Fact]
    public void Dispatchers_MatchDirectVNCalls()
    {
        double r1 = 0.40, g1 = 0.40, b1 = 0.40;
        double r2 = 0.40, g2 = 0.40, b2 = 0.40;
        BasicTone.ApplyShadows(ref r1, ref g1, ref b1, 80.0, -2.5);
        BasicTone.ApplyShadowsV3(ref r2, ref g2, ref b2, 80.0, -2.5);
        Assert.Equal(r2, r1, 12);
    }

    // ── 13. EdgeAwareLuma: uniform input ⇒ uniform EV output ───────────────
    [Fact]
    public void EdgeAwareLuma_UniformInput_ReturnsExpectedEv()
    {
        const int w = 64, h = 64;
        var yLinear = new float[w * h];
        System.Array.Fill(yLinear, 0.5f);
        var po = new System.Threading.Tasks.ParallelOptions();
        float[] ev = EdgeAwareLuma.BuildEvBase(yLinear, w, h, po);

        Assert.Equal(w * h, ev.Length);
        double expected = System.Math.Log2(0.5 / BasicTone.MiddleGray);
        for (int i = 0; i < ev.Length; i++)
            Assert.Equal(expected, ev[i], 3);
    }

    // ── 14. EdgeAwareLuma: deep interior of a step image keeps its value ───
    // The whole point of the guided filter (vs a plain box blur) is that flat
    // interior regions don't get pulled toward the global mean by nearby edges.
    [Fact]
    public void EdgeAwareLuma_StepImage_PreservesFlatInteriors()
    {
        const int w = 128, h = 128;
        var yLinear = new float[w * h];
        for (int yy = 0; yy < h; yy++)
            for (int x = 0; x < w; x++)
                yLinear[yy * w + x] = x < w / 2 ? 0.05f : 0.80f;

        var po = new System.Threading.Tasks.ParallelOptions();
        float[] ev = EdgeAwareLuma.BuildEvBase(yLinear, w, h, po);

        double leftExpected  = System.Math.Log2(0.05 / BasicTone.MiddleGray);
        double rightExpected = System.Math.Log2(0.80 / BasicTone.MiddleGray);

        // Deep into each region (well beyond the filter radius) the value
        // should be within ~0.3 EV of the expected log2 ratio.
        Assert.Equal(leftExpected,  ev[64 * w +  4], 0);
        Assert.Equal(rightExpected, ev[64 * w + 123], 0);
    }
}
