using Rawr.Develop;
using Rawr.Raw;
using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Tests for the crop / straighten / orientation stage.
///
/// <para>The images here are built so every pixel names its own coordinate —
/// red carries x, green carries y — which turns "did the rotation land the right
/// way round" into an exact equality rather than a judgement about pixels.</para>
/// </summary>
public class GeometryTests
{
    /// <summary>An image whose red channel is x and green channel is y, so any
    /// remap can be checked by reading a pixel and asking where it came from.</summary>
    private static LinearRawImage Coordinates(int width, int height)
    {
        var pixels = new ushort[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 3;
                pixels[i] = (ushort)x;
                pixels[i + 1] = (ushort)y;
                pixels[i + 2] = 1000;
            }
        }
        return new LinearRawImage(width, height, pixels);
    }

    private static (int x, int y) ReadCoordinate(LinearRawImage img, int x, int y)
    {
        int i = (y * img.Width + x) * 3;
        return (img.Pixels[i], img.Pixels[i + 1]);
    }

    [Fact]
    public void NeutralGeometryReturnsTheSameBuffer()
    {
        var src = Coordinates(16, 9);
        Assert.Same(src, Geometry.Apply(src, new GeometrySettings()));
        Assert.Same(src, Geometry.Apply(src, null));
    }

    [Fact]
    public void CropTakesTheRequestedWindowWithoutResampling()
    {
        var src = Coordinates(100, 80);
        var g = new GeometrySettings { CropX = 0.25, CropY = 0.5, CropWidth = 0.5, CropHeight = 0.25 };

        var cropped = Geometry.Apply(src, g);

        Assert.Equal(50, cropped.Width);
        Assert.Equal(20, cropped.Height);
        // Exact source values, not blends of neighbours — the axis-aligned path
        // must not go anywhere near the bilinear sampler.
        Assert.Equal((25, 40), ReadCoordinate(cropped, 0, 0));
        Assert.Equal((74, 59), ReadCoordinate(cropped, 49, 19));
    }

    [Fact]
    public void QuarterTurnClockwiseMovesTopLeftToTopRight()
    {
        var src = Coordinates(10, 4);
        var rotated = Geometry.Apply(src, new GeometrySettings { Quadrant = 1 });

        Assert.Equal(4, rotated.Width);
        Assert.Equal(10, rotated.Height);
        // The source origin ends up along the top edge at the far right.
        Assert.Equal((0, 0), ReadCoordinate(rotated, 3, 0));
        Assert.Equal((0, 3), ReadCoordinate(rotated, 0, 0));
    }

    [Fact]
    public void FourQuarterTurnsRestoreTheOriginal()
    {
        var src = Coordinates(7, 5);
        var g = new GeometrySettings();
        Geometry.Rotate(g, 4);

        Assert.True(g.IsNeutral);
        Assert.Same(src, Geometry.Apply(src, g));
    }

    [Fact]
    public void FlipsMirrorTheCorrectAxis()
    {
        var src = Coordinates(8, 6);

        var flippedH = Geometry.Apply(src, new GeometrySettings { FlipHorizontal = true });
        Assert.Equal((7, 0), ReadCoordinate(flippedH, 0, 0));
        Assert.Equal((0, 5), ReadCoordinate(flippedH, 7, 5));

        var flippedV = Geometry.Apply(src, new GeometrySettings { FlipVertical = true });
        Assert.Equal((0, 5), ReadCoordinate(flippedV, 0, 0));
    }

    [Fact]
    public void RotatingASideOnCropCarriesTheBoxWithIt()
    {
        // A box hugging the top-left of a landscape frame must still hug the
        // same corner of the picture after a clockwise turn — which puts it at
        // the top-right of the new, portrait frame.
        var g = new GeometrySettings { CropX = 0.0, CropY = 0.0, CropWidth = 0.25, CropHeight = 0.5 };
        Geometry.Rotate(g, 1);

        Assert.Equal(1, g.Quadrant);
        Assert.Equal(0.5, g.CropX, 9);
        Assert.Equal(0.0, g.CropY, 9);
        Assert.Equal(0.5, g.CropWidth, 9);
        Assert.Equal(0.25, g.CropHeight, 9);
    }

    [Fact]
    public void RotatingSwapsWhichFlipIsSet()
    {
        // A quarter turn past a mirror is the other mirror past the same turn.
        // Without the swap, rotating a flipped photo would silently un-flip it.
        var g = new GeometrySettings { FlipHorizontal = true };
        Geometry.Rotate(g, 1);

        Assert.False(g.FlipHorizontal);
        Assert.True(g.FlipVertical);
    }

    [Fact]
    public void StraightenTurnsTheContentClockwise()
    {
        // A tall bright bar down the middle of a dark frame. Turned clockwise,
        // its top should move to the right of centre.
        const int w = 81, h = 81;
        var pixels = new ushort[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int i = (y * w + 40) * 3;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 60000;
        }
        var src = new LinearRawImage(w, h, pixels);

        var turned = Geometry.Apply(src, new GeometrySettings { Straighten = 20.0 });

        int cx = turned.Width / 2;
        int quarter = turned.Height / 4;
        int brightAboveCentre = -1;
        for (int x = 0; x < turned.Width; x++)
        {
            int i = (quarter * turned.Width + x) * 3;
            if (turned.Pixels[i] > 30000) brightAboveCentre = x;
        }

        Assert.True(brightAboveCentre > cx,
            $"expected the bar's upper half to swing right of centre ({cx}); found it at {brightAboveCentre}");
    }

    [Fact]
    public void StraightenKeepsTheCropCentrePixelStill()
    {
        // The rotation turns about the crop's centre, so whatever was under the
        // centre stays under it however far the slider goes.
        var src = Coordinates(101, 101);
        var g = new GeometrySettings { Straighten = 12.5 };

        var turned = Geometry.Apply(src, g);
        var (x, y) = ReadCoordinate(turned, turned.Width / 2, turned.Height / 2);

        Assert.InRange(x, 49, 51);
        Assert.InRange(y, 49, 51);
    }

    [Fact]
    public void OutputSizeAgreesWithWhatApplyProduces()
    {
        var src = Coordinates(120, 90);
        foreach (var g in new[]
                 {
                     new GeometrySettings { CropWidth = 0.5, CropHeight = 0.5 },
                     new GeometrySettings { Quadrant = 1, CropWidth = 0.75, CropHeight = 0.6 },
                     new GeometrySettings { Straighten = 8, CropWidth = 0.7, CropHeight = 0.7,
                                            CropX = 0.15, CropY = 0.15 },
                 })
        {
            var expected = Geometry.OutputSize(g, src.Width, src.Height);
            var actual = Geometry.Apply(src, g);
            Assert.Equal(expected, (actual.Width, actual.Height));
        }
    }

    [Fact]
    public void FullExtentCoversTheWholeRotatedFrame()
    {
        var g = new GeometrySettings { Straighten = 30.0 };
        var extent = Geometry.FullExtent(g, 100, 100);

        // A square turned 30° needs cos30 + sin30 ≈ 1.366 of its own width.
        Assert.Equal(1.3660254, extent.CropWidth, 6);
        Assert.Equal(1.3660254, extent.CropHeight, 6);
        // Still centred on the image.
        Assert.Equal(-0.1830127, extent.CropX, 6);
    }

    [Fact]
    public void CropRectRoundTripsThroughTheFullExtentFrame()
    {
        // The panel reads the box out of the extent frame and the drag writes it
        // back; if those two disagree the box drifts a little on every gesture.
        foreach (double angle in new[] { 0.0, 7.5, -21.0 })
        {
            var g = new GeometrySettings
            {
                CropX = 0.2, CropY = 0.15, CropWidth = 0.5, CropHeight = 0.45,
                Straighten = angle,
            };

            var (x, y, w, h) = Geometry.CropRectInFullExtent(g, 200, 150);
            var round = g.Clone();
            Geometry.SetCropFromFullExtent(round, x, y, w, h, 200, 150);

            Assert.Equal(g.CropX, round.CropX, 9);
            Assert.Equal(g.CropY, round.CropY, 9);
            Assert.Equal(g.CropWidth, round.CropWidth, 9);
            Assert.Equal(g.CropHeight, round.CropHeight, 9);
        }
    }

    [Fact]
    public void RoundTripSurvivesAFlip()
    {
        // A mirror reverses the handedness the straighten lands in. If the two
        // directions disagree about that, the box jumps the moment you flip.
        var g = new GeometrySettings
        {
            CropX = 0.1, CropY = 0.3, CropWidth = 0.6, CropHeight = 0.4,
            Straighten = 11.0, FlipHorizontal = true,
        };

        var (x, y, w, h) = Geometry.CropRectInFullExtent(g, 160, 120);
        var round = g.Clone();
        Geometry.SetCropFromFullExtent(round, x, y, w, h, 160, 120);

        Assert.Equal(g.CropX, round.CropX, 9);
        Assert.Equal(g.CropY, round.CropY, 9);
    }

    [Fact]
    public void FullFrameCropFitsUntilItIsStraightened()
    {
        var g = new GeometrySettings();
        Assert.True(Geometry.CropFits(g, 100, 100));

        g.Straighten = 5.0;
        Assert.False(Geometry.CropFits(g, 100, 100));
    }

    [Fact]
    public void ConstrainCropShrinksUntilEveryCornerLands()
    {
        var g = new GeometrySettings { Straighten = 15.0 };
        Geometry.ConstrainCrop(g, 300, 200);

        Assert.True(Geometry.CropFits(g, 300, 200));
        Assert.True(g.CropWidth < 1.0);
        // Shrunk about the centre, so the box stays centred.
        Assert.Equal(0.5, g.CropX + g.CropWidth / 2, 9);
        Assert.Equal(0.5, g.CropY + g.CropHeight / 2, 9);
        // And the proportions are untouched — both axes scale by the same factor.
        Assert.Equal(300.0 / 200.0,
            (g.CropWidth * 300) / (g.CropHeight * 200), 9);
    }

    [Fact]
    public void ConstrainCropLeavesAFittingBoxAlone()
    {
        var g = new GeometrySettings { CropX = 0.3, CropY = 0.3, CropWidth = 0.2, CropHeight = 0.2 };
        var before = g.Clone();
        Geometry.ConstrainCrop(g, 400, 400);

        Assert.True(before.Matches(g));
    }

    [Fact]
    public void StraightenedCropRendersRealPixelsInEveryCorner()
    {
        // The point of the constraint: no black wedges anywhere in the result.
        var src = Coordinates(200, 150);
        var g = new GeometrySettings { Straighten = 9.0 };
        Geometry.ConstrainCrop(g, src.Width, src.Height);

        var turned = Geometry.Apply(src, g);
        foreach (var (x, y) in new[]
                 {
                     (0, 0), (turned.Width - 1, 0),
                     (0, turned.Height - 1), (turned.Width - 1, turned.Height - 1),
                 })
        {
            int i = (y * turned.Width + x) * 3;
            Assert.True(turned.Pixels[i + 2] > 0,
                $"corner ({x}, {y}) sampled outside the photo");
        }
    }

    [Fact]
    public void ConstrainingFromAFixedBaselineIsReversible()
    {
        // The reason the view-model keeps a crop baseline. Constraining in place
        // ratchets — each angle shrinks what the next one starts from, so
        // rotating out and back loses framing that was never cut. Re-deriving
        // from the baseline every time gives it all back at zero.
        var baseline = new GeometrySettings { CropWidth = 1.0, CropHeight = 1.0 };

        var g = baseline.Clone();
        foreach (double angle in new[] { 3.0, 9.0, 20.0, 9.0, 0.0 })
        {
            g.Straighten = angle;
            g.CropX = baseline.CropX;
            g.CropY = baseline.CropY;
            g.CropWidth = baseline.CropWidth;
            g.CropHeight = baseline.CropHeight;
            Geometry.ConstrainCrop(g, 300, 200);
        }

        Assert.Equal(1.0, g.CropWidth, 9);
        Assert.Equal(1.0, g.CropHeight, 9);

        // And the in-place version is what it is being contrasted with: the same
        // walk without the restore never comes back.
        var ratcheted = baseline.Clone();
        foreach (double angle in new[] { 3.0, 9.0, 20.0, 9.0, 0.0 })
        {
            ratcheted.Straighten = angle;
            Geometry.ConstrainCrop(ratcheted, 300, 200);
        }
        Assert.True(ratcheted.CropWidth < 0.8);
    }

    [Fact]
    public void ApplyAspectLeavesConstrainingToTheCaller()
    {
        // It hands back the full-frame box of that shape even when the current
        // straighten could not hold it — that box is the user's intent, and the
        // caller decides whether it is the one to remember or the one to show.
        var g = new GeometrySettings { Straighten = 20.0 };
        Geometry.ApplyAspect(g, 1.0, 300, 200);

        Assert.Equal(1.0, g.CropHeight, 9);
        Assert.False(Geometry.CropFits(g, 300, 200));
    }

    [Fact]
    public void ApplyAspectFillsTheFrameOnTheConstrainedAxis()
    {
        var g = new GeometrySettings();
        Geometry.ApplyAspect(g, 1.0, 300, 200);   // square out of a 3:2 frame

        Assert.Equal(200.0 / 300.0, g.CropWidth, 9);   // width limited by the height
        Assert.Equal(1.0, g.CropHeight, 9);
        Assert.Equal(0.5, g.CropX + g.CropWidth / 2, 9);
    }

    [Fact]
    public void GeometryIsPartOfWhetherTheSettingsAreNeutral()
    {
        var s = new DevelopSettings();
        Assert.True(s.IsNeutral);

        s.Geometry.CropWidth = 0.5;
        Assert.False(s.IsNeutral);

        s.Reset();
        Assert.True(s.IsNeutral);
    }

    [Fact]
    public void CloningTheSettingsCopiesTheGeometryRatherThanSharingIt()
    {
        // An export renders off a clone while the user keeps dragging the box;
        // a shared reference would let that drag change what the export writes.
        var s = new DevelopSettings();
        s.Geometry.CropWidth = 0.5;

        var copy = s.Clone();
        s.Geometry.CropWidth = 0.25;

        Assert.Equal(0.5, copy.Geometry.CropWidth);
    }
}
