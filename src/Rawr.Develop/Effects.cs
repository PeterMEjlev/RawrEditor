using System.Runtime.CompilerServices;

namespace Rawr.Develop;

/// <summary>
/// Lightroom-<i>like</i> <b>Texture</b>, <b>Clarity</b>, <b>Dehaze</b> and
/// <b>Grain</b>. Approximations of the look of Adobe's sliders built from
/// published technique — not Adobe's algorithms, and carrying no GPL code.
///
/// <para><b>Texture and Clarity are the same idea at two scales</b>, which is
/// why they share the base/detail machinery here: split the luminance into an
/// edge-aware base and the detail riding on it, then put the detail back with a
/// different gain. Texture uses a small radius, so it moves pores, fabric and
/// foliage; Clarity a large one, so it moves the broad modelling of a face or a
/// cloudbank. Both use a <i>guided</i> filter for the split rather than a
/// Gaussian, because an unsharp mask at Clarity's radius is precisely the
/// operator that produces the grey halo around every horizon — the base has to
/// follow edges or the detail layer inherits them.</para>
///
/// <para><b>Clarity is additionally midtone-weighted.</b> Local contrast pushed
/// into the deep shadows crushes them and into the highlights clips them, so the
/// gain is scaled by how central a pixel's tone is. Texture is not weighted: it
/// works at a scale small enough that it has nowhere to push a pixel to.</para>
///
/// <para><b>Dehaze is a dark-channel-prior recovery</b> [He, Sun, Tang 2009]
/// operating in scene-linear RGB, where the haze model is actually valid.
/// See <see cref="EstimateAirlight"/> for the one part of it that is a global
/// statistic, and why that matters here.</para>
///
/// <para>Every operator is a bit-exact no-op at its neutral value, so the
/// baseline render the tone LUT is calibrated against is unchanged.</para>
/// </summary>
public static class Effects
{
    // ── Radii ───────────────────────────────────────────────────────────────
    // All scale with the image's short edge, so an edit made on the 1920 px
    // preview lands the same way on a full-resolution export. This is the
    // opposite of Detail's choice — sharpening radius is in native pixels
    // because it is about the sensor and the print, whereas these three are
    // about *regions of the subject*, which are a property of the photograph
    // rather than of its sampling.

    /// <summary>Texture's base/detail radius — fine structure.</summary>
    public static int TextureRadius(int w, int h)
        => Math.Clamp(Math.Min(w, h) / 400, 1, 12);

    /// <summary>Clarity's base/detail radius — broad modelling.</summary>
    public static int ClarityRadius(int w, int h)
        => Math.Clamp(Math.Min(w, h) / 60, 6, 120);

    /// <summary>Window the dark channel is minimised over.</summary>
    public static int DehazeMinRadius(int w, int h)
        => Math.Clamp(Math.Min(w, h) / 200, 2, 24);

    /// <summary>Radius the transmission map is smoothed at before it is used.</summary>
    public static int DehazeRefineRadius(int w, int h)
        => Math.Clamp(Math.Min(w, h) / 50, 8, 100);

    /// <summary>The widest neighbourhood anything in this module reads. The
    /// renderer adds this to a mask crop's padding so a locally-rendered region
    /// sees the same context the full frame does.</summary>
    public static int MaxRegionRadius(int w, int h)
        => Math.Max(ClarityRadius(w, h), DehazeRefineRadius(w, h));

    // ── Texture ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scale the fine detail layer of <paramref name="luma"/> (0…255 perceptual
    /// units) in place. −100 removes it entirely — the skin-smoothing end — and
    /// +100 doubles it.
    /// </summary>
    public static void ApplyTexture(float[] luma, int w, int h, double amount,
                                    int contextW, int contextH, ParallelOptions po)
    {
        double k = Math.Clamp(amount, -100.0, 100.0) / 100.0;
        if (k == 0.0) return;

        int radius = TextureRadius(contextW, contextH);
        // ~4% of the range squared: variation smaller than this is texture,
        // larger is an edge that must survive into the base.
        const float eps = 104f;

        var baseLayer = new float[luma.Length];
        GuidedBase(luma, baseLayer, w, h, radius, eps, po);

        float gain = (float)(1.0 + k);
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float b = baseLayer[i];
                luma[i] = b + (luma[i] - b) * gain;
            }
        });
    }

    // ── Clarity ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Add (or remove) broad local contrast, weighted toward the midtones.
    /// </summary>
    public static void ApplyClarity(float[] luma, int w, int h, double amount,
                                    int contextW, int contextH, ParallelOptions po)
    {
        double k = Math.Clamp(amount, -100.0, 100.0) / 100.0;
        if (k == 0.0) return;

        int radius = ClarityRadius(contextW, contextH);
        // Deliberately looser than Texture's: at this radius the "edges" worth
        // protecting are whole subject boundaries, not local contrast.
        const float eps = 420f;

        var baseLayer = new float[luma.Length];
        GuidedBase(luma, baseLayer, w, h, radius, eps, po);

        float gain = (float)k;
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float v = luma[i];

                // 4·L·(1−L) peaks at 1 in the middle and reaches 0 at both ends;
                // the square root broadens it so three-quarter tones still get
                // most of the effect instead of only the exact midtone.
                float L = v * (1f / 255f);
                if (L < 0f) L = 0f; else if (L > 1f) L = 1f;
                float mid = MathF.Sqrt(4f * L * (1f - L));

                float b = baseLayer[i];
                luma[i] = v + (v - b) * gain * mid;
            }
        });
    }

    // ── Dehaze ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The haze colour, in <b>un-gained sensor units</b> (0…1 of sensor
    /// saturation, before exposure or white balance).
    ///
    /// <para>Storing it before the gains, rather than in the working space it
    /// was measured in, is what makes it a property of the <i>photograph</i>.
    /// Haze radiance does not change because the exposure slider moved; the
    /// numbers representing it do, and by exactly the gain. Keeping the gain out
    /// of the stored value and re-applying it per render (<see cref="Gained"/>)
    /// means a mask that changes Exposure dehazes against the same haze the rest
    /// of the frame does — hold the gained value instead and the mask's region
    /// is dehazed against an airlight that is wrong by its own exposure offset.</para>
    /// </summary>
    public readonly record struct Airlight(float R, float G, float B)
    {
        public bool IsValid => R > 0f && G > 0f && B > 0f;

        /// <summary>This airlight in a working space with the given per-channel
        /// gains applied. Floored well above zero because the recovery divides
        /// by the airlight, and a near-black one would become an unbounded gain.</summary>
        public Airlight Gained(double gainR, double gainG, double gainB)
        {
            const float floor = 0.05f;
            return new Airlight(
                MathF.Max((float)(R * gainR), floor),
                MathF.Max((float)(G * gainG), floor),
                MathF.Max((float)(B * gainB), floor));
        }
    }

    // The airlight depends only on the buffer contents, yet the tile path used to
    // re-estimate it on every render — at 100% zoom that walks the whole 45 MP
    // frame to recompute a number that has not changed since the last drag. Memoise
    // it on the pixel array itself: the weak table drops the entry when the buffer
    // is collected, so the preview, full and scaled buffers each keep their own
    // estimate with no lifetime plumbing, and a repeated drag becomes a table hit.
    private static readonly ConditionalWeakTable<ushort[], StrongBox<Airlight>> AirlightCache = new();

    /// <summary>
    /// Estimate the haze colour from the raw sensor buffer: the average RGB of
    /// the pixels whose dark channel is brightest, which is where the haze is
    /// thickest.
    ///
    /// <para><b>This must be computed once, on the whole image.</b> It is the
    /// only quantity in the pipeline derived from global statistics, and that
    /// makes it the one thing a mask's cropped re-render cannot work out for
    /// itself — a crop of clear foreground would estimate a completely different
    /// airlight from the full frame and dehaze its region against it, leaving
    /// the mask's outline visible as a step in colour. So the renderer estimates
    /// it from the full frame and hands the same value to every crop.</para>
    ///
    /// <para>Taken from the <i>ungained</i> buffer so the answer does not depend
    /// on which settings the pass that happened to compute it was carrying —
    /// see <see cref="Airlight"/>.</para>
    /// </summary>
    public static Airlight EstimateAirlightFromSensor(ushort[] src, int w, int h)
    {
        int n = w * h;
        if (n == 0 || src.Length < n * 3) return default;

        // ConditionalWeakTable serialises the factory per key, so a concurrent pair
        // of tile renders computes the estimate at most once, then reuses it.
        return AirlightCache.GetValue(src,
            key => new StrongBox<Airlight>(ComputeAirlightFromSensor(key, w, h, n))).Value;
    }

    /// <summary>
    /// The dark-channel airlight straight off the interleaved sensor buffer, with
    /// no intermediate float planes — the histogram needs only the per-pixel dark
    /// value, computable from <paramref name="src"/> directly. This drops the four
    /// full-frame <c>float[]</c> allocations the plane path made (~720 MB of LOH
    /// garbage per estimate at 45 MP) to zero. Bit-identical to
    /// <see cref="EstimateAirlight"/> over the same buffer: the histogram is integer
    /// so its parallel per-thread merge is order-independent, and the averaging pass
    /// is kept serial so its double summation runs in the buffer's own order.
    /// </summary>
    private static Airlight ComputeAirlightFromSensor(ushort[] src, int w, int h, int n)
    {
        const float inv = 1f / 65535f;
        const int Bins = 1024;

        // Pass 1 — histogram of the per-pixel dark channel. Parallel over rows with a
        // thread-local histogram merged at the end; integer counts make the merge
        // exact however the rows were partitioned, so the cutoff below is unchanged.
        var histogram = new int[Bins];
        Parallel.For(0, h, () => new int[Bins],
            (y, _, local) =>
            {
                int o = y * w * 3;
                for (int x = 0; x < w; x++, o += 3)
                {
                    float d = MathF.Min(src[o] * inv, MathF.Min(src[o + 1] * inv, src[o + 2] * inv));
                    if (d < 0f) d = 0f; else if (d > 1f) d = 1f;
                    local[(int)(d * (Bins - 1))]++;
                }
                return local;
            },
            local => { lock (histogram) { for (int bin = 0; bin < Bins; bin++) histogram[bin] += local[bin]; } });

        int target = Math.Max(1, n / 1000);
        int seen = 0, cutoff = Bins - 1;
        for (int bin = Bins - 1; bin >= 0; bin--)
        {
            seen += histogram[bin];
            if (seen >= target) { cutoff = bin; break; }
        }

        float threshold = cutoff / (float)(Bins - 1);

        // Pass 2 — average the pixels at or above the cutoff. Serial on purpose:
        // double addition is not associative, so summing in the buffer's own order
        // is what keeps the result bit-identical to the float-plane path.
        double sr = 0, sg = 0, sb = 0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            float rr = src[o] * inv, gg = src[o + 1] * inv, bb = src[o + 2] * inv;
            float d = MathF.Min(rr, MathF.Min(gg, bb));
            if (d < 0f) d = 0f; else if (d > 1f) d = 1f;
            if (d < threshold) continue;
            sr += rr; sg += gg; sb += bb; count++;
        }
        if (count == 0) return default;

        return new Airlight((float)(sr / count), (float)(sg / count), (float)(sb / count));
    }

    /// <summary>
    /// Estimate the haze colour from linear RGB planes.
    ///
    /// <para><b>Values are expected in 0…1</b> — the dark-channel histogram
    /// clamps there, so the estimate is not invariant to a scale factor that
    /// pushes pixels past white. That is not a limitation in practice because
    /// the pipeline always calls <see cref="EstimateAirlightFromSensor"/>, where
    /// the range is 0…1 by construction; it is a trap only for a caller that
    /// hands over already-gained planes and expects the two to agree.</para>
    /// </summary>
    public static Airlight EstimateAirlight(float[] r, float[] g, float[] b, int w, int h)
    {
        int n = w * h;
        if (n == 0) return default;

        // Histogram of the per-pixel dark channel, then walk down from the top
        // to find the brightest 0.1% — a percentile without a sort.
        const int Bins = 1024;
        var histogram = new int[Bins];
        var dark = new float[n];
        for (int i = 0; i < n; i++)
        {
            float d = MathF.Min(r[i], MathF.Min(g[i], b[i]));
            if (d < 0f) d = 0f; else if (d > 1f) d = 1f;
            dark[i] = d;
            histogram[(int)(d * (Bins - 1))]++;
        }

        int target = Math.Max(1, n / 1000);
        int seen = 0, cutoff = Bins - 1;
        for (int bin = Bins - 1; bin >= 0; bin--)
        {
            seen += histogram[bin];
            if (seen >= target) { cutoff = bin; break; }
        }

        float threshold = cutoff / (float)(Bins - 1);
        double sr = 0, sg = 0, sb = 0;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (dark[i] < threshold) continue;
            sr += r[i]; sg += g[i]; sb += b[i];
            count++;
        }
        if (count == 0) return default;

        // Deliberately unfloored — the floor belongs at the point of use, where
        // the gains have been applied. Flooring here would make the stored value
        // depend on the scale it was measured at and break the equivalence
        // Airlight documents.
        return new Airlight((float)(sr / count), (float)(sg / count), (float)(sb / count));
    }

    /// <summary>
    /// Remove (positive) or add (negative) atmospheric haze, in place, on
    /// scene-linear RGB planes. <paramref name="airlight"/> must come from
    /// <see cref="EstimateAirlight"/> over the <i>whole</i> image.
    /// </summary>
    public static void ApplyDehaze(float[] r, float[] g, float[] b, int w, int h,
                                   double amount, Airlight airlight,
                                   int contextW, int contextH, ParallelOptions po)
    {
        double k = Math.Clamp(amount, -100.0, 100.0) / 100.0;
        if (k == 0.0 || !airlight.IsValid) return;

        int n = w * h;

        // ── Dark channel: per-pixel min over RGB, then over a window ──
        // The window minimum is what makes the prior work: a hazy patch has no
        // dark pixel anywhere near it, while a clear patch almost always does.
        var dark = new float[n];
        float ar = airlight.R, ag = airlight.G, ab = airlight.B;
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                // Normalised by the airlight, per the standard formulation:
                // the prior is about how dark a pixel is *relative to the haze*.
                float v = MathF.Min(r[i] / ar, MathF.Min(g[i] / ag, b[i] / ab));
                dark[i] = v < 0f ? 0f : v;
            }
        });

        MinFilter(dark, w, h, DehazeMinRadius(contextW, contextH), po);

        // Smooth the transmission before using it. Straight from the min filter
        // it is blocky, and blocky transmission shows up as blocky colour.
        var transmission = new float[n];
        GuidedBase(dark, transmission, w, h, DehazeRefineRadius(contextW, contextH), 1e-3f, po);

        bool removing = k > 0.0;
        float strength = (float)Math.Abs(k);
        // Never claim to remove *all* the haze: leaving a little keeps distance
        // legible, which is the usual reason the fully-dehazed look reads wrong.
        float omega = 0.92f * strength;
        const float minT = 0.12f;

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float t = 1f - omega * transmission[i];
                if (t < minT) t = minT; else if (t > 1f) t = 1f;

                if (removing)
                {
                    // J = (I − A)/t + A, the haze model solved for the scene.
                    r[i] = (r[i] - ar) / t + ar;
                    g[i] = (g[i] - ag) / t + ag;
                    b[i] = (b[i] - ab) / t + ab;
                }
                else
                {
                    // The same model run forward: composite the scene back
                    // toward the airlight, most where the prior says it is
                    // already distant.
                    float m = (1f - t) * 0.65f;
                    r[i] += (ar - r[i]) * m;
                    g[i] += (ag - g[i]) * m;
                    b[i] += (ab - b[i]) * m;
                }

                if (r[i] < 0f) r[i] = 0f;
                if (g[i] < 0f) g[i] = 0f;
                if (b[i] < 0f) b[i] = 0f;
            }
        });
    }

    // ── Grain ───────────────────────────────────────────────────────────────

    /// <summary>Per-render grain constants.</summary>
    public readonly struct GrainParams
    {
        public readonly float Amount;      // luma units at full strength
        public readonly float CellPixels;  // noise cell size, in pixels
        public readonly float Roughness;   // 0…1, second-octave mix
        public readonly bool IsActive;

        public GrainParams(float amount, float cellPixels, float roughness, bool isActive)
        {
            Amount = amount;
            CellPixels = cellPixels;
            Roughness = roughness;
            IsActive = isActive;
        }
    }

    public const double DefaultGrainSize = 25.0;
    public const double DefaultGrainRoughness = 50.0;

    public static GrainParams BuildGrain(double amount, double size, double roughness,
                                         int contextW, int contextH)
    {
        double a = Math.Clamp(amount, 0.0, 100.0) / 100.0;

        // Cell size scales with resolution so the grain is the same size
        // *relative to the picture* in the preview and the export. Grain that
        // was a pixel wide either way would be invisible in a print and
        // overwhelming on screen. It is computed even when Amount is zero so a
        // local grain mask — which force-activates this field to modulate its
        // amplitude per pixel — still gets valid field geometry to work from.
        double scale = Math.Max(1.0, Math.Min(contextW, contextH) / 1280.0);
        double cell = (1.0 + 5.0 * (Math.Clamp(size, 0.0, 100.0) / 100.0)) * scale;

        return new GrainParams(
            (float)(a * 26.0),
            (float)cell,
            (float)(Math.Clamp(roughness, 0.0, 100.0) / 100.0),
            a > 0.0);
    }

    /// <summary>Full-strength grain amplitude, in luma units, for a given
    /// amount slider — the per-pixel currency a local grain mask accumulates into.
    /// Kept next to <see cref="BuildGrain"/> so the two never drift.</summary>
    public static float GrainAmplitude(double amount)
        => (float)(Math.Clamp(amount, 0.0, 100.0) / 100.0 * 26.0);

    /// <summary>
    /// Add monochrome film grain to RGB planes in place, using absolute image
    /// coordinates so the pattern is a property of the photograph.
    ///
    /// <para>Value noise from a hashed lattice rather than per-pixel random
    /// numbers: real grain is clumped, and per-pixel noise reads as digital
    /// speckle however it is scaled. Two octaves, mixed by Roughness.</para>
    ///
    /// <para>Applied equally to all three channels, so the grain is neutral —
    /// colour grain looks like sensor noise, which is the thing photographers
    /// are usually adding grain to disguise.</para>
    /// </summary>
    public static void ApplyGrain(float[] r, float[] g, float[] b, int w, int h,
                                  in GrainParams p, ParallelOptions po)
        => ApplyGrain(r, g, b, w, h, 0, 0, p, null, po);

    /// <summary>
    /// As the whole-frame <see cref="ApplyGrain(float[],float[],float[],int,int,in GrainParams,ParallelOptions)"/>,
    /// but for a sub-window whose top-left sits at
    /// (<paramref name="offsetX"/>, <paramref name="offsetY"/>) in the full frame.
    /// The lattice is sampled in those absolute coordinates, so a region tile
    /// carries the exact pattern the whole-frame render would place there — which
    /// is what keeps a zoomed 1:1 tile identical to the export. See
    /// <see cref="DevelopProcessor.RenderRegion"/>.
    /// </summary>
    public static void ApplyGrain(float[] r, float[] g, float[] b, int w, int h,
                                  int offsetX, int offsetY, in GrainParams p, ParallelOptions po)
        => ApplyGrain(r, g, b, w, h, offsetX, offsetY, p, null, po);

    /// <summary>
    /// As the offset overload, but with an optional per-pixel amplitude map — one
    /// grain amount (in luma units, as <see cref="GrainParams.Amount"/>) for each
    /// pixel of the window. This is how a local grain mask works: the field's
    /// geometry (cell size, roughness, absolute lattice) stays one thing across
    /// the whole photograph, and only how <i>much</i> of it shows varies with the
    /// mask weight. When <paramref name="amounts"/> is null the flat
    /// <see cref="GrainParams.Amount"/> is used and this is the plain grain pass.
    /// </summary>
    public static void ApplyGrain(float[] r, float[] g, float[] b, int w, int h,
                                  int offsetX, int offsetY, in GrainParams p,
                                  float[]? amounts, ParallelOptions po)
    {
        // With a per-pixel map the field may be flat-inactive (global grain 0)
        // yet still carry grain where a mask adds it, so the map's presence is
        // itself a reason to run.
        if (!p.IsActive && amounts is null) return;

        float inv1 = 1f / MathF.Max(p.CellPixels, 0.5f);
        float inv2 = 1f / MathF.Max(p.CellPixels * 0.45f, 0.5f);
        float mix2 = p.Roughness * 0.5f;
        float mix1 = 1f - mix2;
        float flatAmount = p.Amount;

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            float ay = y + offsetY;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float amount = amounts is null ? flatAmount : amounts[i];
                if (amount == 0f) continue;

                float ax = x + offsetX;

                float noise = mix1 * ValueNoise(ax * inv1, ay * inv1, 0)
                            + mix2 * ValueNoise(ax * inv2, ay * inv2, 1);
                noise = noise * 2f - (mix1 + mix2);   // centre on zero

                // Fade out of the highlights. Film grain lives in the emulsion's
                // active range; a blown sky has none, and adding it there is the
                // giveaway that the effect is synthetic.
                float L = r[i] * 0.2126f + g[i] * 0.7152f + b[i] * 0.0722f;
                L *= 1f / 255f;
                if (L < 0f) L = 0f; else if (L > 1f) L = 1f;
                float weight = 1f - BasicToneSmoothStep(0.55f, 1f, L) * 0.85f;

                float d = noise * amount * weight;
                r[i] += d;
                g[i] += d;
                b[i] += d;
            }
        });
    }

    private static float BasicToneSmoothStep(float edge0, float edge1, float x)
    {
        float u = (x - edge0) / (edge1 - edge0);
        if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
        return u * u * (3f - 2f * u);
    }

    /// <summary>Bilinearly-interpolated hashed lattice noise in 0…1.</summary>
    private static float ValueNoise(float x, float y, int octave)
    {
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        // Smootherstep on the interpolant, so cell boundaries do not show up as
        // a visible grid in flat areas.
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);

        float n00 = Hash(x0, y0, octave);
        float n10 = Hash(x0 + 1, y0, octave);
        float n01 = Hash(x0, y0 + 1, octave);
        float n11 = Hash(x0 + 1, y0 + 1, octave);

        float a = n00 + (n10 - n00) * fx;
        float bq = n01 + (n11 - n01) * fx;
        return a + (bq - a) * fy;
    }

    private static float Hash(int x, int y, int octave)
    {
        uint n = unchecked((uint)(x * 374761393 + y * 668265263 + octave * 1274126177));
        n = (n ^ (n >> 13)) * 1274126177u;
        n ^= n >> 16;
        return (n & 0xFFFFFF) * (1f / 16777216f);
    }

    // ── Shared filters ──────────────────────────────────────────────────────

    /// <summary>
    /// Sliding-window minimum, separable, clamp-extend edges. Monotonic-deque
    /// form, so the cost is independent of the radius — a naive window minimum
    /// at Dehaze's radius would dominate the whole render.
    /// </summary>
    private static void MinFilter(float[] plane, int w, int h, int radius, ParallelOptions po)
    {
        if (radius <= 0) return;
        var tmp = new float[plane.Length];

        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            MinPass(plane, row, 1, w, radius, tmp, row, 1);
        });

        Parallel.For(0, w, po, x =>
        {
            MinPass(tmp, x, w, h, radius, plane, x, w);
        });
    }

    /// <summary>One 1-D minimum pass over a strided line.</summary>
    private static void MinPass(float[] src, int srcOffset, int srcStride, int length,
                                int radius, float[] dst, int dstOffset, int dstStride)
    {
        var deque = new int[length + 1];
        int head = 0, tail = 0;

        // Prime the window with the clamp-extended left edge, which is just the
        // first sample repeated — so seeding with index 0 covers all of it.
        for (int i = 0; i <= radius && i < length; i++)
        {
            float v = src[srcOffset + i * srcStride];
            while (tail > head && src[srcOffset + deque[tail - 1] * srcStride] >= v) tail--;
            deque[tail++] = i;
        }

        for (int i = 0; i < length; i++)
        {
            dst[dstOffset + i * dstStride] = src[srcOffset + deque[head] * srcStride];

            // Advance: drop anything that has fallen out on the left, admit the
            // next sample on the right (clamped, so past the end nothing new
            // enters and the window simply shrinks against the final value).
            int outgoing = i - radius;
            if (outgoing >= 0 && deque[head] == outgoing) head++;

            int incoming = i + radius + 1;
            if (incoming < length)
            {
                float v = src[srcOffset + incoming * srcStride];
                while (tail > head && src[srcOffset + deque[tail - 1] * srcStride] >= v) tail--;
                deque[tail++] = incoming;
            }
        }
    }

    /// <summary>
    /// Self-guided filter [He, Sun, Tang 2010] — an edge-preserving base layer.
    /// Same shape as the one in <see cref="LocalHighlights"/>, kept local so this
    /// module can choose its own radius and epsilon per operator.
    /// </summary>
    private static void GuidedBase(float[] src, float[] baseOut, int w, int h,
                                   int radius, float eps, ParallelOptions po)
    {
        int n = w * h;
        // One scratch plane shared by every box blur, blurs done in place — the
        // pattern Detail.GuidedSmooth adopted after GC, not arithmetic, came to
        // dominate a slider drag. Five planes instead of the naive eleven.
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

    // Sliding-window separable box blur, clamp-extend edges — the same one the
    // other spatial modules carry. <paramref name="scratch"/> holds the horizontal
    // pass so callers can reuse one plane; src and dst may alias.
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
        // memory-sequential within a row (the column-at-a-time form touched a new
        // cache line on every access, re-fetching each line ~16×). Each column's
        // running sum still accumulates in the exact original order, so the result
        // is byte-identical; the inner column loops also auto-vectorise.
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
