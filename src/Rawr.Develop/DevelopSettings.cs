namespace Rawr.Develop;

/// <summary>
/// The full set of develop adjustments for one photo. All sliders are neutral at
/// their default values, so a fresh instance renders the camera-matched baseline
/// (identical to RAWR's preview look) with nothing applied.
///
/// Ranges mirror Lightroom's Basic panel so the numbers feel familiar:
///   • Temperature / Tint            −100 … +100  (relative to the camera WB)
///   • Exposure                       −5 … +5     stops
///   • Contrast / Highlights /
///     Shadows / Whites / Blacks     −100 … +100
///   • Vibrance / Saturation         −100 … +100
///
/// It is a mutable class (not a record) so the WPF view-model can bind two-way
/// to each property; <see cref="Clone"/> gives the exporter an immutable-enough
/// snapshot to render off-thread.
/// </summary>
public sealed class DevelopSettings
{
    public double Temperature { get; set; }   // −100 cool ……… +100 warm
    public double Tint { get; set; }           // −100 green ……… +100 magenta
    public double Exposure { get; set; }       // stops, 2^Exposure linear gain
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }
    public double Vibrance { get; set; }
    public double Saturation { get; set; }

    public DevelopSettings Clone() => (DevelopSettings)MemberwiseClone();

    /// <summary>True when every slider is at its neutral default.</summary>
    public bool IsNeutral =>
        Temperature == 0 && Tint == 0 && Exposure == 0 && Contrast == 0 &&
        Highlights == 0 && Shadows == 0 && Whites == 0 && Blacks == 0 &&
        Vibrance == 0 && Saturation == 0;

    public void Reset()
    {
        Temperature = Tint = Exposure = Contrast = Highlights =
            Shadows = Whites = Blacks = Vibrance = Saturation = 0;
    }
}
