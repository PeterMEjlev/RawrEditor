using CommunityToolkit.Mvvm.ComponentModel;
using Rawr.Develop;

namespace Rawr.Editor.App.ViewModels;

/// <summary>
/// Backs <c>ExportDialog</c> — the Lightroom-style "Export" panel. ComboBoxes
/// bind to the integer index properties (option text lives in the XAML), and
/// <see cref="BuildSettings"/> folds the choices into an <see cref="ExportSettings"/>.
/// Enable/disable flags model Lightroom's behaviour: JPEG is 8-bit only,
/// compression is TIFF-only, quality is JPEG-only, the pixel box is live only
/// when a long-edge limit is chosen.
/// </summary>
public sealed partial class ExportDialogViewModel : ObservableObject
{
    public ExportDialogViewModel(ExportSettings seed)
    {
        _formatIndex = seed.Format switch
        {
            ExportFormat.Jpeg => 0, ExportFormat.Tiff => 1, ExportFormat.Png => 2, _ => 1
        };
        _bitDepthIndex = seed.BitDepth == 16 ? 1 : 0;
        _colorSpaceIndex = seed.ColorSpace == ExportColorSpace.AdobeRgb ? 1 : 0;
        _compressionIndex = seed.TiffCompression switch
        {
            TiffCompressionKind.Lzw => 1, TiffCompressionKind.Zip => 2, _ => 0
        };
        _dimensionsIndex = seed.ResizeLongEdge is null ? 0 : 1;
        _longEdgePixels = seed.ResizeLongEdge ?? 2048;
        _jpegQuality = seed.JpegQuality;
    }

    // ── Bound option indices ──
    [ObservableProperty] private int _formatIndex;       // 0 JPEG, 1 TIFF, 2 PNG
    [ObservableProperty] private int _bitDepthIndex;     // 0 = 8, 1 = 16
    [ObservableProperty] private int _colorSpaceIndex;   // 0 sRGB, 1 Adobe RGB
    [ObservableProperty] private int _compressionIndex;  // 0 None, 1 LZW, 2 ZIP
    [ObservableProperty] private int _dimensionsIndex;   // 0 Full Size, 1 Long Edge
    [ObservableProperty] private int _longEdgePixels;
    [ObservableProperty] private int _jpegQuality;

    private bool IsJpeg => FormatIndex == 0;
    private bool IsTiff => FormatIndex == 1;

    // ── Derived enable/visibility flags (re-raised when Format/Dimensions change) ──
    public bool BitDepthEnabled  => !IsJpeg;            // JPEG is an 8-bit container
    public bool CompressionEnabled => IsTiff;           // only TIFF carries a compression choice
    public bool QualityVisible   => IsJpeg;             // JPEG-only
    public bool LongEdgeEnabled  => DimensionsIndex == 1;

    partial void OnFormatIndexChanged(int value)
    {
        // JPEG is an 8-bit container — reflect that in the (disabled) box,
        // matching Lightroom rather than leaving a stale "16 bits" showing.
        if (IsJpeg) BitDepthIndex = 0;
        OnPropertyChanged(nameof(BitDepthEnabled));
        OnPropertyChanged(nameof(CompressionEnabled));
        OnPropertyChanged(nameof(QualityVisible));
    }

    partial void OnDimensionsIndexChanged(int value)
        => OnPropertyChanged(nameof(LongEdgeEnabled));

    /// <summary>Fold the UI choices into an immutable export descriptor.</summary>
    public ExportSettings BuildSettings() => new()
    {
        Format = FormatIndex switch { 0 => ExportFormat.Jpeg, 2 => ExportFormat.Png, _ => ExportFormat.Tiff },
        BitDepth = BitDepthIndex == 1 ? 16 : 8,
        ColorSpace = ColorSpaceIndex == 1 ? ExportColorSpace.AdobeRgb : ExportColorSpace.Srgb,
        TiffCompression = CompressionIndex switch
        {
            1 => TiffCompressionKind.Lzw, 2 => TiffCompressionKind.Zip, _ => TiffCompressionKind.None
        },
        JpegQuality = Math.Clamp(JpegQuality, 1, 100),
        ResizeLongEdge = DimensionsIndex == 1 ? Math.Max(16, LongEdgePixels) : null,
    };
}
