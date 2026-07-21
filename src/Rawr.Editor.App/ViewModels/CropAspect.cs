namespace Rawr.Editor.App.ViewModels;

/// <summary>
/// One entry in the crop panel's Aspect list.
///
/// <para><see cref="Ratio"/> is width ÷ height, with two sentinels: <see cref="Free"/>
/// leaves the box unconstrained, and <see cref="Original"/> defers to whatever
/// the photo already is — which cannot be a literal number here because it
/// depends on the sensor and flips with every 90° turn.</para>
/// </summary>
public sealed class CropAspect
{
    /// <summary>Dragging changes both dimensions independently.</summary>
    public const double Free = 0.0;

    /// <summary>Resolved against the photo's own proportions at use time.</summary>
    public const double Original = -1.0;

    public CropAspect(string name, double ratio)
    {
        Name = name;
        Ratio = ratio;
    }

    public string Name { get; }
    public double Ratio { get; }
}
