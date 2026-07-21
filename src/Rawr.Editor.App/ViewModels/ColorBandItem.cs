using System.Windows.Media;
using Rawr.Develop;
using Rawr.Editor.App.Theming;

namespace Rawr.Editor.App.ViewModels;

/// <summary>
/// One dot in the Colour Mixer's swatch row. Immutable — the eight bands are a
/// fixed set, and which one is selected lives on the view-model, not here.
/// </summary>
public sealed class ColorBandItem
{
    public ColorBandItem(ColorBand band)
    {
        Band = band;
        Name = band.ToString();
        Swatch = SliderTracks.BandSwatch(band);
    }

    public ColorBand Band { get; }

    /// <summary>Shown as the dot's tooltip — the row is colour only, and "Aqua"
    /// vs. "Blue" is not always obvious at 22 px.</summary>
    public string Name { get; }

    /// <summary>The band's own centre hue.</summary>
    public Brush Swatch { get; }
}
