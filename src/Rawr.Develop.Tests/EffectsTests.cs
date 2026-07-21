using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins Texture, Clarity, Dehaze and Grain: that each is a bit-exact no-op at
/// neutral (the baseline the tone LUT is calibrated against must not move), that
/// each does the thing its name promises, and — for Dehaze — that its one global
/// statistic behaves.
/// </summary>
public class EffectsTests
{
    private const int W = 192, H = 128;

    private static readonly ParallelOptions Po = new();

    /// <summary>A luminance plane with a soft ramp plus fine detail, so the
    /// base/detail split has both a base and a detail to find.</summary>
    private static float[] Luma()
    {
        var p = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                p[y * W + x] = 40f + 150f * (x / (float)W)
                             + ((x / 2 + y / 2) % 2 == 0 ? 6f : -6f);
        return p;
    }

    /// <summary>Population standard deviation — the measure of "how much
    /// contrast is there", which is what Texture and Clarity move.</summary>
    private static double StdDev(float[] p)
    {
        double mean = 0;
        foreach (float v in p) mean += v;
        mean /= p.Length;

        double acc = 0;
        foreach (float v in p) { double d = v - mean; acc += d * d; }
        return Math.Sqrt(acc / p.Length);
    }

    /// <summary>Mean absolute difference between neighbouring pixels — sensitive
    /// to fine detail specifically, where StdDev is dominated by the ramp.</summary>
    private static double LocalDetail(float[] p)
    {
        double acc = 0; int count = 0;
        for (int y = 0; y < H; y++)
            for (int x = 1; x < W; x++) { acc += Math.Abs(p[y * W + x] - p[y * W + x - 1]); count++; }
        return acc / count;
    }

    // ── Neutral is exactly nothing ─────────────────────────────────────────

    [Fact]
    public void Texture_AtZero_IsExactNoOp()
    {
        var a = Luma();
        var b = Luma();
        Effects.ApplyTexture(b, W, H, 0.0, W, H, Po);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Clarity_AtZero_IsExactNoOp()
    {
        var a = Luma();
        var b = Luma();
        Effects.ApplyClarity(b, W, H, 0.0, W, H, Po);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Dehaze_AtZero_IsExactNoOp()
    {
        var (r, g, b) = HazyScene();
        var (r2, g2, b2) = HazyScene();
        var air = Effects.EstimateAirlight(r, g, b, W, H).Gained(1, 1, 1);
        Effects.ApplyDehaze(r2, g2, b2, W, H, 0.0, air, W, H, Po);
        Assert.Equal(r, r2);
        Assert.Equal(g, g2);
        Assert.Equal(b, b2);
    }

    [Fact]
    public void Grain_AtZero_IsExactNoOp()
    {
        var (r, g, b) = HazyScene();
        var (r2, g2, b2) = HazyScene();
        var p = Effects.BuildGrain(0.0, 50.0, 50.0, W, H);
        Effects.ApplyGrain(r2, g2, b2, W, H, p, Po);
        Assert.Equal(r, r2);
        Assert.Equal(g, g2);
        Assert.Equal(b, b2);
    }

    // ── Airlight: cached + plane-free, still bit-identical ──────────────────

    /// <summary>The plane-free sensor estimate must agree with the float-plane
    /// estimate to the last bit — same dark-channel math, same summation order —
    /// so nothing downstream of Dehaze moves.</summary>
    [Fact]
    public void AirlightFromSensor_MatchesPlaneEstimate_BitExact()
    {
        var src = HazySensor();
        int n = W * H;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        const float inv = 1f / 65535f;
        for (int i = 0; i < n; i++)
        {
            r[i] = src[i * 3] * inv;
            g[i] = src[i * 3 + 1] * inv;
            b[i] = src[i * 3 + 2] * inv;
        }

        var expected = Effects.EstimateAirlight(r, g, b, W, H);
        var actual = Effects.EstimateAirlightFromSensor(src, W, H);

        Assert.True(actual.IsValid);
        Assert.Equal(expected.R, actual.R);
        Assert.Equal(expected.G, actual.G);
        Assert.Equal(expected.B, actual.B);
    }

    /// <summary>A second estimate on the same buffer is served from the weak-table
    /// cache and returns the identical value.</summary>
    [Fact]
    public void AirlightFromSensor_CachesPerBuffer()
    {
        var src = HazySensor();
        var first = Effects.EstimateAirlightFromSensor(src, W, H);
        var second = Effects.EstimateAirlightFromSensor(src, W, H);
        Assert.Equal(first.R, second.R);
        Assert.Equal(first.G, second.G);
        Assert.Equal(first.B, second.B);
    }

    /// <summary>A hazy interleaved 16-bit sensor frame — the blue-ish haze of
    /// <see cref="HazyScene"/> quantised to sensor units.</summary>
    private static ushort[] HazySensor()
    {
        var src = new ushort[W * H * 3];
        const float ar = 0.72f, ag = 0.75f, ab = 0.82f;
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float depth = x / (float)W;
                float t = 1f - 0.75f * depth;
                float scene = ((x / 3 + y / 3) % 2 == 0) ? 0.22f : 0.55f;
                src[i * 3]     = (ushort)((scene * t + ar * (1f - t)) * 65535f);
                src[i * 3 + 1] = (ushort)((scene * t + ag * (1f - t)) * 65535f);
                src[i * 3 + 2] = (ushort)((scene * t + ab * (1f - t)) * 65535f);
            }
        }
        return src;
    }

    // ── Texture ────────────────────────────────────────────────────────────

    [Fact]
    public void Texture_Positive_AddsFineDetail_Negative_RemovesIt()
    {
        double baseline = LocalDetail(Luma());

        var up = Luma();
        Effects.ApplyTexture(up, W, H, 80.0, W, H, Po);
        var down = Luma();
        Effects.ApplyTexture(down, W, H, -80.0, W, H, Po);

        Assert.True(LocalDetail(up) > baseline, "positive Texture must add fine detail");
        Assert.True(LocalDetail(down) < baseline, "negative Texture must smooth fine detail");
    }

    /// <summary>
    /// Texture works at a small radius, so it must leave the broad tonal
    /// structure alone — that is Clarity's job, and a Texture that moved the
    /// overall contrast would just be a second Clarity slider.
    /// </summary>
    [Fact]
    public void Texture_LeavesBroadContrastAlone()
    {
        var plain = Luma();
        var textured = Luma();
        Effects.ApplyTexture(textured, W, H, 100.0, W, H, Po);

        double before = StdDev(plain), after = StdDev(textured);
        Assert.True(Math.Abs(after - before) / before < 0.15,
            $"broad contrast moved too much: {before:F2} -> {after:F2}");
    }

    // ── Clarity ────────────────────────────────────────────────────────────

    [Fact]
    public void Clarity_Positive_AddsLocalContrast()
    {
        var plain = Luma();
        var clear = Luma();
        Effects.ApplyClarity(clear, W, H, 90.0, W, H, Po);
        Assert.True(StdDev(clear) > StdDev(plain), "positive Clarity must add contrast");
    }

    /// <summary>
    /// Clarity is midtone-weighted so it cannot crush blacks or blow highlights.
    /// A plane pinned at the extremes must come through essentially untouched
    /// however hard the slider is pushed.
    /// </summary>
    [Fact]
    public void Clarity_ProtectsTheExtremes()
    {
        var plane = new float[W * H];
        for (int i = 0; i < plane.Length; i++)
            plane[i] = (i % 2 == 0) ? 1f : 254f;   // only extremes, high local contrast

        var before = (float[])plane.Clone();
        Effects.ApplyClarity(plane, W, H, 100.0, W, H, Po);

        for (int i = 0; i < plane.Length; i++)
            Assert.True(Math.Abs(plane[i] - before[i]) < 6f,
                $"extreme tone moved by {Math.Abs(plane[i] - before[i]):F1} at {i}");
    }

    // ── Dehaze ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A scene whose contrast falls off with "distance" (x), composited toward a
    /// bright grey airlight — the situation the haze model describes.
    /// </summary>
    private static (float[] r, float[] g, float[] b) HazyScene()
    {
        var r = new float[W * H];
        var g = new float[W * H];
        var b = new float[W * H];
        const float ar = 0.72f, ag = 0.75f, ab = 0.82f;   // slightly blue haze

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                float depth = x / (float)W;                 // 0 near, 1 far
                float t = 1f - 0.75f * depth;               // transmission
                float scene = ((x / 3 + y / 3) % 2 == 0) ? 0.22f : 0.55f;

                r[i] = scene * t + ar * (1f - t);
                g[i] = scene * t + ag * (1f - t);
                b[i] = scene * t + ab * (1f - t);
            }
        }
        return (r, g, b);
    }

    [Fact]
    public void Dehaze_RestoresContrastInTheHazyRegion()
    {
        var (r, g, b) = HazyScene();
        var air = Effects.EstimateAirlight(r, g, b, W, H).Gained(1, 1, 1);

        // Contrast in the far (hazy) third, before and after.
        double Far(float[] p)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            for (int y = 0; y < H; y++)
                for (int x = W * 2 / 3; x < W; x++)
                { lo = Math.Min(lo, p[y * W + x]); hi = Math.Max(hi, p[y * W + x]); }
            return hi - lo;
        }

        double before = Far(g);
        Effects.ApplyDehaze(r, g, b, W, H, 85.0, air, W, H, Po);
        double after = Far(g);

        Assert.True(after > before * 1.25,
            $"dehaze should open up the hazy region: {before:F3} -> {after:F3}");
    }

    /// <summary>
    /// One stored estimate has to serve renders carrying different exposures —
    /// that is the whole point of keeping it un-gained. <see cref="Effects.Airlight.Gained"/>
    /// is where the exposure comes back in, so it must scale linearly.
    ///
    /// <para>(The estimator itself is <i>not</i> scale-equivariant — its dark-channel
    /// histogram clamps at 1.0 — which is exactly why the pipeline always measures
    /// from the un-gained sensor buffer, where values are 0…1 by construction, and
    /// never from a gained plane. <c>Dehaze_MatchesAcrossTheCropBoundary</c> in
    /// MaskTests is what pins that end to end.)</para>
    /// </summary>
    [Fact]
    public void Airlight_GainedScalesLinearlyAndFloors()
    {
        var air = new Effects.Airlight(0.40f, 0.50f, 0.60f);

        var doubled = air.Gained(2.0, 2.0, 2.0);
        Assert.Equal(0.80f, doubled.R, 5);
        Assert.Equal(1.00f, doubled.G, 5);
        Assert.Equal(1.20f, doubled.B, 5);

        // Per-channel, as white balance would apply it.
        var wb = air.Gained(1.0, 2.0, 0.5);
        Assert.Equal(0.40f, wb.R, 5);
        Assert.Equal(1.00f, wb.G, 5);
        Assert.Equal(0.30f, wb.B, 5);

        // The recovery divides by the airlight, so it must never reach zero
        // however far a gain is pulled down.
        var crushed = air.Gained(1e-6, 1e-6, 1e-6);
        Assert.True(crushed.R >= 0.05f && crushed.G >= 0.05f && crushed.B >= 0.05f);
        Assert.True(crushed.IsValid);
    }

    [Fact]
    public void Dehaze_EstimatesTheHazeColour()
    {
        var (r, g, b) = HazyScene();
        var air = Effects.EstimateAirlight(r, g, b, W, H).Gained(1, 1, 1);

        Assert.True(air.IsValid);
        // The scene's airlight is (0.72, 0.75, 0.82) — bluish. The estimate is
        // taken from the haziest pixels, which never reach it exactly, but it
        // must land in the right region and keep the blue cast.
        Assert.InRange(air.G, 0.45f, 0.85f);
        Assert.True(air.B > air.R, "estimate must preserve the blue cast of the haze");
    }

    [Fact]
    public void Dehaze_Negative_AddsHaze()
    {
        var (r, g, b) = HazyScene();
        var air = Effects.EstimateAirlight(r, g, b, W, H).Gained(1, 1, 1);

        double Contrast(float[] p)
        {
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (float v in p) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
            return hi - lo;
        }

        double before = Contrast(g);
        Effects.ApplyDehaze(r, g, b, W, H, -80.0, air, W, H, Po);
        Assert.True(Contrast(g) < before, "negative Dehaze should flatten the scene");
    }

    // ── Grain ──────────────────────────────────────────────────────────────

    [Fact]
    public void Grain_IsDeterministicAndNeutral()
    {
        var p = Effects.BuildGrain(60.0, 40.0, 50.0, W, H);

        var (r1, g1, b1) = HazyScene();
        var (r2, g2, b2) = HazyScene();
        Effects.ApplyGrain(r1, g1, b1, W, H, p, Po);
        Effects.ApplyGrain(r2, g2, b2, W, H, p, Po);

        // Same input, same output — grain is a function of position, not of a
        // running random stream, so it survives re-rendering unchanged.
        Assert.Equal(r1, r2);

        // Neutral: the same delta went into all three channels, so grain adds
        // no colour. (The scene itself is not grey, so compare the deltas.)
        var (r0, g0, b0) = HazyScene();
        for (int i = 0; i < r1.Length; i += 97)
        {
            float dr = r1[i] - r0[i], dg = g1[i] - g0[i], db = b1[i] - b0[i];
            Assert.Equal(dr, dg, 4);
            Assert.Equal(dr, db, 4);
        }
    }

    [Fact]
    public void Grain_AmountControlsStrength()
    {
        var (r0, g0, b0) = HazyScene();

        double Deviation(double amount)
        {
            var (r, g, b) = HazyScene();
            var p = Effects.BuildGrain(amount, 40.0, 50.0, W, H);
            Effects.ApplyGrain(r, g, b, W, H, p, Po);
            double acc = 0;
            for (int i = 0; i < r.Length; i++) acc += Math.Abs(r[i] - r0[i]);
            return acc / r.Length;
        }

        double light = Deviation(20.0);
        double heavy = Deviation(90.0);
        Assert.True(light > 0.0, "grain at 20 should do something");
        Assert.True(heavy > light * 2.0, $"grain should scale with Amount: {light:F3} vs {heavy:F3}");
    }

    /// <summary>
    /// Grain cell size scales with the image, so the preview and a full-size
    /// export show grain of the same visual coarseness rather than the export
    /// rendering it invisibly fine.
    /// </summary>
    [Fact]
    public void Grain_SizeScalesWithResolution()
    {
        var small = Effects.BuildGrain(50.0, 50.0, 50.0, 1280, 1280);
        var large = Effects.BuildGrain(50.0, 50.0, 50.0, 5120, 5120);
        Assert.True(large.CellPixels > small.CellPixels * 3.0,
            $"cell size should track resolution: {small.CellPixels} vs {large.CellPixels}");
    }
}
