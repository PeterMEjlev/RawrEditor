using CommunityToolkit.Mvvm.ComponentModel;
using Rawr.Develop;

namespace Rawr.Editor.App.ViewModels;

/// <summary>
/// One row in the Masks list, and the binding surface for its adjustment
/// sliders. Wraps a <see cref="MaskSettings"/> rather than duplicating it: the
/// overlay control edits the same instance's geometry directly during a drag, so
/// a copy here would have to be reconciled after every mouse move.
///
/// <para>Every setter writes straight through to the model and raises
/// <see cref="Changed"/>; the main view-model listens and schedules a debounced
/// re-render. Going through a single event rather than having the parent
/// subscribe to <c>PropertyChanged</c> and filter by name keeps the "which
/// properties affect the render" question answerable in one place — here, where
/// the properties are.</para>
/// </summary>
public sealed partial class MaskItem : ObservableObject
{
    public MaskItem(MaskSettings mask)
    {
        Mask = mask;
    }

    /// <summary>The model the renderer and the overlay both work against.</summary>
    public MaskSettings Mask { get; }

    private RadialMask Radial => Mask.Radial;
    private LinearGradientMask Linear => Mask.Linear;
    private MaskAdjustments Adjustments => Mask.Adjustments;

    /// <summary>Which shape rows the panel should show. Fixed for a mask's
    /// lifetime — changing kind would mean reinterpreting geometry that has no
    /// sensible translation, so the panel offers a new mask instead.</summary>
    public bool IsRadial => Mask.IsRadial;
    public bool IsLinear => Mask.IsLinear;

    /// <summary>Raised whenever an edit here should cause a re-render.</summary>
    public event EventHandler? Changed;

    private void Edit(Action apply, string propertyName)
    {
        apply();
        OnPropertyChanged(propertyName);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Name
    {
        get => Mask.Name;
        set
        {
            if (Mask.Name == value) return;
            // A rename changes the list row, not the photo, so it notifies
            // without waking the renderer.
            Mask.Name = value;
            OnPropertyChanged();
        }
    }

    public bool IsEnabled
    {
        get => Mask.IsEnabled;
        set { if (Mask.IsEnabled != value) Edit(() => Mask.IsEnabled = value, nameof(IsEnabled)); }
    }

    // ── Shape ───────────────────────────────────────────────────────────────

    public double Feather
    {
        get => Radial.Feather;
        set { if (Radial.Feather != value) Edit(() => Radial.Feather = value, nameof(Feather)); }
    }

    /// <summary>Width of a linear gradient's transition, as a percentage of the
    /// image width. This is the linear equivalent of <see cref="Feather"/> —
    /// there is no separate feather for a ramp, the ramp <i>is</i> the feather.</summary>
    public double Length
    {
        get => Linear.Length * 100.0;
        set
        {
            double v = value / 100.0;
            if (Linear.Length != v) Edit(() => Linear.Length = v, nameof(Length));
        }
    }

    public double Angle
    {
        get => Linear.Angle;
        set { if (Linear.Angle != value) Edit(() => Linear.Angle = value, nameof(Angle)); }
    }

    /// <summary>Routes to whichever shape this mask is — both kinds have an
    /// Invert, and the panel binds one row for it.</summary>
    public bool Invert
    {
        get => Mask.IsLinear ? Linear.Invert : Radial.Invert;
        set
        {
            if (Invert == value) return;
            Edit(() =>
            {
                if (Mask.IsLinear) Linear.Invert = value;
                else Radial.Invert = value;
            }, nameof(Invert));
        }
    }

    // ── Light ───────────────────────────────────────────────────────────────

    public double Exposure
    {
        get => Adjustments.Exposure;
        set { if (Adjustments.Exposure != value) Edit(() => Adjustments.Exposure = value, nameof(Exposure)); }
    }

    public double Contrast
    {
        get => Adjustments.Contrast;
        set { if (Adjustments.Contrast != value) Edit(() => Adjustments.Contrast = value, nameof(Contrast)); }
    }

    public double Highlights
    {
        get => Adjustments.Highlights;
        set { if (Adjustments.Highlights != value) Edit(() => Adjustments.Highlights = value, nameof(Highlights)); }
    }

    public double Shadows
    {
        get => Adjustments.Shadows;
        set { if (Adjustments.Shadows != value) Edit(() => Adjustments.Shadows = value, nameof(Shadows)); }
    }

    public double Whites
    {
        get => Adjustments.Whites;
        set { if (Adjustments.Whites != value) Edit(() => Adjustments.Whites = value, nameof(Whites)); }
    }

    public double Blacks
    {
        get => Adjustments.Blacks;
        set { if (Adjustments.Blacks != value) Edit(() => Adjustments.Blacks = value, nameof(Blacks)); }
    }

    // ── Color ───────────────────────────────────────────────────────────────

    public double Temperature
    {
        get => Adjustments.Temperature;
        set { if (Adjustments.Temperature != value) Edit(() => Adjustments.Temperature = value, nameof(Temperature)); }
    }

    public double Tint
    {
        get => Adjustments.Tint;
        set { if (Adjustments.Tint != value) Edit(() => Adjustments.Tint = value, nameof(Tint)); }
    }

    public double Vibrance
    {
        get => Adjustments.Vibrance;
        set { if (Adjustments.Vibrance != value) Edit(() => Adjustments.Vibrance = value, nameof(Vibrance)); }
    }

    public double Saturation
    {
        get => Adjustments.Saturation;
        set { if (Adjustments.Saturation != value) Edit(() => Adjustments.Saturation = value, nameof(Saturation)); }
    }

    /// <summary>Reset the adjustments but keep the shape — the common case when
    /// a mask is placed correctly and dialled in wrongly.</summary>
    public void ResetAdjustments()
    {
        Adjustments.Reset();
        OnPropertyChanged(nameof(Exposure));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(Highlights));
        OnPropertyChanged(nameof(Shadows));
        OnPropertyChanged(nameof(Whites));
        OnPropertyChanged(nameof(Blacks));
        OnPropertyChanged(nameof(Temperature));
        OnPropertyChanged(nameof(Tint));
        OnPropertyChanged(nameof(Vibrance));
        OnPropertyChanged(nameof(Saturation));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tell the list row that the overlay moved the ellipse, so the
    /// Feather/Invert bindings and any shape readout refresh.</summary>
    public void NotifyShapeChanged()
    {
        OnPropertyChanged(nameof(Feather));
        OnPropertyChanged(nameof(Invert));
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(Angle));
    }
}
