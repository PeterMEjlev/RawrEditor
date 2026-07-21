# Shadows slider — Lightroom calibration

Same method as [../highlights](../highlights/README.md), applied to `BasicTone.ApplyShadowsV3`.
Dataset: 29 scenes × Shadows `0, ±25, ±50, ±75, ±100` (`Datasets/Shadows/<stem>_shadows{…}.jpg`)
+ the raws in `Datasets/Dataset 1/`.

## The model

Shadows is a **per-pixel regional gain** (not a spatial base/detail operator like Highlights):
`gain = 2^((shadows/100) · ShadowsAmplitude(evBase))`, where `evBase` is the regional
luminance in EV from `EdgeAwareLuma` (self-guided filter, radius `min(w,h)/16` — much larger
than Highlights' 64 px). It scales RGB by one factor, so detail and hue ride through unflattened.

`ShadowsAmplitude` is the **mirror image of the Highlights softplus** — it rises smoothly as the
region gets darker and tapers to 0 out of the highlights:

```
Amp(evBase) = (AmpFloor + AmpSlope·AmpWidthEv·softplus((AmpKneeEv − evBase)/AmpWidthEv))
              · smoothstep(WhitesGuardHiEv, WhitesGuardLoEv, evBase)   # → 0 in highlights
```

## What the data said

- **The previous operator was ~3× too strong.** It applied a flat 3.5-stop lift; Lightroom
  lifts ~0.15 EV at middle grey, ~1 EV at 68/255, ~2–3 EV near black. At Shadows +100 the old
  operator was *worse than doing nothing* (RMS vs LR ~51 codes).
- **Linear in the slider**, **near-symmetric** lift/deepen in the reliable band (code 27–164).
- **Weak detail component** (~0.2, noisy) — measured but not baked (moved the match 0.03 codes,
  and would break the shared per-pixel-gain shape Blacks also uses).
- **AmpSlope 0.54 < 1** keeps a +Shadows lift order-preserving (a darker region can't overtake
  a lighter one).

Result vs Lightroom (JPEG-domain, all 29 scenes, display-code RMS): do-nothing 20.4 →
old 3.5-stop **26.0** → calibrated **10.3**, improving every slider level; +100 51 → 18.
End-to-end on the raws, the average ΔEV(base) curve tracks LR closely (e.g. code 131:
±0.39 both engines); the residual is dominated by the shared baseline gap and, for large
lifts, by noise amplification rather than the tone curve.

## Reproduce

```
python cache_shadows.py     # decode+invert all Shadows JPEGs → cache/
python measure_shadows.py   # ΔEV(base) curves, linearity, detail slope → shadows_measured.npz
python finalize_shadows.py  # fit the amplitude softplus, eval RMS, print C# constants
python compare_shadows.py   # end-to-end vs the raws (needs renders in ../renders_shadows)
```

Note: `finalize_shadows.py`'s C# print line has a variable-shadowing bug (`AmpSlope` prints the
last slider, 100); the real fitted slope is on the `Amp fit:` line (0.540).

## Same two findings as Highlights

The ~26-code neutral **baseline gap** (`BasicTone.LightroomMatch`, one-scene fit) and LR's
**scene-adaptivity** bound how close any local operator gets — see the highlights README.
