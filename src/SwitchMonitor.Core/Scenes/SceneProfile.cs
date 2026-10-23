using System.Globalization;
using System.Text.Json;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Scenes;

/// <summary>One control in a scene: a percentage for continuous controls, a raw MCCS value otherwise.</summary>
public sealed record SceneSetting(FeatureDefinition Feature, double? Percent, uint? Value)
{
    public override string ToString() => Percent is double p
        ? $"{Feature.Name} {p:0.#}%"
        : $"{Feature.Name} {Feature.DescribeValue(Value ?? 0)}";
}

/// <summary>
/// A named set of monitor settings applied together, e.g. "Night": brightness 15 %, color preset 5000 K.
/// Power and input are never part of a scene: they can leave the user without a picture.
/// </summary>
public sealed record SceneProfile(string Name, IReadOnlyList<SceneSetting> Settings, string? MonitorFilter = null)
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
    public const string DefaultJson = """
        {
          "version": 1,
          "scenes": [
            { "name": "Work",  "settings": { "brightness": 70, "contrast": 70 } },
            { "name": "Night", "settings": { "brightness": 15, "color preset": "6500 K" } },
            { "name": "Game",  "settings": { "brightness": 100, "picture mode": "Games" } }
          ]
        }
        """;

    public static IReadOnlyList<SceneProfile> LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultJson);
        }
        return Parse(File.ReadAllText(path));
    }

    public static IReadOnlyList<SceneProfile> Parse(string json)
    {
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
            foreach (var property in settings.EnumerateObject())
            {
                var feature = FeatureCatalog.FindByName(property.Name)
                    ?? throw new FormatException($"Scene \"{name}\": unknown control \"{property.Name}\".");
                if (feature.Risk == FeatureRisk.Disruptive)
                    throw new FormatException($"Scene \"{name}\": {feature.Name} cannot be part of a scene.");
                if (parsed.Any(s => s.Feature.Code == feature.Code))
                    throw new FormatException($"Scene \"{name}\": {feature.Name} appears twice.");
                parsed.Add(ParseSetting(name, feature, property.Value));
            }
            if (parsed.Count == 0) throw new FormatException($"Scene \"{name}\" has no settings.");

            string? filter = scene.TryGetProperty("monitor", out var m) ? m.GetString() : null;
            result.Add(new SceneProfile(name, parsed, filter));
        }
        return result;
    }

    private static SceneSetting ParseSetting(string scene, FeatureDefinition feature, JsonElement value)
    {
        string where = $"Scene \"{scene}\", {feature.Name}";
        switch (feature.Kind)
        {
            case FeatureKind.Continuous:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double percent) || percent is < 0 or > 100)
                    throw new FormatException($"{where}: use a percentage from 0 to 100.");
                return new SceneSetting(feature, percent, null);

            case FeatureKind.Toggle:
                // Mute: true / "muted" → 1, false / "sound on" → 2.
                uint? toggle = value.ValueKind switch
                {
                    JsonValueKind.True => 1,
                    JsonValueKind.False => 2,
                    JsonValueKind.String => NamedValue(feature, value.GetString()!),
                    _ => null
                };
                return toggle is uint t ? new SceneSetting(feature, null, t)
                    : throw new FormatException($"{where}: use true or false.");

            default:
                uint? choice = value.ValueKind == JsonValueKind.String ? NamedValue(feature, value.GetString()!) : null;
                return choice is uint c ? new SceneSetting(feature, null, c)
                    : throw new FormatException($"{where}: unknown value. Use a name such as " +
                        $"\"{feature.ValueNames.Values.FirstOrDefault()}\" or a hex code such as \"0x05\".");
        }
    }

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
