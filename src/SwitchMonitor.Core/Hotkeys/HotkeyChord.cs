using System.Globalization;

namespace SwitchMonitor.Core.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8
}

/// <summary>
/// A global shortcut such as "Ctrl+Alt+1" or "Ctrl+Alt+NumpadAdd", in the RegisterHotKey format
/// (modifier flags + Windows virtual-key code). Parsing needs no UI framework so it can be tested anywhere.
/// </summary>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, uint VirtualKey)
{
    private static readonly Dictionary<string, uint> Named = BuildKeyTable();
    private static readonly Dictionary<uint, string> Names = Named
        .GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);

    /// <summary>
    /// Safe as a global shortcut: Ctrl, Alt or Win must be part of it (a bare key or Shift+key would steal typing).
    /// </summary>
    public bool IsAllowed => (Modifiers & ~HotkeyModifiers.Shift) != HotkeyModifiers.None && VirtualKey is > 0 and < 0xFF
        && !IsModifierKey(VirtualKey);

    /// <summary>Shift, Ctrl, Alt and Windows keys themselves (VK 0x10-0x12, 0x5B-0x5C, 0xA0-0xA5).</summary>
    public static bool IsModifierKey(uint virtualKey) => virtualKey is >= 0x10 and <= 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;

    /// <summary>Text form used in settings files, e.g. "Ctrl+Alt+NumpadAdd".</summary>
    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(Names.TryGetValue(VirtualKey, out var name) ? name : $"0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }

    /// <summary>
    /// Parses "Ctrl+Alt+1". At least one modifier is required (a bare key would steal normal typing),
    /// and Shift alone is not enough for the same reason.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 5) return false;
        var modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            var flag = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None
            };
            if (flag == HotkeyModifiers.None || modifiers.HasFlag(flag)) return false;
            modifiers |= flag;
        }
        if ((modifiers & ~HotkeyModifiers.Shift) == HotkeyModifiers.None) return false;
        if (!TryParseKey(parts[^1], out uint key)) return false;
        chord = new HotkeyChord(modifiers, key);
        return true;
    }

    public static HotkeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"\"{text}\" is not a valid shortcut (example: Ctrl+Alt+1).");

    private static bool TryParseKey(string name, out uint key)
    {
        if (Named.TryGetValue(name.ToLowerInvariant(), out key)) return true;
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key) && key is > 0 and < 0xFF)
            return true;
        key = 0;
        return false;
    }

    private static Dictionary<string, uint> BuildKeyTable()
    {
        var table = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        for (char c = 'A'; c <= 'Z'; c++) table[c.ToString()] = c;
        for (char c = '0'; c <= '9'; c++) table[c.ToString()] = c;
        for (int i = 1; i <= 24; i++) table[$"F{i}"] = (uint)(0x6F + i);
        for (int i = 0; i <= 9; i++) table[$"Numpad{i}"] = (uint)(0x60 + i);
        void Add(uint vk, params string[] names) { foreach (var n in names) table[n] = vk; }
        Add(0x6A, "NumpadMultiply", "Multiply");
        Add(0x6B, "NumpadAdd", "Add");
        Add(0x6D, "NumpadSubtract", "Subtract");
        Add(0x6E, "NumpadDecimal", "Decimal");
        Add(0x6F, "NumpadDivide", "Divide");
        Add(0x25, "Left"); Add(0x26, "Up"); Add(0x27, "Right"); Add(0x28, "Down");
        Add(0x21, "PageUp"); Add(0x22, "PageDown"); Add(0x23, "End"); Add(0x24, "Home");
        Add(0x2D, "Insert"); Add(0x2E, "Delete"); Add(0x20, "Space"); Add(0x0D, "Enter");
        Add(0xBB, "Plus", "OemPlus", "Equals"); Add(0xBD, "Minus", "OemMinus");
        Add(0xBC, "Comma", "OemComma"); Add(0xBE, "Period", "OemPeriod");
        Add(0xBA, "Semicolon", "Oem1"); Add(0xBF, "Slash", "Oem2"); Add(0xC0, "Backquote", "Oem3");
        Add(0xDB, "OpenBracket", "Oem4"); Add(0xDC, "Backslash", "Oem5"); Add(0xDD, "CloseBracket", "Oem6");
        Add(0xDE, "Quote", "Oem7"); Add(0xE2, "Oem102");
        Add(0x09, "Tab"); Add(0x08, "Backspace"); Add(0x13, "Pause"); Add(0x2C, "PrintScreen"); Add(0x91, "ScrollLock");
        Add(0xAD, "VolumeMute"); Add(0xAE, "VolumeDown"); Add(0xAF, "VolumeUp");
        return table;
    }
}
