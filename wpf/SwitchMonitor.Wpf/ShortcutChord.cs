using System.Windows.Input;

namespace SwitchMonitor.Wpf;

internal readonly record struct ShortcutChord(uint Modifiers, uint VirtualKey, string Label)
{
    public string Identity => $"{Modifiers}:{VirtualKey}";

    public static bool TryParse(string? text, out ShortcutChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Length > 4) return false;
        uint modifiers = 0;
        foreach (string part in parts[..^1])
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= 0x2; break;
                case "alt": modifiers |= 0x1; break;
                case "shift": modifiers |= 0x4; break;
                case "win": case "windows": modifiers |= 0x8; break;
                default: return false;
            }
        if (modifiers == 0) return false;
        string keyName = parts[^1] switch
        {
            "NumpadAdd" => "Add",
            "NumpadSubtract" => "Subtract",
            "NumpadMultiply" => "Multiply",
            var name => name
        };
        if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName;
        if (!Enum.TryParse(keyName, true, out Key key)) return false;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;
        chord = new ShortcutChord(modifiers, virtualKey, Format(modifiers, key));
        return true;
    }

    public static bool TryFromKey(Key key, ModifierKeys keys, out ShortcutChord chord)
    {
        chord = default;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
            Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
        uint modifiers = 0;
        if (keys.HasFlag(ModifierKeys.Control)) modifiers |= 0x2;
        if (keys.HasFlag(ModifierKeys.Alt)) modifiers |= 0x1;
        if (keys.HasFlag(ModifierKeys.Shift)) modifiers |= 0x4;
        if (keys.HasFlag(ModifierKeys.Windows)) modifiers |= 0x8;
        if (modifiers == 0 || System.Numerics.BitOperations.PopCount(modifiers) > 3) return false;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;
        chord = new ShortcutChord(modifiers, virtualKey, Format(modifiers, key));
        return true;
    }

    private static string Format(uint modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & 0x2) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x1) != 0) parts.Add("Alt");
        if ((modifiers & 0x4) != 0) parts.Add("Shift");
        if ((modifiers & 0x8) != 0) parts.Add("Win");
        parts.Add(key switch
        {
            Key.Add => "NumpadAdd", Key.Subtract => "NumpadSubtract",
            Key.Multiply => "NumpadMultiply",
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString()
        });
        return string.Join('+', parts);
    }
}
