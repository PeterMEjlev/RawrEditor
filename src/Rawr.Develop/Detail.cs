namespace Rawr.Develop;

/// <summary>
/// Lightroom-<i>like</i> <b>Detail</b>: capture sharpening and noise reduction. An
/// approximation of the look of Adobe's panel built from standard published
/// technique — not Adobe's algorithm and not reverse-engineered from it.
///
/// <para>Both operators are <i>spatial</i>, so like <see cref="LocalHighlights"/>
/// they work on whole planes rather than a pixel at a time. They run at the end of
/// <see cref="DevelopProcessor.DecomposeToPlanes"/>, on the display-referred luma
/// plane — the same place the chroma denoise already lived, and the same place
/// Lightroom sharpens: after the tone curve, so the slider means the same thing
/// whatever the exposure.</para>
///
/// <para><b>Order is denoise, then sharpen.</b> The reverse amplifies the very
/// grain the next step is trying to remove, and no amount of luminance smoothing
/// afterwards puts that back.</para>
///
/// <para><b>Radius is in native pixels of whichever buffer is being rendered</b>,
/// so the 1920 px preview shows more apparent sharpening than the full-resolution
/// export — the same caveat Lightroom carries at fit view, and why it tells you to
/// judge sharpening at 1:1. This is a deliberate choice over scaling the radius by
/// the resolution ratio: at preview size a 1.0 px export radius becomes ~0.3 px,
/// which is invisible, and a slider you cannot see working is worse than one that
/// overstates. It differs from <see cref="EdgeAwareLuma"/>, which does scale its
/// radius — but that one is measuring <i>regions</i>, which are a property of the
/// composition and so must be resolution-independent. Noise and acutance are
/// properties of the pixel grid.</para>
/// </summary>
public static class Detail
{
    // ── Lightroom's panel defaults ──
    public const double DefaultSharpenRadius = 1.0;
    public const double DefaultSharpenDetail = 25.0;
    public const double DefaultSharpenMasking = 0.0;
    public const double DefaultLuminanceDetail = 50.0;
    public const double DefaultLuminanceContrast = 0.0;

    /// <summary>
    /// Colour NR at its default. Chosen so it reproduces the radius-2 chroma box
    /// blur this pipeline used to hard-code — the baseline render has to stay
    /// byte-identical, and that blur was part of it.
    /// </summary>
    public const double DefaultColorNoiseReduction = 25.0;

    /// <summary>Chroma blur radius per unit of the Colour slider: 25 → 2 px, which
    /// is the constant that used to be inline, and 100 → 8 px.</summary>
    private const double ColorRadiusPerUnit = 2.0 / 25.0;

    /// <summary>Amplitude (in 0…255 luma units) at which the Detail slider's halo
    /// suppression is centred, at each end of its travel. At Detail 0 only
    /// low-amplitude texture survives the weighting, so big edges are left alone
    /// and the sharpening reads as micro-contrast; at 100 nearly everything passes
    /// and strong edges get haloes, which is exactly what that end is for.</summary>
    private const float DetailThresholdMin = 8f;
    private const float DetailThresholdMax = 250f;

    /// <summary>
    /// Gradient magnitude (0…255 luma units per pixel) that counts as a definite
    /// edge when Masking is at 100. Below it, sharpening is faded out — which is
    /// how Masking keeps grain out of skies and skin.
    ///
    /// <para><b>Measured, not guessed.</b> Gradient magnitudes on a real frame, as
    /// this operator sees them (σ-1 blur, central difference, halved), are
    /// extraordinarily bottom-heavy: p50 = 0.9, p90 = 2.2, p95 = 3.2, then p99
    /// jumps to 16 and the maximum is 69. Texture and edges are separated by
    /// roughly an order of magnitude, and almost the whole frame lives under 3.
    /// A threshold of 10 therefore puts Masking 100 just under the 99th
    /// percentile — only genuine edges survive, which is the point of that end.</para>
    ///
    /// <para>The slider reaches it <i>quadratically</i>. Linearly, half the travel
    /// sat above the 99th percentile where everything is already masked out: the
    /// first version of this used 40 linear, and measured on that frame it had
    /// surrendered nearly all sharpening by Masking 50, cramming the usable range
    /// into about 0–30. Squaring spends the low half of the slider in the 0–2.5
    /// band where the pixels actually are.</para>
    /// </summary>
    private const float MaskEdgeScale = 10f;

    /// <summary>Guided-filter radius for luminance NR, in pixels. Small on purpose:
    /// sensor noise is a few pixels wide, and a wider window starts removing
    /// texture rather than grain.</summary>
    private const int LuminanceRadius = 2;

    /// <summary>Edge threshold for luminance NR, in (0…255 luma units)², at each end
    /// of the Detail slider. Detail 0 treats a 20-unit step as noise worth
    /// smoothing; Detail 100 only smooths variation under about 2 units.</summary>
    private const float LuminanceEpsMax = 400f;
    private const float LuminanceEpsMin = 4f;

    /// <summary>Local standard deviation (0…255 luma units) at which the NR Contrast
    /// slider considers a region "textured" and hands its detail back.</summary>
    private const float ContrastScale = 12f;

    // ── Sharpening ──────────────────────────────────────────────────────────

    public readonly struct SharpenParams
    {
        public readonly float Amount;      // 0 … 1.5
        public readonly double Radius;     // pixels
        public readonly float Threshold;   // Detail, as a highpass amplitude
        public readonly float MaskScale;   // 0 ⇒ no masking
        public readonly bool IsActive;

        public SharpenParams(float amount, double radius, float threshold, float maskScale, bool isActive)
        {
            Amount = amount;
            Radius = radius;
            Threshold = threshold;
            MaskScale = maskScale;
            IsActive = isActive;
        }
    }

    public static SharpenParams BuildSharpen(double amount, double radius, double detail, double masking)
    {
        float a = (float)(Math.Clamp(amount, 0.0, 150.0) / 100.0);
        double r = Math.Clamp(radius, 0.5, 3.0);
        double d = Math.Clamp(detail, 0.0, 100.0);
        double m = Math.Clamp(masking, 0.0, 100.0);

        float threshold = DetailThresholdMin + (float)(d / 100.0) * (DetailThresholdMax - DetailThresholdMin);
        float mNorm = (float)(m / 100.0);
        float maskScale = mNorm * mNorm * MaskEdgeScale;

        return new SharpenParams(a, r, threshold, maskScale, a > 0f);
    }

    /// <summary>
    /// Unsharp mask on the luma plane, in place.
    ///
    /// <para>out = luma + Amount · w(|d|) · mask · d, where d is the highpass
    /// (luma − blur). The two weights are what separate this from a naive USM:
    /// <c>w</c> is the Detail slider suppressing the large-amplitude highpass that
    /// becomes a halo, and <c>mask</c> is the Masking slider fading the whole
    /// effect out where there is no edge to sharpen.</para>
    /// </summary>
    public static void Sharpen(float[] luma, int w, int h, in SharpenParams p, ParallelOptions po)
    {
        if (!p.IsActive) return;

        int n = w * h;
        var blur = new float[n];
        GaussianBlur(luma, blur, w, h, p.Radius, po);

        float amount = p.Amount;
        float threshold = p.Threshold;
        float invThresholdSq = 1f / (threshold * threshold);
        float maskScale = p.MaskScale;
        bool masking = maskScale > 0f;

        // The mask is read off the blurred plane, not the original: on a noisy
        // file the raw gradient is dominated by grain, and a mask built from that
        // would happily "protect" nothing and sharpen the noise it was added to
        // suppress.
        //
        // Written in place. Safe because every neighbour this loop touches comes
        // from `blur` — the only read of `luma` is at i itself, so no pixel ever
        // sees a value another iteration has already rewritten.
        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            int up = (yy > 0 ? yy - 1 : 0) * w;
            int dn = (yy < h - 1 ? yy + 1 : h - 1) * w;

            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                int xl = x > 0 ? x - 1 : 0;
                int xr = x < w - 1 ? x + 1 : w - 1;

                float d = luma[i] - blur[i];

                // Detail: attenuate the highpass as its amplitude grows.
                float weight = 1f / (1f + d * d * invThresholdSq);

                if (masking)
                    weight *= MaskWeight(blur[row + xl], blur[row + xr],
                                         blur[up + x], blur[dn + x], maskScale);

                luma[i] = luma[i] + amount * weight * d;
            }
        });
    }

    /// <summary>
    /// How much of the sharpening survives Masking at one pixel: 1 where there is
    /// a strong enough edge, falling to 0 across flat ground. Central difference on
    /// the blurred plane, halved to per-pixel units.
    ///
    /// <para>Shared by <see cref="Sharpen"/> and <see cref="RenderMask"/> rather
    /// than written out twice — a mask overlay that disagrees with the sharpening
    /// it claims to describe is worse than no overlay, and two copies of this would
    /// drift the first time either was tuned.</para>
    /// </summary>
    private static float MaskWeight(float left, float right, float up, float down, float maskScale)
    {
        float gx = right - left;
        float gy = down - up;
        float mag = MathF.Sqrt(gx * gx + gy * gy) * 0.5f;
        return SmoothStep(0f, maskScale, mag);
    }

    /// <summary>
    /// The Masking mask as a 0…1 plane, for Lightroom's black-and-white overlay:
    /// white is sharpened, black is masked out.
    ///
    /// <para><paramref name="luma"/> must be in the state <see cref="Sharpen"/>
    /// would receive it — after noise reduction, before sharpening — or the overlay
    /// describes an image the user is not editing. Radius matters here as well as
    /// Masking, since the gradient is measured on the blurred plane; Amount and
    /// Detail do not enter into it.</para>
    /// </summary>
    public static float[] RenderMask(float[] luma, int w, int h, in SharpenParams p, ParallelOptions po)
    {
        int n = w * h;
        var blur = new float[n];
        GaussianBlur(luma, blur, w, h, p.Radius, po);

        float maskScale = p.MaskScale;
        var mask = new float[n];

        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            int up = (yy > 0 ? yy - 1 : 0) * w;
            int dn = (yy < h - 1 ? yy + 1 : h - 1) * w;

            for (int x = 0; x < w; x++)
            {
                int xl = x > 0 ? x - 1 : 0;
                int xr = x < w - 1 ? x + 1 : w - 1;
                mask[row + x] = MaskWeight(blur[row + xl], blur[row + xr],
                                           blur[up + x], blur[dn + x], maskScale);
            }
        });

        return mask;
    }

    // ── Luminance noise reduction ───────────────────────────────────────────

    public readonly struct LuminanceParams
    {
        public readonly float Strength;   // 0 … 1
        public readonly float Epsilon;    // guided-filter edge threshold
        public readonly float Contrast;   // 0 … 1
        public readonly bool IsActive;

        public LuminanceParams(float strength, float epsilon, float contrast, bool isActive)
        {
            Strength = strength;
            Epsilon = epsilon;
            Contrast = contrast;
            IsActive = isActive;
        }
    }

    public static LuminanceParams BuildLuminance(double amount, double detail, double contrast)
    {
        float s = (float)(Math.Clamp(amount, 0.0, 100.0) / 100.0);
        float d = (float)(Math.Clamp(detail, 0.0, 100.0) / 100.0);
        float c = (float)(Math.Clamp(contrast, 0.0, 100.0) / 100.0);

        // Squared so the useful half of the Detail travel isn't crammed into the
        // top few percent — eps spans two orders of magnitude.
        float inv = 1f - d;
        float eps = LuminanceEpsMin + LuminanceEpsMax * inv * inv;

        return new LuminanceParams(s, eps, c, s > 0f);
    }

    /// <summary>
    /// Edge-preserving luminance smoothing, in place.
    ///
    /// <para>A self-guided filter [He, Sun, Tang 2010] — the same tool
    /// <see cref="EdgeAwareLuma"/> and <see cref="LocalHighlights"/> use to find a
    /// base layer, which is precisely what noise reduction wants: it averages flat
    /// regions hard while leaving anything with real structure alone. Its per-pixel
    /// variance also falls out for free, and the Contrast slider spends it —
    /// handing detail back where the region was textured rather than smooth.</para>
    /// </summary>
    public static void ReduceLuminanceNoise(float[] luma, int w, int h, in LuminanceParams p, ParallelOptions po)
    {
        if (!p.IsActive) return;

        int n = w * h;
        var smooth = new float[n];
        var variance = new float[n];
        GuidedSmooth(luma, smooth, variance, w, h, LuminanceRadius, p.Epsilon, po);

        float strength = p.Strength;
        float contrast = p.Contrast;
        bool doContrast = contrast > 0f;

        Parallel.For(0, h, po, y =>
        {
            int rowBase = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = rowBase + x;
                float removed = luma[i] - smooth[i];
                float keep = 1f - strength;

                if (doContrast)
                {
                    // Give detail back in proportion to how textured the region is, so
                    // Contrast holds micro-structure without also un-smoothing the flat
                    // areas that were the point of running NR at all.
                    float sd = MathF.Sqrt(variance[i]);
                    keep += strength * contrast * SmoothStep(0f, ContrastScale, sd);
                    if (keep > 1f) keep = 1f;
                }

                luma[i] = smooth[i] + removed * keep;
            }
        });
    }

    // ── Colour noise reduction ──────────────────────────────────────────────

    /// <summary>
    /// The chroma denoise, as a slider. This used to be an unconditional radius-2
    /// separable box blur inline in the decompose; <see cref="DefaultColorNoiseReduction"/>
    /// reproduces it exactly, so the baseline render is unchanged and only a user
    /// who moves the slider sees anything different. 0 skips the blur entirely.
    /// </summary>
    public static void ReduceColorNoise(float[] cb, float[] cr, int w, int h,
                                        double amount, ParallelOptions po)
    {
        int radius = ColorRadius(amount);
        if (radius <= 0) return;

        // One scratch plane shared by both channel blurs, instead of a fresh
        // allocation inside each call — the same GC-churn lesson as GuidedSmooth.
        var scratch = new float[w * h];
        BoxBlur(cb, cb, scratch, w, h, radius, po);
        BoxBlur(cr, cr, scratch, w, h, radius, po);
    }

    /// <summary>Chroma blur radius for a Colour slider value, in pixels.</summary>
    public static int ColorRadius(double amount)
        => (int)Math.Round(Math.Clamp(amount, 0.0, 100.0) * ColorRadiusPerUnit,
                           MidpointRounding.AwayFromZero);

    // ── Shared primitives ───────────────────────────────────────────────────

    internal static float SmoothStep(float edge0, float edge1, float x)
    {
        if (edge1 <= edge0) return x >= edge1 ? 1f : 0f;
        float t = (x - edge0) / (edge1 - edge0);
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Separable Gaussian, clamp-extend edges. A real Gaussian rather than the
    /// repeated box blur used elsewhere in the pipeline: those approximate one well
    /// at the large radii regional work needs, but sharpening runs at 0.5–3 px
    /// where a box kernel's corners are the difference between crisp and blocky.
    /// </summary>
    private static void GaussianBlur(float[] src, float[] dst, int w, int h,
                                     double radius, ParallelOptions po)
    {
        float[] k = GaussianKernel(radius, out int half);
        int n = w * h;
        var tmp = new float[n];

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int t = -half; t <= half; t++)
                {
                    int xc = x + t;
                    if (xc < 0) xc = 0; else if (xc >= w) xc = w - 1;
                    sum += src[row + xc] * k[t + half];
                }
                tmp[row + x] = sum;
            }
        });

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int t = -half; t <= half; t++)
                {
                    int yc = y + t;
                    if (yc < 0) yc = 0; else if (yc >= h) yc = h - 1;
                    sum += tmp[yc * w + x] * k[t + half];
                }
                dst[row + x] = sum;
            }
        });
    }

    /// <summary>Normalised 1-D Gaussian. Radius is taken as σ, the usual unsharp-mask
    /// reading, and the kernel is truncated at 3σ where the tail is under 0.3%.</summary>
    private static float[] GaussianKernel(double radius, out int half)
    {
        double sigma = Math.Max(radius, 0.1);
        half = Math.Max(1, (int)Math.Ceiling(sigma * 3.0));

        var k = new float[half * 2 + 1];
        double inv2s2 = 1.0 / (2.0 * sigma * sigma);
        double sum = 0.0;
        for (int t = -half; t <= half; t++)
        {
            double v = Math.Exp(-(t * t) * inv2s2);
            k[t + half] = (float)v;
            sum += v;
        }

        float invSum = (float)(1.0 / sum);
        for (int i = 0; i < k.Length; i++) k[i] *= invSum;
        return k;
    }

    /// <summary>
    /// Self-guided filter, also returning the per-pixel local variance the Contrast
    /// slider reads. Same shape as <see cref="LocalHighlights"/>'s GuidedBase; kept
    /// local so this module stays self-contained, as the others do.
    /// </summary>
    private static void GuidedSmooth(float[] src, float[] dst, float[] varianceOut,
                                     int w, int h, int radius, float eps, ParallelOptions po)
    {
        int n = w * h;

        // One scratch plane shared by every blur, and blurs done in place. On a
        // 5.5 MP preview each plane is 22 MB, so the naive twelve-allocation form
        // costs a quarter-gigabyte of garbage per render — enough that GC, not
        // arithmetic, dominated the cost of moving a Detail slider.
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
                float v = pp[i] - meanP[i] * meanP[i];
                if (v < 0f) v = 0f;
                varianceOut[i] = v;
                float ai = v / (v + eps);
                a[i] = ai;
                b[i] = (1f - ai) * meanP[i];
            }
        });

        BoxBlur(a, a, tmp, w, h, radius, po);
        BoxBlur(b, b, tmp, w, h, radius, po);

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) { int i = row + x; dst[i] = a[i] * src[i] + b[i]; }
        });
    }

    /// <summary>
    /// Sliding-window separable box blur, clamp-extend edges. Same algorithm as
    /// <see cref="EdgeAwareLuma"/>'s and <see cref="LocalHighlights"/>'s; kept
    /// local for the same reason they each keep theirs.
    ///
    /// <para><paramref name="src"/> and <paramref name="dst"/> may be the same
    /// array: the horizontal pass reads only <paramref name="src"/> and writes only
    /// <paramref name="scratch"/>, and the vertical pass reads only
    /// <paramref name="scratch"/>, so by the time anything is written to
    /// <paramref name="dst"/> the source is no longer needed.</para>
    /// </summary>
    private static void BoxBlur(float[] src, float[] dst, float[] scratch,
                                int w, int h, int radius, ParallelOptions po)
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
        // memory-sequential within a row (the column-at-a-time form touched a new
        // cache line on every access). Each column's running sum accumulates in the
        // exact original order, so the result is byte-identical.
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
