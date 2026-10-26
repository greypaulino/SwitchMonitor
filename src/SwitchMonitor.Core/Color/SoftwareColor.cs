using System.Runtime.InteropServices;

namespace SwitchMonitor.Core.Color;

/// <summary>
/// Color temperature and red/green/blue through the graphics card's gamma ramp. Works on any display, including
/// laptop panels that have no color controls and monitors whose presets are not reachable over DDC (LG Medium).
/// Windows refuses ramps that stray too far from neutral unless GdiIcmGammaRange is set, and resets the ramp when a
/// display is reconnected, so callers apply it again after such changes.
/// </summary>
public static class SoftwareColor
{
    /// <summary>Neutral white point: no change.</summary>
    public const int NeutralKelvin = 6500;

    public const int MinKelvin = 3000;
    public const int MaxKelvin = 10000;

    /// <summary>
    /// Channel multipliers (0-1) for a white point, relative to 6500 K (approximation of black-body colors by
    /// Tanner Helland), so 6500 K is exactly (1, 1, 1) and no channel ever goes above 1.
    /// </summary>
    public static (double R, double G, double B) WhitePoint(int kelvin)
    {
        var (r, g, b) = Blackbody(Math.Clamp(kelvin, 1000, 40000));
        var (nr, ng, nb) = Blackbody(NeutralKelvin);
        double rr = r / nr, gg = g / ng, bb = b / nb;
        double top = Math.Max(rr, Math.Max(gg, bb));
        return (rr / top, gg / top, bb / top);
    }

    private static (double R, double G, double B) Blackbody(int kelvin)
    {
        double t = kelvin / 100.0;
        double r = t <= 66 ? 255 : 329.698727446 * Math.Pow(t - 60, -0.1332047592);
        double g = t <= 66 ? 99.4708025861 * Math.Log(t) - 161.1195681661 : 288.1221695283 * Math.Pow(t - 60, -0.0755148492);
        double b = t >= 66 ? 255 : t <= 19 ? 0 : 138.5177312231 * Math.Log(t - 10) - 305.0447927307;
        return (Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255));
    }

    /// <summary>The 768-entry ramp (red, green, blue) for a white point and extra per-channel gains (0-1).</summary>
    public static ushort[] Ramp(int kelvin, double red = 1, double green = 1, double blue = 1)
    {
        var (wr, wg, wb) = WhitePoint(kelvin);
        double[] gains = [wr * Math.Clamp(red, 0, 1), wg * Math.Clamp(green, 0, 1), wb * Math.Clamp(blue, 0, 1)];
        var ramp = new ushort[768];
        for (int channel = 0; channel < 3; channel++)
            for (int i = 0; i < 256; i++)
                ramp[channel * 256 + i] = (ushort)Math.Clamp(Math.Round(i * 257 * gains[channel]), 0, 65535);
        return ramp;
    }

    public static bool IsNeutral(int kelvin, double red, double green, double blue) =>
        kelvin == NeutralKelvin && red >= 0.999 && green >= 0.999 && blue >= 0.999;

    /// <summary>Sets the ramp on one display (\\.\DISPLAY1). False when Windows or the driver refuses it.</summary>
    public static bool Apply(string gdiName, int kelvin, double red = 1, double green = 1, double blue = 1) =>
        Set(gdiName, Ramp(kelvin, red, green, blue));

    /// <summary>Back to the neutral ramp.</summary>
    public static bool Reset(string gdiName) => Set(gdiName, Ramp(NeutralKelvin));

    private static bool Set(string gdiName, ushort[] ramp)
    {
        IntPtr hdc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try { return SetDeviceGammaRamp(hdc, ramp); }
        finally { DeleteDC(hdc); }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr data);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
}
