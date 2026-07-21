"""Ablations: how much RMS does each modeling choice cost? Fix radius, test curve variants."""
import numpy as np
import fit  # reuse load_all, guided_base, fit_curves, rms_eval, apply_model, current_op, CENT
from fit import CENT, SLIDERS

R = 8  # engine radius (full-res ~64px)
scenes = fit.load_all()
bases = [(lambda B: (B, sc["ev_n"] - B, fit.bin_idx(B)))(fit.guided_base(sc["ev_n"], R)) for sc in scenes]
print(f"{len(scenes)} scenes, R={R}")

# reference fit at this radius
curves = fit.fit_curves(scenes, bases)
An_raw, Ap_raw = curves[-1][0], curves[1][0]
Gn_raw, Gp_raw = curves[-1][1], curves[1][1]
n_neg, n_pos = curves[-1][2], curves[1][2]
Asm = {d: fit.smooth_curve(curves[d][0], curves[d][2]) for d in (-1, 1)}
Gsm = {d: fit.smooth_curve(curves[d][1], curves[d][2]) for d in (-1, 1)}


def ev_of_code(c):
    # rough inverse for annotation only
    from rawr_transform import display_to_linear, MIDDLE_GRAY
    lin = display_to_linear(c / 255.0)
    return np.log2(max(lin, 1e-9) / MIDDLE_GRAY)


def evalr(An, Ap, Gn, Gp, label):
    A = {-1: An, 1: Ap}; G = {-1: Gn, 1: Gp}
    r = fit.rms_eval(scenes, bases, A, G)
    print(f"  {label:42s} RMS all={r['model']['all']:.3f}   "
          f"S-100={r['model'][-100]:.2f} S+100={r['model'][100]:.2f}")
    return r['model']['all']


# ---- build a clean symmetric amplitude curve --------------------------------
# reliable boost only up to ~+1.2 EV; above that use recovery (mirror), and
# extend recovery's high end monotonically. Detail: symmetric magnitude, denoise.
reliable_pos = CENT <= 1.2
Amp = np.where(reliable_pos, 0.5 * (-An_raw + Ap_raw), -An_raw)   # >=0
Amp = fit.smooth_curve(Amp, np.minimum(n_neg, n_pos))
# monotone (non-decreasing) above midgray so the high end never turns over
midj = np.argmin(np.abs(CENT - 0.0))
for j in range(midj + 1, len(Amp)):
    Amp[j] = max(Amp[j], Amp[j - 1])

Det = 0.5 * (Gn_raw - Gp_raw)                                    # recovery +, boost -
Det = fit.smooth_curve(Det, np.minimum(n_neg, n_pos))
Det = np.clip(Det, 0, None)
Det[CENT > 1.6] = Det[np.argmin(np.abs(CENT - 1.6))]            # clamp noisy top

print("\n=== reference ===")
evalr(-Asm[-1], Asm[1], Gsm[-1], Gsm[1], "raw per-direction fit (LUT)")
base = fit.rms_eval(scenes, bases, {-1: -Asm[-1], 1: Asm[1]}, {-1: Gsm[-1], 1: Gsm[1]})['base']['all']
cur = fit.rms_eval(scenes, bases, Asm, Gsm, include_current=True)['current']['all']
print(f"  {'do nothing':42s} RMS all={base:.3f}")
print(f"  {'current operator':42s} RMS all={cur:.3f}")

print("\n=== symmetric model ===")
evalr(-Amp, Amp, Det, -Det, "symmetric Amp + symmetric Det")
evalr(-Amp, Amp, Det * 0, -Det * 0, "symmetric Amp, NO detail")
evalr(-Amp * 0, Amp * 0, Det, -Det, "detail only, NO offset")

print("\n=== floor ablations (zero Amp below EV threshold via taper) ===")
for thr, lo in [(-1.5, -2.5), (-1.0, -2.0), (-2.5, -3.5)]:
    taper = np.clip((CENT - lo) / (thr - lo), 0, 1)
    Amp_t = Amp * taper
    evalr(-Amp_t, Amp_t, Det, -Det, f"floor tapered to 0 in [{lo},{thr}] EV")

print("\n=== detail strength scan (symmetric Amp) ===")
for sc_ in [0.5, 0.75, 1.0, 1.25]:
    evalr(-Amp, Amp, Det * sc_, -Det * sc_, f"Det x{sc_}")

np.savez("sym_curves.npz", cent=CENT, Amp=Amp, Det=Det)
print("\nsaved sym_curves.npz")
