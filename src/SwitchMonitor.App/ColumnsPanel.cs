using System.Windows;
using System.Windows.Controls;

namespace SwitchMonitor.App;

/// <summary>
/// Responsive layout for a view's cards: one column in a normal window; when the window is wide (maximized or full screen)
/// the cards spread over two or three columns, each card going to the shortest column, in order. Elements marked
/// <see cref="FullWidthProperty"/> (headings, notices) span every column at their place.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(nameof(MinColumnWidth), typeof(double),
        typeof(ColumnsPanel), new FrameworkPropertyMetadata(520.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int),
        typeof(ColumnsPanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double),
        typeof(ColumnsPanel), new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FullWidthProperty = DependencyProperty.RegisterAttached("FullWidth", typeof(bool),
        typeof(ColumnsPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }

    public static bool GetFullWidth(DependencyObject element) => (bool)element.GetValue(FullWidthProperty);
    public static void SetFullWidth(DependencyObject element, bool value) => element.SetValue(FullWidthProperty, value);

    private readonly List<Rect> places = [];

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumnWidth : available.Width;
        return Layout(width, measure: true);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Layout(final.Width, measure: false);
        for (int i = 0; i < InternalChildren.Count && i < places.Count; i++) InternalChildren[i].Arrange(places[i]);
        return final;
    }

    private Size Layout(double width, bool measure)
    {
        int columns = Math.Clamp((int)((width + Gap) / (MinColumnWidth + Gap)), 1, Math.Max(1, MaxColumns));
        double columnWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var heights = new double[columns];
        places.Clear();
        foreach (UIElement child in InternalChildren)
        {
            bool full = GetFullWidth(child) || columns == 1;
            double childWidth = full ? width : columnWidth;
            if (measure) child.Measure(new Size(childWidth, double.PositiveInfinity));
            double height = child.DesiredSize.Height;
            if (full)
            {
                double top = heights.Max();
                places.Add(new Rect(0, top, width, height));
                for (int c = 0; c < columns; c++) heights[c] = top + height;
            }
            else
            {
                int column = Array.IndexOf(heights, heights.Min());
                places.Add(new Rect(column * (columnWidth + Gap), heights[column], columnWidth, height));
                heights[column] += height;
            }
        }
        return new Size(width, heights.Length == 0 ? 0 : heights.Max());
    }
}
