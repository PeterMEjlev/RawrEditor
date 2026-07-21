namespace Rawr.Develop;

/// <summary>
/// Rebuilds RAW channels that ran into the sensor's clipping point, so the tone
/// stage has something above display white left to bring back.
///
/// <para>This is the stage that makes a Highlights slider able to recover blown
/// detail at all. <see cref="Rawr.Raw.RawDecoder"/> hands us a buffer that LibRaw
/// hard-clipped at the sensor white level, so a blown region arrives as a
/// perfectly flat plateau: every channel pinned to the clip, luminance identical
/// across the whole area. No tone curve — however clever, however local — can pull
/// structure out of a constant. It can only make the plateau a darker plateau,
/// which is exactly the "grey blob" that a naive highlight recovery produces.</para>
///
/// <para>The information is not, however, entirely gone. A channel clips when
/// <i>that</i> channel saturates, and the three channels rarely saturate together:
/// after white balance a warm subject clips red first, a neutral one clips green
/// first. So most "blown" pixels still carry one or two honest channels, and those
/// channels still hold the luminance variation that reads as detail. What has been
/// lost is the clipped channel's value — and with it the pixel's colour, which is
/// why unreconstructed recovery desaturates.</para>
///
/// <para><b>Method.</b> Standard colour-propagation highlight reconstruction (the
/// same family as RawTherapee's colour propagation and darktable's colour
/// reconstruction; this is an independent implementation of the published idea, not
/// ported code):</para>
/// <list type="number">
/// <item>Classify each channel per pixel as valid or clipped, with a soft ramp so
///       the near-clip region blends instead of switching (preview buffers are
///       box-averaged, which softens the plateau edge — a hard test would behave
///       differently at preview and export sizes).</item>
/// <item>Build a low-frequency guide for each channel over the pixels where
///       <i>all</i> channels are valid. Sharing one support across the three guides
///       is what makes <c>guide[c] / guide[m]</c> a genuine local hue ratio rather
///       than an artefact of the channels having been measured over different sets
///       of pixels.</item>
/// <item>Fill the guide across the clipped regions by weighted push-pull pyramid
///       interpolation, so holes of any size get plausible values — a single blur
///       only propagates as far as its radius, which is useless for a large sky.</item>
/// <item>Reconstruct each clipped channel as <c>valid_channel × local hue ratio</c>,
///       never darkening it (we know the truth is at or above the clip) and capping
///       the result at <see cref="Options.MaxHeadroomStops"/>.</item>
/// <item>Fill fully-clipped cores — pixels where no channel survived — from the
///       now-reconstructed rim, again by push-pull. Because that rim sits above the
///       clip, a blown core comes back as a smooth dome rather than a flat disc.</item>
/// </list>
///
/// <para>The guide is deliberately low-frequency, so it is built on a
/// <see cref="Options.GuideDownscale"/>-reduced grid and sampled back bilinearly
/// rather than being carried at full resolution. That is not an approximation of
/// convenience — a full-resolution guide would reproduce the pixel it is meant to
/// be a reference for — and it keeps the stage fast enough to sit under a live
/// slider drag.</para>
///
/// <para>Values above the clip are the whole point: the planes are float and
/// unclamped, so a recovered highlight leaves here at 1.4 or 2.0 and it is
/// <see cref="LocalHighlights"/>'s job to decide how much of that headroom to bring
/// down into range. Running this stage on its own does not change the rendered
/// image — anything above 1.0 still clips to white at the display transform — which
/// is why the neutral render is unaffected.</para>
/// </summary>
public static class HighlightReconstruction
{
    /// <summary>Tunable parameters. The defaults are what the develop pipeline uses.</summary>
    public sealed class Options
    {
        /// <summary>Fraction of the clip level where a channel starts being treated
        /// as suspect. Below this it is fully trusted.</summary>
        public double ClipStartFraction = 0.92;

        /// <summary>Fraction of the clip level at which a channel counts as fully
        /// clipped and carries no information.</summary>
        public double ClipEndFraction = 0.999;

        /// <summary>Ceiling on reconstruction, in stops above the clip. Keeps a bad
        /// hue ratio in a strongly-coloured region from producing a runaway value.</summary>
        public double MaxHeadroomStops = 2.5;

        /// <summary>Push-pull blend constant: how much valid coverage a pyramid level
        /// needs before its own average outweighs the coarser level's estimate.</summary>
        public double Confidence = 0.05;

        /// <summary>Linear reduction factor for the grid the guide is built on. The
        /// guide only ever supplies a local hue <i>ratio</i>, which is smooth, so a
        /// quarter-resolution grid loses nothing and cuts the pyramid work ~16x.</summary>
        public int GuideDownscale = 4;

        /// <summary>Pyramid level (on the reduced grid) the hue guide is read from.</summary>
        public int GuideLevel = 0;

        /// <summary>Pyramid level (on the reduced grid) the fully-clipped-core fill is
        /// read from. Coarser than <see cref="GuideLevel"/> so large blown areas fill
        /// smoothly rather than picking up their own rim's texture.</summary>
        public int CoreGuideLevel = 1;
    }

    /// <summary>
    /// Reconstruct clipped channels in place across planar linear-RGB float buffers.
    ///
    /// <para><paramref name="clipR"/>/<paramref name="clipG"/>/<paramref name="clipB"/>
    /// are where the sensor's clipping point lands <i>in these buffers</i>. The caller
    /// has usually folded white balance and exposure into the planes already, so the
    /// clip sits at each channel's own gain rather than at 1.0 — passing 1.0 blindly
    /// would look for clipping in the wrong place on every channel but the reference
    /// one.</para>
    /// </summary>
    /// <returns>True if any clipping was found and reconstructed.</returns>
    public static bool Apply(float[] r, float[] g, float[] b, int w, int h,
                             double clipR, double clipG, double clipB,
                             Options? options = null, ParallelOptions? po = null)
    {
        options ??= new Options();
        po ??= new ParallelOptions();
        int n = w * h;
        if (w <= 0 || h <= 0) return false;
        if (r.Length < n || g.Length < n || b.Length < n)
            throw new ArgumentException("Each RGB plane must have at least w*h elements.");

        var planes = new[] { r, g, b };
        var clip = new[] { clipR, clipG, clipB };
        for (int c = 0; c < 3; c++)
            if (!(clip[c] > 0.0) || double.IsNaN(clip[c])) return false;

        // ── 1. Soft per-channel validity: 1 well below the clip, 0 at it ──
        var valid = new float[3][];
        for (int c = 0; c < 3; c++) valid[c] = new float[n];
        var rowClipped = new bool[h];

        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            bool clipped = false;
            for (int c = 0; c < 3; c++)
            {
                float[] p = planes[c];
                float[] v = valid[c];
                double lo = clip[c] * options.ClipStartFraction;
                double hi = clip[c] * options.ClipEndFraction;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    float vi = (float)(1.0 - BasicTone.SmoothStep(lo, hi, p[i]));
                    v[i] = vi;
                    if (vi < 1f) clipped = true;
                }
            }
            rowClipped[yy] = clipped;
        });

        bool anyClipped = false;
        for (int y = 0; y < h && !anyClipped; y++) anyClipped = rowClipped[y];
        if (!anyClipped) return false;   // nothing blown ⇒ exact no-op.

        // Shared support: only pixels whose channels are *all* honest contribute to
        // the guides, so the three guides are measured over identical pixels and
        // their ratios mean something.
        var support = new float[n];
        Parallel.For(0, n, po, i =>
        {
            float v = valid[0][i];
            if (valid[1][i] < v) v = valid[1][i];
            if (valid[2][i] < v) v = valid[2][i];
            support[i] = v;
        });

        // ── 2–3. Low-frequency hue guide per channel, holes filled by push-pull ──
        int scale = Math.Max(1, options.GuideDownscale);
        int cw = (w + scale - 1) / scale;
        int ch = (h + scale - 1) / scale;
        var (cNum, cDen) = Downscale(planes, support, w, h, cw, ch, scale, po);
        float[][] guide = PushPullGuides(cNum, cDen, cw, ch,
                                         options.GuideLevel, (float)options.Confidence, po);

        // ── 4. Rebuild clipped channels from a surviving one via the hue ratio ──
        var maxVal = new double[3];
        for (int c = 0; c < 3; c++) maxVal[c] = clip[c] * Math.Pow(2.0, options.MaxHeadroomStops);

        // 1 where the pixel kept at least one usable channel, so it now holds a
        // trustworthy value; 0 for fully-clipped cores, handled in step 5.
        var anchor = new float[n];
        double invScale = 1.0 / scale;

        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            double fy = (yy + 0.5) * invScale - 0.5;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float v0 = valid[0][i], v1 = valid[1][i], v2 = valid[2][i];
                if (v0 >= 1f && v1 >= 1f && v2 >= 1f) { anchor[i] = 1f; continue; }

                // Reconstruct against the most trustworthy surviving channel.
                int m = 0; float vm = v0;
                if (v1 > vm) { m = 1; vm = v1; }
                if (v2 > vm) { m = 2; vm = v2; }
                if (vm <= 0f) continue;          // nothing survived — step 5 fills it.

                anchor[i] = 1f;

                double fx = (x + 0.5) * invScale - 0.5;
                double gm = Sample(guide[m], cw, ch, fx, fy);
                if (!(gm > 1e-6)) continue;
                double xm = planes[m][i];

                for (int c = 0; c < 3; c++)
                {
                    float vc = valid[c][i];
                    if (vc >= 1f) continue;

                    double est = xm * (Sample(guide[c], cw, ch, fx, fy) / gm);
                    double cur = planes[c][i];
                    if (double.IsNaN(est) || double.IsInfinity(est)) continue;
                    if (est < cur) est = cur;              // a clipped channel is never darker than the clip
                    if (est > maxVal[c]) est = maxVal[c];
                    planes[c][i] = (float)(cur + (est - cur) * (1.0 - vc));
                }
            }
        });

        // ── 5. Fill fully-clipped cores from the reconstructed rim ──
        bool anyCore = false;
        var rowCore = new bool[h];
        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            for (int x = 0; x < w; x++)
                if (anchor[row + x] == 0f) { rowCore[yy] = true; break; }
        });
        for (int y = 0; y < h && !anyCore; y++) anyCore = rowCore[y];

        if (anyCore)
        {
            var (coreNum, coreDen) = Downscale(planes, anchor, w, h, cw, ch, scale, po);
            float[][] fill = PushPullGuides(coreNum, coreDen, cw, ch,
                                            options.CoreGuideLevel, (float)options.Confidence, po);

            Parallel.For(0, h, po, yy =>
            {
                int row = yy * w;
                double fy = (yy + 0.5) * invScale - 0.5;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    if (anchor[i] != 0f) continue;
                    double fx = (x + 0.5) * invScale - 0.5;
                    for (int c = 0; c < 3; c++)
                    {
                        double est = Sample(fill[c], cw, ch, fx, fy);
                        double cur = planes[c][i];
                        if (double.IsNaN(est) || double.IsInfinity(est)) continue;
                        if (est < cur) est = cur;
                        if (est > maxVal[c]) est = maxVal[c];
                        planes[c][i] = (float)est;
                    }
                }
            });
        }

        return true;
    }

    /// <summary>
    /// Accumulate each channel's weighted sum and the weight itself into a reduced
    /// grid, normalised so the weight stays in 0..1 and <see cref="Options.Confidence"/>
    /// keeps its meaning regardless of the reduction factor.
    /// </summary>
    private static (float[][] num, float[] den) Downscale(
        float[][] planes, float[] weight, int w, int h, int cw, int ch, int scale, ParallelOptions po)
    {
        var num = new float[3][];
        for (int c = 0; c < 3; c++) num[c] = new float[cw * ch];
        var den = new float[cw * ch];
        double norm = 1.0 / (scale * scale);

        Parallel.For(0, ch, po, cy =>
        {
            int y0 = cy * scale, y1 = Math.Min(y0 + scale, h);
            for (int cx = 0; cx < cw; cx++)
            {
                int x0 = cx * scale, x1 = Math.Min(x0 + scale, w);
                double d = 0, s0 = 0, s1 = 0, s2 = 0;
                for (int y = y0; y < y1; y++)
                {
                    int row = y * w;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x;
                        float wv = weight[i];
                        d += wv;
                        s0 += planes[0][i] * wv;
                        s1 += planes[1][i] * wv;
                        s2 += planes[2][i] * wv;
                    }
                }
                int ci = cy * cw + cx;
                den[ci] = (float)(d * norm);
                num[0][ci] = (float)(s0 * norm);
                num[1][ci] = (float)(s1 * norm);
                num[2][ci] = (float)(s2 * norm);
            }
        });

        return (num, den);
    }

    /// <summary>Bilinear sample of a reduced-grid plane, clamped at the edges.</summary>
    private static double Sample(float[] src, int sw, int sh, double fx, double fy)
    {
        if (fx < 0.0) fx = 0.0; else if (fx > sw - 1) fx = sw - 1;
        if (fy < 0.0) fy = 0.0; else if (fy > sh - 1) fy = sh - 1;
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = x0 + 1 < sw ? x0 + 1 : x0;
        int y1 = y0 + 1 < sh ? y0 + 1 : y0;
        double tx = fx - x0, ty = fy - y0;
        double a = src[y0 * sw + x0], bb = src[y0 * sw + x1];
        double c = src[y1 * sw + x0], d = src[y1 * sw + x1];
        return (a * (1.0 - tx) + bb * tx) * (1.0 - ty) + (c * (1.0 - tx) + d * tx) * ty;
    }

    /// <summary>
    /// Weighted push-pull pyramid interpolation of several value planes sharing one
    /// weight plane. Each output is the weighted average of its input, with regions
    /// of zero weight filled by interpolation from however far away valid data lives
    /// — which is what lets a large blown area be filled at all.
    ///
    /// <para>The pull stops at <paramref name="guideLevel"/> and any remaining levels
    /// are plain expansions, which is how the core fill is made coarser than the hue
    /// guide without building a second pyramid.</para>
    /// </summary>
    private static float[][] PushPullGuides(float[][] nums, float[] den, int w, int h,
                                            int guideLevel, float confidence, ParallelOptions po)
    {
        var dw = new List<int> { w };
        var dh = new List<int> { h };
        while (Math.Min(dw[^1], dh[^1]) > 2 && dw.Count < 14)
        {
            dw.Add((dw[^1] + 1) / 2);
            dh.Add((dh[^1] + 1) / 2);
        }
        int levels = dw.Count;
        int stop = Math.Clamp(guideLevel, 0, levels - 1);
        int top = levels - 1;

        // Weight pyramid is shared by every plane — build it once.
        var pd = new float[levels][];
        pd[0] = den;
        for (int l = 1; l < levels; l++)
            pd[l] = Reduce(pd[l - 1], dw[l - 1], dh[l - 1], dw[l], dh[l], po);

        var results = new float[nums.Length][];
        for (int k = 0; k < nums.Length; k++)
        {
            var pn = new float[levels][];
            pn[0] = nums[k];
            for (int l = 1; l < levels; l++)
                pn[l] = Reduce(pn[l - 1], dw[l - 1], dh[l - 1], dw[l], dh[l], po);

            // Coarsest level: whatever weight reached it is all we have.
            float[] nt = pn[top], dt = pd[top];
            var cur = new float[dw[top] * dh[top]];
            Parallel.For(0, cur.Length, po, i => cur[i] = dt[i] > 1e-8f ? nt[i] / dt[i] : 0f);

            // Pull down, preferring each level's own average where it has coverage.
            for (int l = top - 1; l >= stop; l--)
            {
                var up = Expand(cur, dw[l + 1], dh[l + 1], dw[l], dh[l], po);
                int len = dw[l] * dh[l];
                var next = new float[len];
                float[] nl = pn[l], dl = pd[l];
                Parallel.For(0, len, po, i =>
                {
                    float d = dl[i];
                    float a = d / (d + confidence);
                    float own = d > 1e-8f ? nl[i] / d : 0f;
                    next[i] = a * own + (1f - a) * up[i];
                });
                cur = next;
            }

            // Remaining levels: interpolate only, keeping the result smooth.
            for (int l = stop - 1; l >= 0; l--)
                cur = Expand(cur, dw[l + 1], dh[l + 1], dw[l], dh[l], po);

            results[k] = cur;
        }

        return results;
    }

    // ── Burt–Adelson pyramid primitives ───────────────────────────────────────

    // Blur (binomial 5-tap) then subsample by 2.
    private static float[] Reduce(float[] src, int sw, int sh, int dwl, int dhl, ParallelOptions po)
    {
        var blurred = Binomial5(src, sw, sh, 1f / 16f, po);
        var dst = new float[dwl * dhl];
        Parallel.For(0, dhl, po, j =>
        {
            int sy = Math.Min(2 * j, sh - 1);
            for (int i = 0; i < dwl; i++)
            {
                int sx = Math.Min(2 * i, sw - 1);
                dst[j * dwl + i] = blurred[sy * sw + sx];
            }
        });
        return dst;
    }

    // Upsample by 2 (zero-insert) then blur with the doubled binomial kernel.
    private static float[] Expand(float[] src, int sw, int sh, int dwl, int dhl, ParallelOptions po)
    {
        var up = new float[dwl * dhl];
        Parallel.For(0, sh, po, j =>
        {
            int yy = 2 * j;
            if (yy >= dhl) return;
            for (int i = 0; i < sw; i++)
            {
                int xx = 2 * i;
                if (xx < dwl) up[yy * dwl + xx] = src[j * sw + i];
            }
        });
        return Binomial5(up, dwl, dhl, 1f / 8f, po);
    }

    // Separable [1,4,6,4,1] convolution, clamp-extend edges, with a given norm.
    private static float[] Binomial5(float[] src, int w, int h, float norm, ParallelOptions po)
    {
        var tmp = new float[w * h];
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int x0 = x - 2 < 0 ? 0 : x - 2;
                int x1 = x - 1 < 0 ? 0 : x - 1;
                int x3 = x + 1 >= w ? w - 1 : x + 1;
                int x4 = x + 2 >= w ? w - 1 : x + 2;
                tmp[row + x] = (src[row + x0] + 4f * src[row + x1] + 6f * src[row + x]
                              + 4f * src[row + x3] + src[row + x4]) * norm;
            }
        });
        var dst = new float[w * h];
        Parallel.For(0, w, po, x =>
        {
            for (int y = 0; y < h; y++)
            {
                int y0 = y - 2 < 0 ? 0 : y - 2;
                int y1 = y - 1 < 0 ? 0 : y - 1;
                int y3 = y + 1 >= h ? h - 1 : y + 1;
                int y4 = y + 2 >= h ? h - 1 : y + 2;
                dst[y * w + x] = (tmp[y0 * w + x] + 4f * tmp[y1 * w + x] + 6f * tmp[y * w + x]
                               + 4f * tmp[y3 * w + x] + tmp[y4 * w + x]) * norm;
            }
        });
        return dst;
    }
}
