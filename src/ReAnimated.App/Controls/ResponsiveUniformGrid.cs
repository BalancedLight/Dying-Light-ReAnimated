using System.Windows;
using System.Windows.Controls.Primitives;

namespace ReAnimated.App.Controls;

/// <summary>
/// Keeps compact vector/form rows horizontal when space permits and stacks
/// them when a dock pane is made portrait or narrow.
/// </summary>
public sealed class ResponsiveUniformGrid : UniformGrid
{
    public static readonly DependencyProperty PreferredColumnsProperty =
        DependencyProperty.Register(
            nameof(PreferredColumns),
            typeof(int),
            typeof(ResponsiveUniformGrid),
            new FrameworkPropertyMetadata(
                1,
                FrameworkPropertyMetadataOptions.AffectsMeasure),
            static value => (int)value > 0);

    public static readonly DependencyProperty MinimumCellWidthProperty =
        DependencyProperty.Register(
            nameof(MinimumCellWidth),
            typeof(double),
            typeof(ResponsiveUniformGrid),
            new FrameworkPropertyMetadata(
                105.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure),
            static value => (double)value > 0.0);

    public int PreferredColumns
    {
        get => (int)GetValue(PreferredColumnsProperty);
        set => SetValue(PreferredColumnsProperty, value);
    }

    public double MinimumCellWidth
    {
        get => (double)GetValue(MinimumCellWidthProperty);
        set => SetValue(MinimumCellWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        int columns = PreferredColumns;
        if (!double.IsInfinity(constraint.Width) &&
            !double.IsNaN(constraint.Width))
        {
            columns = Math.Clamp(
                (int)Math.Floor(
                    Math.Max(1.0, constraint.Width) /
                    MinimumCellWidth),
                1,
                PreferredColumns);
        }

        if (Columns != columns)
        {
            SetCurrentValue(ColumnsProperty, columns);
        }

        return base.MeasureOverride(constraint);
    }
}
