namespace Rawr.Develop;

/// <summary>
/// Edge-preserving luminance smoother used by the v3 (edge-aware) tone sliders.
/// Produces a per-pixel "regional luminance" plane, expressed in EV relative to
/// middle grey, via a self-guided filter [He, Sun, Tang 2010] operating in
/// log space. Flat regions are strongly averaged while high-contrast edges are
/// preserved.
///
/// <para>The v3 Highlights/Shadows/Whites/Blacks masks read this plane instead
/// of the per-pixel luminance. That is what makes those sliders feel like
/// Lightroom's: a dark pixel inside an otherwise bright region behaves as
/// "highlight" (its <i>region</i> is bright), and vice versa — so Shadows +
/// doesn't just lift every dark pixel in the frame, it opens up the actually-
/// shadow regions without flattening adjacent detail.</para>
///
/// <para>Linear cost (≈ four separable box blurs) and deterministic regardless
/// of core count.</para>
/// </summary>
public static class EdgeAwareLuma
{
    /// <summary>
    /// Smoothing parameter for the guided filter, in (log2-EV)² units. Edges
    /// whose local variance exceeds this are preserved; flat regions of
    /// smaller variation get averaged. 0.25 ≈ (0.5 EV)² — about half a stop
    /// of luminance step counts as a "real" edge.
    /// </summary>
    public const float Epsilon = 0.25f;

    /// <summary>
    /// The guided-filter radius for an image of the given size: 1/16 of the
    /// short edge — large enough to be "regional" (smooths over texture, picks
    /// up the brightness of the area a pixel sits in) yet small enough not to
    /// leak across major composition boundaries.
    ///
    /// <para>Public because a caller rendering a <i>crop</i> — which is what a
    /// masked local adjustment does — must pass the radius the <b>whole</b>
    /// image would have used. Letting the crop derive its own would make the
    /// same pixel "regional" over a smaller neighbourhood inside the mask than
    /// outside it, and the v3 Shadows/Blacks masks would then disagree across
    /// the mask boundary — a visible seam produced entirely by the tiling.</para>
    /// </summary>
    public static int RegionRadius(int w, int h) => Math.Max(8, Math.Min(w, h) / 16);

    /// <summary>
    /// Build the regional-luminance plane (EV relative to middle grey) from a
    /// linear luminance buffer that has already been gained (post-WB / post-
    /// exposure). Self-guided filter in log2-EV space; radius scales with the
    /// short edge so the same regional behaviour shows up at preview and
    /// full-resolution export resolutions.
    /// </summary>
    public static float[] BuildEvBase(float[] yLinear, int w, int h, ParallelOptions po,
                                      int radius = 0)
    {
        int n = w * h;
        if (radius <= 0) radius = RegionRadius(w, h);
        const float lumaFloor = 1e-6f;
        float midGray = (float)BasicTone.MiddleGray;

        // L = log2(max(Y, floor) / midGray)
        var L = new float[n];
        Parallel.For(0, h, po, yy =>
        {
            int row = yy * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float y = yLinear[i];
                if (y < lumaFloor) y = lumaFloor;
                L[i] = MathF.Log2(y / midGray);
            }
        });

        // One scratch plane shared by every box blur, blurs done in place — the
        // GC-churn lesson Detail.GuidedSmooth already learned. Byte-identical.
        var tmp = new float[n];
        var meanL = new float[n];
        BoxBlur(L, meanL, tmp, w, h, radius, po);

        var LL = new float[n];
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) { int i = row + x; LL[i] = L[i] * L[i]; }
        });
        BoxBlur(LL, LL, tmp, w, h, radius, po);   // LL := mean(L²)

        // a = var/(var+eps); b = (1−a)·mean. Self-guided ⇒ cov(I,p) = var(I).
        var a = new float[n];
        var b = new float[n];
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = row + x;
                float vL = LL[i] - meanL[i] * meanL[i];
                if (vL < 0f) vL = 0f;
                float ai = vL / (vL + Epsilon);
                a[i] = ai;
                b[i] = (1f - ai) * meanL[i];
            }
        });

        BoxBlur(a, a, tmp, w, h, radius, po);   // a := mean(a)
        BoxBlur(b, b, tmp, w, h, radius, po);   // b := mean(b)

        // L_base = mean_a · L + mean_b. Lives in EV; v3 sliders read this directly.
        var ev = new float[n];
        Parallel.For(0, h, po, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++) { int i = row + x; ev[i] = a[i] * L[i] + b[i]; }
        });
        return ev;
    }

    /// <summary>
    /// Sliding-window separable box blur, clamp-extend edges. Same algorithm as
    /// <see cref="LocalHighlights"/>'s and <see cref="Detail"/>'s; kept local so
    /// this module is self-contained and the guided filter can choose its own
    /// radius without coupling to the noise-reduction step.
    /// </summary>
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
