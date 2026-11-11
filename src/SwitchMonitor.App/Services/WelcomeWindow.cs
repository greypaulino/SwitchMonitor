using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The welcome tour, three slides: a message with the logo, the general categories of what the program does (icons, a few
/// examples), and "Let's start!". It opens the first time the program runs on a computer, and again when features were added
/// since the person last looked (those categories carry a "New" mark). Left/Right keys and the buttons move; Esc closes.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private const int SlideCount = 3;
    private readonly int seen;
    private readonly bool firstTime;
    private readonly IReadOnlyList<FeatureInfo> news;
    private readonly ContentControl stage = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
    private readonly StackPanel dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button back = new(), next = new(), skip = new();
    private int index;

    /// <param name="news">Features added since the person last looked (all of them the first time).</param>
    public WelcomeWindow(IReadOnlyList<FeatureInfo> news, bool firstTime)
    {
        this.news = news;
        this.firstTime = firstTime;
        seen = news.Count == 0 ? Features.Latest : news.Min(f => f.Order) - 1;
        Title = firstTime ? "Welcome to SwitchMonitor" : "What's new in SwitchMonitor";
        Width = 820; Height = 600;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)FindResource("PopupBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/monitor-switch.ico"));
        SourceInitialized += (_, _) => Theme.UseDarkCaption(this);

        back.Content = "Back"; back.MinWidth = 90; back.Click += (_, _) => Go(index - 1);
        next.MinWidth = 130; next.Style = (Style)FindResource("PrimaryButton"); next.Click += (_, _) => { if (index >= SlideCount - 1) Close(); else Go(index + 1); };
        skip.Content = "Skip"; skip.Style = (Style)FindResource("QuietButton"); skip.Click += (_, _) => Close();

        // The buttons sit together in the middle, with room above them.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(26, 10, 26, 30) };
        skip.Margin = new Thickness(0, 0, 14, 0);
        back.Margin = new Thickness(0, 0, 12, 0);
        buttons.Children.Add(skip); buttons.Children.Add(back); buttons.Children.Add(next);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var clip = new Grid { ClipToBounds = true, Margin = new Thickness(36, 34, 36, 10) };
        clip.Children.Add(stage);
        Grid.SetRow(clip, 0);
        dots.Margin = new Thickness(0, 14, 0, 22);
        Grid.SetRow(dots, 1);
        Grid.SetRow(buttons, 2);
        root.Children.Add(clip); root.Children.Add(dots); root.Children.Add(buttons);
        Content = root;

        for (int i = 0; i < SlideCount; i++)
        {
            int target = i;
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(4, 0, 4, 0), Cursor = Cursors.Hand, Background = (Brush)FindResource("FaintBrush") };
            dot.MouseLeftButtonUp += (_, _) => Go(target);
            dots.Children.Add(dot);
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Right) { if (index < SlideCount - 1) Go(index + 1); }
            else if (e.Key == Key.Left) Go(index - 1);
            else if (e.Key == Key.Escape) Close();
        };
        Show(0, 0);
    }

    private void Go(int to)
    {
        if (to < 0 || to >= SlideCount || to == index) return;
        int direction = to > index ? 1 : -1;
        index = to;
        Show(direction, 220);
    }

    private TextBlock Line(string text, double size, FontWeight weight, string brush, double maxWidth, Thickness margin) => new()
    {
        Text = Loc.T(text), FontSize = size, FontWeight = weight, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        MaxWidth = maxWidth, Margin = margin, Foreground = (Brush)FindResource(brush), HorizontalAlignment = HorizontalAlignment.Center
    };

    /// <summary>The biggest picture of monitor-switch.ico (a small one scaled up looks pixelated).</summary>
    private static System.Windows.Media.Imaging.BitmapSource LargestIcon()
    {
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(new Uri("pack://application:,,,/monitor-switch.ico"),
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
        return decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
    }

    /// <summary>Slide 1: the logo and a message.</summary>
    private UIElement Welcome()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var logo = new Image { Source = LargestIcon(), Width = 104, Height = 104, Margin = new Thickness(0, 0, 0, 28), HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        panel.Children.Add(logo);
        panel.Children.Add(Line(firstTime ? "Welcome to SwitchMonitor" : "What's new in SwitchMonitor", 28, FontWeights.SemiBold, "TextBrush", 560, new Thickness(0)));
        panel.Children.Add(Line(firstTime
            ? "One place for the brightness, inputs and picture modes of all your displays: from the keyboard, from your phone or from your other computers."
            : "A few things were added since you last looked. Their categories carry a \"New\" mark on the next page.",
            15, FontWeights.Normal, "MutedBrush", 500, new Thickness(0, 12, 0, 0)));
        if (!firstTime)
            foreach (var item in news.Take(4))
                panel.Children.Add(Line("· " + Loc.T(item.Title), 13, FontWeights.SemiBold, "TextBrush", 500, new Thickness(0, 6, 0, 0)));
        return panel;
    }

    /// <summary>Slide 2: one icon tile per category, with a few examples.</summary>
    private UIElement Categories()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(Line("All in one program", 24, FontWeights.SemiBold, "TextBrush", 560, new Thickness(0)));
        panel.Children.Add(Line("Everything below turns on with a switch in Settings > About, and each part has its own page.", 14, FontWeights.Normal, "MutedBrush", 540, new Thickness(0, 10, 0, 34)));
        var grid = new UniformGrid { Columns = 3, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var group in Features.Groups)
        {
            var glyph = new TextBlock
            {
                Text = group.Glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 26, Foreground = (Brush)FindResource("TextBrush"),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Tag = IconRestyler.Skip
            };
            var tile = new Border
            {
                Width = 60, Height = 60, CornerRadius = new CornerRadius(19), Background = (Brush)FindResource("TileBrush"),
                BorderBrush = (Brush)FindResource("CardEdgeBrush"), BorderThickness = new Thickness(1), Child = glyph, HorizontalAlignment = HorizontalAlignment.Center
            };
            var card = new StackPanel { Margin = new Thickness(10, 0, 10, 30), Width = 200 };
            card.Children.Add(tile);
            card.Children.Add(Line(group.Title, 14.5, FontWeights.SemiBold, "TextBrush", 190, new Thickness(0, 12, 0, 0)));
            card.Children.Add(Line(group.Text, 12.5, FontWeights.Normal, "MutedBrush", 190, new Thickness(0, 4, 0, 0)));
            if (!firstTime && group.HasNew(seen))
            {
                card.Children.Add(new Border
                {
                    Background = (Brush)FindResource("AccentSolidBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 1, 8, 2), Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new TextBlock { Text = Loc.T("New"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
                });
            }
            grid.Children.Add(card);
        }
        panel.Children.Add(grid);
        return panel;
    }

    /// <summary>Slide 3: let's start.</summary>
    private UIElement Start()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var glyph = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 38, Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Tag = IconRestyler.Skip
        };
        panel.Children.Add(new Border
        {
            Width = 88, Height = 88, CornerRadius = new CornerRadius(28), Background = (Brush)FindResource("TileBrush"), BorderBrush = (Brush)FindResource("CardEdgeBrush"),
            BorderThickness = new Thickness(1), Child = glyph, Margin = new Thickness(0, 0, 0, 24), HorizontalAlignment = HorizontalAlignment.Center
        });
        panel.Children.Add(Line("Let's start!", 28, FontWeights.SemiBold, "TextBrush", 520, new Thickness(0)));
        panel.Children.Add(Line("To connect your other computers and your phone, open Settings > Devices. You can see this tour again, and every feature with its switch, in Settings > About.",
            15, FontWeights.Normal, "MutedBrush", 500, new Thickness(0, 12, 0, 0)));
        return panel;
    }

    private void Show(int direction, int milliseconds)
    {
        stage.Content = index switch { 0 => Welcome(), 1 => Categories(), _ => Start() };
        back.Visibility = index == 0 ? Visibility.Collapsed : Visibility.Visible;
        next.Content = Loc.T(index >= SlideCount - 1 ? "Let's start!" : "Next");
        skip.Visibility = index >= SlideCount - 1 ? Visibility.Collapsed : Visibility.Visible;
        for (int i = 0; i < dots.Children.Count; i++)
        {
            var dot = (Border)dots.Children[i];
            dot.Background = (Brush)FindResource(i == index ? "AccentSolidBrush" : "FaintBrush");
            dot.Width = i == index ? 22 : 8;
        }
        if (milliseconds == 0) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var move = (TranslateTransform)stage.RenderTransform;
        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40 * direction, 0, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
        stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
    }
}
