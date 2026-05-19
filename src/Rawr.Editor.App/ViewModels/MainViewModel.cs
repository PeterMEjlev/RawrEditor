using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Rawr.Develop;
using Rawr.Editor.App;
using Rawr.Raw;

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
    private BitmapSource? _currentRender;    // latest edited render; what After shows
    private ExportSettings _lastExport = new();   // remembered between Export… dialog opens

    public MainViewModel()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); RenderPreview(); };
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
        PreviewImage = value ? _neutralPreview : _currentRender;
        StatusText = value
            ? "Showing BEFORE — toggle off to return to your edits."
            : $"{FileName} — edits apply live";
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

    // ── Math-version selectors (default v1 = original RAWR math) ──
    // Wired to the inline "v1/v2/…" button on each AdjustmentRow. Changing one
    // re-renders the preview just like a slider move.
    [ObservableProperty] private int _contrastVersion = 1;
    [ObservableProperty] private int _highlightsVersion = BasicTone.HighlightsVersionCount;
    [ObservableProperty] private int _shadowsVersion = 1;
    [ObservableProperty] private int _whitesVersion = 1;
    [ObservableProperty] private int _blacksVersion = 1;

    // Exposed for the XAML so the cycle button knows how many versions exist.
    // When a new vN is added in BasicTone, bumping the matching VersionCount
    // there is enough — this property just surfaces the value.
    public int ContrastVersionCount   => BasicTone.ContrastVersionCount;
    public int HighlightsVersionCount => BasicTone.HighlightsVersionCount;
    public int ShadowsVersionCount    => BasicTone.ShadowsVersionCount;
    public int WhitesVersionCount     => BasicTone.WhitesVersionCount;
    public int BlacksVersionCount     => BasicTone.BlacksVersionCount;

    private static readonly HashSet<string> AdjustmentNames = new()
    {
        nameof(Temperature), nameof(Tint), nameof(Exposure), nameof(Contrast),
        nameof(Highlights), nameof(Shadows), nameof(Whites), nameof(Blacks),
        nameof(Vibrance), nameof(Saturation),
        nameof(ContrastVersion), nameof(HighlightsVersion), nameof(ShadowsVersion),
        nameof(WhitesVersion), nameof(BlacksVersion)
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
        Tint = Tint,
        Exposure = Exposure,
        Contrast = Contrast,
        Highlights = Highlights,
        Shadows = Shadows,
        Whites = Whites,
        Blacks = Blacks,
        Vibrance = Vibrance,
        Saturation = Saturation,
        ContrastVersion = ContrastVersion,
        HighlightsVersion = HighlightsVersion,
        ShadowsVersion = ShadowsVersion,
        WhitesVersion = WhitesVersion,
        BlacksVersion = BlacksVersion,
    };

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
        var ct = cts.Token;

        Task.Run(() =>
        {
            try
            {
                var bmp = DevelopProcessor.Render(preview, settings, ct);
                if (!ct.IsCancellationRequested)
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (ct.IsCancellationRequested) return;
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

        // Render the neutral baseline once and keep it as the Before snapshot.
        // This is also the initial After since all sliders sit at 0.
        var neutralBmp = await Task.Run(() => DevelopProcessor.Render(preview, new DevelopSettings()));
        _neutralPreview = neutralBmp;
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

    private DevelopSettings NeutralCalibrationSettings() => new()
    {
        ContrastVersion = ContrastVersion,
        HighlightsVersion = HighlightsVersion,
        ShadowsVersion = ShadowsVersion,
        WhitesVersion = WhitesVersion,
        BlacksVersion = BlacksVersion,
    };

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
