using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SwitchMonitor.Core.Scenes;

namespace SwitchMonitor.App.Services;

/// <summary>
/// A black cover over every screen for the moment a setting such as FreeSync makes the display lose its signal: it fades
/// in before the change and out once the display answers again.
/// What it is: a borderless, click-through, always-on-top black window over the whole desktop. What it is NOT: control of
/// the monitor's signal. While the link re-syncs the monitor itself shows black or "no signal"; the cover only hides what
/// Windows draws before and after (the desktop flashing, the app moving). If the cover cannot be shown the change still
/// happens, uncovered. A timer removes it after at most <see cref="MaxCover"/> whatever happens.
/// </summary>
internal sealed class BlackoutOverlay
{
    public static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(700);
    public static readonly TimeSpan MaxCover = TimeSpan.FromSeconds(20);

    private readonly Dispatcher dispatcher;
    private Window? cover;
    private readonly DispatcherTimer failsafe = new() { Interval = MaxCover };

    private readonly Action<string>? log;

    /// <param name="log">Receives a line for each step with its time (the log shows how long the cover really took).</param>
    public BlackoutOverlay(Dispatcher dispatcher, Action<string>? log = null)
    {
        this.dispatcher = dispatcher;
        this.log = log;
        failsafe.Tick += (_, _) => RemoveNow();
    }

    /// <summary>The hooks to give <see cref="SceneApplier.Apply"/>: they run on a worker thread and block until the fade is done.</summary>
    public TransitionHooks CreateHooks() => new()
    {
        BeforeDisruptive = () => Run(Cover),
        AfterDisruptive = () => Run(Uncover)
    };

    /// <summary>Runs <paramref name="start"/> on the UI thread and waits for the fade it starts (never indefinitely).</summary>
    private void Run(Action<ManualResetEventSlim> start)
    {
        using var done = new ManualResetEventSlim();
        try { dispatcher.BeginInvoke(() => { try { start(done); } catch (Exception error) when (error is not OutOfMemoryException) { done.Set(); } }); }
        catch (Exception error) when (error is InvalidOperationException or TaskCanceledException) { return; }   // the app is closing
        done.Wait(FadeTime + TimeSpan.FromSeconds(1));
    }

    private int users;   // changes covering at the same time (two displays): the cover comes off when the last one is done

    private void Cover(ManualResetEventSlim done)
    {
        users++;
        if (cover is not null) { done.Set(); return; }
        var window = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Black, ShowInTaskbar = false,
            Topmost = true, ShowActivated = false, Focusable = false, ResizeMode = ResizeMode.NoResize, Opacity = 0, Title = ""
        };
        window.SourceInitialized += (_, _) => Fit(window);
        window.Show();
        cover = window;
        failsafe.Stop();
        failsafe.Start();
        log?.Invoke("Cover: fading to black.");
        FadeTo(window, 1, done);
    }

    private void Uncover(ManualResetEventSlim done)
    {
        if (users > 0) users--;
        if (cover is not { } window || users > 0) { done.Set(); return; }
        // A display re-sync can make Windows redraw or drop what a window shows: put the cover back at full black, on top and
        // in place, let it paint, and only then fade it out, so the picture comes back through the fade and not in a cut.
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Opacity = 1;
        window.Topmost = false;
        window.Topmost = true;
        Fit(window);
        window.InvalidateVisual();
        window.UpdateLayout();
        log?.Invoke("Cover: the display answers again; black re-asserted, fading out.");
        var wait = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (cover is { } still && ReferenceEquals(still, window)) FadeTo(window, 0, done, then: () => { RemoveNow(); log?.Invoke("Cover: gone."); });
            else done.Set();
        };
        wait.Start();
    }

    private void FadeTo(Window window, double opacity, ManualResetEventSlim done, Action? then = null)
    {
        var animation = new DoubleAnimation(opacity, FadeTime)
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        animation.Completed += (_, _) => { then?.Invoke(); done.Set(); };
        window.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    private void RemoveNow()
    {
        failsafe.Stop();
        users = 0;
        var window = cover;
        cover = null;
        if (window is null) return;
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Close();
    }

    // ---- Click-through, never activated, and exactly the size of the whole desktop in device pixels (any scale) ----

    private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x10;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);

    private static void Fit(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        SetWindowLong(handle, GwlExStyle, GetWindowLong(handle, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate);
        // SM_XVIRTUALSCREEN 76, SM_YVIRTUALSCREEN 77, SM_CXVIRTUALSCREEN 78, SM_CYVIRTUALSCREEN 79
        SetWindowPos(handle, HwndTopmost, GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79), SwpNoActivate);
    }
}
