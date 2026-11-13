using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Inputs;

namespace SwitchMonitor.App;

/// <summary>
/// The sun panel (1.x "brillo"): opens above the notification area from the sun icon, closes when something else is
/// clicked, with Esc, or after five seconds without the pointer on it. Keys work while it is open (see
/// <see cref="SunPanelViewModel"/>): + and -, digits, Enter, Esc, * (held: link) and M (held: main display).
/// </summary>
public partial class SunPanel : Window
{
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StarHold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MainHold = TimeSpan.FromSeconds(0.5);
    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    private readonly DispatcherTimer idle = new() { Interval = IdleClose };
    private readonly DispatcherTimer starTimer = new() { Interval = StarHold };
    private readonly DispatcherTimer mainTimer = new() { Interval = MainHold };
    private DateTime hiddenAt = DateTime.MinValue;
    private DateTime stepDown;
    private DateTime lastHoldStep;
    private bool starHeld;

    public SunPanel(SunPanelViewModel model)
    {
        InitializeComponent();
        DataContext = model;
        idle.Tick += (_, _) => { if (!IsMouseOver && !Model.IsTyping && !Model.IsModePending && !ModesPopup.IsMouseOver && !InputsPopup.IsMouseOver) HidePanel(); };
        starTimer.Tick += (_, _) => { starTimer.Stop(); starHeld = true; Model.Linked = !Model.Linked; };
        mainTimer.Tick += (_, _) => { mainTimer.Stop(); Model.SwitchMain(); Place(); };
        Deactivated += (_, _) => HidePanel();
        outsideClicks = new Services.OutsideClicks(PanelWindows, HidePanel);
        Closed += (_, _) => outsideClicks.Dispose();
        // A click in the panel itself closes the side panels (except on the buttons that open them).
        Card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!ModesPopup.IsOpen && !InputsPopup.IsOpen) return;
            // The side panels sit in the panel's element tree, so their clicks come through here too: a click inside a
            // side panel is its own (pick a mode, switch an input), not a click in the panel.
            if (ModesCard.IsMouseOver || InputsCard.IsMouseOver || InsideSidePanel(e.OriginalSource as DependencyObject)) return;
            if (OpensSidePanel(e.OriginalSource as DependencyObject)) return;
            CloseModes();
            CloseInputs();
        };
        MouseEnter += (_, _) => idle.Stop();
        MouseLeave += (_, _) => RestartIdle();
        PreviewMouseDown += (_, _) => RestartIdle();
        SizeChanged += (_, _) => { Place(); MoveHighlight(animate: false); Theme.RoundWindow(this, Theme.PanelCornerRadius(Translucent), Translucent); };
        PreviewKeyDown += OnKeyDown;
        ModesPopup.CustomPopupPlacementCallback = PlaceModes;
        ModesPopup.Opened += (_, _) => FitPopupLook(ModesCard);
        InputsPopup.Opened += (_, _) => FitPopupLook(InputsCard);
        InputsPopup.CustomPopupPlacementCallback = PlaceInputs;   // beside the panel, like the picture modes
        // Its list grows once it is shown: place it again so the bottoms stay lined up.
        InputsPopup.Opened += (_, _) => Dispatcher.BeginInvoke(() => Reposition(InputsPopup), DispatcherPriority.Background);
        ModesPopup.Opened += (_, _) => Dispatcher.BeginInvoke(() => Reposition(ModesPopup), DispatcherPriority.Background);
        markTimer.Tick += (_, _) => MainMarkHeld();
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (modesHover && Model.ShowsModes && !ModesPopup.IsOpen) OpenModes();
        };
        // The window's look (Settings > Appearance) applies here too.
        SourceInitialized += (_, _) => FitLook();
        Theme.Changed += FitLook;
        Closed += (_, _) => Theme.Changed -= FitLook;
        Card.MouseMove += (_, e) => MovePointerLight(e.GetPosition(PointerLight));
        Card.MouseLeave += (_, _) =>
        {
            lightShown = false;
            PointerLight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };
        model.PropertyChanged += (_, e) =>
        {
            // The picture modes only open by a click (or half a second of hover) on Custom in use; never by themselves.
            if (e.PropertyName == nameof(SunPanelViewModel.ShowsModes) && !Model.ShowsModes) CloseModes();
        };
        PreviewKeyUp += OnKeyUp;
        model.SelectionMoved += () => Dispatcher.BeginInvoke(() => MoveHighlight(animate: true), DispatcherPriority.Loaded);
        // A row changes height when its slider appears (a display of another computer is read a moment after it is listed): the highlight follows it.
        Rows.SizeChanged += (_, _) => { MarkLayoutShift(); MoveHighlight(animate: false); };
        model.Rows.CollectionChanged += (_, _) => MarkLayoutShift();
    }

    // The rows move under a pointer that stays still when a display of another computer is read (its slider appears) or the list is
    // rebuilt: WPF then reports "mouse entered" on whichever row is there now, which must not select it (the highlight jumped to the
    // local display right after something was done on a remote one). Only a pointer that moved afterwards selects by hovering.
    private long layoutShiftAt;
    private Point pointerAtShift;

    private void MarkLayoutShift()
    {
        layoutShiftAt = Environment.TickCount64;
        pointerAtShift = Mouse.GetPosition(this);
    }

    private SunPanelViewModel Model => (SunPanelViewModel)DataContext;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool join);

    /// <summary>
    /// Brings the panel to the front with the keyboard. Opened by the * key on the desktop, Windows would refuse a plain
    /// SetForegroundWindow: joining the input of the window in front for a moment lets it through.
    /// </summary>
    private static void TakeForeground(IntPtr handle)
    {
        if (SetForegroundWindow(handle) && GetForegroundWindow() == handle) return;
        uint front = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), mine = GetCurrentThreadId();
        if (front == 0 || front == mine) return;
        AttachThreadInput(mine, front, true);
        try { SetForegroundWindow(handle); }
        finally { AttachThreadInput(mine, front, false); }
    }

    private bool closing;
    private readonly Services.OutsideClicks outsideClicks;

    /// <summary>The panel and its open side panels, as window handles (clicks inside them are not "outside").</summary>
    private IEnumerable<IntPtr> PanelWindows()
    {
        yield return new System.Windows.Interop.WindowInteropHelper(this).Handle;
        foreach (var card in new Visual[] { ModesCard, InputsCard })
            if (PresentationSource.FromVisual(card) is System.Windows.Interop.HwndSource source) yield return source.Handle;
    }

    private bool InsideSidePanel(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (ReferenceEquals(element, ModesCard) || ReferenceEquals(element, InputsCard)) return true;
        return false;
    }

    /// <summary>True for a click on the monitor icon (inputs) or on Custom in use (picture modes): those toggle themselves.</summary>
    private static bool OpensSidePanel(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { Name: "InputsButton" }) return true;
            if (element is FrameworkElement { Name: "ModesButton" }) return true;
        }
        return false;
    }

    /// <summary>Sun icon clicked: open, or close when open. A click that just closed it (by taking focus) does not reopen it.</summary>
    public void Toggle()
    {
        if (IsVisible && !closing) { HidePanel(); return; }
        if (DateTime.UtcNow - hiddenAt < TimeSpan.FromMilliseconds(350)) return;
        Open();
    }

    /// <summary>
    /// Opens the panel from a shortcut. <paramref name="activate"/> false (brightness keys) shows it without taking the
    /// keyboard from the app in use; it then closes after the idle time. Already open: it just stays open longer.
    /// </summary>
    public void ShowPanel(bool activate = true)
    {
        if (IsVisible && !closing)
        {
            if (activate) Activate();
            RestartIdle();
            return;
        }
        Open(activate);
    }

    private void Open(bool activate = true)
    {
        closing = false;
        // Laid out and placed while still invisible, so it never flashes where it was last (or at its default place)
        // before moving above the tray: that was the "first try fails, then it comes out again" glitch.
        BeginAnimation(OpacityProperty, null);
        Opacity = 0;
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        Slide.Y = 14;
        ShowActivated = activate;
        if (!IsVisible) Show();
        UpdateLayout();
        Place();
        if (activate)
        {
            Activate();
            // The tray click gives this app the right to take the foreground: take it, so the keys reach the panel.
            TakeForeground(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Focus();
        }
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        RestartIdle();
        outsideClicks.Start();
        _ = Model.RefreshAsync();
        Dispatcher.BeginInvoke(() => MoveHighlight(animate: false), DispatcherPriority.Loaded);
    }

    /// <summary>Closes with a short slide down and fade, then hides.</summary>
    public void HidePanel()
    {
        if (!IsVisible || closing) return;
        closing = true;
        outsideClicks.Stop();
        CloseModes();
        CloseInputs();
        hoverTimer.Stop();
        idle.Stop();
        starTimer.Stop();
        mainTimer.Stop();
        Model.Closed();
        hiddenAt = DateTime.UtcNow;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (!closing) return;   // opened again meanwhile
            closing = false;
            Hide();
        };
        Slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(Slide.Y, 12, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        BeginAnimation(OpacityProperty, fade);
    }

    private void RestartIdle()
    {
        idle.Stop();
        idle.Start();
    }

    /// <summary>Bottom right of the work area, just above the taskbar (where the notification area usually is).</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double inset = Translucent ? 12 : 4;   // the card sits at the same spot with or without its transparent margin
        Left = area.Right - ActualWidth - inset;
        Top = area.Bottom - ActualHeight - inset;
    }

    // ---- Keys ----

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        RestartIdle();
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int? digit = key switch
        {
            >= Key.D0 and <= Key.D9 when !shift => key - Key.D0,
            >= Key.NumPad0 and <= Key.NumPad9 => key - Key.NumPad0,
            _ => null
        };
        int sign = key switch
        {
            Key.Add or Key.Up or Key.Right => 1,
            Key.OemPlus => 1,
            Key.Subtract or Key.OemMinus or Key.Down or Key.Left => -1,
            _ => 0
        };
        bool star = key == Key.Multiply || (key == Key.D8 && shift);

        if (star)
        {
            if (!e.IsRepeat) { starHeld = false; starTimer.Start(); }
        }
        else if (key == Key.M)
        {
            if (!e.IsRepeat) mainTimer.Start();
        }
        else if (sign != 0)
        {
            var now = DateTime.UtcNow;
            if (!e.IsRepeat)
            {
                stepDown = now;
                lastHoldStep = now;
                Model.Tap(sign);
            }
            else
            {
                var held = now - stepDown;
                if (held >= BrightnessSteps.HoldFirst && now - lastHoldStep >= BrightnessSteps.HoldRepeat(held))
                {
                    lastHoldStep = now;
                    Model.Hold(sign, held);
                }
            }
        }
        else if (digit is int d) Model.Digit(d);
        else if (key == Key.Enter) Model.CommitTyped();
        else if (key == Key.Escape)
        {
            if (!Model.CancelTyped()) HidePanel();
        }
        else return;
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Multiply || key == Key.D8)
        {
            bool wasTap = starTimer.IsEnabled && !starHeld;
            starTimer.Stop();
            if (wasTap) Model.SelectNext();   // * tapped: next display
            e.Handled = true;
        }
        else if (key == Key.M)
        {
            mainTimer.Stop();   // a short tap does nothing (a held M moves the main display)
            e.Handled = true;
        }
    }

    // ---- Pointer ----

    // ---- Profiles and the picture modes beside the panel ----

    /// <summary>A profile icon: applies it; Custom clicked again while in use opens (or closes) the picture modes.</summary>
    private void ProfileClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SceneViewModel scene) return;
        hoverTimer.Stop();
        CloseInputs();
        CloseModes();
        scene.ApplyCommand.Execute(null);
    }

    /// <summary>The Picture mode button: opens (or closes) the picture modes beside the panel.</summary>
    private void ModesButtonClick(object sender, RoutedEventArgs e)
    {
        hoverTimer.Stop();
        CloseInputs();
        if (ModesPopup.IsOpen && !modesClosing) CloseModes();
        else if (Model.ShowsModes) OpenModes();
    }

    private void ModesButtonEnter(object sender, MouseEventArgs e)
    {
        hovered = null;
        hoverTimer.Stop();
        hoverTimer.Start();
        modesHover = true;
    }

    private void ModesButtonLeave(object sender, MouseEventArgs e)
    {
        hoverTimer.Stop();
        modesHover = false;
    }

    private bool modesHover;

    private void OpenModes()
    {
        CloseInputs();
        modesCloseSeq++;   // a close still running is cancelled
        modesClosing = false;
        ModesPopup.IsOpen = true;
        ModesSlide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease });
        ModesCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        RestartIdle();
    }

    // Resting the pointer on Custom (while it is in use) for half a second also opens the picture modes.
    private readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private SceneViewModel? hovered;

    private void ProfileEnter(object sender, MouseEventArgs e)
    {
        hovered = (sender as FrameworkElement)?.DataContext as SceneViewModel;
        hoverTimer.Stop();
    }

    private void ProfileLeave(object sender, MouseEventArgs e)
    {
        hoverTimer.Stop();
        hovered = null;
    }

    // ---- M: click makes the display the main one; held half a second it becomes the Preferred main display ----

    private readonly DispatcherTimer markTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private SunRow? markRow;

    private void MainMarkDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SunRow row } mark) return;
        markRow = row;
        markTimer.Stop();
        markTimer.Start();
        mark.CaptureMouse();
        e.Handled = true;
    }

    private void MainMarkUp(object sender, MouseButtonEventArgs e)
    {
        (sender as FrameworkElement)?.ReleaseMouseCapture();
        e.Handled = true;
        if (!markTimer.IsEnabled) return;   // held: already made Preferred
        markTimer.Stop();
        if (markRow is { } row && sender is FrameworkElement { IsMouseOver: true }) row.MakeMainCommand.Execute(null);
        markRow = null;
    }

    private void MainMarkHeld()
    {
        markTimer.Stop();
        if (markRow is { } row) Model.TogglePreferred(row);
        markRow = null;
        RestartIdle();
    }

    private void MainMarkRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SunRow row) return;
        Model.TogglePreferred(row);
        RestartIdle();
        e.Handled = true;
    }

    // ---- Inputs of a monitor ----

    /// <summary>The inputs button of a row: opens that monitor's inputs under it (or closes them).</summary>
    private void InputsClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SunRow { HasInputs: true } row }) return;
        bool same = InputsPopup.IsOpen && !inputsClosing && ReferenceEquals(InputsPopup.DataContext, row);
        if (same) { CloseInputs(); return; }
        ShowInputs(row);
    }

    private void ShowInputs(SunRow row)
    {
        CloseModes();
        inputsCloseSeq++;   // a close still running is cancelled
        inputsClosing = false;
        InputsPopup.IsOpen = false;
        InputsPopup.DataContext = row;
        InputsPopup.IsOpen = true;
        InputsSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease });
        InputsCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        RestartIdle();
        _ = row.Monitor.ReadInputAsync();   // the current input may have changed on the monitor itself
    }

    private static void Reposition(System.Windows.Controls.Primitives.Popup popup)
    {
        if (!popup.IsOpen) return;
        double offset = popup.VerticalOffset;
        popup.VerticalOffset = offset + 1;
        popup.VerticalOffset = offset;
    }

    // ---- Leaving: both side panels fade and slide out the same way and at the same speed (the click outside is the way out) ----

    private static readonly TimeSpan SideExit = TimeSpan.FromMilliseconds(150);
    private int modesCloseSeq, inputsCloseSeq;
    private bool modesClosing, inputsClosing;

    private void CloseModes()
    {
        if (!ModesPopup.IsOpen || modesClosing) return;
        modesClosing = true;
        int seq = ++modesCloseSeq;
        FadeOutSide(ModesCard, ModesSlide, () => { if (seq == modesCloseSeq) { ModesPopup.IsOpen = false; modesClosing = false; } });
    }

    private void CloseInputs()
    {
        if (!InputsPopup.IsOpen || inputsClosing) return;
        inputsClosing = true;
        int seq = ++inputsCloseSeq;
        FadeOutSide(InputsCard, InputsSlide, () => { if (seq == inputsCloseSeq) { InputsPopup.IsOpen = false; inputsClosing = false; } });
    }

    private static void FadeOutSide(UIElement card, TranslateTransform slide, Action done)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(card.Opacity, 0, SideExit) { EasingFunction = ease };
        fade.Completed += (_, _) => done();
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slide.X, 12, SideExit) { EasingFunction = ease });
        card.BeginAnimation(OpacityProperty, fade);
    }

    private void ModeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModeItem item) Model.PendingMode = item.Option;
        RestartIdle();
    }


    /// <summary>Pointer on a monitor's icon: its inputs open beside the panel (a click toggles them, as before).</summary>
    private void MonitorIconEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SunRow { HasInputs: true } row }) return;
        if (InputsPopup.IsOpen && ReferenceEquals(InputsPopup.DataContext, row)) return;   // already showing this monitor's inputs
        ShowInputs(row);
    }

    private void ModesEnter(object sender, MouseEventArgs e) => idle.Stop();

    private static bool Translucent => Theme.Current.Appearance == Core.Settings.Appearance.Translucent;

    /// <summary>
    /// Ground, edge and blur like the main window. Translucent: the card fills the whole window (no transparent margin),
    /// because Windows blurs the full window rectangle.
    /// </summary>
    private void FitLook()
    {
        bool translucent = Translucent;
        Card.Margin = new Thickness(translucent ? 0 : 8);
        Width = translucent ? 384 : 400;
        var edge = new System.Windows.Media.SolidColorBrush(translucent
            ? System.Windows.Media.Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)
            : System.Windows.Media.Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        Card.BorderBrush = ModesCard.BorderBrush = InputsCard.BorderBrush = edge;
        Theme.FitWindow(this, translucent);
        // Windows 10 cannot round the blur: in Translucent the panels are square there (Theme.PanelCornerRadius).
        double radius = Theme.PanelCornerRadius(translucent);
        Card.CornerRadius = ModesCard.CornerRadius = InputsCard.CornerRadius = new CornerRadius(radius);
        GlowRect.RadiusX = GlowRect.RadiusY = PointerLight.RadiusX = PointerLight.RadiusY = Math.Max(0, radius - 1);
        Theme.RoundWindow(this, radius, translucent);
        // Windows' own fade when a window is shown, on top of the panel's: the panel seemed to come out twice.
        Theme.NoShowAnimation(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (!Theme.Current.PointerLight)
        {
            lightShown = false;
            PointerLight.BeginAnimation(OpacityProperty, null);
            PointerLight.Opacity = 0;
        }
        if (IsVisible) Place();
    }

    private static readonly HashSet<FrameworkElement> roundedCards = [];

    private static void FitPopupLook(FrameworkElement card)
    {
        if (System.Windows.PresentationSource.FromVisual(card) is not System.Windows.Interop.HwndSource source) return;
        Theme.FitWindow(source.Handle, Translucent);
        void Round() => Theme.RoundHandle(source.Handle, card, card.ActualWidth, card.ActualHeight, Theme.PanelCornerRadius(Translucent), Translucent);
        Round();
        if (roundedCards.Add(card)) card.SizeChanged += (_, _) => { if (System.Windows.PresentationSource.FromVisual(card) is System.Windows.Interop.HwndSource now) Theme.RoundHandle(now.Handle, card, card.ActualWidth, card.ActualHeight, Theme.PanelCornerRadius(Translucent), Translucent); };
        Theme.NoShowAnimation(source.Handle);
    }

    private bool lightShown;

    private void MovePointerLight(Point at)
    {
        if (!Theme.Current.PointerLight) return;
        PointerLightBrush.Center = PointerLightBrush.GradientOrigin = at;
        if (lightShown) return;
        lightShown = true;
        PointerLight.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>
    /// Left of the panel with a small gap. The inputs: tops lined up when their display is in the upper half of the
    /// panel, bottoms lined up when it is in the lower half. Sizes are in pixels.
    /// </summary>
    private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceBeside(Size popup, Size target, SunRow? row)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double gap = 10 * dpi.DpiScaleX;
        double y = InUpperHalf(row) ? 0 : target.Height - popup.Height;
        // Never over the taskbar: the side panel ends at the bottom of the work area at the lowest.
        if (PresentationSource.FromVisual(Card) is not null)
        {
            double cardTop = Card.PointToScreen(new Point(0, 0)).Y;
            double bottom = SystemParameters.WorkArea.Bottom * dpi.DpiScaleY;
            y = Math.Min(y, bottom - cardTop - popup.Height);
        }
        return [new(new Point(-popup.Width - gap, y), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)];
    }

    /// <summary>The picture modes: bottoms lined up with the panel, above the taskbar.</summary>
    private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceModes(Size popup, Size target, Point offset) =>
        PlaceBeside(popup, target, null);

    private System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceInputs(Size popup, Size target, Point offset) =>
        PlaceBeside(popup, target, InputsPopup.DataContext as SunRow);

    private bool InUpperHalf(SunRow? row)
    {
        if (row is null || Card.ActualHeight <= 0 || RowList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement box
            || !box.IsVisible) return false;
        double middle = box.TranslatePoint(new Point(0, box.ActualHeight / 2), Card).Y;
        return middle < Card.ActualHeight / 2;
    }

    private void RowHover(object sender, MouseEventArgs e)
    {
        if (Environment.TickCount64 - layoutShiftAt < 1500 && (Mouse.GetPosition(this) - pointerAtShift).Length < 2) return;   // the rows moved, not the pointer
        if (sender is FrameworkElement { Tag: SunRow row }) Model.Select(row);
    }

    private void RowClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SunRow row }) Model.Select(row);
    }

    private void RowWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SunRow row } && row.IsReady)
        {
            Model.Select(row);
            row.MoveTo(row.Percent + Notches(e));
            RestartIdle();
            e.Handled = true;
        }
    }

    private void LinkedWheel(object sender, MouseWheelEventArgs e)
    {
        if (Model.LeadRow is { IsReady: true } row) row.MoveTo(row.Percent + Notches(e));
        RestartIdle();
        e.Handled = true;
    }

    private static int Notches(MouseWheelEventArgs e) => Math.Sign(e.Delta) * Math.Max(1, Math.Abs(e.Delta) / 120);

    // ---- Highlight ----

    /// <summary>Glides the rounded highlight to the selected row (hidden while linked: one box stands for all).</summary>
    private void MoveHighlight(bool animate)
    {
        if (Model.Linked || Model.Selected is not { } row
            || RowList.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement box || box.ActualHeight <= 0)
        {
            Highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            return;
        }
        double top = box.TranslatePoint(new Point(0, 0), Rows).Y;
        var time = TimeSpan.FromMilliseconds(animate && Highlight.Opacity > 0 ? 200 : 0);
        HighlightShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(top, time) { EasingFunction = Ease });
        Highlight.BeginAnimation(HeightProperty, new DoubleAnimation(box.ActualHeight, time) { EasingFunction = Ease });
        Highlight.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }
}
