using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwitchMonitor.App.Services;

/// <summary>Shared look of the two pairing pop-ups: the app's dark popup, a title, a hint, the content and the buttons.</summary>
internal abstract class PairWindow : Window
{
    protected PairWindow(string title)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = (Brush)FindResource("PopupBrush");
        Foreground = (Brush)FindResource("TextBrush");
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;
        SourceInitialized += (_, _) => Theme.UseDarkCaption(this);
    }

    protected Brush Muted => (Brush)FindResource("MutedBrush");

    protected static TextBlock Text(string text, double size, FontWeight weight, Brush? brush = null, Thickness? margin = null, bool center = true) => new()
    {
        Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, MaxWidth = 320,
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left, HorizontalAlignment = center ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        Foreground = brush ?? Brushes.White, Margin = margin ?? new Thickness(0)
    };
}

/// <summary>
/// On the computer that starts the pairing: asks for the code the other computer is showing. The window returns the code typed
/// (six digits) or nothing when cancelled.
/// </summary>
internal sealed class PairCodeWindow : PairWindow
{
    private readonly TextBox box;

    public string? Code { get; private set; }

    /// <summary>The number of this connection: it must be the same one the other computer shows when it asks to allow it.</summary>
    public void ShowVerification(string number)
    {
        status.Foreground = Muted;
        status.Text = Loc.T($"Check that the other computer shows {number}.");
    }

    /// <param name="pair">Pairs with the typed code and returns the reason it failed, or null when it worked.</param>
    public PairCodeWindow(string otherName, Func<string, Task<string?>>? pair = null) : base("Pair with " + otherName)
    {
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 300 };
        panel.Children.Add(Text($"Pair with {otherName}", 17, FontWeights.SemiBold, null, null, true));
        panel.Children.Add(Text($"{otherName} is showing a 6-digit code. Type it here: it pairs by itself.", 13, FontWeights.Normal, Muted, new Thickness(0, 6, 0, 18), true));
        // The app's text box is 36 px tall: large digits need a taller one or they are cut off and look empty.
        box = new TextBox
        {
            MaxLength = 6, FontSize = 32, FontFamily = new FontFamily("Consolas, Segoe UI"), FontWeight = FontWeights.SemiBold,
            Height = 68, Padding = new Thickness(8, 0, 8, 0), Width = 250, HorizontalAlignment = HorizontalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White, CaretBrush = Brushes.White
        };
        AutomationProperties_SetName(box, "Pairing code");
        panel.Children.Add(box);

        // No Pair button: the sixth digit pairs by itself.
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        panel.Children.Add(cancel);
        Content = panel;

        status = Text("", 13, FontWeights.Normal, Muted, new Thickness(0, 14, 0, 0));
        panel.Children.Insert(panel.Children.Count - 1, status);

        box.TextChanged += async (_, _) =>
        {
            string digits = new string(box.Text.Where(char.IsDigit).ToArray());
            if (digits != box.Text) { box.Text = digits; box.CaretIndex = digits.Length; return; }
            if (digits.Length != 6 || prefilling || busy) return;
            Code = digits;
            if (pair is null) { DialogResult = true; return; }
            busy = true;
            box.IsEnabled = false;
            status.Foreground = Muted;
            status.Text = "Pairing…";
            string? error = await pair(digits);
            if (error is null)
            {
                // The sync is done: say so for a moment, then close.
                status.Foreground = Brushes.White;
                status.FontSize = 18;
                status.FontWeight = FontWeights.SemiBold;
                status.Text = "Connected!";
                await Task.Delay(1300);
                DialogResult = true;
                return;
            }
            status.Foreground = (Brush)FindResource("WarningTextBrush");
            status.Text = error;
            busy = false;
            box.IsEnabled = true;
            box.Text = "";
            box.Focus();
        };
        Loaded += (_, _) => { Activate(); box.Focus(); };
    }

    private readonly TextBlock status;
    private bool busy;

    private bool prefilling;

    /// <summary>Types digits into the box without pairing (the picture test).</summary>
    internal void Prefill(string digits) { prefilling = true; box.Text = digits; prefilling = false; }

    private static void AutomationProperties_SetName(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}

/// <summary>
/// On the computer that was asked: shows who wants to pair and the code, large, with the time left. It closes by itself when
/// the code is used or expires; Cancel stops the code.
/// </summary>
internal sealed class PairShowWindow : PairWindow
{
    private readonly DispatcherTimer clock;
    private readonly Func<DateTimeOffset?> expires;
    private readonly TextBlock countdown;

    public PairShowWindow(string otherName, string code, Func<DateTimeOffset?> expires, Action cancel) : base("Pairing request")
    {
        this.expires = expires;
        Topmost = true;
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 300 };
        panel.Children.Add(Text("Pairing request", 12, FontWeights.SemiBold, Muted));
        panel.Children.Add(Text($"{otherName} wants to pair", 17, FontWeights.SemiBold, null, new Thickness(0, 4, 0, 0)));
        panel.Children.Add(Text($"Type this code on {otherName}. Once paired, the two computers can control each other's displays.", 13, FontWeights.Normal, Muted, new Thickness(0, 6, 0, 14)));
        panel.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 10, 18, 10), Background = (Brush)FindResource("RaisedBrush"), HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = code, FontSize = 38, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Consolas, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White
            }
        });
        countdown = Text("", 12, FontWeights.Normal, Muted, new Thickness(0, 8, 0, 0));
        panel.Children.Add(countdown);
        var close = new Button { Content = "Cancel", IsCancel = true, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        close.Click += (_, _) => { cancel(); Close(); };
        panel.Children.Add(close);
        Content = panel;

        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Tick();
        Closed += (_, _) => clock.Stop();
        Loaded += (_, _) => Activate();
    }

    private void Tick()
    {
        if (expires() is not { } until || until <= DateTimeOffset.Now) { Close(); return; }   // used up, cancelled or expired
        countdown.Text = $"Valid for {Math.Max(0, (int)(until - DateTimeOffset.Now).TotalSeconds)} s.";
    }
}

/// <summary>
/// On a computer that is shown a device asking to join "My devices": who it is and where it is, with Allow and Don't allow.
/// No answer in a minute counts as no.
/// </summary>
internal sealed class EnrollApproveWindow : PairWindow
{
    private readonly DispatcherTimer clock;
    private DateTime until = DateTime.UtcNow.AddSeconds(60);
    private readonly TextBlock countdown;

    public bool Allowed { get; private set; }

    public EnrollApproveWindow(string name, string kind, string address, string verification = "", bool pairing = false) : base(pairing ? "Pair a device" : "Add a device")
    {
        Topmost = true;
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 320 };
        panel.Children.Add(Text(pairing ? "Pair a device" : "Add a device", 12, FontWeights.SemiBold, Muted));
        panel.Children.Add(Text(pairing ? $"{name} wants to pair with this computer" : $"{name} wants to join your devices", 17, FontWeights.SemiBold, null, new Thickness(0, 4, 0, 0)));
        panel.Children.Add(Text(kind == "phone"
            ? $"It is a phone ({address}) and typed the code of this computer. Only allow it if it is yours."
            : $"It is a computer ({address}) and typed the code of this computer. Only allow it if it is yours.",
            13, FontWeights.Normal, Muted, new Thickness(0, 6, 0, 14)));
        if (verification.Length > 0)
        {
            // The same number must show on the other device: if it does not, somebody is in between.
            panel.Children.Add(Text("Verification number", 12, FontWeights.Normal, Muted));
            panel.Children.Add(Text(verification, 34, FontWeights.SemiBold, null, new Thickness(0, 0, 0, 4)));
            panel.Children.Add(Text("It must be the same number the other device shows. If it is not, press Don't allow.", 12, FontWeights.Normal, Muted, new Thickness(0, 0, 0, 14)));
        }
        countdown = Text("", 12, FontWeights.Normal, Muted, new Thickness(0, 0, 0, 14));
        panel.Children.Add(countdown);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        var allow = new Button { Content = "Allow", MinWidth = 110, Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
        var deny = new Button { Content = "Don't allow", MinWidth = 110, IsCancel = true };
        allow.Click += (_, _) => { Allowed = true; Close(); };
        deny.Click += (_, _) => Close();
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        panel.Children.Add(buttons);
        Content = panel;

        clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        Tick();
        Closed += (_, _) => clock.Stop();
        Loaded += (_, _) => Activate();
    }

    private void Tick()
    {
        int left = (int)(until - DateTime.UtcNow).TotalSeconds;
        if (left <= 0) { Close(); return; }
        countdown.Text = $"It is refused in {left} s if you do nothing.";
    }
}

/// <summary>
/// Joins another computer's group of devices: pick the computer, type the 6-digit code it shows (or its authenticator app
/// makes); it joins by itself with the sixth digit and says "Connected!".
/// </summary>
internal sealed class JoinGroupWindow : PairWindow
{
    private readonly TextBox box;
    private readonly ComboBox pick;
    private readonly TextBlock status;
    private bool busy;

    public bool Joined { get; private set; }

    /// <summary>The number of this connection: it must be the same one the other computer shows when it asks to allow it.</summary>
    public void ShowVerification(string number)
    {
        status.Foreground = Muted;
        status.Text = Loc.T($"Waiting for the other computer to allow it… It must show {number}.");
    }

    /// <param name="join">Joins that computer's group with the code and returns the reason it failed, or null when it worked.</param>
    public JoinGroupWindow(IReadOnlyList<Core.Remote.DiscoveredComputer> nearby, Func<Core.Remote.DiscoveredComputer, string, Task<string?>> join) : base("Join a group")
    {
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 320 };
        panel.Children.Add(Text("Join a group of devices", 17, FontWeights.SemiBold));
        panel.Children.Add(Text("Pick the computer whose group you want to join, and type the code it shows in Settings > Devices > My devices.", 13, FontWeights.Normal, Muted, new Thickness(0, 6, 0, 14)));
        pick = new ComboBox { ItemsSource = nearby, DisplayMemberPath = nameof(Core.Remote.DiscoveredComputer.Name), SelectedIndex = nearby.Count > 0 ? 0 : -1, Width = 250, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(pick);
        box = new TextBox
        {
            MaxLength = 6, FontSize = 32, FontFamily = new FontFamily("Consolas, Segoe UI"), FontWeight = FontWeights.SemiBold,
            Height = 68, Padding = new Thickness(8, 0, 8, 0), Width = 250, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White, CaretBrush = Brushes.White
        };
        System.Windows.Automation.AutomationProperties.SetName(box, "Group code");
        panel.Children.Add(box);
        status = Text(nearby.Count == 0 ? "No computer with SwitchMonitor was found on this network." : "", 13, FontWeights.Normal, Muted, new Thickness(0, 14, 0, 0));
        panel.Children.Add(status);
        panel.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) });
        Content = panel;

        box.TextChanged += async (_, _) =>
        {
            string digits = new string(box.Text.Where(char.IsDigit).ToArray());
            if (digits != box.Text) { box.Text = digits; box.CaretIndex = digits.Length; return; }
            if (digits.Length != 6 || busy || pick.SelectedItem is not Core.Remote.DiscoveredComputer target) return;
            busy = true;
            box.IsEnabled = pick.IsEnabled = false;
            status.Foreground = Muted;
            status.Text = "Waiting for the other computer to allow it…";
            string? error = await join(target, digits);
            if (error is null)
            {
                status.Foreground = Brushes.White;
                status.FontSize = 18;
                status.FontWeight = FontWeights.SemiBold;
                status.Text = "Connected!";
                Joined = true;
                await Task.Delay(1300);
                DialogResult = true;
                return;
            }
            status.Foreground = (Brush)FindResource("WarningTextBrush");
            status.Text = error;
            busy = false;
            box.IsEnabled = pick.IsEnabled = true;
            box.Text = "";
            box.Focus();
        };
        Loaded += (_, _) => { Activate(); box.Focus(); };
    }
}
