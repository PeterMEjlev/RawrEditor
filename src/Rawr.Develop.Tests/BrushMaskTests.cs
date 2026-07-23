using System.Windows.Media.Imaging;
using Rawr.Raw;
using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins the brush mask: the distance-field stroke, how Opacity caps one stroke and
/// builds across several, what an erase stroke does, the bounds the renderer crops
/// to, and — the part it shares with the other shapes — that a brushed region
/// composited through the real pipeline lands exactly where a global render of the
/// same settings would.
///
/// <para>The brush differs from the closed shapes in that its geometry is a
/// <i>list</i> rather than a handful of numbers, so the tests that matter most are
/// the ones about that list: that resampling a stroke's spine does not change the
/// field it paints, and that strokes accumulate in order.</para>
/// </summary>
public class BrushMaskTests
{
    // 512 for the same reason MaskTests uses it: the regional filters get a radius
    // meaningfully larger than a small mask crop would derive from its own size,
    // so the crop-parity test can actually tell a wrong radius apart.
    private const int W = 512, H = 512;

    private static LinearRawImage MakeRaw(int w = W, int h = H)
    {
        var px = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double ramp = (x + y) / (double)(w + h);
                double blobs = 0.22 * Math.Sin(x * Math.PI / 48.0) * Math.Sin(y * Math.PI / 48.0);
                double texture = ((x / 4 + y / 4) % 2) * 0.04;
                double v = Math.Clamp(0.10 + 0.55 * ramp + blobs + texture, 0.02, 1.0);
                int i = (y * w + x) * 3;
                px[i] = (ushort)(v * 60000);
                px[i + 1] = (ushort)(v * 55000);
                px[i + 2] = (ushort)(v * 50000);
            }
        }
        return new LinearRawImage(w, h, px);
    }

    private static byte[] RenderBytes(LinearRawImage raw, DevelopSettings s)
    {
        BitmapSource bmp = DevelopProcessor.Render(raw, s);
        int stride = bmp.PixelWidth * 3;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);
        return buf;
    }

    private static int Offset(int x, int y) => (y * W + x) * 3;

    private static BrushStroke Stroke(double size, double opacity, bool erase,
                                      params (double x, double y)[] points)
    {
        var stroke = new BrushStroke { Size = size, Opacity = opacity, Erase = erase };
        foreach (var (x, y) in points) stroke.Points.Add(new BrushPoint(x, y));
        return stroke;
    }

    /// <summary>A single fat dab in the middle of the frame — big enough that its
    /// fully-on core spans plenty of pixels for the pipeline tests to sample.</summary>
    private static MaskSettings CenteredDab(double size, MaskAdjustments adjustments)
    {
        var mask = new MaskSettings { Kind = MaskKind.Brush, Adjustments = adjustments };
        mask.Brush.Strokes.Add(Stroke(size, 1.0, false, (0.5, 0.5)));
        return mask;
    }

    private static float[] Full(BrushMask brush, int w = W, int h = H)
        => brush.Weights(w, h, new PixelRect(0, 0, w, h));

    // ── 1. The weight field ────────────────────────────────────────────────

    [Fact]
    public void ADab_IsFullyOnAtItsCentreAndOffBeyondItsRadius()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 1.0, false, (0.5, 0.5)));

        var w = Full(brush);
        int cx = W / 2, cy = H / 2;
        int radius = (int)(0.1 * W);

        Assert.Equal(1f, w[cy * W + cx], 5);
        Assert.Equal(0f, w[cy * W + cx + radius + 2]);
        Assert.Equal(0f, w[0]);
    }

    /// <summary>The radius is a fraction of <i>width</i> on both axes, as every
    /// other mask normalises its extents — so a dab is round, not an ellipse that
    /// stretches with the sensor.</summary>
    [Fact]
    public void ADab_IsCircularOnANonSquareImage()
    {
        const int w = 300, h = 200;
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 1.0, false, (0.5, 0.5)));

        var weights = Full(brush, w, h);
        int radius = (int)(0.1 * w);
        int cx = w / 2, cy = h / 2;

        Assert.True(weights[cy * w + cx + radius - 3] > 0f);
        Assert.True(weights[(cy + radius - 3) * w + cx] > 0f);
        Assert.Equal(0f, weights[cy * w + cx + radius + 3]);
        Assert.Equal(0f, weights[(cy + radius + 3) * w + cx]);
    }

    [Fact]
    public void ADab_FallsOffMonotonicallyFromTheCore()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 1.0, false, (0.5, 0.5)));

        var w = Full(brush);
        int cx = W / 2, cy = H / 2;

        float previous = 1f;
        for (int d = 0; d <= (int)(0.1 * W) + 4; d++)
        {
            float here = w[cy * W + cx + d];
            Assert.True(here <= previous + 1e-6f, $"weight rose at distance {d}: {previous} → {here}");
            previous = here;
        }
        Assert.Equal(0f, previous);
    }

    /// <summary>
    /// The heart of storing strokes as spines rather than stamped dabs: a stroke
    /// sampled at two points and the same stroke sampled at twenty produce the same
    /// field, because both are the distance to the same line. A dab-stamping
    /// implementation fails this — the sparse version beads.
    /// </summary>
    [Fact]
    public void ResamplingAStrokesSpine_DoesNotChangeWhatItPaints()
    {
        var sparse = new BrushMask();
        sparse.Strokes.Add(Stroke(0.06, 1.0, false, (0.2, 0.5), (0.8, 0.5)));

        var dense = new BrushMask();
        var many = new List<(double, double)>();
        for (int i = 0; i <= 20; i++) many.Add((0.2 + 0.6 * i / 20.0, 0.5));
        dense.Strokes.Add(Stroke(0.06, 1.0, false, many.ToArray()));

        var a = Full(sparse);
        var b = Full(dense);

        for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i], 3);
    }

    [Fact]
    public void AStroke_PaintsAlongItsWholeSpineWithRoundCaps()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.05, 1.0, false, (0.25, 0.5), (0.75, 0.5)));

        var w = Full(brush);
        int cy = H / 2;
        int radius = (int)(0.05 * W);

        // On at both ends and everywhere between.
        Assert.Equal(1f, w[cy * W + (int)(0.25 * W)], 5);
        Assert.Equal(1f, w[cy * W + W / 2], 5);
        Assert.Equal(1f, w[cy * W + (int)(0.75 * W)], 5);

        // Off past the caps — the spine ends, it does not run on to the frame edge.
        Assert.Equal(0f, w[cy * W + (int)(0.25 * W) - radius - 2]);
        Assert.Equal(0f, w[cy * W + (int)(0.75 * W) + radius + 2]);

        // Off above the stroke, so the band has the width its Size says.
        Assert.Equal(0f, w[(cy - radius - 2) * W + W / 2]);
    }

    // ── 2. Opacity ─────────────────────────────────────────────────────────

    [Fact]
    public void Opacity_ScalesWhatOneStrokePaints()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 0.4, false, (0.5, 0.5)));

        Assert.Equal(0.4f, Full(brush)[(H / 2) * W + W / 2], 5);
    }

    /// <summary>Overlapping passes inside one stroke do not stack — that is what
    /// keeps a stroke even where the cursor slowed down or doubled back.</summary>
    [Fact]
    public void OneStroke_DoesNotBuildUpWhereItCrossesItself()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.08, 0.5, false,
            (0.4, 0.5), (0.6, 0.5), (0.4, 0.5), (0.6, 0.5)));

        Assert.Equal(0.5f, Full(brush)[(H / 2) * W + W / 2], 5);
    }

    /// <summary>A second stroke over the same ground builds on the first, toward
    /// but never past fully selected: 0.5 then 0.5 again is 0.75, not 1.0.</summary>
    [Fact]
    public void SeparateStrokes_BuildUpTowardFullySelected()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 0.5, false, (0.5, 0.5)));
        brush.Strokes.Add(Stroke(0.1, 0.5, false, (0.5, 0.5)));

        Assert.Equal(0.75f, Full(brush)[(H / 2) * W + W / 2], 5);

        brush.Strokes.Add(Stroke(0.1, 0.5, false, (0.5, 0.5)));
        Assert.Equal(0.875f, Full(brush)[(H / 2) * W + W / 2], 5);
    }

    [Fact]
    public void EveryWeight_StaysWithinZeroAndOneHoweverManyStrokesOverlap()
    {
        var brush = new BrushMask();
        for (int i = 0; i < 12; i++)
            brush.Strokes.Add(Stroke(0.12, 0.9, false, (0.45 + i * 0.005, 0.5), (0.55, 0.52)));

        foreach (float v in Full(brush))
            Assert.InRange(v, 0f, 1f);
    }

    // ── 3. Erasing ─────────────────────────────────────────────────────────

    [Fact]
    public void AnEraseStroke_TakesWeightBackOut()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.15, 1.0, false, (0.5, 0.5)));
        brush.Strokes.Add(Stroke(0.05, 1.0, true, (0.5, 0.5)));

        var w = Full(brush);
        int cx = W / 2, cy = H / 2;

        Assert.Equal(0f, w[cy * W + cx], 5);
        // Still fully painted where the eraser did not reach: 32 px out is past the
        // eraser's 25.6 px radius but still inside the paint dab's 38.4 px core.
        Assert.Equal(1f, w[cy * W + cx + 32], 5);
    }

    [Fact]
    public void APartialEraseStroke_ScalesTheWeightDownRatherThanClearingIt()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.15, 1.0, false, (0.5, 0.5)));
        brush.Strokes.Add(Stroke(0.05, 0.25, true, (0.5, 0.5)));

        Assert.Equal(0.75f, Full(brush)[(H / 2) * W + W / 2], 5);
    }

    /// <summary>Order matters for a brush, unlike the mask list itself: erasing then
    /// painting is not the same as painting then erasing.</summary>
    [Fact]
    public void StrokeOrder_Matters()
    {
        var eraseLast = new BrushMask();
        eraseLast.Strokes.Add(Stroke(0.1, 1.0, false, (0.5, 0.5)));
        eraseLast.Strokes.Add(Stroke(0.1, 1.0, true, (0.5, 0.5)));

        var paintLast = new BrushMask();
        paintLast.Strokes.Add(Stroke(0.1, 1.0, true, (0.5, 0.5)));
        paintLast.Strokes.Add(Stroke(0.1, 1.0, false, (0.5, 0.5)));

        Assert.Equal(0f, Full(eraseLast)[(H / 2) * W + W / 2], 5);
        Assert.Equal(1f, Full(paintLast)[(H / 2) * W + W / 2], 5);
    }

    // ── 4. Invert and blankness ────────────────────────────────────────────

    [Fact]
    public void Invert_ComplementsTheWeight()
    {
        var painted = new BrushMask();
        painted.Strokes.Add(Stroke(0.12, 0.8, false, (0.35, 0.4), (0.6, 0.7)));

        var inverted = painted.Clone();
        inverted.Invert = true;

        var a = Full(painted);
        var b = Full(inverted);
        for (int i = 0; i < a.Length; i++) Assert.Equal(1f, a[i] + b[i], 5);
    }

    [Fact]
    public void AnUnpaintedBrush_IsBlankButAnInvertedOneIsNot()
    {
        var brush = new BrushMask();
        Assert.True(brush.IsBlank);
        Assert.True(new MaskSettings { Kind = MaskKind.Brush, Adjustments = new MaskAdjustments { Exposure = 1 } }.IsInert);

        brush.Invert = true;
        Assert.False(brush.IsBlank);
    }

    [Fact]
    public void ABrushCarryingOnlyEraseStrokes_IsStillBlank()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 1.0, true, (0.5, 0.5)));
        Assert.True(brush.IsBlank);
    }

    [Fact]
    public void Clone_CopiesTheStrokesRatherThanSharingThem()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.1, 1.0, false, (0.5, 0.5)));

        var copy = brush.Clone();
        // The exporter renders from a clone while the user keeps painting; a shared
        // list would let those strokes land in the file being written.
        brush.Strokes.Add(Stroke(0.1, 1.0, false, (0.2, 0.2)));
        brush.Strokes[0].Points.Add(new BrushPoint(0.9, 0.9));

        Assert.Single(copy.Strokes);
        Assert.Single(copy.Strokes[0].Points);
    }

    // ── 5. Bounds ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.05, 0.5, 0.5)]
    [InlineData(0.2, 0.3, 0.7)]
    [InlineData(0.02, 0.05, 0.95)]
    public void Bounds_EncloseEveryNonZeroWeight(double size, double x, double y)
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(size, 1.0, false, (x, y), (x + 0.1, y - 0.15), (x + 0.2, y)));

        var full = Full(brush);
        var bounds = brush.Bounds(W, H);

        for (int py = 0; py < H; py++)
        {
            for (int px = 0; px < W; px++)
            {
                if (full[py * W + px] <= 0f) continue;
                Assert.True(px >= bounds.X && px < bounds.Right && py >= bounds.Y && py < bounds.Bottom,
                    $"weight at ({px},{py}) falls outside the reported bounds {bounds}");
            }
        }
    }

    /// <summary>An erase stroke can only take weight away, so it must not enlarge
    /// the crop the renderer works over.</summary>
    [Fact]
    public void Bounds_IgnoreEraseStrokes()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.05, 1.0, false, (0.5, 0.5)));
        var paintOnly = brush.Bounds(W, H);

        brush.Strokes.Add(Stroke(0.05, 1.0, true, (0.05, 0.05)));
        Assert.Equal(paintOnly, brush.Bounds(W, H));
    }

    [Fact]
    public void UnpaintedBounds_AreEmptyAndInvertedOnesCoverTheWholeImage()
    {
        var brush = new BrushMask();
        Assert.True(brush.Bounds(W, H).IsEmpty);

        brush.Invert = true;
        Assert.Equal(new PixelRect(0, 0, W, H), brush.Bounds(W, H));
    }

    /// <summary>Rasterising over a sub-rectangle must give the same weights that
    /// rectangle has in the whole-frame field — the renderer only ever asks for the
    /// bounds, so a rect-relative indexing slip would go unseen until export.</summary>
    [Fact]
    public void WeightsOverASubRect_MatchTheWholeFrameField()
    {
        var brush = new BrushMask();
        brush.Strokes.Add(Stroke(0.07, 0.8, false, (0.4, 0.45), (0.6, 0.55)));

        var full = Full(brush);
        var rect = brush.Bounds(W, H);
        var cropped = brush.Weights(W, H, rect);

        for (int row = 0; row < rect.Height; row++)
            for (int col = 0; col < rect.Width; col++)
                Assert.Equal(full[(rect.Y + row) * W + rect.X + col], cropped[row * rect.Width + col], 6);
    }

    // ── 6. Compositing through the real pipeline ────────────────────────────

    [Fact]
    public void PixelsOutsideTheMask_AreUntouched()
    {
        var raw = MakeRaw();
        var baseline = new DevelopSettings { Exposure = 0.2, Shadows = 20 };
        var masked = baseline.Clone();
        masked.Masks.Add(CenteredDab(0.15, new MaskAdjustments { Exposure = 2.0, Saturation = 60 }));

        byte[] a = RenderBytes(raw, baseline);
        byte[] b = RenderBytes(raw, masked);

        foreach (var (x, y) in new[] { (0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1), (5, 5) })
        {
            int o = Offset(x, y);
            Assert.Equal(a[o], b[o]);
            Assert.Equal(a[o + 1], b[o + 1]);
            Assert.Equal(a[o + 2], b[o + 2]);
        }

        int c = Offset(W / 2, H / 2);
        Assert.NotEqual(a[c], b[c]);
    }

    [Fact]
    public void FullWeightInterior_MatchesAGlobalRenderOfTheSameSettings()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings { Exposure = 0.25 };
        var offsets = new MaskAdjustments { Exposure = 1.25, Saturation = 40 };

        var masked = global.Clone();
        masked.Masks.Add(CenteredDab(0.2, offsets));

        byte[] viaMask = RenderBytes(raw, masked);
        byte[] viaGlobal = RenderBytes(raw, offsets.ApplyTo(global));

        int o = Offset(W / 2, H / 2);
        Assert.Equal(viaGlobal[o], viaMask[o]);
        Assert.Equal(viaGlobal[o + 1], viaMask[o + 1]);
        Assert.Equal(viaGlobal[o + 2], viaMask[o + 2]);
    }

    /// <summary>
    /// The regional (guided-filter) sliders must match across the crop boundary —
    /// the test that fails if the brush's crop derives its own filter radius from
    /// its own size or pads too thin for the filter's reach. Same guarantee as the
    /// radial's and the rectangle's.
    /// </summary>
    [Fact]
    public void RegionalSliders_MatchAcrossTheCropBoundary()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings();
        var offsets = new MaskAdjustments { Shadows = 70, Blacks = -40, Highlights = -50 };

        var masked = global.Clone();
        masked.Masks.Add(CenteredDab(0.25, offsets));

        byte[] viaMask = RenderBytes(raw, masked);
        byte[] viaGlobal = RenderBytes(raw, offsets.ApplyTo(global));

        // Well inside the fully-on core (radius 128 px, core 64 px).
        for (int dy = -40; dy <= 40; dy += 10)
        {
            for (int dx = -40; dx <= 40; dx += 10)
            {
                int o = Offset(W / 2 + dx, H / 2 + dy);
                for (int c = 0; c < 3; c++)
                    Assert.True(Math.Abs(viaMask[o + c] - viaGlobal[o + c]) <= 1,
                        $"crop/global mismatch at ({W / 2 + dx},{H / 2 + dy}) channel {c}: " +
                        $"{viaMask[o + c]} vs {viaGlobal[o + c]}");
            }
        }
    }
}
