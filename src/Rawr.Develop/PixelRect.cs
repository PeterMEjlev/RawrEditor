namespace Rawr.Develop;

/// <summary>
/// A pixel-space rectangle, half-open in both axes.
///
/// <para>Lives at namespace scope rather than inside a mask type because every
/// mask shape answers the same two questions — "which rectangle can you affect"
/// and "what is your weight inside it" — and the renderer composites them
/// through that pair without knowing which shape it is holding.</para>
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>The whole of an image of the given size.</summary>
    public static PixelRect Full(int imageWidth, int imageHeight)
        => new(0, 0, imageWidth, imageHeight);

    /// <summary>
    /// The bounding rectangle of a set of points, rounded outward by a pixel and
    /// clamped to the image. The outward rounding keeps the boundary row and
    /// column — where a mask's weight is small but not yet zero — inside.
    /// </summary>
    public static PixelRect FromPoints(ReadOnlySpan<(double x, double y)> points,
                                       int imageWidth, int imageHeight)
    {
        if (points.Length == 0) return default;

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        int x0 = Math.Clamp((int)Math.Floor(minX) - 1, 0, imageWidth);
        int y0 = Math.Clamp((int)Math.Floor(minY) - 1, 0, imageHeight);
        int x1 = Math.Clamp((int)Math.Ceiling(maxX) + 1, 0, imageWidth);
        int y1 = Math.Clamp((int)Math.Ceiling(maxY) + 1, 0, imageHeight);

        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}
