using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Schedule;

/// <summary>
/// Automatic profiles, kept in automation.json: whether linked programs may switch profiles. The links themselves live
/// with their profile in scenes.json.
/// </summary>
public sealed class AutomationStore
{
    private readonly string? path;
    private readonly object gate = new();
    private readonly Dictionary<string, string> appPaths = new(StringComparer.OrdinalIgnoreCase);

    public AutomationStore(string? path = null) => this.path = path;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "automation.json");

    /// <summary>Linked programs switch profiles (off: they are ignored until turned on again).</summary>
    public bool AppsEnabled { get; private set; } = true;

    /// <summary>Where a linked program was found ("game.exe" → its full path), for its icon and name.</summary>
    public string? PathFor(string exe)
    {
        lock (gate) return appPaths.GetValueOrDefault(exe);
    }

    public void RememberAppPath(string exe, string fullPath)
    {
        lock (gate)
        {
            if (appPaths.TryGetValue(exe, out var known) && string.Equals(known, fullPath, StringComparison.OrdinalIgnoreCase)) return;
            appPaths[exe] = fullPath;
            Save();
        }
    }

    public void SetAppsEnabled(bool enabled)
    {
        lock (gate)
        {
            AppsEnabled = enabled;
            Save();
        }
    }

    public static AutomationStore Load(string? path = null)
    {
        path ??= DefaultPath;
        var store = new AutomationStore(path);
        if (!File.Exists(path)) return store;
        try
        {
            // Older files may also hold night-schedule entries (removed feature); they are ignored and dropped on save.
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject root && root["apps"] is JsonValue apps &&
                apps.TryGetValue(out bool enabled))
                store.AppsEnabled = enabled;
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject again && again["appPaths"] is JsonObject paths)
                foreach (var (exe, node) in paths)
                    if (node is JsonValue value && value.TryGetValue(out string? full) && full is { Length: > 0 }) store.appPaths[exe] = full;
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or FormatException)
        {
            // A damaged file only loses the switch; linked programs stay on.
        }
        return store;
    }

    private void Save()
    {
        if (path is null) return;
        try
        {
            var paths = new JsonObject();
            foreach (var (exe, full) in appPaths) paths[exe] = full;
            var root = new JsonObject { ["version"] = 2, ["apps"] = AppsEnabled, ["appPaths"] = paths };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
