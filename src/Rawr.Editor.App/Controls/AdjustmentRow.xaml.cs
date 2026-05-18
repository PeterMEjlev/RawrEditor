using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Rawr.Editor.App.Controls;

/// <summary>
/// One labelled adjustment: name · centre-anchored slider · numeric value.
/// The slider's accent fill grows from 0 (neutral) toward the thumb in either
/// direction; double-clicking the slider snaps the value back to zero.
/// </summary>
public partial class AdjustmentRow : UserControl
{
    public AdjustmentRow()
    {
        InitializeComponent();
        PART_Slider.MouseDoubleClick += (_, _) => Value = 0;
        Loaded += (_, _) => UpdateFill();
    }

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(AdjustmentRow),
            new PropertyMetadata("Adjustment"));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(AdjustmentRow),
            new FrameworkPropertyMetadata(0.0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty =
        DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(-100.0));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(100.0));

    public static readonly DependencyProperty StepProperty =
        DependencyProperty.Register(nameof(Step), typeof(double), typeof(AdjustmentRow),
            new PropertyMetadata(1.0));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((AdjustmentRow)d).UpdateFill();

    // Anchor the accent selection fill at 0 so the bar reads from neutral.
    private void UpdateFill()
    {
        double v = Value;
        PART_Slider.SelectionStart = Math.Min(0, v);
        PART_Slider.SelectionEnd = Math.Max(0, v);
    }
}
