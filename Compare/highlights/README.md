# Highlights slider — Lightroom calibration

How `LocalHighlights`'s amplitude/detail/headroom constants were derived, and how to
re-derive them. The same method applies to the Blacks / Shadows / Whites / Texture
datasets.

## The dataset

29 scenes, each exported from Lightroom with **only** the Highlights slider changed:
`0, ±25, ±50, ±75, ±100` (9 variants × 29 = 261 JPEGs), plus the matching `.CR3` raws.

```
Datasets/Highlights/<stem>_h{000,+025,…,-100}.jpg   # LR exports, full res, pixel-aligned per scene
Datasets/Dataset 1/<stem>.CR3                        # the raws (for end-to-end validation)
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
   generalising. Re-deriving that LUT by CDF-matching these 29 neutral pairs would help every
   slider and is likely the larger visible win.
2. **Scene-adaptivity** (above): the ceiling for any local operator; closing it needs a
   content-adaptive model.
