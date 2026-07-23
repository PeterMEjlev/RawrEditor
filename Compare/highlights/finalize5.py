"""
PRODUCTION FIT -- two-curve scene-adaptive Highlights amplitude.

    amp(B, Fd) = max(A(B) + G(B)*(Fd - Fd0), 0) * taper(B)
    A(B) = f0 + s*W*softplus((B - b0)/W)          amplitude at the reference scene
    G(B) = g0 + (g1 - g0)*smoothstep(gB0, gB1, B) Fd-sensitivity (deep-shadow floor
                                                   rising to a midtone/highlight plateau)
    Fd   = fraction of the scene's unclipped pixels below -3 EV (bounded [0,1])

Rationale (see feature_corr.py, joint_model*.py, twocurve_luts.npz):
  * LR's Highlights response at matched local base varies ~40x across scenes
    and correlates r=+0.94 with scene mean exposure -> a global scene term.
  * The per-bin Fd-sensitivity gamma(B) is NOT constant: ~1.0 in deep shadows
    -> ~2.9 near middle grey, i.e. it has its own tone profile -> two curves.
  * Additive-in-EV keeps monotonicity easy to verify: dBo/dB = 1 - k*(A'+G'*dFd),
    checked explicitly across the whole possible Fd range below.

Fits on all 59 scenes (D1+D2); D1->D2 generalisation of the scene-term family
was established separately (joint_model2.py: held-out D2 RMS 19.5 -> 12.0).
"""
import numpy as np
from scipy.optimize import curve_fit
import fit
from fit import CENT, NB, SLIDERS, guided_base, bin_idx, ev_to_code
from finalize3 import load_all, softplus

DET_MAX, DET_LO, DET_HI = 0.1873, -1.0186, -0.6876   # shipped D1 detail constants
GLO, GHI = -3.3, -2.4                                 # blacks guard


def det_weight(B): return DET_MAX * fit.smoothstep(DET_LO, DET_HI, B)
def taper(B): return np.clip((B - GLO) / (GHI - GLO), 0, 1)


def main():
    z = np.load("twocurve_luts.npz")
    A_sym, G_sym, Fd0 = z["A_sym"], z["G_sym"], float(z["Fd0"])
    stems, scenes = load_all()
    Fd_arr = np.array([sc["Fd"] for sc in scenes])
    R = 8
    bases = [(lambda B: (B, sc["ev_n"] - B, bin_idx(B)))(guided_base(sc["ev_n"], R)) for sc in scenes]

    # weights: pixel counts per bin (recompute cheaply from one pass)
    n_tot = np.zeros(NB)
    for sc, (B, D, idx) in zip(scenes, bases):
        valid = ~sc["clip_n"]
        n_tot += np.bincount(idx[valid], minlength=NB)[:NB]

    # ---- analytic fits ----
    reli = (n_tot > 20000) & (CENT >= -4.5) & (CENT <= 1.7)
    Br = CENT[reli]; w = np.sqrt(n_tot[reli])

    def amp_form(B, f0, s, b0, W): return f0 + s * W * softplus((B - b0) / W)
    pA, _ = curve_fit(amp_form, Br, A_sym[reli], p0=[0.15, 0.3, -0.8, 1.0],
                      bounds=([0.0, 0.0, -3.0, 0.1], [0.8, 1.0, 2.0, 3.0]),
                      sigma=1.0 / w, maxfev=40000)
    errA = np.max(np.abs(amp_form(Br, *pA) - A_sym[reli]))
    print(f"A(B):  f0={pA[0]:.4f} s={pA[1]:.4f} b0={pA[2]:.4f} W={pA[3]:.4f}   max|err|={errA:.3f} EV")

    def g_form(B, g0, g1, gB0, gB1): return g0 + (g1 - g0) * fit.smoothstep(gB0, gB1, B)
    pG, _ = curve_fit(g_form, Br, G_sym[reli], p0=[0.8, 2.8, -3.0, 0.0],
                      bounds=([0.0, 0.5, -6.0, -1.0], [2.0, 4.0, -1.0, 1.5]),
                      sigma=1.0 / w, maxfev=40000)
    errG = np.max(np.abs(g_form(Br, *pG) - G_sym[reli]))
    print(f"G(B):  g0={pG[0]:.4f} g1={pG[1]:.4f} gB0={pG[2]:.4f} gB1={pG[3]:.4f}   max|err|={errG:.3f}")

    def amp_eff(B, Fd):
        return np.maximum(amp_form(B, *pA) + g_form(B, *pG) * (Fd - Fd0), 0.0) * taper(B)

    # ---- eval: analytic two-curve vs current shipped operator, per dataset & slider ----
    def cur_amp(B):
        # shipped LocalHighlights constants (Options defaults)
        f0, s, b0, W = 0.1294, 0.2536, -1.4381, 0.3209
        return (f0 + s * W * softplus((B - b0) / W)) * taper(B)

    print(f"\n{'':>4} {'S':>5} {'new(2-curve)':>12} {'shipped':>9} {'nothing':>8}")
    tot = {}
    for prefix in ("D1", "D2"):
        sse_n = sse_c = sse_0 = 0.0; N = 0
        per = {}
        for sc, (Bb, Dd, idx) in zip(scenes, bases):
            if not sc["stem"].startswith(prefix): continue
            clip_n = sc["clip_n"]; code_n = ev_to_code(sc["ev_n"])
            aN = amp_eff(Bb, sc["Fd"]); aC = cur_amp(Bb)
            for s_ in SLIDERS:
                k = abs(s_) / 100.0; dsign = -1.0 if s_ < 0 else 1.0
                valid = (~clip_n) & (~sc[f"clip_{s_}"])
                code_s = ev_to_code(sc[f"ev_{s_}"])
                detG = 1.0 - dsign * k * det_weight(Bb)
                eN = (ev_to_code(Bb + dsign * k * aN + detG * Dd) - code_s)[valid]
                eC = (ev_to_code(Bb + dsign * k * aC + detG * Dd) - code_s)[valid]
                e0 = (code_n - code_s)[valid]
                p = per.setdefault(s_, [0.0, 0.0, 0.0, 0])
                p[0] += float(np.sum(eN**2)); p[1] += float(np.sum(eC**2))
                p[2] += float(np.sum(e0**2)); p[3] += int(valid.sum())
                sse_n += float(np.sum(eN**2)); sse_c += float(np.sum(eC**2))
                sse_0 += float(np.sum(e0**2)); N += int(valid.sum())
        for s_ in SLIDERS:
            p = per[s_]
            print(f"{prefix:>4} {s_:+5d} {np.sqrt(p[0]/p[3]):12.2f} {np.sqrt(p[1]/p[3]):9.2f} {np.sqrt(p[2]/p[3]):8.2f}")
        print(f"{prefix:>4} {'all':>5} {np.sqrt(sse_n/N):12.3f} {np.sqrt(sse_c/N):9.3f} {np.sqrt(sse_0/N):8.3f}")
        tot[prefix] = (sse_n, sse_c, sse_0, N)
    sn = sum(t[0] for t in tot.values()); sc_ = sum(t[1] for t in tot.values())
    s0 = sum(t[2] for t in tot.values()); NN = sum(t[3] for t in tot.values())
    print(f"{'ALL':>4} {'all':>5} {np.sqrt(sn/NN):12.3f} {np.sqrt(sc_/NN):9.3f} {np.sqrt(s0/NN):8.3f}")

    # ---- monotonicity: k=1 recovery over full Fd in [0,1] ----
    grid = np.linspace(-5, 5, 10001)
    worst = None
    for Fd_t in np.linspace(0, 1, 101):
        Bo = grid - amp_eff(grid, Fd_t)
        mind = np.min(np.diff(Bo)) / (grid[1] - grid[0])
        if worst is None or mind < worst[0]: worst = (mind, Fd_t)
    print(f"\nmonotonicity (k=1, Fd 0..1): min dBo/dB = {worst[0]:.4f} at Fd={worst[1]:.2f}"
          f"  ({'SAFE' if worst[0] > 0 else 'UNSAFE'})")

    print("\n--- C# constants (two-curve scene-adaptive amplitude) ---")
    print(f"AmpFloor = {pA[0]:.4f}; AmpSlope = {pA[1]:.4f}; AmpKneeEv = {pA[2]:.4f}; AmpWidthEv = {pA[3]:.4f};")
    print(f"FdGainLo = {pG[0]:.4f}; FdGainHi = {pG[1]:.4f}; FdGainLoEv = {pG[2]:.4f}; FdGainHiEv = {pG[3]:.4f};")
    print(f"FdRef = {Fd0:.4f}; FdThresholdEv = -3.0;")
    print(f"BlacksGuardLoEv = {GLO}; BlacksGuardHiEv = {GHI};")
    print(f"DetailMax = {DET_MAX}; DetailLoEv = {DET_LO}; DetailHiEv = {DET_HI};")
    print("// amp(B,Fd) = max(A(B) + G(B)*(Fd - FdRef), 0) * blacksTaper(B)")
    print("// Fd = fraction of unclipped pixels with EV(lum) < FdThresholdEv, computed once per frame")

    np.savez("final_params5.npz", pA=pA, pG=pG, Fd0=Fd0, gLo=GLO, gHi=GHI,
             dmax=DET_MAX, dLo=DET_LO, dHi=DET_HI)
    print("saved final_params5.npz")


if __name__ == "__main__":
    main()
