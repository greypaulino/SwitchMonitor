using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App;

/// <summary>Picks any color; <see cref="Color"/> is "#RRGGBB" and binds two-way.</summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorPicker),
        new FrameworkPropertyMetadata("#8A8A8A", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorPicker)d).FromColor()));

    private double hue, saturation, value;
    private bool updating;

    public ColorPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => FromColor();
        Field.SizeChanged += (_, _) => PlaceThumbs();
        HueStrip.SizeChanged += (_, _) => PlaceThumbs();
    }

    public string Color
    {
        get => (string)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private void FromColor()
    {
        if (updating) return;
        var color = Theme.Parse(Color);
        var (h, s, v) = Theme.ToHsv(color);
        if (s > 0.001) hue = h;   // a gray keeps the hue the strip shows
        saturation = s;
        value = v;
        Show(color);
    }

    private void Show(System.Windows.Media.Color color)
    {
        HueFill.Background = new SolidColorBrush(Theme.FromHsv(hue, 1, 1));
        Preview.Fill = new SolidColorBrush(color);
        if (!HexBox.IsKeyboardFocused) HexBox.Text = Theme.ToHex(color);
        PlaceThumbs();
    }

    private void PlaceThumbs()
    {
        Canvas.SetLeft(FieldThumb, saturation * Field.ActualWidth - FieldThumb.Width / 2);
        Canvas.SetTop(FieldThumb, (1 - value) * Field.ActualHeight - FieldThumb.Height / 2);
        Canvas.SetLeft(HueThumb, hue / 360 * HueStrip.ActualWidth - HueThumb.Width / 2);
    }

    private void Commit()
    {
        var color = Theme.FromHsv(hue, saturation, value);
        updating = true;
        try { Color = Theme.ToHex(color); }
        finally { updating = false; }
        Show(color);
    }

    private void FieldDown(object sender, MouseButtonEventArgs e)
    {
        Field.CaptureMouse();
        FieldAt(e.GetPosition(Field));
    }

    private void FieldMove(object sender, MouseEventArgs e)
    {
        if (Field.IsMouseCaptured) FieldAt(e.GetPosition(Field));
    }

    private void FieldAt(Point point)
    {
        saturation = Math.Clamp(point.X / Math.Max(1, Field.ActualWidth), 0, 1);
        value = 1 - Math.Clamp(point.Y / Math.Max(1, Field.ActualHeight), 0, 1);
        Commit();
    }

    private void HueDown(object sender, MouseButtonEventArgs e)
    {
        HueStrip.CaptureMouse();
        HueAt(e.GetPosition(HueStrip));
    }

    private void HueMove(object sender, MouseEventArgs e)
    {
        if (HueStrip.IsMouseCaptured) HueAt(e.GetPosition(HueStrip));
    }

    private void HueAt(Point point)
    {
        hue = Math.Clamp(point.X / Math.Max(1, HueStrip.ActualWidth), 0, 0.9999) * 360;
        if (saturation < 0.05) saturation = 0.6;   // moving the hue of a gray shows a color
        if (value < 0.15) value = 0.75;
        Commit();
    }

    private void ReleaseMouse(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void HexKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) HexCommitted(sender, e);
    }

    private void HexCommitted(object sender, RoutedEventArgs e)
    {
        if (InterfaceSettings.NormalizeColor(HexBox.Text) is { } hex)
        {
            Color = hex;
            HexBox.Text = hex;
        }
        else HexBox.Text = Color;
    }
}
