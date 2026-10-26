namespace SwitchMonitor.Core.Scenes;

/// <summary>
/// Ready-made starting points offered by "Add profile" (never created on their own). Each one becomes a new, unsaved
/// profile: the template's lines replace the display's current values, everything else starts as the display has it.
/// Lines a display lacks, or values it does not offer, are skipped when the profile is applied; a list is tried in order
/// (LG picture-mode names first, then the standard MCCS ones).
///
/// Values, from published guidance (2026-10-25), for a monitor of about 250 cd/m² such as the LG 29WK600:
/// <list type="bullet">
/// <item>Work: 100-150 cd/m² in a normal office (about 50 %), contrast 60-70 %, neutral 6500 K, Standard/Custom mode,
/// sharpness at its neutral default.</item>
/// <item>Night: dim (about 50 cd/m², 20 %) and warmer; the LG's Reader mode is its low-blue-light mode, other monitors
/// get 5000 K.</item>
/// <item>Game: the user game mode (guides advise Standard/User over fixed presets; on the LG, Custom (Game) is the
/// game mode where response time can be set, FPS Game 1 forces its own), brightness about 75-80 %, middle overdrive
/// step (the fastest one causes inverse ghosting), adaptive sync on.</item>
/// <item>Theater: cinema mode, about 100 cd/m² for a dark room (40 %), D65 white (6500 K).</item>
/// </list>
/// </summary>
public static class SceneTemplates
{
    public const string Json = """
        {
          "version": 4,
          "scenes": [
            { "name": "Work",    "settings": { "picture mode": ["Custom", "Standard"], "brightness": 50, "contrast": 70,
                                               "color preset": "6500 K", "sharpness": 50 } },
            { "name": "Night",   "settings": { "picture mode": ["Reader"], "brightness": 20, "color preset": "5000 K" } },
            { "name": "Game",    "settings": { "picture mode": ["Custom (Game)", "Games"], "brightness": 80,
                                               "response time": "Fast", "FreeSync": "On" } },
            { "name": "Theater", "settings": { "picture mode": ["Cinema", "Movie"], "brightness": 40, "color preset": "6500 K" } }
          ]
        }
        """;

    private static IReadOnlyList<SceneProfile>? all;

    /// <summary>Work, Night, Game and Theater, in menu order.</summary>
    public static IReadOnlyList<SceneProfile> All => all ??= SceneStore.Parse(Json);

    public static IReadOnlyList<string> Names => All.Select(t => t.Name).ToArray();

    public static SceneProfile? Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The template's lines as written (lists of alternatives included), for the editor.</summary>
    public static IReadOnlyList<KeyValuePair<string, System.Text.Json.Nodes.JsonNode?>> RawSettings(string name) =>
        SceneStore.RawSettings(Json, name);
}
