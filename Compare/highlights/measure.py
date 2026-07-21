"""
Measure Lightroom's Highlights slider response across the dataset, in the exact
scene-linear EV-about-middle-grey space LocalHighlights operates in.

For each scene and each non-zero slider level S:
  * invert RAWR's output transform on the neutral (h000) and slider JPEGs to get
    per-pixel scene-linear luminance EV (ev_n, ev_s);
  * guided-filter ev_n to the regional base B (same self-guided filter, eps=0.25 EV^2
    as LocalHighlights), so we characterise LR's *regional* response, not per-pixel;
  * bin dEV = ev_s - ev_n by B; within each base bin also regress dEV on the detail
    residual d = ev_n - B to recover the detail-layer gain LR applies.

Pixels clipped (any channel >=253) in either image are excluded from the band fit
(that's the above-white headroom 8-bit JPEGs cannot measure). We separately count
'recovered' pixels: clipped at neutral, unclipped at the slider.

Output: highlights_measured.npz with per-slider mean-dEV(B) curves + detail slopes,
aggregated over all scenes.
"""
import os, re, sys, glob, time
import numpy as np
from PIL import Image
from scipy.ndimage import uniform_filter
import rawr_transform as rt

DATA = r"C:\Users\Torsten\Desktop\Datasets\Highlights"
DRAFT = (760, 1140)      # ask PIL for ~1/8 scale during JPEG decode (fast)
GUIDED_EPS = 0.25        # EV^2, matches LocalHighlights.Options.GuidedEpsilon
GUIDED_RADIUS = 8        # ~ full-res radius 64 at 1/8 decode
CLIP_CODE = 253

BIN_LO, BIN_HI, BIN_W = -6.0, 4.0, 0.1
BIN_EDGES = np.arange(BIN_LO, BIN_HI + BIN_W, BIN_W)
NBINS = BIN_EDGES.size - 1
BIN_CENT = 0.5 * (BIN_EDGES[:-1] + BIN_EDGES[1:])

SLIDERS = [-100, -75, -50, -25, 25, 50, 75, 100]


def scene_stems():
    stems = set()
    for p in glob.glob(os.path.join(DATA, "*_h000.jpg")):
        stems.add(os.path.basename(p)[:-len("_h000.jpg")])
    return sorted(stems)


def load_codes(path):
    im = Image.open(path)
    im.draft("RGB", DRAFT)          # decode at reduced DCT scale
    im = im.convert("RGB")
    return np.asarray(im, dtype=np.uint8)


def guided_base(ev):
    r = GUIDED_RADIUS
    size = 2 * r + 1
    mean = uniform_filter(ev, size, mode="nearest")
    corr = uniform_filter(ev * ev, size, mode="nearest")
    var = np.maximum(corr - mean * mean, 0.0)
    a = var / (var + GUIDED_EPS)
    b = (1.0 - a) * mean
    return uniform_filter(a, size, mode="nearest") * ev + uniform_filter(b, size, mode="nearest")


def suffix(s):
    return f"h{'+' if s > 0 else '-'}{abs(s):03d}"


class Accum:
    """Per-slider accumulator: enough moments per base-bin to get mean dEV and the
    least-squares slope of dEV on detail residual."""
    def __init__(self):
        self.n   = np.zeros(NBINS)
        self.sy  = np.zeros(NBINS)   # sum dEV
        self.sd  = np.zeros(NBINS)   # sum detail
        self.sdd = np.zeros(NBINS)   # sum detail^2
        self.sdy = np.zeros(NBINS)   # sum detail*dEV
        self.recovered = 0
        self.clip_excluded = 0
        self.total = 0

    def add(self, base, detail, dEV):
        idx = np.digitize(base, BIN_EDGES) - 1
        m = (idx >= 0) & (idx < NBINS)
        idx = idx[m]; d = detail[m]; y = dEV[m]
        np.add.at(self.n,   idx, 1.0)
        np.add.at(self.sy,  idx, y)
        np.add.at(self.sd,  idx, d)
        np.add.at(self.sdd, idx, d * d)
        np.add.at(self.sdy, idx, d * y)

    def curves(self):
        n = np.maximum(self.n, 1)
        mean_dev = self.sy / n
        # slope of y on d within each bin: cov(d,y)/var(d)
        md = self.sd / n
        my = self.sy / n
        cov = self.sdy / n - md * my
        var = self.sdd / n - md * md
        slope = np.where(var > 1e-6, cov / np.maximum(var, 1e-9), np.nan)
        return mean_dev, slope, self.n.copy()


def main():
    stems = scene_stems()
    print(f"{len(stems)} scenes, sliders {SLIDERS}")
    acc = {s: Accum() for s in SLIDERS}
    t0 = time.time()
    for si, stem in enumerate(stems):
        neutral_path = os.path.join(DATA, f"{stem}_h000.jpg")
        codes_n = load_codes(neutral_path)
        ev_n, clip_n = rt.code_to_ev_luma(codes_n)
        base_n = guided_base(ev_n)
        detail_n = ev_n - base_n
        for s in SLIDERS:
            p = os.path.join(DATA, f"{stem}_{suffix(s)}.jpg")
            if not os.path.exists(p):
                print(f"  MISSING {p}"); continue
            codes_s = load_codes(p)
            if codes_s.shape != codes_n.shape:
                print(f"  SIZE MISMATCH {stem} {s}: {codes_s.shape} vs {codes_n.shape}"); continue
            ev_s, clip_s = rt.code_to_ev_luma(codes_s)
            a = acc[s]
            a.total += ev_n.size
            recov = clip_n & (~clip_s)
            a.recovered += int(recov.sum())
            valid = (~clip_n) & (~clip_s)
            a.clip_excluded += int((~valid).sum())
            a.add(base_n[valid], detail_n[valid], (ev_s - ev_n)[valid])
        print(f"[{si+1:2d}/{len(stems)}] {stem}  ({time.time()-t0:5.1f}s)")

    np.savez(
        "highlights_measured.npz",
        bin_cent=BIN_CENT,
        sliders=np.array(SLIDERS),
        **{f"mean_dev_{s}": acc[s].curves()[0] for s in SLIDERS},
        **{f"slope_{s}":    acc[s].curves()[1] for s in SLIDERS},
        **{f"count_{s}":    acc[s].curves()[2] for s in SLIDERS},
        recovered=np.array([acc[s].recovered for s in SLIDERS]),
        clip_excluded=np.array([acc[s].clip_excluded for s in SLIDERS]),
        total=np.array([acc[s].total for s in SLIDERS]),
    )
    print("\nSaved highlights_measured.npz")
    # Quick console summary: peak pull/push and where it lands.
    for s in SLIDERS:
        md, slope, cnt = acc[s].curves()
        good = cnt > 2000
        if good.any():
            if s < 0:
                j = np.nanargmin(np.where(good, md, np.nan))
            else:
                j = np.nanargmax(np.where(good, md, np.nan))
            print(f" S={s:+4d}: peak dEV {md[j]:+.3f} at base {BIN_CENT[j]:+.2f} EV "
                  f"({rt.display_transform(np.array([rt.MIDDLE_GRAY*2**BIN_CENT[j]]))[0]*255:.0f}/255); "
                  f"recovered px={acc[s].recovered:,}")


if __name__ == "__main__":
    main()
