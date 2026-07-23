using System.Buffers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.Develop;

/// <summary>
/// Renders a <see cref="DevelopSettings"/> over a 16-bit linear <see cref="LinearRawImage"/>
/// into a displayable BGR24 bitmap.
///
/// This is RAWR's <c>ExposureProcessor.Render</c> generalised from "exposure
/// only" to the full Basic panel, with the Lightroom-like tone model in
/// <see cref="BasicTone"/>:
///
///   1. White balance + Exposure are per-channel <i>linear</i> gains applied
///      to the sensor value (Exposure = 2^EV, white balance from
///      <see cref="WhiteBalance"/>'s illuminant model). Values are kept unclamped
///      from here on so highlight detail above the old 65535 ceiling survives.
///   2. Highlights / Shadows / Contrast run in scene-linear log-EV space as a
///      single hue-preserving luminance ratio; Whites / Blacks then remap the
///      linear endpoints. All five are no-ops at neutral.
///   3. The display transform — sRGB encode, camera-match midtone lift, gentle
///      base S, then the Lightroom-match calibration — is a pure function of
///      one channel value, baked once into a 65536-entry float LUT (three
///      lookups per pixel). At neutral the whole pipeline reduces to exactly
///      this composed LUT, so the baseline render matches Lightroom.
///   4. Colour (Vibrance/Saturation) needs cross-channel work, so pixels are
///      decomposed into luma + Rec.709 chroma, then the saturation/vibrance
///      scale, then recompose.
///   4a. Detail — colour + luminance noise reduction and capture sharpening —
///      runs on those planes at the end of the decompose. See <see cref="Detail"/>.
///   4b. The Color Mixer (per-hue-band H/S/L) runs on the recomposed RGB, in
///      HSL — see <see cref="ColorMixer"/> for why it is not on the chroma pair.
///   5. TPDF dither before the 8-bit round so the sRGB curve's compression of
///      the bright end doesn't band in skies and skin.
///
/// Same input → same output regardless of core count (per-row deterministic RNG).
///
/// The tone work (1–3) plus the luma/chroma decompose and chroma denoise (4,
/// pre-scale) are shared by the live preview and by full-resolution export via
/// <see cref="DecomposeToPlanes"/>; only the final colour-scale + quantise
/// stage differs (8-bit sRGB preview vs. 8/16-bit, sRGB/Adobe RGB export).
/// </summary>
public static class DevelopProcessor
{
    /// <summary>
    /// Render <paramref name="raw"/> with <paramref name="s"/> to a frozen BGR24
    /// <see cref="BitmapSource"/> ready to assign to an Image. Pass a cancellation
    /// token from the preview debouncer so a superseded render bails cheaply.
    ///
    /// This is the live-preview path; at neutral it reduces to the composed
    /// display LUT (camera transform → Lightroom-match), so the preview shows
    /// the same baseline the export writes. The final loop is order-/core-
    /// invariant (per-row deterministic dither) — keep it that way.
    /// </summary>
    public static BitmapSource Render(LinearRawImage raw, DevelopSettings s, CancellationToken ct = default)
    {
        var po = new ParallelOptions { CancellationToken = ct };
        var (w, h, r, g, b) = BuildRgbPlanes(raw, s, po, ct);
        return QuantizeBgr24Window(r, g, b, w, 0, 0, w, h, 0, po, ct);
    }

    /// <summary>
    /// Dither and quantise a sub-window of fractional 8-bit sRGB planes into a
    /// frozen Bgr24 <see cref="BitmapSource"/>.
    ///
    /// <para>The planes are sized <paramref name="planeStride"/> wide; the window
    /// starts at (<paramref name="srcX0"/>, <paramref name="srcY0"/>) within them
    /// and is <paramref name="outW"/>×<paramref name="outH"/>. The per-row dither
    /// seed is taken from the <i>absolute</i> output row
    /// (<paramref name="absRowBase"/> + local row), so a windowed render dithers
    /// byte-for-byte the same as the whole-frame render of the same content — the
    /// property the region-render parity test relies on. <see cref="Render"/> is
    /// just the full-frame case (window = the whole plane, absRowBase = 0).</para>
    /// </summary>
    private static BitmapSource QuantizeBgr24Window(
        float[] r, float[] g, float[] b, int planeStride,
        int srcX0, int srcY0, int outW, int outH, int absRowBase,
        ParallelOptions po, CancellationToken ct)
    {
        int stride = outW * 3;
        // Rent the scratch buffer: BitmapSource.Create copies it eagerly, so it can
        // be returned to the pool the moment Create returns — this buffer is churned
        // once per render (~11 MB at 2560×1440), previously a fresh LOH allocation
        // each frame. Rented arrays may be longer than requested; Create reads only
        // outH * stride bytes, so the extra tail is harmless.
        int size = outH * stride;
        byte[] bgr = ArrayPool<byte>.Shared.Rent(size);
        try
        {
        Parallel.For(0, outH, po, oy =>
        {
            // Per-row XorShift seed (Knuth constant + offset) keyed to the absolute
            // output row — independent, deterministic, scheduling- and window-invariant.
            uint rng = unchecked((uint)(absRowBase + oy) * 2654435761u + 0x12345678u);
            if (rng == 0) rng = 1;

            int row = oy * stride;
            int rowI = (srcY0 + oy) * planeStride + srcX0;
            for (int x = 0; x < outW; x++)
            {
                int i = rowI + x;
                float lr = r[i];
                float lg = g[i];
                float lb = b[i];

                // TPDF dither (sum of two uniforms) per channel, neutral-coloured.
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ar = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float br = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dr = ar + br;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ag = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bg = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dg = ag + bg;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ab = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bb = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float db = ab + bb;

                int rv = (int)(lr + dr + 0.5f);
                int gv = (int)(lg + dg + 0.5f);
                int bv = (int)(lb + db + 0.5f);

                int o = row + x * 3;
                bgr[o]     = (byte)(bv < 0 ? 0 : bv > 255 ? 255 : bv);
                bgr[o + 1] = (byte)(gv < 0 ? 0 : gv > 255 ? 255 : gv);
                bgr[o + 2] = (byte)(rv < 0 ? 0 : rv > 255 ? 255 : rv);
            }
        });

        ct.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(outW, outH, 96, 96, PixelFormats.Bgr24, null, bgr, stride);
        result.Freeze();
        return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bgr);
        }
    }

    /// <summary>
    /// Render a rectangular window of a <b>full-resolution</b>, already-developed
    /// image at native resolution — the editor's Lightroom-style 1:1 view.
    ///
    /// <para><paramref name="developed"/> is the sensor buffer with geometry
    /// (crop / straighten / orientation) <i>already applied</i>: it is the frame
    /// the viewer shows, in the same coordinates the on-screen box uses. Caching
    /// that buffer is the caller's job (it changes only when the geometry does),
    /// so a slider drag re-renders just the visible tile and never re-runs the
    /// geometry pass. <paramref name="roi"/> is the window to render, in that
    /// buffer's pixels.</para>
    ///
    /// <para><b>Cost is bounded by the window, not the sensor.</b> Only
    /// <paramref name="roi"/> (padded for the spatial filters, exactly as mask
    /// compositing pads its crops) is run through the pipeline, so a 1:1 tile of a
    /// 45 MP file costs about what the whole downsampled preview does. The result
    /// is byte-for-byte the sub-rectangle a full-frame <see cref="Render"/> would
    /// have produced — same tone maths, same masks, same grain lattice, same
    /// per-row dither — which is what lets the zoomed view agree with the export
    /// to the pixel. See <c>RenderRegionTests</c>.</para>
    /// </summary>
    public static BitmapSource RenderRegion(LinearRawImage developed, DevelopSettings s,
                                            PixelRect roi, CancellationToken ct = default)
    {
        var po = new ParallelOptions { CancellationToken = ct };
        int fw = developed.Width, fh = developed.Height;

        // Clamp the requested window to the frame, and never let it collapse to
        // nothing — a degenerate ROI still has to return a 1 px bitmap rather than
        // throw from the viewer's paint path.
        int rx0 = Math.Clamp(roi.X, 0, fw);
        int ry0 = Math.Clamp(roi.Y, 0, fh);
        int rx1 = Math.Clamp(roi.Right, 0, fw);
        int ry1 = Math.Clamp(roi.Bottom, 0, fh);
        if (rx1 <= rx0) rx1 = Math.Min(fw, rx0 + 1);
        if (ry1 <= ry0) ry1 = Math.Min(fh, ry0 + 1);
        var outRect = new PixelRect(rx0, ry0, rx1 - rx0, ry1 - ry0);

        // Pad the window so the spatial filters read real neighbouring pixels at
        // its edges instead of clamp-extending a false boundary into the tile —
        // the same padding, sized the same way, that ComposeMasks uses.
        int regional = Math.Max(EdgeAwareLuma.RegionRadius(fw, fh),
                       Math.Max(LocalHighlights.RegionRadius(fw, fh), Effects.MaxRegionRadius(fw, fh)));
        int pad = 2 * regional + 16;
        int px0 = Math.Max(0, outRect.X - pad);
        int py0 = Math.Max(0, outRect.Y - pad);
        int px1 = Math.Min(fw, outRect.Right + pad);
        int py1 = Math.Min(fh, outRect.Bottom + pad);
        int padW = px1 - px0, padH = py1 - py0;

        bool wholeFrame = px0 == 0 && py0 == 0 && padW == fw && padH == fh;
        var region = wholeFrame ? developed : developed.Crop(px0, py0, padW, padH);
        if (region is null)
        {
            var blank = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[3], 3);
            blank.Freeze();
            return blank;
        }

        // Geometry is already baked into 'developed'; masks are composited
        // separately below (mask rectangles are normalised to the full frame, so
        // they cannot ride inside the tone crop). Everything else — the tone
        // sliders, Detail, Texture/Clarity/Dehaze — runs over the crop with the
        // full frame as context so every regional radius matches the whole-frame
        // render.
        var tone = s.Clone();
        tone.Geometry = new GeometrySettings();
        tone.Masks = new List<MaskSettings>();

        // Dehaze estimates one airlight for the whole photograph; a tile must not
        // derive its own from a fragment. Estimate it here from the full developed
        // frame — exactly the buffer and statistic the whole-frame path uses — and
        // hand it to the tone pass and every mask.
        Effects.Airlight? airlight = null;
        if (s.Dehaze != 0.0)
            airlight = Effects.EstimateAirlightFromSensor(developed.Pixels, fw, fh);

        var (_, _, r, g, b) = RenderRgbPlanes(region, tone, po, ct, fw, fh, ref airlight,
                                              sceneFrame: developed);

        ComposeMasksRegion(developed, s, r, g, b, px0, py0, padW, padH, po, ct, airlight);

        // Grain last, keyed to absolute frame coordinates so the tile's pattern
        // registers with the whole-frame render instead of restarting at the crop.
        // Local grain masks modulate its amplitude over the tile the same way the
        // whole-frame path does, so a 1:1 tile still matches the export.
        var grain = Effects.BuildGrain(s.GrainAmount, s.GrainSize, s.GrainRoughness, fw, fh);
        var grainAmounts = BuildLocalGrainAmounts(s, grain, fw, fh, px0, py0, padW, padH);
        Effects.ApplyGrain(r, g, b, padW, padH, px0, py0, grain, grainAmounts, po);

        ct.ThrowIfCancellationRequested();
        return QuantizeBgr24Window(r, g, b, padW,
            outRect.X - px0, outRect.Y - py0, outRect.Width, outRect.Height, outRect.Y, po, ct);
    }

    /// <summary>
    /// Render the sharpening mask as a black-and-white image: white is sharpened,
    /// black is masked out. This is Lightroom's Alt-drag visualisation, and its
    /// only job is to show where Masking is taking effect.
    ///
    /// <para>Sharpening is suppressed for this pass — not as an optimisation, but
    /// because the mask is defined on the luma <see cref="Detail.Sharpen"/> would
    /// be handed: after noise reduction, before any sharpening. Running it over an
    /// already-sharpened plane would show the mask of a different image.</para>
    ///
    /// <para>No dither. Every other output here is a photograph, where dither buys
    /// gradients; this is a diagnostic, and noise in it would read as mask
    /// structure that isn't there.</para>
    /// </summary>
    public static BitmapSource RenderSharpenMask(LinearRawImage raw, DevelopSettings s,
                                                 CancellationToken ct = default)
    {
        var po = new ParallelOptions { CancellationToken = ct };

        // Same crop the photo is rendered under — the mask has to line up with
        // what the viewer is showing, not with the uncropped sensor frame.
        raw = Geometry.Apply(raw, s.Geometry);

        var unsharpened = s.Clone();
        unsharpened.Sharpening = 0;
        Effects.Airlight? airlight = null;
        var (w, h, luma, _, _) = DecomposeToPlanes(raw, unsharpened, po, ct,
                                                   raw.Width, raw.Height, ref airlight);

        var sharpen = Detail.BuildSharpen(s.Sharpening, s.SharpenRadius, s.SharpenDetail, s.SharpenMasking);
        float[] mask = Detail.RenderMask(luma, w, h, sharpen, po);

        int stride = w * 3;
        byte[] bgr = new byte[h * stride];
        Parallel.For(0, h, po, yy =>
        {
            int row = yy * stride;
            int rowI = yy * w;
            for (int x = 0; x < w; x++)
            {
                float m = mask[rowI + x];
                int v = (int)(m * 255f + 0.5f);
                if (v < 0) v = 0; else if (v > 255) v = 255;
                int o = row + x * 3;
                bgr[o] = bgr[o + 1] = bgr[o + 2] = (byte)v;
            }
        });

        ct.ThrowIfCancellationRequested();
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bgr, stride);
        result.Freeze();
        return result;
    }

    /// <summary>
    /// Full-quality export render. Runs the identical tone pipeline as the
    /// preview (<see cref="DecomposeToPlanes"/>) but quantises to 8- or 16-bit
    /// and, for <see cref="ExportColorSpace.AdobeRgb"/>, re-encodes each pixel
    /// from sRGB into Adobe RGB (1998). Returns a frozen Bgr24 (8-bit) or
    /// Rgb48 (16-bit) <see cref="BitmapSource"/>; the caller embeds the matching
    /// ICC profile.
    /// </summary>
    public static BitmapSource RenderExport(LinearRawImage raw, DevelopSettings s,
                                            bool sixteenBit, ExportColorSpace colorSpace,
                                            CancellationToken ct = default)
    {
        var po = new ParallelOptions { CancellationToken = ct };
        var (w, h, rPlane, gPlane, bPlane) = BuildRgbPlanes(raw, s, po, ct);

        bool adobe = colorSpace == ExportColorSpace.AdobeRgb;
        float max = sixteenBit ? 65535f : 255f;
        // TPDF amplitude is ±1 LSB *of the output*; in the 0..255 working space
        // a 16-bit LSB is 255/65535 of a unit, so the dither shrinks with depth.
        float ditherUnit = sixteenBit ? 255f / 65535f : 1f;

        // 8-bit → byte[] Bgr24 (matches preview layout); 16-bit → ushort[] Rgb48.
        int comps = w * h * 3;
        byte[]? bytes = sixteenBit ? null : new byte[h * (w * 3)];
        ushort[]? shorts = sixteenBit ? new ushort[comps] : null;

        Parallel.For(0, h, po, yy =>
        {
            uint rng = unchecked((uint)yy * 2654435761u + 0x12345678u);
            if (rng == 0) rng = 1;

            int rowI = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowI + x;
                float lr = rPlane[i];
                float lg = gPlane[i];
                float lb = bPlane[i];

                // sRGB display value in 0..1 (the pipeline encodes to sRGB).
                float fr = lr * (1f / 255f); if (fr < 0f) fr = 0f; else if (fr > 1f) fr = 1f;
                float fg = lg * (1f / 255f); if (fg < 0f) fg = 0f; else if (fg > 1f) fg = 1f;
                float fb = lb * (1f / 255f); if (fb < 0f) fb = 0f; else if (fb > 1f) fb = 1f;

                if (adobe) SrgbToAdobeRgb(ref fr, ref fg, ref fb);

                // Same TPDF stream structure as the preview, scaled to the
                // target depth so 16-bit gets a correspondingly finer dither.
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ar = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float br = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dr = (ar + br) * ditherUnit;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ag = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bg = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float dg = (ag + bg) * ditherUnit;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float ab = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
                float bb = (rng & 0xFFFF) * (1f / 65536f) - 0.5f;
                float db = (ab + bb) * ditherUnit;

                int R = Quant(fr, dr, max);
                int G = Quant(fg, dg, max);
                int B = Quant(fb, db, max);

                if (sixteenBit)
                {
                    int o = i * 3;
                    shorts![o]     = (ushort)R;
                    shorts[o + 1]  = (ushort)G;
                    shorts[o + 2]  = (ushort)B;
                }
                else
                {
                    int o = yy * (w * 3) + x * 3;
                    bytes![o]     = (byte)B;
                    bytes[o + 1]  = (byte)G;
                    bytes[o + 2]  = (byte)R;
                }
            }
        });

        ct.ThrowIfCancellationRequested();
        BitmapSource result = sixteenBit
            ? BitmapSource.Create(w, h, 96, 96, PixelFormats.Rgb48, null, shorts!, w * 3 * sizeof(ushort))
            : BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, bytes!, w * 3);
        result.Freeze();
        return result;
    }

    /// <summary>Quantise a 0..1 display value to [0, <paramref name="max"/>]
    /// with the supplied (already depth-scaled, 0..255-space) TPDF dither.</summary>
    private static int Quant(float f01, float dither255, float max)
    {
        // Work in 0..255 units (where the dither lives), then scale to target.
        float v = f01 * 255f + dither255;
        int q = (int)(v * (max / 255f) + 0.5f);
        if (q < 0) q = 0; else if (q > (int)max) q = (int)max;
        return q;
    }

    // sRGB display value (0..1, sRGB primaries/TRC) → Adobe RGB (1998) display
    // value (0..1, Adobe RGB primaries, γ 2.19921875). Decode sRGB → linear,
    // rotate primaries (both D65, no chromatic adaptation), re-encode.
    private static void SrgbToAdobeRgb(ref float r, ref float g, ref float b)
    {
        double lr = SrgbToLinear(r);
        double lg = SrgbToLinear(g);
        double lb = SrgbToLinear(b);

        // linear sRGB → linear Adobe RGB (D65). Standard conversion matrix.
        double ar = 0.71516490 * lr + 0.28483510 * lg;
        double ag = lg;
        double ab = 0.04117138 * lg + 0.95882862 * lb;

        if (ar < 0.0) ar = 0.0; else if (ar > 1.0) ar = 1.0;
        if (ag < 0.0) ag = 0.0; else if (ag > 1.0) ag = 1.0;
        if (ab < 0.0) ab = 0.0; else if (ab > 1.0) ab = 1.0;

        const double invGamma = 1.0 / 2.19921875;
        r = (float)Math.Pow(ar, invGamma);
        g = (float)Math.Pow(ag, invGamma);
        b = (float)Math.Pow(ab, invGamma);
    }

    private static double SrgbToLinear(double c)
        => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>
    /// The full render up to (but not including) dither and quantisation: the
    /// global pipeline, then every active mask composited over it. Returns
    /// fractional 8-bit sRGB planes in 0…255. Both <see cref="Render"/> and
    /// <see cref="RenderExport"/> start here, so preview and export cannot drift.
    /// </summary>
    private static (int w, int h, float[] r, float[] g, float[] b)
        BuildRgbPlanes(LinearRawImage raw, DevelopSettings s, ParallelOptions po, CancellationToken ct)
    {
        // Crop, straighten and orientation resolve into the buffer before any
        // tone work, and every stage below then reads "the photograph" — which
        // after a crop is the cropped frame, not the sensor. That is not a
        // convenience: the regional filters size their radius from the frame,
        // Dehaze estimates one airlight for it, and the mask rectangles are
        // normalised to it. Cropping afterwards would leave all three describing
        // a picture nobody is going to see. Neutral geometry hands the buffer
        // straight back, so the baseline render is untouched.
        raw = Geometry.Apply(raw, s.Geometry);

        // Estimated on the full frame by the pass below, then handed to every
        // mask crop so they dehaze against the same haze colour — see
        // Effects.EstimateAirlight for why a crop must not estimate its own.
        Effects.Airlight? airlight = null;

        var planes = RenderRgbPlanes(raw, s, po, ct, raw.Width, raw.Height, ref airlight);
        ComposeMasks(raw, s, planes.r, planes.g, planes.b, po, ct, airlight);

        // Grain goes on last, after the masks. It is a property of the print
        // rather than of any region, and running it inside each mask's crop
        // would tile the noise field — the pattern is a function of position,
        // so a crop would generate a different one and the mask's outline would
        // appear as a seam in the grain. A mask that carries a grain offset does
        // not get its own field: it modulates the amplitude of this one, per
        // pixel, so the pattern stays continuous across the mask edge.
        var grain = Effects.BuildGrain(s.GrainAmount, s.GrainSize, s.GrainRoughness,
                                       raw.Width, raw.Height);
        var grainAmounts = BuildLocalGrainAmounts(s, grain, raw.Width, raw.Height,
                                                   0, 0, planes.w, planes.h);
        Effects.ApplyGrain(planes.r, planes.g, planes.b, planes.w, planes.h,
                           0, 0, grain, grainAmounts, po);

        return planes;
    }

    /// <summary>
    /// <see cref="DecomposeToPlanes"/> followed by the per-pixel colour stage —
    /// Vibrance/Saturation on the chroma pair, recompose to RGB, then the Colour
    /// Mixer — leaving fractional 8-bit sRGB in three planes.
    ///
    /// <para>The luma/chroma buffers are <i>reused</i> as the RGB output rather
    /// than a second trio being allocated: at full export resolution three more
    /// float planes is another half-gigabyte on a 45 MP file. Every read for a
    /// pixel happens before any write to it, so the aliasing is safe.</para>
    ///
    /// <para><paramref name="contextW"/>/<paramref name="contextH"/> are the
    /// dimensions of the image this buffer is <i>part of</i>, which differ from
    /// the buffer's own only when rendering a mask's crop. The regional filters
    /// size their radius from them so a crop measures "region" over the same
    /// neighbourhood the full frame does — see <see cref="EdgeAwareLuma.RegionRadius"/>.</para>
    /// </summary>
    private static (int w, int h, float[] r, float[] g, float[] b)
        RenderRgbPlanes(LinearRawImage raw, DevelopSettings s, ParallelOptions po,
                        CancellationToken ct, int contextW, int contextH,
                        ref Effects.Airlight? airlight, LinearRawImage? sceneFrame = null)
    {
        var (w, h, luma, cb, cr) = DecomposeToPlanes(raw, s, po, ct, contextW, contextH, ref airlight, sceneFrame);

        // ── Colour scale: baseline pop + Saturation + chroma-aware Vibrance ──
        var presence = Presence.Build(s.Vibrance, s.Saturation);
        // The Color Mixer works in HSL on recomposed RGB, so it runs after the
        // recompose below rather than on the chroma pair. Inactive at neutral,
        // where Apply returns before touching the pixel.
        var mixer = ColorMixer.Build(s.ColorMixer);

        Parallel.For(0, h, po, yy =>
        {
            int rowI = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowI + x;
                float y = luma[i];
                float cbv = cb[i];
                float crv = cr[i];

                Presence.Apply(presence, ref cbv, ref crv);

                float lr = y + crv;
                float lb = y + cbv;
                float lg = (y - 0.2126f * lr - 0.0722f * lb) * (1f / 0.7152f);

                ColorMixer.Apply(mixer, ref lr, ref lg, ref lb);

                // Aliased writes — cr becomes R, luma becomes G, cb becomes B.
                cr[i] = lr;
                luma[i] = lg;
                cb[i] = lb;
            }
        });

        ct.ThrowIfCancellationRequested();
        return (w, h, cr, luma, cb);
    }

    /// <summary>
    /// Composite each active mask onto the already-rendered global planes.
    ///
    /// <para><b>The model is re-render and crossfade.</b> For each mask the whole
    /// pipeline runs a second time with the mask's offsets folded into the global
    /// settings, and the two results are mixed per pixel by the mask's weight.
    /// That is more expensive than modulating slider strengths inside the tone
    /// loop, and it is the right trade here: a masked Exposure is then <i>exactly</i>
    /// the same operator as the global one, including the spatial stages
    /// (Highlights' local tone mapping, the v3 regional masks) that a per-pixel
    /// strength scale cannot express at all. Masks that disagree with the global
    /// render only where they are actually applied is a property worth paying for.</para>
    ///
    /// <para><b>Cost is bounded by the mask, not the frame.</b> A radial covering
    /// a tenth of the picture re-renders a tenth of the pixels, because the second
    /// pass runs over the mask's bounding rectangle. The rectangle is padded first:
    /// the pipeline's spatial filters clamp-extend at the buffer edge, so an
    /// unpadded crop would invent a hard boundary in the middle of the photo and
    /// leave a bright rim inside the mask. Padding by twice the regional radius
    /// covers the guided filter's two chained box blurs, which is the widest reach
    /// anything in the pipeline has.</para>
    ///
    /// <para><b>Masks accumulate.</b> Each contributes the difference between its
    /// own render and the global one, scaled by its weight, and those differences
    /// <i>add</i> — two overlapping masks that each brighten a region both
    /// brighten it, rather than the later one replacing the earlier. The
    /// alternative (crossfading each mask onto the running composite) makes the
    /// last mask in the list win outright wherever it sits at full weight, which
    /// means a mask can silently undo the one beneath it.</para>
    ///
    /// <para>Two consequences worth knowing. Masks are <b>order-independent</b>:
    /// addition commutes, so re-ordering the list cannot change the render. And
    /// the sum is taken in display space, so overlapping adjustments add their
    /// visible <i>effect</i> rather than their slider values — two +1 EV masks
    /// land somewhat brighter than a single +2 EV one would, because the output
    /// curve compresses the second stop and this addition happens after it.</para>
    /// </summary>
    private static void ComposeMasks(LinearRawImage raw, DevelopSettings s,
                                     float[] r, float[] g, float[] b,
                                     ParallelOptions po, CancellationToken ct,
                                     Effects.Airlight? airlight)
    {
        int w = raw.Width;
        int h = raw.Height;

        // Widest neighbourhood any stage reads, doubled for the guided filter's
        // blur-of-a-blur, plus a margin for Detail's small kernels (chroma NR
        // reaches 9 px at Colour 100; the sharpening Gaussian about the same).
        int regional = Math.Max(EdgeAwareLuma.RegionRadius(w, h),
                       Math.Max(LocalHighlights.RegionRadius(w, h), Effects.MaxRegionRadius(w, h)));
        int pad = 2 * regional + 16;

        // Every mask measures its delta against the *global* render, so that has
        // to survive the compositing — the planes themselves are being written
        // as we go. Snapshotting only the union of the mask areas rather than
        // the whole frame keeps the cost proportional to what the masks actually
        // cover, which at export resolution is the difference between a few MB
        // and a few hundred.
        var jobs = new List<(MaskSettings mask, PixelRect area)>();
        foreach (var mask in s.ActiveMasks)
        {
            var bounds = mask.Bounds(w, h);
            if (!bounds.IsEmpty) jobs.Add((mask, bounds));
        }
        if (jobs.Count == 0) return;

        int ux0 = w, uy0 = h, ux1 = 0, uy1 = 0;
        foreach (var (_, area) in jobs)
        {
            ux0 = Math.Min(ux0, area.X);
            uy0 = Math.Min(uy0, area.Y);
            ux1 = Math.Max(ux1, area.Right);
            uy1 = Math.Max(uy1, area.Bottom);
        }
        int uw = ux1 - ux0, uh = uy1 - uy0;
        if (uw <= 0 || uh <= 0) return;

        var baseR = new float[uw * uh];
        var baseG = new float[uw * uh];
        var baseB = new float[uw * uh];
        Parallel.For(0, uh, po, row =>
        {
            int src = (uy0 + row) * w + ux0;
            int dst = row * uw;
            Array.Copy(r, src, baseR, dst, uw);
            Array.Copy(g, src, baseG, dst, uw);
            Array.Copy(b, src, baseB, dst, uw);
        });

        foreach (var (mask, area) in jobs)
        {
            ct.ThrowIfCancellationRequested();

            // The region actually rendered: the mask's area plus enough context
            // for the spatial filters, clipped to the image.
            int px0 = Math.Max(0, area.X - pad);
            int py0 = Math.Max(0, area.Y - pad);
            int px1 = Math.Min(w, area.Right + pad);
            int py1 = Math.Min(h, area.Bottom + pad);
            int padW = px1 - px0;
            int padH = py1 - py0;
            if (padW <= 0 || padH <= 0) continue;

            // Once the padding has swallowed the whole frame — a large or
            // inverted mask — cropping is pure copying, so render in place.
            bool wholeFrame = px0 == 0 && py0 == 0 && padW == w && padH == h;
            var region = wholeFrame ? raw : raw.Crop(px0, py0, padW, padH);
            if (region is null) continue;

            var masked = mask.Adjustments.ApplyTo(s);
            // The crop carries no masks of its own: they are composited here, in
            // image space, and recursing would apply every mask inside every
            // other mask's region. Geometry goes the same way — it was already
            // resolved into the buffer this region was cut from, and re-applying
            // it would crop the crop.
            masked.Masks = new List<MaskSettings>();
            masked.Geometry = new GeometrySettings();

            var regionAirlight = airlight;
            var (_, _, mr, mg, mb) = RenderRgbPlanes(region, masked, po, ct, w, h,
                                                     ref regionAirlight, sceneFrame: raw);
            var weights = mask.Weights(w, h, area);

            Parallel.For(0, area.Height, po, row =>
            {
                int imageRow = (area.Y + row) * w;
                int regionRow = (area.Y + row - py0) * padW;
                int baseRow = (area.Y + row - uy0) * uw;
                int weightRow = row * area.Width;
                for (int col = 0; col < area.Width; col++)
                {
                    float t = weights[weightRow + col];
                    if (t <= 0f) continue;

                    int i = imageRow + area.X + col;
                    int j = regionRow + area.X + col - px0;
                    int k = baseRow + area.X + col - ux0;

                    // Add this mask's departure from the global render. Against
                    // the snapshot, not against r[i] — reading the running
                    // composite here is exactly what would make a later mask
                    // overwrite an earlier one instead of adding to it.
                    r[i] += (mr[j] - baseR[k]) * t;
                    g[i] += (mg[j] - baseG[k]) * t;
                    b[i] += (mb[j] - baseB[k]) * t;
                }
            });
        }
    }

    /// <summary>
    /// Per-pixel grain amplitude over a window, or <c>null</c> when no active mask
    /// carries a grain offset (the common case, kept allocation-free so a photo
    /// without local grain pays nothing).
    ///
    /// <para>Grain cannot ride the crop-and-crossfade path the other adjustments
    /// use — it is laid on after masking, keyed to absolute position — so a local
    /// grain offset instead varies the amplitude of the single global field. The
    /// map starts at the global amount and, for each grain-carrying mask, adds the
    /// mask's departure from that amount scaled by its weight, exactly the additive
    /// model <see cref="ComposeMasks"/> uses for tone. Because each contribution is
    /// a weight-in-0…1 lerp between two non-negative amounts, the result never goes
    /// negative and needs no clamp.</para>
    ///
    /// <para><paramref name="winX"/>/<paramref name="winY"/> place the window in
    /// full-frame coordinates so the tile path and the whole-frame path build the
    /// same values for the same pixels.</para>
    /// </summary>
    private static float[]? BuildLocalGrainAmounts(DevelopSettings s, in Effects.GrainParams field,
                                                   int fullW, int fullH,
                                                   int winX, int winY, int winW, int winH)
    {
        if (winW <= 0 || winH <= 0) return null;

        List<MaskSettings>? grainMasks = null;
        foreach (var mask in s.ActiveMasks)
            if (mask.Adjustments.Grain != 0.0) (grainMasks ??= new()).Add(mask);
        if (grainMasks is null) return null;

        float baseAmount = field.Amount;
        var amounts = new float[winW * winH];
        Array.Fill(amounts, baseAmount);

        foreach (var mask in grainMasks)
        {
            float maskAmount = Effects.GrainAmplitude(s.GrainAmount + mask.Adjustments.Grain);
            float delta = maskAmount - baseAmount;
            if (delta == 0f) continue;

            var bounds = mask.Bounds(fullW, fullH);
            int ax0 = Math.Max(bounds.X, winX);
            int ay0 = Math.Max(bounds.Y, winY);
            int ax1 = Math.Min(bounds.Right, winX + winW);
            int ay1 = Math.Min(bounds.Bottom, winY + winH);
            if (ax1 <= ax0 || ay1 <= ay0) continue;

            var rect = new PixelRect(ax0, ay0, ax1 - ax0, ay1 - ay0);
            var weights = mask.Weights(fullW, fullH, rect);

            for (int row = 0; row < rect.Height; row++)
            {
                int dst = (ay0 - winY + row) * winW + (ax0 - winX);
                int wsrc = row * rect.Width;
                for (int col = 0; col < rect.Width; col++)
                    amounts[dst + col] += weights[wsrc + col] * delta;
            }
        }

        return amounts;
    }

    /// <summary>
    /// The region-render counterpart to <see cref="ComposeMasks"/>: composite the
    /// masks onto a tile's planes rather than the whole frame's.
    ///
    /// <para>The planes passed in cover the padded tile
    /// <paramref name="padW"/>×<paramref name="padH"/> whose top-left is at
    /// (<paramref name="px0"/>, <paramref name="py0"/>) in the full developed
    /// frame. Each mask is still rendered over <i>its own</i> padded bounds cut
    /// from the full frame — so its spatial filters see the same neighbourhood the
    /// whole-frame render gives them — and its delta is added only where its area
    /// overlaps this tile. The accumulate-against-the-global-snapshot model is
    /// identical to <see cref="ComposeMasks"/>, which is why a tile with masks
    /// still matches the export to the byte.</para>
    /// </summary>
    private static void ComposeMasksRegion(LinearRawImage developed, DevelopSettings s,
                                           float[] r, float[] g, float[] b,
                                           int px0, int py0, int padW, int padH,
                                           ParallelOptions po, CancellationToken ct,
                                           Effects.Airlight? airlight)
    {
        int fw = developed.Width, fh = developed.Height;

        var jobs = new List<(MaskSettings mask, PixelRect area)>();
        foreach (var mask in s.ActiveMasks)
        {
            var bounds = mask.Bounds(fw, fh);
            if (!bounds.IsEmpty) jobs.Add((mask, bounds));
        }
        if (jobs.Count == 0) return;

        int regional = Math.Max(EdgeAwareLuma.RegionRadius(fw, fh),
                       Math.Max(LocalHighlights.RegionRadius(fw, fh), Effects.MaxRegionRadius(fw, fh)));
        int pad = 2 * regional + 16;

        // Masks measure their departure from the *global* render, so snapshot the
        // tile's global planes before any mask writes into them. Tile-sized, so a
        // few MB even at 1:1.
        var baseR = (float[])r.Clone();
        var baseG = (float[])g.Clone();
        var baseB = (float[])b.Clone();

        foreach (var (mask, area) in jobs)
        {
            ct.ThrowIfCancellationRequested();

            // Where this mask's effect overlaps the tile — nothing to do if disjoint.
            int ax0 = Math.Max(area.X, px0);
            int ay0 = Math.Max(area.Y, py0);
            int ax1 = Math.Min(area.Right, px0 + padW);
            int ay1 = Math.Min(area.Bottom, py0 + padH);
            if (ax1 <= ax0 || ay1 <= ay0) continue;

            // The mask's own padded render region, cut from the full frame exactly
            // as ComposeMasks does.
            int mpx0 = Math.Max(0, area.X - pad);
            int mpy0 = Math.Max(0, area.Y - pad);
            int mpx1 = Math.Min(fw, area.Right + pad);
            int mpy1 = Math.Min(fh, area.Bottom + pad);
            int mpadW = mpx1 - mpx0, mpadH = mpy1 - mpy0;
            if (mpadW <= 0 || mpadH <= 0) continue;

            bool wholeFrame = mpx0 == 0 && mpy0 == 0 && mpadW == fw && mpadH == fh;
            var mregion = wholeFrame ? developed : developed.Crop(mpx0, mpy0, mpadW, mpadH);
            if (mregion is null) continue;

            var masked = mask.Adjustments.ApplyTo(s);
            masked.Masks = new List<MaskSettings>();
            masked.Geometry = new GeometrySettings();

            var maskAir = airlight;
            var (_, _, mr, mg, mb) = RenderRgbPlanes(mregion, masked, po, ct, fw, fh,
                                                     ref maskAir, sceneFrame: developed);
            var weights = mask.Weights(fw, fh, area);

            Parallel.For(ay0, ay1, po, row =>
            {
                int weightRow = (row - area.Y) * area.Width;
                int tileRow = (row - py0) * padW;
                int maskRow = (row - mpy0) * mpadW;
                for (int col = ax0; col < ax1; col++)
                {
                    float t = weights[weightRow + (col - area.X)];
                    if (t <= 0f) continue;

                    int k = tileRow + (col - px0);       // tile plane (and snapshot) index
                    int j = maskRow + (col - mpx0);      // mask render index

                    r[k] += (mr[j] - baseR[k]) * t;
                    g[k] += (mg[j] - baseG[k]) * t;
                    b[k] += (mb[j] - baseB[k]) * t;
                }
            });
        }
    }

    /// <summary>
    /// The shared tone pipeline: per-channel gain → EV-space H/S/C → endpoint
    /// remap → display LUT → luma/chroma decompose → chroma denoise. Returns
    /// the post-blur planes in 0..255 perceptual units (luma) and Rec.709
    /// chroma differences. Both <see cref="Render"/> and
    /// <see cref="RenderExport"/> finish from here, so preview and export run
    /// identical maths and differ only in the final colour-scale/quantise.
    /// </summary>
    private static (int w, int h, float[] luma, float[] cb, float[] cr)
        DecomposeToPlanes(LinearRawImage raw, DevelopSettings s, ParallelOptions po,
                          CancellationToken ct, int contextW, int contextH,
                          ref Effects.Airlight? airlight, LinearRawImage? sceneFrame = null)
    {
        int w = raw.Width;
        int h = raw.Height;
        int n = w * h;
        ushort[] src = raw.Pixels;

        // Regional filters size themselves from the image this buffer belongs to,
        // which is the buffer itself except when rendering a mask's crop.
        if (contextW <= 0) contextW = w;
        if (contextH <= 0) contextH = h;

        // ── Per-channel linear gain: exposure × white balance ──
        // White balance comes from real illuminant chromaticities (see
        // WhiteBalance): Temperature names a Kelvin on the blackbody/daylight
        // locus, Tint displaces it along Duv, and the pair becomes von Kries
        // gains that preserve luma. Staying diagonal is a requirement, not a
        // simplification — HighlightReconstruction below is handed these same
        // three numbers as each channel's clipping point.
        double expGain = BasicTone.ExposureGain(s.Exposure);
        var (wbR, wbG, wbB) = WhiteBalance.Gains(s.EffectiveKelvin, s.Tint);
        double gainR = expGain * wbR;
        double gainG = expGain * wbG;
        double gainB = expGain * wbB;

        // ── Highlights/Shadows/Contrast/Whites/Blacks: EV-space, linear ──
        // These five run in scene-linear space *before* the display curve,
        // exactly RAWR's recommended order (… → Exposure → Highlights/Shadows
        // → Contrast → Whites/Blacks endpoints → output transform). All slider
        // strengths are hoisted to per-render constants; at neutral every flag
        // is false so this is a true no-op and the render is byte-identical to
        // the original camera-matched look (which now lives wholly in the LUT).
        //
        // Per-slider Version selects which BasicTone formula runs: v1 is RAWR's
        // original math, v2 the darktable-inspired global model, v3 the edge-
        // aware (Lightroom-feel) variant that reads a regional-luminance plane.
        // When every slider in a block is v1 we take the combined fast path
        // (one luminance / log / pow per pixel for HSL; one (lr−bk)/span for
        // ends) which is byte-identical to the original render. As soon as
        // any v2/v3 appears the block falls back to per-slider passes; v3 in
        // addition triggers the optional Pass 0 below.
        double hl = s.Highlights;
        double sh = s.Shadows;
        double co = s.Contrast;
        double wh = s.Whites;
        double bk = s.Blacks;
        double contrastSlope = BasicTone.ContrastSlope(co);
        double ex = s.Exposure;
        // Highlights is now a SPATIAL operator (edge-aware base/detail local tone
        // mapping — see LocalHighlights), so it can't ride the per-pixel EV pass.
        // When it's engaged we materialise gained linear RGB planes, run it once
        // over the whole image, then feed those planes into Pass 1.
        bool doLocalHighlights = hl != 0.0;
        bool doEv = sh != 0.0 || co != 0.0;
        bool doEnds = wh != 0.0 || bk != 0.0;
        // Build the regional luminance plane whenever Shadows/Blacks are active;
        // those v3 sliders consult evBase. Highlights no longer does, and neither
        // does Whites since v4 made it global.
        bool needsEvBase = sh != 0.0 || bk != 0.0;

        // Rebuilding clipped channels only changes the render when something
        // downstream pulls highlights back below white — otherwise the recovered
        // headroom clips to white exactly as the unreconstructed value did, and we
        // would pay for it to be invisible. So gate on the sliders that pull down.
        // Negative Exposure is on this list because that is precisely how Lightroom
        // recovers a blown sky when you pull the exposure back.
        bool doReconstruct = hl < 0.0 || ex < 0.0 || wh < 0.0 || co < 0.0;

        // Dehaze is a scene-referred correction — the haze model it inverts is
        // only valid on linear light — so it also needs the planes, and it runs
        // before every slider that reads them.
        bool doDehaze = s.Dehaze != 0.0;

        // Any of these stages needs the gained planes materialised. At neutral
        // all are false, so the cheap inline-gain path survives and the baseline
        // render stays byte-identical.
        bool needPlanes = doLocalHighlights || doReconstruct || doDehaze;
        const double inv65535 = 1.0 / 65535.0;

        // ── Display LUT: normalised linear → fractional 8-bit perceptual ──
        // Pure camera-matched output transform (sRGB → midtone match → base
        // S). No slider terms — those are the EV-space block above. The table is
        // settings-independent, so it is built once (DisplayLut) and shared by
        // every render, mask and export pass rather than recomputed each call.
        float[] lut = DisplayLut;

        // ── Pass H (conditional): the stages that need whole planes ──
        // Materialise the gained scene-linear RGB planes, rebuild whatever the
        // sensor clipped, then run the edge-aware local-tone Highlights. Pass 1
        // reads these planes instead of re-gaining from src.
        //
        // The order is the pipeline's tone order and it matters: reconstruction has
        // to see the raw gained values so the clip sits exactly at each channel's
        // own gain, and it is conceptually part of the decode, so it precedes every
        // slider that reads those planes.
        float[]? rLin = null, gLin = null, bLin = null;
        if (needPlanes)
        {
            rLin = new float[n]; gLin = new float[n]; bLin = new float[n];
            Parallel.For(0, h, po, yy =>
            {
                int rowI = yy * w;
                int srcIdx = rowI * 3;
                for (int x = 0; x < w; x++)
                {
                    int i = rowI + x;
                    rLin[i] = (float)(src[srcIdx]     * gainR * inv65535);
                    gLin[i] = (float)(src[srcIdx + 1] * gainG * inv65535);
                    bLin[i] = (float)(src[srcIdx + 2] * gainB * inv65535);
                    srcIdx += 3;
                }
            });

            // Recover the channels LibRaw clipped at the sensor white level, so the
            // sliders below have real above-white detail to bring down rather than a
            // flat plateau. The clip lands at each channel's own gain, not at 1.0,
            // because white balance and exposure are already folded into the planes.
            if (doReconstruct)
                HighlightReconstruction.Apply(rLin, gLin, bLin, w, h,
                                              gainR, gainG, gainB, null, po);

            // Dehaze before the tone sliders: haze is part of what the camera
            // recorded, so removing it belongs with the decode, and Highlights
            // or Contrast applied to a still-hazy image would be spending their
            // travel on the haze rather than on the scene.
            if (doDehaze)
            {
                // Estimated once per render, on the full frame and before the
                // gains. A mask's crop is handed the value already computed
                // rather than deriving its own from a fragment of the picture,
                // and because it is stored un-gained it stays correct even when
                // the mask's own Exposure or white balance differ from the
                // global ones — see Effects.Airlight.
                airlight ??= Effects.EstimateAirlightFromSensor(src, w, h);
                Effects.ApplyDehaze(rLin, gLin, bLin, w, h, s.Dehaze,
                                    airlight.Value.Gained(gainR, gainG, gainB),
                                    contextW, contextH, po);
            }

            if (doLocalHighlights)
            {
                // Lightroom's Highlights response scales with how dark the photograph
                // is overall (see LocalHighlights.Options.SceneShadowFraction), so the
                // statistic must come from the whole frame — sceneFrame when this
                // buffer is a crop — under this render's own gains. Measured on the
                // sensor pixels (pre-reconstruction, pre-dehaze): reconstruction only
                // touches near-clip pixels the deep-shadow count never sees, and
                // keying it pre-dehaze means dragging Dehaze cannot jump the
                // Highlights response.
                var frame = sceneFrame ?? raw;
                double fd = LocalHighlights.EstimateSceneShadowFraction(
                    frame.Pixels, frame.Width, frame.Height, gainR, gainG, gainB);
                LocalHighlights.Apply(rLin, gLin, bLin, w, h,
                    new LocalHighlights.Options
                    {
                        Highlights = hl,
                        Radius = LocalHighlights.RegionRadius(contextW, contextH),
                        SceneShadowFraction = fd,
                    }, po);
            }
        }

        // ── Pass 0 (conditional): regional luminance plane for v3 sliders ──
        // Build the post-gain (and post-Pass-H, if any) Y plane, then run
        // EdgeAwareLuma's guided filter to get evBase[i] = log2(Y_region /
        // MiddleGray). v3 Shadows/Blacks read this for their masks.
        // Skipped entirely when neither of those sliders is active.
        float[]? evBase = null;
        if (needsEvBase)
        {
            var yLinear = new float[n];
            Parallel.For(0, h, po, yy =>
            {
                int rowI = yy * w;
                int srcIdx = rowI * 3;
                for (int x = 0; x < w; x++)
                {
                    int i = rowI + x;
                    double lr, lg, lb;
                    if (needPlanes) { lr = rLin![i]; lg = gLin![i]; lb = bLin![i]; }
                    else
                    {
                        lr = src[srcIdx]     * gainR * inv65535;
                        lg = src[srcIdx + 1] * gainG * inv65535;
                        lb = src[srcIdx + 2] * gainB * inv65535;
                    }
                    yLinear[i] = (float)BasicTone.Luminance(lr, lg, lb);
                    srcIdx += 3;
                }
            });
            evBase = EdgeAwareLuma.BuildEvBase(yLinear, w, h, po,
                                               EdgeAwareLuma.RegionRadius(contextW, contextH));
        }

        // ── Pass 1: gain → EV tone → endpoints → LUT, decompose luma/chroma ──
        float[] luma = new float[n];
        float[] cb = new float[n]; // B − Y
        float[] cr = new float[n]; // R − Y

        Parallel.For(0, h, po, yy =>
        {
            int rowI = yy * w;
            int srcIdx = rowI * 3;
            for (int x = 0; x < w; x++)
            {
                int idx = rowI + x;
                // Scene-linear, normalised so 1.0 = sensor saturation. Kept
                // unclamped through the tone math so highlight detail above
                // the old 65535 ceiling survives until Whites decides it.
                // Reconstruction, the Exposure shoulder and Highlights, when
                // active, are already baked into rLin/gLin/bLin (Pass H).
                double lr, lg, lb;
                if (needPlanes) { lr = rLin![idx]; lg = gLin![idx]; lb = bLin![idx]; }
                else
                {
                    lr = src[srcIdx]     * gainR * inv65535;
                    lg = src[srcIdx + 1] * gainG * inv65535;
                    lb = src[srcIdx + 2] * gainB * inv65535;
                }

                // evB only consulted by the v3 Shadows/Whites/Blacks masks.
                double evB = evBase != null ? evBase[idx] : 0.0;

                if (doEv)
                {
                    if (sh != 0.0) BasicTone.ApplyShadows(ref lr, ref lg, ref lb, sh, evB);
                    if (co != 0.0) BasicTone.ApplyContrast(ref lr, ref lg, ref lb, co);
                }

                if (doEnds)
                {
                    if (wh != 0.0) BasicTone.ApplyWhites(ref lr, ref lg, ref lb, wh);
                    if (bk != 0.0) BasicTone.ApplyBlacks(ref lr, ref lg, ref lb, bk, evB);
                }

                int ir = (int)(lr * 65535.0 + 0.5);
                int ig = (int)(lg * 65535.0 + 0.5);
                int ib = (int)(lb * 65535.0 + 0.5);
                if (ir < 0) ir = 0; else if (ir > 65535) ir = 65535;
                if (ig < 0) ig = 0; else if (ig > 65535) ig = 65535;
                if (ib < 0) ib = 0; else if (ib > 65535) ib = 65535;

                float pr = lut[ir];
                float pg = lut[ig];
                float pb = lut[ib];
                float y = 0.2126f * pr + 0.7152f * pg + 0.0722f * pb;
                luma[idx] = y;
                cb[idx] = pb - y;
                cr[idx] = pr - y;
                srcIdx += 3;
            }
        });

        // ── Detail: noise reduction, then sharpening ──
        // Chroma first — this is the box blur that used to be hard-coded here at
        // radius 2, now the Colour slider, which reproduces exactly that at its
        // default. Luminance NR precedes sharpening so the unsharp mask is not
        // handed grain to amplify.
        Detail.ReduceColorNoise(cb, cr, w, h, s.ColorNoiseReduction, po);

        var luminanceNr = Detail.BuildLuminance(
            s.LuminanceNoiseReduction, s.LuminanceNoiseDetail, s.LuminanceNoiseContrast);
        Detail.ReduceLuminanceNoise(luma, w, h, luminanceNr, po);

        // Texture then Clarity — fine scale before broad, so Clarity's base sees
        // whatever Texture decided the detail should be. Both precede sharpening
        // for the same reason noise reduction does: the unsharp mask should be
        // the last thing to touch the luminance, not be handed micro-contrast
        // that another operator has just amplified.
        Effects.ApplyTexture(luma, w, h, s.Texture, contextW, contextH, po);
        Effects.ApplyClarity(luma, w, h, s.Clarity, contextW, contextH, po);

        var sharpen = Detail.BuildSharpen(
            s.Sharpening, s.SharpenRadius, s.SharpenDetail, s.SharpenMasking);
        Detail.Sharpen(luma, w, h, sharpen, po);

        return (w, h, luma, cb, cr);
    }

    /// <summary>
    /// 65536-entry table: normalised-linear value → fractional 8-bit
    /// perceptual [0..255]. It composes <see cref="BasicTone.DisplayCurve"/>
    /// (RAWR's original camera-matched transform: sRGB encode → midtone lift →
    /// gentle base S) with <see cref="BasicTone.LightroomMatch"/> (the
    /// empirical calibration that pulls the result onto Lightroom's default
    /// look). The slider tone work happens upstream in EV space, so this table
    /// is still a pure, settings-independent output transform — at neutral the
    /// whole pipeline reduces to exactly this composed curve, so the baseline
    /// render now matches Lightroom rather than the old camera look.
    /// Fractional output is what lets the dither resolve banding into gradient.
    /// </summary>
    /// <remarks>
    /// The table has no inputs, so it is computed once into this static and
    /// handed out read-only to every caller. The CLR guarantees thread-safe
    /// one-time initialisation of a static readonly field, so no locking is
    /// needed, and nothing in the pipeline writes to the array (it is only
    /// indexed in Pass 1), so sharing a single instance is safe. A three-mask
    /// edit used to rebuild this 65 536-entry transcendental table four times
    /// per debounce tick; now it never rebuilds after process start.
    /// </remarks>
    private static readonly float[] DisplayLut = BuildDisplayLut();

    private static float[] BuildDisplayLut()
    {
        var lut = new float[65536];
        for (int i = 0; i < 65536; i++)
            lut[i] = (float)(BasicTone.LightroomMatch(BasicTone.DisplayCurve(i / 65535.0)) * 255.0);
        return lut;
    }

}
