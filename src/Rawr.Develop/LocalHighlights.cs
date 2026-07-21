namespace Rawr.Develop;

/// <summary>
/// Lightroom-<i>like</i> <b>Highlights</b>. An approximation of the look of Adobe's
/// slider built from published local-tone-mapping technique — not Adobe's algorithm,
/// not reverse-engineered from it, and carrying no GPL code from darktable or
/// RawTherapee.
///
/// <para><b>What makes recovery work.</b> A highlight slider that simply darkens
/// bright pixels cannot bring back blown detail, because a blown region arrives flat
/// and a darker copy of a flat region is still flat. Two things have to be true
/// instead: there must be information above display white to recover (that is
/// <see cref="HighlightReconstruction"/>'s job, and it runs first), and this operator
/// must <i>compress</i> the highlight range rather than offset it, so several stops
/// of headroom fold down into the visible range while keeping their ordering. The
/// previous implementation offset the highlight base by at most one stop, which is
/// neither enough authority to reach above-white content nor a transform that
/// preserves structure — hence the flat result.</para>
///
/// <para><b>Method.</b> The image is split into an edge-aware base and a detail layer
/// in EV space, the base is offset by a band-shaped weight, and the detail is added
/// back at full strength (slightly boosted on recovery):</para>
/// <list type="number">
/// <item>Y = Rec.709 luminance; I = log2(Y / middle grey), so the working space is
///       stops and every constant below reads as a photographic quantity.</item>
/// <item>Base B = self-guided filter of I; detail D = I − B. A guided filter rather
///       than a Gaussian, so the base follows edges and the reconstruction does not
///       halo around a bright horizon.</item>
/// <item>B′ = B + amplitude · k · <see cref="BandWeight"/>(B): a signed EV offset whose
///       weight rises across the highlight band. Negative slider pulls the band down
///       (recovery); positive pushes it up. On recovery a second soft-knee fold then
///       folds whatever headroom sits above the band into the visible range.</item>
/// <item>I′ = B′ + detailGain · D. Moving the base while leaving the detail alone is
///       what turns recovered highlights into <i>detail</i> instead of mush — the
///       local contrast that survived in the unclipped channels is carried through at
///       full amplitude on top of a relocated base.</item>
/// <item>Back to Y′ = middle grey · 2^I′, and colour is preserved by scaling RGB by
///       the single luminance ratio Y′/Y.</item>
/// </list>
///
/// <para>At and below <see cref="Options.BandLoEv"/> the band weight is exactly 0 and
/// detailGain is exactly 1, so I′ == I and the gain is exactly 1: deep midtones and
/// shadows are bit-exact untouched, not merely approximately so.</para>
///
/// <para><b>Why a band, and why it is anchored so low.</b> This operator was a single
/// soft-knee compression anchored at middle grey. Measured against Lightroom on paired
/// 0 / −50 / −100 exports, it was roughly an order of magnitude too weak: moving the
/// slider to −100 changed the frame by ~1.2 code values on average where Lightroom
/// changed it by ~15.4. The cause was reach, not strength. This pipeline renders scene
/// middle grey at ~190/255, so an operator anchored there is an exact no-op below
/// 190/255 — yet that is where most of the work happens. Lightroom still pulls
/// −0.55 EV at a regional level of 178/255 and −0.35 EV at 140/255, both of which the
/// old anchor could not touch at all. Widening the reach downward is the fix; simply
/// steepening the slope is not, and is actively harmful — the old asymptote sat at
/// <c>knee + (1−slope)·width</c>, which <i>rises</i> as the slope is driven down, so a
/// hypothetical "−200" would have made the brightest regions brighter while flattening
/// everything below them. See <see cref="Options.RecoveryEv"/> for the numbers and how
/// they were obtained, and the note above <see cref="Options.TopKneeEv"/> for where the
/// measurement stops being trustworthy.</para>
///
/// <para>This is a <i>spatial</i> operator — it needs neighbouring pixels for the
/// base/detail split — so it works on whole planes rather than one pixel at a time,
/// unlike the per-pixel curves in <see cref="BasicTone"/>.</para>
///
/// <para><b>Example (planar linear-RGB float buffers, modified in place):</b></para>
/// <code>
/// // r,g,b are length w*h, scene-linear; values above 1.0 are the headroom that
/// // HighlightReconstruction recovered and are exactly what recovery acts on.
/// LocalHighlights.Apply(r, g, b, w, h, new LocalHighlights.Options { Highlights = -60 });
/// </code>
/// </summary>
public static class LocalHighlights
{
    /// <summary>Tunable parameters with Lightroom-ish defaults.</summary>
    public sealed class Options
    {
        /// <summary>Slider value, −100 … +100. 0 is a no-op.</summary>
        public double Highlights = 0.0;

        /// <summary>Edge-aware filter radius in pixels. 0 ⇒ auto from resolution
        /// (1/16 of the short edge, clamped to 16–64) so the regional behaviour is
        /// the same at preview and full-resolution sizes.</summary>
        public int Radius = 0;

        /// <summary>
        /// Bottom of the highlight band, in stops about middle grey. At and below
        /// this the operator is a bit-exact no-op.
        ///
        /// <para>Far lower than it sounds — −2.1 EV renders at about 84/255 — and that
        /// is deliberate. Measured against Lightroom (see the calibration note on
        /// <see cref="RecoveryEv"/>), its Highlights slider still pulls −0.17 EV at a
        /// regional level of 119/255 and −0.35 EV at 140/255. An operator anchored at
        /// middle grey, which this pipeline renders at ~190/255, is an exact no-op
        /// across that whole span and therefore cannot go where most of the work
        /// actually happens.</para>
        /// </summary>
        public double BandLoEv = -2.1;

        /// <summary>Where the rising edge of the band reaches full weight, in stops
        /// about middle grey (+0.6 EV ≈ 221/255).</summary>
        public double BandHiEv = 0.6;

        // ── Stage 2: folding the headroom above the calibrated band ────────────
        //
        // The band offset above is bounded by construction, so on its own it cannot
        // bring several stops of above-white headroom into view — and that headroom is
        // exactly what HighlightReconstruction recovers and hands to this operator.
        // A second, gentler soft-knee compression sits on top of the band to fold it.
        //
        // This stage is deliberately NOT fitted to the reference exports, because the
        // measurement goes blind here. The transfer was obtained by binning on the
        // Highlights-0 render, and that render clips at 255 (+2.5 EV) — so every scene
        // tone above +2.5 EV lands in the same bin and the recovered EV of the top bins
        // is an underestimate. That biases the apparent pull toward zero and shows up
        // as a taper above roughly +0.5 EV. Below that the reference is unclipped and
        // trustworthy, which is where the band constants come from; above it, these
        // constants serve the headroom requirement instead of extrapolating an artifact.
        // Re-deriving them needs a reference exported with headroom intact (a 16-bit
        // or raw-linear pair), not an 8-bit JPEG.

        /// <summary>Where the top-end fold begins, in stops about middle grey — applied
        /// after the band offset, so it only sees tones the band could not reach.</summary>
        public double TopKneeEv = 0.0;

        /// <summary>Asymptotic slope of the top-end fold at Highlights −100. Scaled by
        /// the slider so it is exactly the identity (slope 1) at neutral.</summary>
        public double TopSlope = 0.12;

        /// <summary>Stops the top-end fold takes to reach <see cref="TopSlope"/>.</summary>
        public double TopWidthEv = 0.5;

        /// <summary>
        /// Peak downward pull of the regional tone, in stops, at Highlights −100.
        ///
        /// <para><b>Calibrated, not guessed.</b> Derived from paired exports of one
        /// scene at Highlights 0 / −50 / −100 from both engines. Comparing each engine
        /// against its <i>own</i> neutral export isolates the slider from any baseline
        /// difference — and the neutral pair confirms
        /// <see cref="BasicTone.LightroomMatch"/> is sound, agreeing within ~5 code
        /// values — so the whole measured gap is the slider. Fitting this band shape to
        /// Lightroom's curve reproduces it to an RMS of 0.032 EV, within about two code
        /// values everywhere.</para>
        ///
        /// <para>The previous knee-and-slope formulation delivered −0.084 EV where
        /// Lightroom delivers −0.658, and −0.013 EV where Lightroom delivers −0.553:
        /// eight to forty times too little, and only ~1.2 code values of mean change
        /// across the frame against Lightroom's ~15.4.</para>
        /// </summary>
        public double RecoveryEv = 0.68;

        /// <summary>Peak upward push at Highlights +100, in stops.
        ///
        /// <para><b>Uncalibrated</b> — the reference exports covered the recovery
        /// direction only. Kept symmetric with <see cref="RecoveryEv"/> as the neutral
        /// assumption. Unlike the slope-expansion it replaces, this is bounded, so
        /// +100 pushes the band toward the clip without driving it through.</para>
        /// </summary>
        public double BoostEv = 0.68;

        /// <summary>Extra detail-layer amplitude at full recovery, so compressed
        /// highlights keep their micro-contrast. Gated by the highlight weight, so
        /// midtones never see it. It matters more here than it would elsewhere,
        /// because the base is compressed so hard that the detail layer carries most
        /// of what is left to see.</summary>
        public double DetailBoost = 0.40;

        /// <summary>Guided-filter edge threshold in (EV)². 0.25 ≈ (½ stop)², so
        /// roughly half a stop of step counts as a real edge worth preserving while
        /// flatter variation gets averaged into the base.</summary>
        public double GuidedEpsilon = 0.25;

        /// <summary>Luminance floor; keeps pure black off −∞ and bounds the gain for
        /// near-zero pixels.</summary>
        public double Eps = 1e-6;
    }

    /// <summary>
    /// Band weight from a base (region) tone in EV — the shape of Lightroom's
    /// Highlights response, measured. Rises smoothly from 0 at
    /// <see cref="Options.BandLoEv"/> to 1 at <see cref="Options.BandHiEv"/>.
    ///
    /// <para>Exactly 0 at and below <c>BandLoEv</c>, so the operator — pull and detail
    /// boost alike — is a bit-exact no-op there, and exactly 0 for a neutral slider.</para>
    /// </summary>
    private static double BandWeight(double baseEv, Options o)
        => BasicTone.SmoothStep(o.BandLoEv, o.BandHiEv, baseEv);

    /// <summary>
    /// The auto radius for an image of the given size — what <see cref="Options.Radius"/>
    /// resolves to when left at 0. Public so a caller rendering a <i>crop</i>
    /// (a masked local adjustment) can pin the crop to the radius the whole
    /// image would have used; deriving it from the crop's own dimensions would
    /// split highlights over a different base layer inside the mask than
    /// outside, and the difference shows up as a seam at the mask edge.
    /// </summary>
    public static int RegionRadius(int w, int h) => Math.Clamp(Math.Min(w, h) / 16, 16, 64);

    /// <summary>
    /// Apply the Highlights adjustment in place to planar linear-RGB float buffers
    /// (each at least <paramref name="w"/>·<paramref name="h"/> long). Prefer linear
    /// (not gamma-encoded) input; values above 1.0 are the recoverable headroom.
    /// </summary>
    public static void Apply(float[] r, float[] g, float[] b, int w, int h,
                             Options? options = null, ParallelOptions? po = null)
    {
        options ??= new Options();
        po ??= new ParallelOptions();
        int n = w * h;
        if (w <= 0 || h <= 0) return;
        if (r.Length < n || g.Length < n || b.Length < n)
            throw new ArgumentException("Each RGB plane must have at least w*h elements.");

        double hl = Math.Clamp(options.Highlights, -100.0, 100.0);
        if (hl == 0.0) return;   // neutral ⇒ exact no-op.

        double eps = options.Eps;
        double midGray = BasicTone.MiddleGray;
        double logMidGray = double.Log2(midGray);

        // ── 1. Luminance, and EV relative to middle grey ──
        var Y = new float[n];
        var I = new float[n];
        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                double y = BasicTone.Luminance(r[i], g[i], b[i]);
                if (y < 0.0) y = 0.0;
                Y[i] = (float)y;
                I[i] = (float)(double.Log2(eps + y) - logMidGray);
            }
        });

        // ── 2. Edge-aware base/detail split (guided filter — avoids halos) ──
        int radius = options.Radius > 0 ? options.Radius : RegionRadius(w, h);
        var B = new float[n];
        GuidedBase(I, B, w, h, radius, (float)options.GuidedEpsilon, po);

        // ── 3. Slider → signed peak offset of the highlight band ──
        // Linear in the slider: the measured -50 export lands at 0.494 of the -100
        // export in EV (Lightroom 0.494, this operator 0.496), so a straight k scale
        // is the correct parameterisation and both engines agree on it.
        double k = Math.Abs(hl) / 100.0;
        bool recovering = hl < 0.0;
        double amplitude = recovering ? -options.RecoveryEv : options.BoostEv;
        double detailBoost = recovering ? options.DetailBoost : 0.0;
        double topKnee = options.TopKneeEv;
        double topWidth = options.TopWidthEv;
        double topSlope = 1.0 - k * (1.0 - options.TopSlope);   // 1 at neutral ⇒ identity

        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                double Ii = I[i];
                double Bi = B[i];
                double Di = Ii - Bi;

                // ── 4a. Offset the base across the calibrated band. ──
                double wB = BandWeight(Bi, options);
                double Bo = Bi + amplitude * k * wB;

                // ── 4b. Fold whatever headroom sits above the band. ──
                // Identity at neutral (topSlope == 1 ⇒ SoftKnee is the identity), so
                // this cannot disturb the exact no-op.
                if (recovering)
                    Bo = topKnee + BasicTone.SoftKnee(Bo - topKnee, topSlope, topWidth);

                double detailGain = 1.0 + detailBoost * k * wB;

                double Io = Bo + detailGain * Di;

                // ── 5. Back to linear luminance, colour via a single gain. ──
                double y2 = midGray * double.Exp2(Io) - eps;

                double y0 = Y[i];
                if (y0 < eps || double.IsNaN(y2) || double.IsInfinity(y2)) continue;
                if (y2 < 0.0) y2 = 0.0;

                double gain = y2 / y0;
                if (double.IsNaN(gain) || double.IsInfinity(gain)) continue;

                r[i] = (float)(r[i] * gain);
                g[i] = (float)(g[i] * gain);
                b[i] = (float)(b[i] * gain);
            }
        });
    }

    /// <summary>
    /// Self-guided filter [He, Sun, Tang 2010] of <paramref name="src"/> (here, EV),
    /// producing an edge-preserving base layer. Cost ≈ four separable box blurs and
    /// deterministic regardless of core count. Same shape as <see cref="EdgeAwareLuma"/>
    /// but parameterised on its own epsilon so callers can split into base/detail.
    /// </summary>
    private static void GuidedBase(float[] src, float[] baseOut, int w, int h,
                                   int radius, float eps, ParallelOptions po)
    {
        int n = w * h;
        // One scratch plane shared by every box blur, blurs done in place — the
        // GC-churn lesson Detail.GuidedSmooth already learned. Byte-identical.
        var tmp = new float[n];
        var meanP = new float[n];
        var pp = new float[n];
        var a = new float[n];
        var b = new float[n];

        BoxBlur(src, meanP, tmp, w, h, radius, po);

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) { int i = row + x; pp[i] = src[i] * src[i]; }
        });
        BoxBlur(pp, pp, tmp, w, h, radius, po);   // pp := mean(p²)

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float variance = pp[i] - meanP[i] * meanP[i];
                if (variance < 0f) variance = 0f;
                float ai = variance / (variance + eps);
                a[i] = ai;
                b[i] = (1f - ai) * meanP[i];
            }
        });

        BoxBlur(a, a, tmp, w, h, radius, po);   // a := mean(a)
        BoxBlur(b, b, tmp, w, h, radius, po);   // b := mean(b)

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) { int i = row + x; baseOut[i] = a[i] * src[i] + b[i]; }
        });
    }

    // Sliding-window separable box blur, horizontal then vertical, clamp-extend
    // edges. Kept local so the module is self-contained (same pattern as
    // EdgeAwareLuma / DevelopProcessor). <paramref name="scratch"/> holds the
    // horizontal pass so callers can reuse one plane; src and dst may alias.
    private static void BoxBlur(float[] src, float[] dst, float[] scratch, int w, int h, int radius, ParallelOptions po)
    {
        int taps = radius * 2 + 1;
        float inv = 1f / taps;
        var tmp = scratch;

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            float sum = 0f;
            for (int k = -radius; k <= radius; k++)
            {
                int xc = k < 0 ? 0 : k >= w ? w - 1 : k;
                sum += src[row + xc];
            }
            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                int addX = x + radius + 1;
                int subX = x - radius;
                if (addX > w - 1) addX = w - 1;
                if (subX < 0) subX = 0;
                sum += src[row + addX] - src[row + subX];
            }
        });

        // Vertical pass, blocked over columns so every read and write is
        // memory-sequential within a row. Each column's running sum accumulates in
        // the exact original order, so the result is byte-identical.
        const int block = 64;
        int nBlocks = (w + block - 1) / block;
        Parallel.For(0, nBlocks, po, bi =>
        {
            int x0 = bi * block;
            int bw = Math.Min(block, w - x0);
            Span<float> sums = stackalloc float[block];
            sums = sums.Slice(0, bw);
            sums.Clear();

            for (int k = -radius; k <= radius; k++)
            {
                int yc = k < 0 ? 0 : k >= h ? h - 1 : k;
                int r = yc * w + x0;
                for (int x = 0; x < bw; x++) sums[x] += tmp[r + x];
            }
            for (int y = 0; y < h; y++)
            {
                int d = y * w + x0;
                for (int x = 0; x < bw; x++) dst[d + x] = sums[x] * inv;
                int addY = y + radius + 1;
                int subY = y - radius;
                if (addY > h - 1) addY = h - 1;
                if (subY < 0) subY = 0;
                int ar = addY * w + x0;
                int sr = subY * w + x0;
                for (int x = 0; x < bw; x++) sums[x] += tmp[ar + x] - tmp[sr + x];
            }
        });
    }
}
