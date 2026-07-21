using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins the linear gradient's weight field and, in particular, its bounds — the
/// part with real consequences. A linear gradient is a half-plane, so a bounds
/// computation that treats it like a closed shape either clips the effect at an
/// invented edge or gives up and returns the whole frame on every gradient.
/// </summary>
public class LinearGradientTests
{
    private const int W = 320, H = 240;

    private static float At(float[] weights, PixelRect rect, int x, int y)
        => weights[(y - rect.Y) * rect.Width + (x - rect.X)];

    // ── The ramp ───────────────────────────────────────────────────────────

    /// <summary>
    /// At the default 90° the gradient fades downward: full at the top, gone at
    /// the bottom. Getting this backwards is the easiest possible sign error and
    /// would make every "hold back the sky" gradient darken the ground instead.
    /// </summary>
    [Fact]
    public void DefaultAngle_FadesDownward()
    {
        var m = new LinearGradientMask { CenterX = 0.5, CenterY = 0.5, Angle = 90, Length = 0.3 };
        var rect = PixelRect.Full(W, H);
        var weights = m.Weights(W, H, rect);

        Assert.Equal(1f, At(weights, rect, W / 2, 2), 4);          // top
        Assert.Equal(0f, At(weights, rect, W / 2, H - 3), 4);      // bottom

        // Half way across the transition. Not exactly 0.5: weights are sampled
        // at pixel *centres*, so row H/2 sits half a pixel past the midline —
        // which is correct, and is why this is a range rather than an equality.
        Assert.InRange(At(weights, rect, W / 2, H / 2), 0.48f, 0.52f);
    }

    [Fact]
    public void Weight_DecreasesMonotonicallyAlongTheFadeDirection()
    {
        var m = new LinearGradientMask { CenterX = 0.5, CenterY = 0.5, Angle = 90, Length = 0.6 };
        var rect = PixelRect.Full(W, H);
        var weights = m.Weights(W, H, rect);

        float prev = float.PositiveInfinity;
        for (int y = 0; y < H; y++)
        {
            float v = At(weights, rect, W / 2, y);
            Assert.True(v <= prev + 1e-6f, $"weight rose going down at y={y}");
            prev = v;
        }
    }

    /// <summary>Perpendicular to the fade the weight must not change at all —
    /// that is what makes it a <i>linear</i> gradient rather than a smear.</summary>
    [Fact]
    public void Weight_IsConstantPerpendicularToTheFade()
    {
        var m = new LinearGradientMask { CenterX = 0.5, CenterY = 0.5, Angle = 90, Length = 0.4 };
        var rect = PixelRect.Full(W, H);
        var weights = m.Weights(W, H, rect);

        for (int y = 0; y < H; y += 17)
        {
            float reference = At(weights, rect, 0, y);
            for (int x = 0; x < W; x += 13)
                Assert.Equal(reference, At(weights, rect, x, y), 5);
        }
    }

    [Fact]
    public void Angle_RotatesTheFadeDirection()
    {
        var down = new LinearGradientMask { Angle = 90, Length = 0.3 };
        var right = new LinearGradientMask { Angle = 0, Length = 0.3 };
        var rect = PixelRect.Full(W, H);

        var d = down.Weights(W, H, rect);
        var r = right.Weights(W, H, rect);

        // At 0° the fade runs left-to-right: full at the left edge, gone at the right.
        Assert.Equal(1f, At(r, rect, 2, H / 2), 4);
        Assert.Equal(0f, At(r, rect, W - 3, H / 2), 4);
        // And it is genuinely a different field from the downward one.
        Assert.NotEqual(d, r);
    }

    [Fact]
    public void Invert_ComplementsTheWeight()
    {
        var a = new LinearGradientMask { CenterX = 0.4, CenterY = 0.55, Angle = 35, Length = 0.45 };
        var b = a.Clone();
        b.Invert = true;

        var rect = PixelRect.Full(W, H);
        var wa = a.Weights(W, H, rect);
        var wb = b.Weights(W, H, rect);

        for (int i = 0; i < wa.Length; i++) Assert.Equal(1f, wa[i] + wb[i], 5);
    }

    /// <summary>
    /// The transition must meet the flat regions with zero slope. A straight ramp
    /// has a kink at each end, and across a clear sky — the exact thing this tool
    /// is for — those kinks read as two visible bands.
    /// </summary>
    [Fact]
    public void Transition_HasNoSlopeDiscontinuityAtItsEnds()
    {
        var m = new LinearGradientMask { CenterX = 0.5, CenterY = 0.5, Angle = 90, Length = 0.5 };
        var rect = PixelRect.Full(W, H);
        var weights = m.Weights(W, H, rect);

        // Second difference down the centre column: a kink shows up as a spike.
        double worst = 0;
        for (int y = 1; y < H - 1; y++)
        {
            double a = At(weights, rect, W / 2, y - 1);
            double b = At(weights, rect, W / 2, y);
            double c = At(weights, rect, W / 2, y + 1);
            worst = Math.Max(worst, Math.Abs(a - 2 * b + c));
        }
        // A linear ramp of this length would spike to ~1/steps at each end; the
        // smoothstep's curvature is spread over the whole transition instead.
        Assert.True(worst < 0.002, $"transition has a kink: peak curvature {worst:F5}");
    }

    // ── Bounds ─────────────────────────────────────────────────────────────

    /// <summary>The contract the renderer relies on: nothing outside the bounds
    /// may be non-zero, or the gradient is silently clipped there.</summary>
    [Theory]
    [InlineData(0.5, 0.5, 90.0, 0.3, false)]
    [InlineData(0.5, 0.5, 90.0, 0.3, true)]
    [InlineData(0.2, 0.8, 35.0, 0.5, false)]
    [InlineData(0.75, 0.25, 200.0, 0.15, false)]
    [InlineData(0.5, 0.5, 0.0, 1.2, false)]
    [InlineData(0.1, 0.1, 315.0, 0.05, true)]
    public void Bounds_EncloseEveryNonZeroWeight(double cx, double cy, double angle,
                                                 double length, bool invert)
    {
        var m = new LinearGradientMask
        {
            CenterX = cx, CenterY = cy, Angle = angle, Length = length, Invert = invert,
        };

        var full = PixelRect.Full(W, H);
        var everywhere = m.Weights(W, H, full);
        var bounds = m.Bounds(W, H);

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (everywhere[y * W + x] <= 0f) continue;
                Assert.True(x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom,
                    $"weight at ({x},{y}) falls outside bounds {bounds}");
            }
        }
    }

    /// <summary>
    /// Bounds must also be worth having. A gradient tucked into a corner should
    /// not cost the whole frame — that is the entire reason the renderer clips
    /// the image rectangle against the half-plane rather than giving up.
    /// </summary>
    [Fact]
    public void Bounds_AreTightForAGradientThatOnlyCoversACorner()
    {
        // Fades toward the bottom-right, centred near the top-left corner, with a
        // short transition — only that corner is non-zero.
        var m = new LinearGradientMask
        {
            CenterX = 0.12, CenterY = 0.12, Angle = 45, Length = 0.08,
        };

        var bounds = m.Bounds(W, H);
        long area = (long)bounds.Width * bounds.Height;
        long frame = (long)W * H;

        Assert.True(area < frame / 2,
            $"bounds should be much smaller than the frame: {bounds} vs {W}x{H}");
    }

    /// <summary>A gradient spanning the frame legitimately needs all of it.</summary>
    [Fact]
    public void Bounds_CoverTheFrameWhenTheGradientDoes()
    {
        var m = new LinearGradientMask { CenterX = 0.5, CenterY = 0.5, Angle = 90, Length = 2.0 };
        var bounds = m.Bounds(W, H);
        Assert.Equal(PixelRect.Full(W, H), bounds);
    }

    // ── Through MaskSettings ───────────────────────────────────────────────

    /// <summary>
    /// The renderer only ever sees <see cref="MaskSettings.Bounds"/> and
    /// <see cref="MaskSettings.Weights"/>, so the discriminator has to route to
    /// the right shape — a mask set to Linear must not quietly rasterise its
    /// (default, untouched) radial.
    /// </summary>
    [Fact]
    public void MaskSettings_RoutesToTheSelectedShape()
    {
        var mask = new MaskSettings
        {
            Kind = MaskKind.Linear,
            Linear = new LinearGradientMask { Angle = 90, Length = 0.3, CenterY = 0.5 },
        };

        var rect = PixelRect.Full(W, H);
        Assert.Equal(mask.Linear.Weights(W, H, rect), mask.Weights(W, H, rect));
        Assert.Equal(mask.Linear.Bounds(W, H), mask.Bounds(W, H));

        mask.Kind = MaskKind.Radial;
        Assert.Equal(mask.Radial.Weights(W, H, rect), mask.Weights(W, H, rect));
    }

    [Fact]
    public void Clone_DeepCopiesBothShapes()
    {
        var mask = new MaskSettings { Kind = MaskKind.Linear };
        var copy = mask.Clone();

        copy.Linear.Angle = 12;
        copy.Radial.CenterX = 0.1;
        copy.Kind = MaskKind.Radial;

        Assert.Equal(90.0, mask.Linear.Angle);
        Assert.Equal(0.5, mask.Radial.CenterX);
        Assert.Equal(MaskKind.Linear, mask.Kind);
    }
}
