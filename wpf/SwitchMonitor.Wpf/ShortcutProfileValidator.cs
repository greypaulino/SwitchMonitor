using System.IO;

namespace SwitchMonitor.Wpf;

internal static class ShortcutProfileValidator
{
    internal static void Validate(Preferences profile) =>
        Validate(profile.GlobalShortcuts, profile.PortShortcuts);

    internal static void Validate(IReadOnlyDictionary<string, string> globals,
        IReadOnlyDictionary<string, Dictionary<int, string>> ports)
    {
        var seen = new Dictionary<string, string>();
        void Add(string? text, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!ShortcutChord.TryParse(text, out var chord))
                throw new InvalidDataException($"Unsupported shortcut in {label}: {text}");
            if (seen.TryGetValue(chord.Identity, out string? previous))
                throw new InvalidDataException($"Shortcut {text} conflicts between {previous} and {label}.");
            seen[chord.Identity] = label;
        }
        foreach (var pair in globals) Add(pair.Value, pair.Key);
        foreach (var monitor in ports)
            foreach (var port in monitor.Value)
                Add(port.Value, $"{monitor.Key} / {MonitorService.PortName(port.Key)}");
    }
}
