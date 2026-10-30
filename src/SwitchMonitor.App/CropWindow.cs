using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

namespace SwitchMonitor.App;

/// <summary>
/// Crop step of Import: the picture with a square frame to drag and size; outside it is dimmed, a preview shows the icon
/// as it will look. "Use this part" returns the square scaled to the icon size.
/// </summary>
public sealed class CropWindow : Window
{
    private const double View = 420;
    private readonly BitmapSource picture;
    private readonly double scale;           // view pixels per picture pixel
    private readonly Canvas canvas = new();
    private readonly Border frame = new();
    private readonly ShapePath shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Slider size = new() { Minimum = 16, SmallChange = 4, LargeChange = 32 };
    private readonly Border preview = new() { Width = 64, Height = 64, CornerRadius = new CornerRadius(14) };
    private Point square;                    // top-left of the square, in view pixels
    private Point? grab;

    public BitmapSource? Result { get; private set; }

    public CropWindow(BitmapSource picture, Brush accent)
    {
        this.picture = picture;
        Title = "Crop the icon";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)FindResource("PopupBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SourceInitialized += (_, _) => Theme.UseDarkCaption(this);

        scale = Math.Min(View / picture.PixelWidth, View / picture.PixelHeight);
        double width = picture.PixelWidth * scale, height = picture.PixelHeight * scale;
        canvas.Width = width;
        canvas.Height = height;
        canvas.ClipToBounds = true;
        canvas.Background = Brushes.Transparent;
        canvas.Children.Add(new Image { Source = picture, Width = width, Height = height, Stretch = Stretch.Fill });
        canvas.Children.Add(shade);
        frame.BorderBrush = Brushes.White;
        frame.BorderThickness = new Thickness(2);
        frame.Background = Brushes.Transparent;
        frame.Cursor = Cursors.SizeAll;
        frame.Child = new Border { BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), BorderThickness = new Thickness(1) };
        canvas.Children.Add(frame);

        double side = Math.Min(width, height);
        size.Maximum = side;
        size.Value = side;
        square = new Point((width - side) / 2, (height - side) / 2);
        size.ValueChanged += (_, _) => Place(square, keepCenter: true);
        frame.MouseLeftButtonDown += (_, e) => { grab = e.GetPosition(canvas) - (Vector)square; frame.CaptureMouse(); };
        frame.MouseMove += (_, e) => { if (grab is { } g && frame.IsMouseCaptured) Place(e.GetPosition(canvas) - (Vector)g, keepCenter: false); };
        frame.MouseLeftButtonUp += (_, _) => { grab = null; frame.ReleaseMouseCapture(); };

        var use = new Button { Content = "Use this part", Style = (Style)FindResource("PrimaryButton"), IsDefault = true, Margin = new Thickness(6, 0, 0, 0) };
        use.Click += (_, _) => { Result = Crop(); DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true };

        var sizeRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var label = new TextBlock { Text = "Size", Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        DockPanel.SetDock(label, Dock.Left);
        sizeRow.Children.Add(label);
        sizeRow.Children.Add(size);

        preview.Background = accent;
        var side2 = new StackPanel { Margin = new Thickness(20, 0, 0, 0), Width = 150 };
        side2.Children.Add(new TextBlock { Text = "Preview", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        side2.Children.Add(preview);
        side2.Children.Add(new TextBlock
        {
            Text = $"Drag the square to choose the part to keep; the slider sets its size. It is saved at {IconSize} × {IconSize} px.",
            TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("FaintBrush"), FontSize = 12, Margin = new Thickness(0, 12, 0, 0)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(use);

        var left = new StackPanel();
        left.Children.Add(new Border { Child = canvas, CornerRadius = new CornerRadius(10), ClipToBounds = true, Background = (Brush)FindResource("RaisedBrush") });
        left.Children.Add(sizeRow);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(left);
        row.Children.Add(side2);
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(row);
        root.Children.Add(buttons);
        Content = root;
        Place(square, keepCenter: false);
    }

    /// <summary>Saved icon size in pixels.</summary>
    public const int IconSize = 128;

    private void Place(Point at, bool keepCenter)
    {
        double side = size.Value;
        if (keepCenter)
        {
            double old = frame.Width is double w && !double.IsNaN(w) ? w : side;
            at = new Point(at.X + (old - side) / 2, at.Y + (old - side) / 2);
        }
        at = new Point(Math.Clamp(at.X, 0, canvas.Width - side), Math.Clamp(at.Y, 0, canvas.Height - side));
        square = at;
        frame.Width = frame.Height = side;
        Canvas.SetLeft(frame, at.X);
        Canvas.SetTop(frame, at.Y);
        shade.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, canvas.Width, canvas.Height)), new RectangleGeometry(new Rect(at, new Size(side, side))));
        preview.Child = null;
        if (Crop() is { } icon) preview.Background = new ImageBrush(icon) { Stretch = Stretch.UniformToFill };
    }

    private BitmapSource? Crop()
    {
        int x = (int)Math.Round(square.X / scale), y = (int)Math.Round(square.Y / scale);
        int side = (int)Math.Round(size.Value / scale);
        side = Math.Min(side, Math.Min(picture.PixelWidth - x, picture.PixelHeight - y));
        if (side < 1) return null;
        var cropped = new CroppedBitmap(picture, new Int32Rect(x, y, side, side));
        BitmapSource result = cropped;
        if (side != IconSize)
        {
            double factor = (double)IconSize / side;
            var scaled = new TransformedBitmap(cropped, new ScaleTransform(factor, factor));
            result = scaled;
        }
        result.Freeze();
        return result;
    }
}
