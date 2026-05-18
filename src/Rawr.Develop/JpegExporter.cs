using System.IO;
using System.Windows.Media.Imaging;
using Rawr.Raw;

namespace Rawr.Develop;

/// <summary>
/// Full-quality export. The preview path decodes half-size and downsamples for
/// speed; export instead decodes the RAW at full sensor resolution and runs the
/// identical <see cref="DevelopProcessor"/> pipeline, so the saved JPEG is the
/// preview at every pixel the sensor recorded.
/// </summary>
public static class JpegExporter
{
    /// <summary>
    /// Decode <paramref name="rawPath"/> at full resolution, apply
    /// <paramref name="settings"/>, and write a JPEG to <paramref name="outputPath"/>.
    /// Returns false if the RAW could not be decoded.
    /// </summary>
    public static bool ExportJpeg(string rawPath, DevelopSettings settings, string outputPath,
                                  int quality = 92, CancellationToken ct = default)
    {
        var full = RawDecoder.DecodeLinearRgb(rawPath, halfSize: false);
        if (full is null) return false;

        var rendered = DevelopProcessor.Render(full, settings, ct);

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(rendered));

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        encoder.Save(fs);
        return true;
    }
}
