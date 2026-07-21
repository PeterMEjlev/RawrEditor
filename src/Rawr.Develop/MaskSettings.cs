namespace Rawr.Develop;

/// <summary>
/// The per-mask adjustment deltas — Lightroom's local Light and Color sliders.
///
/// <para><b>These are offsets, not absolute settings.</b> A mask carrying
/// Exposure +1 means "one stop brighter <i>than whatever the global panel is
/// doing</i>", which is what makes a mask survive a later change to the global
/// sliders instead of pinning its region to a fixed rendering. It is also how
/// Lightroom behaves, and the reason every field here is neutral at zero while
/// the global <see cref="DevelopSettings"/> has non-zero neutral points in
/// Detail.</para>
///
/// <para>Detail (sharpening, noise reduction) and the Colour Mixer are
/// deliberately absent. Both are available globally; locally they would each
/// need the mask to carry its own spatial pass, and the compositing model here
/// — re-render the region and crossfade — gives no sensible meaning to
/// blending two different sharpening radii across a feather.</para>
/// </summary>
public sealed class MaskAdjustments
{
    public double Exposure { get; set; }       // stops, added to the global exposure
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }

    /// <summary>Temperature shift in the relative (mired) units of the global
    /// Temp slider, applied whichever mode that slider is in — see
    /// <see cref="ApplyTo"/>.</summary>
    public double Temperature { get; set; }
    public double Tint { get; set; }
    public double Vibrance { get; set; }
    public double Saturation { get; set; }

    public MaskAdjustments Clone() => (MaskAdjustments)MemberwiseClone();

    public bool IsNeutral =>
        Exposure == 0 && Contrast == 0 && Highlights == 0 && Shadows == 0 &&
        Whites == 0 && Blacks == 0 && Temperature == 0 && Tint == 0 &&
        Vibrance == 0 && Saturation == 0;

    public void Reset()
    {
        Exposure = Contrast = Highlights = Shadows = Whites = Blacks =
            Temperature = Tint = Vibrance = Saturation = 0;
    }

    /// <summary>
    /// Fold these offsets into a copy of the global settings, producing the
    /// settings the masked region should be rendered with.
    ///
    /// <para>The bipolar sliders clamp after adding, because the tone functions
    /// in <see cref="BasicTone"/> are only defined — and only calibrated — over
    /// ±100; letting a sum run to +160 would extrapolate curves nobody fitted.
    /// Exposure is the exception and is left unclamped: it is a plain 2^EV gain
    /// with no calibrated endpoint, so a global +2 plus a local +2 is honestly
    /// four stops rather than something that has to be capped.</para>
    ///
    /// <para><b>Temperature goes through Kelvin.</b> The offset is in relative
    /// mired units, but the global setting may be in either mode, so the only
    /// place the two can meet is the illuminant itself: convert the global
    /// effective Kelvin back to relative units, add, and convert forward again.
    /// The result is written as an absolute Kelvin with
    /// <see cref="DevelopSettings.UseKelvin"/> set, so the masked render is
    /// independent of which control the user happens to have on screen.</para>
    /// </summary>
    public DevelopSettings ApplyTo(DevelopSettings global)
    {
        var s = global.Clone();

        s.Exposure += Exposure;
        s.Contrast = Clamp100(s.Contrast + Contrast);
        s.Highlights = Clamp100(s.Highlights + Highlights);
        s.Shadows = Clamp100(s.Shadows + Shadows);
        s.Whites = Clamp100(s.Whites + Whites);
        s.Blacks = Clamp100(s.Blacks + Blacks);
        s.Tint = Clamp100(s.Tint + Tint);
        s.Vibrance = Clamp100(s.Vibrance + Vibrance);
        s.Saturation = Clamp100(s.Saturation + Saturation);

        if (Temperature != 0.0)
        {
            double relative = WhiteBalance.KelvinToTemperature(global.EffectiveKelvin) + Temperature;
            s.UseKelvin = true;
            s.TemperatureKelvin = WhiteBalance.TemperatureToKelvin(relative);
        }

        return s;
    }

    private static double Clamp100(double v) => Math.Clamp(v, -100.0, 100.0);
}

/// <summary>Which shape a <see cref="MaskSettings"/> is using.</summary>
public enum MaskKind
{
    Radial,
    Linear,
}

/// <summary>
/// One mask on a photo: a shape, the adjustments that apply through it, and the
/// bookkeeping the panel needs.
///
/// <para><b>The shapes are sibling members plus a discriminator</b>, rather than
/// an interface or a hierarchy. Both are plain data that has to round-trip to a
/// settings file, an inert unused shape costs a few dozen bytes, and keeping
/// them concrete means the panel can bind straight to
/// <c>SelectedMask.Linear.Length</c> without a cast. What the renderer sees is
/// narrower than either: <see cref="Bounds"/> and <see cref="Weights"/>, which is
/// the whole contract a shape has to satisfy — a rectangle and a weight buffer.
/// A brush or a colour-range mask would slot in the same way.</para>
/// </summary>
public sealed class MaskSettings
{
    public string Name { get; set; } = "Radial Gradient";

    /// <summary>Unchecked masks keep their geometry and adjustments but render
    /// as though they were not there — the standard way to A/B a local edit.</summary>
    public bool IsEnabled { get; set; } = true;

    public MaskKind Kind { get; set; } = MaskKind.Radial;

    public RadialMask Radial { get; set; } = new();

    public LinearGradientMask Linear { get; set; } = new();

    public MaskAdjustments Adjustments { get; set; } = new();

    public bool IsRadial => Kind == MaskKind.Radial;
    public bool IsLinear => Kind == MaskKind.Linear;

    /// <summary>True when this mask cannot change the render: switched off, or
    /// every adjustment at zero. The renderer skips these outright, so an empty
    /// mask the user has added but not yet dialled in costs nothing.</summary>
    public bool IsInert => !IsEnabled || Adjustments.IsNeutral;

    /// <summary>The rectangle outside which this mask is exactly zero.</summary>
    public PixelRect Bounds(int imageWidth, int imageHeight) => Kind switch
    {
        MaskKind.Linear => Linear.Bounds(imageWidth, imageHeight),
        _ => Radial.Bounds(imageWidth, imageHeight),
    };

    /// <summary>This mask's weight over <paramref name="rect"/>, row-major.</summary>
    public float[] Weights(int imageWidth, int imageHeight, PixelRect rect) => Kind switch
    {
        MaskKind.Linear => Linear.Weights(imageWidth, imageHeight, rect),
        _ => Radial.Weights(imageWidth, imageHeight, rect),
    };

    public MaskSettings Clone() => new()
    {
        Name = Name,
        IsEnabled = IsEnabled,
        Kind = Kind,
        Radial = Radial.Clone(),
        Linear = Linear.Clone(),
        Adjustments = Adjustments.Clone(),
    };
}
