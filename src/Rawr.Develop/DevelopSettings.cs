namespace Rawr.Develop;

/// <summary>
/// The full set of develop adjustments for one photo. All sliders are neutral at
/// their default values, so a fresh instance renders the camera-matched baseline
/// (identical to RAWR's preview look) with nothing applied.
///
/// Ranges mirror Lightroom's Basic panel so the numbers feel familiar:
///   • Temperature / Tint            −100 … +100  (relative to the camera WB)
///   • Temperature (Kelvin mode)     2000 … 50000 K
///   • Exposure                       −5 … +5     stops
///   • Contrast / Highlights /
///     Shadows / Whites / Blacks     −100 … +100
///   • Vibrance / Saturation         −100 … +100
///   • Color Mixer, per band         −100 … +100  (Hue / Saturation / Luminance)
///
/// It is a mutable class (not a record) so the WPF view-model can bind two-way
/// to each property; <see cref="Clone"/> gives the exporter an immutable-enough
/// snapshot to render off-thread.
/// </summary>
public sealed class DevelopSettings
{
    public double Temperature { get; set; }   // −100 cool ……… +100 warm (relative)
    public double Tint { get; set; }           // −100 green ……… +100 magenta
    public double Exposure { get; set; }       // stops, 2^Exposure linear gain
    public double Contrast { get; set; }
    public double Highlights { get; set; }
    public double Shadows { get; set; }
    public double Whites { get; set; }
    public double Blacks { get; set; }
    public double Vibrance { get; set; }
    public double Saturation { get; set; }

    /// <summary>Which of the two Temperature controls is live. The pair is
    /// deliberately <i>not</i> kept in sync automatically: the relative slider
    /// spans only ±100 mireds about the anchor, so a round trip through it would
    /// silently clamp an illuminant the Kelvin slider can reach. The view-model
    /// converts once, at the moment the user flips the mode.</summary>
    public bool UseKelvin { get; set; }

    /// <summary>Absolute illuminant temperature, used when <see cref="UseKelvin"/>
    /// is set. Defaults to the anchor, where white balance is a no-op.</summary>
    public double TemperatureKelvin { get; set; } = WhiteBalance.AsShotKelvin;

    /// <summary>The illuminant Kelvin the render should actually use, whichever
    /// control the user has in front of them.</summary>
    public double EffectiveKelvin =>
        UseKelvin ? TemperatureKelvin : WhiteBalance.TemperatureToKelvin(Temperature);

    /// <summary>The eight-band Hue/Saturation/Luminance mixer. Never null, and
    /// neutral until a band is touched.</summary>
    public ColorMixerSettings ColorMixer { get; set; } = new();

    // ── Detail ──────────────────────────────────────────────────────────────
    // Lightroom ships raw files with Sharpening at 40; this defaults to 0 instead,
    // so a fresh DevelopSettings still reproduces the calibrated baseline exactly.
    // The supporting sliders keep Lightroom's defaults, which are inert while
    // Amount is 0, and Colour NR defaults to the strength that reproduces the
    // radius-2 chroma blur this pipeline used to apply unconditionally.
    public double Sharpening { get; set; }
    public double SharpenRadius { get; set; } = Detail.DefaultSharpenRadius;
    public double SharpenDetail { get; set; } = Detail.DefaultSharpenDetail;
    public double SharpenMasking { get; set; } = Detail.DefaultSharpenMasking;

    public double LuminanceNoiseReduction { get; set; }
    public double LuminanceNoiseDetail { get; set; } = Detail.DefaultLuminanceDetail;
    public double LuminanceNoiseContrast { get; set; } = Detail.DefaultLuminanceContrast;

    public double ColorNoiseReduction { get; set; } = Detail.DefaultColorNoiseReduction;

    // ── Effects ─────────────────────────────────────────────────────────────
    // Texture and Clarity are the same base/detail operator at two scales;
    // Dehaze is a scene-linear haze recovery; Grain is a synthetic emulsion.
    // All four are neutral at 0 except Grain's supporting pair, which — like
    // Sharpening's Radius/Detail — are inert while Amount is 0.
    public double Texture { get; set; }
    public double Clarity { get; set; }
    public double Dehaze { get; set; }

    public double GrainAmount { get; set; }
    public double GrainSize { get; set; } = Effects.DefaultGrainSize;
    public double GrainRoughness { get; set; } = Effects.DefaultGrainRoughness;

    // ── Geometry ────────────────────────────────────────────────────────────
    /// <summary>
    /// Crop, straighten and 90° orientation. Applied before every other stage —
    /// see <see cref="Geometry"/> — so the rest of this class describes what the
    /// cropped photograph looks like rather than what the sensor recorded.
    /// </summary>
    public GeometrySettings Geometry { get; set; } = new();

    // ── Masks ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Local adjustments, applied in order over the global ones. Empty on a
    /// fresh photo, and an empty list is what keeps the neutral render on the
    /// single-pass path — see <see cref="DevelopProcessor"/>.
    ///
    /// <para>Where masks overlap their adjustments accumulate, so list order
    /// does not affect the render — see <see cref="DevelopProcessor"/>. The list
    /// is ordered for the panel's benefit, not the renderer's.</para>
    /// </summary>
    public List<MaskSettings> Masks { get; set; } = new();

    /// <summary>The masks that can actually change the render — switched on and
    /// carrying at least one non-zero adjustment.</summary>
    public IEnumerable<MaskSettings> ActiveMasks => Masks.Where(m => !m.IsInert);

    /// <summary>
    /// A snapshot the exporter can render off-thread. MemberwiseClone is shallow,
    /// so the reference-typed members — <see cref="ColorMixer"/>,
    /// <see cref="Geometry"/> and each entry in <see cref="Masks"/> — are copied
    /// explicitly; sharing them would let a slider moved (or a crop box dragged)
    /// during an export change what that export writes.
    /// </summary>
    public DevelopSettings Clone()
    {
        var copy = (DevelopSettings)MemberwiseClone();
        copy.ColorMixer = ColorMixer.Clone();
        copy.Geometry = this.Geometry.Clone();
        copy.Masks = Masks.Select(m => m.Clone()).ToList();
        return copy;
    }

    /// <summary>True when every slider is at its neutral default.</summary>
    public bool IsNeutral =>
        EffectiveKelvin == WhiteBalance.AsShotKelvin &&
        Tint == 0 && Exposure == 0 && Contrast == 0 &&
        Highlights == 0 && Shadows == 0 && Whites == 0 && Blacks == 0 &&
        Vibrance == 0 && Saturation == 0 &&
        ColorMixer.IsNeutral &&
        // "Neutral" means renders the baseline, which is not the same as every
        // Detail slider reading zero: the baseline has always carried the chroma
        // blur that Colour NR at its default reproduces.
        Sharpening == 0 && LuminanceNoiseReduction == 0 &&
        ColorNoiseReduction == Detail.DefaultColorNoiseReduction &&
        Texture == 0 && Clarity == 0 && Dehaze == 0 && GrainAmount == 0 &&
        this.Geometry.IsNeutral &&
        // A mask with nothing dialled in is not an edit. Testing IsInert rather
        // than Masks.Count keeps "add a mask, then undo the slider" neutral.
        !ActiveMasks.Any();

    public void Reset()
    {
        Temperature = Tint = Exposure = Contrast = Highlights =
            Shadows = Whites = Blacks = Vibrance = Saturation = 0;
        UseKelvin = false;
        TemperatureKelvin = WhiteBalance.AsShotKelvin;
        ColorMixer.Reset();

        Sharpening = 0;
        SharpenRadius = Detail.DefaultSharpenRadius;
        SharpenDetail = Detail.DefaultSharpenDetail;
        SharpenMasking = Detail.DefaultSharpenMasking;
        LuminanceNoiseReduction = 0;
        LuminanceNoiseDetail = Detail.DefaultLuminanceDetail;
        LuminanceNoiseContrast = Detail.DefaultLuminanceContrast;
        ColorNoiseReduction = Detail.DefaultColorNoiseReduction;

        Texture = Clarity = Dehaze = 0;
        GrainAmount = 0;
        GrainSize = Effects.DefaultGrainSize;
        GrainRoughness = Effects.DefaultGrainRoughness;

        this.Geometry.Reset();

        Masks.Clear();
    }
}
