using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Settings;

/// <summary>
/// Last values written to write-only controls (LG sharpness: the monitor applies it but always reports 50), per display.
/// Kept in remembered.json so the panel shows what was really set, also after a restart.
/// </summary>
public sealed class RememberedValues
{
    private readonly Dictionary<string, uint> values = [];
    private readonly string? path;
    private readonly object gate = new();

    public RememberedValues(string? path = null) => this.path = path;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "remembered.json");

    private static RememberedValues? current;

    /// <summary>The store the app uses (loaded from <see cref="DefaultPath"/> on first use). Tests replace it.</summary>
    public static RememberedValues Current
    {
        get => current ??= Load();
        set => current = value;
    }

    private static string Id(string monitorKey, byte code) => $"{monitorKey}#{code:X2}";

    public uint? Get(string monitorKey, byte code)
    {
        lock (gate) return values.TryGetValue(Id(monitorKey, code), out uint value) ? value : null;
    }

    public void Set(string monitorKey, byte code, uint value)
    {
        lock (gate)
        {
            values[Id(monitorKey, code)] = value;
            Save();
        }
    }

    public static RememberedValues Load(string? path = null)
    {
        path ??= DefaultPath;
        var store = new RememberedValues(path);
        if (!File.Exists(path)) return store;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("values", out var entries))
                foreach (var entry in entries.EnumerateObject())
                    if (entry.Value.TryGetUInt32(out uint value)) store.values[entry.Name] = value;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException)
        {
            // A damaged file only loses the remembered values; the controls fall back to their defaults.
        }
        return store;
    }

    private void Save()
    {
        if (path is null) return;
        try
        {
            var entries = new JsonObject();
            foreach (var (key, value) in values) entries[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["version"] = 1, ["values"] = entries }
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
