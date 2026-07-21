"""Visualize the measured LR Highlights response and the fitted operator curves."""
import numpy as np
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import rawr_transform as rt

meas = np.load("highlights_measured.npz")
fit = np.load("fit_result.npz")
bc = meas["bin_cent"]; sliders = meas["sliders"]
cent = fit["cent"]
def code(ev): return rt.display_transform(rt.MIDDLE_GRAY * 2 ** ev) * 255

# current-model band offset amplitude at |S|=100 for reference
def smoothstep(e0, e1, x):
    u = np.clip((x - e0) / (e1 - e0), 0, 1); return u * u * (3 - 2 * u)
cur_wB = smoothstep(-2.1, 0.6, cent)
cur_pull = -0.68 * cur_wB   # (ignores top-knee fold, ~identity at slider level)

x = code(cent); xm = code(bc)
mask = (x >= 30) & (x <= 256)

fig, ax = plt.subplots(2, 2, figsize=(14, 10))

# Panel 1: base-offset amplitude at |S|=100, both directions, vs current model
a = ax[0, 0]
a.plot(x[mask], fit["A_neg"][mask], 'b-', lw=2, label="LR recovery (fit, S=-100)")
a.plot(x[mask], fit["A_pos"][mask], 'r-', lw=2, label="LR boost (fit, S=+100)")
a.plot(x[mask], cur_pull[mask], 'b--', lw=1.2, label="current op recovery (RecoveryEv=.68)")
a.plot(x[mask], -cur_pull[mask], 'r--', lw=1.2, label="current op boost (BoostEv=.68)")
a.axhline(0, color='k', lw=0.5); a.axvline(190, color='gray', ls=':', label="middle grey 190")
a.set_xlabel("regional base level (display code)"); a.set_ylabel("EV offset at |slider|=100")
a.set_title("Base-offset amplitude: LR vs current operator"); a.legend(fontsize=8); a.grid(alpha=.3)

# Panel 2: detail-gain slope
a = ax[0, 1]
a.plot(x[mask], fit["G_neg"][mask], 'b-', lw=2, label="LR recovery detail slope (S=-100)")
a.plot(x[mask], fit["G_pos"][mask], 'r-', lw=2, label="LR boost detail slope (S=+100)")
a.axhline(0, color='k', lw=0.5); a.axvline(190, color='gray', ls=':')
a.set_xlabel("regional base level (display code)"); a.set_ylabel("detail gain - 1 at |slider|=100")
a.set_title("Detail-layer behaviour (>0 expands, <0 compresses)"); a.legend(fontsize=8); a.grid(alpha=.3)

# Panel 3: slider linearity (mean dEV normalised by k)
a = ax[1, 0]
for s in sliders:
    k = abs(s) / 100
    a.plot(xm[mask], meas[f"mean_dev_{s}"][mask] / k, lw=1,
           color='b' if s < 0 else 'r', alpha=0.5 + 0.4 * abs(s) / 100)
a.axhline(0, color='k', lw=0.5); a.axvline(190, color='gray', ls=':')
a.set_xlabel("regional base level (display code)"); a.set_ylabel("mean dEV / (|slider|/100)")
a.set_title("Slider linearity: curves overlay if dEV is linear in slider"); a.grid(alpha=.3)

# Panel 4: raw vs smoothed fit + counts
a = ax[1, 1]
a.plot(x[mask], fit["A_neg_raw"][mask], 'b.', ms=3, alpha=.4, label="recovery raw bins")
a.plot(x[mask], fit["A_neg"][mask], 'b-', lw=2, label="recovery smoothed")
a.plot(x[mask], fit["A_pos_raw"][mask], 'r.', ms=3, alpha=.4, label="boost raw bins")
a.plot(x[mask], fit["A_pos"][mask], 'r-', lw=2, label="boost smoothed")
a2 = a.twinx()
a2.fill_between(x[mask], fit["n_neg"][mask] / 1e6, color='gray', alpha=.15)
a2.set_ylabel("pixels per bin (millions)", color='gray')
a.set_xlabel("regional base level (display code)"); a.set_ylabel("EV offset at |slider|=100")
a.set_title(f"Fit quality & data density (radius={int(fit['radius'])})"); a.legend(fontsize=8); a.grid(alpha=.3)

plt.tight_layout()
plt.savefig("findings.png", dpi=110)
print("saved findings.png")
