using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Color;

/// <summary>Software color of one display: white point in kelvin plus red/green/blue gains (0-1).</summary>
public sealed record SoftwareColorSetting(int Kelvin = SoftwareColor.NeutralKelvin, double Red = 1, double Green = 1, double Blue = 1)
{
    public bool IsNeutral => SoftwareColor.IsNeutral(Kelvin, Red, Green, Blue);
}

/// <summary>
/// Software color per display, kept in color.json so it can be applied again at start, after sleep and when Windows
/// reconnects a display (each of those resets the gamma ramp).
/// </summary>
public sealed class ColorStore
{
    private readonly Dictionary<string, SoftwareColorSetting> values = [];
    private readonly string? path;
    private readonly object gate = new();

    public ColorStore(string? path = null) => this.path = path;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "color.json");

    public SoftwareColorSetting Get(string monitorKey)
    {
        lock (gate) return values.TryGetValue(monitorKey, out var setting) ? setting : new SoftwareColorSetting();
    }

    public IReadOnlyDictionary<string, SoftwareColorSetting> All()
    {
        lock (gate) return new Dictionary<string, SoftwareColorSetting>(values);
    }

    public void Set(string monitorKey, SoftwareColorSetting setting)
    {
        lock (gate)
        {
            if (setting.IsNeutral) values.Remove(monitorKey);
            else values[monitorKey] = setting;
            Save();
        }
    }

    public static ColorStore Load(string? path = null)
    {
        path ??= DefaultPath;
        var store = new ColorStore(path);
        if (!File.Exists(path)) return store;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("displays", out var displays))
                foreach (var entry in displays.EnumerateObject())
                {
                    var e = entry.Value;
                    store.values[entry.Name] = new SoftwareColorSetting(
                        Math.Clamp(e.TryGetProperty("kelvin", out var k) && k.TryGetInt32(out int kelvin) ? kelvin : SoftwareColor.NeutralKelvin,
                            SoftwareColor.MinKelvin, SoftwareColor.MaxKelvin),
                        Gain(e, "red"), Gain(e, "green"), Gain(e, "blue"));
                }
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException)
        {
            // A damaged file only loses the software color; displays stay neutral.
        }
        return store;
    }

    private static double Gain(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.TryGetDouble(out double gain) ? Math.Clamp(gain, 0, 1) : 1;

    private void Save()
    {
        if (path is null) return;
        try
        {
            var displays = new JsonObject();
            foreach (var (key, s) in values)
                displays[key] = new JsonObject { ["kelvin"] = s.Kelvin, ["red"] = s.Red, ["green"] = s.Green, ["blue"] = s.Blue };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["version"] = 1, ["displays"] = displays }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
