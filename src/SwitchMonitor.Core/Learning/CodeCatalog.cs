using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Learning;

/// <summary>How the Learn settings wizard treats a found code.</summary>
public enum CodeCategory
{
    /// <summary>Could be a setting of the monitor's menu: offered for a test.</summary>
    Setting,
    /// <summary>Information the monitor reports (lists, ids, counters, analog-only adjustments): shown, never written.</summary>
    ReadOnly,
    /// <summary>Known to reboot, reset or rewrite the monitor's memory: hidden and never written.</summary>
    Sensitive
}

/// <summary>What is known about a code: a short description, how it is treated, and where the description comes from.</summary>
public sealed record CodeInfo(string Description, CodeCategory Category, string? Source = null);

/// <summary>
/// Descriptions of codes the wizard can find. Standard codes follow MCCS 2.2a; vendor codes come from public notes
/// (LG's own enum names collected by owners and the ddcutil / ddccontrol projects, 2026-10-30) and are per manufacturer
/// (the first three letters of the PnP ID: GSM = LG). Unknown codes get a description from their range.
/// </summary>
public static class CodeCatalog
{
    private const string Mccs = "MCCS 2.2a";
    private const string LgNotes = "LG enum names (LG app), ddcutil issues #331/#419, ddccontrol-db #44";

    private static readonly Dictionary<byte, CodeInfo> Standard = new()
    {
        [0x0B] = new("Color temperature step: how many kelvin one step of 0x0C is. Information.", CodeCategory.ReadOnly, Mccs),
        [0x0C] = new("Color temperature request (in steps of 0x0B). Many monitors ignore it or keep one value.", CodeCategory.ReadOnly, Mccs),
        [0x0E] = new("Pixel clock: only adjusts a VGA (analog) picture; nothing on HDMI or DisplayPort.", CodeCategory.ReadOnly, Mccs),
        [0x20] = new("Horizontal position: only moves a VGA (analog) picture.", CodeCategory.ReadOnly, Mccs),
        [0x30] = new("Vertical position: only moves a VGA (analog) picture.", CodeCategory.ReadOnly, Mccs),
        [0x3E] = new("Clock phase: only sharpens a VGA (analog) picture.", CodeCategory.ReadOnly, Mccs),
        [0x59] = new("6-axis saturation, red.", CodeCategory.Setting, Mccs),
        [0x5A] = new("6-axis saturation, yellow.", CodeCategory.Setting, Mccs),
        [0x5B] = new("6-axis saturation, green.", CodeCategory.Setting, Mccs),
        [0x5C] = new("6-axis saturation, cyan.", CodeCategory.Setting, Mccs),
        [0x5D] = new("6-axis saturation, blue.", CodeCategory.Setting, Mccs),
        [0x5E] = new("6-axis saturation, magenta.", CodeCategory.Setting, Mccs),
        [0x6C] = new("Black level, red.", CodeCategory.Setting, Mccs),
        [0x6E] = new("Black level, green.", CodeCategory.Setting, Mccs),
        [0x70] = new("Black level, blue.", CodeCategory.Setting, Mccs),
        [0x72] = new("Gamma.", CodeCategory.Setting, Mccs),
        [0x87] = new("Sharpness.", CodeCategory.Setting, Mccs),
        [0x8A] = new("Color saturation.", CodeCategory.Setting, Mccs),
        [0x90] = new("Hue.", CodeCategory.Setting, Mccs),
        [0xAA] = new("Screen orientation. Information.", CodeCategory.ReadOnly, Mccs),
        [0xC0] = new("Hours the monitor has been on. Information.", CodeCategory.ReadOnly, Mccs),
        [0xC9] = new("Firmware version. Information.", CodeCategory.ReadOnly, Mccs),
        [0xDF] = new("MCCS version. Information.", CodeCategory.ReadOnly, Mccs),
    };

    private static readonly Dictionary<string, Dictionary<byte, CodeInfo>> Vendor = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GSM"] = new()
        {
            [0x4D] = new("LG: list of picture modes (part 1). Information the monitor reports, not a setting.", CodeCategory.ReadOnly, LgNotes),
            [0x4E] = new("LG: list of picture modes (part 2). Information.", CodeCategory.ReadOnly, LgNotes),
            [0x4F] = new("LG: list of picture modes (part 3). Information.", CodeCategory.ReadOnly, LgNotes),
            [0xE4] = new("LG: service/diagnostic mode. Writing it opened a diagnostic screen and reset the hours counter on another LG.", CodeCategory.Sensitive, LgNotes),
            [0xEB] = new("LG: writes a new color matrix to the monitor's memory (some notes say OSD lock).", CodeCategory.Sensitive, LgNotes),
            [0xEF] = new("LG: display ID. Information.", CodeCategory.ReadOnly, LgNotes),
            [0xF5] = new("LG: aspect ratio value; other notes call it a system reset or the picture-by-picture input.", CodeCategory.Sensitive, LgNotes),
            [0xF6] = new("LG: SES; writing it reboots the monitor.", CodeCategory.Sensitive, LgNotes),
            [0xFA] = new("LG: Motion Blur Reduction (on models that have it).", CodeCategory.Setting, LgNotes),
            [0xFB] = new("LG: scaler ISP / EEPROM area: the monitor's internal memory.", CodeCategory.Sensitive, LgNotes),
            [0xFD] = new("LG: power LED on or off.", CodeCategory.Setting, LgNotes),
        }
    };

    /// <summary>What is known about <paramref name="code"/> on a monitor whose PnP ID is <paramref name="pnpId"/>.</summary>
    public static CodeInfo Describe(byte code, string? pnpId, uint maximum)
    {
        string? maker = pnpId is { Length: >= 3 } ? pnpId[..3] : null;
        if (maker is not null && Vendor.TryGetValue(maker, out var codes) && codes.TryGetValue(code, out var vendor)) return vendor;
        if (Standard.TryGetValue(code, out var standard)) return standard;
        if (FeatureCatalog.IsInformational(code) || FeatureCatalog.IsVolatile(code))
            return new("Information the monitor reports. Never written.", CodeCategory.ReadOnly, Mccs);
        // A full 16-bit range is data (ids, lists, counters), not a menu setting.
        if (maximum >= 0xFFFF) return new("Reports 0-65535: looks like data (an id, a list or a counter), not a menu setting.", CodeCategory.ReadOnly);
        if (code >= 0xE0) return new("Manufacturer's own code, not documented: may be a menu setting.", CodeCategory.Setting);
        return new($"MCCS code \"{VcpCode.Describe(code)}\": not used by this app yet.", CodeCategory.Setting, Mccs);
    }
}
