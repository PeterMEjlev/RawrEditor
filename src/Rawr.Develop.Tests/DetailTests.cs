using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins Detail: the defaults must reproduce the pipeline's previous behaviour
/// exactly (Colour NR replaced a hard-coded blur, so "off" is not the same as
/// "default"), sharpening must actually raise acutance without running away, the
/// Masking and Detail weights must do what their names say, and luminance NR must
/// remove noise while leaving real edges standing.
/// </summary>
public class DetailTests
{
    private static ParallelOptions Po => new();

    /// <summary>A flat mid-grey field with a single vertical step down the middle —
    /// the classic acutance target.</summary>
    private static float[] EdgeImage(int w, int h, float lo = 100f, float hi = 160f)
    {
        var p = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                p[y * w + x] = x < w / 2 ? lo : hi;
        return p;
    }

    /// <summary>Flat field plus a deterministic ± dither — "noise" with no structure.</summary>
    private static float[] NoisyFlat(int w, int h, float level = 6f, float base_ = 128f)
    {
        var p = new float[w * h];
        uint rng = 12345u;
        for (int i = 0; i < p.Length; i++)
        {
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            float u = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
            p[i] = base_ + u * 2f * level;
        }
        return p;
    }

    private static float StdDev(float[] p)
    {
        double mean = 0.0;
        foreach (float v in p) mean += v;
        mean /= p.Length;
        double sum = 0.0;
        foreach (float v in p) sum += (v - mean) * (v - mean);
        return (float)Math.Sqrt(sum / p.Length);
    }

    // ── 1. Defaults reproduce the old pipeline exactly ─────────────────────
    // Colour NR replaced an unconditional radius-2 box blur. If the default
    // strength no longer maps to radius 2, every existing render shifts and the
    // Lightroom-match calibration drifts with it.
    [Fact]
    public void ColorNoiseReduction_DefaultIsTheOldHardCodedRadius()
    {
        Assert.Equal(2, Detail.ColorRadius(Detail.DefaultColorNoiseReduction));
    }

    [Fact]
    public void ColorNoiseReduction_ZeroIsAnExactNoOp()
    {
        var cb = NoisyFlat(32, 32);
        var cr = NoisyFlat(32, 32, 4f, 0f);
        var cb0 = (float[])cb.Clone();
        var cr0 = (float[])cr.Clone();

        Detail.ReduceColorNoise(cb, cr, 32, 32, 0.0, Po);

        Assert.Equal(cb0, cb);
        Assert.Equal(cr0, cr);
    }

    [Fact]
    public void ColorNoiseReduction_RadiusRisesWithTheSlider()
    {
        Assert.Equal(0, Detail.ColorRadius(0));
        Assert.Equal(2, Detail.ColorRadius(25));
        Assert.Equal(4, Detail.ColorRadius(50));
        Assert.Equal(8, Detail.ColorRadius(100));

        int prev = -1;
        for (double v = 0; v <= 100; v += 5)
        {
            int r = Detail.ColorRadius(v);
            Assert.True(r >= prev, $"radius must not fall; dropped at {v}");
            prev = r;
        }
    }

    [Fact]
    public void DevelopSettings_DefaultIsNeutral_WithDetailInPlace()
    {
        var s = new DevelopSettings();
        Assert.True(s.IsNeutral);
        Assert.Equal(0, s.Sharpening);
        Assert.Equal(0, s.LuminanceNoiseReduction);
        Assert.Equal(Detail.DefaultColorNoiseReduction, s.ColorNoiseReduction);

        // Off is not default: turning colour NR off is a real edit.
        s.ColorNoiseReduction = 0;
        Assert.False(s.IsNeutral);

        s.Reset();
        Assert.True(s.IsNeutral);
    }

    // ── 2. Sharpening is a no-op at 0, and only at 0 ───────────────────────
    [Fact]
    public void Sharpen_ZeroAmount_IsExactNoOp()
    {
        var p = EdgeImage(64, 64);
        var before = (float[])p.Clone();

        var sp = Detail.BuildSharpen(0, 1.0, 25, 0);
        Assert.False(sp.IsActive);
        Detail.Sharpen(p, 64, 64, sp, Po);

        Assert.Equal(before, p);
    }

    [Fact]
    public void Sharpen_RaisesAcutanceAtTheEdge()
    {
        const int w = 64, h = 16;
        var p = EdgeImage(w, h);
        var before = (float[])p.Clone();

        Detail.Sharpen(p, w, h, Detail.BuildSharpen(100, 1.0, 100, 0), Po);

        // The step gains over/undershoot: darker just left of the edge, brighter
        // just right of it. That overshoot *is* acutance.
        int mid = w / 2;
        int row = (h / 2) * w;
        Assert.True(p[row + mid - 1] < before[row + mid - 1], "should undershoot before the edge");
        Assert.True(p[row + mid] > before[row + mid], "should overshoot after the edge");
    }

    [Fact]
    public void Sharpen_LeavesAPerfectlyFlatFieldAlone()
    {
        // No highpass anywhere ⇒ nothing to amplify, at any setting.
        const int w = 32, h = 32;
        var p = new float[w * h];
        Array.Fill(p, 120f);

        Detail.Sharpen(p, w, h, Detail.BuildSharpen(150, 3.0, 100, 0), Po);

        foreach (float v in p) Assert.Equal(120f, v, 3);
    }

    [Fact]
    public void Sharpen_AmountScalesTheEffect()
    {
        const int w = 64, h = 16;
        int probe = (h / 2) * w + w / 2;
        float base_ = EdgeImage(w, h)[probe];

        float Overshoot(double amount)
        {
            var p = EdgeImage(w, h);
            Detail.Sharpen(p, w, h, Detail.BuildSharpen(amount, 1.0, 100, 0), Po);
            return p[probe] - base_;
        }

        float low = Overshoot(25);
        float high = Overshoot(100);
        Assert.True(high > low, $"more amount must sharpen harder ({high:F2} vs {low:F2})");
    }

    // ── 3. Detail suppresses haloes; Masking protects flat areas ───────────
    [Fact]
    public void SharpenDetail_Low_SuppressesTheLargeEdgeMoreThanHigh()
    {
        // That is the whole point of the slider: at 0 it sharpens texture and
        // leaves big tonal steps (and their haloes) alone.
        const int w = 64, h = 16;
        int probe = (h / 2) * w + w / 2;
        float base_ = EdgeImage(w, h)[probe];

        float Overshoot(double detail)
        {
            var p = EdgeImage(w, h);
            Detail.Sharpen(p, w, h, Detail.BuildSharpen(100, 1.0, detail, 0), Po);
            return p[probe] - base_;
        }

        Assert.True(Overshoot(0) < Overshoot(100),
            "low Detail must halo the strong edge less than high Detail");
    }

    [Fact]
    public void SharpenMasking_HoldsBackFlatNoise_ButKeepsTheEdge()
    {
        const int w = 64, h = 64;

        // Noise in a flat field: masking should progressively refuse to sharpen it.
        float NoiseGain(double masking)
        {
            var p = NoisyFlat(w, h);
            float before = StdDev(p);
            Detail.Sharpen(p, w, h, Detail.BuildSharpen(150, 1.0, 100, masking), Po);
            return StdDev(p) / before;
        }

        float unmasked = NoiseGain(0);
        float masked = NoiseGain(100);
        Assert.True(unmasked > masked,
            $"masking must amplify flat-field noise less ({masked:F3} vs {unmasked:F3})");

        // But a real edge must still get sharpened at full masking.
        var edge = EdgeImage(w, 16);
        int probe = 8 * w + w / 2;
        float baseline = edge[probe];
        Detail.Sharpen(edge, w, 16, Detail.BuildSharpen(100, 1.0, 100, 100), Po);
        Assert.True(edge[probe] > baseline, "a genuine edge must survive full masking");
    }

    // ── 4. Luminance noise reduction ───────────────────────────────────────
    [Fact]
    public void LuminanceNr_ZeroAmount_IsExactNoOp()
    {
        var p = NoisyFlat(48, 48);
        var before = (float[])p.Clone();

        var lp = Detail.BuildLuminance(0, 50, 0);
        Assert.False(lp.IsActive);
        Detail.ReduceLuminanceNoise(p, 48, 48, lp, Po);

        Assert.Equal(before, p);
    }

    [Fact]
    public void LuminanceNr_ReducesNoise_AndMoreAtHigherStrength()
    {
        const int w = 48, h = 48;

        float Residual(double amount)
        {
            var p = NoisyFlat(w, h);
            Detail.ReduceLuminanceNoise(p, w, h, Detail.BuildLuminance(amount, 50, 0), Po);
            return StdDev(p);
        }

        float none = StdDev(NoisyFlat(w, h));
        float half = Residual(50);
        float full = Residual(100);

        Assert.True(half < none, $"NR must reduce noise ({half:F3} vs {none:F3})");
        Assert.True(full < half, $"more strength must smooth further ({full:F3} vs {half:F3})");
    }

    [Fact]
    public void LuminanceNr_PreservesARealEdge()
    {
        // The guided filter is the whole reason to use one here: heavy smoothing
        // in the flat halves, but the step itself must stay a step.
        const int w = 64, h = 32;
        var p = EdgeImage(w, h, 80f, 180f);

        Detail.ReduceLuminanceNoise(p, w, h, Detail.BuildLuminance(100, 50, 0), Po);

        int row = (h / 2) * w;
        float left = p[row + 4];
        float right = p[row + w - 5];
        Assert.Equal(80f, left, 0);
        Assert.Equal(180f, right, 0);
        Assert.True(right - left > 95f, $"the edge must survive; got {right - left:F1} of 100");
    }

    [Fact]
    public void LuminanceNrDetail_HigherPreservesMore()
    {
        const int w = 48, h = 48;

        float Residual(double detail)
        {
            var p = NoisyFlat(w, h);
            Detail.ReduceLuminanceNoise(p, w, h, Detail.BuildLuminance(100, detail, 0), Po);
            return StdDev(p);
        }

        Assert.True(Residual(100) > Residual(0),
            "high Detail must leave more of the fine structure standing");
    }

    [Fact]
    public void LuminanceNrContrast_HandsDetailBackInTexturedRegions()
    {
        const int w = 48, h = 48;

        float Residual(double contrast)
        {
            var p = NoisyFlat(w, h, 20f);   // high-amplitude ⇒ genuinely textured
            Detail.ReduceLuminanceNoise(p, w, h, Detail.BuildLuminance(100, 50, contrast), Po);
            return StdDev(p);
        }

        Assert.True(Residual(100) > Residual(0),
            "Contrast must retain more local structure than it does at 0");
    }

    // ── 5. The mask overlay must describe the real sharpening ──────────────
    [Fact]
    public void RenderMask_IsWhiteEverywhere_WhenMaskingIsOff()
    {
        // Masking 0 protects nothing, so the honest picture is a white frame.
        var p = EdgeImage(48, 32);
        var mask = Detail.RenderMask(p, 48, 32, Detail.BuildSharpen(100, 1.0, 25, 0), Po);

        foreach (float m in mask) Assert.Equal(1f, m, 4);
    }

    [Fact]
    public void RenderMask_IsWhiteOnEdges_AndBlackOnFlatGround()
    {
        const int w = 64, h = 32;
        var p = EdgeImage(w, h, 60f, 200f);
        var mask = Detail.RenderMask(p, w, h, Detail.BuildSharpen(100, 1.0, 25, 100), Po);

        int row = (h / 2) * w;
        Assert.Equal(0f, mask[row + 4], 3);          // flat left
        Assert.Equal(0f, mask[row + w - 5], 3);      // flat right
        Assert.True(mask[row + w / 2] > 0.9f,
            $"the edge should read as white; got {mask[row + w / 2]:F3}");
    }

    [Fact]
    public void RenderMask_StaysInRange()
    {
        var p = NoisyFlat(48, 48, 40f);
        foreach (double masking in new[] { 0.0, 30.0, 70.0, 100.0 })
        {
            var mask = Detail.RenderMask(p, 48, 48, Detail.BuildSharpen(100, 1.0, 25, masking), Po);
            foreach (float m in mask) Assert.InRange(m, 0f, 1f);
        }
    }

    [Fact]
    public void RenderMask_AgreesWithWhatSharpeningActuallyDid()
    {
        // The point of the overlay. Where the mask reads black, sharpening must
        // have left the pixel alone; where it reads white, the pixel must have
        // moved by the full unmasked amount. If these two ever drift apart the
        // overlay is telling the user something untrue.
        const int w = 64, h = 32;
        var src = EdgeImage(w, h, 60f, 200f);

        var masked = (float[])src.Clone();
        Detail.Sharpen(masked, w, h, Detail.BuildSharpen(100, 1.0, 100, 100), Po);

        var unmasked = (float[])src.Clone();
        Detail.Sharpen(unmasked, w, h, Detail.BuildSharpen(100, 1.0, 100, 0), Po);

        var mask = Detail.RenderMask(src, w, h, Detail.BuildSharpen(100, 1.0, 100, 100), Po);

        for (int i = 0; i < w * h; i++)
        {
            // masked = src + mask·(unmasked − src), by construction.
            float expected = src[i] + mask[i] * (unmasked[i] - src[i]);
            Assert.Equal(expected, masked[i], 2);
        }
    }

    [Fact]
    public void RenderMask_TracksRadius_NotJustMasking()
    {
        // Radius feeds the blur the gradient is measured on, so the overlay has to
        // move with it too — otherwise it would misdescribe the mask at any radius
        // other than the one it was drawn for.
        const int w = 64, h = 32;
        var p = EdgeImage(w, h, 60f, 200f);

        float Coverage(double radius)
        {
            var m = Detail.RenderMask(p, w, h, Detail.BuildSharpen(100, radius, 25, 100), Po);
            float sum = 0f;
            foreach (float v in m) sum += v;
            return sum / m.Length;
        }

        // A wider blur spreads the step over more pixels, so more of the frame
        // registers as "edge".
        Assert.True(Coverage(3.0) > Coverage(0.5),
            "a larger radius must widen the masked-in region");
    }

    // ── 6. Parameter plumbing ──────────────────────────────────────────────
    [Fact]
    public void BuildSharpen_ClampsToLightroomsRanges()
    {
        var over = Detail.BuildSharpen(500, 99, 500, 500);
        Assert.Equal(1.5f, over.Amount, 4);      // 150 is the panel maximum
        Assert.Equal(3.0, over.Radius, 4);

        var under = Detail.BuildSharpen(-50, 0.01, -20, -20);
        Assert.False(under.IsActive);
        Assert.Equal(0.5, under.Radius, 4);
    }

    [Fact]
    public void Clone_CarriesDetailSettings()
    {
        var s = new DevelopSettings { Sharpening = 60, LuminanceNoiseReduction = 30 };
        var snap = s.Clone();
        s.Sharpening = 0;

        Assert.Equal(60, snap.Sharpening);
        Assert.Equal(30, snap.LuminanceNoiseReduction);
    }
}
