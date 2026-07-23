"""
Robust production fit, fixing finalize2.py's two defects:

1. The softplus curve_fit diverged (AmpSlope~1500, knee at +54 EV -- a
   degenerate near-linear parametrisation). Here the fit is BOUNDED to the
   photographically meaningful region (knee within [-3,+2] EV, width
   [0.1,2] EV, slope [0,1] so the recovery map can never invert tone).
2. gammaMag was leveraged by the 2-3 most extreme scenes (2.02 full vs
   ~1.45 without them). Here gamma is estimated by iteratively-reweighted
   least squares with a Huber weight over SCENES (residual of each scene's
   cell-mean curve), so extreme scenes inform but do not dominate.

Everything is fitted on all 59 scenes; D1->D2 generalisation was already
established by joint_model2.py. Outputs analytic constants + RMS eval of
the final analytic model (not just the LUT) on both datasets.
"""
import os, glob
import numpy as np
from scipy.optimize import curve_fit
import fit
from fit import CACHE, SLIDERS, CENT, NB, guided_base, bin_idx, smooth_curve, ev_to_code, DS


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
        d = {"stem": stem, "ev_n": block_down(z["ev_n"].astype(np.float32), DS),
             "clip_n": clip_down(z["clip_n"], DS)}
        for s in SLIDERS:
            d[f"ev_{s}"] = block_down(z[f"ev_{s}"].astype(np.float32), DS)
            d[f"clip_{s}"] = clip_down(z[f"clip_{s}"], DS)
        vis = d["ev_n"][~d["clip_n"]]
        d["Fd"] = float(np.mean(vis < -3.0))
        scenes.append(d)
    return stems, scenes


def scene_cells(scenes, bases):
    """Per direction: per-scene per-bin pixel counts and dEV/k sums."""
    out = {}
    for d in (-1, 1):
        n_sb, sy_sb = [], []
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
            n_sb.append(n_bin); sy_sb.append(sy_bin)
        out[d] = (np.array(n_sb), np.array(sy_sb))
    return out


def fit_joint_huber(cells, Fd_arr, Fd0, n_iters=8, huber_k=1.345):
    """FWL estimator for A(bin), gamma -- with Huber IRLS weights per scene."""
    out = {}
    for d in (-1, 1):
        n_sb, sy_sb = cells[d]
        M_arr = Fd_arr - Fd0
        w_scene = np.ones(len(M_arr))
        gamma = 0.0
        for _ in range(n_iters):
            w_eff = n_sb * w_scene[:, None]
            n_tot = w_eff.sum(axis=0)
            y_mean = np.divide((sy_sb * w_scene[:, None]).sum(axis=0), n_tot,
                               out=np.zeros(NB), where=n_tot > 0)
            M_mean = np.divide((w_eff * M_arr[:, None]).sum(axis=0), n_tot,
                               out=np.zeros(NB), where=n_tot > 0)
            rM = M_arr[:, None] - M_mean[None, :]
            cellmean_y = np.divide(sy_sb, n_sb, out=np.full_like(sy_sb, np.nan), where=n_sb > 0)
            rY = cellmean_y - y_mean[None, :]
            m = n_sb > 300
            num = np.nansum(np.where(m, w_eff * rM * rY, 0.0))
            den = np.nansum(np.where(m, w_eff * rM * rM, 0.0))
            gamma = num / den if den > 0 else 0.0
            A = y_mean - gamma * M_mean
            # scene-level residual: weighted mean |cellmean - (A + gamma*M)| over bins
            pred = A[None, :] + gamma * M_arr[:, None]
            res = np.where(m, cellmean_y - pred, np.nan)
            scene_res = np.nansum(np.where(m, n_sb * np.abs(res), 0.0), axis=1) / \
                        np.maximum(np.where(m, n_sb, 0.0).sum(axis=1), 1.0)
            sigma = np.median(scene_res) / 0.6745 + 1e-9
            u = scene_res / (huber_k * sigma)
            w_scene = np.where(u <= 1.0, 1.0, 1.0 / u)
        out[d] = (A, gamma, n_sb.sum(axis=0), w_scene)
    return out


def softplus(x):
    return np.log1p(np.exp(-np.abs(x))) + np.maximum(x, 0.0)


def main():
    stems, scenes = load_all()
    Fd_arr = np.array([sc["Fd"] for sc in scenes])
    Fd0 = float(Fd_arr.mean())
    print(f"{len(scenes)} scenes; Fd0={Fd0:.4f}")

    R = 8
    bases = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in scenes]
    cells = scene_cells(scenes, bases)
    joint = fit_joint_huber(cells, Fd_arr, Fd0)

    A_neg, gamma_neg, n_neg, w_neg = joint[-1]
    A_pos, gamma_pos, n_pos, w_pos = joint[1]
    n = np.minimum(n_neg, n_pos)
    gammaMag = 0.5 * (-gamma_neg + gamma_pos)
    print(f"Huber-robust: gamma_neg={gamma_neg:+.4f} gamma_pos={gamma_pos:+.4f} -> gammaMag={gammaMag:.4f}")
    downwt = [(scenes[i]['stem'], round(float(w_neg[i]),2)) for i in np.argsort(w_neg)[:6]]
    print(f"most down-weighted scenes (recovery dir): {downwt}")

    Amp_lut = smooth_curve(0.5 * (-A_neg + A_pos), n)

    # bounded softplus fit
    reli = (n > 5000) & (CENT >= -3.0) & (CENT <= 1.6)
    B = CENT[reli]; y = Amp_lut[reli]; w = np.sqrt(n[reli])
    def amp_form(B, f0, s, b0, W): return f0 + s * W * softplus((B - b0) / W)
    popt, _ = curve_fit(amp_form, B, y, p0=[0.2, 0.3, -0.5, 0.8],
                        bounds=([0.0, 0.0, -3.0, 0.1], [0.8, 1.0, 2.0, 2.0]),
                        sigma=1.0 / w, maxfev=40000)
    f0, s, b0, W = popt
    fit_err = np.max(np.abs(amp_form(B, *popt) - y))
    print(f"Amp fit: f0={f0:.4f} s={s:.4f} b0={b0:.4f} W={W:.4f}  (max |fit-LUT| on reliable band: {fit_err:.4f} EV)")

    gLo, gHi = -3.3, -2.4
    def taper(Bv): return np.clip((Bv - gLo) / (gHi - gLo), 0, 1)
    def Amp(Bv): return amp_form(Bv, *popt) * taper(Bv)

    # detail-layer gain: refit on pooled data (unchanged model family)
    curves = fit.fit_curves(scenes, bases)
    Det_lut = smooth_curve(0.5 * (curves[-1][1] - curves[1][1]), n)
    reld = (n > 5000) & (CENT >= -2.0) & (CENT <= 1.4)
    def det_form(Bv, dmax, dLo, dHi): return dmax * fit.smoothstep(dLo, dHi, Bv)
    pd_, _ = curve_fit(det_form, CENT[reld], Det_lut[reld], p0=[0.2, -1.0, 0.2],
                       sigma=1.0/np.sqrt(n[reld]), maxfev=20000)
    dmax, dLo, dHi = pd_
    print(f"Det fit: dmax={dmax:.4f} dLo={dLo:.4f} dHi={dHi:.4f}")

    # ---- final analytic model eval, per dataset ----
    def eval_group(prefix):
        sse_new = sse_old = sse_base = 0.0; N = 0
        for sc, (Bb, Dd, idx) in zip(scenes, bases):
            if not sc["stem"].startswith(prefix):
                continue
            clip_n = sc["clip_n"]; code_n = ev_to_code(sc["ev_n"])
            FdT = gammaMag * (sc["Fd"] - Fd0)
            for s_ in SLIDERS:
                k = abs(s_) / 100.0; dsign = -1.0 if s_ < 0 else 1.0
                valid = (~clip_n) & (~sc[f"clip_{s_}"])
                code_s = ev_to_code(sc[f"ev_{s_}"])
                ampB = Amp(Bb)
                detG = 1.0 - dsign * k * det_form(Bb, *pd_)
                pred_new = Bb + dsign * k * (ampB - FdT) + detG * Dd
                pred_old = Bb + dsign * k * ampB + detG * Dd
                e_new = (ev_to_code(pred_new) - code_s)[valid]
                e_old = (ev_to_code(pred_old) - code_s)[valid]
                e_b = (code_n - code_s)[valid]
                sse_new += float(np.sum(e_new**2)); sse_old += float(np.sum(e_old**2))
                sse_base += float(np.sum(e_b**2)); N += int(valid.sum())
        return np.sqrt(sse_new/N), np.sqrt(sse_old/N), np.sqrt(sse_base/N)

    for prefix in ("D1", "D2"):
        rn, ro, rb = eval_group(prefix)
        print(f"  {prefix}: analytic+Fd={rn:.3f}   analytic(no Fd)={ro:.3f}   nothing={rb:.3f}")

    # monotonicity across Fd in [0,1] at k=1 (recovery pulls hardest when Fd small)
    grid = np.linspace(-4, 4, 4001)
    worst = min((np.diff(grid - (Amp(grid) - gammaMag*(t - Fd0))).min()/(grid[1]-grid[0]), t)
                for t in np.linspace(0, 1, 41))
    print(f"monotonicity, full Fd [0,1] at -100: min dBo/dB = {worst[0]:.4f} at Fd={worst[1]:.3f}"
          f"  ({'SAFE' if worst[0] > 0 else 'UNSAFE'})")

    print("\n--- C# constants ---")
    print(f"AmpFloor   = {f0:.4f};  AmpSlope = {s:.4f};  AmpKneeEv = {b0:.4f};  AmpWidthEv = {W:.4f};")
    print(f"BlacksGuardLoEv = {gLo:.2f}; BlacksGuardHiEv = {gHi:.2f};")
    print(f"DetailMax  = {dmax:.4f};  DetailLoEv = {dLo:.4f};  DetailHiEv = {dHi:.4f};")
    print(f"// scene-adaptive term: amp -= ShadowFracGamma*(Fd - ShadowFracRef), Fd = frac(unclipped EV < -3)")
    print(f"ShadowFracGamma = {gammaMag:.4f};  ShadowFracRef = {Fd0:.4f};  ShadowFracThresholdEv = -3.0;")

    np.savez("final_params3.npz", f0=f0, s=s, b0=b0, W=W, gLo=gLo, gHi=gHi,
             dmax=dmax, dLo=dLo, dHi=dHi, gammaMag=gammaMag, Fd0=Fd0,
             Amp_lut=Amp_lut, Det_lut=Det_lut, cent=CENT, n=n)
    print("saved final_params3.npz")


if __name__ == "__main__":
    main()
