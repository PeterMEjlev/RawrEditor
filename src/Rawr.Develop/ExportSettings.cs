namespace Rawr.Develop;

/// <summary>Container/encoder the file is written as.</summary>
public enum ExportFormat { Jpeg, Tiff, Png }

/// <summary>Output colour space. sRGB is left untagged (universally assumed);
/// AdobeRGB re-encodes the pixels and embeds an Adobe RGB (1998) ICC profile.</summary>
public enum ExportColorSpace { Srgb, AdobeRgb }

/// <summary>TIFF compression. JPEG/PNG ignore this (JPEG = quality, PNG = always deflate).</summary>
public enum TiffCompressionKind { None, Lzw, Zip }

/// <summary>
/// The "Export" panel settings — the delivery-format counterpart to
/// <see cref="DevelopSettings"/> (which is the look). Defaults mirror the
/// Lightroom dialog the editor is modelled on: 16-bit uncompressed TIFF,
/// full size, Adobe RGB.
///
/// Not every Lightroom row is here — only the ones wired to behave honestly:
/// HDR / transparency / watermark / output-sharpening are deliberately omitted
/// rather than shown as no-ops.
/// </summary>
public sealed class ExportSettings
{
    public ExportFormat Format { get; set; } = ExportFormat.Tiff;

    /// <summary>8 or 16. Forced to 8 for <see cref="ExportFormat.Jpeg"/>
    /// (JPEG is an 8-bit container) — see <see cref="EffectiveBitDepth"/>.</summary>
    public int BitDepth { get; set; } = 16;

    public ExportColorSpace ColorSpace { get; set; } = ExportColorSpace.AdobeRgb;

    public TiffCompressionKind TiffCompression { get; set; } = TiffCompressionKind.None;

    /// <summary>1..100, JPEG only.</summary>
    public int JpegQuality { get; set; } = 92;

    /// <summary>null ⇒ full sensor resolution; otherwise the long edge is
    /// scaled to this many pixels (only ever downscales, never enlarges).</summary>
    public int? ResizeLongEdge { get; set; }

    /// <summary>JPEG can only carry 8 bits regardless of the requested depth.</summary>
    public int EffectiveBitDepth => Format == ExportFormat.Jpeg ? 8 : BitDepth;

    public string DefaultExtension => Format switch
    {
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Tiff => ".tif",
        ExportFormat.Png  => ".png",
        _ => ".jpg",
    };

    public string FileFilter => Format switch
    {
        ExportFormat.Jpeg => "JPEG image|*.jpg",
        ExportFormat.Tiff => "TIFF image|*.tif",
        ExportFormat.Png  => "PNG image|*.png",
        _ => "Image|*.*",
    };

    public ExportSettings Clone() => (ExportSettings)MemberwiseClone();
}
