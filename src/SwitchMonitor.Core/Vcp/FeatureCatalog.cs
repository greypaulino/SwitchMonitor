namespace SwitchMonitor.Core.Vcp;

public enum FeatureKind
{
    /// <summary>A 0..maximum range (brightness, contrast, volume, gains).</summary>
    Continuous,

    /// <summary>One value from a list (color preset, display mode, power mode).</summary>
    Choice,

    /// <summary>Two values meaning on and off (mute).</summary>
    Toggle
}

/// <summary>
/// Whether a control may be written. Power and input can leave the user without a picture on this computer,
/// so scenes and automated tests never send them. Unmapped vendor codes are shown read-only until a quirk
/// (or the learn tool) says what they do: some vendors use them for resets. ReadOnly controls are mapped
/// but the monitor ignores DDC writes to them (verified on hardware).
/// </summary>
public enum FeatureRisk { Safe, Disruptive, Unmapped, ReadOnly }

/// <summary>Description of one MCCS control the app knows how to present.</summary>
public sealed record FeatureDefinition(
    byte Code,
    string Name,
    FeatureKind Kind,
    FeatureRisk Risk,
    IReadOnlyDictionary<uint, string> ValueNames)
{
    /// <summary>
    /// The monitor lists this control's values in its capabilities string as decimal digits read as hex
    /// (LG firmware: "15(01 06 28 48)" means 1, 6, 28, 48). Set by quirks.
    /// </summary>
    public bool CapabilityValuesAreDecimal { get; init; }

    /// <summary>The advertised value list is wrong for this control; only the quirk's named values are allowed.</summary>
    public bool IgnoreCapabilityValues { get; init; }

    /// <summary>
    /// Real top of a continuous control when the monitor reports a larger maximum
    /// (LG Black Stabilizer runs 0-100 but reports 255). Set by quirks.
    /// </summary>
    public uint? Maximum { get; init; }

    /// <summary>Order the monitor's own menu uses for the values (quirks list them that way); null sorts by value.</summary>
    public IReadOnlyList<uint>? DisplayOrder { get; init; }

    /// <summary>
    /// The control can only be changed while every listed control has one of the listed raw values
    /// (LG: red/green/blue only in picture mode Custom). Set by quirks; the interface locks the control otherwise.
    /// </summary>
    public IReadOnlyList<(byte Code, IReadOnlyList<uint> Values)> EnabledWhen { get; init; } = [];

    /// <summary>Older or alternative names accepted in scenes.json (e.g. "LG picture mode").</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// For two-option controls whose names mean on and off (FreeSync On/Off, Mute Muted/Sound on): the raw values,
    /// so the interface can show a switch. Null otherwise.
    /// </summary>
    public (uint On, uint Off)? OnOffValues(IReadOnlyCollection<uint> allowed)
    {
        if (Kind == FeatureKind.Continuous || allowed.Count != 2) return null;
        uint? on = null, off = null;
        foreach (uint value in allowed)
        {
            string name = DescribeValue(value).Trim().ToLowerInvariant();
            if (name is "on" or "muted" or "enabled" or "enable" or "yes") on = value;
            else if (name is "off" or "sound on" or "disabled" or "disable" or "no") off = value;
        }
        return on is uint o && off is uint f ? (o, f) : null;
    }

    public string DescribeValue(uint value) =>
        ValueNames.TryGetValue(value, out var name) ? name : $"0x{value:X2}";
}

/// <summary>Controls in the order the interface shows them. Values follow MCCS 2.2a.</summary>
public static class FeatureCatalog
{
    private static readonly IReadOnlyDictionary<uint, string> NoNames = new Dictionary<uint, string>();

    public static FeatureDefinition Brightness { get; } = new(VcpCode.Brightness, "Brightness", FeatureKind.Continuous, FeatureRisk.Safe, NoNames);
    public static FeatureDefinition Contrast { get; } = new(VcpCode.Contrast, "Contrast", FeatureKind.Continuous, FeatureRisk.Safe, NoNames);
    public static FeatureDefinition Volume { get; } = new(VcpCode.AudioVolume, "Volume", FeatureKind.Continuous, FeatureRisk.Safe, NoNames);

    public static FeatureDefinition Mute { get; } = new(VcpCode.AudioMute, "Mute", FeatureKind.Toggle, FeatureRisk.Safe,
        new Dictionary<uint, string> { [1] = "Muted", [2] = "Sound on" });

    public static FeatureDefinition ColorPreset { get; } = new(VcpCode.ColorPreset, "Color preset", FeatureKind.Choice, FeatureRisk.Safe,
        new Dictionary<uint, string>
        {
            [0x01] = "sRGB", [0x02] = "Native", [0x03] = "4000 K", [0x04] = "5000 K", [0x05] = "6500 K",
            [0x06] = "7500 K", [0x07] = "8200 K", [0x08] = "9300 K", [0x09] = "10000 K", [0x0A] = "11500 K",
            [0x0B] = "User 1", [0x0C] = "User 2", [0x0D] = "User 3"
        });

    public static FeatureDefinition RedGain { get; } = new(VcpCode.RedGain, "Red", FeatureKind.Continuous, FeatureRisk.Safe, NoNames);
    public static FeatureDefinition GreenGain { get; } = new(VcpCode.GreenGain, "Green", FeatureKind.Continuous, FeatureRisk.Safe, NoNames);
    public static FeatureDefinition BlueGain { get; } = new(VcpCode.BlueGain, "Blue", FeatureKind.Continuous, FeatureRisk.Safe, NoNames);

    public static FeatureDefinition DisplayMode { get; } = new(VcpCode.DisplayMode, "Picture mode", FeatureKind.Choice, FeatureRisk.Safe,
        new Dictionary<uint, string>
        {
            [0x00] = "Standard", [0x01] = "Productivity", [0x02] = "Mixed", [0x03] = "Movie", [0x04] = "User",
            [0x05] = "Games", [0x06] = "Sports", [0x07] = "Professional", [0x08] = "Standard (intermediate)",
            [0x09] = "Standard (low power)", [0x0A] = "Demonstration", [0xF0] = "Dynamic contrast"
        });

    public static FeatureDefinition PowerMode { get; } = new(VcpCode.PowerMode, "Power", FeatureKind.Choice, FeatureRisk.Disruptive,
        new Dictionary<uint, string> { [1] = "On", [2] = "Standby", [3] = "Suspend", [4] = "Off (soft)", [5] = "Off (hard)" });

    public static FeatureDefinition Input { get; } = new(VcpCode.InputSource, "Input", FeatureKind.Choice, FeatureRisk.Disruptive,
        new Dictionary<uint, string>());

    public static IReadOnlyList<FeatureDefinition> All { get; } =
    [
        Brightness, Contrast, Volume, Mute, ColorPreset, DisplayMode, RedGain, GreenGain, BlueGain, PowerMode, Input
    ];

    public static FeatureDefinition? Find(byte code) => All.FirstOrDefault(f => f.Code == code);

    /// <summary>
    /// MCCS commands (degauss, factory resets, auto setup, save/restore). Never read or written by scans or
    /// generic controls: a stray write could wipe the user's monitor settings.
    /// </summary>
    public static bool IsCommand(byte code) => code is 0x01 or 0x03 or 0x04 or 0x05 or 0x06 or 0x08 or 0x0A or 0x1E or 0x1F or 0xB0;

    /// <summary>Read-only information: active control, frequencies, panel type, usage time, firmware, VCP version.</summary>
    public static bool IsInformational(byte code) =>
        code is 0x02 or 0x52 or 0xAC or 0xAE or 0xB2 or 0xB6 or 0xC0 or 0xC6 or 0xC8 or 0xC9 or 0xDF;

    /// <summary>Values that change on their own (timers, measured frequencies); ignored when comparing snapshots.</summary>
    public static bool IsVolatile(byte code) => code is 0x02 or 0x52 or 0xAC or 0xAE or 0xC0;

    /// <summary>Placeholder for an advertised code nobody has mapped yet. Read-only.</summary>
    public static FeatureDefinition Unmapped(byte code, bool hasValueList) => new(
        code,
        code >= 0xE0 ? $"Vendor 0x{code:X2}" : $"VCP 0x{code:X2}",
        hasValueList ? FeatureKind.Choice : FeatureKind.Continuous,
        FeatureRisk.Unmapped,
        NoNames);

    /// <summary>Accepts names used in scene files: "brightness", "contrast", "volume", "red", "picture-mode" ...</summary>
    public static FeatureDefinition? FindByName(string name)
    {
        string key = Normalize(name);
        return All.FirstOrDefault(f => Normalize(f.Name) == key) ?? key switch
        {
            "redgain" => RedGain,
            "greengain" => GreenGain,
            "bluegain" => BlueGain,
            "displaymode" or "mode" => DisplayMode,
            "audiovolume" => Volume,
            "colorpreset" or "colortemperature" => ColorPreset,
            _ => null
        };
    }

    private static string Normalize(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>A control available on one display, with the values that display accepts.</summary>
public sealed record MonitorFeature(FeatureDefinition Definition, IReadOnlyList<uint> AllowedValues)
{
    public byte Code => Definition.Code;
    public string Name => Definition.Name;
    public FeatureKind Kind => Definition.Kind;
}
