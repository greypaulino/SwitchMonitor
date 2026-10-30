using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwitchMonitor.App;

/// <summary>
/// A profile's mark: its color with its glyph in dark on it, or the picture it chose (filling the shape). One control
/// for the profile row, the list, the editor and the shortcuts, so they always look the same.
/// </summary>
public sealed class ProfileBadge : Border
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string),
        typeof(ProfileBadge), new PropertyMetadata("", (d, _) => ((ProfileBadge)d).Update()));
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(nameof(Image), typeof(ImageSource),
        typeof(ProfileBadge), new PropertyMetadata(null, (d, _) => ((ProfileBadge)d).Update()));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush),
        typeof(ProfileBadge), new PropertyMetadata(Brushes.Gray, (d, _) => ((ProfileBadge)d).Update()));
    public static readonly DependencyProperty GlyphFontProperty = DependencyProperty.Register(nameof(GlyphFont), typeof(FontFamily),
        typeof(ProfileBadge), new PropertyMetadata(new FontFamily("Segoe MDL2 Assets"), (d, _) => ((ProfileBadge)d).Update()));
    public static readonly DependencyProperty GlyphWeightProperty = DependencyProperty.Register(nameof(GlyphWeight), typeof(FontWeight),
        typeof(ProfileBadge), new PropertyMetadata(FontWeights.Normal, (d, _) => ((ProfileBadge)d).Update()));
    public static readonly DependencyProperty SecondaryProperty = DependencyProperty.Register(nameof(Secondary), typeof(string),
        typeof(ProfileBadge), new PropertyMetadata(null, (d, _) => ((ProfileBadge)d).Update()));
    public static readonly DependencyProperty GlyphSizeProperty = DependencyProperty.Register(nameof(GlyphSize), typeof(double),
        typeof(ProfileBadge), new PropertyMetadata(12.0, (d, _) => ((ProfileBadge)d).Update()));

    private readonly TextBlock glyph = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
        Tag = IconRestyler.Skip   // carries its own style
    };

    // Duotone: the soft filled shape under the outline.
    private readonly TextBlock back = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
        Opacity = 0.3,
        Tag = IconRestyler.Skip
    };

    public ProfileBadge()
    {
        var layers = new Grid();
        layers.Children.Add(back);
        layers.Children.Add(glyph);
        Child = layers;
        SnapsToDevicePixels = true;
        Update();
    }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public ImageSource? Image { get => (ImageSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public FontFamily GlyphFont { get => (FontFamily)GetValue(GlyphFontProperty); set => SetValue(GlyphFontProperty, value); }
    public FontWeight GlyphWeight { get => (FontWeight)GetValue(GlyphWeightProperty); set => SetValue(GlyphWeightProperty, value); }
    public string? Secondary { get => (string?)GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }
    public double GlyphSize { get => (double)GetValue(GlyphSizeProperty); set => SetValue(GlyphSizeProperty, value); }

    private void Update()
    {
        if (Image is { } picture)
        {
            // The picture fills the shape (cropped to it); the glyph steps aside.
            Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill };
            glyph.Visibility = back.Visibility = Visibility.Collapsed;
        }
        else
        {
            Background = Fill;
            glyph.Text = Glyph ?? "";
            glyph.FontSize = GlyphSize;
            glyph.FontFamily = GlyphFont;
            glyph.FontWeight = GlyphWeight;
            glyph.Visibility = Visibility.Visible;
            back.Text = Secondary ?? "";
            back.FontSize = GlyphSize;
            back.FontFamily = GlyphFont;
            back.Visibility = Secondary is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
