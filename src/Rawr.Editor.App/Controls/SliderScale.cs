namespace Rawr.Editor.App.Controls;

/// <summary>How a slider's value is distributed along its track.</summary>
public enum SliderScale
{
    /// <summary>Equal distances are equal value steps. Right for every bipolar
    /// adjustment (−100…+100) and for Exposure's stops.</summary>
    Linear,

    /// <summary>Equal distances are equal <i>ratios</i>. Right for a quantity
    /// spanning more than an order of magnitude, where the useful values bunch at
    /// one end — see <see cref="SliderScales"/> for why Kelvin is exactly that.</summary>
    Logarithmic,
}

/// <summary>
/// Maps between a slider's value and its position along the track, 0…1.
///
/// <para><b>Why this exists.</b> A WPF Slider places the thumb linearly, which is
/// wrong for colour temperature: the 2000–50000 K range is dominated by values
/// nobody photographs under. Linearly, everything from candlelight to overcast
/// daylight — 2000 to 10000 K — is squeezed into the leftmost 17% of the track,
/// so 6500 K sits at 9% with the thumb jammed against the end, and four fifths of
/// the travel is spent above 10000 K. On a log scale that same working range gets
/// half the track and 6500 K lands at 37%, which is where Lightroom puts it.</para>
///
/// <para>Sliders therefore run in position space and convert at the edges. The row
/// exposes real units (the readout says 6500, not 0.366) and the gradient track is
/// sampled through the same mapping, so colour, thumb and number all agree.</para>
/// </summary>
public static class SliderScales
{
    /// <summary>Value → position in 0…1. Inverse of <see cref="ToValue"/>.</summary>
    public static double ToPosition(SliderScale scale, double value, double min, double max)
    {
        if (max <= min) return 0.0;
        double t = scale == SliderScale.Logarithmic && CanLog(min, max)
            ? (Math.Log(Math.Max(value, min)) - Math.Log(min)) / (Math.Log(max) - Math.Log(min))
            : (value - min) / (max - min);
        return double.IsFinite(t) ? Math.Clamp(t, 0.0, 1.0) : 0.0;
    }

    /// <summary>Position in 0…1 → value. Inverse of <see cref="ToPosition"/>.</summary>
    public static double ToValue(SliderScale scale, double position, double min, double max)
    {
        if (max <= min) return min;
        double t = Math.Clamp(position, 0.0, 1.0);
        double v = scale == SliderScale.Logarithmic && CanLog(min, max)
            ? Math.Exp(Math.Log(min) + t * (Math.Log(max) - Math.Log(min)))
            : min + t * (max - min);
        return double.IsFinite(v) ? Math.Clamp(v, min, max) : min;
    }

    /// <summary>A log scale needs a strictly positive range — ln(0) is −∞ and a
    /// bipolar slider has no meaningful ratio between its ends. Anything else
    /// silently falls back to linear rather than producing NaN positions.</summary>
    private static bool CanLog(double min, double max) => min > 0.0 && max > 0.0;
}
