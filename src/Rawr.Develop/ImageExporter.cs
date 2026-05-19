using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.Develop;

/// <summary>
/// Full-quality export. The preview path decodes half-size and downsamples for
/// speed; export instead decodes the RAW at full sensor resolution and runs the
/// identical <see cref="DevelopProcessor"/> pipeline, then writes the chosen
/// container (JPEG / TIFF / PNG) at the chosen bit depth, colour space and size.
/// </summary>
public static class ImageExporter
{
    /// <summary>
    /// Decode <paramref name="rawPath"/> at full resolution, apply the develop
    /// <paramref name="develop"/> look, then encode per <paramref name="export"/>
    /// to <paramref name="outputPath"/>. Returns false if the RAW could not be
    /// decoded at full resolution.
    /// </summary>
    public static bool Export(string rawPath, DevelopSettings develop, ExportSettings export,
                              string outputPath, CancellationToken ct = default)
    {
        var full = RawDecoder.DecodeLinearRgb(rawPath, halfSize: false);
        if (full is null) return false;

        Export(full, develop, export, outputPath, ct);
        return true;
    }

    /// <summary>
    /// Apply <paramref name="develop"/> to an already-decoded full-resolution
    /// RAW and encode it according to <paramref name="export"/>. Used by batch
    /// calibration export so the RAW is decoded once and rendered several ways.
    /// </summary>
    public static void Export(LinearRawImage full, DevelopSettings develop, ExportSettings export,
                              string outputPath, CancellationToken ct = default)
    {
        bool sixteenBit = export.EffectiveBitDepth == 16;
        BitmapSource img = DevelopProcessor.RenderExport(full, develop, sixteenBit, export.ColorSpace, ct);

        img = Resize(img, export.ResizeLongEdge);

        ct.ThrowIfCancellationRequested();

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Adobe RGB pixels carry no meaning without the profile, so embed one.
        // sRGB is left untagged — the universal default every viewer assumes.
        string? tempIcc = null;
        try
        {
            ReadOnlyCollection<ColorContext>? colorContexts = null;
            if (export.ColorSpace == ExportColorSpace.AdobeRgb)
            {
                tempIcc = Path.Combine(Path.GetTempPath(),
                    "rawr_adobergb_" + Guid.NewGuid().ToString("N") + ".icc");
                File.WriteAllBytes(tempIcc, IccProfiles.AdobeRgb1998);
                var cc = new ColorContext(new Uri(tempIcc));
                colorContexts = new ReadOnlyCollection<ColorContext>(new[] { cc });
            }

            BitmapEncoder encoder = CreateEncoder(export);
            var frame = BitmapFrame.Create(img, thumbnail: null, metadata: null, colorContexts: colorContexts);
            encoder.Frames.Add(frame);

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            encoder.Save(fs);
        }
        finally
        {
            if (tempIcc is not null && File.Exists(tempIcc))
            {
                try { File.Delete(tempIcc); } catch { /* temp dir cleanup, non-fatal */ }
            }
        }
    }

    private static BitmapEncoder CreateEncoder(ExportSettings e) => e.Format switch
    {
        ExportFormat.Jpeg => new JpegBitmapEncoder { QualityLevel = Math.Clamp(e.JpegQuality, 1, 100) },
        ExportFormat.Png  => new PngBitmapEncoder(),
        ExportFormat.Tiff => new TiffBitmapEncoder
        {
            Compression = e.TiffCompression switch
            {
                TiffCompressionKind.Lzw => TiffCompressOption.Lzw,
                TiffCompressionKind.Zip => TiffCompressOption.Zip,
                _ => TiffCompressOption.None,
            }
        },
        _ => new JpegBitmapEncoder(),
    };

    /// <summary>Downscale so the long edge is <paramref name="longEdge"/> px.
    /// Never upscales; returns the source unchanged when no resize is needed.</summary>
    private static BitmapSource Resize(BitmapSource src, int? longEdge)
    {
        if (longEdge is not int target || target <= 0) return src;
        int srcLong = Math.Max(src.PixelWidth, src.PixelHeight);
        if (target >= srcLong) return src; // only ever shrink

        double scale = (double)target / srcLong;
        var scaled = new TransformedBitmap(src, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }
}
