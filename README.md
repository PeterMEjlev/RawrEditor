# RAWR Editor

A RAW photo *develop* tool (Lightroom-style), built to merge cleanly into the
[RAWR culling app](../RAWR) later. MVP scope: **one photo at a time** — load it,
see it full quality, adjust it live, export a JPEG.

## What works (MVP)

- **Load** a single RAW (CR2/CR3/NEF/ARW/RAF/RW2/ORF/DNG/PEF/SRW…) via LibRaw.
- **See it** rendered from the real 16-bit sensor data (not the embedded JPEG),
  fit-to-window at preview quality.
- **One adjustments panel**, all live:
  - White Balance — Temp, Tint
  - Tone — Exposure, Contrast, Highlights, Shadows, Whites, Blacks
  - Presence — Vibrance, Saturation
- **Reset** all, **double-click any slider** to zero it.
- **Export** a full-resolution JPEG with the edits baked in.

Edits preview in realtime: the RAW is decoded once (half-size) and box-averaged
to a 1920-px working buffer; each slider move re-renders only that buffer,
debounced 40 ms and cancellable, so dragging stays fluid on 45 MP files
(~70 ms/render measured). Export re-decodes at full sensor resolution.

## Run it

```
dotnet run --project src/Rawr.Editor.App
```

Requires the .NET 9+ SDK on Windows. The LibRaw native DLLs are in
`src/Rawr.Editor.App/native/` and copy next to the exe automatically.

## Architecture

Three projects, namespaces deliberately matching RAWR's:

| Project | Role |
|---|---|
| `Rawr.Raw` | RAW → `LinearRawImage` (16-bit linear RGB). `LibRawInterop.cs` and `LinearRawImage.cs` are **verbatim copies** of RAWR's; `RawDecoder.cs` is RAWR's proven `ExtractLinearRgb` logic, parameterised half/full-size and stripped of the `Rawr.Core` dependency. |
| `Rawr.Develop` | The develop engine. `DevelopSettings` (all sliders), `DevelopProcessor` (the pipeline), `JpegExporter`. Depends only on `Rawr.Raw`. |
| `Rawr.Editor.App` | WPF/MVVM shell — viewer, adjustments panel, debounced preview, export. |

`DevelopProcessor` is RAWR's `ExposureProcessor.Render` generalised from
exposure-only to the full Basic panel, keeping the same proven structure:
per-channel linear gain (WB + exposure) → a 65 536-entry tone LUT baking
sRGB + camera-match midtone lift + Blacks/Whites/Shadows/Highlights/Contrast →
luma/chroma split with chroma denoise → Vibrance/Saturation → TPDF dither →
BGR24. Deterministic regardless of core count.

## Merging into RAWR later

The split is designed so the merge is additive:

1. Add `Rawr.Develop` to `RAWR.sln` and reference it from `Rawr.App`.
2. **Delete this repo's `Rawr.Raw`** and point `Rawr.Develop` at RAWR's
   `Rawr.Raw` instead — RAWR's `LibRawExtractor.ExtractLinearRgb` already
   returns the identical `Rawr.Raw.LinearRawImage` the engine consumes (this is
   why `RawDecoder` was kept a thin, deletable shim).
3. Add a "Develop" panel/tab to RAWR's `MainWindow` bound to a view-model that
   wraps `DevelopProcessor` — the realtime/debounce logic in
   `MainViewModel` is the reference implementation.
4. RAWR already ships the same LibRaw `native/` DLLs and the same DarkTheme
   palette (copied here verbatim), so no native or visual reconciliation.

Nothing here modifies RAWR; the develop engine only *reads* a `LinearRawImage`.

## Not in the MVP (future)

1:1 / zoom & pan, crop/rotate, tone curve, HSL, sharpening/NR controls,
local adjustments, edit history (RAWR has `EditHistory` to reuse), non-destructive
sidecars, batch. The pipeline order already anticipates most of these.
