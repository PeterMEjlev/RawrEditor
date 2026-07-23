"""
Final production fit: base/detail Amp(B) + gamma*Fd (deep-shadow-fraction
covariate), pooled over ALL 59 scenes (both datasets -- generalisation is
already validated by fit_holdout.py / joint_model2.py's D1->D2 held-out
numbers, so the shipping fit uses every scene for the best estimate).

Fits the analytic softplus form to the NEW Amp(B) (with the Fd-component
removed via the same within-bin/FWL estimator as joint_model2.py), verifies
the recovery mapping stays monotonic (no tone inversion) across the full
OBSERVED Fd range at |slider|=100, and prints C# constants.
"""
import os, glob
import numpy as np
from scipy.optimize import curve_fit
import fit
from fit import CACHE, SLIDERS, CENT, NB, guided_base, bin_idx, smooth_curve, DS


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


def fit_joint(scenes, bases, Fd0):
    out = {}
    for d in (-1, 1):
        n_sb, sy_sb, Fd_list = [], [], []
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
            n_sb.append(n_bin); sy_sb.append(sy_bin); Fd_list.append(sc["Fd"] - Fd0)
        n_sb = np.array(n_sb); sy_sb = np.array(sy_sb); Fd_arr = np.array(Fd_list)

        n_bin_tot = n_sb.sum(axis=0)
        y_bin_mean = np.divide(sy_sb.sum(axis=0), n_bin_tot, out=np.zeros(NB), where=n_bin_tot > 0)
        Fd_bin_mean = np.divide((n_sb * Fd_arr[:, None]).sum(axis=0), n_bin_tot,
                                 out=np.zeros(NB), where=n_bin_tot > 0)

        rFd = Fd_arr[:, None] - Fd_bin_mean[None, :]
        cellmean_y = np.divide(sy_sb, n_sb, out=np.full_like(sy_sb, np.nan), where=n_sb > 0)
        rY = cellmean_y - y_bin_mean[None, :]

        w = n_sb
        m = n_sb > 300
        num = np.nansum(np.where(m, w * rFd * rY, 0.0))
        den = np.nansum(np.where(m, w * rFd * rFd, 0.0))
        gamma = num / den if den > 0 else 0.0

        A = y_bin_mean - gamma * Fd_bin_mean
        out[d] = (A, gamma, n_bin_tot)
    return out


def softplus(x):
    return np.log1p(np.exp(-np.abs(x))) + np.maximum(x, 0.0)


def main():
    stems, scenes = load_all()
    print(f"{len(scenes)} scenes pooled (D1+D2)")
    Fd_vals = np.array([sc["Fd"] for sc in scenes])
    Fd0 = float(Fd_vals.mean())
    print(f"Fd (deep-shadow fraction, EV<-3): min={Fd_vals.min():.3f} max={Fd_vals.max():.3f} "
          f"mean(Fd0)={Fd0:.3f} std={Fd_vals.std():.3f}")

    R = 8
    bases = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in scenes]
    joint = fit_joint(scenes, bases, Fd0)

    # symmetric magnitude form (mirrors the existing "one Amp curve, signed by dir" design)
    A_neg_raw, gamma_neg, n_neg = joint[-1]
    A_pos_raw, gamma_pos, n_pos = joint[1]
    n = np.minimum(n_neg, n_pos)
    Amp_lut = 0.5 * (-A_neg_raw + A_pos_raw)
    Amp_lut = smooth_curve(Amp_lut, n)
    gammaMag = 0.5 * (-gamma_neg + gamma_pos)
    print(f"\ngamma_neg={gamma_neg:+.4f}  gamma_pos={gamma_pos:+.4f}  symmetric gammaMag={gammaMag:.4f}")

    # fit softplus analytic form to the (Fd-removed) symmetric Amp(B)
    reli = (n > 5000) & (CENT >= -3.0) & (CENT <= 1.6)
    B = CENT[reli]; y = Amp_lut[reli]; w = np.sqrt(n[reli])

    def amp_form(B, f0, s, b0, W): return f0 + s * W * softplus((B - b0) / W)
    p0 = [0.13, 0.28, -0.3, 0.8]
    popt, _ = curve_fit(amp_form, B, y, p0=p0, sigma=1.0 / w, maxfev=20000)
    f0, s, b0, W = popt
    print(f"Amp fit: f0={f0:.4f} s={s:.4f} b0={b0:.4f} W={W:.4f}")

    gLo, gHi = -3.3, -2.4
    def taper(B): return np.clip((B - gLo) / (gHi - gLo), 0, 1)
    def Amp(B): return amp_form(B, *popt) * taper(B)

    # ---- SAFETY CHECK: monotonicity of B -> B - (Amp(B) + gammaMag*(Fd-Fd0)) at k=1,
    # across the full OBSERVED Fd range, not just Fd0 ----
    grid = np.linspace(-4, 4, 4001)
    Fd_lo, Fd_hi = Fd_vals.min(), Fd_vals.max()
    worst = None
    for Fd_test in np.linspace(Fd_lo, Fd_hi, 21):
        term = gammaMag * (Fd_test - Fd0)
        Bo = grid - (Amp(grid) + term)
        d = np.diff(Bo)
        mind = d.min() / (grid[1] - grid[0])
        if worst is None or mind < worst[1]:
            worst = (Fd_test, mind)
    print(f"\nmonotonicity across observed Fd range [{Fd_lo:.3f},{Fd_hi:.3f}]: "
          f"worst-case min dBo/dB = {worst[1]:.4f} at Fd={worst[0]:.3f}  "
          f"({'SAFE (still increasing)' if worst[1] > 0 else 'UNSAFE -- tone inversion risk'})")
    # also check a generous margin beyond the observed range (Fd in [0,1] by construction)
    worst_full = None
    for Fd_test in np.linspace(0.0, 1.0, 41):
        term = gammaMag * (Fd_test - Fd0)
        Bo = grid - (Amp(grid) + term)
        d = np.diff(Bo)
        mind = d.min() / (grid[1] - grid[0])
        if worst_full is None or mind < worst_full[1]:
            worst_full = (Fd_test, mind)
    print(f"monotonicity across FULL possible Fd range [0,1]: worst-case min dBo/dB = "
          f"{worst_full[1]:.4f} at Fd={worst_full[0]:.3f}  "
          f"({'SAFE' if worst_full[1] > 0 else 'UNSAFE'})")

    print("\n--- C# constants (production, pooled 59-scene fit) ---")
    print(f"AmpFloor   = {f0:.4f};  AmpSlope = {s:.4f};  AmpKneeEv = {b0:.4f};  AmpWidthEv = {W:.4f};")
    print(f"BlacksGuardLoEv = {gLo:.2f}; BlacksGuardHiEv = {gHi:.2f};")
    print(f"ShadowFractionGamma = {gammaMag:.4f};  ShadowFractionRefEv/Threshold = -3.0;  Fd0 = {Fd0:.4f};")

    np.savez("final_params2.npz", f0=f0, s=s, b0=b0, W=W, gLo=gLo, gHi=gHi,
             gammaMag=gammaMag, Fd0=Fd0, Fd_lo=Fd_lo, Fd_hi=Fd_hi)
    print("saved final_params2.npz")


if __name__ == "__main__":
    main()
