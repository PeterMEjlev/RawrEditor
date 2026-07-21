# Whites slider — Lightroom calibration

Same method as [../highlights](../highlights/README.md), applied to `BasicTone.ApplyWhitesV4`.
Dataset: 29 scenes × Whites `0, ±25, ±50, ±75, ±100` (`Datasets/Whites/<stem>_whites{…}.jpg`)
+ the raws in `Datasets/Dataset 1/`.

## The model

Whites is a **global** top-weighted tone control (per-pixel luminance EV, no regional base):
`y2 = midGray · 2^(knee + softKnee(ev − knee, slope, width))`, `slope = (dir<0 ? MinSlope : MaxSlope)^(|whites|/100)`.
The geometric slope reproduces the measured asymmetry — **− compresses sub-linearly (saturating),
+ expands super-linearly (accelerating into the clip)**.

## Two findings, one of which upends the old design

1. **Global is right.** Binning ΔEV by the pixel's own EV explains Lightroom marginally better
   than binning by a regional base (0.152 vs 0.154 EV RMS) — so v4's per-pixel design is correct,
   and Whites keeps its "moves an isolated specular" character (vs regional Highlights).

2. **Whites is BROAD, not a white point.** The old operator put the knee at +0.1 EV (≈195/255) so
   "midtones stay exactly put." **Lightroom's Whites does no such thing** — it's a top-weighted
   curve that reaches deep into the midtones: at ±100 it moves *middle grey itself* by ~½…1 stop
   (190/255 → ~170 at −100, ~219 at +100, verified on raw pixels), tapering to a no-op only below
   ~85/255. So the knee moved to −2.0 EV. This is a deliberate character change; the
   `Whites_LeavesMidtonesExact` test was rewritten to `Whites_IsBroadAndTopWeighted_ExactBelowTheKnee`.

Result vs Lightroom (JPEG-domain, all 29 scenes, display-code RMS): do-nothing 15.1 →
old narrow-knee **14.2** → calibrated **9.2**; −100 alone 11.2 → **2.8**. End-to-end on the raws,
the negative direction matches closely (operator-delta RMS 4.9 at −100); the **+ direction is only
calibrated up to where it clips to 255** (like a boost) and is approximate above that.

## Reproduce

```
python cache_whites.py      # decode+invert → cache/
python measure_whites.py    # global-vs-regional test + ΔEV(ev) curves → whites_measured.npz
python finalize_whites.py   # fit knee/width/MinSlope/MaxSlope, eval RMS, print constants
python compare_whites.py    # end-to-end vs the raws (needs ../renders_whites)
```

## Same baseline caveat

The ~26-code neutral **baseline gap** (`BasicTone.LightroomMatch`) is unchanged and remains the
largest RAWR↔LR difference — see the highlights README.
