using System.Text.Json;
using System.IO;

namespace SwitchMonitor.Wpf;

internal sealed class Preferences
{
    public string SelectedMonitor { get; set; } = "";
    public string BrightnessMonitor { get; set; } = "";
    public bool LinkedBrightness { get; set; }
    public bool AutomaticUpdateChecks { get; set; } = true;
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
        Preferences preferences = new();
        try
        {
            if (File.Exists(SettingsPath))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(SettingsPath)) ?? new Preferences();
        }
        catch (JsonException) { }
        catch (IOException) { }
        string disableMarker = Path.Combine(AppContext.BaseDirectory, "disable-auto-updates.flag");
        if (File.Exists(disableMarker))
        {
            preferences.AutomaticUpdateChecks = false;
            try
            {
                preferences.Save();
                File.Delete(disableMarker);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return preferences;
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
