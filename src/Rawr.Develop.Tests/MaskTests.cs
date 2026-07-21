using System.Windows.Media.Imaging;
using Rawr.Raw;
using Xunit;

namespace Rawr.Develop.Tests;

/// <summary>
/// Pins local adjustments: the radial weight field, the offset model that folds a
/// mask's sliders into the global ones, and — the part with real teeth — the
/// mask-bounded re-render.
///
/// <para>That last one is where the bugs live. The renderer runs the pipeline a
/// second time over a <i>crop</i> of the photo and crossfades the result in, so
/// three things have to hold and none of them are obvious: pixels outside the
/// mask must come through untouched to the bit; pixels at full mask weight must
/// match what a global render with the same settings would have produced; and
/// that must stay true for the <i>spatial</i> sliders, whose filters size their
/// radius from the image dimensions and would quietly use a smaller neighbourhood
/// inside a crop than outside it.</para>
/// </summary>
public class MaskTests
{
    // 512 so the regional filters get a radius (32 px) meaningfully larger than
    // the one a mask's crop would derive from its own size. At 256 the two land
    // close enough together that the parity test below cannot tell them apart,
    // which makes it pass for the wrong reason.
    private const int W = 512, H = 512;

    /// <summary>
    /// A synthetic frame carrying structure at three scales, each of which some
    /// test depends on:
    /// <list type="bullet">
    /// <item>a smooth ramp, so the tone curves have something to move;</item>
    /// <item><b>blobs at roughly the guided filter's own radius</b>, which is the
    ///       only scale at which a wrong filter radius changes the output — a
    ///       ramp is very nearly blur-invariant and fine texture is smoothed away
    ///       at any radius, so a frame made of only those two cannot detect it;</item>
    /// <item>4 px texture, so the base/detail split has detail to preserve.</item>
    /// </list>
    /// </summary>
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

    /// <summary>Render and pull the BGR bytes out. The dither stream is seeded
    /// from the row index alone, so two renders of the same size get identical
    /// dither at identical coordinates and byte comparison stays meaningful.</summary>
    private static byte[] RenderBytes(LinearRawImage raw, DevelopSettings s)
    {
        BitmapSource bmp = DevelopProcessor.Render(raw, s);
        int stride = bmp.PixelWidth * 3;
        var buf = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(buf, stride, 0);
        return buf;
    }

    private static int Offset(int x, int y) => (y * W + x) * 3;

    /// <summary>A mask centred on the frame, hard-edged so weight is exactly 1
    /// inside and exactly 0 outside — no feather to blur the assertions.</summary>
    private static MaskSettings CenteredMask(double radius, MaskAdjustments adjustments)
        => new()
        {
            Radial = new RadialMask
            {
                CenterX = 0.5,
                CenterY = 0.5,
                RadiusX = radius,
                RadiusY = radius,
                Feather = 0.0,
            },
            Adjustments = adjustments,
        };

    // ── 1. The weight field ────────────────────────────────────────────────

    [Fact]
    public void Weights_AreOneAtCentreAndZeroOutsideTheEllipse()
    {
        var mask = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.2, RadiusY = 0.2, Feather = 50 };
        var rect = new PixelRect(0, 0, W, H);
        var weights = mask.Weights(W, H, rect);

        Assert.Equal(1f, weights[(H / 2) * W + (W / 2)], 5);
        Assert.Equal(0f, weights[0]);                       // a corner is well outside
        Assert.Equal(0f, weights[(H - 1) * W + (W - 1)]);
    }

    [Fact]
    public void Weights_FallOffMonotonicallyFromTheCentre()
    {
        var mask = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.3, RadiusY = 0.3, Feather = 100 };
        var weights = mask.Weights(W, H, new PixelRect(0, 0, W, H));

        float prev = float.PositiveInfinity;
        for (int x = W / 2; x < W; x++)
        {
            float v = weights[(H / 2) * W + x];
            Assert.True(v <= prev + 1e-6f, $"weight rose walking outward at x={x}");
            prev = v;
        }
        Assert.Equal(0f, prev);
    }

    [Fact]
    public void ZeroFeather_GivesAHardEdge()
    {
        var mask = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.25, RadiusY = 0.25, Feather = 0 };
        var weights = mask.Weights(W, H, new PixelRect(0, 0, W, H));

        // Every pixel is fully in or fully out — nothing in between.
        foreach (float v in weights) Assert.True(v == 0f || v == 1f, $"unexpected partial weight {v}");
    }

    /// <summary>
    /// Equal radii must draw a circle on a non-square frame. This is the whole
    /// reason both radii are normalised to <i>width</i>; normalising each to its
    /// own axis would make this ellipse, and a mask authored on the preview would
    /// change shape when the export ran at a different aspect-preserving size.
    /// </summary>
    [Fact]
    public void EqualRadii_AreCircularOnANonSquareImage()
    {
        const int w = 300, h = 200;
        var mask = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.2, RadiusY = 0.2, Feather = 0 };
        var weights = mask.Weights(w, h, new PixelRect(0, 0, w, h));

        int radiusPx = (int)(0.2 * w);
        int cx = w / 2, cy = h / 2;

        // Just inside the circle along both axes, and just outside along both.
        Assert.Equal(1f, weights[cy * w + cx + radiusPx - 2]);
        Assert.Equal(1f, weights[(cy + radiusPx - 2) * w + cx]);
        Assert.Equal(0f, weights[cy * w + cx + radiusPx + 2]);
        Assert.Equal(0f, weights[(cy + radiusPx + 2) * w + cx]);
    }

    [Fact]
    public void Rotation_SwapsTheAxesOfAnEllipseAtNinetyDegrees()
    {
        var flat = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.3, RadiusY = 0.1, Feather = 0 };
        var upright = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.1, RadiusY = 0.3, Feather = 0 };
        var rotated = new RadialMask { CenterX = 0.5, CenterY = 0.5, RadiusX = 0.3, RadiusY = 0.1, Feather = 0, Rotation = 90 };

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
        var inside = new RadialMask { CenterX = 0.4, CenterY = 0.6, RadiusX = 0.2, RadiusY = 0.3, Feather = 60 };
        var outside = inside.Clone();
        outside.Invert = true;

        var rect = new PixelRect(0, 0, W, H);
        var a = inside.Weights(W, H, rect);
        var b = outside.Weights(W, H, rect);

        for (int i = 0; i < a.Length; i++) Assert.Equal(1f, a[i] + b[i], 5);
    }

    // ── 2. Bounds ──────────────────────────────────────────────────────────

    /// <summary>
    /// The renderer only re-renders inside <see cref="RadialMask.Bounds"/> and
    /// only blends inside it, so any non-zero weight it fails to enclose is an
    /// adjustment that silently vanishes at the edge of the mask.
    /// </summary>
    [Theory]
    [InlineData(0.5, 0.5, 0.2, 0.2, 0.0)]
    [InlineData(0.3, 0.7, 0.35, 0.12, 40.0)]
    [InlineData(0.5, 0.5, 0.3, 0.1, 30.0)]
    [InlineData(0.15, 0.2, 0.25, 0.25, 0.0)]
    public void Bounds_EncloseEveryNonZeroWeight(double cx, double cy, double rx, double ry, double rotation)
    {
        var mask = new RadialMask
        {
            CenterX = cx, CenterY = cy, RadiusX = rx, RadiusY = ry,
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
        var mask = new RadialMask { RadiusX = 0.1, RadiusY = 0.1, Invert = true };
        var bounds = mask.Bounds(W, H);
        Assert.Equal(new PixelRect(0, 0, W, H), bounds);
    }

    // ── 3. The offset model ────────────────────────────────────────────────

    [Fact]
    public void Adjustments_AddToTheGlobalSliders()
    {
        var global = new DevelopSettings { Exposure = 1.0, Contrast = 20, Shadows = -10 };
        var result = new MaskAdjustments { Exposure = 0.5, Contrast = 30, Shadows = 15 }.ApplyTo(global);

        Assert.Equal(1.5, result.Exposure, 9);
        Assert.Equal(50, result.Contrast, 9);
        Assert.Equal(5, result.Shadows, 9);
    }

    /// <summary>
    /// The bipolar sliders clamp because <see cref="BasicTone"/>'s curves are only
    /// fitted over ±100; Exposure does not, because it is an uncalibrated 2^EV gain
    /// and capping a genuine four-stop request would be the surprising behaviour.
    /// </summary>
    [Fact]
    public void BipolarSlidersClamp_ButExposureDoesNot()
    {
        var global = new DevelopSettings { Exposure = 3.0, Contrast = 80, Blacks = -70 };
        var result = new MaskAdjustments { Exposure = 3.0, Contrast = 80, Blacks = -70 }.ApplyTo(global);

        Assert.Equal(6.0, result.Exposure, 9);
        Assert.Equal(100, result.Contrast, 9);
        Assert.Equal(-100, result.Blacks, 9);
    }

    [Fact]
    public void NeutralAdjustments_LeaveTheGlobalSettingsAlone()
    {
        var global = new DevelopSettings { Exposure = 0.7, Vibrance = 30, Temperature = 15 };
        var result = new MaskAdjustments().ApplyTo(global);

        Assert.Equal(global.Exposure, result.Exposure, 9);
        Assert.Equal(global.Vibrance, result.Vibrance, 9);
        Assert.Equal(global.EffectiveKelvin, result.EffectiveKelvin, 6);
    }

    /// <summary>
    /// A temperature offset has to land on the same illuminant whichever control
    /// the global panel is showing — the two modes are different parameterisations
    /// of one physical quantity, and the mask must not care which is on screen.
    /// </summary>
    [Fact]
    public void TemperatureOffset_IsModeIndependent()
    {
        var relative = new DevelopSettings { Temperature = 20 };
        var kelvin = new DevelopSettings
        {
            UseKelvin = true,
            TemperatureKelvin = WhiteBalance.TemperatureToKelvin(20),
        };

        var offset = new MaskAdjustments { Temperature = 25 };
        Assert.Equal(offset.ApplyTo(relative).EffectiveKelvin,
                     offset.ApplyTo(kelvin).EffectiveKelvin, 6);
    }

    // ── 4. Settings bookkeeping ────────────────────────────────────────────

    [Fact]
    public void Clone_DeepCopiesMasks()
    {
        var settings = new DevelopSettings();
        settings.Masks.Add(CenteredMask(0.2, new MaskAdjustments { Exposure = 1.0 }));

        var copy = settings.Clone();
        copy.Masks[0].Adjustments.Exposure = -2.0;
        copy.Masks[0].Radial.CenterX = 0.1;

        Assert.Equal(1.0, settings.Masks[0].Adjustments.Exposure);
        Assert.Equal(0.5, settings.Masks[0].Radial.CenterX);
    }

    [Fact]
    public void AMaskWithNothingDialledIn_IsStillNeutral()
    {
        var settings = new DevelopSettings();
        settings.Masks.Add(CenteredMask(0.2, new MaskAdjustments()));
        Assert.True(settings.IsNeutral);

        settings.Masks[0].Adjustments.Exposure = 0.5;
        Assert.False(settings.IsNeutral);

        settings.Masks[0].IsEnabled = false;
        Assert.True(settings.IsNeutral);
    }

    // ── 5. Compositing ─────────────────────────────────────────────────────

    /// <summary>
    /// No masks must mean no change at all — this is the path every existing
    /// calibration was measured on, and the refactor that introduced mask
    /// compositing rerouted it through new code.
    /// </summary>
    [Fact]
    public void NoMasks_RendersExactlyAsBefore()
    {
        var raw = MakeRaw();
        var plain = new DevelopSettings { Exposure = 0.4, Contrast = 25, Shadows = 30 };
        var withEmptyList = plain.Clone();

        Assert.Equal(RenderBytes(raw, plain), RenderBytes(raw, withEmptyList));
    }

    [Fact]
    public void AnInertMask_ChangesNothing()
    {
        var raw = MakeRaw();
        var baseline = new DevelopSettings { Exposure = 0.3 };

        var disabled = baseline.Clone();
        var mask = CenteredMask(0.25, new MaskAdjustments { Exposure = 2.0 });
        mask.IsEnabled = false;
        disabled.Masks.Add(mask);

        Assert.Equal(RenderBytes(raw, baseline), RenderBytes(raw, disabled));
    }

    /// <summary>
    /// Pixels the mask does not reach must be bit-identical, not merely close.
    /// The blend loop writes only where the weight is above zero, so any drift
    /// out here would mean the region pass is leaking outside its rectangle.
    /// </summary>
    [Fact]
    public void PixelsOutsideTheMask_AreUntouched()
    {
        var raw = MakeRaw();
        var baseline = new DevelopSettings { Exposure = 0.2, Shadows = 20 };
        var masked = baseline.Clone();
        masked.Masks.Add(CenteredMask(0.15, new MaskAdjustments { Exposure = 2.0, Saturation = 60 }));

        byte[] a = RenderBytes(raw, baseline);
        byte[] b = RenderBytes(raw, masked);

        // Corners: far outside a 0.15-width radial centred on the frame.
        foreach (var (x, y) in new[] { (0, 0), (W - 1, 0), (0, H - 1), (W - 1, H - 1), (5, 5) })
        {
            int o = Offset(x, y);
            Assert.Equal(a[o], b[o]);
            Assert.Equal(a[o + 1], b[o + 1]);
            Assert.Equal(a[o + 2], b[o + 2]);
        }

        // And the mask must actually be doing something in the middle, or the
        // assertion above passes for the wrong reason.
        int c = Offset(W / 2, H / 2);
        Assert.NotEqual(a[c], b[c]);
    }

    /// <summary>
    /// At full mask weight the result must equal a global render carrying the same
    /// settings. This is the crop's parity test: the second pass runs over a
    /// padded sub-rectangle of the photo, and if that changed the arithmetic in any
    /// way the centre of the mask would not match.
    /// </summary>
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
    /// The same parity, but for the sliders that read a <i>regional</i> luminance
    /// plane built by a guided filter. This is the test that fails if the crop is
    /// allowed to derive its own filter radius from its own (smaller) dimensions,
    /// or if the padding is too thin for the filter's reach — both of which leave
    /// the mask's interior rendering against a different base layer than the rest
    /// of the frame, and show up in the photo as a rim just inside the mask edge.
    ///
    /// <para>A tolerance rather than equality: the padding makes the crop's
    /// interior mathematically equivalent, but the box blurs accumulate their
    /// running sums in a different order over a different buffer, so the last bit
    /// of the float can differ.</para>
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

        // Sample across the mask interior, not just dead centre — a radius-driven
        // mismatch is worst near the mask edge, where the crop boundary is closest.
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

    /// <summary>
    /// The same crop parity, but with Dehaze active — the one stage whose
    /// behaviour depends on a statistic of the <i>whole</i> image. A crop that
    /// estimated its own airlight would dehaze the mask's region against a
    /// different haze colour and leave the mask's outline visible as a step in
    /// colour, even though Dehaze is a global slider the mask never touches.
    /// </summary>
    [Fact]
    public void Dehaze_MatchesAcrossTheCropBoundary()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings { Dehaze = 60 };
        var offsets = new MaskAdjustments { Exposure = 0.75 };

        var masked = global.Clone();
        masked.Masks.Add(CenteredMask(0.12, offsets));

        byte[] viaMask = RenderBytes(raw, masked);
        byte[] viaGlobal = RenderBytes(raw, offsets.ApplyTo(global));

        for (int dy = -40; dy <= 40; dy += 20)
        {
            for (int dx = -40; dx <= 40; dx += 20)
            {
                int o = Offset(W / 2 + dx, H / 2 + dy);
                for (int c = 0; c < 3; c++)
                    Assert.True(Math.Abs(viaMask[o + c] - viaGlobal[o + c]) <= 1,
                        $"dehaze crop/global mismatch at ({W / 2 + dx},{H / 2 + dy}) ch {c}: " +
                        $"{viaMask[o + c]} vs {viaGlobal[o + c]}");
            }
        }
    }

    /// <summary>
    /// Overlapping masks add up. Two masks that each brighten the middle must
    /// leave it brighter than either alone — the failure this pins is the
    /// natural one for a crossfade compositor, where the last mask written wins
    /// outright and silently discards the one beneath it.
    /// </summary>
    [Fact]
    public void OverlappingMasks_Accumulate()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings { Exposure = 0.2 };

        var one = global.Clone();
        one.Masks.Add(CenteredMask(0.2, new MaskAdjustments { Exposure = 0.5 }));

        var two = global.Clone();
        two.Masks.Add(CenteredMask(0.2, new MaskAdjustments { Exposure = 0.5 }));
        two.Masks.Add(CenteredMask(0.2, new MaskAdjustments { Exposure = 0.5 }));

        int o = Offset(W / 2, H / 2);
        byte[] plain = RenderBytes(raw, global);
        byte[] single = RenderBytes(raw, one);
        byte[] doubled = RenderBytes(raw, two);

        Assert.True(single[o] > plain[o], "one mask should brighten the centre");
        Assert.True(doubled[o] > single[o],
            $"two stacked masks must accumulate: {doubled[o]} was not brighter than {single[o]}");
    }

    /// <summary>
    /// Because the contributions are summed rather than layered, re-ordering the
    /// list cannot change a pixel — addition commutes. Worth pinning: it is the
    /// property that makes the Masks panel a set rather than a stack, and a
    /// future change back to sequential crossfading would break it silently.
    /// </summary>
    [Fact]
    public void MaskOrder_DoesNotAffectTheRender()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings { Exposure = 0.1 };

        var warmThenCool = global.Clone();
        warmThenCool.Masks.Add(CenteredMask(0.22, new MaskAdjustments { Exposure = 0.8, Temperature = 40 }));
        warmThenCool.Masks.Add(CenteredMask(0.16, new MaskAdjustments { Shadows = 50, Temperature = -30 }));

        var coolThenWarm = global.Clone();
        coolThenWarm.Masks.Add(warmThenCool.Masks[1].Clone());
        coolThenWarm.Masks.Add(warmThenCool.Masks[0].Clone());

        Assert.Equal(RenderBytes(raw, warmThenCool), RenderBytes(raw, coolThenWarm));
    }

    /// <summary>
    /// A region only one mask reaches must render exactly as it would with that
    /// mask alone — accumulation may not leak the other mask's adjustment into
    /// territory it does not cover.
    /// </summary>
    [Fact]
    public void NonOverlappingRegions_AreUnaffectedByOtherMasks()
    {
        var raw = MakeRaw();
        var global = new DevelopSettings();
        var offsets = new MaskAdjustments { Exposure = 1.0 };

        var left = new RadialMask { CenterX = 0.25, CenterY = 0.5, RadiusX = 0.12, RadiusY = 0.12, Feather = 0 };
        var right = new RadialMask { CenterX = 0.75, CenterY = 0.5, RadiusX = 0.12, RadiusY = 0.12, Feather = 0 };

        var alone = global.Clone();
        alone.Masks.Add(new MaskSettings { Radial = left.Clone(), Adjustments = offsets.Clone() });

        var both = global.Clone();
        both.Masks.Add(new MaskSettings { Radial = left.Clone(), Adjustments = offsets.Clone() });
        both.Masks.Add(new MaskSettings { Radial = right.Clone(), Adjustments = offsets.Clone() });

        byte[] a = RenderBytes(raw, alone);
        byte[] b = RenderBytes(raw, both);

        // The centre of the left mask is disjoint from the right one.
        int o = Offset(W / 4, H / 2);
        for (int c = 0; c < 3; c++) Assert.Equal(a[o + c], b[o + c]);
    }
}
