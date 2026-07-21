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
/// in EV space; the base is offset by a measured amplitude curve, and the detail is
/// carried back with a direction-dependent gain:</para>
/// <list type="number">
/// <item>Y = Rec.709 luminance; I = log2(Y / middle grey), so the working space is
///       stops and every constant below reads as a photographic quantity.</item>
/// <item>Base B = self-guided filter of I; detail D = I − B. A guided filter rather
///       than a Gaussian, so the base follows edges and the reconstruction does not
///       halo around a bright horizon.</item>
/// <item>B′ = B + dir · k · <see cref="HighlightAmplitude"/>(B): a signed EV offset whose
///       magnitude is a softplus that rises monotonically through the highlights and
///       never saturates (see <see cref="Options.AmpFloor"/>). dir = −1 pulls the band
///       down (recovery), +1 pushes it up (boost). The softplus's sub-unity tail is what
///       folds recovered above-white headroom into view while keeping tone ordering.</item>
/// <item>I′ = B′ + detailGain · D, detailGain = 1 − dir·k·<see cref="DetailWeight"/>(B):
///       recovery <i>expands</i> the highlight detail so compressed highlights stay
///       detail rather than mush; boost <i>compresses</i> it. Both are what Lightroom
///       measurably does.</item>
/// <item>Back to Y′ = middle grey · 2^I′, and colour is preserved by scaling RGB by
///       the single luminance ratio Y′/Y.</item>
/// </list>
///
/// <para>At and below <see cref="Options.BlacksGuardLoEv"/> the amplitude is exactly 0
/// and (below <see cref="Options.DetailLoEv"/>) detailGain is exactly 1, so I′ == I and
/// the gain is exactly 1: deep shadows and blacks are bit-exact untouched, not merely
/// approximately so; and at slider 0 (k = 0) the whole frame is.</para>
///
/// <para><b>Calibration.</b> The amplitude and detail curves are fitted to Lightroom
/// across 261 exports — 29 scenes × Highlights 0 / ±25 / ±50 / ±75 / ±100 — by inverting
/// this pipeline's output transform on each export to scene-linear luminance EV (the
/// operator's own space), guided-filtering the neutral to a regional base B, and binning
/// ΔEV against B over ~18 M unclipped pixels per level. Three findings shaped the model:
/// the response is <i>linear in the slider</i> (one k scale is exact); it is
/// <i>near-symmetric</i> between recovery and boost up to the clip (one amplitude curve,
/// signed by direction); and it <i>does not saturate</i> — Lightroom pulls harder the
/// brighter the region, ~0.54 EV at middle grey (190/255) rising past 0.88 EV near white,
/// where the previous smoothstep band flattened out at +0.6 EV. Fitting the softplus
/// reproduces the binned curve to ~0.02 EV and, across the whole set, cuts the residual
/// against Lightroom from the do-nothing 12.9 to 8.6 code values (the previous band +
/// soft-knee model scored 8.9), improving every slider level. The analysis scripts live
/// under Compare/highlights.</para>
///
/// <para><b>Headroom above display white.</b> Base tones the 8-bit exports clip at (above
/// ~+1.6 EV / 250-255) are invisible to the fit, yet recovering blown highlights into view
/// is the whole point of the slider. The display transform is nearly flat up there — it
/// squeezes +1.6…+2.5 EV into the last few code values — so an <i>accelerating</i> top-end
/// compression is needed that a saturating softplus cannot supply. So on recovery only, an
/// aggressive soft-knee (see <see cref="Options.HeadroomKneeEv"/>) folds the top after the
/// calibrated offset. Crucially it is anchored just above the calibrated band's own output
/// (~0.6 EV at −100), so it is an exact identity across all but the near-clip top and touches
/// only the headroom — unlike the previous fold, anchored at middle grey, which folded the
/// band itself. Its shape is uncalibratable from JPEGs and tuned to land recovered content
/// clear of white; refining it needs a headroom-intact (16-bit / raw-linear) reference.</para>
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

        // ── Base-offset amplitude: a softplus in EV, fitted to Lightroom ───────
        //
        // The signed EV the regional base is moved by at |slider| = 100 is
        //
        //     Amp(baseEv) = (AmpFloor + AmpSlope·AmpWidthEv·softplus((baseEv−AmpKneeEv)/AmpWidthEv))
        //                   · smoothstep(BlacksGuardLoEv, BlacksGuardHiEv, baseEv)
        //
        // (softplus(x) = ln(1+eˣ)), applied downward on recovery and upward on boost.
        // It is a floor of ~0.13 EV that rises smoothly through the highlight band —
        // ~0.26 EV at 140/255, ~0.54 at middle grey (190/255), ~0.76 at 234/255 —
        // and, unlike a smoothstep band, never saturates: it keeps steepening into the
        // brightest tones, which is what Lightroom's Highlights actually does. The
        // asymptotic slope is AmpSlope ≈ 0.25 < 1, so B ↦ B − Amp(B) stays strictly
        // increasing at −100 (no tone inversion) and the linear tail folds recovered
        // above-white headroom monotonically into view — subsuming the old two-stage
        // band-plus-soft-knee arrangement.
        //
        // <b>Calibrated from 261 exports.</b> 29 scenes, each exported from Lightroom at
        // Highlights 0 / ±25 / ±50 / ±75 / ±100. Each export's output transform was
        // inverted (DisplayCurve∘LightroomMatch) to scene-linear luminance EV — the very
        // space this operator works in — the neutral guided-filtered to a regional base
        // B, and ΔEV binned against B over ~18 M unclipped pixels per level. The measured
        // response is linear in the slider (so a single k = |slider|/100 scale is exact)
        // and near-symmetric between recovery and boost up to the clip, so one amplitude
        // curve serves both directions. A softplus fit reproduces the binned curve to
        // within ~0.02 EV across the high-density band; on held-out scenes it beats a raw
        // per-bin LUT, so it is not over-fitted. See Compare/highlights (analysis scripts).

        /// <summary>Amplitude floor in EV — the gentle pull Lightroom keeps even in the
        /// low midtones (~0.13 EV at |slider| = 100).</summary>
        public double AmpFloor = 0.1294;

        /// <summary>Asymptotic EV-per-EV slope of the amplitude in the bright tones.
        /// Kept below 1 so recovery never inverts tone ordering.</summary>
        public double AmpSlope = 0.2536;

        /// <summary>Softplus knee, in stops about middle grey: where the highlight band
        /// starts rising above the floor (≈ −1.44 EV ≈ 113/255).</summary>
        public double AmpKneeEv = -1.4381;

        /// <summary>Softplus width in EV — how sharply the band turns on at the knee.</summary>
        public double AmpWidthEv = 0.3209;

        /// <summary>
        /// Below <see cref="BlacksGuardHiEv"/> the amplitude is tapered to 0, reaching an
        /// exact no-op at and below <see cref="BlacksGuardLoEv"/>, so deep shadows and
        /// blacks stay bit-exact untouched and Highlights never fights the Blacks slider.
        ///
        /// <para>Lightroom does apply a small (~0.13 EV) pull even this far down; dropping
        /// it here costs only ~0.05 code values of match — imperceptible — and buys a clean
        /// shadow no-op, the same property the previous anchor prized.</para>
        /// </summary>
        public double BlacksGuardLoEv = -3.3;

        /// <summary>Top of the blacks-guard taper, in stops about middle grey (≈ 66/255).</summary>
        public double BlacksGuardHiEv = -2.4;

        // ── Detail-layer gain ──────────────────────────────────────────────────
        //
        // Within a highlight region Lightroom does not move every pixel by the same
        // amount: on recovery it *expands* the local detail (a bright speck in a bright
        // region drops less than its surround, so micro-contrast survives the
        // compression), and on boost it *compresses* it. Measured as the slope of ΔEV on
        // the detail residual D = I − B, this is ≈ ±0.19 across the band, near-symmetric
        // between the two directions. detailGain = 1 − dir·k·DetailWeight(B) reproduces
        // it: > 1 on recovery, < 1 on boost, exactly 1 (and gated to 0) below the band.

        /// <summary>Peak detail-layer gain offset at |slider| = 100 (recovery expands by
        /// this, boost compresses by it).</summary>
        public double DetailMax = 0.1873;

        /// <summary>Where the detail gain begins to act, in stops about middle grey — 0
        /// (exact) below this, so midtones and shadows keep their detail untouched.</summary>
        public double DetailLoEv = -1.0186;

        /// <summary>Where the detail gain reaches full strength, in stops about middle grey.</summary>
        public double DetailHiEv = -0.6876;

        // ── Headroom fold (recovery only) ──────────────────────────────────────
        //
        // The calibrated amplitude above handles every tone the reference exports could
        // measure. Above that — content the 8-bit JPEGs clipped, and the above-white
        // headroom HighlightReconstruction hands back — a soft-knee compression folds the
        // top into the visible range. It is anchored on the base value <i>after</i> the
        // calibrated pull, just above <see cref="HighlightAmplitude"/>'s output for the
        // brightest measured tones (~0.6 EV at −100), so it is identity across all but the
        // near-clip top of the band and acts on the headroom above it. Its asymptotic slope is
        // scaled by the slider (1 ⇒ identity at neutral, HeadroomSlope at −100), and it is
        // applied on recovery only — boost has no headroom to fold. Uncalibrated (see the
        // class remarks); tuned to land recovered highlights clear of white.

        /// <summary>Where the headroom fold begins, in stops about middle grey — measured on
        /// the base <i>after</i> the calibrated offset, so it clears the whole calibrated band.</summary>
        public double HeadroomKneeEv = 0.60;

        /// <summary>Asymptotic slope of the headroom fold at Highlights −100 (aggressive, so
        /// several stops of headroom fold into the top of the visible range).</summary>
        public double HeadroomSlope = 0.03;

        /// <summary>Stops the headroom fold takes to reach <see cref="HeadroomSlope"/>.</summary>
        public double HeadroomWidthEv = 0.20;

        /// <summary>Guided-filter edge threshold in (EV)². 0.25 ≈ (½ stop)², so
        /// roughly half a stop of step counts as a real edge worth preserving while
        /// flatter variation gets averaged into the base.</summary>
        public double GuidedEpsilon = 0.25;

        /// <summary>Luminance floor; keeps pure black off −∞ and bounds the gain for
        /// near-zero pixels.</summary>
        public double Eps = 1e-6;
    }

    /// <summary>Numerically-stable softplus, ln(1 + eˣ).</summary>
    private static double Softplus(double x)
        => Math.Max(x, 0.0) + Math.Log(1.0 + Math.Exp(-Math.Abs(x)));

    /// <summary>
    /// The signed-magnitude EV the regional base is moved by at |slider| = 100, from a
    /// base (region) tone in EV — Lightroom's measured Highlights response (see the note
    /// on <see cref="Options.AmpFloor"/>). A softplus that floors near <c>AmpFloor</c> and
    /// rises monotonically through the highlights, tapered to an exact 0 in the blacks.
    ///
    /// <para>Returns ≥ 0; the caller applies the sign (down on recovery, up on boost).
    /// Exactly 0 at and below <see cref="Options.BlacksGuardLoEv"/>, so the operator is a
    /// bit-exact no-op on deep shadows there, and (via k) for a neutral slider.</para>
    /// </summary>
    private static double HighlightAmplitude(double baseEv, Options o)
    {
        double t = (baseEv - o.BlacksGuardLoEv) / (o.BlacksGuardHiEv - o.BlacksGuardLoEv);
        if (t <= 0.0) return 0.0;
        if (t > 1.0) t = 1.0;
        double sp = Softplus((baseEv - o.AmpKneeEv) / o.AmpWidthEv);
        return (o.AmpFloor + o.AmpSlope * o.AmpWidthEv * sp) * t;
    }

    /// <summary>Detail-layer gain magnitude at |slider| = 100 from a base tone in EV:
    /// <see cref="Options.DetailMax"/> across the highlight band, 0 (exact) below
    /// <see cref="Options.DetailLoEv"/>.</summary>
    private static double DetailWeight(double baseEv, Options o)
        => o.DetailMax * BasicTone.SmoothStep(o.DetailLoEv, o.DetailHiEv, baseEv);

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

        // ── 3. Slider → base offset and detail gain ──
        // Linear in the slider: the measured ±25/±50/±75 exports land at exactly
        // 0.25/0.50/0.75 of the ±100 response in EV, so a straight k scale is the
        // correct parameterisation. dir points the offset (down on recovery, up on
        // boost); the amplitude/detail curves themselves are direction-symmetric.
        double k = Math.Abs(hl) / 100.0;
        double dir = hl < 0.0 ? -1.0 : 1.0;

        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                double Ii = I[i];
                double Bi = B[i];
                double Di = Ii - Bi;

                // ── 4a. Offset the base by the calibrated amplitude. ──
                // A monotone softplus in EV: no false saturation in the highlights, and a
                // sub-unity tail that keeps recovery order-preserving.
                double amp = HighlightAmplitude(Bi, options);
                double Bo = Bi + dir * k * amp;

                // ── 4b. Fold the headroom above the calibrated band (recovery only). ──
                // Identity across the whole calibrated range (its knee sits above the band's
                // own output) and at neutral (foldSlope == 1 ⇒ SoftKnee is the identity), so
                // it cannot disturb the calibration or the no-op — it only pulls above-white
                // content into view.
                if (dir < 0.0)
                {
                    double foldSlope = 1.0 - k * (1.0 - options.HeadroomSlope);
                    Bo = options.HeadroomKneeEv
                       + BasicTone.SoftKnee(Bo - options.HeadroomKneeEv, foldSlope, options.HeadroomWidthEv);
                }

                // ── 4c. Detail layer: expand on recovery, compress on boost. ──
                // 1 at neutral (k = 0) and below the band (weight 0), so the no-op holds.
                double detailGain = 1.0 - dir * k * DetailWeight(Bi, options);

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
