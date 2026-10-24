using System.Globalization;
using System.Text.Json;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Scenes;

/// <summary>One control in a scene: a percentage for continuous controls, a raw MCCS value otherwise.</summary>
public sealed record SceneSetting(FeatureDefinition Feature, double? Percent, uint? Value)
{
    /// <summary>
    /// Other ways to satisfy the same line, tried in order when the monitor lacks this one. "picture mode":
    /// ["FPS Game 1", "Games"] is LG picture mode FPS Game 1 first, then the standard MCCS Games mode.
    /// </summary>
    public IReadOnlyList<SceneSetting> Alternatives { get; init; } = [];

    /// <summary>This setting followed by its alternatives.</summary>
    public IEnumerable<SceneSetting> Candidates => Alternatives.Prepend(this);

    public override string ToString() => Percent is double p
        ? $"{Feature.Name} {p:0.#}%"
        : $"{Feature.Name} {Feature.DescribeValue(Value ?? 0)}";
}

/// <summary>
/// A named set of monitor settings applied together, e.g. "Night": brightness 15 %, color preset 5000 K.
/// Power and input are never part of a scene: they can leave the user without a picture.
/// </summary>
public sealed record SceneProfile(string Name, IReadOnlyList<SceneSetting> Settings, string? MonitorFilter = null,
    Hotkeys.HotkeyChord? Hotkey = null)
{
    /// <summary>True when the scene applies to a monitor (no filter, or the name contains the filter).</summary>
    public bool AppliesTo(MonitorDevice monitor) =>
        string.IsNullOrWhiteSpace(MonitorFilter) || monitor.Name.Contains(MonitorFilter, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads and writes scenes.json in the v2 data directory.</summary>
public static class SceneStore
{
    public const string FileName = "scenes.json";

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, FileName);

    /// <summary>Scenes created on first run. Controls a monitor lacks are skipped when applied.</summary>
    /// <summary>
    /// Scenes (shown as "Profiles") created on first run. Controls a monitor lacks are skipped when applied; a list of values
    /// is tried in order, so one "picture mode" line serves the LG (FPS Game 1) and standard monitors (Games).
    /// No shortcuts by default: Ctrl+Alt+1..9 belong to inputs, as in 1.x.
    /// </summary>
    public const string DefaultJson = """
        {
          "version": 3,
          "scenes": [
            { "name": "Work",  "settings": { "brightness": 70, "contrast": 70 } },
            { "name": "Night", "settings": { "brightness": 15, "color preset": "6500 K" } },
            { "name": "Game",  "settings": { "brightness": 100, "picture mode": ["FPS Game 1", "Games"] } }
          ]
        }
        """;

    /// <summary>The first preview's defaults, which took Ctrl+Alt+1..3 from the input shortcuts; replaced on load.</summary>
    internal const string PreviewDefaultJson = """
        {
          "version": 1,
          "scenes": [
            { "name": "Work",  "hotkey": "Ctrl+Alt+1", "settings": { "brightness": 70, "contrast": 70 } },
            { "name": "Night", "hotkey": "Ctrl+Alt+2", "settings": { "brightness": 15, "color preset": "6500 K" } },
            { "name": "Game",  "hotkey": "Ctrl+Alt+3", "settings": { "brightness": 100, "picture mode": "Games" } }
          ]
        }
        """;

    public static IReadOnlyList<SceneProfile> LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path) || IsPreviewDefault(File.ReadAllText(path)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultJson);
        }
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Returns <paramref name="json"/> with each named scene's "hotkey" set (or removed for null). Everything else
    /// the user wrote is kept; comments are dropped. Unknown scene names are ignored.
    /// </summary>
    public static string SetHotkeys(string json, IReadOnlyDictionary<string, Hotkeys.HotkeyChord?> hotkeys)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new FormatException("scenes.json is empty.");
        if (root["scenes"] is not System.Text.Json.Nodes.JsonArray scenes)
            throw new FormatException("scenes.json must contain a \"scenes\" array.");
        foreach (var scene in scenes.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            string? name = scene["name"]?.GetValue<string>()?.Trim();
            var match = hotkeys.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (match is null) continue;
            if (hotkeys[match] is { } chord) scene["hotkey"] = chord.ToString();
            else scene.Remove("hotkey");
        }
        string result = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Parse(result);   // never write a file the app could not read back
        return result;
    }

    public static void SaveHotkeys(IReadOnlyDictionary<string, Hotkeys.HotkeyChord?> hotkeys, string? path = null)
    {
        path ??= DefaultPath;
        string updated = SetHotkeys(File.ReadAllText(path), hotkeys);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, updated);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The 24/10 preview default, which named "LG picture mode" separately; replaced on load because picture mode is now one
    /// line that works on any monitor.
    /// </summary>
    internal const string Preview2DefaultJson = """
        {
          "version": 2,
          "scenes": [
            { "name": "Work",  "settings": { "brightness": 70, "contrast": 70 } },
            { "name": "Night", "settings": { "brightness": 15, "color preset": "6500 K" } },
            { "name": "Game",  "settings": { "brightness": 100, "picture mode": "Games", "LG picture mode": "FPS Game 1" } }
          ]
        }
        """;

    internal static bool IsPreviewDefault(string json)
    {
        string compact = Compact(json);
        return compact == Compact(PreviewDefaultJson) || compact == Compact(Preview2DefaultJson);
    }

    private static string Compact(string json) => string.Concat(json.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>Vendor controls known from quirks.json and this computer's learned mappings.</summary>
    private static IEnumerable<FeatureDefinition> KnownVendorControls()
    {
        Quirks.QuirkDatabase database;
        try { database = Quirks.QuirkDatabase.LoadDefault(); }
        catch (Exception error) when (error is FormatException or JsonException or IOException) { database = Quirks.QuirkDatabase.LoadEmbedded(); }
        return database.All.SelectMany(q => q.Controls);
    }

    /// <param name="vendorControls">Vendor controls a scene may name (default: every quirk's controls).</param>
    public static IReadOnlyList<SceneProfile> Parse(string json, IEnumerable<FeatureDefinition>? vendorControls = null)
    {
        var vendor = (vendorControls ?? KnownVendorControls()).ToArray();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (!document.RootElement.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
            throw new FormatException("scenes.json must contain a \"scenes\" array.");

        var result = new List<SceneProfile>();
        foreach (var scene in scenes.EnumerateArray())
        {
            string name = scene.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } text
                ? text.Trim()
                : throw new FormatException("Every scene needs a \"name\".");
            if (result.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"Scene \"{name}\" is defined twice.");
            if (!scene.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                throw new FormatException($"Scene \"{name}\" needs a \"settings\" object.");

            var parsed = new List<SceneSetting>();
            var seen = new HashSet<string>();
            foreach (var property in settings.EnumerateObject())
            {
                string key = Simplify(property.Name);
                // Every control with this name: the MCCS one and vendor ones (LG "Picture mode" is VCP 0x15, MCCS is 0xDC).
                var definitions = new List<FeatureDefinition>();
                if (FeatureCatalog.FindByName(property.Name) is { } standard) definitions.Add(standard);
                definitions.AddRange(vendor.Where(v => Simplify(v.Name) == key || v.Aliases.Any(a => Simplify(a) == key)));
                if (definitions.Count == 0)
                    throw new FormatException($"Scene \"{name}\": unknown control \"{property.Name}\".");
                if (definitions.Any(d => d.Risk == FeatureRisk.Disruptive))
                    throw new FormatException($"Scene \"{name}\": {definitions[0].Name} cannot be part of a scene.");
                definitions.RemoveAll(d => d.Risk is FeatureRisk.ReadOnly or FeatureRisk.Unmapped);
                if (definitions.Count == 0)
                    throw new FormatException($"Scene \"{name}\": {property.Name} cannot be changed over DDC/CI.");
                if (!seen.Add(Simplify(definitions[0].Name)))
                    throw new FormatException($"Scene \"{name}\": {definitions[0].Name} appears twice.");
                parsed.Add(ParseSetting(name, definitions, property.Value));
            }
            if (parsed.Count == 0) throw new FormatException($"Scene \"{name}\" has no settings.");

            string? filter = scene.TryGetProperty("monitor", out var m) ? m.GetString() : null;
            Hotkeys.HotkeyChord? hotkey = null;
            if (scene.TryGetProperty("hotkey", out var h) && h.GetString() is { Length: > 0 } hotkeyText)
                hotkey = Hotkeys.HotkeyChord.TryParse(hotkeyText, out var chord)
                    ? chord
                    : throw new FormatException($"Scene \"{name}\": \"{hotkeyText}\" is not a valid shortcut (example: Ctrl+Alt+1).");
            if (hotkey is not null && result.Any(s => s.Hotkey == hotkey))
                throw new FormatException($"Scene \"{name}\": shortcut {hotkey} is already used by another scene.");
            result.Add(new SceneProfile(name, parsed, filter, hotkey));
        }
        return result;
    }

    /// <summary>A value, or a list of values tried in order: "FPS Game 1" or ["FPS Game 1", "Games"].</summary>
    private static SceneSetting ParseSetting(string scene, IReadOnlyList<FeatureDefinition> definitions, JsonElement value)
    {
        var values = value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
        if (values.Length == 0) throw new FormatException($"Scene \"{scene}\", {definitions[0].Name}: the value list is empty.");
        var candidates = new List<SceneSetting>();
        string? firstError = null;
        foreach (var item in values)
            foreach (var feature in definitions)
            {
                var (setting, error) = ParseOne(scene, feature, item);
                if (setting is not null) candidates.Add(setting);
                else firstError ??= error;
            }
        if (candidates.Count == 0) throw new FormatException(firstError ?? $"Scene \"{scene}\": invalid value.");
        return candidates[0] with { Alternatives = candidates.Skip(1).ToArray() };
    }

    private static (SceneSetting? Setting, string? Error) ParseOne(string scene, FeatureDefinition feature, JsonElement value)
    {
        string where = $"Scene \"{scene}\", {feature.Name}";
        switch (feature.Kind)
        {
            case FeatureKind.Continuous:
                return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double percent) && percent is >= 0 and <= 100
                    ? (new SceneSetting(feature, percent, null), null)
                    : (null, $"{where}: use a percentage from 0 to 100.");

            case FeatureKind.Toggle:
                // Mute: true / "muted" → 1, false / "sound on" → 2.
                uint? toggle = value.ValueKind switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 2,
                    JsonValueKind.String => NamedValue(feature, value.GetString()!),
                    _ => null
                };
                return toggle is uint t ? (new SceneSetting(feature, null, t), null) : (null, $"{where}: use true or false.");

            default:
                uint? choice = value.ValueKind switch
                {
                    JsonValueKind.String => NamedValue(feature, value.GetString()!),
                    JsonValueKind.True => OnOff(feature, true),
                    JsonValueKind.False => OnOff(feature, false),
                    _ => null
                };
                return choice is uint c
                    ? (new SceneSetting(feature, null, c), null)
                    : (null, $"{where}: unknown value. Use a name such as \"{feature.ValueNames.Values.FirstOrDefault()}\" or a hex code such as \"0x05\".");
        }
    }

    /// <summary>true/false for two-option controls named On/Off (FreeSync).</summary>
    private static uint? OnOff(FeatureDefinition feature, bool on) =>
        feature.ValueNames.Where(pair => Simplify(pair.Value) == (on ? "on" : "off")).Select(pair => (uint?)pair.Key).FirstOrDefault();

    /// <summary>"5000 K", "games", "0x05" or "05" → raw value.</summary>
    internal static uint? NamedValue(FeatureDefinition feature, string text)
    {
        string trimmed = text.Trim();
        foreach (var (raw, name) in feature.ValueNames)
            if (Simplify(name) == Simplify(trimmed)) return raw;
        string hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        return hex.Length is > 0 and <= 4 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint code)
            && (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || hex.All(char.IsAsciiHexDigit) && hex.Length == 2)
            ? code : null;
    }

    private static string Simplify(string text) =>
        new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
