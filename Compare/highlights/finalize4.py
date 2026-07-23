"""
Final model selection + production constants, correcting finalize3's sign
error and using the per-bin gamma(B) diagnostic result: gamma(B) tracks
A(B) at a ~constant ratio, so the scene term is MULTIPLICATIVE -- LR scales
the whole amplitude curve by a scene-brightness factor -- not additive.

Candidates evaluated (per dataset, display-code RMS, all 8 slider levels):
  old:    amp_eff = A(B)                        (current shipped family)
  add:    amp_eff = A(B) + g*(Fd - Fd0)         (additive, taper applied)
  mult:   amp_eff = A(B) * clamp(1 + c*(Fd-Fd0), 0, capS)
where Fd = fraction of unclipped pixels below -3 EV, Fd0 = pooled mean.
A(B) is the Huber-joint amplitude at Fd0 (analytic softplus fit).
Detail layer: the shipped D1 constants (pooled Det LUT is shadow-noise
corrupted; the term is second-order, keep it stable).

c is fitted by weighted LS of per-bin per-scene cell means against the
multiplicative prediction; capS bounds the scale so the recovery map stays
monotonic for any Fd in [0,1] (checked explicitly).
"""
import os, glob
import numpy as np
from scipy.optimize import curve_fit
import fit
from fit import CACHE, SLIDERS, CENT, NB, guided_base, bin_idx, smooth_curve, ev_to_code, DS
from finalize3 import load_all, scene_cells, fit_joint_huber, softplus

# shipped D1 detail constants (LocalHighlights.Options)
DET_MAX, DET_LO, DET_HI = 0.1873, -1.0186, -0.6876
GLO, GHI = -3.3, -2.4


def det_weight(B):
    return DET_MAX * fit.smoothstep(DET_LO, DET_HI, B)


def taper(B):
    return np.clip((B - GLO) / (GHI - GLO), 0, 1)


def main():
    stems, scenes = load_all()
    Fd_arr = np.array([sc["Fd"] for sc in scenes])
    Fd0 = float(Fd_arr.mean())
    R = 8
    bases = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in scenes]
    cells = scene_cells(scenes, bases)

    # amplitude at Fd0 from the Huber joint fit (recovery/boost symmetrised)
    joint = fit_joint_huber(cells, Fd_arr, Fd0)
    A_neg, gamma_neg, n_neg, _ = joint[-1]
    A_pos, gamma_pos, n_pos, _ = joint[1]
    n = np.minimum(n_neg, n_pos)
    Amp_lut = smooth_curve(0.5 * (-A_neg + A_pos), n)
    g_add = 0.5 * (-gamma_neg + gamma_pos)

    # analytic softplus fit of Amp_lut (bounded; wider W bound than finalize3)
    reli = (n > 5000) & (CENT >= -3.0) & (CENT <= 1.6)
    B_ = CENT[reli]; y_ = Amp_lut[reli]; w_ = np.sqrt(n[reli])
    def amp_form(B, f0, s, b0, W): return f0 + s * W * softplus((B - b0) / W)
    popt, _ = curve_fit(amp_form, B_, y_, p0=[0.15, 0.3, -0.8, 1.0],
                        bounds=([0.0, 0.0, -3.0, 0.1], [0.8, 1.0, 2.0, 3.0]),
                        sigma=1.0 / w_, maxfev=40000)
    f0, s, b0, W = popt
    print(f"Amp@Fd0 fit: f0={f0:.4f} s={s:.4f} b0={b0:.4f} W={W:.4f}  "
          f"max|err|={np.max(np.abs(amp_form(B_, *popt) - y_)):.4f} EV")
    def Amp(B): return amp_form(B, *popt) * taper(B)

    # ---- fit multiplicative coefficient c on scene-level cell means ----
    # model per direction d: cellmean_{scene,bin} = d * Amp(B_bin) * (1 + c*(Fd_s - Fd0))
    num = den = 0.0
    for d in (-1, 1):
        n_sb, sy_sb = cells[d]
        cellmean = np.divide(sy_sb, n_sb, out=np.full_like(sy_sb, np.nan), where=n_sb > 300)
        A_bin = Amp(CENT)
        for si in range(len(scenes)):
            ok = np.isfinite(cellmean[si]) & (A_bin > 0.02)
            if not ok.any(): continue
            x = d * A_bin[ok] * (Fd_arr[si] - Fd0)     # regressor for c
            r = cellmean[si][ok] - d * A_bin[ok]        # residual after base model
            wgt = n_sb[si][ok]
            num += np.sum(wgt * x * r); den += np.sum(wgt * x * x)
    c_mult = num / den
    print(f"additive g={g_add:.4f} EV/unit-Fd;  multiplicative c={c_mult:.4f} per unit-Fd")

    # scale cap from monotonicity: need capS * max A'(B) < 1 with margin
    grid = np.linspace(-4, 4, 8001)
    Agrid = Amp(grid)
    maxslope = np.max(np.diff(Agrid) / (grid[1] - grid[0]))
    capS = 0.9 / maxslope
    print(f"max dAmp/dB = {maxslope:.4f} -> scale cap capS = {capS:.3f} "
          f"(scale at observed Fd max {Fd_arr.max():.3f}: {1 + c_mult*(Fd_arr.max()-Fd0):.3f})")

    def scale_of(Fd):
        return np.clip(1.0 + c_mult * (Fd - Fd0), 0.0, capS)

    # ---- evaluate all candidates per dataset ----
    def eval_group(prefix, mode):
        sse = 0.0; N = 0
        for sc, (Bb, Dd, idx) in zip(scenes, bases):
            if not sc["stem"].startswith(prefix): continue
            clip_n = sc["clip_n"]
            if mode == "old":
                amp_eff = Amp(Bb)
            elif mode == "add":
                amp_eff = np.maximum((amp_form(Bb, *popt) + g_add * (sc["Fd"] - Fd0)), 0.0) * taper(Bb)
            elif mode == "mult":
                amp_eff = Amp(Bb) * scale_of(sc["Fd"])
            for s_ in SLIDERS:
                k = abs(s_) / 100.0; dsign = -1.0 if s_ < 0 else 1.0
                valid = (~clip_n) & (~sc[f"clip_{s_}"])
                code_s = ev_to_code(sc[f"ev_{s_}"])
                detG = 1.0 - dsign * k * det_weight(Bb)
                pred = Bb + dsign * k * amp_eff + detG * Dd
                e = (ev_to_code(pred) - code_s)[valid]
                sse += float(np.sum(e * e)); N += int(valid.sum())
        return np.sqrt(sse / N)

    print(f"\n{'':>4} {'old(no Fd)':>11} {'additive':>9} {'mult':>9} {'nothing':>8}")
    for prefix in ("D1", "D2"):
        rms_n = 0.0; N = 0
        for sc in scenes:
            if not sc["stem"].startswith(prefix): continue
            code_n = ev_to_code(sc["ev_n"])
            for s_ in SLIDERS:
                valid = (~sc["clip_n"]) & (~sc[f"clip_{s_}"])
                e = (code_n - ev_to_code(sc[f"ev_{s_}"]))[valid]
                rms_n += float(np.sum(e * e)); N += int(valid.sum())
        print(f"{prefix:>4} {eval_group(prefix,'old'):11.3f} {eval_group(prefix,'add'):9.3f} "
              f"{eval_group(prefix,'mult'):9.3f} {np.sqrt(rms_n/N):8.3f}")

    # ---- monotonicity across full Fd in [0,1] at -100 ----
    worst = None
    for Fd_t in np.linspace(0, 1, 41):
        Bo = grid - Amp(grid) * scale_of(Fd_t)
        mind = np.min(np.diff(Bo)) / (grid[1] - grid[0])
        if worst is None or mind < worst[0]: worst = (mind, Fd_t)
    print(f"\nmonotonicity (mult, k=1, Fd in [0,1]): min dBo/dB = {worst[0]:.4f} at Fd={worst[1]:.3f}"
          f"  ({'SAFE' if worst[0] > 0 else 'UNSAFE'})")

    print("\n--- C# constants (multiplicative scene-adaptive model) ---")
    print(f"AmpFloor = {f0:.4f}; AmpSlope = {s:.4f}; AmpKneeEv = {b0:.4f}; AmpWidthEv = {W:.4f};")
    print(f"BlacksGuardLoEv = {GLO}; BlacksGuardHiEv = {GHI};")
    print(f"DetailMax = {DET_MAX}; DetailLoEv = {DET_LO}; DetailHiEv = {DET_HI};  // unchanged (D1)")
    print(f"SceneScaleCoeff = {c_mult:.4f};   // scale = clamp(1 + coeff*(Fd - SceneShadowFracRef), 0, {capS:.3f})")
    print(f"SceneShadowFracRef = {Fd0:.4f};  SceneShadowThresholdEv = -3.0;  SceneScaleCap = {capS:.4f};")

    np.savez("final_params4.npz", f0=f0, s=s, b0=b0, W=W, gLo=GLO, gHi=GHI,
             c_mult=c_mult, Fd0=Fd0, capS=capS, Amp_lut=Amp_lut, cent=CENT, n=n)
    print("saved final_params4.npz")


if __name__ == "__main__":
    main()
