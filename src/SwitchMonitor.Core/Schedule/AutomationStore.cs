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

    public AutomationStore(string? path = null) => this.path = path;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "automation.json");

    /// <summary>Linked programs switch profiles (off: they are ignored until turned on again).</summary>
    public bool AppsEnabled { get; private set; } = true;

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
            var root = new JsonObject { ["version"] = 2, ["apps"] = AppsEnabled };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
