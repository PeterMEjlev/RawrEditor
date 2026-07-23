"""
Fit the Highlights amplitude/detail curves on Dataset 1 ONLY (exactly as the
shipped calibration did) and evaluate against Dataset 2 -- a second batch shot
on different cameras (Sony ARW, Canon CR2 mixed in with CR3) that the fit has
never seen -- to measure true generalisation error rather than in-sample RMS.

Also reports the -100 pull's scene-to-scene spread (the "scene-adaptivity"
finding) separately per dataset, to check it isn't a Dataset-1-only artifact.

Requires cache_ev2.py to have populated cache/D1_*.npz and cache/D2_*.npz.
"""
import os, glob, time
import numpy as np
import fit
from fit import CACHE, SLIDERS, CENT, guided_base, bin_idx, fit_curves, smooth_curve, rms_eval, DS


def block_down(a, f):
    h, w = a.shape; h -= h % f; w -= w % f
    return a[:h, :w].reshape(h // f, f, w // f, f).mean(axis=(1, 3))


def clip_down(a, f):
    h, w = a.shape; h -= h % f; w -= w % f
    return a[:h, :w].reshape(h // f, f, w // f, f).any(axis=(1, 3))


def load_group(prefix):
    stems = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(CACHE, f"{prefix}_*.npz")))
    scenes = []
    for stem in stems:
        z = np.load(os.path.join(CACHE, stem + ".npz"))
        d = {"stem": stem,
             "ev_n": block_down(z["ev_n"].astype(np.float32), DS),
             "clip_n": clip_down(z["clip_n"], DS)}
        for s in SLIDERS:
            d[f"ev_{s}"] = block_down(z[f"ev_{s}"].astype(np.float32), DS)
            d[f"clip_{s}"] = clip_down(z[f"clip_{s}"], DS)
        scenes.append(d)
    return stems, scenes


def main():
    t0 = time.time()
    d1_stems, d1 = load_group("D1")
    d2_stems, d2 = load_group("D2")
    print(f"D1: {len(d1)} scenes, D2: {len(d2)} scenes  (loaded in {time.time()-t0:.0f}s)")
    if not d1 or not d2:
        print("Missing a group's cache -- run cache_ev2.py first."); return

    R = 8  # engine radius; cache-radius 8 == full-res ~64px (see fit.py header)
    bases1 = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in d1]
    bases2 = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in d2]

    curves = fit_curves(d1, bases1)
    Asm = {d: smooth_curve(curves[d][0], curves[d][2]) for d in (-1, 1)}
    Gsm = {d: smooth_curve(curves[d][1], curves[d][2]) for d in (-1, 1)}

    r_in = rms_eval(d1, bases1, Asm, Gsm)
    r_out = rms_eval(d2, bases2, Asm, Gsm)

    print("\n--- RMS (display codes), curve fitted on D1 only ---")
    print(f"  in-sample  D1 (n={len(d1):2d} scenes): model={r_in['model']['all']:.3f}   nothing={r_in['base']['all']:.3f}")
    print(f"  held-out   D2 (n={len(d2):2d} scenes): model={r_out['model']['all']:.3f}   nothing={r_out['base']['all']:.3f}")
    print(f"\n  {'S':>5} | {'in-sample D1':>12} {'nothing':>8} | {'held-out D2':>12} {'nothing':>8}")
    for s in SLIDERS:
        print(f"  {s:+5d} | {r_in['model'][s]:12.2f} {r_in['base'][s]:8.2f} | "
              f"{r_out['model'][s]:12.2f} {r_out['base'][s]:8.2f}")

    print("\n--- scene-adaptivity of the -100 pull at base~middle grey, split by dataset ---")
    j = np.argmin(np.abs(CENT - 0.0))
    for label, scenes, bases in [("D1", d1, bases1), ("D2", d2, bases2)]:
        pulls = []
        for sc, (B, D, idx) in zip(scenes, bases):
            m = (idx == j) & (~sc["clip_n"]) & (~sc["clip_-100"])
            if m.sum() > 200:
                pulls.append(float(np.mean((sc["ev_-100"] - sc["ev_n"])[m])))
        pulls = np.array(pulls)
        if pulls.size:
            print(f"  {label}: n_scenes={pulls.size:2d}  mean={pulls.mean():+.3f}  std={pulls.std():.3f}  "
                  f"range=[{pulls.min():+.2f},{pulls.max():+.2f}]")

    np.savez("holdout_result.npz", A_neg=Asm[-1], A_pos=Asm[1], G_neg=Gsm[-1], G_pos=Gsm[1], cent=CENT,
             d1_stems=np.array(d1_stems), d2_stems=np.array(d2_stems))
    print(f"\nsaved holdout_result.npz  ({time.time()-t0:.0f}s total)")


if __name__ == "__main__":
    main()
