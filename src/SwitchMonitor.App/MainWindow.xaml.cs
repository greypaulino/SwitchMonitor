using System.Windows;
using System.Windows.Input;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Hotkeys;

namespace SwitchMonitor.App;

/// <summary>Main window. Behavior lives in the view models; this only records shortcuts on the Shortcuts tab.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) => Record(e);
        Deactivated += (_, _) => (DataContext as MainViewModel)?.ShortcutEditor?.CancelCapture();
        SourceInitialized += (_, _) =>
        {
            UseDarkTitleBar();
            FitFrame();
        };
        StateChanged += (_, _) => FitFrame();
        Theme.Changed += FitFrame;
        Theme.Changed += FitPointerLight;
        Closed += (_, _) =>
        {
            Theme.Changed -= FitFrame;
            Theme.Changed -= FitPointerLight;
        };
        SetUpMotion();
    }

    // ---- Window buttons drawn in the header (the title bar is part of the interface) ----

    private void MinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();   // the app keeps running in the tray

    /// <summary>
    /// The window is its frame: rounded with a hairline edge, no shadow. Translucent: Windows blurs the whole window
    /// rectangle, so the corners tighten to the point where the blur cannot show past them. Maximized: square.
    /// </summary>
    private void FitFrame()
    {
        bool maximized = WindowState == WindowState.Maximized;
        bool translucent = Theme.Current.Appearance == Core.Settings.Appearance.Translucent;
        // Maximized, Windows may place the window a few pixels past the screen edges (its resize border): the part of
        // the header under the pointer was then not where it was drawn, and the header buttons ignored clicks. Pull the
        // frame back inside the monitor's work area by exactly that overhang.
        Frame.Margin = maximized ? MaximizedInset() : new Thickness(0);
        double radius = maximized ? 0 : translucent ? Theme.TranslucentCornerRadius : Theme.CornerRadius;
        Chrome.CornerRadius = new CornerRadius(radius);
        Chrome.BorderThickness = new Thickness(maximized ? 0 : 1);
        Chrome.BorderBrush = new System.Windows.Media.SolidColorBrush(translucent
            ? System.Windows.Media.Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)    // a brighter edge reads over the blur
            : System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        Theme.FitWindow(this, translucent);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    /// <summary>How far the maximized window reaches past its monitor's work area on each side, in this window's units.</summary>
    private Thickness MaximizedInset()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window)) return new Thickness(0);
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return new Thickness(0);
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
        return new Thickness(
            Math.Max(0, info.Work.Left - window.Left) / scale.M11, Math.Max(0, info.Work.Top - window.Top) / scale.M22,
            Math.Max(0, window.Right - info.Work.Right) / scale.M11, Math.Max(0, window.Bottom - info.Work.Bottom) / scale.M22);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor; public NativeRect Work; public uint Flags; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    /// <summary>Dark caption to match the window (attribute 20; 19 on Windows 10 builds before 20H1). Ignored where unsupported.</summary>
    private void UseDarkTitleBar()
    {
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(handle, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(handle, 19, ref on, sizeof(int));
    }

    /// <summary>The ⋯ and "Add profile" buttons open their menu on a normal click.</summary>
    private void ShowOptions(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ContextMenu: { } menu } button) return;
        menu.DataContext = button.DataContext ?? DataContext;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // ---- Motion: the tab pill slides, views settle in, a soft light follows the pointer ----

    /// <summary>Windows' "show animations" setting; off, everything changes at once.</summary>
    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private static readonly System.Windows.Media.Animation.IEasingFunction EaseOut =
        new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

    private static System.Windows.Media.Animation.DoubleAnimation To(double value, int milliseconds) =>
        new(value, TimeSpan.FromMilliseconds(Animate ? milliseconds : 0)) { EasingFunction = EaseOut };

    private bool navPlaced;
    private int lastViewOrder;

    /// <summary>Holds <paramref name="from"/> until <paramref name="delay"/>, then eases to <paramref name="to"/>.</summary>
    private static System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames Delayed(double from, double to, TimeSpan delay, int milliseconds)
    {
        var frames = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(from, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new System.Windows.Media.Animation.DiscreteDoubleKeyFrame(from, System.Windows.Media.Animation.KeyTime.FromTimeSpan(delay)));
        frames.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(to,
            System.Windows.Media.Animation.KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(milliseconds)), EaseOut));
        return frames;
    }

    /// <summary>The cards of a view that just appeared rise into place one after another (the first eight).</summary>
    private void StaggerCards(FrameworkElement view)
    {
        if (!Animate) return;
        var cardStyle = TryFindResource("Card") as Style;
        var found = new List<System.Windows.Controls.Border>();
        void Walk(DependencyObject parent)
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent) && found.Count < 8; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is UIElement { IsVisible: false }) continue;
                if (child is System.Windows.Controls.Border border && border.Style == cardStyle) { found.Add(border); continue; }
                Walk(child);
            }
        }
        Walk(view);
        for (int i = 0; i < found.Count; i++)
        {
            var card = found[i];
            var start = TimeSpan.FromMilliseconds(40 * i);
            if (card.RenderTransform is not System.Windows.Media.TranslateTransform move || move.IsFrozen)
                card.RenderTransform = move = new System.Windows.Media.TranslateTransform();
            card.BeginAnimation(OpacityProperty, Delayed(0, 1, start, 220));
            move.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, Delayed(12, 0, start, 260));
        }
    }

    private void SetUpMotion()
    {
        foreach (var tab in NavTabs.Children.OfType<System.Windows.Controls.StackPanel>().SelectMany(p => p.Children.OfType<System.Windows.Controls.RadioButton>()))
        {
            tab.Checked += (_, _) => PlaceNavIndicator(animate: true);
            tab.Unchecked += (_, _) => Dispatcher.InvokeAsync(() => PlaceNavIndicator(animate: true),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        NavTabs.SizeChanged += (_, _) => PlaceNavIndicator(animate: navPlaced);
        NavTabs.Loaded += (_, _) => PlaceNavIndicator(animate: false);

        // Views slide in from the side of the tab that was chosen (left or right of the previous one), and their cards
        // follow one after another; the gear and the wizard come up from below.
        var views = new FrameworkElement[] { DisplaysView, ProfilesView, ShortcutsView, SettingsView, LearnView };
        foreach (var view in views)
        {
            var shift = new System.Windows.Media.TranslateTransform();
            view.RenderTransform = shift;
            view.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is not true || !Animate) return;
                int order = Array.IndexOf(views, view);
                int direction = Math.Sign(order - lastViewOrder);
                lastViewOrder = order;
                bool sideways = order <= 2 && direction != 0;
                view.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = EaseOut });
                shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(sideways ? 28 * direction : 0, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = EaseOut });
                shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(sideways ? 0 : 10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = EaseOut });
                Dispatcher.BeginInvoke(() => StaggerCards(view), System.Windows.Threading.DispatcherPriority.Loaded);
            };
        }

        Chrome.MouseMove += (_, e) => MovePointerLight(e.GetPosition(PointerLight));
        Chrome.MouseLeave += (_, _) => ShowPointerLight(false);
        SizeChanged += (_, _) => cardsReadAt = DateTime.MinValue;
    }

    private bool pointerLightShown;
    private readonly List<System.Windows.Controls.Border> cards = [];
    private DateTime cardsReadAt = DateTime.MinValue;

    /// <summary>
    /// Moves the light. It sits under everything (window ground and card backgrounds, which are see-through), so it only
    /// brightens backgrounds: never text, icons or controls drawn on top.
    /// </summary>
    private void MovePointerLight(Point at)
    {
        if (!Theme.Current.PointerLight) return;
        PointerLightBrush.Center = PointerLightBrush.GradientOrigin = at;
        if (!pointerLightShown) ShowPointerLight(true);
    }

    private void Collect(DependencyObject parent, Style? cardStyle)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: false }) continue;
            if (child is System.Windows.Controls.Border border && cardStyle is not null && border.Style == cardStyle)
                cards.Add(border);
            Collect(child, cardStyle);
        }
    }

    private static System.Windows.Controls.ScrollViewer? FindScroller(DependencyObject element)
    {
        for (var at = System.Windows.Media.VisualTreeHelper.GetParent(element); at is not null; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
            if (at is System.Windows.Controls.ScrollViewer scroller) return scroller;
        return null;
    }

    private static double Distance(Point point, Rect rect)
    {
        double dx = Math.Max(Math.Max(rect.Left - point.X, 0), point.X - rect.Right);
        double dy = Math.Max(Math.Max(rect.Top - point.Y, 0), point.Y - rect.Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void ShowPointerLight(bool show)
    {
        pointerLightShown = show && Theme.Current.PointerLight;
        var fade = To(pointerLightShown ? 1 : 0, pointerLightShown ? 240 : 400);
        PointerLight.BeginAnimation(OpacityProperty, fade);
    }

    private void FitPointerLight()
    {
        if (!Theme.Current.PointerLight) ShowPointerLight(false);
    }

    /// <summary>Moves the light pill under the current tab (it fades out on the Settings view, where no tab is current).</summary>
    private void PlaceNavIndicator(bool animate)
    {
        var current = NavTabs.Children.OfType<System.Windows.Controls.StackPanel>()
            .SelectMany(p => p.Children.OfType<System.Windows.Controls.RadioButton>())
            .FirstOrDefault(t => t.IsChecked == true && t.ActualWidth > 0);
        int length = animate ? 260 : 0;
        if (current is null)
        {
            NavIndicator.BeginAnimation(OpacityProperty, To(0, animate ? 160 : 0));
            return;
        }
        double x = current.TranslatePoint(new Point(0, 0), NavTabs).X;
        double width = Math.Max(0, current.ActualWidth - 4);
        bool wasHidden = NavIndicator.Opacity < 0.5 || !navPlaced;
        if (wasHidden)
        {
            // Appearing (back from Settings): no slide from a stale place, just a fade in where it belongs.
            NavIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            NavIndicator.BeginAnimation(WidthProperty, null);
            NavIndicatorShift.X = x;
            NavIndicator.Width = width;
            NavIndicator.BeginAnimation(OpacityProperty, To(1, animate ? 160 : 0));
        }
        else
        {
            NavIndicatorShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, To(x, length));
            NavIndicator.BeginAnimation(WidthProperty, To(width, length));
            NavIndicator.BeginAnimation(OpacityProperty, To(1, 120));
        }
        navPlaced = true;
    }

    private void Record(KeyEventArgs e)
    {
        if (DataContext is not MainViewModel { IsShortcutsTab: true, ShortcutEditor: { IsCapturing: true } editor }) return;
        e.Handled = true;
        var key = e.Key switch
        {
            Key.System => e.SystemKey,          // keys pressed with Alt
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        var modifiers = HotkeyModifiers.None;
        var held = Keyboard.Modifiers;
        if (held.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Ctrl;
        if (held.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (held.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (held.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Win;

        if (modifiers == HotkeyModifiers.None && key == Key.Escape)
        {
            editor.CancelCapture();
            return;
        }
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0 || HotkeyChord.IsModifierKey(virtualKey))
        {
            editor.ShowPending(modifiers);
            return;
        }
        editor.Capture(new HotkeyChord(modifiers, virtualKey));
    }
}
