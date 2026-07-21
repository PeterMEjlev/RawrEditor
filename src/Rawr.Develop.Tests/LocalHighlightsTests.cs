using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Verification of the spatial, edge-aware <see cref="LocalHighlights"/> operator.
///
/// <para>Uniform images isolate the tone curve from the base/detail split (a flat
/// image's guided-filter base equals its input, so the reconstruction is clean and
/// the expected value can be worked out in closed form); ramps and textured fields
/// check the two properties that actually decide whether highlight recovery looks
/// like Lightroom's — that above-white content lands below white, and that it keeps
/// its structure on the way down.</para>
/// </summary>
public class LocalHighlightsTests
{
    // Build a uniform grey plane set at linear value v.
    private static (float[] r, float[] g, float[] b) Uniform(int n, double v)
    {
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        System.Array.Fill(r, (float)v);
        System.Array.Fill(g, (float)v);
        System.Array.Fill(b, (float)v);
        return (r, g, b);
    }

    private static double Lum(float r, float g, float b) => BasicTone.Luminance(r, g, b);

    // ── 1. Highlights = 0 returns the image unchanged ──────────────────────
    [Fact]
    public void Zero_IsExactNoOp()
    {
        const int w = 32, h = 32, n = w * h;
        var (r, g, b) = Uniform(n, 0.5);
        // give it some colour/texture so "unchanged" is meaningful
        for (int i = 0; i < n; i++) { r[i] = 0.3f + i % 7 * 0.01f; g[i] = 0.6f; b[i] = 0.9f - i % 5 * 0.02f; }
        var r0 = (float[])r.Clone(); var g0 = (float[])g.Clone(); var b0 = (float[])b.Clone();

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = 0 });

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(r0[i], r[i]);
            Assert.Equal(g0[i], g[i]);
            Assert.Equal(b0[i], b[i]);
        }
    }

    // ── 2. Negative highlights darkens bright regions more than midtones ────
    [Fact]
    public void Negative_DarkensBrightMoreThanMid()
    {
        const int w = 24, h = 24, n = w * h;
        var opt = new LocalHighlights.Options { Highlights = -100, Radius = 4 };

        var bright = Uniform(n, 0.95);
        LocalHighlights.Apply(bright.r, bright.g, bright.b, w, h, opt);
        double brightDrop = 0.95 - Lum(bright.r[n / 2], bright.g[n / 2], bright.b[n / 2]);

        var mid = Uniform(n, BasicTone.MiddleGray);
        LocalHighlights.Apply(mid.r, mid.g, mid.b, w, h, opt);
        double midDrop = BasicTone.MiddleGray - Lum(mid.r[n / 2], mid.g[n / 2], mid.b[n / 2]);

        Assert.True(brightDrop > 0.05, $"bright region should be pulled down, drop {brightDrop:F4}");
        Assert.True(midDrop < brightDrop * 0.1, $"midtone barely moves, mid {midDrop:F5} vs bright {brightDrop:F4}");
    }

    // ── 3. Shadows are essentially unchanged ───────────────────────────────
    [Fact]
    public void Shadows_AreUnchanged()
    {
        const int w = 24, h = 24, n = w * h;
        var (r, g, b) = Uniform(n, 0.03);
        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -100, Radius = 4 });

        // Below the knee the soft knee is exactly the identity and detailGain is
        // exactly 1, so the log round-trip returns the input.
        Assert.Equal(0.03, Lum(r[n / 2], g[n / 2], b[n / 2]), 6);
    }

    // ── 4. RGB ratios are preserved for nonzero pixels ─────────────────────
    [Fact]
    public void Preserves_RgbRatios()
    {
        const int w = 24, h = 24, n = w * h;
        // a bright, strongly coloured uniform patch (well inside the highlight band)
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        System.Array.Fill(r, 1.60f); System.Array.Fill(g, 1.10f); System.Array.Fill(b, 0.60f);

        double rgBefore = r[0] / g[0], bgBefore = b[0] / g[0];

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -80, Radius = 4 });

        int i = n / 2;
        Assert.True(g[i] < 1.10f, "expected a luminance change in the bright patch");
        Assert.Equal(rgBefore, r[i] / g[i], 5);
        Assert.Equal(bgBefore, b[i] / g[i], 5);
    }

    // ── 5. No NaNs or infinities, across an extreme image and both signs ────
    [Theory]
    [InlineData(-100)]
    [InlineData(-50)]
    [InlineData(50)]
    [InlineData(100)]
    public void Produces_NoNaNsOrInfs(double highlights)
    {
        const int w = 40, h = 40, n = w * h;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        var rng = new System.Random(1);
        for (int i = 0; i < n; i++)
        {
            // mix of pure black, normal, and HDR scene-linear values
            double v = (i % 11 == 0) ? 0.0 : rng.NextDouble() * 4.0;
            r[i] = (float)(v * rng.NextDouble());
            g[i] = (float)v;
            b[i] = (float)(v * (0.5 + rng.NextDouble()));
        }

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = highlights });

        for (int i = 0; i < n; i++)
        {
            Assert.False(float.IsNaN(r[i]) || float.IsInfinity(r[i]), $"r[{i}]={r[i]}");
            Assert.False(float.IsNaN(g[i]) || float.IsInfinity(g[i]), $"g[{i}]={g[i]}");
            Assert.False(float.IsNaN(b[i]) || float.IsInfinity(b[i]), $"b[{i}]={b[i]}");
        }
    }

    // ── 6. Positive highlights brightens the bright band ───────────────────
    [Fact]
    public void Positive_BrightensHighlights()
    {
        const int w = 24, h = 24, n = w * h;
        var (r, g, b) = Uniform(n, 0.85);
        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = 100, Radius = 4 });
        Assert.True(Lum(r[n / 2], g[n / 2], b[n / 2]) > 0.85,
            $"positive highlights should brighten, got {Lum(r[n / 2], g[n / 2], b[n / 2]):F4}");
    }

    // ── 7. Edge-aware: a bright/dark step keeps flat interiors clean ───────
    // The dark side sits below the knee so its deep interior must be exact; the
    // bright side is recovered. A Gaussian base would bleed the edge and halo —
    // the guided filter must not.
    [Fact]
    public void StepImage_RecoversBright_LeavesDarkInteriorExact()
    {
        const int w = 128, h = 64, n = w * h;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        for (int yy = 0; yy < h; yy++)
            for (int x = 0; x < w; x++)
            {
                float v = x < w / 2 ? 0.04f : 1.20f; // dark | bright HDR step
                int i = yy * w + x;
                r[i] = v; g[i] = v; b[i] = v;
            }

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -100, Radius = 8 });

        int darkInterior = (h / 2) * w + 4;     // deep in the dark half
        int brightInterior = (h / 2) * w + w - 5; // deep in the bright half

        Assert.Equal(0.04, Lum(r[darkInterior], g[darkInterior], b[darkInterior]), 4);
        Assert.True(Lum(r[brightInterior], g[brightInterior], b[brightInterior]) < 1.0,
            $"bright interior should be recovered below the clip, got {Lum(r[brightInterior], g[brightInterior], b[brightInterior]):F4}");
    }

    // ═══════════════════ The recovery behaviour that matters ═══════════════

    /// <summary>
    /// What the pipeline's output transform would render a linear value as, in 8-bit.
    /// The recovery assertions are written against this rather than against linear
    /// values, because this pipeline's transform is heavily lifted — it renders middle
    /// grey at ~190/255 and squeezes everything above +2 EV into the last few code
    /// values — so "below linear 1.0" says nothing about what a viewer sees.
    /// </summary>
    private static double Display8(double linear)
        => BasicTone.LightroomMatch(BasicTone.DisplayCurve(linear)) * 255.0;

    // ── 8. Above-white content is brought back into the visible range ───────
    // The headline requirement. Anything left at or above white renders as flat 255
    // and is indistinguishable from its neighbours, so recovery has to land it far
    // enough down the output range to be seen. This is the case a fixed offset (the
    // previous implementation's one-stop pull) could never reach.
    [Theory]
    [InlineData(1.4)]
    [InlineData(2.0)]   // 1 stop above white
    [InlineData(4.0)]   // 2 stops above white
    public void FullRecovery_BringsHeadroomIntoTheVisibleRange(double linear)
    {
        const int w = 32, h = 32, n = w * h;
        var (r, g, b) = Uniform(n, linear);

        // Everything at or above white renders identically — pinned, since it is the
        // reason recovery is needed at all.
        Assert.Equal(255.0, Display8(linear), 3);

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -100, Radius = 4 });

        double after = Display8(Lum(r[n / 2], g[n / 2], b[n / 2]));
        Assert.True(after < 235.0, $"{linear:F1} linear should recover well clear of white, renders {after:F1}/255");
        Assert.True(after > 195.0, $"...but not crush to grey mush, renders {after:F1}/255");
    }

    // ── 9. Recovery is order-preserving across the highlight range ──────────
    // Compression is only useful if it is monotonic: brighter input must stay
    // brighter output, otherwise a gradient inverts and detail turns to mud.
    [Fact]
    public void Recovery_PreservesOrderingAcrossARamp()
    {
        const int w = 128, h = 32, n = w * h;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        for (int yy = 0; yy < h; yy++)
            for (int x = 0; x < w; x++)
            {
                float v = (float)(0.6 + 2.9 * x / (w - 1.0)); // 0.6 → 3.5 linear
                int i = yy * w + x;
                r[i] = v; g[i] = v; b[i] = v;
            }

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -100, Radius = 8 });

        int row = (h / 2) * w;
        double lo = Lum(r[row + 30], g[row + 30], b[row + 30]);
        double mid = Lum(r[row + 64], g[row + 64], b[row + 64]);
        double hi = Lum(r[row + 100], g[row + 100], b[row + 100]);

        Assert.True(lo < mid && mid < hi,
            $"ramp must stay ordered after compression: {lo:F4} < {mid:F4} < {hi:F4}");
        Assert.True(hi < 1.0, $"the top of the ramp should land below white, got {hi:F4}");
    }

    // ── 10. Recovered highlights keep their detail ──────────────────────────
    // The whole point of splitting base from detail. The base is compressed hard;
    // the detail layer rides through at full amplitude (slightly boosted), so a
    // textured blown region comes back as texture rather than as a flat patch.
    [Fact]
    public void Recovery_KeepsDetailInTexturedHighlights()
    {
        const int w = 128, h = 128, n = w * h;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        for (int yy = 0; yy < h; yy++)
            for (int x = 0; x < w; x++)
            {
                // 1.8 linear (well above white) with a fine ±0.15 texture
                float v = 1.8f + ((x / 2) % 2 == 0 ? 0.15f : -0.15f);
                int i = yy * w + x;
                r[i] = v; g[i] = v; b[i] = v;
            }

        double inMin = 1.65, inMax = 1.95;
        double inRelative = (inMax - inMin) / 1.8;

        LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -100 });

        // Sample well inside the image so the box-blur edge clamp is not in play.
        double outMin = double.MaxValue, outMax = double.MinValue, sum = 0;
        int count = 0;
        for (int yy = 48; yy < 80; yy++)
            for (int x = 48; x < 80; x++)
            {
                double v = Lum(r[yy * w + x], g[yy * w + x], b[yy * w + x]);
                if (v < outMin) outMin = v;
                if (v > outMax) outMax = v;
                sum += v; count++;
            }
        double mean = sum / count;

        Assert.True(mean < 1.0, $"the blown field should be recovered below white, mean {mean:F4}");

        double outRelative = (outMax - outMin) / mean;
        Assert.True(outRelative > inRelative * 0.9,
            $"detail must survive the compression: relative contrast {inRelative:F4} -> {outRelative:F4}");
    }

    // ── 11. The soft knee joins the identity smoothly ──────────────────────
    // A slope discontinuity at the knee would show up as a visible edge in a sky.
    // Value and first derivative both have to match at d = 0. Highlights, Whites
    // and the Exposure shoulder all share this curve, so this pins all three.
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(1.0)]
    [InlineData(2.2)]
    public void SoftKnee_IsC1AtTheJoin(double slope)
    {
        const double width = 0.9, eps = 1e-5;

        Assert.Equal(0.0, BasicTone.SoftKnee(0.0, slope, width), 10);

        // One-sided derivatives either side of the join must agree.
        double below = (BasicTone.SoftKnee(0.0, slope, width)
                      - BasicTone.SoftKnee(-eps, slope, width)) / eps;
        double above = (BasicTone.SoftKnee(eps, slope, width)
                      - BasicTone.SoftKnee(0.0, slope, width)) / eps;

        Assert.Equal(1.0, below, 4);
        Assert.Equal(1.0, above, 4);
    }

    // ── 12. The knee is monotonic and tends to the requested slope ─────────
    [Theory]
    [InlineData(0.05)]
    [InlineData(0.2)]
    [InlineData(2.2)]
    public void SoftKnee_IsMonotonicAndApproachesSlope(double slope)
    {
        const double width = 0.9;
        double prev = double.MinValue;
        for (double d = 0.0; d <= 8.0; d += 0.05)
        {
            double v = BasicTone.SoftKnee(d, slope, width);
            Assert.True(v > prev, $"soft knee must be strictly increasing at d={d:F2}");
            prev = v;
        }

        // Far from the knee the local slope should be close to the asymptote.
        double far = (BasicTone.SoftKnee(8.0, slope, width)
                    - BasicTone.SoftKnee(7.0, slope, width)) / 1.0;
        Assert.Equal(slope, far, 3);
    }
}
