using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SwitchMonitor.Wpf;

public partial class BrightnessFlyout : Window
{
    private readonly ObservableCollection<BrightnessRow> rows;
    private readonly Func<bool> isLinked;
    private readonly Func<bool> canLink;
    private readonly Action toggleLink;
    private readonly Action openSettings;
    private readonly Action<BrightnessRow, double> changeBrightness;
    private readonly Action<BrightnessRow> selectRow;
    private readonly DispatcherTimer inactivity = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer outsideClick = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private bool wantsOpen;
    private int animationVersion;

    internal BrightnessFlyout(ObservableCollection<BrightnessRow> rows, Func<bool> isLinked, Func<bool> canLink,
        Action toggleLink, Action openSettings, Action<BrightnessRow, double> changeBrightness,
        Action<BrightnessRow> selectRow)
    {
        InitializeComponent();
        this.rows = rows;
        this.isLinked = isLinked;
        this.canLink = canLink;
        this.toggleLink = toggleLink;
        this.openSettings = openSettings;
        this.changeBrightness = changeBrightness;
        this.selectRow = selectRow;
        BrightnessRows.ItemsSource = rows;
        rows.CollectionChanged += (_, _) => { if (IsVisible) Position(); RefreshLink(); };
        inactivity.Tick += (_, _) =>
        {
            inactivity.Stop();
            if (!IsMouseOver) Animate(false);
        };
        outsideClick.Tick += (_, _) => { outsideClick.Stop(); if (!IsActive) Animate(false); };
        RefreshLink();
    }

    private void Position()
    {
        LinkButton.Visibility = canLink() ? Visibility.Visible : Visibility.Collapsed;
        double contentHeight = rows.Count == 0 ? 82 : rows.Sum(row => row.Name.Contains('\n') ? 82d : 64d);
        Height = Math.Max(118, 49 + contentHeight);
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 10;
        Top = area.Bottom - Height;
    }

    public void Toggle() => Animate(!wantsOpen);
    public void Reveal()
    {
        Animate(true);
        KeepAlive();
    }

    private void Animate(bool open)
    {
        outsideClick.Stop();
        wantsOpen = open;
        int version = ++animationVersion;
        if (open)
        {
            Position();
            RefreshLink();
            if (!IsVisible)
            {
                Opacity = 0;
                CardTransform.Y = 22;
                Show();
            }
        }
        else inactivity.Stop();
        if (!IsVisible) return;
        var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };
        var slide = new DoubleAnimation
        {
            To = open ? 0 : 22,
            Duration = TimeSpan.FromMilliseconds(open ? 230 : 190),
            EasingFunction = easing
        };
        var fade = new DoubleAnimation
        {
            To = open ? 1 : 0,
            Duration = slide.Duration,
            EasingFunction = easing
        };
        fade.Completed += (_, _) =>
        {
            if (version != animationVersion) return;
            if (open) KeepAlive();
            else Hide();
        };
        CardTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
        BeginAnimation(OpacityProperty, fade);
    }

    public void KeepAlive()
    {
        if (!wantsOpen) return;
        inactivity.Stop();
        if (!IsMouseOver) inactivity.Start();
    }

    public void RefreshLink()
    {
        LinkButton.Foreground = isLinked()
            ? System.Windows.Media.Brushes.White
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150));
        LinkButton.Visibility = canLink() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is Slider { DataContext: BrightnessRow row } slider && row.Supported && IsVisible &&
            (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin))
        {
            changeBrightness(row, e.NewValue);
            KeepAlive();
        }
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrightnessRow row })
        {
            selectRow(row);
            KeepAlive();
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        toggleLink();
        RefreshLink();
        Position();
        KeepAlive();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Animate(false);
        openSettings();
    }

    private void Flyout_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var row = rows.FirstOrDefault(item => item.Active && item.Supported)
            ?? rows.FirstOrDefault(item => item.Supported);
        if (row is null) return;
        row.Value = Math.Clamp(row.Value + (e.Delta > 0 ? 1 : -1), 0, 100);
        changeBrightness(row, row.Value);
        KeepAlive();
        e.Handled = true;
    }

    private void Flyout_Deactivated(object sender, EventArgs e)
    {
        if (wantsOpen) { outsideClick.Stop(); outsideClick.Start(); }
    }

    private void Flyout_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => inactivity.Stop();
    private void Flyout_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => KeepAlive();
}
