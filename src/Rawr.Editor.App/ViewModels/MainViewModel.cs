using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Rawr.Develop;
using Rawr.Editor.App;
using Rawr.Editor.App.Controls;
using Rawr.Editor.App.Theming;
using Rawr.Raw;

// WPF has its own Geometry (a drawing path), so the crop/straighten maths needs
// naming explicitly wherever the two namespaces meet.
using ImageGeometry = Rawr.Develop.Geometry;

namespace Rawr.Editor.App.ViewModels;

/// <summary>
/// Drives the single-photo develop screen. One RAW is decoded once at half-size
/// and box-averaged down to a viewport-sized preview buffer; every slider move
/// then re-renders only that small buffer, debounced and cancellable, so editing
/// stays smooth on big sensors. Zooming to 100% overlays a full-resolution tile
/// for the visible window, and export re-decodes at full resolution.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    // Preview working resolution, as the buffer's long edge. Sized to the
    // viewport (see SetPreviewLongEdge) so the Fit view isn't upscaled on a large
    // or hi-DPI panel, but capped so a full pipeline pass stays fluid for slider
    // drags (measured: 45 MP CR3 → ~130 ms at 2560 px long edge, ~70 ms at 1920).
    // True full resolution is served by the 1:1 tile at 100%+ zoom
    // (DevelopProcessor.RenderRegion); export always re-decodes at full res.
    private const int DefaultPreviewLong = 1920;
    private const int MinPreviewLong = 1280;
    private const int MaxPreviewLong = 2560;

    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _renderCts;
    private LinearRawImage? _preview;   // downsampled, drives realtime editing
    private string? _rawPath;
    private bool _suppressRender;
    private BitmapSource? _neutralPreview;   // cached unedited render; powers the Before view
    private GeometrySettings _neutralGeometry = new();   // the crop _neutralPreview was rendered under
    private BitmapSource? _currentRender;    // latest edited render; what After shows
    private ExportSettings _lastExport = new();   // remembered between Export… dialog opens

    // ── Full-resolution 1:1 view ────────────────────────────────────────────
    // The preview above is a downsample; when the user zooms to 100% the viewer
    // asks for real sensor pixels over just the visible window. _full is the
    // sensor-resolution decode (loaded lazily in the background after the
    // preview appears); _fullDeveloped is that buffer with the current geometry
    // baked in, cached so a slider drag re-renders only the tile — see
    // DevelopProcessor.RenderRegion.
    private LinearRawImage? _full;
    private LinearRawImage? _fullDeveloped;
    private GeometrySettings _fullDevelopedGeometry = new();
    // A copy of _fullDeveloped downsampled to the current zoom's display
    // resolution. This is what makes every zoom sharp, not just 100%+: the tile is
    // rendered from a buffer that already has exactly the pixels the screen needs
    // for this zoom — full sensor pixels at 100%, fewer as you zoom out — so it is
    // never upscaled. Cached by (geometry, long edge) so editing at a fixed zoom
    // only re-runs the tile.
    private LinearRawImage? _scaledDeveloped;
    private GeometrySettings _scaledDevelopedGeometry = new();
    private int _scaledDevelopedLong;
    private readonly object _fullDevelopedLock = new();
    private CancellationTokenSource? _detailCts;
    private PixelRect _detailRoi;
    private double _detailScale = 1.0;
    private bool _detailWanted;

    // ── Adaptive preview resolution (Phase 1) ───────────────────────────────
    // The half-size decode is kept resident so the preview buffer can be re-derived
    // at a new size when the viewport grows, without decoding again. _targetPreviewLong
    // is the long edge the viewer last asked for; the rebuild is debounced.
    private LinearRawImage? _previewSource;
    private int _targetPreviewLong = DefaultPreviewLong;
    private readonly DispatcherTimer _previewResizeDebounce;

    // The developed 1:1 buffers (_fullDeveloped, _scaledDeveloped) can hold up to
    // ~540 MB on a 45 MP file, yet are only needed while the user is zoomed in. This
    // timer frees them after a spell of no rendering activity; they rebuild cheaply
    // from _full (kept resident) on the next tile render. _full itself is not freed —
    // re-decoding it costs a full-resolution RAW pass, which is the opposite of cheap.
    private readonly DispatcherTimer _idleFreeTimer;
    private const double IdleFreeSeconds = 30.0;

    public MainViewModel()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };
        _previewResizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _previewResizeDebounce.Tick += (_, _) => { _previewResizeDebounce.Stop(); RebuildPreviewForViewport(); };
        _idleFreeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IdleFreeSeconds) };
        _idleFreeTimer.Tick += (_, _) => { _idleFreeTimer.Stop(); InvalidateFullDeveloped(); };
        SelectedCropAspect = CropAspects[0];
        StatusText = RawDecoder.IsAvailable
            ? "Open a RAW photo to begin."
            : "LibRaw native library not found — RAW decoding unavailable.";
    }

    // ── Adaptive preview sizing ─────────────────────────────────────────────

    /// <summary>
    /// The viewer's report of how many device pixels its long edge spans, so the
    /// preview can be built to match instead of being upscaled from a fixed 1920.
    /// Stored always (it may arrive before any photo); a rebuild is scheduled only
    /// when a photo is loaded and the change is worth the re-downsample. The viewer
    /// calls this only while at Fit, so a rebuild — which resets the zoom to Fit as
    /// the bitmap dimensions change — never interrupts a zoomed-in session.
    /// </summary>
    public void SetPreviewLongEdge(int deviceLongEdge)
    {
        int target = Math.Clamp(deviceLongEdge, MinPreviewLong, MaxPreviewLong);
        if (target == _targetPreviewLong) return;
        _targetPreviewLong = target;

        if (_previewSource is null) return;   // no photo yet; applied on next load
        _previewResizeDebounce.Stop();
        _previewResizeDebounce.Start();
    }

    /// <summary>Width to hand <see cref="LinearRawImage.Downsample"/> so the preview's
    /// long edge lands near <paramref name="targetLong"/>, never above the source.</summary>
    private static int PreviewTargetWidth(LinearRawImage source, int targetLong)
    {
        int sw = source.Width, sh = source.Height;
        int desiredLong = Math.Min(targetLong, Math.Max(sw, sh));
        return sw >= sh ? desiredLong : (int)Math.Ceiling(desiredLong * sw / (double)sh);
    }

    /// <summary>Re-derive the preview buffer at the current viewport size and
    /// re-render. Only meaningful changes go through, so a nudge-resize is free.</summary>
    private void RebuildPreviewForViewport()
    {
        var source = _previewSource;
        if (source is null || !HasPhoto) return;

        int tw = PreviewTargetWidth(source, _targetPreviewLong);
        // Skip churn: a few percent either way is invisible and not worth a re-downsample.
        if (_preview is { } cur && Math.Abs(tw - cur.Width) <= Math.Max(8, cur.Width * 0.05)) return;

        _preview = source.Downsample(tw);

        // The neutral (Before) snapshot is tied to the old dimensions.
        _neutralPreview = null;
        _neutralGeometry = new GeometrySettings();

        // The crop overlay measures against the preview buffer.
        OnPropertyChanged(nameof(CropSourceWidth));
        OnPropertyChanged(nameof(CropSourceHeight));
        CropVisualsChanged?.Invoke(this, EventArgs.Empty);

        RenderPreview();
        StatusText = $"{FileName} — {_preview.Width}×{_preview.Height} preview · zoom to 100% for full resolution";
    }

    // ── Loaded-photo state ──
    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string? _fileName;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasPhoto;
    [ObservableProperty] private bool _isShowingBefore;

    partial void OnIsShowingBeforeChanged(bool value)
    {
        if (!HasPhoto) return;
        if (value) _ = ShowBeforeAsync();
        else PreviewImage = _currentRender;

        // The sharp tile carries the edit in After and the neutral frame in Before;
        // a toggle flips which, so rebuild it under the new state. Without this the
        // tile would keep the old side's pixels sharp over the new preview. No-op
        // when the tile isn't live — KickDetailRender self-guards on _detailWanted.
        KickDetailRender();

        StatusText = value
            ? "Showing BEFORE — toggle off to return to your edits."
            : $"{FileName} — edits apply live";
    }

    /// <summary>
    /// Put the unedited render on screen, rebuilding it first if the crop has
    /// moved since it was cached.
    ///
    /// <para>Before has to show the <i>same frame</i> as After — a Before view
    /// that quietly reverted the crop would change the composition along with
    /// the tone, which is not the comparison anyone is asking for. Geometry is
    /// therefore the one part of the edit the neutral render keeps, and the
    /// rebuild is lazy so cropping costs nothing until someone asks.</para>
    /// </summary>
    private async Task ShowBeforeAsync()
    {
        var preview = _preview;
        if (preview is null) return;

        var geometry = PreviewGeometry();
        if (_neutralPreview is not null && _neutralGeometry.Matches(geometry))
        {
            PreviewImage = _neutralPreview;
            return;
        }

        var settings = new DevelopSettings { Geometry = geometry };
        var bmp = await Task.Run(() => DevelopProcessor.Render(preview, settings));

        _neutralPreview = bmp;
        _neutralGeometry = geometry;
        if (IsShowingBefore) PreviewImage = bmp;
    }

    [RelayCommand]
    private void ToggleBefore()
    {
        if (!HasPhoto) return;
        IsShowingBefore = !IsShowingBefore;
    }

    // ── Full-resolution detail tile (bound by the viewer) ──────────────────
    /// <summary>The sharp tile for the visible window, or null when it is not
    /// showing. The viewer places it over the preview.</summary>
    [ObservableProperty] private BitmapSource? _detailImage;

    /// <summary>Whether the viewer should currently show <see cref="DetailImage"/>
    /// on top of the downsampled preview.</summary>
    [ObservableProperty] private bool _detailActive;

    /// <summary>Top-left of the rendered tile, in full-resolution output pixels
    /// (fractional) — what the viewer uses to register the tile against the photo.</summary>
    [ObservableProperty] private double _detailOriginX;
    [ObservableProperty] private double _detailOriginY;

    /// <summary>Tile pixels per full-resolution output pixel: 1 at 100%+, less as
    /// the user zooms out. The viewer needs it to place the tile at the right size.</summary>
    [ObservableProperty] private double _detailScaleValue = 1.0;

    /// <summary>True once the sensor-resolution buffer has finished decoding, so
    /// the viewer can offer the sharp view. Raised on the UI thread.</summary>
    [ObservableProperty] private bool _detailReady;

    /// <summary>The size of the full-resolution frame the viewer maps pixels
    /// against — the current geometry's output size over the sensor buffer. Zero
    /// until the sensor decode lands.</summary>
    public int DetailFrameWidth
        => _full is { } f ? ImageGeometry.OutputSize(_geometry, f.Width, f.Height).width : 0;
    public int DetailFrameHeight
        => _full is { } f ? ImageGeometry.OutputSize(_geometry, f.Width, f.Height).height : 0;

    /// <summary>
    /// The viewer's request for the sharp view: render <paramref name="roi"/> (the
    /// visible window, in full-resolution output pixels) at <paramref name="scale"/>
    /// tile-pixels per output-pixel — 1.0 at 100%+, the zoom fraction below that so
    /// the tile carries exactly the pixels the screen shows and is never upscaled.
    /// When <paramref name="wanted"/> is false the tile is dropped and the preview
    /// shows through. Called as the user zooms and pans.
    /// </summary>
    public void SetDetailRequest(PixelRect roi, double scale, bool wanted)
    {
        _detailWanted = wanted && DetailReady && _full is not null;
        _detailRoi = roi;
        _detailScale = scale;

        if (!_detailWanted)
        {
            _detailCts?.Cancel();
            if (DetailActive) DetailActive = false;
            DetailImage = null;
            return;
        }

        KickDetailRender();
    }

    /// <summary>Start (or restart) a render of the current detail window. Cancels
    /// any in-flight tile — panning and slider drags both outrun a render.</summary>
    private void KickDetailRender()
    {
        if (!_detailWanted || _full is null || MaskPreviewActive) return;

        NoteRenderActivity();   // keep the developed buffers alive while the tile is in use

        // Cancel the previous tile but do not Dispose it: a superseded render may
        // still be mid-flight, and registering its Parallel.For on a disposed source
        // throws ObjectDisposedException (which is not OperationCanceledException, so
        // it escapes the catch). Disposing here would also make the next Cancel()
        // throw once a render has finished. A CTS with no wait-handle or timer holds
        // nothing unmanaged, so GC reclaims it once the render lets go of the token.
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;

        // In Before the tile must match the neutral preview, not the edit —
        // otherwise toggling Before would leave the edited pixels sharp on screen.
        var settings = IsShowingBefore ? NeutralSettings() : CurrentSettings();
        var roi = _detailRoi;
        var scale = _detailScale;
        var ct = cts.Token;

        Task.Run(() =>
        {
            try { RenderDetailTile(settings, roi, scale, ct); }
            catch (OperationCanceledException) { /* superseded */ }
        }, ct);
    }

    private void RenderDetailTile(DevelopSettings settings, PixelRect roi, double scale,
                                 CancellationToken ct)
    {
        var full = _full;
        if (full is null) return;
        double ts = Math.Clamp(scale, 0.02, 1.0);

        // Produce the buffer this zoom needs: geometry baked in (cached per
        // crop/orientation), then downsampled to the display resolution for the
        // current zoom (cached per long edge). At 100% ts == 1 and both hand back
        // the full-resolution buffer unchanged, so this reduces to the true 1:1 path.
        LinearRawImage scaled;
        double sx, sy;
        lock (_fullDevelopedLock)
        {
            if (_fullDeveloped is null || !_fullDevelopedGeometry.Matches(settings.Geometry))
            {
                _fullDeveloped = ImageGeometry.Apply(full, settings.Geometry);
                _fullDevelopedGeometry = settings.Geometry.Clone();
                _scaledDeveloped = null;
            }
            var developed = _fullDeveloped;

            int fullLong = Math.Max(developed.Width, developed.Height);
            int targetLong = Math.Max(1, (int)Math.Round(fullLong * ts));
            // Bucket to 64 px so a continuous zoom doesn't re-downsample every step.
            targetLong = Math.Min(fullLong, (targetLong + 63) / 64 * 64);

            if (_scaledDeveloped is null || _scaledDevelopedLong != targetLong
                || !_scaledDevelopedGeometry.Matches(settings.Geometry))
            {
                int targetW = developed.Width >= developed.Height
                    ? targetLong
                    : (int)Math.Ceiling(targetLong * developed.Width / (double)developed.Height);
                _scaledDeveloped = developed.Downsample(targetW);
                _scaledDevelopedLong = targetLong;
                _scaledDevelopedGeometry = settings.Geometry.Clone();
            }

            scaled = _scaledDeveloped;
            // Actual scale achieved (Downsample rounds), used to map the window in.
            sx = scaled.Width / (double)developed.Width;
            sy = scaled.Height / (double)developed.Height;
        }

        // The visible window, mapped from full-output pixels into the scaled buffer
        // and clamped to it so the returned tile is exactly this rectangle.
        int rx0 = Math.Clamp((int)Math.Floor(roi.X * sx), 0, scaled.Width);
        int ry0 = Math.Clamp((int)Math.Floor(roi.Y * sy), 0, scaled.Height);
        int rx1 = Math.Clamp((int)Math.Ceiling(roi.Right * sx), 0, scaled.Width);
        int ry1 = Math.Clamp((int)Math.Ceiling(roi.Bottom * sy), 0, scaled.Height);
        if (rx1 <= rx0) rx1 = Math.Min(scaled.Width, rx0 + 1);
        if (ry1 <= ry0) ry1 = Math.Min(scaled.Height, ry0 + 1);
        var scaledRoi = new PixelRect(rx0, ry0, rx1 - rx0, ry1 - ry0);

        ct.ThrowIfCancellationRequested();
        var bmp = DevelopProcessor.RenderRegion(scaled, settings, scaledRoi, ct);
        if (ct.IsCancellationRequested) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (ct.IsCancellationRequested) return;
            // Report placement in full-output coordinates so the viewer can register
            // the tile the same way it maps the preview.
            DetailOriginX = rx0 / sx;
            DetailOriginY = ry0 / sy;
            DetailScaleValue = sx;
            DetailImage = bmp;
            DetailActive = true;
        });
    }

    /// <summary>Restart the idle-free countdown. Called whenever a render happens,
    /// so the developed 1:1 buffers are only reclaimed after the user goes quiet.</summary>
    private void NoteRenderActivity()
    {
        _idleFreeTimer.Stop();
        _idleFreeTimer.Start();
    }

    /// <summary>Drop the cached developed buffers — on a new photo, or when the
    /// geometry changes and the developed cache is stale.</summary>
    private void InvalidateFullDeveloped()
    {
        lock (_fullDevelopedLock)
        {
            _fullDeveloped = null;
            _fullDevelopedGeometry = new GeometrySettings();
            _scaledDeveloped = null;
            _scaledDevelopedGeometry = new GeometrySettings();
            _scaledDevelopedLong = 0;
        }
    }

    // ── Adjustments (neutral = 0) ──
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _tint;
    [ObservableProperty] private double _exposure;
    [ObservableProperty] private double _contrast;
    [ObservableProperty] private double _highlights;
    [ObservableProperty] private double _shadows;
    [ObservableProperty] private double _whites;
    [ObservableProperty] private double _blacks;
    [ObservableProperty] private double _vibrance;
    [ObservableProperty] private double _saturation;

    // ── Detail ──────────────────────────────────────────────────────────────
    // Unlike every slider above, these are not bipolar and several are not
    // neutral at 0 — they carry Lightroom's panel defaults, and Colour NR's
    // default is the strength that reproduces the chroma blur the pipeline has
    // always applied. Reset returns them here, not to zero.
    [ObservableProperty] private double _sharpening;
    [ObservableProperty] private double _sharpenRadius = Detail.DefaultSharpenRadius;
    [ObservableProperty] private double _sharpenDetail = Detail.DefaultSharpenDetail;
    [ObservableProperty] private double _sharpenMasking = Detail.DefaultSharpenMasking;

    [ObservableProperty] private double _luminanceNoiseReduction;
    [ObservableProperty] private double _luminanceNoiseDetail = Detail.DefaultLuminanceDetail;
    [ObservableProperty] private double _luminanceNoiseContrast = Detail.DefaultLuminanceContrast;

    [ObservableProperty] private double _colorNoiseReduction = Detail.DefaultColorNoiseReduction;

    // ── Effects ─────────────────────────────────────────────────────────────
    [ObservableProperty] private double _texture;
    [ObservableProperty] private double _clarity;
    [ObservableProperty] private double _dehaze;
    [ObservableProperty] private double _grainAmount;
    [ObservableProperty] private double _grainSize = Effects.DefaultGrainSize;
    [ObservableProperty] private double _grainRoughness = Effects.DefaultGrainRoughness;

    // ── Section switches (Lightroom's per-panel on/off) ──────────────────────
    // Each turns its whole section off in the render without disturbing the
    // sliders under it: CurrentSettings substitutes that section's defaults while
    // the switch is off, so the photo shows as if the section were untouched and
    // flipping it back restores the edit intact. It is the same "keep the settings,
    // drop them from the render" A/B the per-mask Enabled checkbox gives locally,
    // one level up. The names are on AdjustmentNames, so a toggle re-renders.
    [ObservableProperty] private bool _lightEnabled = true;
    [ObservableProperty] private bool _colorEnabled = true;
    [ObservableProperty] private bool _colorMixerEnabled = true;
    [ObservableProperty] private bool _effectsEnabled = true;
    [ObservableProperty] private bool _sharpeningEnabled = true;
    [ObservableProperty] private bool _noiseReductionEnabled = true;

    // ── Masking mask overlay ────────────────────────────────────────────────
    // Lightroom shows this on Alt-drag; here it is an explicit option, because a
    // modifier key is undiscoverable and this is the one control in the panel
    // whose effect is genuinely hard to see in the photo itself.
    [ObservableProperty] private bool _showSharpenMask;

    /// <summary>Set by the Masking row while its thumb is held.</summary>
    [ObservableProperty] private bool _isDraggingMasking;

    /// <summary>Whether the viewer should currently be showing the mask rather
    /// than the photo.</summary>
    private bool MaskPreviewActive => ShowSharpenMask && IsDraggingMasking && HasPhoto;

    partial void OnIsDraggingMaskingChanged(bool value)
    {
        if (!ShowSharpenMask || !HasPhoto) return;

        // Coming out of the drag, put the photo back at once rather than leaving
        // the mask up for the ~100 ms a fresh render costs. _currentRender is one
        // Masking value stale, which is invisible next to the alternative of the
        // overlay hanging on after the mouse is released.
        if (!value && _currentRender is not null && !IsShowingBefore)
            PreviewImage = _currentRender;

        RenderPreview();
    }

    partial void OnShowSharpenMaskChanged(bool value)
    {
        // Toggling the option mid-drag should take effect immediately.
        if (IsDraggingMasking && HasPhoto) RenderPreview();
    }

    // ── Temperature: relative (mireds about the camera WB) or absolute Kelvin ──
    // Both are kept as live state rather than deriving one from the other on the
    // fly, because the relative slider only spans ±100 mireds: round-tripping a
    // 40000 K setting through it would silently clamp the illuminant. Conversion
    // therefore happens exactly once, at the moment the user flips the mode.
    [ObservableProperty] private bool _useKelvin;
    [ObservableProperty] private double _temperatureKelvin = WhiteBalance.AsShotKelvin;

    /// <summary>What the Temp row's slider is bound to. Routes to whichever of
    /// the two temperature properties the current mode owns.</summary>
    public double TemperatureSliderValue
    {
        get => UseKelvin ? TemperatureKelvin : Temperature;
        set
        {
            if (UseKelvin) TemperatureKelvin = value;
            else Temperature = value;
        }
    }

    public double TempMinimum => UseKelvin ? WhiteBalance.MinKelvin : -100.0;
    public double TempMaximum => UseKelvin ? WhiteBalance.MaxKelvin : 100.0;
    // 50 K per click is about a quarter mired at daylight — fine enough to be
    // smooth, coarse enough that the readout doesn't flicker through values
    // nobody can see.
    public double TempStep => UseKelvin ? 50.0 : 1.0;

    /// <summary>Kelvin is laid out logarithmically; the relative slider is already
    /// linear in mireds and symmetric, so it stays linear. See <see cref="SliderScales"/>.</summary>
    public SliderScale TempScale => UseKelvin ? SliderScale.Logarithmic : SliderScale.Linear;
    /// <summary>Where "no white balance change" sits: 0 on the relative slider,
    /// the as-shot anchor on the Kelvin one.</summary>
    public double TempOrigin => UseKelvin ? WhiteBalance.AsShotKelvin : 0.0;

    // ── Coloured slider tracks ──
    // Temp's is mode-dependent: the two modes span different illuminant ranges, so
    // the same pixel position is genuinely a different colour. The other three are
    // fixed, and SliderTracks caches them.
    //
    // Both are sampled over 0…1 of the *track* and converted through the same
    // scale the thumb uses. Sampling in Kelvin instead would slide the gradient
    // out of register with the thumb on the log scale — the blue end would occupy
    // the leftmost sixth while the thumb reporting 3000 K sat a third of the way
    // along, over what the track was drawing as daylight.
    public Brush TempTrackBrush => UseKelvin
        ? SliderTracks.Temperature(0.0, 1.0,
            p => SliderScales.ToValue(SliderScale.Logarithmic, p,
                                      WhiteBalance.MinKelvin, WhiteBalance.MaxKelvin))
        : SliderTracks.Temperature(-100.0, 100.0, WhiteBalance.TemperatureToKelvin);

    public Brush TintTrackBrush => SliderTracks.Tint;
    public Brush PresenceTrackBrush => SliderTracks.Saturation;

    // ── Colour Mixer ────────────────────────────────────────────────────────
    // Eight bands × three sliders, but the panel only ever shows one band at a
    // time (Lightroom's "Adjust: Color" mode). The three Band* properties below
    // are a view onto whichever band the swatch row has selected, in the same
    // shape as TemperatureSliderValue routing to one of two temperature fields.
    private readonly ColorMixerSettings _colorMixer = new();

    [ObservableProperty] private ColorBand _selectedColorBand = ColorBand.Red;

    /// <summary>The swatch row: one dot per band, in Lightroom's order.</summary>
    public IReadOnlyList<ColorBandItem> ColorBands { get; } =
        Enum.GetValues<ColorBand>().Select(b => new ColorBandItem(b)).ToArray();

    public double BandHue
    {
        get => _colorMixer[SelectedColorBand].Hue;
        set
        {
            if (_colorMixer[SelectedColorBand].Hue == value) return;
            _colorMixer[SelectedColorBand].Hue = value;
            OnPropertyChanged();
        }
    }

    public double BandSaturation
    {
        get => _colorMixer[SelectedColorBand].Saturation;
        set
        {
            if (_colorMixer[SelectedColorBand].Saturation == value) return;
            _colorMixer[SelectedColorBand].Saturation = value;
            OnPropertyChanged();
        }
    }

    public double BandLuminance
    {
        get => _colorMixer[SelectedColorBand].Luminance;
        set
        {
            if (_colorMixer[SelectedColorBand].Luminance == value) return;
            _colorMixer[SelectedColorBand].Luminance = value;
            OnPropertyChanged();
        }
    }

    public Brush BandHueTrackBrush => SliderTracks.BandHue(SelectedColorBand);
    public Brush BandSaturationTrackBrush => SliderTracks.BandSaturation(SelectedColorBand);
    public Brush BandLuminanceTrackBrush => SliderTracks.BandLuminance(SelectedColorBand);

    partial void OnSelectedColorBandChanged(ColorBand value)
    {
        // Picking a different band changes what the three rows *display*, not the
        // photo — so the value notifications are suppressed. Without this, every
        // swatch click would cost a full pipeline pass to render a pixel-identical
        // image, because the Band* names are on the render-scheduling list.
        bool prior = _suppressRender;
        _suppressRender = true;
        try
        {
            OnPropertyChanged(nameof(BandHue));
            OnPropertyChanged(nameof(BandSaturation));
            OnPropertyChanged(nameof(BandLuminance));
        }
        finally { _suppressRender = prior; }

        OnPropertyChanged(nameof(BandHueTrackBrush));
        OnPropertyChanged(nameof(BandSaturationTrackBrush));
        OnPropertyChanged(nameof(BandLuminanceTrackBrush));
    }

    partial void OnTemperatureChanged(double value) => OnPropertyChanged(nameof(TemperatureSliderValue));
    partial void OnTemperatureKelvinChanged(double value) => OnPropertyChanged(nameof(TemperatureSliderValue));

    partial void OnUseKelvinChanged(bool value)
    {
        // Carry the current illuminant across, so flipping the mode never moves
        // the picture. The conversion is suppressed because it is a no-op by
        // design; a render is scheduled once at the end, and only because the
        // relative direction can clamp (±100 mireds cannot express every Kelvin).
        bool prior = _suppressRender;
        _suppressRender = true;
        try
        {
            if (value)
                TemperatureKelvin = WhiteBalance.TemperatureToKelvin(Temperature);
            else
                Temperature = Math.Clamp(
                    WhiteBalance.KelvinToTemperature(TemperatureKelvin), -100.0, 100.0);
        }
        finally { _suppressRender = prior; }

        // Range before value. The row coerces its Value against whatever range it
        // currently holds, so announcing a 8800 K value while the row is still
        // bounded at ±100 would show it clamped to 100 until something forced a
        // refresh. (The reverse hazard — the row clamping its stale value and
        // writing that back over the new one — was tested for and does not
        // happen: WPF leaves the source alone on a coercion-only change.)
        OnPropertyChanged(nameof(TempMinimum));
        OnPropertyChanged(nameof(TempMaximum));
        OnPropertyChanged(nameof(TempStep));
        OnPropertyChanged(nameof(TempScale));
        OnPropertyChanged(nameof(TempOrigin));
        OnPropertyChanged(nameof(TempTrackBrush));
        OnPropertyChanged(nameof(TemperatureSliderValue));

        if (!_suppressRender) ScheduleRender();
    }

    // ── Crop, straighten and orientation ────────────────────────────────────
    // One mutable settings object is the single source of truth: the on-canvas
    // box drags it directly (as the mask overlay does with a MaskSettings) and
    // the panel's rows route through the properties below. Keeping the two on
    // one object is what stops the box and the sliders disagreeing about where
    // the crop is.

    private readonly GeometrySettings _geometry = new();

    /// <summary>The live geometry — handed to the overlay, which edits it in
    /// place, and cloned into every render.</summary>
    public GeometrySettings CropGeometry => _geometry;

    /// <summary>
    /// The box as the user last placed it, before any straighten pulled it in.
    ///
    /// <para>Straightening re-derives the crop from <i>this</i> rather than from
    /// wherever the previous angle left it. Without the baseline the constraint
    /// ratchets: every degree shrinks the box a little, and rotating back does
    /// not give the framing back.</para>
    /// </summary>
    private (double x, double y, double w, double h) _cropBaseline = (0.0, 0.0, 1.0, 1.0);

    private void CaptureCropBaseline()
        => _cropBaseline = (_geometry.CropX, _geometry.CropY, _geometry.CropWidth, _geometry.CropHeight);

    private void RestoreCropBaseline()
        => (_geometry.CropX, _geometry.CropY, _geometry.CropWidth, _geometry.CropHeight) = _cropBaseline;

    public double Straighten
    {
        get => _geometry.Straighten;
        set
        {
            double angle = Math.Clamp(value, -GeometrySettings.MaxStraighten,
                                      GeometrySettings.MaxStraighten);
            if (_geometry.Straighten == angle) return;
            _geometry.Straighten = angle;

            // Re-frame from the baseline, then pull in far enough that the
            // rotation cannot open a black wedge. Without the constraint the
            // very first degree on a full-frame crop exposes all four corners.
            RestoreCropBaseline();
            if (_preview is { } p) ImageGeometry.ConstrainCrop(_geometry, p.Width, p.Height);
            OnCropEdited();
        }
    }

    /// <summary>Which guide grid the box draws — Lightroom's Crop Overlay.</summary>
    [ObservableProperty] private CropGuide _cropGuide = CropGuide.Thirds;

    public IReadOnlyList<CropGuide> CropGuides { get; } = Enum.GetValues<CropGuide>();

    public IReadOnlyList<CropAspect> CropAspects { get; } = new[]
    {
        new CropAspect("Original", CropAspect.Original),
        new CropAspect("Custom", CropAspect.Free),
        new CropAspect("1 × 1", 1.0),
        new CropAspect("3 × 2", 3.0 / 2.0),
        new CropAspect("2 × 3", 2.0 / 3.0),
        new CropAspect("4 × 3", 4.0 / 3.0),
        new CropAspect("5 × 4", 5.0 / 4.0),
        new CropAspect("4 × 5", 4.0 / 5.0),
        new CropAspect("16 × 9", 16.0 / 9.0),
        new CropAspect("9 × 16", 9.0 / 16.0),
    };

    [ObservableProperty] private CropAspect? _selectedCropAspect;

    partial void OnSelectedCropAspectChanged(CropAspect? value)
    {
        OnPropertyChanged(nameof(CropAspectRatio));
        if (value is null || value.Ratio == CropAspect.Free || _preview is not { } p) return;

        // The full-frame box of this shape is the intent; the constrained one is
        // only what fits at the current angle. Remember the former.
        ImageGeometry.ApplyAspect(_geometry, CropAspectRatio, p.Width, p.Height);
        CaptureCropBaseline();
        ImageGeometry.ConstrainCrop(_geometry, p.Width, p.Height);
        OnCropEdited();
    }

    /// <summary>The ratio the box is held to while dragging, 0 when free.
    /// "Original" resolves here rather than in the list, because it follows the
    /// photo through every 90° turn.</summary>
    public double CropAspectRatio
    {
        get
        {
            double r = SelectedCropAspect?.Ratio ?? CropAspect.Free;
            if (r != CropAspect.Original) return r;
            if (_preview is not { } p) return CropAspect.Free;
            var (ow, oh) = ImageGeometry.OrientedSize(p.Width, p.Height, _geometry.Quadrant);
            return oh > 0 ? (double)ow / oh : CropAspect.Free;
        }
    }

    /// <summary>Proportions of the current box, for the panel's readout. Pixel
    /// dimensions would be a lie here — the preview is a downsample, and the
    /// export re-decodes at full sensor resolution.</summary>
    public string CropRatioText
    {
        get
        {
            if (_preview is not { } p) return "";
            var (w, h) = ImageGeometry.OutputSize(_geometry, p.Width, p.Height);
            if (w <= 0 || h <= 0) return "";
            return w >= h ? $"{(double)w / h:0.00} : 1" : $"1 : {(double)h / w:0.00}";
        }
    }

    /// <summary>Dimensions of the uncropped preview buffer, which is the frame
    /// every geometry calculation is defined against.</summary>
    public int CropSourceWidth => _preview?.Width ?? 0;
    public int CropSourceHeight => _preview?.Height ?? 0;

    /// <summary>The crop box changed and the viewer should catch up.</summary>
    public event EventHandler? CropVisualsChanged;

    /// <summary>
    /// Called after anything mutates <see cref="_geometry"/>: repaint the box,
    /// refresh the panel's readouts, and re-render if the pixels actually moved.
    ///
    /// <para>Inside the crop tool most of them do not. The viewer is showing the
    /// flat frame with the straighten applied as a transform, so neither moving
    /// the box nor turning the photo changes the rendered bitmap by one pixel —
    /// only a quarter turn or a flip does, which is what
    /// <paramref name="orientationChanged"/> marks. Outside the tool the crop is
    /// baked in and everything needs a pass.</para>
    /// </summary>
    private void OnCropEdited(bool orientationChanged = false)
    {
        OnPropertyChanged(nameof(Straighten));
        OnPropertyChanged(nameof(CropRatioText));
        OnPropertyChanged(nameof(CropAspectRatio));
        CropVisualsChanged?.Invoke(this, EventArgs.Empty);
        if (!_suppressRender && (orientationChanged || !IsCropActive)) ScheduleRender();
    }

    /// <summary>The overlay finished a drag step. Named to match the mask
    /// overlay's hook; the debounce in <see cref="ScheduleRender"/> is what keeps
    /// the drag fluid.</summary>
    public void NotifyCropChanged()
    {
        // A box the user placed by hand is the new intent, so it becomes the
        // baseline a later straighten re-derives from.
        CaptureCropBaseline();
        // The box moved on screen already — it drew itself. Only the panel's
        // readout needs telling; the viewer is showing the whole frame while the
        // tool is open, so the render is unaffected by where the box sits.
        OnPropertyChanged(nameof(CropRatioText));
        if (!IsCropActive) ScheduleRender();
    }

    /// <summary>
    /// A rotate drag in the margin around the box. Routed through
    /// <see cref="Straighten"/> so it settles the crop the same way the slider
    /// does — and so the slider tracks the drag.
    /// </summary>
    public void NotifyStraightenDragged(double angle) => Straighten = angle;

    [RelayCommand]
    private void RotateLeft() => RotateBy(-1);

    [RelayCommand]
    private void RotateRight() => RotateBy(1);

    private void RotateBy(int steps)
    {
        if (!HasPhoto) return;
        ImageGeometry.Rotate(_geometry, steps);
        // "Original" means a different number after a quarter turn, so a locked
        // aspect has to be re-solved against the new orientation.
        if (SelectedCropAspect is { Ratio: not CropAspect.Free } && _preview is { } p)
        {
            ImageGeometry.ApplyAspect(_geometry, CropAspectRatio, p.Width, p.Height);
            CaptureCropBaseline();
            ImageGeometry.ConstrainCrop(_geometry, p.Width, p.Height);
        }
        else
        {
            // The box turned with the photo, so the baseline has to turn too.
            CaptureCropBaseline();
        }
        OnCropEdited(orientationChanged: true);
    }

    [RelayCommand]
    private void FlipHorizontal()
    {
        if (!HasPhoto) return;
        _geometry.FlipHorizontal = !_geometry.FlipHorizontal;
        OnCropEdited(orientationChanged: true);
    }

    [RelayCommand]
    private void FlipVertical()
    {
        if (!HasPhoto) return;
        _geometry.FlipVertical = !_geometry.FlipVertical;
        OnCropEdited(orientationChanged: true);
    }

    /// <summary>Back to the whole frame. The 90° orientation and the flips stay:
    /// they say how the camera was held, which resetting the framing does not
    /// change.</summary>
    [RelayCommand]
    private void ResetCrop()
    {
        if (!HasPhoto) return;
        _geometry.ResetCrop();
        CaptureCropBaseline();
        SelectedCropAspect = CropAspects[0];
        OnCropEdited();
    }

    // ── Committing the crop ─────────────────────────────────────────────────
    // The crop tool is modal in the way Lightroom's is: the framing is a
    // proposal until Enter accepts it, and walking away from the tab throws it
    // out. That is what makes it safe to drag the box about freely — nothing is
    // decided until you say so.

    /// <summary>The geometry as it stood when the tool was opened, or null when
    /// the tool is not open. Restored on abandon.</summary>
    private GeometrySettings? _geometryOnEnter;

    /// <summary>Open the crop tool (the C key).</summary>
    [RelayCommand]
    private void OpenCrop()
    {
        if (!HasPhoto) return;
        SelectedPanelTab = CropTabIndex;
    }

    /// <summary>Accept the framing and leave the tool (Enter).</summary>
    [RelayCommand]
    private void CommitCrop()
    {
        if (!IsCropActive) return;
        // Dropping the snapshot first is what turns the tab change below into an
        // accept rather than the abandon it would otherwise trigger.
        _geometryOnEnter = null;
        SelectedPanelTab = 0;
        StatusText = $"{FileName} — crop applied";
    }

    /// <summary>Throw the framing away and leave the tool (Escape, or any other
    /// way of leaving the tab).</summary>
    [RelayCommand]
    private void CancelCrop()
    {
        if (!IsCropActive) return;
        SelectedPanelTab = 0;
    }

    /// <summary>Put back the geometry the tool opened with.</summary>
    private void AbandonCrop()
    {
        if (_geometryOnEnter is not { } original) return;
        _geometryOnEnter = null;

        _geometry.CropX = original.CropX;
        _geometry.CropY = original.CropY;
        _geometry.CropWidth = original.CropWidth;
        _geometry.CropHeight = original.CropHeight;
        _geometry.Straighten = original.Straighten;
        _geometry.Quadrant = original.Quadrant;
        _geometry.FlipHorizontal = original.FlipHorizontal;
        _geometry.FlipVertical = original.FlipVertical;
        CaptureCropBaseline();
        OnCropEdited();
    }

    // ── Masks ───────────────────────────────────────────────────────────────
    // Local adjustments. The list is the model the renderer reads (via
    // CurrentSettings) and the overlay draws, wrapped one-for-one in MaskItems
    // that give the panel something bindable.

    public ObservableCollection<MaskItem> Masks { get; } = new();

    [ObservableProperty] private MaskItem? _selectedMask;

    /// <summary>Armed by the "Radial Gradient" button: the next drag on the photo
    /// places a new mask. Cleared as soon as one is created, so the tool is a
    /// one-shot rather than a mode the user has to remember to leave.</summary>
    [ObservableProperty] private bool _isCreatingMask;

    /// <summary>Paint the selected mask's falloff over the photo in red. Off by
    /// default and while editing: it is switched on automatically when a mask is
    /// placed (so its shape is visible) and dropped again the moment the user
    /// touches an adjustment slider — see <see cref="OnMaskAdjustmentChanged"/>.</summary>
    [ObservableProperty] private bool _showMaskOverlay;

    /// <summary>Which right-panel tab is showing: 0 = Edit, 1 = Crop, 2 = Masks.</summary>
    [ObservableProperty] private int _selectedPanelTab;

    private const int CropTabIndex = 1;
    private const int MasksTabIndex = 2;

    /// <summary>
    /// Whether the crop tool is up. Like <see cref="IsMaskingActive"/> this is a
    /// property of the tab rather than of the settings: a crop keeps applying
    /// wherever you are in the panel, but the box, the scrim and the guide grid
    /// only appear while you are actually framing.
    ///
    /// <para>It also changes what the viewer renders — see
    /// <see cref="PreviewGeometry"/> — because a crop box cannot be dragged
    /// outward over an image that has already been cropped.</para>
    /// </summary>
    public bool IsCropActive => HasPhoto && SelectedPanelTab == CropTabIndex;

    /// <summary>
    /// Whether the overlay draws and takes mouse input: only with a photo open,
    /// and only on the Masks tab.
    ///
    /// <para>Tying it to the tab rather than to "are there any masks" is what
    /// keeps the ellipses and their handles out of the way while you work on the
    /// global sliders — the masks still <i>render</i>, you just are not looking
    /// at their outlines. It also means the viewer's pan and zoom behave exactly
    /// as they did before masking existed whenever the Edit tab is up, since an
    /// inactive overlay passes every press straight through.</para>
    /// </summary>
    public bool IsMaskingActive => HasPhoto && SelectedPanelTab == MasksTabIndex;

    partial void OnSelectedPanelTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsMaskingActive));
        OnPropertyChanged(nameof(IsCropActive));
        // Leaving the tab abandons a pending "place a radial" click, which would
        // otherwise fire the next time the user came back and clicked the photo.
        if (value != MasksTabIndex) IsCreatingMask = false;

        if (value == CropTabIndex) _geometryOnEnter = _geometry.Clone();
        else AbandonCrop();   // a no-op once CommitCrop has dropped the snapshot

        RaiseMaskVisualsChanged();
        CropVisualsChanged?.Invoke(this, EventArgs.Empty);
        // Entering or leaving Crop switches the viewer between the whole
        // un-straightened frame and the cropped result, so it needs a fresh
        // render even though no adjustment moved.
        ScheduleRender();
    }

    /// <summary>The models the overlay draws — the same instances it edits.</summary>
    public IEnumerable<MaskSettings> MaskShapes => Masks.Select(m => m.Mask);

    public MaskSettings? SelectedMaskShape => SelectedMask?.Mask;

    /// <summary>
    /// Something the on-canvas overlay draws has changed. Needed because the
    /// overlay reads mask geometry out of the model objects directly rather than
    /// through bindings — so edits made in the panel (Feather, Invert, the
    /// enable checkbox) mutate what it draws without touching any property it is
    /// bound to, and it would otherwise keep showing the previous shape until
    /// the next unrelated repaint.
    /// </summary>
    public event EventHandler? MaskVisualsChanged;

    private void RaiseMaskVisualsChanged() => MaskVisualsChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedMaskChanged(MaskItem? value)
    {
        OnPropertyChanged(nameof(SelectedMaskShape));
        RaiseMaskVisualsChanged();
    }

    partial void OnIsCreatingMaskChanged(bool value) => OnPropertyChanged(nameof(IsMaskingActive));

    partial void OnHasPhotoChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMaskingActive));
        OnPropertyChanged(nameof(IsCropActive));
    }

    /// <summary>Which shape the armed create gesture will produce.</summary>
    private MaskKind _pendingMaskKind = MaskKind.Radial;

    [RelayCommand]
    private void BeginRadialMask()
    {
        if (!HasPhoto) return;
        _pendingMaskKind = MaskKind.Radial;
        IsCreatingMask = true;
        StatusText = "Drag on the photo to place a radial gradient.";
    }

    [RelayCommand]
    private void BeginLinearMask()
    {
        if (!HasPhoto) return;
        _pendingMaskKind = MaskKind.Linear;
        IsCreatingMask = true;
        StatusText = "Drag from the full-strength edge toward where the effect should fade out.";
    }

    [RelayCommand]
    private void BeginRectangleMask()
    {
        if (!HasPhoto) return;
        _pendingMaskKind = MaskKind.Rectangle;
        IsCreatingMask = true;
        StatusText = "Drag on the photo to draw a rectangle, from its centre outward.";
    }

    /// <summary>
    /// Called by the overlay at the start of a create drag, with the press point
    /// in normalised image coordinates. Must add the mask and select it before
    /// returning — the same gesture goes straight on to drag out its geometry.
    ///
    /// <para>The kinds read the press point differently: a radial or rectangle
    /// treats it as the centre and grows outward, a linear treats it as the
    /// full-strength end and ramps away from it.</para>
    /// </summary>
    public void CreateMaskAt(double x, double y)
    {
        var mask = _pendingMaskKind switch
        {
            MaskKind.Linear => new MaskSettings
            {
                Name = NextMaskName("Linear"),
                Kind = MaskKind.Linear,
                Linear = new LinearGradientMask
                {
                    CenterX = x,
                    CenterY = y,
                    // Effectively zero-length until the drag defines it; anything
                    // larger would snap a visible ramp across the photo before the
                    // user has said which way it should run.
                    Length = 0.002,
                },
            },
            MaskKind.Rectangle => new MaskSettings
            {
                Name = NextMaskName("Rectangle"),
                Kind = MaskKind.Rectangle,
                Rectangle = new RectangleMask
                {
                    CenterX = x,
                    CenterY = y,
                    HalfWidth = 0.01,
                    HalfHeight = 0.01,
                    Feather = 50,
                },
            },
            _ => new MaskSettings
            {
                Name = NextMaskName("Radial"),
                Kind = MaskKind.Radial,
                Radial = new RadialMask
                {
                    CenterX = x,
                    CenterY = y,
                    RadiusX = 0.01,
                    RadiusY = 0.01,
                    Feather = 50,
                },
            },
        };

        AddMask(mask);
        IsCreatingMask = false;
    }

    private void AddMask(MaskSettings mask)
    {
        var item = new MaskItem(mask);
        item.Changed += OnMaskChanged;
        item.AdjustmentChanged += OnMaskAdjustmentChanged;
        Masks.Add(item);
        SelectedMask = item;

        // Show the falloff overlay for a freshly placed mask so its shape is
        // visible while the user positions it; the first adjustment drops it.
        ShowMaskOverlay = true;

        OnPropertyChanged(nameof(MaskShapes));
        OnPropertyChanged(nameof(IsMaskingActive));
        RaiseMaskVisualsChanged();
    }

    /// <summary>The user moved an adjustment slider on a mask — they are past
    /// placing it, so drop the red overlay and leave it off. Reshaping the mask
    /// does not come through here, so dragging a handle keeps the overlay up.</summary>
    private void OnMaskAdjustmentChanged(object? sender, EventArgs e)
    {
        if (ShowMaskOverlay) ShowMaskOverlay = false;
    }

    private void OnMaskChanged(object? sender, EventArgs e)
    {
        RaiseMaskVisualsChanged();
        ScheduleRender();
    }

    /// <summary>Re-render after the overlay has dragged a mask's geometry. The
    /// debounce in <see cref="ScheduleRender"/> is what keeps a drag fluid —
    /// mouse moves arrive far faster than a pipeline pass completes.</summary>
    public void NotifyMaskGeometryChanged()
    {
        SelectedMask?.NotifyShapeChanged();
        ScheduleRender();
    }

    /// <summary>Select the mask the user clicked on the photo.</summary>
    public void SelectMaskByShape(MaskSettings shape)
        => SelectedMask = Masks.FirstOrDefault(m => ReferenceEquals(m.Mask, shape));

    [RelayCommand]
    private void DeleteMask(MaskItem? item)
    {
        item ??= SelectedMask;
        if (item is null) return;

        item.Changed -= OnMaskChanged;
        item.AdjustmentChanged -= OnMaskAdjustmentChanged;
        int index = Masks.IndexOf(item);
        Masks.Remove(item);
        SelectedMask = Masks.Count == 0
            ? null
            : Masks[Math.Clamp(index, 0, Masks.Count - 1)];

        OnPropertyChanged(nameof(MaskShapes));
        OnPropertyChanged(nameof(IsMaskingActive));
        RaiseMaskVisualsChanged();
        ScheduleRender();
    }

    [RelayCommand]
    private void DuplicateMask(MaskItem? item)
    {
        item ??= SelectedMask;
        if (item is null) return;

        var copy = item.Mask.Clone();
        copy.Name = NextMaskName(KindPrefix(copy.Kind));
        // Nudged so the duplicate is visibly a second mask rather than appearing
        // to have done nothing.
        switch (copy.Kind)
        {
            case MaskKind.Linear:
                copy.Linear.CenterX = Math.Clamp(copy.Linear.CenterX + 0.04, 0.0, 1.0);
                copy.Linear.CenterY = Math.Clamp(copy.Linear.CenterY + 0.04, 0.0, 1.0);
                break;
            case MaskKind.Rectangle:
                copy.Rectangle.CenterX = Math.Clamp(copy.Rectangle.CenterX + 0.04, 0.0, 1.0);
                copy.Rectangle.CenterY = Math.Clamp(copy.Rectangle.CenterY + 0.04, 0.0, 1.0);
                break;
            default:
                copy.Radial.CenterX = Math.Clamp(copy.Radial.CenterX + 0.04, 0.0, 1.0);
                copy.Radial.CenterY = Math.Clamp(copy.Radial.CenterY + 0.04, 0.0, 1.0);
                break;
        }

        AddMask(copy);
        ScheduleRender();
    }

    [RelayCommand]
    private void ResetMask(MaskItem? item)
    {
        item ??= SelectedMask;
        item?.ResetAdjustments();
    }

    private string NextMaskName(string prefix) => $"{prefix} {Masks.Count + 1}";

    private static string KindPrefix(MaskKind kind) => kind switch
    {
        MaskKind.Linear => "Linear",
        MaskKind.Rectangle => "Rectangle",
        _ => "Radial",
    };

    private void ClearMasks()
    {
        foreach (var item in Masks)
        {
            item.Changed -= OnMaskChanged;
            item.AdjustmentChanged -= OnMaskAdjustmentChanged;
        }
        Masks.Clear();
        SelectedMask = null;
        IsCreatingMask = false;
        OnPropertyChanged(nameof(MaskShapes));
        OnPropertyChanged(nameof(IsMaskingActive));
        RaiseMaskVisualsChanged();
    }

    private static readonly HashSet<string> AdjustmentNames = new()
    {
        nameof(Temperature), nameof(TemperatureKelvin), nameof(Tint),
        nameof(Exposure), nameof(Contrast),
        nameof(Highlights), nameof(Shadows), nameof(Whites), nameof(Blacks),
        nameof(Vibrance), nameof(Saturation),
        nameof(BandHue), nameof(BandSaturation), nameof(BandLuminance),
        nameof(Sharpening), nameof(SharpenRadius), nameof(SharpenDetail), nameof(SharpenMasking),
        nameof(LuminanceNoiseReduction), nameof(LuminanceNoiseDetail),
        nameof(LuminanceNoiseContrast), nameof(ColorNoiseReduction),
        nameof(Texture), nameof(Clarity), nameof(Dehaze),
        nameof(GrainAmount), nameof(GrainSize), nameof(GrainRoughness),
        // Section on/off switches — a flip re-renders like any slider move.
        nameof(LightEnabled), nameof(ColorEnabled), nameof(ColorMixerEnabled),
        nameof(EffectsEnabled), nameof(SharpeningEnabled), nameof(NoiseReductionEnabled)
    };

    // Any adjustment change schedules a debounced re-render.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && AdjustmentNames.Contains(e.PropertyName) && !_suppressRender)
            ScheduleRender();
    }

    private DevelopSettings CurrentSettings()
    {
        var s = new DevelopSettings
        {
            Temperature = Temperature,
            UseKelvin = UseKelvin,
            TemperatureKelvin = TemperatureKelvin,
            Tint = Tint,
            Exposure = Exposure,
            Contrast = Contrast,
            Highlights = Highlights,
            Shadows = Shadows,
            Whites = Whites,
            Blacks = Blacks,
            Vibrance = Vibrance,
            Saturation = Saturation,
            // Cloned, not handed over: the render runs on a background thread while
            // the user keeps dragging, and the bands are a mutable array.
            ColorMixer = _colorMixer.Clone(),
            Sharpening = Sharpening,
            SharpenRadius = SharpenRadius,
            SharpenDetail = SharpenDetail,
            SharpenMasking = SharpenMasking,
            LuminanceNoiseReduction = LuminanceNoiseReduction,
            LuminanceNoiseDetail = LuminanceNoiseDetail,
            LuminanceNoiseContrast = LuminanceNoiseContrast,
            ColorNoiseReduction = ColorNoiseReduction,
            Texture = Texture,
            Clarity = Clarity,
            Dehaze = Dehaze,
            GrainAmount = GrainAmount,
            GrainSize = GrainSize,
            GrainRoughness = GrainRoughness,
            // Cloned for the same reason as the mixer: the render runs off-thread
            // while the user may still be dragging a mask (or the crop box) across
            // the photo.
            Geometry = _geometry.Clone(),
            Masks = Masks.Select(m => m.Mask.Clone()).ToList(),
        };

        ApplySectionSwitches(s);
        return s;
    }

    /// <summary>
    /// Zero out any section whose switch is off, resetting exactly its fields to
    /// the values a fresh <see cref="DevelopSettings"/> carries — so a disabled
    /// section renders as untouched while the sliders keep the user's numbers for
    /// when it comes back on. Masks are left alone: a section switch is the global
    /// panel's on/off, and a mask's own Light/Colour/Effects offsets are its
    /// business (they add onto whatever the global resolved to, neutral or not),
    /// exactly as Lightroom keeps its panel switches and local adjustments apart.
    /// Colour NR stays at its default rather than 0 when Noise Reduction is off,
    /// because that default *is* the baseline chroma blur, not an edit.
    /// </summary>
    private void ApplySectionSwitches(DevelopSettings s)
    {
        if (!LightEnabled)
        {
            s.Exposure = 0; s.Contrast = 0;
            s.Highlights = 0; s.Shadows = 0; s.Whites = 0; s.Blacks = 0;
        }
        if (!ColorEnabled)
        {
            // Temperature 0 with the relative control resolves to the as-shot
            // illuminant, so white balance and the presence pair all go neutral.
            s.Temperature = 0; s.Tint = 0;
            s.UseKelvin = false; s.TemperatureKelvin = WhiteBalance.AsShotKelvin;
            s.Vibrance = 0; s.Saturation = 0;
        }
        if (!ColorMixerEnabled)
            s.ColorMixer = new ColorMixerSettings();
        if (!EffectsEnabled)
        {
            s.Texture = 0; s.Clarity = 0; s.Dehaze = 0;
            s.GrainAmount = 0;
            s.GrainSize = Effects.DefaultGrainSize;
            s.GrainRoughness = Effects.DefaultGrainRoughness;
        }
        if (!SharpeningEnabled)
        {
            s.Sharpening = 0;
            s.SharpenRadius = Detail.DefaultSharpenRadius;
            s.SharpenDetail = Detail.DefaultSharpenDetail;
            s.SharpenMasking = Detail.DefaultSharpenMasking;
        }
        if (!NoiseReductionEnabled)
        {
            s.LuminanceNoiseReduction = 0;
            s.LuminanceNoiseDetail = Detail.DefaultLuminanceDetail;
            s.LuminanceNoiseContrast = Detail.DefaultLuminanceContrast;
            s.ColorNoiseReduction = Detail.DefaultColorNoiseReduction;
        }
    }

    /// <summary>
    /// The settings the Before view renders under: geometry preserved, every tonal
    /// and colour control left at default. Backs both the neutral preview and its
    /// sharp detail tile, so the two show the same unedited frame. The geometry
    /// matches <see cref="CurrentSettings"/> whenever the tile is live (the crop
    /// tool is closed), so switching sides reuses the developed 1:1 buffer.
    /// </summary>
    private DevelopSettings NeutralSettings() => new() { Geometry = PreviewGeometry() };

    /// <summary>
    /// The geometry the <i>viewer</i> renders under, which is not the one the
    /// export uses while the crop tool is open.
    ///
    /// <para><b>The crop tool renders the frame flat and lets WPF turn it.</b>
    /// The straighten and the crop are both dropped here: the viewer shows the
    /// whole oriented photo, the box is drawn over it, and the rotation is a
    /// <c>RenderTransform</c> on the Image. That keeps the rendered bitmap
    /// <i>constant</i> for the whole of a straighten drag, so turning the photo
    /// costs a transform update instead of a full pipeline pass — the difference
    /// between instant and visibly lagging behind the cursor.</para>
    ///
    /// <para>The quarter turns and flips stay, because they are free (an index
    /// remap) and they change what "upright" means for the box.</para>
    /// </summary>
    private GeometrySettings PreviewGeometry()
    {
        var g = _geometry.Clone();
        if (!IsCropActive) return g;

        g.ResetCrop();   // clears the box and the straighten together
        return g;
    }

    /// <summary>The angle the viewer should turn the photo by — live while the
    /// crop tool is open, zero elsewhere because the renderer has already baked
    /// it in.</summary>
    public double ViewerStraighten => IsCropActive ? _geometry.Straighten : 0.0;

    /// <summary>
    /// How far the turned frame reaches, in preview-buffer pixels — the viewer
    /// shrinks by the ratio of this to the bitmap so a straightened photo still
    /// fits on screen instead of running off the edges.
    ///
    /// <para>Zero outside the crop tool, where the renderer has already baked the
    /// rotation in and the bitmap is its own extent.</para>
    /// </summary>
    public (double width, double height) ViewerExtent()
    {
        if (!IsCropActive || _preview is not { } p) return (0.0, 0.0);
        var (w, h) = ImageGeometry.OutputSize(
            ImageGeometry.FullExtent(_geometry, p.Width, p.Height), p.Width, p.Height);
        return (w, h);
    }

    private void ScheduleRender()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void RenderPreview()
    {
        var preview = _preview;
        if (preview is null) return;

        NoteRenderActivity();   // an edit is activity — hold off the idle free

        // Cancel but do not Dispose the previous CTS — see KickDetailRender: a
        // superseded render still holds this token, and disposing it races its
        // Parallel.For registration (and the next Cancel) into ObjectDisposedException.
        _renderCts?.Cancel();
        var cts = new CancellationTokenSource();
        _renderCts = cts;
        var settings = CurrentSettings();
        settings.Geometry = PreviewGeometry();
        var ct = cts.Token;
        bool mask = MaskPreviewActive;

        Task.Run(() =>
        {
            try
            {
                var bmp = mask
                    ? DevelopProcessor.RenderSharpenMask(preview, settings, ct)
                    : DevelopProcessor.Render(preview, settings, ct);

                if (!ct.IsCancellationRequested)
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        if (mask)
                        {
                            // Shown, but never recorded as the current edit — Before
                            // must keep toggling against the photo, and releasing the
                            // thumb has to have a real render to fall back on.
                            PreviewImage = bmp;
                            return;
                        }

                        _currentRender = bmp;
                        // Hold the Before view if it's currently shown; otherwise swap in the fresh edit.
                        if (!IsShowingBefore) PreviewImage = bmp;
                    });
            }
            catch (OperationCanceledException) { /* superseded by a newer edit */ }
        }, ct);

        if (mask)
        {
            // The 1:1 tile is drawn *over* the preview, so leaving it up would
            // cover the mask with the very photo the mask stands in for — the
            // overlay would swap in underneath and never be seen. Hide it for the
            // duration of the drag, and cancel any tile already in flight: one
            // kicked before the drag would otherwise land mid-preview and switch
            // the layer back on. Releasing the thumb re-renders and restores it.
            _detailCts?.Cancel();
            DetailActive = false;
            return;
        }

        // Keep the 1:1 tile in step with the edit. It renders in parallel with the
        // preview above (both viewport-bounded, both cancellable) and shows the
        // same settings at native resolution over the visible window.
        if (_detailWanted) KickDetailRender();
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open RAW photo",
            Filter = "RAW photos|*.cr2;*.cr3;*.nef;*.arw;*.raf;*.rw2;*.orf;*.dng;*.pef;*.srw;*.raw|" +
                     "All files|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        await LoadAsync(dlg.FileName);
    }

    private async Task LoadAsync(string path)
    {
        if (!RawDecoder.IsAvailable)
        {
            StatusText = "LibRaw native library not found — cannot decode RAW.";
            return;
        }

        IsBusy = true;
        FileName = Path.GetFileName(path);
        StatusText = $"Decoding {FileName}…";

        // Drop the previous photo's full-resolution buffers and 1:1 tile before
        // anything can ask for them.
        _detailCts?.Cancel();
        _detailWanted = false;
        DetailReady = false;
        DetailActive = false;
        DetailImage = null;
        _full = null;
        _previewSource = null;
        InvalidateFullDeveloped();

        // Decode half-size, then downsample to the viewport-sized preview. The
        // half-size buffer is kept (_previewSource) so a later window resize can
        // re-derive the preview at a new size without decoding again.
        int targetLong = _targetPreviewLong;
        var (source, preview) = await Task.Run(() =>
        {
            var src = RawDecoder.DecodeLinearRgb(path, halfSize: true);
            return src is null
                ? (null, (LinearRawImage?)null)
                : (src, src.Downsample(PreviewTargetWidth(src, targetLong)));
        });

        if (source is null || preview is null)
        {
            IsBusy = false;
            HasPhoto = false;
            StatusText = $"Failed to decode {FileName}. Unsupported camera or corrupt file.";
            return;
        }

        _rawPath = path;
        _previewSource = source;
        _preview = preview;

        // Reset adjustments and the Before toggle *before* flipping HasPhoto so the
        // OnIsShowingBeforeChanged hook (gated on HasPhoto) can't fire with stale buffers
        // from the previously loaded image.
        _suppressRender = true;
        ResetAdjustments();
        IsShowingBefore = false;
        _suppressRender = false;
        HasPhoto = true;

        // The crop panel measures everything against the new buffer.
        OnPropertyChanged(nameof(CropSourceWidth));
        OnPropertyChanged(nameof(CropSourceHeight));
        OnPropertyChanged(nameof(CropRatioText));
        OnPropertyChanged(nameof(CropAspectRatio));
        CropVisualsChanged?.Invoke(this, EventArgs.Empty);

        // Render the neutral baseline once and keep it as the Before snapshot.
        // This is also the initial After since all sliders sit at 0.
        var neutralBmp = await Task.Run(() => DevelopProcessor.Render(preview, new DevelopSettings()));
        _neutralPreview = neutralBmp;
        _neutralGeometry = new GeometrySettings();
        _currentRender = neutralBmp;
        PreviewImage = neutralBmp;

        IsBusy = false;
        StatusText = $"{FileName} — {preview.Width}×{preview.Height} preview · zoom to 100% for full resolution";

        // Decode the sensor-resolution buffer in the background so a later zoom to
        // 100% has real pixels ready. The preview is already interactive; this just
        // lights up the 1:1 view a moment later. Guard against the user having
        // opened another photo meanwhile.
        _ = LoadFullResolutionAsync(path);
    }

    /// <summary>
    /// Decode <paramref name="path"/> at full sensor resolution for the 1:1 view.
    /// Runs off the load path so the preview stays instant; on success it flips
    /// <see cref="DetailReady"/> and lets the viewer offer a true 100% zoom.
    /// </summary>
    private async Task LoadFullResolutionAsync(string path)
    {
        var full = await Task.Run(() => RawDecoder.DecodeLinearRgb(path, halfSize: false));

        // A newer Open (or a close) superseded this decode — discard it.
        if (full is null || !string.Equals(path, _rawPath, StringComparison.Ordinal)) return;

        _full = full;
        InvalidateFullDeveloped();
        OnPropertyChanged(nameof(DetailFrameWidth));
        OnPropertyChanged(nameof(DetailFrameHeight));
        DetailReady = true;   // the viewer watches this to enable 1:1
    }

    [RelayCommand]
    private void Reset()
    {
        if (!HasPhoto) return;
        _suppressRender = true;
        ResetAdjustments();
        _suppressRender = false;
        ScheduleRender();
    }

    private void ResetAdjustments()
    {
        // Every section back on — a switch left off is an edit like any slider.
        LightEnabled = ColorEnabled = ColorMixerEnabled =
            EffectsEnabled = SharpeningEnabled = NoiseReductionEnabled = true;

        Temperature = Tint = Exposure = Contrast = Highlights =
            Shadows = Whites = Blacks = Vibrance = Saturation = 0;
        // Back to the anchor, where white balance is a no-op. The Kelvin/relative
        // *mode* is deliberately left alone: it's a display preference, and a
        // photographer who works in Kelvin doesn't want it undone by every Reset.
        TemperatureKelvin = WhiteBalance.AsShotKelvin;
        OnPropertyChanged(nameof(TemperatureSliderValue));

        // Every band back to neutral. Like the Kelvin/relative mode above, which
        // band is *selected* is left alone — that's where the user was working.
        _colorMixer.Reset();
        OnPropertyChanged(nameof(BandHue));
        OnPropertyChanged(nameof(BandSaturation));
        OnPropertyChanged(nameof(BandLuminance));

        // Back to the panel defaults rather than to zero — Colour NR at 0 would
        // switch the chroma denoise off, which is an edit, not a reset.
        Sharpening = 0;
        SharpenRadius = Detail.DefaultSharpenRadius;
        SharpenDetail = Detail.DefaultSharpenDetail;
        SharpenMasking = Detail.DefaultSharpenMasking;
        LuminanceNoiseReduction = 0;
        LuminanceNoiseDetail = Detail.DefaultLuminanceDetail;
        LuminanceNoiseContrast = Detail.DefaultLuminanceContrast;
        ColorNoiseReduction = Detail.DefaultColorNoiseReduction;

        Texture = Clarity = Dehaze = 0;
        GrainAmount = 0;
        GrainSize = Effects.DefaultGrainSize;
        GrainRoughness = Effects.DefaultGrainRoughness;

        // The framing is an edit like any other, so it goes too — including the
        // 90° orientation and the flips, which ResetCrop alone would keep.
        _geometry.Reset();
        SelectedCropAspect = CropAspects[0];
        OnPropertyChanged(nameof(Straighten));
        OnPropertyChanged(nameof(CropRatioText));
        OnPropertyChanged(nameof(CropAspectRatio));
        CropVisualsChanged?.Invoke(this, EventArgs.Empty);

        // Masks are local edits, so Reset drops them outright. Unlike the Kelvin
        // mode or the selected colour band — display preferences that survive a
        // reset — a mask *is* an edit, and leaving one behind would mean Reset
        // did not return the photo to neutral.
        ClearMasks();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_rawPath is null) return;

        var dlg = new SaveFileDialog
        {
            Title = "Export JPEG",
            Filter = "JPEG image|*.jpg",
            FileName = Path.GetFileNameWithoutExtension(_rawPath) + "_edit.jpg"
        };
        if (dlg.ShowDialog() != true) return;

        var settings = CurrentSettings();
        var rawPath = _rawPath;
        var outPath = dlg.FileName;
        // Reuse the full-res decode if the background load has landed — it is the
        // same buffer the exporter would produce, so this skips a 2–5 s re-decode.
        // Captured here (not read inside Task.Run) so a concurrent Open, which
        // replaces _full rather than mutating it, cannot null it mid-export.
        var full = _full;

        IsBusy = true;
        StatusText = "Exporting full-resolution JPEG…";
        bool ok = await Task.Run(() =>
        {
            if (full is not null)
            {
                JpegExporter.ExportJpeg(full, settings, outPath);
                return true;
            }
            return JpegExporter.ExportJpeg(rawPath, settings, outPath);
        });
        IsBusy = false;
        StatusText = ok
            ? $"Exported → {Path.GetFileName(outPath)}"
            : "Export failed — could not decode RAW at full resolution.";
    }

    /// <summary>
    /// Opens the Lightroom-style Export panel, then renders the current photo
    /// at full sensor resolution with the chosen format / bit depth / colour
    /// space / size and writes it out.
    /// </summary>
    [RelayCommand]
    private async Task ExportAdvancedAsync()
    {
        if (_rawPath is null) return;

        var dialog = new ExportDialog(_lastExport) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is null) return;

        var export = dialog.Result;
        _lastExport = export;

        var saveDlg = new SaveFileDialog
        {
            Title = "Export image",
            Filter = export.FileFilter + "|All files|*.*",
            FileName = Path.GetFileNameWithoutExtension(_rawPath) + "_edit" + export.DefaultExtension
        };
        if (saveDlg.ShowDialog() != true) return;

        var develop = CurrentSettings();
        var rawPath = _rawPath;
        var outPath = saveDlg.FileName;
        string space = export.ColorSpace == ExportColorSpace.AdobeRgb ? "Adobe RGB" : "sRGB";
        // Reuse the resident full-res decode when available (see ExportAsync); it is
        // the identical buffer, so the exported file is unchanged.
        var full = _full;

        IsBusy = true;
        StatusText = $"Exporting full-resolution {export.Format} · {export.EffectiveBitDepth}-bit · {space}…";
        bool ok;
        string? error = null;
        try
        {
            ok = await Task.Run(() =>
            {
                if (full is not null)
                {
                    ImageExporter.Export(full, develop, export, outPath);
                    return true;
                }
                return ImageExporter.Export(rawPath, develop, export, outPath);
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }
        IsBusy = false;
        StatusText = ok
            ? $"Exported → {Path.GetFileName(outPath)}"
            : error is null
                ? "Export failed — could not decode RAW at full resolution."
                : $"Export failed — {error}";
    }

    /// <summary>
    /// Export a four-file calibration set for one slider using neutral develop
    /// values except that slider at -100, -50, +50, and +100. Math-version
    /// selectors are copied from the UI so the files test the currently chosen
    /// algorithm version.
    /// </summary>
    [RelayCommand]
    private async Task ExportCalibrationSetAsync(string? slider)
    {
        if (_rawPath is null || string.IsNullOrWhiteSpace(slider)) return;

        string rawPath = _rawPath;
        string outputDir = CalibrationOutputDirectory(rawPath);
        string originalName = Path.GetFileNameWithoutExtension(rawPath);
        string sliderName = CanonicalSliderName(slider);
        DevelopSettings baseDevelop = NeutralCalibrationSettings();
        int[] values = [-100, -50, 50, 100];

        var export = new ExportSettings
        {
            Format = ExportFormat.Tiff,
            BitDepth = 16,
            ColorSpace = ExportColorSpace.AdobeRgb,
            TiffCompression = TiffCompressionKind.None,
            ResizeLongEdge = null,
        };

        var progress = new Progress<string>(text => StatusText = text);

        IsBusy = true;
        StatusText = $"Exporting {sliderName} calibration TIFFs...";

        bool ok;
        string? error = null;
        try
        {
            ok = await Task.Run(() =>
            {
                var full = RawDecoder.DecodeLinearRgb(rawPath, halfSize: false);
                if (full is null) return false;

                Directory.CreateDirectory(outputDir);

                foreach (int value in values)
                {
                    string signed = SignedSliderValue(value);
                    string outputPath = Path.Combine(outputDir,
                        $"{originalName}_{sliderName}_{signed}_RAWR.tif");

                    ((IProgress<string>)progress).Report(
                        $"Exporting {Path.GetFileName(outputPath)}...");

                    var develop = baseDevelop.Clone();
                    ApplyCalibrationSlider(develop, sliderName, value);
                    ImageExporter.Export(full, develop, export, outputPath);
                }

                return true;
            });
        }
        catch (Exception ex)
        {
            ok = false;
            error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }

        StatusText = ok
            ? $"Exported {sliderName} calibration TIFFs -> {outputDir}"
            : error is null
                ? "Calibration export failed - could not decode RAW."
                : $"Calibration export failed - {error}";
    }

    private DevelopSettings NeutralCalibrationSettings() => new();

    private static void ApplyCalibrationSlider(DevelopSettings s, string sliderName, int value)
    {
        switch (sliderName)
        {
            case "Contrast": s.Contrast = value; break;
            case "Highlights": s.Highlights = value; break;
            case "Shadows": s.Shadows = value; break;
            case "Whites": s.Whites = value; break;
            case "Blacks": s.Blacks = value; break;
            case "Vibrance": s.Vibrance = value; break;
            case "Saturation": s.Saturation = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(sliderName), sliderName, "Unsupported calibration slider.");
        }
    }

    private static string CalibrationOutputDirectory(string rawPath)
    {
        string? dir = Path.GetDirectoryName(rawPath);
        if (string.IsNullOrWhiteSpace(dir)) return Environment.CurrentDirectory;

        var info = new DirectoryInfo(dir);
        if (string.Equals(info.Name, "RAW Images", StringComparison.OrdinalIgnoreCase)
            && info.Parent is not null)
        {
            return info.Parent.FullName;
        }

        return dir;
    }

    private static string CanonicalSliderName(string slider) => slider.Trim() switch
    {
        "Contrast" => "Contrast",
        "Highlights" => "Highlights",
        "Shadows" => "Shadows",
        "Whites" => "Whites",
        "Blacks" => "Blacks",
        "Vibrance" => "Vibrance",
        "Saturation" => "Saturation",
        _ => throw new ArgumentOutOfRangeException(nameof(slider), slider, "Unsupported calibration slider."),
    };

    private static string SignedSliderValue(int value) => value > 0 ? $"+{value}" : value.ToString();
}
