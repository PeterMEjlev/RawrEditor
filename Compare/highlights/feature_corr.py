"""
Diagnose WHY LR's Highlights response is scene-adaptive (README/memory finding:
at matched local base, the -100 pull ranges roughly -0.06..-2.29 EV across
scenes). A purely local operator (base/detail split at one radius) cannot
depend on anything outside its neighbourhood, so if scene-to-scene variation
in the pull correlates with a GLOBAL scene statistic, that's evidence LR's
Highlights looks at more than the local region -- e.g. a histogram-relative
or auto-normalising term -- which a local radius sweep alone can't fix.

For every scene (both datasets pooled), computes:
  * pull_-100: mean dEV at slider -100 in a band around middle grey base
    (-0.3..+0.3 EV), the same quantity the "scene-adaptivity" finding used
  * candidate global features of the *neutral* image: fraction bright,
    fraction clipped, 95th/5th percentile EV, dynamic range, mean EV
then reports the Pearson correlation of each feature with the per-scene
deviation from the pooled-mean pull -- large |r| flags a real global driver;
near-zero across the board would instead point at per-scene noise / true
non-determinism (e.g. LR's own local masking) rather than a missing feature.
"""
import os, glob
import numpy as np
from scipy import stats
import fit
from fit import CACHE, SLIDERS, CENT, guided_base, bin_idx, DS


def block_down(a, f):
    h, w = a.shape; h -= h % f; w -= w % f
    return a[:h, :w].reshape(h // f, f, w // f, f).mean(axis=(1, 3))


def clip_down(a, f):
    h, w = a.shape; h -= h % f; w -= w % f
    return a[:h, :w].reshape(h // f, f, w // f, f).any(axis=(1, 3))


def load_all():
    stems = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE, "*.npz")))
    scenes = []
    for stem in stems:
        z = np.load(os.path.join(CACHE, stem + ".npz"))
        d = {"stem": stem,
             "ev_n": block_down(z["ev_n"].astype(np.float32), DS),
             "clip_n": clip_down(z["clip_n"], DS),
             "ev_-100": block_down(z["ev_-100"].astype(np.float32), DS),
             "clip_-100": clip_down(z["clip_-100"], DS)}
        scenes.append(d)
    return scenes


def main():
    scenes = load_all()
    print(f"{len(scenes)} scenes pooled (D1+D2)")
    R = 8
    rows = []
    for sc in scenes:
        ev_n, clip_n = sc["ev_n"], sc["clip_n"]
        B = guided_base(ev_n, R)
        band = (B > -0.3) & (B < 0.3)
        valid = band & (~clip_n) & (~sc["clip_-100"])
        if valid.sum() < 500:
            continue
        pull = float(np.mean((sc["ev_-100"] - ev_n)[valid]))

        vis = ~clip_n  # exclude already-clipped pixels from scene stats
        ev_vis = ev_n[vis]
        feats = {
            "frac_bright_gt1ev":  float(np.mean(ev_vis > 1.0)),
            "frac_clipped":       float(np.mean(clip_n)),
            "p95_ev":             float(np.percentile(ev_vis, 95)),
            "p5_ev":              float(np.percentile(ev_vis, 5)),
            "mean_ev":            float(np.mean(ev_vis)),
        }
        feats["dynamic_range"] = feats["p95_ev"] - feats["p5_ev"]
        rows.append((sc["stem"], pull, feats))

    pulls = np.array([r[1] for r in rows])
    print(f"\npull_-100 at base~middle grey: mean={pulls.mean():+.3f} std={pulls.std():.3f} "
          f"range=[{pulls.min():+.2f},{pulls.max():+.2f}]  (n={len(rows)} scenes)")

    feat_names = list(rows[0][2].keys())
    print(f"\n{'feature':>20} | {'pearson r':>10} | {'p-value':>10}")
    for name in feat_names:
        x = np.array([r[2][name] for r in rows])
        r, p = stats.pearsonr(x, pulls)
        print(f"{name:>20} | {r:+10.3f} | {p:10.4f}")

    print("\nper-scene detail (sorted by pull, most-recovered first):")
    print(f"{'stem':>16} {'pull_-100':>9} {'frac_bright':>11} {'frac_clip':>10} {'p95_ev':>7} {'DR':>6}")
    for stem, pull, feats in sorted(rows, key=lambda r: r[1]):
        print(f"{stem:>16} {pull:+9.3f} {feats['frac_bright_gt1ev']:11.3f} "
              f"{feats['frac_clipped']:10.3f} {feats['p95_ev']:7.2f} {feats['dynamic_range']:6.2f}")


if __name__ == "__main__":
    main()
