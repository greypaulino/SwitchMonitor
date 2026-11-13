using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace SwitchMonitor.App.Services;

/// <summary>
/// "--glass-test": nine small windows side by side, each with a different way of making a window see-through, over whatever the desktop
/// shows. One photograph of them tells which way gives a real glass look on this Windows (the answers Windows gave are written in each one).
/// Click a window to see it active (some kinds turn into a flat color when the window is not the active one); Esc closes them all.
/// </summary>
internal static class GlassTest
{
    private sealed record Variant(string Name, bool Layered, int Accent, int Gradient, int Backdrop, byte Tint);

    public static void Run(Action finished)
    {
        var variants = new[]
        {
            new Variant("A  blur-behind, tint 60 %  (what the app does now)", true, 3, 0x01000000, 0, 0x99),
            new Variant("B  blur-behind, tint 20 %", true, 3, 0x01000000, 0, 0x33),
            new Variant("C  acrylic, no tint", true, 4, 0x01000000, 0, 0x00),
            new Variant("D  acrylic, tint 40 %", true, 4, 0x01000000, 0, 0x66),
            new Variant("E  host backdrop, tint 40 %", true, 5, 0x01000000, 0, 0x66),
            new Variant("F  Windows 11 acrylic (own backdrop)", false, 0, 0, 3, 0x00),
            new Variant("G  Windows 11 Mica", false, 0, 0, 2, 0x00),
            new Variant("H  Windows 11 Mica Alt", false, 0, 0, 4, 0x00),
            new Variant("I  Windows 11 acrylic + tint 40 %", false, 0, 0, 3, 0x66),
        };
        var windows = new List<Window>();
        for (int i = 0; i < variants.Length; i++) windows.Add(Make(variants[i], 40 + i % 3 * 360, 60 + i / 3 * 250, windows, finished));
        foreach (var w in windows) w.Show();
        windows[0].Activate();
    }

    private static Window Make(Variant v, double left, double top, List<Window> all, Action finished)
    {
        var window = new Window
        {
            Width = 340, Height = 230, Left = left, Top = top, WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Brushes.Transparent,
            AllowsTransparency = v.Layered, Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 13,
        };
        if (!v.Layered) WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 0, GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(0), UseAeroCaptionButtons = false, CornerRadius = new CornerRadius(0) });
        var text = new TextBlock { Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap };
        var frame = new Border
        {
            CornerRadius = new CornerRadius(v.Layered ? 8 : 0), Child = text, BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(v.Tint, 0x14, 0x14, 0x14)),
        };
        window.Content = frame;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            int answer;
            if (v.Layered)
            {
                int round = 2, dark = 1;
                DwmRound(handle, ref round, ref dark);
                answer = Theme.SetAccentRaw(handle, v.Accent, v.Gradient);
            }
            else
            {
                if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
                answer = Theme.SetBackdrop(handle, v.Backdrop);
            }
            text.Inlines.Add(new System.Windows.Documents.Run(v.Name) { FontWeight = FontWeights.SemiBold, FontSize = 15 });
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run((v.Layered ? "see-through window" : "normal window") + $" · Windows answered {answer}"));
            text.Inlines.Add(new System.Windows.Documents.LineBreak());
            text.Inlines.Add(new System.Windows.Documents.Run($"Windows build {Environment.OSVersion.Version.Build}. Click me to make me the active window. Esc closes all.") { Foreground = Brushes.Silver });
        };
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) { foreach (var w in all.ToArray()) w.Close(); } };
        window.Closed += (_, _) => { all.Remove(window); if (all.Count == 0) finished(); };
        window.MouseLeftButtonDown += (_, _) => window.Activate();
        return window;
    }

    private static void DwmRound(IntPtr handle, ref int round, ref int dark)
    {
        DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
