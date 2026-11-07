using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SwitchMonitor.App.Services;

namespace SwitchMonitor.App;

/// <summary>One line of the notification-area menu: its icon, its name, what it does; a line with a switch shows its state.</summary>
public sealed record TrayMenuItem(string Label, string Glyph, Action Click, Func<bool>? IsOn = null)
{
    public bool HasSwitch => IsOn is not null;
    public bool On => IsOn?.Invoke() ?? false;
}

/// <summary>
/// The menu of a notification-area icon, drawn like the brightness panel and opened in the same corner: bottom right of
/// the work area, with the same gap to the taskbar. It always sits above the taskbar, so it never covers the icons.
/// It closes when clicked outside, when another window takes the focus, or when a line is chosen.
/// </summary>
public partial class TrayMenu : Window
{
    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };
    private readonly OutsideClicks outsideClicks;
    private bool closing;
    private DateTime hiddenAt = DateTime.MinValue;

    public TrayMenu()
    {
        InitializeComponent();
        outsideClicks = new OutsideClicks(() => [new System.Windows.Interop.WindowInteropHelper(this).Handle], Close_);
        Deactivated += (_, _) => Close_();
        SourceInitialized += (_, _) => FitLook();
        Theme.Changed += FitLook;
        SizeChanged += (_, _) => Theme.RoundWindow(this, Theme.PanelCornerRadius(Translucent), Translucent);
        Closed += (_, _) => { Theme.Changed -= FitLook; outsideClicks.Dispose(); };
    }

    private static bool Translucent => Theme.Current.Appearance == Core.Settings.Appearance.Translucent;

    /// <summary>Opens the menu with these lines, or closes it when it is open. A click that just closed it does not reopen it.</summary>
    public void Toggle(IReadOnlyList<TrayMenuItem> items)
    {
        if (IsVisible && !closing) { Close_(); return; }
        if (DateTime.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(350)) return;
        List.ItemsSource = items;
        closing = false;
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Slide.BeginAnimation(TranslateTransform.YProperty, null);
        Slide.Y = 14;
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
        Activate();
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        outsideClicks.Start();
    }

    private void Close_()
    {
        if (!IsVisible || closing) return;
        closing = true;
        outsideClicks.Stop();
        hiddenAt = DateTime.UtcNow;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!closing) return;   // opened again meanwhile
            closing = false;
            Hide();
        };
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Slide.Y, 12, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>For the menu self-test only.</summary>
    internal void Close_ForTest() => Close_();

    private void ItemClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrayMenuItem item) return;
        Close_();
        item.Click();
    }

    /// <summary>Bottom right of the work area, just above the taskbar: where the brightness panel opens.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double inset = Translucent ? 12 : 4;   // the card sits at the same spot with or without its transparent margin
        Left = area.Right - ActualWidth - inset;
        Top = area.Bottom - ActualHeight - inset;
    }

    private void FitLook()
    {
        bool translucent = Translucent;
        Card.Margin = new Thickness(translucent ? 0 : 8);
        Card.BorderBrush = new SolidColorBrush(translucent ? Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        Theme.FitWindow(this, translucent);
        double radius = Theme.PanelCornerRadius(translucent);   // square on Windows 10 in Translucent
        Card.CornerRadius = new CornerRadius(radius);
        GlowRect.RadiusX = GlowRect.RadiusY = Math.Max(0, radius - 1);
        Theme.RoundWindow(this, radius, translucent);
        Theme.NoShowAnimation(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (IsVisible) Place();
    }
}
