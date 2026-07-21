namespace Rawr.Develop;

/// <summary>
/// Where the photograph is, as opposed to what it looks like: the crop
/// rectangle, the straighten angle, and the 90° orientation.
///
/// <para><b>Every field is expressed in the frame the user is looking at</b>,
/// not in sensor coordinates. The crop rectangle is normalised against the
/// oriented, flipped image, and <see cref="Straighten"/> turns the content
/// under that rectangle. So a photo held sideways by <see cref="Quadrant"/> has
/// a crop whose <see cref="CropWidth"/> runs along the sensor's <i>height</i> —
/// which is what lets the panel and the on-canvas box talk about the same
/// rectangle the photographer sees. <see cref="Geometry"/> owns the unpicking
/// of that into sensor reads.</para>
///
/// <para>Neutral is the whole frame, unrotated: a fresh instance renders the
/// sensor buffer untouched, and <see cref="Geometry.Apply"/> hands the input
/// straight back rather than copying it.</para>
/// </summary>
public sealed class GeometrySettings
{
    /// <summary>The Straighten slider's travel, in degrees either way. Past this
    /// a "straighten" is really a rotation, which is what Quadrant is for.</summary>
    public const double MaxStraighten = 45.0;

    // Crop rectangle, normalised to the oriented image: (0,0,1,1) is all of it.
    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; } = 1.0;
    public double CropHeight { get; set; } = 1.0;

    /// <summary>Degrees of straighten, positive turning the photo clockwise —
    /// the correction for a horizon that runs downhill to the right.</summary>
    public double Straighten { get; set; }

    /// <summary>Quarter turns clockwise. Unlike <see cref="Straighten"/> this
    /// costs no resampling: it is an index remap.</summary>
    public int Quadrant { get; set; }

    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }

    /// <summary><see cref="Quadrant"/> folded into 0…3, which is what the
    /// mapping in <see cref="Geometry"/> switches on.</summary>
    public int NormalisedQuadrant => ((Quadrant % 4) + 4) % 4;

    /// <summary>True when the whole sensor frame passes through untouched.</summary>
    public bool IsNeutral =>
        CropX == 0.0 && CropY == 0.0 && CropWidth == 1.0 && CropHeight == 1.0 &&
        Straighten == 0.0 && NormalisedQuadrant == 0 &&
        !FlipHorizontal && !FlipVertical;

    /// <summary>True when nothing but the crop rectangle is in play, so the
    /// sampler can take the lossless path.</summary>
    public bool IsAxisAligned => Straighten == 0.0;

    public GeometrySettings Clone() => (GeometrySettings)MemberwiseClone();

    /// <summary>Whether two settings would produce the same pixels. Used to
    /// decide when a cached render has been invalidated by a crop.</summary>
    public bool Matches(GeometrySettings other) =>
        CropX == other.CropX && CropY == other.CropY &&
        CropWidth == other.CropWidth && CropHeight == other.CropHeight &&
        Straighten == other.Straighten &&
        NormalisedQuadrant == other.NormalisedQuadrant &&
        FlipHorizontal == other.FlipHorizontal &&
        FlipVertical == other.FlipVertical;

    /// <summary>Back to the whole frame, keeping the 90° orientation and flips —
    /// those describe how the camera was held, not how the photo was framed.</summary>
    public void ResetCrop()
    {
        CropX = CropY = 0.0;
        CropWidth = CropHeight = 1.0;
        Straighten = 0.0;
    }

    public void Reset()
    {
        ResetCrop();
        Quadrant = 0;
        FlipHorizontal = FlipVertical = false;
    }
}
