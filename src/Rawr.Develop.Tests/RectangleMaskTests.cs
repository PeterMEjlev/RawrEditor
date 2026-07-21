using System.Windows.Media.Imaging;
using Rawr.Raw;
using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins the rectangle mask: its separable weight field, the rotated bounding box
/// the renderer crops to, and — the part that shares its teeth with the radial —
/// that a rectangle composited through the real pipeline lands exactly where a
/// global render of the same settings would, including across the crop boundary.
///
/// <para>The geometry mirrors <see cref="RadialMask"/> deliberately (centre and
/// both half-extents normalised to width), so these tests mirror the radial's in
/// <see cref="MaskTests"/> where the property is the same and diverge only where
/// the shape is: a rectangle's edge is a pair of straight lines, so equal
/// half-extents draw a square and the corners round rather than the sides.</para>
/// </summary>
public class RectangleMaskTests
{
    // 512 for the same reason MaskTests uses it: the regional filters get a radius
    // meaningfully larger than a small mask crop would derive from its own size,
    // so the crop-parity tests can actually tell a wrong radius apart.
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

    /// <summary>A rectangle centred on the frame, hard-edged so weight is exactly
    /// 1 inside and 0 outside — no feather to blur the assertions.</summary>
    private static MaskSettings CenteredMask(double half, MaskAdjustments adjustments)
        => new()
        {
            Kind = MaskKind.Rectangle,
            Rectangle = new RectangleMask
            {
                CenterX = 0.5,
                CenterY = 0.5,
                HalfWidth = half,
                HalfHeight = half,
                Feather = 0.0,
            },
            Adjustments = adjustments,
        };

    // ── 1. The weight field ────────────────────────────────────────────────

    [Fact]
    public void Weights_AreOneAtCentreAndZeroOutsideTheRectangle()
    {
        var mask = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.2, HalfHeight = 0.2, Feather = 50 };
        var weights = mask.Weights(W, H, new PixelRect(0, 0, W, H));

        Assert.Equal(1f, weights[(H / 2) * W + (W / 2)], 5);
        Assert.Equal(0f, weights[0]);                       // a corner is well outside
        Assert.Equal(0f, weights[(H - 1) * W + (W - 1)]);
    }

    [Fact]
    public void ZeroFeather_GivesAHardEdge()
    {
        var mask = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.25, HalfHeight = 0.25, Feather = 0 };
        var weights = mask.Weights(W, H, new PixelRect(0, 0, W, H));

        foreach (float v in weights) Assert.True(v == 0f || v == 1f, $"unexpected partial weight {v}");
    }

    /// <summary>
    /// A hard-edged, unrotated rectangle is exactly its two half-extent bounds:
    /// on inside both axes, off past either. This is the axis-aligned shape the
    /// weight field must produce before rotation or feather complicate it.
    /// </summary>
    [Fact]
    public void HardRectangle_IsOnInsideBothAxesAndOffPastEither()
    {
        var mask = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.2, HalfHeight = 0.1, Feather = 0 };
        var weights = mask.Weights(W, H, new PixelRect(0, 0, W, H));

        int hwPx = (int)(0.2 * W), hhPx = (int)(0.1 * W); // both normalised to width
        int cx = W / 2, cy = H / 2;

        // Inside a corner of the box (short of both edges) is on.
        Assert.Equal(1f, weights[(cy + hhPx - 3) * W + cx + hwPx - 3]);
        // Just past the X edge but within Y is off — a radial would still be on here.
        Assert.Equal(0f, weights[cy * W + cx + hwPx + 3]);
        // Just past the Y edge but within X is off.
        Assert.Equal(0f, weights[(cy + hhPx + 3) * W + cx]);
    }

    /// <summary>Equal half-extents draw a square on a non-square frame — the whole
    /// reason both are normalised to width, exactly as for the radial.</summary>
    [Fact]
    public void EqualHalfExtents_AreSquareOnANonSquareImage()
    {
        const int w = 300, h = 200;
        var mask = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.2, HalfHeight = 0.2, Feather = 0 };
        var weights = mask.Weights(w, h, new PixelRect(0, 0, w, h));

        int extentPx = (int)(0.2 * w);
        int cx = w / 2, cy = h / 2;

        Assert.Equal(1f, weights[cy * w + cx + extentPx - 2]);
        Assert.Equal(1f, weights[(cy + extentPx - 2) * w + cx]);
        Assert.Equal(0f, weights[cy * w + cx + extentPx + 2]);
        Assert.Equal(0f, weights[(cy + extentPx + 2) * w + cx]);
    }

    [Fact]
    public void Rotation_SwapsTheAxesOfARectangleAtNinetyDegrees()
    {
        var flat = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.3, HalfHeight = 0.1, Feather = 0 };
        var upright = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.1, HalfHeight = 0.3, Feather = 0 };
        var rotated = new RectangleMask { CenterX = 0.5, CenterY = 0.5, HalfWidth = 0.3, HalfHeight = 0.1, Feather = 0, Rotation = 90 };

        var rect = new PixelRect(0, 0, W, H);
        var a = upright.Weights(W, H, rect);
        var b = rotated.Weights(W, H, rect);
        var c = flat.Weights(W, H, rect);

        Assert.Equal(a, b);
        Assert.NotEqual(c, b);
    }

    [Fact]
    public void Invert_ComplementsTheWeight()
    {
        var inside = new RectangleMask { CenterX = 0.4, CenterY = 0.6, HalfWidth = 0.2, HalfHeight = 0.3, Feather = 60 };
        var outside = inside.Clone();
        outside.Invert = true;

        var rect = new PixelRect(0, 0, W, H);
        var a = inside.Weights(W, H, rect);
        var b = outside.Weights(W, H, rect);

        for (int i = 0; i < a.Length; i++) Assert.Equal(1f, a[i] + b[i], 5);
    }

    // ── 2. Bounds ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.5, 0.5, 0.2, 0.2, 0.0)]
    [InlineData(0.3, 0.7, 0.35, 0.12, 40.0)]
    [InlineData(0.5, 0.5, 0.3, 0.1, 30.0)]
    [InlineData(0.15, 0.2, 0.25, 0.25, 0.0)]
    public void Bounds_EncloseEveryNonZeroWeight(double cx, double cy, double hw, double hh, double rotation)
    {
        var mask = new RectangleMask
        {
            CenterX = cx, CenterY = cy, HalfWidth = hw, HalfHeight = hh,
            Rotation = rotation, Feather = 50,
        };

        var full = mask.Weights(W, H, new PixelRect(0, 0, W, H));
        var bounds = mask.Bounds(W, H);

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                if (full[y * W + x] <= 0f) continue;
                Assert.True(x >= bounds.X && x < bounds.Right && y >= bounds.Y && y < bounds.Bottom,
                    $"weight at ({x},{y}) falls outside the reported bounds {bounds}");
            }
        }
    }

    [Fact]
    public void InvertedBounds_CoverTheWholeImage()
    {
        var mask = new RectangleMask { HalfWidth = 0.1, HalfHeight = 0.1, Invert = true };
        Assert.Equal(new PixelRect(0, 0, W, H), mask.Bounds(W, H));
    }

    // ── 3. Compositing through the real pipeline ────────────────────────────

    [Fact]
    public void PixelsOutsideTheMask_AreUntouched()
    {
        var raw = MakeRaw();
        var baseline = new DevelopSettings { Exposure = 0.2, Shadows = 20 };
        var masked = baseline.Clone();
        masked.Masks.Add(CenteredMask(0.15, new MaskAdjustments { Exposure = 2.0, Saturation = 60 }));

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
        masked.Masks.Add(CenteredMask(0.2, offsets));

        byte[] viaMask = RenderBytes(raw, masked);
        byte[] viaGlobal = RenderBytes(raw, offsets.ApplyTo(global));

        int o = Offset(W / 2, H / 2);
        Assert.Equal(viaGlobal[o], viaMask[o]);
        Assert.Equal(viaGlobal[o + 1], viaMask[o + 1]);
        Assert.Equal(viaGlobal[o + 2], viaMask[o + 2]);
    }

    /// <summary>
    /// The regional (guided-filter) sliders must match across the crop boundary,
    /// the test that fails if the rectangle's crop derives its own filter radius
    /// from its own size or pads too thin for the filter's reach. Same guarantee
    /// as the radial's <c>RegionalSliders_MatchAcrossTheCropBoundary</c>.
    /// </summary>
    [Fact]
    public void RegionalSliders_MatchAcrossTheCropBoundary()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings();
        var offsets = new MaskAdjustments { Shadows = 70, Blacks = -40, Highlights = -50 };

        var masked = global.Clone();
        masked.Masks.Add(CenteredMask(0.12, offsets));

        byte[] viaMask = RenderBytes(raw, masked);
        byte[] viaGlobal = RenderBytes(raw, offsets.ApplyTo(global));

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
