using System.Windows;
using System.Windows.Controls.Primitives;

namespace SteamLuaManager.Controls;

/// <summary>
/// Keeps recommendation cards evenly stretched while selecting a column count
/// from the actual space left by the window and sidebar.
/// </summary>
public class AdaptiveUniformGrid : UniformGrid
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(310d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(AdaptiveUniformGrid),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public static int CalculateColumns(double availableWidth, double minItemWidth, int maxColumns)
    {
        if (!double.IsFinite(availableWidth) || availableWidth <= 0 ||
            !double.IsFinite(minItemWidth) || minItemWidth <= 0)
            return 1;

        return Math.Clamp((int)Math.Floor(availableWidth / minItemWidth), 1, Math.Max(1, maxColumns));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desiredColumns = CalculateColumns(availableSize.Width, MinItemWidth, MaxColumns);
        if (Columns != desiredColumns)
            Columns = desiredColumns;

        return base.MeasureOverride(availableSize);
    }
}
