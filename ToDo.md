# RAWR Editor — ToDo

## 1. Full-quality editing view (1:1 like Lightroom) — **PHASES 1 & 2 IMPLEMENTED**

### Status (2026-07-21)

**Both phases are implemented and the engine is unit-tested.**
The visible window is rendered sharp at **every zoom**, not just 100%. Once the
sensor buffer is ready, the viewer always overlays a tile rendered at the exact
resolution the screen needs for the current zoom — full sensor pixels at 100%+,
proportionally fewer as you zoom out — so it is never upscaled and never soft, while
staying viewport-cheap at any zoom.

**How the all-zoom sharpness works.** The tile is rendered by `RenderRegion` from a
*developed buffer downsampled to the current zoom's display resolution*
(`_scaledDeveloped` = `_fullDeveloped` box-averaged to `long × ts`, where
`ts = min(1, zoom)`). At 100% `ts = 1` and it's the full-resolution 1:1 path; below
100% it's the visible region at display resolution. This is the same
develop-after-downsample the preview already uses, so it's consistent — and because
`RenderRegion` works on any developed buffer, the engine needed no changes.
`_scaledDeveloped` is cached by (geometry, long edge), so editing at a fixed zoom
only re-runs the tile. The old fixed-1920 preview buffer remains only as the instant
fallback shown for the ~1 s before the sensor decode lands (still viewport-sized via
Phase 1).

**Phase 1 (adaptive preview):** the preview buffer's long edge tracks the viewport's
device pixels, clamped to `[MinPreviewLong, MaxPreviewLong]` = 1280…2560 to hold the
measured drag-latency budget. The half-size decode is kept resident (`_previewSource`)
so a window resize re-derives the preview without decoding again; rebuilds are
debounced and only happen while at Fit (a rebuild changes the bitmap size and snaps
to Fit, so doing it mid-zoom would fight the user). The status line now reads
"…preview · zoom to 100% for full resolution" so the number isn't mistaken for the
photo's real resolution.

- Engine: `DevelopProcessor.RenderRegion(developed, settings, roi)` renders a padded
  viewport tile through the existing pipeline (region-aware mask compositing +
  offset grain + windowed dither). `RenderRegionTests` proves a tile equals the
  same window of a full-frame `Render` — byte-identical for a whole-frame window,
  within ~1–2 levels for interior tiles (the regional filters' float running sums).
  All 264 tests pass.
- View-model: the sensor-resolution buffer decodes in the background after the
  preview appears (`LoadFullResolutionAsync`); `_fullDeveloped` caches the
  geometry-applied buffer; `SetDetailRequest` / `RenderDetailTile` serve the viewer.
- Viewer: the 1:1 tile switches on at true 100%+ (`DetailTriggerScale`), tracks
  pan/zoom by a `MatrixTransform`, throttles re-renders, and the zoom label + 1:1
  button now read **sensor** pixels, not preview-buffer pixels.

**Still open / to verify:**
1. **Visual verification.** The tile-placement math is derived to match the preview
   transform but has not been run against a real RAW. Open a photo and confirm the
   view is crisp at Fit, at mid-zoom (e.g. 50–70%), and at 100%+; that the tile
   registers exactly with the photo (no jump/offset as it refreshes) and pans in
   lockstep; and that Sharpening at 100% matches the export.
2. **Double render while the tile is active.** Each edit renders both the fallback
   preview and the tile (~2× a preview pass; both viewport-bounded). Fine at typical
   sizes; if it drags on huge displays, skip the preview render while the tile fully
   covers the viewport and derive Before/After from the tile.

### The problem

The photo the user edits is **never shown at full quality**. It is downsampled twice before it reaches the screen, and the "100%" button lies about what it is showing.

The load path (`MainViewModel.LoadAsync`, `src/Rawr.Editor.App/ViewModels/MainViewModel.cs:1052-1056`):

1. **Half-size decode** — `RawDecoder.DecodeLinearRgb(path, halfSize: true)` sets LibRaw's `half_size=1`, averaging each 2×2 Bayer cell into one RGB sample (½ width, ½ height → ¼ the pixels). `src/Rawr.Raw/RawDecoder.cs:54-58`.
2. **Box-average down to 1920 px wide** — `full.Downsample(PreviewWidth)` with `PreviewWidth = 1920`. `src/Rawr.Editor.App/ViewModels/MainViewModel.cs:34`.

A 45 MP sensor (~8192×5464) becomes ~4096×2732 at half-size, then ~1920×1280 — about **2.5 MP, ~5 % of the full pixel count**. Every slider re-renders only that small buffer.

Consequences:

- **Fit view is soft on hi-DPI panels.** On a 4K/5K display the image area is usually wider than 1920 px, so the preview is *upscaled* to fit — the class comment's claim of "sharp fit-to-screen on a 4K panel" only holds when the image area is ≤1920 px wide.
- **"100%" is not 100 %.** `OnOneToOneClick` sets zoom to "one *buffer* pixel per screen pixel" (`src/Rawr.Editor.App/MainWindow.xaml.cs:253-264`), and `UpdateZoomLabel` reports the percentage against that same buffer (`:303-314`). On a 45 MP file, the "100%" view is really ~44 % of a true sensor-pixel view, upscaled and soft. There is **no genuine 1:1 view of the RAW anywhere in the UI.**
- **Pixel-level sliders can't be judged.** Sharpening, Luminance/Colour Noise Reduction, Texture, and Grain are spatial filters whose radii scale with the buffer. On a ⅕-linear-resolution preview they operate at the wrong scale and show the wrong result — what you see at "100%" is not what the exported file looks like at 100 %. This is the single biggest fidelity gap in the app.

Export is unaffected — `ExportAsync`/`ExportAdvancedAsync` re-decode with `halfSize: false` at full resolution (`:1270`), so saved files are correct. Only the on-screen editing view is degraded.

### Goal / definition of done

- At **Fit**, the frame renders sharp on any display (no upscaling of a sub-viewport buffer).
- At **1:1 / zoomed-in**, the user sees **true sensor pixels** — the visible region processed from the full-resolution decode, so Sharpening/NR/Texture/Grain match the export exactly.
- Editing stays **fluid** (slider drags still ~50–130 ms per pass). This is non-negotiable and is the reason the current downsample exists.

### Why the naive fix is wrong

"Just decode full-res and render the whole frame on every edit" would make a 45 MP pipeline pass take seconds per slider move — the exact problem the half-size preview was built to avoid. Lightroom does **not** do this either. Lightroom only ever processes about as many pixels as the screen can show: at Fit it processes at fit resolution; at 1:1 it processes only the **visible region** at full resolution, re-processing that region on each edit. The cost is bounded by *screen* pixels, not *sensor* pixels.

### Recommended approach — viewport-driven region-of-interest (ROI) rendering

**The engine already supports this.** Mask compositing renders a padded sub-rectangle of the image with correct spatial-filter radii, using building blocks that are exactly what a 1:1 tile needs:

- `LinearRawImage.Crop(x, y, w, h)` — copies out a clamped sub-rectangle. `src/Rawr.Raw/LinearRawImage.cs:107-130`. Comment already anticipates this use: *"re-run the develop pipeline over the region a mask actually covers rather than over the whole frame."*
- `DevelopProcessor.RenderRgbPlanes(region, settings, po, ct, contextW, contextH, ...)` — renders a crop while sizing regional filters (`EdgeAwareLuma`, `LocalHighlights`, Texture/Clarity/Dehaze) against the **full-image** `contextW`/`contextH`, so a tile measures "region" over the same neighbourhood the whole frame does. `src/Rawr.Develop/DevelopProcessor.cs:353-394`, `433-543`.
- The pad-for-spatial-filters logic (`pad = 2 * regional + 16`) is already worked out for masks (`:444-446`) and applies verbatim to a viewport tile.

So a 1:1 tile is architecturally the same operation as a mask crop: pad the visible rectangle, `Crop` it out of the full-res buffer, render with full-image context dims, blit 1:1.

#### Phase 1 — sharp Fit view (cheap, low-risk, ship first)

Match the preview buffer to the viewport instead of a hard-coded 1920.

1. Make `PreviewWidth` adaptive: render the fit preview at the viewer's device-pixel width (`ViewerHost.ActualWidth × DPI scale`), clamped to a sane ceiling (e.g. 2560–3840) so fluidity holds. Note the fluidity budget from the class comment: ~70 ms at 1920, ~130 ms at 2560.
2. Because the source is decoded half-size (~4096 wide on 45 MP), the buffer can grow to ~2560–3840 and still be a genuine *downsample* (sharp), not an upscale.
3. Re-derive the preview buffer when the viewport is resized past the current buffer width (debounced). Keep the current buffer on shrink.

Touch-points: `PreviewWidth` and `LoadAsync` in `MainViewModel.cs`; a viewport-size signal from `MainWindow.OnViewerSizeChanged` (`MainWindow.xaml.cs:130-139`).

This alone removes the Fit-view softness and makes "100%" honest up to the new buffer width, without touching the render engine.

#### Phase 2 — true 1:1 / zoomed view from full-res decode (the real fix)

1. **Cache the full-resolution linear buffer.** Add a lazily-populated `LinearRawImage? _full` alongside `_preview`. Decode it with `halfSize: false` on a background thread — either eagerly right after load (the preview appears first, full-res arrives a beat later) or on first zoom past Fit. Memory: 45 MP × 3 × 2 bytes ≈ **270 MB** resident; document this and consider freeing it when idle/on file switch.
2. **Detect "needs real pixels."** The viewer already computes an absolute scale (`ComputeFitScale() * _rotationFit * _userScale`, `MainWindow.xaml.cs:143-155`, `303-314`). When that scale means ≥1 source-sensor-pixel per screen pixel — i.e. the user has zoomed in far enough that the 1920 buffer would visibly soften — switch that render to the ROI path.
3. **Compute the visible rectangle in full-res buffer coordinates** from the pan/zoom transform (`_userScale`, `_tx`, `_ty`, `_rotationFit`), pad it by `2 * regional + 16`, and hand it to a new `DevelopProcessor.RenderRegion(full, settings, srcRect, ct)` that wraps the existing `Crop` + `RenderRgbPlanes(contextW: full.Width, contextH: full.Height)` + quantise-to-Bgr24. Blit the tile at 1:1 under the current transform.
4. **Re-render the tile on edit and on pan/zoom**, through the same debounce/cancellation already in `RenderPreview` (`MainViewModel.cs:983-1025`). A viewport tile (~2560×1440 ≈ 3.7 MP) costs about the same as today's full-frame preview, so drags stay fluid — that is the whole point of ROI.
5. **Fall back to the small preview at Fit** so zooming out stays instant and the neutral/Before cache still works.

#### Geometry (the one real subtlety)

`Geometry.Apply` bakes crop/straighten/orientation into the buffer before tone work (`DevelopProcessor.cs:315`; neutral geometry returns the buffer unchanged). Two options for the ROI path:

- **Simple first cut:** apply geometry to the full-res buffer once, cache the result, invalidate on any geometry change, then `Crop` visible tiles from *that*. Costs a full-res geometry pass + a second 270 MB buffer while it exists.
- **Leaner, later:** map the viewport rectangle back through the geometry transform to raw-sensor coordinates and crop the un-transformed buffer, applying only the local transform to the tile. Avoids the second full buffer but needs careful coordinate math (rotation/flip/crop inverse).

Start with the simple cut; optimise only if memory bites.

#### Masks & overlays

- Mask compositing (`ComposeMasks`) works over the whole frame. For an ROI tile, composite only masks whose padded bounds intersect the tile — the union/bounds logic already exists (`:462-483`) and just needs intersecting with the tile rect.
- The mask/crop overlays draw in screen space via `ViewScale`/`ViewOffset` (`MainWindow.xaml.cs:147-156`); they already follow the transform and need no change, but verify alignment once the source is a full-res tile rather than the scaled preview.

#### Zoom-label semantics

Once Phase 2 lands, `OnOneToOneClick` and `UpdateZoomLabel` must report against **sensor** pixels, not buffer pixels, so "100%" finally means one RAW pixel per screen pixel. `MainWindow.xaml.cs:253-264`, `303-314`.

### Risks & notes

- **Memory:** the full-res linear buffer (~270 MB on 45 MP) plus tile float planes. Free `_full` on file switch and consider releasing after an idle timeout. RAWR is a culling app that folds this editor in (see memory `project-merge-into-rawr`) — keep the ROI logic in `Rawr.Develop` so RAWR inherits it and doesn't depend on WPF viewer code.
- **Determinism:** Grain is position-dependent and is applied over the whole frame after masks (`DevelopProcessor.cs:326-332`). A tile must generate grain using the tile's absolute offset into the full frame, or the grain field will differ between the Fit preview and the 1:1 tile. Verify `Effects.BuildGrain` can be offset, or apply grain in tile-absolute coordinates.
- **Verification:** compare a 1:1 on-screen tile against the exported file cropped to the same region — they should be pixel-identical (modulo dither), since both now run `RenderRgbPlanes` at full resolution. That equivalence is the acceptance test.

### Suggested order

Phase 1 (adaptive preview width) is a small, safe, immediately visible win — do it first. Phase 2 (full-res ROI) is the feature that actually delivers "edit at full quality like Lightroom"; land it behind the existing zoom controls so Fit behaviour is unchanged.
