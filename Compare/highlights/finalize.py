"""
Fit clean analytic forms to the symmetric Amp/Det curves, validate RMS against the
raw LUT, verify monotonicity (no tone inversion at slider -100), and emit C# constants.

  Amp(B) = f0 + s*W*softplus((B-b0)/W)          # floor f0, linear-tail slope s
           * taper(B)                            # -> 0 below the blacks guard
  Det(B) = dmax * smoothstep(dLo, dHi, B)        # highlight-band detail gain
"""
import numpy as np
from scipy.optimize import curve_fit
import fit
from fit import CENT, SLIDERS

sym = np.load("sym_curves.npz")
Amp_lut, Det_lut = sym["Amp"], sym["Det"]
fr = np.load("fit_result.npz")
n = np.minimum(fr["n_neg"], fr["n_pos"])

def softplus(x): return np.log1p(np.exp(-np.abs(x))) + np.maximum(x, 0.0)  # stable

# ---- fit Amp over the reliable range (enough pixels, below the clip-noise top) ----
reli = (n > 5000) & (CENT >= -3.0) & (CENT <= 1.6)
B = CENT[reli]; y = Amp_lut[reli]; w = np.sqrt(n[reli])
def amp_form(B, f0, s, b0, W): return f0 + s * W * softplus((B - b0) / W)
p0 = [0.13, 0.28, -0.3, 0.8]
popt, _ = curve_fit(amp_form, B, y, p0=p0, sigma=1.0 / w, maxfev=20000)
f0, s, b0, W = popt
print(f"Amp fit: f0={f0:.4f} s={s:.4f} b0={b0:.4f} W={W:.4f}")

# blacks guard: taper Amp to 0 across [gLo,gHi] EV so deep shadows/blacks stay clean
gLo, gHi = -3.3, -2.4
def taper(B): return np.clip((B - gLo) / (gHi - gLo), 0, 1)
def Amp(B): return amp_form(B, *popt) * taper(B)

# ---- fit Det as a smoothstep highlight-band bump ----
reld = (n > 5000) & (CENT >= -2.0) & (CENT <= 1.4)
Bd = CENT[reld]; yd = Det_lut[reld]; wd = np.sqrt(n[reld])
def det_form(B, dmax, dLo, dHi): return dmax * fit.smoothstep(dLo, dHi, B)
pd, _ = curve_fit(det_form, Bd, yd, p0=[0.22, -1.0, 0.2], sigma=1.0 / wd, maxfev=20000)
dmax, dLo, dHi = pd
print(f"Det fit: dmax={dmax:.4f} dLo={dLo:.4f} dHi={dHi:.4f}")
def Det(B): return det_form(B, *pd)

# ---- monotonicity check: B -> B - Amp(B) at k=1 must be strictly increasing ----
grid = np.linspace(-4, 4, 4001)
Bo = grid - Amp(grid)
mono = np.all(np.diff(Bo) > 0)
print(f"recovery map monotonic at -100: {mono}  (min dBo/dB ~ {np.min(np.diff(Bo))/(grid[1]-grid[0]):.3f})")

# ---- evaluate RMS: analytic vs LUT ----
scenes = fit.load_all()
R = 8
bases = [(lambda Bb: (Bb, sc["ev_n"] - Bb, fit.bin_idx(Bb)))(fit.guided_base(sc["ev_n"], R)) for sc in scenes]
Aa = Amp(CENT); Dd = Det(CENT)
def ev(An, Ap, Gn, Gp): return fit.rms_eval(scenes, bases, {-1: An, 1: Ap}, {-1: Gn, 1: Gp})['model']['all']
print("\nRMS all:")
print(f"  analytic Amp+Det : {ev(-Aa, Aa, Dd, -Dd):.3f}")
print(f"  LUT     Amp+Det  : {ev(-Amp_lut, Amp_lut, Det_lut, -Det_lut):.3f}")
print(f"  analytic Amp only: {ev(-Aa, Aa, Dd*0, Dd*0):.3f}")

# ---- table + C# ----
def code(e): return __import__('rawr_transform').display_transform(0.18*2**e)*255
print("\n  EV   code |  Amp_lut  Amp_fit |  Det_lut  Det_fit")
for e in np.arange(-3,2.01,0.25):
    j=np.argmin(np.abs(CENT-e))
    print(f" {CENT[j]:+5.2f} {code(CENT[j]):4.0f} |  {Amp_lut[j]:+6.3f}  {Amp(CENT[j]):+6.3f} |  {Det_lut[j]:+6.3f}  {Det(CENT[j]):+6.3f}")

np.savez("final_params.npz", f0=f0, s=s, b0=b0, W=W, gLo=gLo, gHi=gHi,
         dmax=dmax, dLo=dLo, dHi=dHi)
print("\n--- C# constants ---")
print(f"AmpFloor   = {f0:.4f};  AmpSlope = {s:.4f};  AmpKneeEv = {b0:.4f};  AmpWidthEv = {W:.4f};")
print(f"BlacksGuardLoEv = {gLo:.2f}; BlacksGuardHiEv = {gHi:.2f};")
print(f"DetailMax  = {dmax:.4f};  DetailLoEv = {dLo:.4f};  DetailHiEv = {dHi:.4f};")
print("saved final_params.npz")
