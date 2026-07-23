# Highlights slider — Lightroom calibration

How `LocalHighlights`'s amplitude/detail/headroom constants were derived, and how to
re-derive them. The same method applies to the Blacks / Shadows / Whites / Texture
datasets.

**Second-generation (two-dataset, scene-adaptive) calibration:** the sections up to
"Two findings" describe the original 29-scene fit; the **"Scene-adaptive recalibration"**
section at the bottom describes the shipped model, which supersedes it.

## The dataset

Two independently shot batches, each exported from Lightroom with **only** the
Highlights slider changed: `0, ±25, ±50, ±75, ±100`.

```
Datasets/Dataset 1/Highlights/<stem>_h{000,+025,…,-100}.jpg          # 29 scenes, Canon CR3
Datasets/Dataset 2/Highlights/<stem>_highlights{000,±…}.jpg          # 30 scenes, CR3+CR2+ARW
Datasets/Dataset {1,2}/<stem>.{CR3,cr2,ARW}                          # the raws
```

## The idea

`LocalHighlights` works in **scene-linear luminance EV about middle grey** (`I = log2(Y/0.18)`),
splitting the image into a guided-filter **base** `B` and **detail** `D = I − B`, then
offsetting the base. So to learn what Lightroom's slider does *in that space*:

1. Invert RAWR's output transform (`DisplayCurve ∘ LightroomMatch`, ported in
   `rawr_transform.py`) on each JPEG, per channel → linear → Rec.709 luma → EV.
   This is exact because at neutral RAWR is calibrated to LR, so an LR display value maps
   back through the same transform to the scene-linear value the operator sees.
2. Guided-filter the neutral (`h000`) EV to the regional base `B` — the **same** self-guided
   filter and `eps` (0.25 EV²) the operator uses, at the engine's full-res radius (~64 px).
3. Bin `ΔEV = EV_slider − EV_neutral` against `B`. Pixels clipped at 255 in either image are
   excluded — that headroom is what 8-bit JPEGs cannot measure.

Because binning by base and averaging `ΔEV` is the least-squares-optimal per-bin base offset,
the binned curve **is** the calibration target.

## What the data said

- **The response does not saturate.** LR pulls harder the brighter the region: ~0.13 EV floor
  low down, ~0.54 at middle grey (190/255), ~0.76 at 234/255, past 1.3 near white. The old
  smoothstep band flattened at +0.6 EV — an order of magnitude too weak up top.
- **Linear in the slider.** ±25/50/75/100 land at exactly ¼/½/¾/1 of the ±100 curve, so one
  `k = |slider|/100` scale is exact.
- **Near-symmetric** between recovery and boost up to the clip → one amplitude curve, signed
  by direction. Boost's apparent turnover above +1.2 EV is clipping survivorship bias.
- **Detail layer:** recovery expands local detail (~+0.19), boost compresses it (~−0.19).
- **Scene-adaptive.** At a fixed base, LR's −100 pull ranges −0.06 … −2.29 EV across scenes
  (mean −0.61, std **0.55**). No deterministic *local* operator can track that; the calibration
  targets the mean, which is the best such an operator can do.

## The fitted model (see `LocalHighlights.Options`)

```
B  = guided(I; r≈64, eps=0.25)
Amp(B) = (AmpFloor + AmpSlope·AmpWidthEv·softplus((B−AmpKneeEv)/AmpWidthEv))
         · smoothstep(BlacksGuardLoEv, BlacksGuardHiEv, B)      # blacks stay exact
B' = B + dir·k·Amp(B)                                            # dir = −1 recover / +1 boost
if recover: B' = HeadroomKneeEv + softKnee(B'−HeadroomKneeEv, foldSlope(k), HeadroomWidthEv)
I' = B' + (1 − dir·k·DetailMax·smoothstep(DetailLoEv,DetailHiEv,B))·D
```

The softplus is monotonic with a sub-unity tail (no tone inversion). The headroom fold is an
aggressive soft-knee anchored *above the calibrated band's own output*, so it is identity
across the measured range and only folds above-white content the JPEGs could not see
(uncalibrated — tune against a 16-bit/raw-linear reference).

Result vs Lightroom (JPEG-domain, all 29 scenes, display-code RMS): do-nothing 12.85 →
old band+knee 8.93 → **this 8.60**, improving every slider level.

## Reproduce

```
pip install numpy scipy pillow matplotlib
python cache_ev.py        # decode+invert all JPEGs → cache/  (once; ~7 min)
python fit.py             # radius sweep + per-direction curve fit → fit_result.npz
python ablate.py          # symmetric model + floor/detail ablations → sym_curves.npz
python finalize.py        # fit softplus+smoothstep, print C# constants → final_params.npz
python plot_findings.py   # findings.png
```

End-to-end (needs the raws + a build of `hlrender`, a headless render harness that calls
`DevelopProcessor.Render`): `compare_agg.py` renders each raw at several slider values and
compares to the LR JPEGs.

## Two findings that are bigger than the slider

1. **Neutral baseline gap.** RAWR's default render is ~26 codes RMS (bias ~−8, darker) from
   LR's neutral on this set. That is `BasicTone.LightroomMatch` — fit on a single scene — not
   generalising. Re-deriving that LUT by CDF-matching the 59 neutral pairs would help every
   slider and is likely the larger visible win. **Still open.**
2. **Scene-adaptivity**: solved below.

## Scene-adaptive recalibration (the shipped model)

Adding Dataset 2 exposed that the single-curve model does not generalise: fitted on D1
it scores 8.5 in-sample but **19.5 held-out on D2** (`fit_holdout.py`) — D2's scenes are
darker on average, and the −100 pull at matched base spans −0.06 … −5.7 EV across the 59
scenes. `feature_corr.py` showed this is not noise: the pull correlates **r = +0.94**
with the scene's overall exposure (robust per-dataset and to outlier removal). LR's
Highlights has a *global scene input* a purely local operator lacks.

The winning form (`finalize5.py`, evaluated against additive-constant and multiplicative
alternatives in `joint_model*.py` / `finalize4.py`):

```
Fd        = fraction of the frame's pixels below −3 EV        (deep-shadow fraction, [0,1])
amp(B,Fd) = max(A(B) + G(B)·(min(Fd,0.85) − 0.3734), 0) · taper(B)
A(B)      = 0.3261·1.4526·softplus((B+3.0)/1.4526)            (reference-scene amplitude)
G(B)      = 0.982 + 1.803·smoothstep(−3.83, +0.32, B)         (Fd-sensitivity, own tone profile)
taper     = blacks guard, widened to [−6.0, −2.4] EV
```

Key measurements behind the form:
- Per-bin regression of the Fd-dependence (`gamma(B)`) is **not constant** — ~1.0 in the
  deep shadows rising to ~2.9 near middle grey — so the scene term is a second *curve*,
  not a scale factor or offset.
- The additive-in-EV form keeps monotonicity verifiable: min d(B−amp)/dB = **+0.21** at
  −100 across the whole Fd ∈ [0,1] range (the Fd cap and the widened guard ramp are both
  load-bearing for this; the old [−3.3,−2.4] ramp inverts tone at high Fd).
- The widened guard also *matches LR better*: dark scenes measurably keep pulling well
  below −3 EV.

JPEG-domain display-code RMS over all 8 slider levels (do-nothing / old single-curve /
shipped scene-adaptive): **D1 12.9 / 8.6 / 7.4 — D2 23.5 / 19.7 / 11.0.**
Generalisation of the scene term was validated D1→D2 before pooling (19.5 → 12.0
held-out, `joint_model2.py`).

Engine integration: `LocalHighlights.Options.SceneShadowFraction` carries Fd; NaN means
"measure from the planes given to Apply" (whole-frame callers), while crop renders (1:1
tiles, mask regions) pin the full-frame value via
`LocalHighlights.EstimateSceneShadowFraction` — threaded through `DevelopProcessor` as
`sceneFrame`, the same full-frame-statistic discipline as Dehaze's airlight. The
statistic is measured on the sensor pixels pre-reconstruction/pre-dehaze, on a
deterministic subsample grid, so every path measuring the same frame gets the same value.

Reproduce: `python cache_ev2.py` (both datasets → cache/), then `fit_holdout.py`,
`feature_corr.py`, `finalize5.py` (+ the intermediate `joint_model*.py`, `finalize4.py`
for the model-selection evidence). Legacy single-dataset scripts (`cache_ev.py`,
`fit.py`, `ablate.py`, `finalize.py`) still work against the old cache layout.
