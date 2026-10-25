namespace SwitchMonitor.Core.Vcp;

/// <summary>MCCS 2.2 VCP feature codes used by SwitchMonitor.</summary>
public static class VcpCode
{
    public const byte Brightness = 0x10;
    public const byte Contrast = 0x12;
    public const byte ColorPreset = 0x14;
    public const byte RedGain = 0x16;
    public const byte GreenGain = 0x18;
    public const byte BlueGain = 0x1A;
    public const byte InputSource = 0x60;
    public const byte AudioVolume = 0x62;
    public const byte AudioMute = 0x8D;
    public const byte DisplayMode = 0xDC;
    public const byte PowerMode = 0xD6;

    /// <summary>Human-readable name for a VCP code, or a hex label when unknown.</summary>
    public static string Describe(byte code) => code switch
    {
        Brightness => "Brightness",
        Contrast => "Contrast",
        ColorPreset => "Color preset",
        RedGain => "Red gain",
        GreenGain => "Green gain",
        BlueGain => "Blue gain",
        InputSource => "Input source",
        AudioVolume => "Volume",
        AudioMute => "Mute",
        DisplayMode => "Display mode",
        PowerMode => "Power mode",
        _ => MccsNames.TryGetValue(code, out var name) ? name : $"VCP 0x{code:X2}"
    };

    /// <summary>MCCS 2.2a names of other standard codes, used to label scan results.</summary>
    private static readonly Dictionary<byte, string> MccsNames = new()
    {
        [0x02] = "New control value", [0x04] = "Restore factory defaults", [0x05] = "Restore luminance/contrast",
        [0x06] = "Restore geometry", [0x08] = "Restore color defaults", [0x0B] = "Color temperature increment",
        [0x0C] = "Color temperature request", [0x0E] = "Clock", [0x1E] = "Auto setup", [0x20] = "Horizontal position",
        [0x30] = "Vertical position", [0x3E] = "Clock phase", [0x52] = "Active control", [0x59] = "6-axis saturation red",
        [0x6C] = "Black level red", [0x6E] = "Black level green", [0x70] = "Black level blue", [0x72] = "Gamma",
        [0x87] = "Sharpness", [0x8A] = "Saturation", [0x90] = "Hue", [0xAA] = "Screen orientation",
        [0xAC] = "Horizontal frequency", [0xAE] = "Vertical frequency", [0xB2] = "Sub-pixel layout",
        [0xB6] = "Display technology", [0xC0] = "Usage time", [0xC6] = "Application enable key",
        [0xC8] = "Display controller", [0xC9] = "Firmware level", [0xCA] = "OSD", [0xCC] = "OSD language",
        [0xDF] = "VCP version"
    };

    /// <summary>
    /// Codes a scan never writes and the app never offers as controls, even when a monitor answers them:
    /// resets and commands, input and power, OSD visibility and language, controller id.
    /// </summary>
    public static bool IsNeverWritten(byte code) =>
        code is 0x01 or 0x03 or 0x04 or 0x05 or 0x06 or 0x08 or 0x0A or 0x1E or 0x1F or 0xB0
            or InputSource or PowerMode or 0xCA or 0xCC or 0xC8 or 0xF4;
}

/// <summary>Standard MCCS values for VCP 0x60 (input source).</summary>
public static class InputSource
{
    public static string Describe(int code) => code switch
    {
        0x01 => "VGA 1",
        0x02 => "VGA 2",
        0x03 => "DVI 1",
        0x04 => "DVI 2",
        0x0F => "DisplayPort 1",
        0x10 => "DisplayPort 2",
        0x11 => "HDMI 1",
        0x12 => "HDMI 2",
        0x1B => "USB-C",
        _ => $"Input {code}"
    };

    /// <summary>Display order used when a monitor advertises several inputs.</summary>
    internal static readonly int[] PreferredOrder = [0x11, 0x12, 0x0F, 0x10, 0x1B, 0x03, 0x04, 0x01, 0x02];

    public static IReadOnlyList<int> Order(IEnumerable<int> codes)
    {
        var set = codes.ToHashSet();
        return PreferredOrder.Where(set.Contains).Concat(set.Except(PreferredOrder).Order()).ToArray();
    }
}

/// <summary>Current and maximum value reported for a VCP feature.</summary>
public readonly record struct VcpValue(uint Current, uint Maximum)
{
    /// <summary>Current value as 0-100 percent of the reported maximum.</summary>
    public double Percent => Maximum == 0 ? 0 : Math.Round(Current * 100.0 / Maximum, 1);

    /// <summary>Raw value for a 0-100 percent request, clamped to the reported range.</summary>
    public static uint FromPercent(double percent, uint maximum)
    {
        if (maximum == 0) throw new InvalidOperationException("The feature range is unavailable.");
        return (uint)Math.Clamp(Math.Round(Math.Clamp(percent, 0, 100) * maximum / 100), 0, maximum);
    }
}
