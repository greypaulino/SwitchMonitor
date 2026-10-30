using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App;

/// <summary>
/// The app's own look (Settings view): the interface color becomes a dark ground of the same hue (gray stays neutral),
/// and the appearance decides how it is drawn. Replaces the ground brushes of App.xaml, which the views use as dynamic
/// resources; glass cards are white at low opacity, so they follow the ground by themselves.
/// </summary>
public static class Theme
{
    /// <summary>Interface color presets: name and the color shown on its swatch.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Presets { get; } =
    [
        ("Graphite", InterfaceSettings.DefaultColor),
        ("Stone", "#A08C70"),
        ("Moss", "#6F9A7C"),
        ("Ocean", "#5F86B8"),
        ("Plum", "#9A78B8"),
        ("Wine", "#B86A78")
    ];

    public static InterfaceSettings Current { get; private set; } = new();

    public static event Action? Changed;

    public static void Apply(InterfaceSettings look)
    {
        Current = look;
        var resources = Application.Current.Resources;
        var (hue, saturation, _) = ToHsl(Parse(look.Color));
        // Keep the hue, mute it strongly so text stays readable on it.
        double s = Math.Min(saturation, 0.6) * 0.6;
        Color Tone(double lightness, byte alpha = 255) => WithAlpha(FromHsl(hue, s, lightness), alpha);

        byte glass = look.Appearance == Appearance.Translucent ? (byte)0x8C : (byte)0xFF;
        resources["WindowBrush"] = Freeze(new SolidColorBrush(Tone(0.08, glass)));
        resources["PopupBrush"] = Freeze(new SolidColorBrush(Tone(0.15)));
        if (look.Appearance == Appearance.Matte)
        {
            resources["BackdropBrush"] = Freeze(new SolidColorBrush(Tone(0.095)));
            resources["GlowBrush"] = Brushes.Transparent;
        }
        else
        {
            var backdrop = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            backdrop.GradientStops.Add(new GradientStop(Tone(0.14, glass), 0));
            backdrop.GradientStops.Add(new GradientStop(Tone(0.09, glass), 0.5));
            backdrop.GradientStops.Add(new GradientStop(Tone(0.07, glass), 1));
            resources["BackdropBrush"] = Freeze(backdrop);
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.15, 0), GradientOrigin = new Point(0.15, 0), RadiusX = 0.75, RadiusY = 0.6
            };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
            resources["GlowBrush"] = Freeze(glow);
        }
        // Panel icons: the chosen style (IconRestyler redraws them); drawn icons get a matching line weight.
        Icons.PanelStyle = Icons.StyleKey(look.IconStyle);
        resources["IconStroke"] = look.IconStyle is "fill" ? 2.3 : 1.8;
        IconRestyler.RefreshAll();
        Changed?.Invoke();
    }

    /// <summary>The ground a color gives (for the swatch preview).</summary>
    public static Brush GroundFor(string hex)
    {
        var (hue, saturation, _) = ToHsl(Parse(hex));
        return Freeze(new SolidColorBrush(FromHsl(hue, Math.Min(saturation, 0.6) * 0.6, 0.12)));
    }

    // ---- Translucent: blur what is behind the window, inside its rounded shape ----

    /// <summary>Corner radius of the window frame.</summary>
    public const double CornerRadius = 14;

    /// <summary>Translucent keeps the same roundness; Windows 11 rounds the blur behind it (see FitWindow).</summary>
    public const double TranslucentCornerRadius = CornerRadius;

    /// <summary>Turns Windows' acrylic blur behind the (see-through) window on or off; Windows adds no border or rounding.</summary>
    public static void FitWindow(Window window, bool translucent)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        // Translucent: Windows 11 rounds the window, so the blur behind it follows the rounded frame instead of filling the
        // square corners. Otherwise no rounding and no border from Windows: the frame is drawn by the app.
        int none = unchecked((int)0xFFFFFFFE), corners = translucent ? 2 : 1;   // 2 = round, 1 = do not round
        DwmSetWindowAttribute(handle, DwmBorderColor, ref none, sizeof(int));
        DwmSetWindowAttribute(handle, DwmCornerPreference, ref corners, sizeof(int));
        SetAccent(handle, translucent);
    }

    /// <summary>Dark title bar for a normal window (dialogs such as the crop step).</summary>
    public static void UseDarkCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        int dark = 1;
        if (handle != IntPtr.Zero) DwmSetWindowAttribute(handle, DwmDarkMode, ref dark, sizeof(int));
    }

    /// <summary>How far a maximized window reaches past the screen (its resize border), in this window's units.</summary>
    public static Thickness MaximizedOverhang(Window window)
    {
        var scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        double x = (GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / scale.M11;
        double y = (GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder)) / scale.M22;
        return new Thickness(x, y, x, y);
    }

    /// <summary>The acrylic of the accent policy (works on see-through windows; square).</summary>
    private static void SetAccent(IntPtr handle, bool on)
    {
        var accent = new AccentPolicy { State = on ? AccentEnableAcrylicBlurBehind : AccentDisabled, Flags = 2, GradientColor = 0x01000000 };
        int size = Marshal.SizeOf(accent);
        IntPtr data = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, data, false);
            var attribute = new CompositionAttributeData { Attribute = WcaAccentPolicy, Data = data, SizeOfData = size };
            SetWindowCompositionAttribute(handle, ref attribute);
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { }
        finally { Marshal.FreeHGlobal(data); }
    }

    // ---- Color helpers ----

    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x8A, 0x8A, 0x8A); }
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static Color FromHsl(double hue, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = c * (1 - Math.Abs(hue / 60 % 2 - 1));
        double m = lightness - c / 2;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    /// <summary>HSV (hue 0-360, saturation and value 0-1), as the color picker uses it.</summary>
    public static Color FromHsv(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        var (r, g, b) = (hue % 360) switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x)
        };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    public static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d < 1e-9 ? 0 : max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, max <= 0 ? 0 : d / max, max);
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int State;
        public int Flags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr window, ref CompositionAttributeData data);
    private const int DwmDarkMode = 20;
    private const int DwmCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmSystemBackdrop = 38;
    private const int SmCxSizeFrame = 32, SmCySizeFrame = 33, SmCxPaddedBorder = 92;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
