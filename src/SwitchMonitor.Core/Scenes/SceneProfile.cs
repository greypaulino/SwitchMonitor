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

    /// <summary>Color that identifies the profile in the app ("#4C8DFF"), or null for the default of its name.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Icon chosen for the profile: "icon:fluent:game" (a library icon in one of the styles), "image:night.png" (a picture
    /// kept in the app's icons folder), "glyph:E7FC" (earlier versions), or null for the usual icon of its name.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>Programs that turn this profile on while they are in front ("eldenring.exe"), lower case.</summary>
    public IReadOnlyList<string> Apps { get; init; } = [];

    /// <summary>True when <paramref name="exe"/> (a file name such as "Game.exe") is linked to this profile.</summary>
    public bool IsLinkedTo(string exe) => Apps.Contains(SceneStore.AppName(exe), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Reads and writes scenes.json in the v2 data directory.</summary>
public static class SceneStore
{
    public const string FileName = "scenes.json";

    /// <summary>The app's own first profile (the person's hand-made settings); a saved profile cannot take this name.</summary>
    public const string CustomName = "Custom";

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, FileName);

    /// <summary>
    /// scenes.json created on first run: no profiles. Custom (the person's own settings) is the only default; Work, Night,
    /// Game and Theater are templates added on request (<see cref="SceneTemplates"/>).
    /// </summary>
    public const string DefaultJson = """
        {
          "version": 4,
          "scenes": []
        }
        """;

    /// <summary>The 25/10 default, which created Work, Night and Game on first run; replaced on load when unedited.</summary>
    internal const string Preview3DefaultJson = """
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
        return compact == Compact(PreviewDefaultJson) || compact == Compact(Preview2DefaultJson) ||
               compact == Compact(Preview3DefaultJson);
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
            var apps = new List<string>();
            if (scene.TryGetProperty("apps", out var appList))
            {
                if (appList.ValueKind != JsonValueKind.Array)
                    throw new FormatException($"Scene \"{name}\": \"apps\" must be a list of program names.");
                foreach (var app in appList.EnumerateArray())
                    if (app.GetString() is { Length: > 0 } exe && !apps.Contains(AppName(exe), StringComparer.OrdinalIgnoreCase))
                        apps.Add(AppName(exe));
            }
            foreach (string app in apps)
                if (result.FirstOrDefault(s => s.IsLinkedTo(app)) is { } other)
                    throw new FormatException($"Scene \"{name}\": {app} is already linked to \"{other.Name}\".");
            string? color = scene.TryGetProperty("color", out var c) && c.GetString() is { } colorText && IsColor(colorText)
                ? colorText.ToUpperInvariant() : null;
            string? icon = scene.TryGetProperty("icon", out var i) && i.ValueKind == JsonValueKind.String && i.GetString() is { } iconText
                && NormalizeIcon(iconText) is { } validIcon ? validIcon : null;
            result.Add(new SceneProfile(name, parsed, filter, hotkey) { Apps = apps, Color = color, Icon = icon });
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

    // ---- Editing (Profiles tab) ----

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static System.Text.Json.Nodes.JsonArray ScenesArray(System.Text.Json.Nodes.JsonNode root) =>
        root["scenes"] as System.Text.Json.Nodes.JsonArray ?? throw new FormatException("scenes.json must contain a \"scenes\" array.");

    private static System.Text.Json.Nodes.JsonObject? FindScene(System.Text.Json.Nodes.JsonArray scenes, string name) =>
        scenes.OfType<System.Text.Json.Nodes.JsonObject>()
            .FirstOrDefault(s => string.Equals(s["name"]?.GetValue<string>()?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The settings of one scene exactly as written (key and JSON value), in file order. The editor keeps an untouched line
    /// as it was, so lists such as "picture mode": ["FPS Game 1", "Games"] survive an edit of another line.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>> RawSettings(string json, string name)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: ReadOptions) ?? throw new FormatException("scenes.json is empty.");
        if (FindScene(ScenesArray(root), name)?["settings"] is not System.Text.Json.Nodes.JsonObject settings) return [];
        return settings.Select(p => new KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>(p.Key, p.Value?.DeepClone())).ToArray();
    }

    /// <summary>How a value is written in scenes.json: a rounded percentage, or the value's name (hex when it has none).</summary>
    public static System.Text.Json.Nodes.JsonNode ValueNode(FeatureDefinition feature, double? percent, uint? value)
    {
        if (feature.Kind == FeatureKind.Continuous)
            return System.Text.Json.Nodes.JsonValue.Create((int)Math.Round(Math.Clamp(percent ?? 0, 0, 100)))!;
        uint raw = value ?? 0;
        return System.Text.Json.Nodes.JsonValue.Create(feature.ValueNames.TryGetValue(raw, out string? label) ? label : $"0x{raw:X2}")!;
    }

    /// <summary>
    /// Adds a scene, or replaces the one called <paramref name="originalName"/> (renaming it when the name changed).
    /// Its shortcut and any field the editor does not know are kept. The result is parsed before it is returned,
    /// so the app never writes a file it could not read back.
    /// </summary>
    /// <param name="monitor">Name filter ("LG 29WK600"), or null for every display.</param>
    /// <param name="apps">Linked programs; null keeps what the file has.</param>
    /// <param name="color">"#RRGGBB" identifying the profile; null keeps what the file has, "" removes it.</param>
    /// <param name="icon">"glyph:E7FC" or "image:name.png"; null keeps what the file has, "" removes it.</param>
    public static string Upsert(string json, string? originalName, string name, string? monitor,
        IReadOnlyList<KeyValuePair<string, System.Text.Json.Nodes.JsonNode>> settings, IReadOnlyList<string>? apps = null,
        string? color = null, string? icon = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new FormatException("A profile needs a name.");
        if (string.Equals(name, CustomName, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("\"Custom\" stands for your own current settings; choose another name for the profile.");
        if (settings.Count == 0) throw new FormatException($"Profile \"{name}\" has no settings: include at least one.");
        var root = System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: ReadOptions) ?? throw new FormatException("scenes.json is empty.");
        var scenes = ScenesArray(root);
        var existing = originalName is null ? null : FindScene(scenes, originalName);
        if (FindScene(scenes, name) is { } clash && clash != existing)
            throw new FormatException($"There is already a profile called \"{name}\".");

        var scene = existing ?? new System.Text.Json.Nodes.JsonObject();
        scene["name"] = name;
        if (string.IsNullOrWhiteSpace(monitor)) scene.Remove("monitor");
        else scene["monitor"] = monitor.Trim();
        var values = new System.Text.Json.Nodes.JsonObject();
        foreach (var (key, node) in settings) values[key] = node.DeepClone();
        scene["settings"] = values;
        if (apps is not null)
        {
            if (apps.Count == 0) scene.Remove("apps");
            else scene["apps"] = new System.Text.Json.Nodes.JsonArray(apps.Select(a => (System.Text.Json.Nodes.JsonNode?)AppName(a)).ToArray());
        }
        if (color is not null)
        {
            if (IsColor(color)) scene["color"] = color.ToUpperInvariant();
            else scene.Remove("color");
        }
        if (icon is not null)
        {
            if (NormalizeIcon(icon) is { } valid) scene["icon"] = valid;
            else scene.Remove("icon");
        }
        if (existing is null) scenes.Add(scene);

        string result = root.ToJsonString(WriteOptions);
        Parse(result);
        return result;
    }

    /// <summary>
    /// "icon:fill:game" (a library icon in a style) and "image:Night.png" (a plain .png file name, no folders) stay;
    /// "glyph:e7fc" → "glyph:E7FC"; anything else → null.
    /// </summary>
    public static string? NormalizeIcon(string text)
    {
        text = text.Trim();
        if (text.StartsWith("icon:", StringComparison.OrdinalIgnoreCase))
        {
            string[] parts = text.ToLowerInvariant().Split(':');
            if (parts.Length == 3 && parts[1] is "fluent" or "bold") parts[1] = "line";   // styles of an earlier preview
            return parts.Length == 3 && parts[1] is "line" or "fill" or "duotone" or "classic" &&
                   parts[2].Length is > 0 and <= 24 && parts[2].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
                ? string.Join(':', parts) : null;
        }
        if (text.StartsWith("glyph:", StringComparison.OrdinalIgnoreCase))
        {
            string code = text[6..];
            return code.Length == 4 && code.All(Uri.IsHexDigit) ? "glyph:" + code.ToUpperInvariant() : null;
        }
        if (text.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
        {
            string file = text[6..];
            return file.Length is > 4 and <= 80 && file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && file.IndexOfAny(['/', '\\', ':']) < 0 && !file.StartsWith('.') && file == Path.GetFileName(file)
                ? "image:" + file : null;
        }
        return null;
    }

    /// <summary>"#RRGGBB".</summary>
    public static bool IsColor(string text) =>
        text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit);

    /// <summary>"C:\\Games\\EldenRing.exe" or "EldenRing.EXE" → "eldenring.exe": how linked programs are compared.</summary>
    public static string AppName(string pathOrName)
    {
        string name = Path.GetFileName(pathOrName.Trim());
        return name.ToLowerInvariant();
    }

    /// <summary>Removes a scene (and with it its shortcut).</summary>
    public static string Remove(string json, string name)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json, documentOptions: ReadOptions) ?? throw new FormatException("scenes.json is empty.");
        var scenes = ScenesArray(root);
        if (FindScene(scenes, name) is { } scene) scenes.Remove(scene);
        string result = root.ToJsonString(WriteOptions);
        Parse(result);
        return result;
    }

    /// <summary>Reads scenes.json (creating it with the defaults when missing).</summary>
    public static string ReadText(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) LoadOrCreate(path);
        return File.ReadAllText(path);
    }

    /// <summary>Writes scenes.json atomically after checking it parses.</summary>
    public static void Write(string json, string? path = null)
    {
        path ??= DefaultPath;
        Parse(json);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }
}
