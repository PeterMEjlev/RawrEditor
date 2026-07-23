# Lightroom-match calibration — what's still on the table

Status after the Highlights scene-adaptive recalibration (2026-07-22, see
[Compare/highlights/README.md](Compare/highlights/README.md) and the
`lr-highlights-findings` memory). Three things remain, ordered by
**value ÷ effort** — do them top to bottom.

| # | Item | Effort | Expected win | Blocked on |
|---|------|--------|--------------|------------|
| 1 | Re-derive the neutral baseline LUT (`LightroomMatch`) | **S–M** | **Largest** — helps *every* slider | A headless neutral render of the raws |
| 2 | Scene-adaptivity audit for Shadows / Whites / Blacks | M | Medium per slider | Dataset 2 exports of those sliders |
| 3 | Multi-scale (local-Laplacian) Highlights operator | L | Small, diminishing | Nothing; optional |

---

## 1. Re-derive the neutral baseline LUT — the biggest visible win

### The problem

At **default settings** (every slider neutral), RAWR renders ~**26 codes RMS**
away from Lightroom's neutral, biased ~**−8 codes darker**, across the 59-scene
set. That gap is larger than the entire Highlights effect and it contaminates
*every* slider calibration, because each slider is measured as a delta on top of
this wrong baseline.

The culprit is [BasicTone.cs](src/Rawr.Develop/BasicTone.cs#L193-L204): the
`LrMatchLut` — a 65-point luma transfer curve composed after `DisplayCurve` to
land the neutral render on Lightroom's look — was **CDF-matched from a single
scene** (`2_Baseline_RAWR.tif` vs `2_Baseline_LR.tif`). One scene cannot capture
the transfer; it over-fits that scene's content distribution and generalises
poorly. The class comment already says so and names the fix: *"Re-run the script
over more baseline pairs and average to refine; replace the table below
verbatim."*

We now have exactly that — **59 neutral pairs** (`h000` / `highlights000`
exports + their raws) across two cameras' colour science. Re-deriving the LUT
from all of them is the highest-leverage change available.

> ⚠️ **Gotcha:** the script the comment references,
> `Compare/compare_baseline_tif.py`, is **no longer in the repo**. It has to be
> rewritten (steps below). Keep the output format identical — a 65-element array
> pasted verbatim into `LrMatchLut` — so nothing downstream changes.

### What CDF matching needs

The LUT maps **RAWR-display values → Lightroom-display values**. To fit it you
need two distributions per scene:

- **RAWR's neutral display distribution** — the output of
  [`DisplayCurve`](src/Rawr.Develop/BasicTone.cs#L150-L169) **before**
  `LightroomMatch` is applied, over a real neutral render of each raw.
- **Lightroom's neutral display distribution** — the code values of each
  `*_h000.jpg` / `*_highlights000.jpg` export (already on disk).

CDF matching aligns the *distributions*, not paired pixels, so the small
framing/crop offset between RAWR's render and LR's export does not matter (the
same property the original single-scene fit relied on).

### Implementation

**Step A — a headless neutral render (the one real prerequisite).**
There is no way to get RAWR's pre-`LightroomMatch` display distribution without
running RAWR's decode + develop pipeline on the raws. Add a tiny console harness
(this is the `hlrender` the Highlights README wishes existed; build it once and
items 2–3 reuse it):

- New project `tools/RawrRender` (or a test-only entrypoint) that, per raw:
  1. `RawDecoder.DecodeLinearRgb(path, halfSize:false)`.
  2. `DevelopProcessor.Render` at **neutral** settings — but capture the display
     values **before** `LightroomMatch`. Cleanest: add an internal
     `DevelopSettings.SkipLightroomMatch` (or a `DisplayLut` override) that swaps
     the composed LUT for `DisplayCurve` alone. Gate it behind `internal` +
     `InternalsVisibleTo` so it never ships in the UI.
  3. Write a 16-bit PNG/TIFF (don't quantise to 8-bit — you want the full
     distribution, and the top end is where the curve matters most).
- Point it at both dataset roots (see the raw layout in
  [Compare/highlights/README.md](Compare/highlights/README.md)).

**Step B — CDF-match, pooled over all 59 scenes.** Write
`Compare/baseline/derive_lrmatch.py`:

1. For each scene, load RAWR-neutral (Step A output) and the LR `h000` export.
   Take luma on both (Rec.709; `LightroomMatch` is a single luma curve applied to
   R/G/B, so fit in luma — see the note on per-channel below).
2. Build a **pooled** CDF for each side (concatenate all scenes, or average the
   per-scene CDFs weighted by pixel count — pool, don't fit per scene, so no
   single scene dominates the way the original did).
3. The transfer is `T(x) = CDF_LR⁻¹(CDF_RAWR(x))`. Sample it at the **65 evenly
   spaced input points** over RAWR-display `[0,1]` that `LrMatchLut` expects
   ([BasicTone.cs:218-223](src/Rawr.Develop/BasicTone.cs#L218-L223) reads it as
   `v * 64` piecewise-linear).
4. **Anchor and regularise:** force `T(0)=0`, `T(1)=1`; enforce monotonicity
   (`np.maximum.accumulate`); lightly smooth so JPEG quantisation noise in the LR
   CDF doesn't put wiggles in the curve. The deep-shadow and extreme-highlight
   ends have little mass — keep them on a gentle separation-preserving line, as
   the current table does, rather than letting a near-empty bin dictate them.
5. Print the 65 numbers in the exact brace-formatted block and paste them over
   [`LrMatchLut`](src/Rawr.Develop/BasicTone.cs#L193-L204).

**Step C — validate.** Re-run the neutral-gap measurement (the per-scene
baseline RMS block in
[Compare/highlights/compare_agg.py](Compare/highlights/compare_agg.py) already
computes exactly this). Target: neutral RMS well under 26, bias near 0. Then
**re-run the Highlights held-out fit** ([fit_holdout.py](Compare/highlights/fit_holdout.py)) —
its numbers will shift because the inverse transform in
[rawr_transform.py](Compare/highlights/rawr_transform.py) must be updated with
the new LUT, and every slider delta now sits on a correct baseline.

> **Keep `rawr_transform.py` in sync.** The Python port of the output transform
> hard-codes `LR_MATCH_LUT`
> ([rawr_transform.py:20-30](Compare/highlights/rawr_transform.py#L20-L30)).
> Update it the moment you change the C# table, or every downstream measurement
> silently inverts through the wrong curve.

**Optional — per-channel.** The original measured a per-channel colour match but
deliberately did **not** bake it (one scene = content bias). With 59 scenes
across two cameras you can now test it honestly: fit three curves (R/G/B) instead
of one luma curve, validate held-out D1→D2, and only bake it if it beats the
single-luma curve *out of sample*. Expect a small, colour-cast-correcting win at
most; the neutral-preserving single curve is the safe default.

---

## 2. Scene-adaptivity audit for Shadows / Whites / Blacks

### Why

Highlights turned out to be **strongly scene-adaptive** (LR's response scales
with overall scene brightness, r≈0.94), and the old single-dataset fit hid it —
it scored 8.5 in-sample but 19.5 held-out. Shadows, Whites, and Blacks were all
calibrated the **same way**: one dataset, in-sample RMS only
(`BasicTone.ApplyShadowsV3`, `ApplyWhitesV4`, and the Blacks endpoint). There is
every reason to expect the same hidden scene-dependence — Shadows especially, as
it is the tonal mirror of Highlights.

### Prerequisite (this is what blocks it)

The held-out protocol needs a **second dataset per slider**. Today only
Highlights has a Dataset 2. Under `Datasets/Dataset 1/` there are already
`Shadows/ Whites/ Blacks/` folders; `Datasets/Dataset 2/` has only `Highlights/`.
So the first task is **exporting Dataset 2 for each slider** with the same recipe
(9 exports per scene at `0, ±25, ±50, ±75, ±100`, only that slider moved).

### Implementation (once the exports exist)

The Highlights scripts are the template — the method is identical, only the
operator's working space changes:

1. **Cache both datasets.** Copy [cache_ev2.py](Compare/highlights/cache_ev2.py)
   into `Compare/shadows/` (etc.), point `ROOTS` at that slider's folders, and
   fix the two filename conventions (`_h000` vs `_highlights000`) as it already
   handles.
2. **Held-out fit.** Copy [fit_holdout.py](Compare/highlights/fit_holdout.py):
   fit the operator's curve on Dataset 1, report RMS **held-out on Dataset 2**.
   If held-out ≫ in-sample, the model is over-fit to D1's scene mix — same
   symptom Highlights showed.
3. **Feature correlation.** Copy
   [feature_corr.py](Compare/highlights/feature_corr.py): correlate each scene's
   per-slider response (at a fixed base tone) against global scene statistics
   (mean exposure, deep-shadow fraction `Fd`, dynamic range, clipped fraction). A
   strong correlation ⇒ a missing global input, fixable exactly as Highlights
   was.
4. **If scene-adaptive, add the two-curve term.** Reuse the shipped mechanism:
   `Options.SceneShadowFraction` + `EstimateSceneShadowFraction` already exist in
   [LocalHighlights.cs](src/Rawr.Develop/LocalHighlights.cs) and are threaded
   through [DevelopProcessor.cs](src/Rawr.Develop/DevelopProcessor.cs) as
   `sceneFrame`. Shadows/Blacks are per-pixel operators in `BasicTone`, not
   spatial, so they'd take `Fd` (or whichever feature correlates) as a scalar
   render-time argument — cheaper than Highlights, no plumbing beyond passing the
   number in. Follow `finalize5.py`'s form: fit `A(B) + G(B)·(feature − ref)`,
   pick additive-in-EV so monotonicity stays checkable, verify the recovery map
   is monotonic across the whole feature range, and **cap** the feature to the
   observed support.

> **Reuse, don't re-derive `Fd`.** `EstimateSceneShadowFraction`
> ([LocalHighlights.cs](src/Rawr.Develop/LocalHighlights.cs)) already computes
> the deep-shadow fraction deterministically. If a different slider correlates
> best with a *different* statistic (e.g. Whites with the *bright* fraction),
> add a sibling estimator with the same subsample-grid discipline so tiles, mask
> crops and exports all agree.

### Do item 1 first

Every slider delta is measured against the neutral baseline. Fixing the baseline
LUT (item 1) shifts all of these measurements, so re-deriving the LUT **before**
auditing the other sliders means you audit them once, not twice.

---

## 3. Multi-scale (local-Laplacian) Highlights operator — optional, diminishing

### Why it's last

After the scene-adaptive term, the held-out D2 residual is ~11.0 codes, and a
chunk of that is **two near-black outlier scenes** whose −5.5 EV pulls even a
full per-bin model can't track. The remaining error is no longer "a missing
global input" — it's the **structural limit of a single-radius base/detail
split**. Adobe's process-2012 tone tools (Highlights/Shadows/Clarity) are
documented (Paris, Hasinoff & Kautz, *Local Laplacian Filters*, SIGGRAPH 2011) to
use a genuinely **multi-scale, edge-aware pyramid remap**, not one guided-filter
band. Matching that shape is the only lever left — and it's a lot of work for a
small, diminishing gain, so it's explicitly optional.

### Implementation sketch

The current operator
([LocalHighlights.cs](src/Rawr.Develop/LocalHighlights.cs)) splits `I` into one
guided-filter **base** `B` and **detail** `D = I − B`, then remaps `B`. A local
Laplacian replaces that with a per-level remap:

1. Build a Gaussian pyramid of `I`.
2. For each output pixel and pyramid level, apply a **point-wise remapping
   function** `r(i)` around that level's local value — the amplitude/detail
   curves you already fitted become the shape of `r`, now applied per scale
   rather than to one base.
3. Collapse the resulting Laplacian pyramid back to the image.
4. Keep the scene-adaptive `Fd` term as a modulation of `r`'s strength — it's
   orthogonal to the multi-scale structure and still needed.

This is a **new operator**, not a tweak — prototype it in Python against the
cached EV data (`Compare/highlights/cache/`) and compare held-out RMS to the
shipped two-curve model **before** porting any C#. Only proceed if the offline
prototype clears the current 11.0 held-out by a margin that justifies the cost
(it needs the headless render harness from item 1 for end-to-end validation).
Realistic expectation: a couple of code values, mostly on high-dynamic-range
scenes.

---

## Summary

1. **Re-derive `LrMatchLut` from all 59 neutral pairs.** Small–medium effort,
   biggest win, helps everything. Needs a headless neutral render; keep
   `rawr_transform.py` in sync; the old `compare_baseline_tif.py` is gone and
   must be rewritten.
2. **Audit Shadows/Whites/Blacks for the same scene-adaptivity**, using the
   Highlights held-out + feature-correlation scripts as the template. Blocked on
   exporting Dataset 2 for those sliders. Do after item 1.
3. **Local-Laplacian Highlights** only if you want the last couple of code
   values on HDR scenes — high effort, diminishing return, prototype offline
   first.
