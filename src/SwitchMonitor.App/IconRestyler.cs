using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace SwitchMonitor.App;

/// <summary>
/// Gives the panel's icons the chosen icon style. Every icon of the panel is written with the Windows icon font; when a
/// text block showing one of the known glyphs loads (or its glyph changes), it is redrawn in the style: another font and
/// glyph, and for Duotone a soft second layer behind it. Elements tagged "NoRestyle" (the logo, previews, profile marks
/// that carry their own style) are left alone.
/// </summary>
public static class IconRestyler
{
    public const string Skip = "NoRestyle";

    private static readonly DependencyProperty OriginalProperty = DependencyProperty.RegisterAttached(
        "Original", typeof(string), typeof(IconRestyler), new PropertyMetadata(null));
    private static readonly DependencyProperty BaseSizeProperty = DependencyProperty.RegisterAttached(
        "BaseSize", typeof(double), typeof(IconRestyler), new PropertyMetadata(double.NaN));
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(IconRestyler), new PropertyMetadata(false));

    private static readonly List<WeakReference<TextBlock>> live = [];
    private static readonly DependencyPropertyDescriptor textChanges =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    private static readonly FontFamily LineFont = new("Segoe MDL2 Assets");
    private static bool applying;

    /// <summary>Called when any text block loads (App.xaml styles).</summary>
    public static void OnLoaded(object sender)
    {
        if (sender is not TextBlock text || Equals(text.Tag, Skip) || (bool)text.GetValue(HookedProperty)) return;
        bool iconFont = text.FontFamily?.Source?.Contains("MDL2", StringComparison.OrdinalIgnoreCase) == true;
        if (!iconFont && !Icons.IsKnownGlyph(text.Text)) return;
        text.SetValue(HookedProperty, true);
        textChanges.AddValueChanged(text, OnTextChanged);
        lock (live) live.Add(new WeakReference<TextBlock>(text));
        if (Icons.IsKnownGlyph(text.Text)) text.SetValue(OriginalProperty, text.Text);
        Apply(text);
    }

    /// <summary>Redraws every live icon after the style changed.</summary>
    public static void RefreshAll()
    {
        List<TextBlock> texts = [];
        lock (live)
        {
            live.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in live) if (w.TryGetTarget(out var t)) texts.Add(t);
        }
        foreach (var text in texts) Apply(text);
    }

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (applying || sender is not TextBlock text) return;
        // A binding (or a trigger) set another glyph: that is the new original.
        text.SetValue(OriginalProperty, Icons.IsKnownGlyph(text.Text) ? text.Text : null);
        Apply(text);
    }

    private static void Apply(TextBlock text)
    {
        if (text.GetValue(OriginalProperty) is not string original) return;
        var mark = Icons.Restyle(original, Icons.PanelStyle);
        if (double.IsNaN((double)text.GetValue(BaseSizeProperty))) text.SetValue(BaseSizeProperty, text.FontSize);
        double baseSize = (double)text.GetValue(BaseSizeProperty);
        applying = true;
        try
        {
            if (mark is null)
            {
                text.Text = original;
                text.FontFamily = LineFont;
                text.FontSize = baseSize;
                text.ClearValue(TextBlock.BackgroundProperty);
                return;
            }
            text.Text = mark.Text;
            text.FontFamily = mark.Font;
            text.FontSize = baseSize * Icons.SizeFactor(Icons.PanelStyle);
            if (mark.Secondary is { } back)
            {
                // Duotone: the filled shape, faint, exactly under the outline (same font, size and origin).
                var layer = new TextBlock { Text = back, FontFamily = mark.Font, FontSize = text.FontSize, Opacity = 0.28, Tag = Skip };
                layer.SetBinding(TextBlock.ForegroundProperty, new Binding(nameof(TextBlock.Foreground)) { Source = text });
                layer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                layer.Arrange(new Rect(layer.DesiredSize));
                var box = new Rect(layer.DesiredSize);   // the layout box, not the ink, so both layers line up
                text.Background = new VisualBrush(layer)
                {
                    Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, TileMode = TileMode.None,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = box,
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = box
                };
            }
            else text.ClearValue(TextBlock.BackgroundProperty);
        }
        finally { applying = false; }
    }
}
