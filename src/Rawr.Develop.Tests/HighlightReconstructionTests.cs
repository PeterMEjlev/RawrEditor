using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// Verification of <see cref="HighlightReconstruction"/> — the stage that gives the
/// Highlights slider something to recover.
///
/// <para>The tests are built around the case that actually occurs in a RAW file: one
/// channel runs into the sensor clip while the others still hold honest data. The
/// reconstruction has to infer the lost channel from a surviving one and the local
/// hue, producing a value <i>above</i> the clip — that headroom is the raw material
/// highlight recovery works with, and without it the tone stage can only ever
/// darken a flat plateau.</para>
/// </summary>
public class HighlightReconstructionTests
{
    private const double Clip = 1.0;

    private static (float[] r, float[] g, float[] b) Fill(int n, double r, double g, double b)
    {
        var rr = new float[n]; var gg = new float[n]; var bb = new float[n];
        System.Array.Fill(rr, (float)r);
        System.Array.Fill(gg, (float)g);
        System.Array.Fill(bb, (float)b);
        return (rr, gg, bb);
    }

    private static bool Run(float[] r, float[] g, float[] b, int w, int h)
        => HighlightReconstruction.Apply(r, g, b, w, h, Clip, Clip, Clip);

    // ── 1. Nothing clipped ⇒ exact no-op ───────────────────────────────────
    [Fact]
    public void NoClipping_IsExactNoOp()
    {
        const int w = 48, h = 48, n = w * h;
        var (r, g, b) = Fill(n, 0.50, 0.30, 0.20);
        for (int i = 0; i < n; i++) { r[i] += i % 5 * 0.01f; g[i] += i % 3 * 0.01f; }
        var r0 = (float[])r.Clone(); var g0 = (float[])g.Clone(); var b0 = (float[])b.Clone();

        Assert.False(Run(r, g, b, w, h));

        for (int i = 0; i < n; i++)
        {
            Assert.Equal(r0[i], r[i]);
            Assert.Equal(g0[i], g[i]);
            Assert.Equal(b0[i], b[i]);
        }
    }

    // ── 2. A single clipped channel is rebuilt from the local hue ratio ────
    // The background fixes the local hue at r:g = 2:1. Inside the patch red has
    // clipped at 1.0 while green still reads 0.8, so the truth red was carrying is
    // 0.8 x 2 = 1.6 — well above the clip, and exactly what recovery needs.
    [Fact]
    public void ClippedChannel_IsRebuiltAboveTheClip_FromLocalHue()
    {
        const int w = 64, h = 64, n = w * h;
        var (r, g, b) = Fill(n, 0.50, 0.25, 0.125);   // background, r:g = 2:1

        const int x0 = 28, x1 = 36, y0 = 28, y1 = 36;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = y * w + x;
                r[i] = 1.00f;   // clipped
                g[i] = 0.80f;   // still valid
                b[i] = 0.40f;   // still valid
            }

        Assert.True(Run(r, g, b, w, h));

        int c = 32 * w + 32;   // patch centre
        Assert.True(r[c] > 1.0f, $"clipped red must be rebuilt above the clip, got {r[c]:F4}");
        Assert.InRange(r[c], 1.45f, 1.75f);          // ≈ 0.8 x (0.50/0.25)

        // The surviving channels are untouched, and the background is untouched.
        Assert.Equal(0.80f, g[c], 4);
        Assert.Equal(0.40f, b[c], 4);
        Assert.Equal(0.50f, r[2 * w + 2], 4);
    }

    // ── 3. A fully clipped core is filled from the reconstructed rim ───────
    // Where every channel clipped there is no per-pixel information left, so the
    // core is interpolated from the rim that *was* reconstructable. Because that rim
    // now sits above the clip, the core comes back as a smooth dome rather than the
    // flat disc a hard clip leaves behind.
    [Fact]
    public void FullyClippedCore_IsFilledFromTheRim()
    {
        const int w = 96, h = 96, n = w * h;
        var (r, g, b) = Fill(n, 0.50, 0.25, 0.125);
        const int cx = 48, cy = 48;

        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double d = System.Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                int i = y * w + x;
                if (d <= 8)            // core: everything clipped, no information left
                {
                    r[i] = 1.00f; g[i] = 1.00f; b[i] = 1.00f;
                }
                else if (d <= 24)      // rim: red clipped, green/blue still valid
                {
                    r[i] = 1.00f; g[i] = 0.80f; b[i] = 0.40f;
                }
            }

        Assert.True(Run(r, g, b, w, h));

        int c = cy * w + cx;
        Assert.True(r[c] > 1.0f, $"blown core should be filled above the clip, got {r[c]:F4}");
        Assert.True(g[c] >= 1.0f, $"core green must never be darkened below the clip, got {g[c]:F4}");
    }

    // ── 4. Reconstruction never darkens ────────────────────────────────────
    // A clipped channel's true value is at or above the clip, never below it, so the
    // operator is only ever allowed to push a sample up.
    [Fact]
    public void NeverDarkensAnySample()
    {
        const int w = 64, h = 48, n = w * h;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        var rng = new System.Random(11);
        for (int i = 0; i < n; i++)
        {
            r[i] = (float)System.Math.Min(1.0, rng.NextDouble() * 1.4);
            g[i] = (float)System.Math.Min(1.0, rng.NextDouble() * 1.4);
            b[i] = (float)System.Math.Min(1.0, rng.NextDouble() * 1.4);
        }
        var r0 = (float[])r.Clone(); var g0 = (float[])g.Clone(); var b0 = (float[])b.Clone();

        Run(r, g, b, w, h);

        for (int i = 0; i < n; i++)
        {
            Assert.True(r[i] >= r0[i] - 1e-5f, $"r[{i}] darkened: {r0[i]:F4} -> {r[i]:F4}");
            Assert.True(g[i] >= g0[i] - 1e-5f, $"g[{i}] darkened: {g0[i]:F4} -> {g[i]:F4}");
            Assert.True(b[i] >= b0[i] - 1e-5f, $"b[{i}] darkened: {b0[i]:F4} -> {b[i]:F4}");
        }
    }

    // ── 5. Reconstruction is capped, and produces no NaNs or infinities ────
    // A wild hue ratio in a strongly coloured region must not run away; the cap is
    // MaxHeadroomStops above the clip.
    [Fact]
    public void RespectsHeadroomCap_AndStaysFinite()
    {
        const int w = 64, h = 64, n = w * h;
        // A background with an extreme colour cast, so the r:g ratio is large.
        var (r, g, b) = Fill(n, 0.90, 0.02, 0.01);
        for (int y = 24; y < 40; y++)
            for (int x = 24; x < 40; x++)
            {
                int i = y * w + x;
                r[i] = 1.00f; g[i] = 0.95f; b[i] = 0.90f;
            }

        var opt = new HighlightReconstruction.Options { MaxHeadroomStops = 2.0 };
        HighlightReconstruction.Apply(r, g, b, w, h, Clip, Clip, Clip, opt);

        float cap = (float)(Clip * System.Math.Pow(2.0, 2.0));
        for (int i = 0; i < n; i++)
        {
            Assert.False(float.IsNaN(r[i]) || float.IsInfinity(r[i]), $"r[{i}]={r[i]}");
            Assert.False(float.IsNaN(g[i]) || float.IsInfinity(g[i]), $"g[{i}]={g[i]}");
            Assert.False(float.IsNaN(b[i]) || float.IsInfinity(b[i]), $"b[{i}]={b[i]}");
            Assert.True(r[i] <= cap + 1e-4f, $"r[{i}]={r[i]} exceeded the headroom cap {cap}");
        }
    }

    // ── 6. Per-channel clip levels are honoured ────────────────────────────
    // White balance and exposure are folded into the planes before this runs, so the
    // sensor clip lands at each channel's own gain. Passing 1.0 for every channel
    // would look for clipping in the wrong place on every channel but the reference.
    [Fact]
    public void UsesPerChannelClipLevels()
    {
        const int w = 48, h = 48, n = w * h;
        // Red plane gained by 2.0, so red's clip sits at 2.0, not 1.0.
        var (r, g, b) = Fill(n, 1.00, 0.25, 0.125);
        for (int y = 20; y < 28; y++)
            for (int x = 20; x < 28; x++)
            {
                int i = y * w + x;
                r[i] = 2.00f;   // at red's clip
                g[i] = 0.80f;
                b[i] = 0.40f;
            }

        bool changed = HighlightReconstruction.Apply(r, g, b, w, h, 2.0, 1.0, 1.0);

        Assert.True(changed);
        int c = 24 * w + 24;
        Assert.True(r[c] > 2.0f, $"red clipped at its own level should be rebuilt, got {r[c]:F4}");
        // The background red of 1.00 is only half of red's clip, so it is fully
        // trusted and must not move.
        Assert.Equal(1.00f, r[2 * w + 2], 4);
    }
}
