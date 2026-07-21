using Xunit;
using Rawr.Develop;

namespace Rawr.Develop.Tests;

/// <summary>
/// The two highlight stages composed exactly as <c>DevelopProcessor</c>'s Pass H
/// composes them — reconstruction, then the slider — over buffers that imitate what
/// LibRaw actually hands the pipeline for an overexposed frame.
///
/// <para>These are the tests that pin the user-visible claim. The unit tests either
/// side of this file check that each stage does what it says; these check that the
/// combination does the thing the slider exists to do: take a region the decoder
/// flattened and give it back its structure, below white, where it can be seen.</para>
/// </summary>
public class HighlightRecoveryPipelineTests
{
    /// <summary>Population standard deviation of one plane over a rectangle.</summary>
    private static double StdDev(float[] p, int w, int x0, int x1, int y0, int y1)
    {
        double sum = 0; int count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) { sum += p[y * w + x]; count++; }
        double mean = sum / count;

        double acc = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                double d = p[y * w + x] - mean;
                acc += d * d;
            }
        return System.Math.Sqrt(acc / count);
    }

    private static double Mean(float[] p, int w, int x0, int x1, int y0, int y1)
    {
        double sum = 0; int count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++) { sum += p[y * w + x]; count++; }
        return sum / count;
    }

    // Region under test, and an image big enough that the guide's background support
    // is nowhere near it.
    private const int W = 128, H = 128;
    private const int X0 = 40, X1 = 88, Y0 = 40, Y1 = 88;

    /// <summary>
    /// A textured bright subject that overexposed the red channel. Red's true values
    /// run 1.5–2.7 but the decoder clipped them flat at 1.0; green and blue stayed
    /// inside the clip and still carry the texture. The background fixes the local
    /// hue at r:g = 3:1.
    /// </summary>
    private static (float[] r, float[] g, float[] b) BlownRedSubject()
    {
        int n = W * H;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        System.Array.Fill(r, 0.45f);    // background, r:g = 3:1
        System.Array.Fill(g, 0.15f);
        System.Array.Fill(b, 0.075f);

        for (int y = Y0; y < Y1; y++)
            for (int x = X0; x < X1; x++)
            {
                int i = y * W + x;
                // Smooth structure across the subject, plus a little fine texture.
                double t = (x - X0) / (double)(X1 - X0);
                double gv = 0.5 + 0.4 * t + 0.02 * ((y / 2) % 2);
                g[i] = (float)gv;
                b[i] = (float)(gv * 0.5);
                r[i] = 1.0f;            // clipped flat — the truth was gv * 3
            }
        return (r, g, b);
    }

    // ── 1. The decoder really does hand us a flat plateau ──────────────────
    // Pins the premise the rest of this file rests on: after clipping, the red
    // channel carries no variation at all, so nothing downstream can recover it.
    [Fact]
    public void Premise_ClippedChannelArrivesCompletelyFlat()
    {
        var (r, _, _) = BlownRedSubject();
        Assert.Equal(0.0, StdDev(r, W, X0, X1, Y0, Y1), 6);
    }

    // ── 2. Reconstruction transfers the surviving channel's structure ──────
    // Red is rebuilt as green x the local hue ratio, so it comes back with roughly
    // three times green's variation — and above the clip, which is what leaves the
    // slider something to bring down.
    [Fact]
    public void Reconstruction_RebuildsClippedChannelStructure()
    {
        var (r, g, b) = BlownRedSubject();
        double greenStd = StdDev(g, W, X0, X1, Y0, Y1);

        Assert.True(HighlightReconstruction.Apply(r, g, b, W, H, 1.0, 1.0, 1.0));

        double redStd = StdDev(r, W, X0, X1, Y0, Y1);
        Assert.True(redStd > 0.15,
            $"rebuilt red should regain real variation, std {redStd:F4}");
        Assert.True(Mean(r, W, X0, X1, Y0, Y1) > 1.5,
            "rebuilt red should sit well above the clip");

        // The hue ratio the background implies is 3:1, so red should track green.
        Assert.Equal(3.0 * greenStd, redStd, 1);

        int c = 64 * W + 64;
        Assert.Equal(3.0f * g[c], r[c], 2);
    }

    /// <summary>
    /// The 8-bit luma the pipeline's output transform would render these planes as.
    /// The transform is applied per channel and then combined, exactly as
    /// <c>DevelopProcessor</c> does it — which matters, because the transform is
    /// concave, so a pixel whose channels have been spread apart renders darker than
    /// its luminance alone would suggest.
    /// </summary>
    private static float[] RenderLuma8(float[] r, float[] g, float[] b)
    {
        static double Ch(double lin)
            => BasicTone.LightroomMatch(BasicTone.DisplayCurve(lin)) * 255.0;

        var outp = new float[r.Length];
        for (int i = 0; i < r.Length; i++)
            outp[i] = (float)(0.2126 * Ch(r[i]) + 0.7152 * Ch(g[i]) + 0.0722 * Ch(b[i]));
        return outp;
    }

    // ── 3. End to end: blown detail comes back into the visible range ───────
    // Reconstruction alone is invisible — everything above 1.0 still renders as 255.
    // It is the slider that folds that headroom back down the output range, and this
    // asserts the combination moves the subject somewhere a viewer can actually see
    // it while keeping the structure that was recovered.
    [Fact]
    public void Recovery_BringsBlownDetailIntoTheVisibleRange()
    {
        var (r0, g0, b0) = BlownRedSubject();
        float[] before = RenderLuma8(r0, g0, b0);
        double meanBefore = Mean(before, W, X0, X1, Y0, Y1);

        var (r, g, b) = BlownRedSubject();
        HighlightReconstruction.Apply(r, g, b, W, H, 1.0, 1.0, 1.0);
        LocalHighlights.Apply(r, g, b, W, H, new LocalHighlights.Options { Highlights = -100 });

        float[] after = RenderLuma8(r, g, b);
        double meanAfter = Mean(after, W, X0, X1, Y0, Y1);
        double stdAfter = StdDev(after, W, X0, X1, Y0, Y1);

        Assert.True(meanBefore > 240.0,
            $"the subject should start out effectively blown, renders {meanBefore:F1}/255");
        Assert.True(meanAfter < 225.0,
            $"recovery must move it well down the output range, {meanBefore:F1} -> {meanAfter:F1}/255");
        Assert.True(stdAfter > 1.0,
            $"recovered subject must show structure, 8-bit std {stdAfter:F2}");

        // The subject brightens left to right; that ordering has to survive.
        int row = 64 * W;
        Assert.True(after[row + X0 + 4] < after[row + X1 - 5],
            "the subject's left-to-right gradient must survive recovery");
    }

    /// <summary>One channel rendered through the output transform, in 8-bit.</summary>
    private static float[] RenderCh8(float[] c)
    {
        var outp = new float[c.Length];
        for (int i = 0; i < c.Length; i++)
            outp[i] = (float)(BasicTone.LightroomMatch(BasicTone.DisplayCurve(c[i])) * 255.0);
        return outp;
    }

    // ── 3b. Pulling Exposure down recovers the clipped channel ─────────────
    // The Exposure parity gap. Pulling exposure back is how a photographer reaches
    // for a blown sky first, and in Lightroom it brings the detail back. Here it
    // could not: a negative gain on a clipped channel is just a darker constant.
    //
    // Measured on the red channel rather than on luminance, because that is where
    // the change lives. Overall luminance already varies via the unclipped green,
    // so it moves only a little; red is the channel that was pinned flat at the
    // clip, and it is red being stuck that drains the colour out of a recovered
    // sky. DevelopProcessor runs reconstruction whenever Exposure is negative.
    [Fact]
    public void NegativeExposure_RecoversTheClippedChannel()
    {
        static void Gain(float[] r, float[] g, float[] b, double k)
        {
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = (float)(r[i] * k); g[i] = (float)(g[i] * k); b[i] = (float)(b[i] * k);
            }
        }

        double PlainRed(double ev)
        {
            var (r, g, b) = BlownRedSubject();
            Gain(r, g, b, BasicTone.ExposureGain(ev));
            return StdDev(RenderCh8(r), W, X0, X1, Y0, Y1);
        }

        double RecoveredRed(double ev)
        {
            double gain = BasicTone.ExposureGain(ev);
            var (r, g, b) = BlownRedSubject();
            Gain(r, g, b, gain);
            // Reconstruct at the clip level the gain puts the sensor ceiling at,
            // exactly as the pipeline does.
            HighlightReconstruction.Apply(r, g, b, W, H, gain, gain, gain);
            return StdDev(RenderCh8(r), W, X0, X1, Y0, Y1);
        }

        // A negative gain on a clipped channel is just a darker constant — however
        // far you pull back, red has exactly no detail without reconstruction.
        Assert.Equal(0.0, PlainRed(-1.0), 4);
        Assert.Equal(0.0, PlainRed(-2.0), 4);

        double at1 = RecoveredRed(-1.0);
        double at2 = RecoveredRed(-2.0);

        Assert.True(at1 > 0.5, $"one stop back should start to reveal red, 8-bit std {at1:F2}");

        // And pulling back further reveals more, because red's true value overshoots
        // the clip by ~3x here: at −1 EV much of it is still above white, at −2 EV
        // essentially all of it has come back into range.
        Assert.True(at2 > at1 * 1.5,
            $"more pull-back should recover more: {at1:F2} at −1 EV vs {at2:F2} at −2 EV");
    }

    // ── 4. A plateau with nothing surviving anywhere near it stays flat ────
    // The physical limit, pinned — and the boundary of what this change set claims.
    // Where every channel clipped and every neighbour is darker rather than
    // partially recoverable, the information is genuinely gone. Neither the
    // reconstruction nor the slider can invent it, and neither can Lightroom; all
    // that is left is to make the white plateau a grey one.
    //
    // The recoverable cases are the ones with something to recover *from*: a
    // surviving channel (tests 2 and 3 above) or a partially-clipped rim that
    // rebuilds above the clip and can be interpolated inward
    // (HighlightReconstructionTests.FullyClippedCore_IsFilledFromTheRim).
    //
    // Sampled well inside the region with a small radius, because the guided
    // filter's base reaches 2x its radius and would otherwise mix in the
    // surrounding background near the region's edge.
    [Fact]
    public void FullyBlownWithNoSurvivingNeighbour_IsNotRecoverable()
    {
        int n = W * H;
        var r = new float[n]; var g = new float[n]; var b = new float[n];
        System.Array.Fill(r, 0.45f); System.Array.Fill(g, 0.15f); System.Array.Fill(b, 0.075f);
        for (int y = Y0; y < Y1; y++)
            for (int x = X0; x < X1; x++)
            {
                int i = y * W + x;
                r[i] = 1f; g[i] = 1f; b[i] = 1f;   // every channel clipped
            }

        HighlightReconstruction.Apply(r, g, b, W, H, 1.0, 1.0, 1.0);
        LocalHighlights.Apply(r, g, b, W, H,
            new LocalHighlights.Options { Highlights = -100, Radius = 4 });

        const int sx0 = X0 + 16, sx1 = X1 - 16, sy0 = Y0 + 16, sy1 = Y1 - 16;

        // Darker, yes — but still perfectly flat. No detail was or could be created.
        Assert.True(Mean(r, W, sx0, sx1, sy0, sy1) < 1.0);
        Assert.Equal(0.0, StdDev(r, W, sx0, sx1, sy0, sy1), 4);
    }
}
