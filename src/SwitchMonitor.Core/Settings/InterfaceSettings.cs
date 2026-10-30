using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Settings;

/// <summary>How the window's ground is drawn.</summary>
public enum Appearance
{
    /// <summary>Opaque ground with a soft light in one corner (the default look).</summary>
    Glossy,
    /// <summary>The same color, see-through, with what is behind the window blurred.</summary>
    Translucent,
    /// <summary>Opaque and flat: no light, no gradient.</summary>
    Matte
}

/// <summary>
/// The app's own look, kept in interface.json: the interface color (any "#RRGGBB"; the window turns it into a dark
/// ground of the same hue, so gray stays gray) and the appearance.
/// </summary>
public sealed record InterfaceSettings
{
    /// <summary>Neutral gray: the ground has no tint.</summary>
    public const string DefaultColor = "#8A8A8A";

    public string Color { get; init; } = DefaultColor;
    public Appearance Appearance { get; init; } = Appearance.Glossy;

    /// <summary>Style of the panel's icons: "line", "fill", "duotone" or "classic".</summary>
    public string IconStyle { get; init; } = "line";

    public static IReadOnlyList<string> IconStyles { get; } = ["line", "fill", "duotone", "classic"];

    /// <summary>A soft light follows the pointer over the window.</summary>
    public bool PointerLight { get; init; } = true;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "interface.json");

    /// <summary>"#RRGGBB" in upper case, or null for anything else.</summary>
    public static string? NormalizeColor(string? text)
    {
        text = text?.Trim();
        if (text is null) return null;
        if (!text.StartsWith('#')) text = "#" + text;
        return text.Length == 7 && text.Skip(1).All(Uri.IsHexDigit) ? text.ToUpperInvariant() : null;
    }

    public static InterfaceSettings Parse(string json)
    {
        var result = new InterfaceSettings();
        if (JsonNode.Parse(json) is not JsonObject root) return result;
        if (root["color"] is JsonValue color && color.TryGetValue(out string? hex) && NormalizeColor(hex) is { } valid)
            result = result with { Color = valid };
        if (root["appearance"] is JsonValue look && look.TryGetValue(out string? name) &&
            Enum.TryParse(name, ignoreCase: true, out Appearance appearance) && Enum.IsDefined(appearance))
            result = result with { Appearance = appearance };
        if (root["iconStyle"] is JsonValue icons && icons.TryGetValue(out string? iconStyle) &&
            IconStyles.Contains(iconStyle.ToLowerInvariant()))
            result = result with { IconStyle = iconStyle.ToLowerInvariant() };
        if (root["pointerLight"] is JsonValue light && light.TryGetValue(out bool on)) result = result with { PointerLight = on };
        return result;
    }

    public string ToJson() => new JsonObject
    {
        ["version"] = 1,
        ["color"] = Color,
        ["appearance"] = Appearance.ToString().ToLowerInvariant(),
        ["iconStyle"] = IconStyle,
        ["pointerLight"] = PointerLight
    }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The saved look, or the default one when the file is missing or damaged.</summary>
    public static InterfaceSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : new InterfaceSettings();
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new InterfaceSettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, ToJson());
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
