using System.Windows;
using System.Windows.Media;
using Rawr.Develop;

namespace Rawr.Editor.App.Theming;

/// <summary>
/// The coloured gradient tracks behind the white-balance, presence and colour-mixer
/// sliders, the way Lightroom draws them: Temp runs blue → yellow, Tint green →
/// magenta, Vibrance/Saturation grey → spectrum, and each mixer band ramps through
/// its own hue.
///
/// <para><b>Temp and Tint are not hand-picked colours.</b> Each stop is a neutral
/// mid-grey pushed through <see cref="WhiteBalance.Gains"/> and the very output
/// transform the renderer uses, sampled at the slider position it sits under. The
/// track is therefore a literal preview: the colour under the thumb is what that
/// setting does to a grey card. Hand-tuned stops would be free to drift from the
/// maths — and this pipeline's white balance has already been re-derived once, at
/// which point a decorative gradient would have started lying.</para>
///
/// <para>That also means the Temp track redraws when the slider switches between
/// relative and Kelvin: the two modes span different illuminant ranges, so the
/// same pixel position genuinely is a different colour.</para>
/// </summary>
public static class SliderTracks
{
    /// <summary>Stops per track. Enough that the ramp reads as continuous at the
    /// ~200 px the panel gives a slider, cheap enough to rebuild on a mode flip.</summary>
    private const int Stops = 32;

    /// <summary>
    /// Grey to push through the sliders for the swatches. Far darker than middle
    /// grey on purpose: the output transform renders 0.18 at about 190/255, and a
    /// track that bright both glares against the dark panel and washes the hues
    /// out — at that level the ramp is nearly white end to end. Sampling here
    /// lands it in the mid-tones, where the chroma actually reads.
    /// </summary>
    private const double SwatchGrey = 0.070;

    /// <summary>
    /// Extra chroma, for display only. The gains are luma-normalised and over the
    /// slider's real range produce a track that is accurate but far too muted to
    /// work as a control — the eye needs the ends obviously blue and obviously
    /// yellow. This lengthens the vector away from neutral without rotating it, so
    /// the track still previews <i>which way</i> each end goes; only the distance
    /// is dramatised. Paired with <see cref="SwatchGrey"/> by eye against
    /// Lightroom's own tracks.
    /// </summary>
    private const double ChromaGain = 1.7;

    private static LinearGradientBrush? _tint;
    private static LinearGradientBrush? _saturation;

    /// <summary>Blue → yellow, sampled across the slider's own value range.
    /// <paramref name="toKelvin"/> maps a slider value to an illuminant, so the
    /// same method serves both the relative and the absolute Kelvin mode.</summary>
    public static LinearGradientBrush Temperature(double min, double max, Func<double, double> toKelvin)
        => Build(min, max, v => WhiteBalance.Gains(toKelvin(v), 0.0));

    /// <summary>Green → magenta across the Tint slider's ±100.</summary>
    public static LinearGradientBrush Tint =>
        _tint ??= Build(-100.0, 100.0, v => WhiteBalance.Gains(WhiteBalance.AsShotKelvin, v));

    /// <summary>
    /// Grey → spectrum, shared by Vibrance and Saturation. This one <i>is</i>
    /// decorative: both sliders act on every hue at once, so no single colour is
    /// the honest answer for a given position. What it does encode truthfully is
    /// the axis — fully desaturated at −100, where Saturation reaches greyscale,
    /// ramping to full chroma at +100.
    /// </summary>
    public static LinearGradientBrush Saturation
    {
        get
        {
            if (_saturation is not null) return _saturation;

            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            for (int i = 0; i < Stops; i++)
            {
                double t = i / (double)(Stops - 1);
                // Green → red only. Carrying the sweep further round, through blue,
                // puts a cyan band a fifth of the way along that Lightroom's track
                // does not have and that reads as a feature of the slider rather
                // than decoration. Value rises with saturation so the desaturated
                // end recedes instead of sitting there as a bright grey bar.
                var (r, g, b) = FromHsv(140.0 - 140.0 * t, 0.88 * t, 0.40 + 0.36 * t);
                brush.GradientStops.Add(new GradientStop(Color.FromRgb(r, g, b), t));
            }
            brush.Freeze();
            return _saturation = brush;
        }
    }

    // ── Colour Mixer ────────────────────────────────────────────────────────
    // These do *not* go through DisplayCurve/LightroomMatch, and that is not an
    // oversight: those map scene-linear to display, and the mixer already runs at
    // the end of the pipeline on display-referred RGB. An HSL colour here is
    // therefore already in the space the panel edits, so it is written out
    // directly. Pushing it through the output transform a second time would light
    // every swatch a stop and a half too bright.

    /// <summary>Saturation and lightness the band swatches are drawn at. Vivid
    /// enough to name the hue at 22 px against a dark panel.</summary>
    private const double SwatchSaturation = 0.85;
    private const double SwatchLightness = 0.52;

    /// <summary>The identifying dot for one band, at that band's own centre hue.</summary>
    public static SolidColorBrush BandSwatch(ColorBand band)
    {
        var brush = new SolidColorBrush(
            Hsl(ColorMixer.BandCenters[(int)band], SwatchSaturation, SwatchLightness));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// What the Hue slider will actually do: the band's centre hue sweeping
    /// through the full ±<see cref="ColorMixer.MaxHueShiftDegrees"/> the slider
    /// can reach, so the colour under the thumb is the hue that setting produces.
    /// </summary>
    public static LinearGradientBrush BandHue(ColorBand band)
    {
        double center = ColorMixer.BandCenters[(int)band];
        return Ramp(t => Hsl(center + (t * 2.0 - 1.0) * ColorMixer.MaxHueShiftDegrees,
                             SwatchSaturation, SwatchLightness));
    }

    /// <summary>Grey → the band's hue at full chroma, matching the slider's
    /// −100-reaches-true-grey travel.</summary>
    public static LinearGradientBrush BandSaturation(ColorBand band)
    {
        double hue = ColorMixer.BandCenters[(int)band];
        return Ramp(t => Hsl(hue, t, SwatchLightness));
    }

    /// <summary>Dark → light in the band's own hue. The ends stop short of black
    /// and white because the slider does too — see
    /// <see cref="ColorMixer.MaxLuminanceShift"/>.</summary>
    public static LinearGradientBrush BandLuminance(ColorBand band)
    {
        double hue = ColorMixer.BandCenters[(int)band];
        return Ramp(t => Hsl(hue, SwatchSaturation, 0.12 + 0.76 * t));
    }

    /// <summary>Sample a colour function across 0…1 of the track.</summary>
    private static LinearGradientBrush Ramp(Func<double, Color> sample)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < Stops; i++)
        {
            double t = i / (double)(Stops - 1);
            brush.GradientStops.Add(new GradientStop(sample(t), t));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>HSL → sRGB, through the renderer's own conversion so a swatch
    /// cannot drift from what the mixer does with that hue.</summary>
    private static Color Hsl(double hue, double saturation, double lightness)
    {
        ColorMixer.HslToRgb((float)hue, (float)saturation, (float)lightness,
                            out float r, out float g, out float b);
        return Color.FromRgb(Byte01(r), Byte01(g), Byte01(b));
    }

    private static byte Byte01(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0.0, 255.0);

    /// <summary>Sample a gain function across a slider range and render each
    /// sample the way the pipeline would render a grey card under it.</summary>
    private static LinearGradientBrush Build(double min, double max,
                                             Func<double, (double r, double g, double b)> gains)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i < Stops; i++)
        {
            double t = i / (double)(Stops - 1);
            var (gr, gg, gb) = gains(min + (max - min) * t);
            brush.GradientStops.Add(new GradientStop(Swatch(gr, gg, gb), t));
        }
        brush.Freeze();
        return brush;
    }

    /// <summary>A grey card under the given gains, through the render's own
    /// output transform — so the swatch is what the setting actually does.</summary>
    private static Color Swatch(double gr, double gg, double gb)
    {
        double r = SwatchGrey * gr, g = SwatchGrey * gg, b = SwatchGrey * gb;

        // Exaggerate about the luma-preserving neutral, in linear light so hue is
        // untouched — this only lengthens the vector away from grey. The scale is
        // capped at what still fits the gamut, because clipping a channel does not
        // just alter one swatch: past the point where a channel pins, every
        // further stop pins with it and the ramp flattens into a slab of primary.
        // At 2000 K that turned the whole cool end into #0000FF.
        double y = BasicTone.Luminance(r, g, b);
        double k = Math.Min(ChromaGain, MaxChromaScale(y, r, g, b));
        r = y + (r - y) * k;
        g = y + (g - y) * k;
        b = y + (b - y) * k;

        return Color.FromRgb(Encode(r), Encode(g), Encode(b));
    }

    /// <summary>Largest scale that keeps every channel inside [0, 1] when pushed
    /// away from <paramref name="y"/> — i.e. the point at which the first channel
    /// would pin.</summary>
    private static double MaxChromaScale(double y, double r, double g, double b)
    {
        double limit = double.PositiveInfinity;
        foreach (double c in stackalloc[] { r, g, b })
        {
            double d = c - y;
            if (Math.Abs(d) < 1e-12) continue;
            limit = Math.Min(limit, d > 0.0 ? (1.0 - y) / d : y / -d);
        }
        return limit;
    }

    private static byte Encode(double lin)
        => (byte)Math.Clamp(
            Math.Round(BasicTone.LightroomMatch(BasicTone.DisplayCurve(lin)) * 255.0), 0.0, 255.0);

    /// <summary>HSV → sRGB bytes. Hue in degrees, s/v in 0…1.</summary>
    private static (byte r, byte g, byte b) FromHsv(double hue, double s, double v)
    {
        double c = v * s;
        double h = (hue % 360.0 + 360.0) % 360.0 / 60.0;
        double x = c * (1.0 - Math.Abs(h % 2.0 - 1.0));
        double m = v - c;

        (double r, double g, double b) rgb = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return ((byte)Math.Round((rgb.r + m) * 255.0),
                (byte)Math.Round((rgb.g + m) * 255.0),
                (byte)Math.Round((rgb.b + m) * 255.0));
    }
}
