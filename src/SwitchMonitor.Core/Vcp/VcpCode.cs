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
        _ => $"VCP 0x{code:X2}"
    };
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
