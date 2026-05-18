using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Rawr.Develop;
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

    private static readonly HashSet<string> AdjustmentNames = new()
    {
        nameof(Temperature), nameof(Tint), nameof(Exposure), nameof(Contrast),
        nameof(Highlights), nameof(Shadows), nameof(Whites), nameof(Blacks),
        nameof(Vibrance), nameof(Saturation)
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
                        if (!ct.IsCancellationRequested) PreviewImage = bmp;
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
        HasPhoto = true;

        _suppressRender = true;
        ResetAdjustments();
        _suppressRender = false;

        IsBusy = false;
        StatusText = $"{FileName} — {preview.Width}×{preview.Height} preview · edits apply live";
        RenderPreview();
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
}
