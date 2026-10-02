using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchMonitor.Wpf;

internal sealed record LegacyImportPreview(Preferences Proposed, string Summary);

internal static class LegacySettingsImporter
{
    public static string InstalledPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SwitchMonitor", "switchMonitor.ini");

    public static LegacyImportPreview Preview(string path, Preferences current,
        IReadOnlyList<PhysicalMonitor> monitors)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is 0 or > 1_048_576)
            throw new InvalidDataException("Select a non-empty SwitchMonitor INI file smaller than 1 MB.");
        var ini = ParseIni(File.ReadAllText(path));
        if (!ini.ContainsKey("GlobalShortcuts") && !ini.ContainsKey("General") &&
            !ini.Values.Any(section => section.ContainsKey("MonitorID")))
            throw new InvalidDataException("This file does not look like SwitchMonitor settings.");

        var proposal = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(current))
            ?? throw new InvalidDataException("Could not copy the WPF settings.");
        var summary = new StringBuilder();
        summary.AppendLine("The AutoHotkey INI will remain unchanged.");
        summary.AppendLine();
        var defaults = new Dictionary<string, string>
        {
            ["cycle"] = "Ctrl|Alt|M", ["settings"] = "Ctrl|Alt|Shift|M",
            ["brightnessDown"] = "Ctrl|Alt|NumpadSub",
            ["brightnessUp"] = "Ctrl|Alt|NumpadAdd",
            ["brightnessNext"] = "Ctrl|Alt|NumpadMult"
        };
        summary.AppendLine("Global shortcuts:");
        foreach (var item in defaults)
        {
            string saved = Get(ini, "GlobalShortcuts", item.Key) ?? item.Value;
            string chord = ConvertChord(saved);
            proposal.GlobalShortcuts[item.Key] = chord;
            summary.AppendLine($"  {item.Key}: {(chord.Length == 0 ? "None" : chord)}");
        }
        proposal.LinkedBrightness = Get(ini, "Brightness", "Linked") == "1";
        summary.AppendLine($"Brightness linked: {(proposal.LinkedBrightness ? "Yes" : "No")}");
        summary.AppendLine();

        int matched = 0;
        string? selectedLegacy = Get(ini, "General", "SelectedMonitor");
        var profileSections = ini.Where(pair => pair.Value.ContainsKey("MonitorID")).ToArray();
        foreach (var monitor in monitors)
        {
            var sections = profileSections.Where(pair =>
                NormalizeId(pair.Value["MonitorID"]) == NormalizeId(monitor.DeviceId)).ToArray();
            if (sections.Length == 0) continue;
            if (sections.Length != 1)
                throw new InvalidDataException($"More than one legacy profile matches {monitor.Name}; import stopped.");
            matched++;
            var section = sections[0];
            var saved = section.Value;
            var disabled = ParseCodes(saved.GetValueOrDefault("DisabledCodes"));
            var marked = ParseCodes(saved.GetValueOrDefault("ConnectedCodes"));
            proposal.ConnectedInputs[monitor.Key] = monitor.InputCodes
                .Where(code => marked.Contains(code) && !disabled.Contains(code)).ToList();
            var shortcuts = monitor.InputCodes.ToDictionary(code => code, _ => "");
            for (int slot = 1; slot <= 9; slot++)
            {
                if (!int.TryParse(saved.GetValueOrDefault($"Slot{slot}Code"), out int code) ||
                    !shortcuts.ContainsKey(code) || disabled.Contains(code) ||
                    string.IsNullOrWhiteSpace(saved.GetValueOrDefault($"Slot{slot}Name"))) continue;
                shortcuts[code] = ConvertChord(saved.GetValueOrDefault($"Slot{slot}Shortcut")
                    ?? $"Ctrl|Alt|{slot}");
            }
            proposal.PortShortcuts[monitor.Key] = shortcuts;
            if (section.Key.Equals(selectedLegacy, StringComparison.OrdinalIgnoreCase))
            {
                proposal.SelectedMonitor = monitor.Key;
                proposal.BrightnessMonitor = monitor.Key;
            }
            summary.AppendLine(monitor.Name + ":");
            foreach (int code in monitor.InputCodes)
            {
                string chord = shortcuts[code];
                summary.AppendLine($"  {MonitorService.PortName(code)}: " +
                    $"{(proposal.ConnectedInputs[monitor.Key].Contains(code) ? "Next input" : "not marked")}, " +
                    (chord.Length == 0 ? "no shortcut" : chord));
            }
            summary.AppendLine();
        }
        if (matched == 0)
            summary.AppendLine("No legacy monitor profile matches a currently detected monitor. " +
                "Only global shortcuts and brightness link state would be imported.");
        ShortcutProfileValidator.Validate(proposal);
        summary.AppendLine("Existing WPF profiles for other monitors will be kept.");
        return new LegacyImportPreview(proposal, summary.ToString());
    }

    private static string ConvertChord(string? legacy)
    {
        if (string.IsNullOrWhiteSpace(legacy)) return "";
        var tokens = legacy.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length < 2) throw new InvalidDataException($"Unsupported AutoHotkey shortcut: {legacy}");
        string last = tokens[^1] switch
        {
            "NumpadSub" => "NumpadSubtract", "NumpadMult" => "NumpadMultiply",
            var name => name
        };
        string proposed = string.Join('+', tokens[..^1].Append(last));
        if (!ShortcutChord.TryParse(proposed, out var parsed))
            throw new InvalidDataException($"The WPF global hotkey system cannot represent: {legacy}");
        return parsed.Label;
    }

    private static HashSet<int> ParseCodes(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(raw => int.TryParse(raw, out int code) ? code : 0)
            .Where(code => code is >= 1 and <= 65535).ToHashSet();

    private static string NormalizeId(string text) =>
        Regex.Replace(text.Trim(), @"\\+", @"\").ToUpperInvariant();

    private static string? Get(Dictionary<string, Dictionary<string, string>> ini,
        string section, string key) => ini.TryGetValue(section, out var values)
            ? values.GetValueOrDefault(key) : null;

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string content)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string section = line[1..^1].Trim();
                if (!result.TryGetValue(section, out current))
                    result[section] = current = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int equal = line.IndexOf('=');
            if (current is not null && equal > 0)
                current[line[..equal].Trim()] = line[(equal + 1)..].Trim();
        }
        return result;
    }
}
