using System.Text;
using Microsoft.Win32;

namespace SwitchMonitor.Core.Displays;

/// <summary>
/// What a monitor says about itself in its EDID: its own name ("Odyssey G5"), its serial number and when it was made.
/// Windows names many monitors "Generic PnP Monitor"; the EDID is where the real name lives. The serial number is the
/// one thing that tells two identical monitors apart and survives a change of port or of computer (the 1.x profiles used it).
/// </summary>
public sealed record EdidInfo(string? Name, string? Serial, int? Week, int? Year)
{
    /// <summary>"Odyssey G5, serial H4ZR600017, week 12 of 2023", for logs and reports.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Name is not null) parts.Add(Name);
        parts.Add(Serial is null ? "no serial number" : $"serial {Serial}");
        if (Week is > 0 && Year is > 0) parts.Add($"week {Week} of {Year}");
        return string.Join(", ", parts);
    }
}

public static class MonitorEdid
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>Reads the 128-byte base block; null when it is not a valid EDID.</summary>
    public static EdidInfo? Parse(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return null;
        for (int i = 0; i < Header.Length; i++)
            if (edid[i] != Header[i]) return null;

        string? name = null, serialText = null;
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0) continue;   // a timing, not a text descriptor
            string text = Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n')[0].Trim();
            if (text.Length == 0 || text.Any(char.IsControl)) continue;
            if (edid[offset + 3] == 0xFC) name = text;
            else if (edid[offset + 3] == 0xFF) serialText = text;
        }

        // The numeric serial (bytes 12-15) is often 0 or 1 on monitors that do not number their units.
        uint number = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));
        string? serial = serialText ?? (number > 1 ? number.ToString("X8") : null);
        int week = edid[16], year = 1990 + edid[17];
        return new EdidInfo(name, serial, week, year);
    }

    /// <summary>
    /// The EDID of the monitor Windows calls <c>MONITOR\SAM7488\{4d36e96e-…}\0007</c>: the registry lists each monitor under
    /// Enum\DISPLAY\SAM7488\&lt;instance&gt; with a "Driver" value naming that same <c>{guid}\0007</c> key.
    /// Null when it is not found, so a name or a serial is never made up.
    /// </summary>
    public static EdidInfo? ReadFor(string? deviceId)
    {
        try
        {
            var parts = (deviceId ?? "").Split('\\');
            if (parts.Length < 4 || !parts[0].Equals("MONITOR", StringComparison.OrdinalIgnoreCase)) return null;
            string driver = parts[2] + "\\" + parts[3];
            using var model = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}");
            if (model is null) return null;
            foreach (string instance in model.GetSubKeyNames())
            {
                using var key = model.OpenSubKey(instance);
                if (key?.GetValue("Driver") is not string value || !value.Equals(driver, StringComparison.OrdinalIgnoreCase)) continue;
                using var parameters = key.OpenSubKey("Device Parameters");
                return Parse(parameters?.GetValue("EDID") as byte[]);
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            // No registry access: the monitor simply has no EDID details.
        }
        return null;
    }
}
