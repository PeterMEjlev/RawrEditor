using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Rawr.Editor.App.Controls;

/// <summary>
/// One labelled adjustment: a caption line carrying the name and the numeric
/// readout, with a full-width centre-anchored slider beneath it.
/// The slider's accent fill grows from <see cref="Origin"/> (neutral) toward the
/// thumb in either direction; double-clicking the slider snaps the value back to
/// that origin.
///
/// <para>Values are quantised to the <see cref="Step"/> grid and displayed with
/// <see cref="Decimals"/> places — 0 for every slider except Exposure, which is
/// a physical quantity in stops and keeps Lightroom's 0.05 resolution.</para>
///
/// <para>When <see cref="AlternateUnitText"/> is set, a small unit pill appears on
/// the right; clicking it toggles <see cref="IsAlternateUnit"/>, which the
/// view-model interprets as a real change of units (Temp: relative ↔ Kelvin).
/// The row itself does no conversion — it only reports the toggle.</para>
/// </summary>
public partial class AdjustmentRow : UserControl
{
    public AdjustmentRow()
    {
        InitializeComponent();
        PART_Slider.MouseDoubleClick += (_, _) => Value = Origin;
        PART_Slider.ValueChanged += OnSliderValueChanged;

        // Thumb drag start/end bubble out of the Track's thumb. Used by rows whose
        // adjustment has a transient visualisation — Masking shows its mask while
        // you hold it. Deliberately *not* raised for track clicks or arrow keys:
        // those change the value without a sustained gesture, and flashing an
        // overlay up for a single keypress would be noise rather than feedback.
        PART_Slider.AddHandler(Thumb.DragStartedEvent,
            new DragStartedEventHandler((_, _) => IsDragging = true));
        PART_Slider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => IsDragging = false));
        Loaded += (_, _) =>
        {
            SyncSlider();
            UpdateFill();
            UpdateValueText(force: true);
        };
    }

    // Guards the two-way hand-off between Value (real units) and the slider
    // (position). Without it the drag fights itself: the slider moves, Value
    // snaps to the Step grid, and pushing that snapped value back at the slider
    // drags the thumb out from under the cursor on every mouse move.
    private bool _syncing;

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _syncing = true;
        try { Value = SliderScales.ToValue(Scale, e.NewValue, Minimum, Maximum); }
        finally { _syncing = false; }
    }

    /// <summary>Move the thumb to wherever <see cref="Value"/> now is. No-op while
    /// the slider is the one driving — see <see cref="_syncing"/>.</summary>
    private void SyncSlider()
    {
        if (_syncing) return;
        _syncing = true;
        try { PART_Slider.Value = SliderScales.ToPosition(Scale, Value, Minimum, Maximum); }
        finally { _syncing = false; }

        // Keyboard steps are in position space. On a linear row one arrow press is
        // one Step; on a log row a fixed fraction, which yields the small moves at
        // the dense end and larger ones at the sparse end that the scale implies.
        double span = Maximum - Minimum;
        double small = Scale == SliderScale.Logarithmic || span <= 0.0
            ? 1.0 / 200.0
            : Math.Clamp(Step / span, 1e-4, 1.0);
        PART_Slider.SmallChange = small;
        PART_Slider.LargeChange = small * 10.0;
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(AdjustmentRow),
            new PropertyMetadata("Adjustment"));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(AdjustmentRow),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged,
                CoerceValue));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(-100.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(100.0, OnRangeChanged));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(1.0, OnRangeChanged));

    /// <summary>Decimal places shown in the readout. 0 (the default) is what
    /// makes every slider read as a whole number.</summary>
    public static readonly DependencyProperty DecimalsProperty =
        DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(AdjustmentRow),
            new PropertyMetadata(0, OnRangeChanged));

    /// <summary>The value double-clicking resets to, and the point the accent
    /// fill grows out of. 0 for a bipolar slider; for an absolute one like
    /// Kelvin it is the setting that means "unchanged".</summary>
    public static readonly DependencyProperty OriginProperty =
        DependencyProperty.Register(nameof(Origin), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(0.0, OnRangeChanged));

    /// <summary>How the value is laid out along the track. Logarithmic is for
    /// Kelvin, whose useful values otherwise bunch into the left sixth.</summary>
    public static readonly DependencyProperty ScaleProperty =
        DependencyProperty.Register(nameof(Scale), typeof(SliderScale), typeof(AdjustmentRow),
            new PropertyMetadata(SliderScale.Linear, OnRangeChanged));

    /// <summary>Optional gradient painted along the track, as Lightroom does for
    /// the white-balance and presence sliders. Setting it also suppresses the
    /// accent fill — see <see cref="OnTrackBrushChanged"/>.</summary>
    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(AdjustmentRow),
            new PropertyMetadata(null, OnTrackBrushChanged));

    public static readonly DependencyProperty UnitTextProperty =
        DependencyProperty.Register(nameof(UnitText), typeof(string), typeof(AdjustmentRow),
            new PropertyMetadata("", OnUnitChanged));

    public static readonly DependencyProperty AlternateUnitTextProperty =
        DependencyProperty.Register(nameof(AlternateUnitText), typeof(string), typeof(AdjustmentRow),
            new PropertyMetadata("", OnUnitChanged));

    public static readonly DependencyProperty IsAlternateUnitProperty =
        DependencyProperty.Register(nameof(IsAlternateUnit), typeof(bool), typeof(AdjustmentRow),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnUnitChanged));

    /// <summary>True while the user is holding the thumb. Two-way so a view-model
    /// can drive a transient preview from it, as the Masking row does.</summary>
    public static readonly DependencyProperty IsDraggingProperty =
        DependencyProperty.Register(nameof(IsDragging), typeof(bool), typeof(AdjustmentRow),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty HasUnitToggleProperty =
        DependencyProperty.Register(nameof(HasUnitToggle), typeof(bool), typeof(AdjustmentRow),
            new PropertyMetadata(false));

    public static readonly DependencyProperty CurrentUnitTextProperty =
        DependencyProperty.Register(nameof(CurrentUnitText), typeof(string), typeof(AdjustmentRow),
            new PropertyMetadata(""));

    public static readonly DependencyProperty UnitToolTipProperty =
        DependencyProperty.Register(nameof(UnitToolTip), typeof(string), typeof(AdjustmentRow),
            new PropertyMetadata(""));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }
    public double Origin { get => (double)GetValue(OriginProperty); set => SetValue(OriginProperty, value); }
    public SliderScale Scale { get => (SliderScale)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }
    public Brush? TrackBrush { get => (Brush?)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public string UnitText { get => (string)GetValue(UnitTextProperty); set => SetValue(UnitTextProperty, value); }
    public string AlternateUnitText { get => (string)GetValue(AlternateUnitTextProperty); set => SetValue(AlternateUnitTextProperty, value); }
    public bool IsAlternateUnit { get => (bool)GetValue(IsAlternateUnitProperty); set => SetValue(IsAlternateUnitProperty, value); }
    public bool IsDragging { get => (bool)GetValue(IsDraggingProperty); set => SetValue(IsDraggingProperty, value); }
    public bool HasUnitToggle { get => (bool)GetValue(HasUnitToggleProperty); private set => SetValue(HasUnitToggleProperty, value); }
    public string CurrentUnitText { get => (string)GetValue(CurrentUnitTextProperty); private set => SetValue(CurrentUnitTextProperty, value); }
    public string UnitToolTip { get => (string)GetValue(UnitToolTipProperty); private set => SetValue(UnitToolTipProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (AdjustmentRow)d;
        row.SyncSlider();
        row.UpdateFill();
        row.UpdateValueText();
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var row = (AdjustmentRow)d;
        double value = (double)baseValue;
        if (!double.IsFinite(value)) return row.Value;

        double min = Math.Min(row.Minimum, row.Maximum);
        double max = Math.Max(row.Minimum, row.Maximum);
        // Snap first, then clamp: the reverse order can push a value that landed
        // exactly on an endpoint back off it when the endpoint isn't itself on
        // the Step grid (Kelvin's 2000…50000 at step 50 is, but nothing enforces
        // that for a future row).
        return Math.Clamp(row.Snap(value), min, max);
    }

    /// <summary>
    /// Quantise to the <see cref="Step"/> grid, then round off the binary
    /// representation error that leaves behind — a 0.05 grid otherwise yields
    /// values like 0.30000000000000004, which the readout would happily print in
    /// full and which make equality checks against a neutral 0 fail.
    /// </summary>
    private double Snap(double value)
    {
        double step = Step;
        if (double.IsFinite(step) && step > 0.0)
            value = Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

        int decimals = Math.Clamp(Decimals, 0, 15);
        return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (AdjustmentRow)d;
        row.CoerceValue(ValueProperty);
        row.SyncSlider();
        row.UpdateFill();
        row.UpdateValueText();
    }

    /// <summary>
    /// Paint the gradient onto the track, and drop the accent fill while it is
    /// there. The two cannot coexist: the accent fill is an opaque bar covering
    /// everything between the origin and the thumb, so on a gradient row it would
    /// hide the very half of the ramp the user is dragging into. Lightroom makes
    /// the same call — its coloured sliders show the gradient and no fill, because
    /// on those the gradient already says which way you are going.
    /// </summary>
    private static void OnTrackBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (AdjustmentRow)d;
        var brush = (Brush?)e.NewValue;
        if (brush is null)
        {
            row.PART_Slider.ClearValue(BackgroundProperty);
            row.PART_Slider.ClearValue(ForegroundProperty);
        }
        else
        {
            row.PART_Slider.Background = brush;
            row.PART_Slider.Foreground = Brushes.Transparent;
        }
    }

    private static void OnUnitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (AdjustmentRow)d;
        row.HasUnitToggle = !string.IsNullOrEmpty(row.AlternateUnitText);
        row.CurrentUnitText = row.IsAlternateUnit ? row.AlternateUnitText : row.UnitText;
        row.UnitToolTip = row.HasUnitToggle
            ? $"Showing {Describe(row.CurrentUnitText)} — click for " +
              $"{Describe(row.IsAlternateUnit ? row.UnitText : row.AlternateUnitText)}"
            : "";
    }

    private static string Describe(string unit) => unit switch
    {
        "K" => "absolute temperature in Kelvin",
        "Δ" => "temperature relative to the camera white balance",
        _ => unit
    };

    // Anchor the accent selection fill at the origin so the bar reads outward
    // from neutral rather than from the left end of the track. Expressed in
    // position space, like everything else the slider sees.
    private void UpdateFill()
    {
        double v = SliderScales.ToPosition(Scale, Value, Minimum, Maximum);
        double o = SliderScales.ToPosition(Scale, Origin, Minimum, Maximum);
        PART_Slider.SelectionStart = Math.Min(o, v);
        PART_Slider.SelectionEnd = Math.Max(o, v);
    }

    private void UpdateValueText(bool force = false)
    {
        if (!force && PART_ValueBox.IsKeyboardFocusWithin) return;
        int decimals = Math.Clamp(Decimals, 0, 15);
        PART_ValueBox.Text = Value.ToString("F" + decimals, CultureInfo.CurrentCulture);
    }

    private void CommitValueText()
    {
        if (TryParseValue(PART_ValueBox.Text, out double typedValue))
            Value = typedValue;

        UpdateValueText(force: true);
    }

    private static bool TryParseValue(string text, out double value)
    {
        text = text.Trim();
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        return parsed && double.IsFinite(value);
    }

    private void OnValueBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => PART_ValueBox.SelectAll();

    private void OnValueBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CommitValueText();

    private void OnValueBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitValueText();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateValueText(force: true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnValueBoxPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (PART_ValueBox.IsKeyboardFocusWithin) return;

        PART_ValueBox.Focus();
        e.Handled = true;
    }

    private void OnUnitClick(object sender, RoutedEventArgs e) => IsAlternateUnit = !IsAlternateUnit;
}
