"""
Test whether adding ONE global per-scene covariate -- the scene's own mean
exposure M = mean(ev_n) over unclipped pixels -- to the existing local-base
amplitude model collapses the "scene-adaptivity" gap (feature_corr.py found
pull_-100 correlates r=+0.94 with M).

Model: dEV/k = A(B) + gamma*(M - M0)   [per direction d, pooled over magnitudes]
vs the current: dEV/k = A(B)

A(bin) and gamma are estimated by the exact within-bin (Frisch-Waugh-Lovell)
estimator: because M is constant across all pixels of one scene, gamma is
identified from between-scene variation in each bin's cell mean, weighted by
how many pixels each scene contributes to that bin -- no approximation.

Fit on D1 only; evaluate both the OLD (base-only) and NEW (base+M) curves as
held-out RMS on D2, to see whether the extra covariate is worth the added
engineering (a global mean-exposure reduction the render pipeline would need
to compute once per frame).
"""
import os, glob
import numpy as np
import fit
from fit import CACHE, SLIDERS, CENT, NB, EDGES, guided_base, bin_idx, smooth_curve, ev_to_code, DS


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
        d = {"stem": stem, "ev_n": block_down(z["ev_n"].astype(np.float32), DS),
             "clip_n": clip_down(z["clip_n"], DS)}
        for s in SLIDERS:
            d[f"ev_{s}"] = block_down(z[f"ev_{s}"].astype(np.float32), DS)
            d[f"clip_{s}"] = clip_down(z[f"clip_{s}"], DS)
        d["M"] = float(np.mean(d["ev_n"][~d["clip_n"]]))
        scenes.append(d)
    return stems, scenes


def fit_joint(scenes, bases, M0):
    """Per direction: cell (scene,bin) accumulators -> A(bin), gamma via FWL."""
    out = {}
    for d in (-1, 1):
        n_sb, sy_sb = [], []   # one row per scene: arrays over bins
        M_list = []
        for sc, (B, D, idx) in zip(scenes, bases):
            n_bin = np.zeros(NB); sy_bin = np.zeros(NB)
            clip_n = sc["clip_n"]
            for s in SLIDERS:
                if (s < 0) != (d == -1):
                    continue
                k = abs(s) / 100.0
                valid = (~clip_n) & (~sc[f"clip_{s}"])
                y = ((sc[f"ev_{s}"] - sc["ev_n"]) / k)[valid]
                ii = idx[valid]
                n_bin += np.bincount(ii, minlength=NB)[:NB]
                sy_bin += np.bincount(ii, y, minlength=NB)[:NB]
            n_sb.append(n_bin); sy_sb.append(sy_bin); M_list.append(sc["M"] - M0)
        n_sb = np.array(n_sb); sy_sb = np.array(sy_sb); M_arr = np.array(M_list)  # (nscenes, NB), (nscenes,)

        n_bin_tot = n_sb.sum(axis=0)                      # (NB,)
        y_bin_mean = np.divide(sy_sb.sum(axis=0), n_bin_tot, out=np.zeros(NB), where=n_bin_tot > 0)
        M_bin_mean = np.divide((n_sb * M_arr[:, None]).sum(axis=0), n_bin_tot,
                                out=np.zeros(NB), where=n_bin_tot > 0)

        rM = M_arr[:, None] - M_bin_mean[None, :]          # (nscenes, NB)
        cellmean_y = np.divide(sy_sb, n_sb, out=np.full_like(sy_sb, np.nan), where=n_sb > 0)
        rY = cellmean_y - y_bin_mean[None, :]

        w = n_sb
        m = n_sb > 300
        num = np.nansum(np.where(m, w * rM * rY, 0.0))
        den = np.nansum(np.where(m, w * rM * rM, 0.0))
        gamma = num / den if den > 0 else 0.0

        A = y_bin_mean - gamma * M_bin_mean
        out[d] = (A, gamma, n_bin_tot)
    return out


def apply_joint(B, Dd, s, M, M0, curves_old, curves_new):
    k = abs(s) / 100.0; d = -1 if s < 0 else 1
    A_old = curves_old[d]
    A_new, gamma = curves_new[d][0], curves_new[d][1]
    pred_old = B + k * np.interp(B, CENT, A_old) + Dd
    pred_new = B + k * (np.interp(B, CENT, A_new) + gamma * (M - M0)) + Dd
    return pred_old, pred_new


def rms_two(scenes, bases, curves_old, curves_new, M0):
    sse_old = sse_new = sse_base = 0.0; N = 0
    per_s_old = {s: [0.0, 0] for s in SLIDERS}
    per_s_new = {s: [0.0, 0] for s in SLIDERS}
    for sc, (B, Dd, idx) in zip(scenes, bases):
        clip_n = sc["clip_n"]; code_n = ev_to_code(sc["ev_n"])
        for s in SLIDERS:
            valid = (~clip_n) & (~sc[f"clip_{s}"])
            code_s = ev_to_code(sc[f"ev_{s}"])
            pred_old, pred_new = apply_joint(B, Dd, s, sc["M"], M0, curves_old, curves_new)
            code_old = ev_to_code(pred_old); code_new = ev_to_code(pred_new)
            e_old = (code_old - code_s)[valid]; e_new = (code_new - code_s)[valid]
            e_base = (code_n - code_s)[valid]
            sse_old += float(np.sum(e_old ** 2)); sse_new += float(np.sum(e_new ** 2))
            sse_base += float(np.sum(e_base ** 2)); N += int(valid.sum())
            per_s_old[s][0] += float(np.sum(e_old ** 2)); per_s_old[s][1] += int(valid.sum())
            per_s_new[s][0] += float(np.sum(e_new ** 2)); per_s_new[s][1] += int(valid.sum())
    print(f"    old(base-only)={np.sqrt(sse_old/N):.3f}  new(base+M)={np.sqrt(sse_new/N):.3f}  nothing={np.sqrt(sse_base/N):.3f}")
    for s in SLIDERS:
        o = np.sqrt(per_s_old[s][0] / max(per_s_old[s][1], 1))
        nn = np.sqrt(per_s_new[s][0] / max(per_s_new[s][1], 1))
        print(f"      S={s:+4d}: old={o:6.2f}  new={nn:6.2f}")


def main():
    d1_stems, d1 = load_group("D1")
    d2_stems, d2 = load_group("D2")
    print(f"D1: {len(d1)} scenes, D2: {len(d2)} scenes")
    R = 8
    bases1 = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in d1]
    bases2 = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in d2]

    M0 = float(np.mean([sc["M"] for sc in d1]))
    print(f"M0 (D1 mean scene exposure, reference point) = {M0:+.3f} EV")

    # old (base-only) curves, exactly as fit.py does
    old_curves_raw = fit.fit_curves(d1, bases1)
    curves_old = {d: smooth_curve(old_curves_raw[d][0], old_curves_raw[d][2]) for d in (-1, 1)}

    # new (base + gamma*M) curves
    joint = fit_joint(d1, bases1, M0)
    curves_new = {d: (smooth_curve(joint[d][0], joint[d][2]), joint[d][1]) for d in (-1, 1)}
    for d in (-1, 1):
        print(f"  direction {d:+d}: gamma = {joint[d][1]:+.4f} EV per EV of scene mean exposure")

    print("\n--- D1 in-sample ---")
    rms_two(d1, bases1, curves_old, curves_new, M0)
    print("\n--- D2 held-out ---")
    rms_two(d2, bases2, curves_old, curves_new, M0)


if __name__ == "__main__":
    main()
