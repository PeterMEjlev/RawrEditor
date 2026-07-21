using System.Windows.Media.Imaging;
using Rawr.Raw;
using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins the full-resolution 1:1 view (<see cref="DevelopProcessor.RenderRegion"/>).
///
/// <para>The whole promise of the zoomed view is that it shows the <i>same pixels
/// the export writes</i>, just windowed — so the test with teeth is parity: a tile
/// rendered over a sub-rectangle must equal that sub-rectangle of a full-frame
/// <see cref="DevelopProcessor.Render"/>, to the byte, across every stage that has
/// a spatial reach (the tone curves' regional masks, Detail, Texture/Clarity,
/// Dehaze's global airlight, the masks, and the grain lattice). If any of those
/// silently used the tile's own size as its frame, this would drift.</para>
/// </summary>
public class RenderRegionTests
{
    // 512 wide so the regional filters get a radius meaningfully larger than a
    // tile would derive from its own extent — the same reasoning as MaskTests.
    private const int W = 512, H = 384;

    /// <summary>Structure at three scales — a ramp for the tone curves, blobs at
    /// roughly the guided filter's radius (the only scale a wrong radius shows up
    /// at), and 4 px texture for the base/detail split.</summary>
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
                px[i]     = (ushort)(v * 60000);
                px[i + 1] = (ushort)(v * 55000);
                px[i + 2] = (ushort)(v * 50000);
            }
        }
        return new LinearRawImage(w, h, px);
    }

    private static (byte[] bytes, int w, int h) RenderFull(LinearRawImage raw, DevelopSettings s)
    {
        BitmapSource bmp = DevelopProcessor.Render(raw, s);
        int stride = bmp.PixelWidth * 3;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);
        return (buf, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static byte[] RenderTile(LinearRawImage developed, DevelopSettings s, PixelRect roi)
    {
        BitmapSource bmp = DevelopProcessor.RenderRegion(developed, s, roi);
        Assert.Equal(roi.Width, bmp.PixelWidth);
        Assert.Equal(roi.Height, bmp.PixelHeight);
        int stride = bmp.PixelWidth * 3;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);
        return buf;
    }

    private static int MaxDiff(byte[] full, int fullW, PixelRect roi, byte[] tile)
    {
        int max = 0;
        for (int ty = 0; ty < roi.Height; ty++)
        {
            for (int tx = 0; tx < roi.Width; tx++)
            {
                int fo = ((roi.Y + ty) * fullW + (roi.X + tx)) * 3;
                int to = (ty * roi.Width + tx) * 3;
                for (int c = 0; c < 3; c++)
                    max = Math.Max(max, Math.Abs(full[fo + c] - tile[to + c]));
            }
        }
        return max;
    }

    // The separable regional filters (chroma NR, the guided filter behind the
    // edge-aware tone masks, Texture/Clarity) accumulate float running sums whose
    // rounding depends on where the crop starts, so an interior tile lands within
    // ~1–2 levels of the whole-frame render rather than bit-exact — the same
    // sub-visible slack the mask-crop path already has. A registration, grain-phase
    // or mask-placement bug would diverge by tens or hundreds, so this still bites.
    private const int RegionalSlack = 2;

    /// <summary>Render both ways and assert the tile equals the window of the full
    /// render within <paramref name="tolerance"/> (0 = byte-exact).</summary>
    private static void AssertParity(LinearRawImage raw, DevelopSettings s, PixelRect roi,
                                     int tolerance = RegionalSlack)
    {
        var developed = Geometry.Apply(raw, s.Geometry);
        var (full, fw, _) = RenderFull(raw, s);
        var tile = RenderTile(developed, s, roi);
        int diff = MaxDiff(full, fw, roi, tile);
        Assert.True(diff <= tolerance,
            $"tile differs from the full render by {diff} (> {tolerance}) somewhere in {roi}");
    }

    private static readonly PixelRect Interior = new(200, 150, 120, 90);
    private static readonly PixelRect Whole = new(0, 0, W, H);
    private static readonly PixelRect Corner = new(0, 0, 96, 72);

    // ── The whole frame, windowed, is still the whole frame ────────────────────

    [Fact]
    public void WholeFrameRoi_IsByteIdenticalToRender()
    {
        var raw = MakeRaw();
        var s = new DevelopSettings { Exposure = 0.6, Contrast = 30, Shadows = 40, Vibrance = 25 };
        // Window == whole frame ⇒ the crop is the frame ⇒ genuinely identical maths.
        AssertParity(raw, s, Whole, tolerance: 0);
    }

    // ── Every stage with a spatial reach, on an interior tile ──────────────────

    [Fact]
    public void Neutral_InteriorTile_IsByteIdentical()
        => AssertParity(MakeRaw(), new DevelopSettings(), Interior);

    [Fact]
    public void ToneSliders_InteriorTile_IsByteIdentical()
    {
        var s = new DevelopSettings
        {
            Temperature = -20, Tint = 15, Exposure = 0.8, Contrast = 45,
            Highlights = -60, Shadows = 55, Whites = -30, Blacks = 25,
            Vibrance = 40, Saturation = -15,
        };
        AssertParity(MakeRaw(), s, Interior);
    }

    [Fact]
    public void Detail_InteriorTile_IsByteIdentical()
    {
        // Detail is the point of the 1:1 view, and its filters size from the frame.
        var s = new DevelopSettings
        {
            Sharpening = 80, SharpenRadius = 1.4, SharpenDetail = 60, SharpenMasking = 40,
            LuminanceNoiseReduction = 50, ColorNoiseReduction = 70,
        };
        AssertParity(MakeRaw(), s, Interior);
    }

    [Fact]
    public void TextureClarityDehaze_InteriorTile_IsByteIdentical()
    {
        // Dehaze in particular has a global statistic (airlight) a tile must share.
        var s = new DevelopSettings { Texture = 60, Clarity = 45, Dehaze = 35 };
        AssertParity(MakeRaw(), s, Interior);
    }

    [Fact]
    public void Grain_InteriorTile_IsByteIdentical()
    {
        // The lattice is position-dependent; the tile must sample it in absolute
        // frame coordinates or the pattern shifts.
        var s = new DevelopSettings { GrainAmount = 70, GrainSize = 40, GrainRoughness = 60 };
        AssertParity(MakeRaw(), s, Interior);
    }

    [Fact]
    public void RadialMask_OverlappingTile_IsByteIdentical()
    {
        var s = new DevelopSettings { Exposure = 0.3 };
        s.Masks.Add(new MaskSettings
        {
            Radial = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.25, RadiusY = 0.25, Feather = 40 },
            Adjustments = new MaskAdjustments { Exposure = 1.2, Shadows = 30, Saturation = 40 },
        });
        AssertParity(MakeRaw(), s, Interior);
    }

    [Fact]
    public void LinearMask_OverlappingTile_IsByteIdentical()
    {
        var s = new DevelopSettings { Contrast = 20 };
        s.Masks.Add(new MaskSettings
        {
            Kind = MaskKind.Linear,
            Linear = new LinearGradientMask { CenterX = 0.5, CenterY = 0.5, Angle = 30, Length = 0.4 },
            Adjustments = new MaskAdjustments { Highlights = -50, Temperature = 20 },
        });
        AssertParity(MakeRaw(), s, Interior);
    }

    // ── Geometry is baked into the developed buffer, and the tile follows it ────

    [Fact]
    public void WithCrop_InteriorTile_IsByteIdentical()
    {
        var s = new DevelopSettings { Exposure = 0.5, Clarity = 30 };
        s.Geometry.CropX = 0.1; s.Geometry.CropY = 0.12;
        s.Geometry.CropWidth = 0.78; s.Geometry.CropHeight = 0.76;
        // Sits inside the ~399×292 cropped frame.
        AssertParity(MakeRaw(), s, new PixelRect(120, 90, 110, 80));
    }

    [Fact]
    public void WithStraighten_InteriorTile_IsByteIdentical()
    {
        var s = new DevelopSettings { Exposure = 0.4 };
        s.Geometry.Straighten = 5.0;   // engages the bilinear resample path
        AssertParity(MakeRaw(), s, Interior);
    }

    // ── An edge-touching tile clamp-extends the same way the full render does ───

    [Fact]
    public void CornerTile_MatchesWithinOneLevel()
    {
        var s = new DevelopSettings
        {
            Exposure = 0.5, Contrast = 40, Sharpening = 60, Texture = 40, GrainAmount = 30,
        };
        // Edge pixels clamp-extend the same way in both paths; only the regional
        // rounding slack remains.
        AssertParity(MakeRaw(), s, Corner);
    }
}
