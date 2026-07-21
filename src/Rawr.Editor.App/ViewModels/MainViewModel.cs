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
/// and box-averaged down to <see cref="PreviewWidth"/>; every slider move then
/// re-renders only that small buffer, debounced and cancellable, so editing
/// stays smooth on big sensors. Export re-decodes at full resolution.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    // Preview working resolution. Sharp fit-to-screen on a 4K panel, yet small
    // enough that a full pipeline pass is ~70 ms so dragging a slider stays
    // fluid (measured: 45 MP CR3 → ~130 ms at 2560 px, ~70 ms here). Export is
    // unaffected — it always re-decodes at full sensor resolution.
    private const int PreviewWidth = 1920;

    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _renderCts;
    private LinearRawImage? _preview;   // downsampled, drives realtime editing
    private string? _rawPath;
    private bool _suppressRender;
    private BitmapSource? _neutralPreview;   // cached unedited render; powers the Before view
    private GeometrySettings _neutralGeometry = new();   // the crop _neutralPreview was rendered under
    private BitmapSource? _currentRender;    // latest edited render; what After shows
    private ExportSettings _lastExport = new();   // remembered between Export… dialog opens

    public MainViewModel()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };
        SelectedCropAspect = CropAspects[0];
        StatusText = RawDecoder.IsAvailable
            ? "Open a RAW photo to begin."
            : "LibRaw native library not found — RAW decoding unavailable.";
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

    /// <summary>Paint the selected mask's falloff over the photo in red.</summary>
    [ObservableProperty] private bool _showMaskOverlay = true;

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

    /// <summary>
    /// Called by the overlay at the start of a create drag, with the press point
    /// in normalised image coordinates. Must add the mask and select it before
    /// returning — the same gesture goes straight on to drag out its geometry.
    ///
    /// <para>The two kinds read the press point differently: a radial treats it
    /// as the centre and grows outward, a linear treats it as the full-strength
    /// end and ramps away from it.</para>
    /// </summary>
    public void CreateMaskAt(double x, double y)
    {
        var mask = _pendingMaskKind == MaskKind.Linear
            ? new MaskSettings
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
            }
            : new MaskSettings
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
            };

        AddMask(mask);
        IsCreatingMask = false;
    }

    private void AddMask(MaskSettings mask)
    {
        var item = new MaskItem(mask);
        item.Changed += OnMaskChanged;
        Masks.Add(item);
        SelectedMask = item;
        OnPropertyChanged(nameof(MaskShapes));
        OnPropertyChanged(nameof(IsMaskingActive));
        RaiseMaskVisualsChanged();
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
        copy.Name = NextMaskName(copy.IsLinear ? "Linear" : "Radial");
        // Nudged so the duplicate is visibly a second mask rather than appearing
        // to have done nothing.
        if (copy.IsLinear)
        {
            copy.Linear.CenterX = Math.Clamp(copy.Linear.CenterX + 0.04, 0.0, 1.0);
            copy.Linear.CenterY = Math.Clamp(copy.Linear.CenterY + 0.04, 0.0, 1.0);
        }
        else
        {
            copy.Radial.CenterX = Math.Clamp(copy.Radial.CenterX + 0.04, 0.0, 1.0);
            copy.Radial.CenterY = Math.Clamp(copy.Radial.CenterY + 0.04, 0.0, 1.0);
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

    private void ClearMasks()
    {
        foreach (var item in Masks) item.Changed -= OnMaskChanged;
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
        nameof(GrainAmount), nameof(GrainSize), nameof(GrainRoughness)
    };

    // Any adjustment change schedules a debounced re-render.
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && AdjustmentNames.Contains(e.PropertyName) && !_suppressRender)
            ScheduleRender();
    }

    private DevelopSettings CurrentSettings() => new()
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

        _renderCts?.Cancel();
        _renderCts?.Dispose();
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

        var preview = await Task.Run(() =>
        {
            var full = RawDecoder.DecodeLinearRgb(path, halfSize: true);
            return full?.Downsample(PreviewWidth);
        });

        if (preview is null)
        {
            IsBusy = false;
            HasPhoto = false;
            StatusText = $"Failed to decode {FileName}. Unsupported camera or corrupt file.";
            return;
        }

        _rawPath = path;
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
        StatusText = $"{FileName} — {preview.Width}×{preview.Height} preview · edits apply live";
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

        IsBusy = true;
        StatusText = "Exporting full-resolution JPEG…";
        bool ok = await Task.Run(() => JpegExporter.ExportJpeg(rawPath, settings, outPath));
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

        IsBusy = true;
        StatusText = $"Exporting full-resolution {export.Format} · {export.EffectiveBitDepth}-bit · {space}…";
        bool ok;
        string? error = null;
        try
        {
            ok = await Task.Run(() => ImageExporter.Export(rawPath, develop, export, outPath));
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
