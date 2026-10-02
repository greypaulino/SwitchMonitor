using System.Text.Json;
using System.IO;

namespace SwitchMonitor.Wpf;

internal sealed class Preferences
{
    public string SelectedMonitor { get; set; } = "";
    public string BrightnessMonitor { get; set; } = "";
    public bool LinkedBrightness { get; set; }
    public Dictionary<string, List<int>> ConnectedInputs { get; set; } = [];
    public Dictionary<string, Dictionary<int, string>> PortShortcuts { get; set; } = [];
    public Dictionary<string, string> GlobalShortcuts { get; set; } = new()
    {
        ["cycle"] = "Ctrl+Alt+M",
        ["settings"] = "Ctrl+Alt+Shift+M",
        ["brightnessDown"] = "Ctrl+Alt+NumpadSubtract",
        ["brightnessUp"] = "Ctrl+Alt+NumpadAdd",
        ["brightnessNext"] = "Ctrl+Alt+NumpadMultiply"
    };

    internal static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SwitchMonitor-Wpf", "settings.json");

    public static Preferences Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) ?? new Preferences();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new Preferences();
    }

    public void Save()
    {
        string directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        string temp = SettingsPath + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
}
