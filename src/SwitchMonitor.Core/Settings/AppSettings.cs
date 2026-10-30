using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Hotkeys;

namespace SwitchMonitor.Core.Settings;

/// <summary>App-wide actions that can have global shortcuts.</summary>
public static class HotkeyAction
{
    public const string BrightnessUp = "brightnessUp";
    public const string BrightnessDown = "brightnessDown";
    public const string NextMonitor = "nextMonitor";
    public const string NextInput = "nextInput";
    public const string OpenWindow = "openWindow";
    /// <summary>Back to Custom (the person's own settings), like the Custom button of the profile row.</summary>
    public const string CustomProfile = "customProfile";

    public static IReadOnlyList<string> All { get; } = [BrightnessUp, BrightnessDown, NextMonitor, NextInput, OpenWindow, CustomProfile];

    public static string Describe(string action) => action switch
    {
        BrightnessUp => "Brightness up",
        BrightnessDown => "Brightness down",
        NextMonitor => "Next display (brightness)",
        NextInput => "Next input",
        OpenWindow => "Open window",
        CustomProfile => "Custom",
        _ => action
    };
}

/// <summary>A shortcut that switches one display to one input (v1: Ctrl+Alt+1 = HDMI 1).</summary>
public sealed record InputShortcut(string MonitorKey, int InputCode, HotkeyChord Chord);

/// <summary>
/// User settings for the v2 app, stored in %LOCALAPPDATA%\SwitchMonitor-v2\settings.json.
/// Every action accepts several shortcuts so the numeric keypad and the main keyboard both work, as in 1.x.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 2;

    /// <summary>The 1.x defaults: keypad keys plus their US main-keyboard equivalents (-, Shift+=, Shift+8).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<HotkeyChord>> DefaultHotkeys { get; } =
        new Dictionary<string, IReadOnlyList<HotkeyChord>>
        {
            [HotkeyAction.BrightnessUp] = [HotkeyChord.Parse("Ctrl+Alt+NumpadAdd"), HotkeyChord.Parse("Ctrl+Alt+Shift+Plus")],
            [HotkeyAction.BrightnessDown] = [HotkeyChord.Parse("Ctrl+Alt+NumpadSubtract"), HotkeyChord.Parse("Ctrl+Alt+Minus")],
            [HotkeyAction.NextMonitor] = [HotkeyChord.Parse("Ctrl+Alt+NumpadMultiply"), HotkeyChord.Parse("Ctrl+Alt+Shift+8")],
            [HotkeyAction.NextInput] = [HotkeyChord.Parse("Ctrl+Alt+M")],
            [HotkeyAction.OpenWindow] = [HotkeyChord.Parse("Ctrl+Alt+Shift+M")],
            [HotkeyAction.CustomProfile] = []
        };

    public IReadOnlyDictionary<string, IReadOnlyList<HotkeyChord>> Hotkeys { get; init; } = DefaultHotkeys;

    /// <summary>Per-input shortcuts. Empty on first run; the app then assigns Ctrl+Alt+1..9 like 1.x did.</summary>
    public IReadOnlyList<InputShortcut> InputShortcuts { get; init; } = [];

    /// <summary>True once input shortcuts were assigned or edited, so removing all of them is respected.</summary>
    public bool InputShortcutsAssigned { get; init; }

    /// <summary>
    /// Inputs marked as "another computer is connected" per display key. Next input only visits these (1.x "connected" boxes).
    /// A display without an entry cycles through all of its inputs.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> CycleInputs { get; init; } = new Dictionary<string, IReadOnlyList<int>>();

    /// <summary>Brightness change for one isolated key tap, in percent (1.x: one point).</summary>
    public int BrightnessStep { get; init; } = 1;

    public bool StartMinimized { get; init; }

    /// <summary>Shows the Advanced tab (red/green/blue, black levels) and the advanced rows of the profile editor.</summary>
    public bool ShowAdvanced { get; init; }

    public IReadOnlyList<HotkeyChord> For(string action) =>
        Hotkeys.TryGetValue(action, out var chords) ? chords : [];

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public static AppSettings LoadOrCreate(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            defaults.Save(path);
            return defaults;
        }
        var settings = Parse(File.ReadAllText(path), out bool upgraded);
        if (upgraded) settings.Save(path);
        return settings;
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, ToJson());
        File.Move(temporary, path, overwrite: true);
    }

    public static AppSettings Parse(string json) => Parse(json, out _);

    /// <param name="upgraded">True when the file came from an older preview and was converted.</param>
    public static AppSettings Parse(string json, out bool upgraded)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        var root = document.RootElement;
        int version = root.TryGetProperty("version", out var v) && v.TryGetInt32(out int parsedVersion) ? parsedVersion : 1;
        // Version 1 was written by the first preview with keypad-only shortcuts nobody edited: take the 1.x defaults.
        upgraded = version < CurrentVersion;

        var hotkeys = new Dictionary<string, IReadOnlyList<HotkeyChord>>(DefaultHotkeys);
        if (!upgraded && root.TryGetProperty("hotkeys", out var hotkeyObject) && hotkeyObject.ValueKind == JsonValueKind.Object)
            foreach (var property in hotkeyObject.EnumerateObject())
            {
                if (!HotkeyAction.All.Contains(property.Name))
                    throw new FormatException($"settings.json: unknown shortcut action \"{property.Name}\".");
                hotkeys[property.Name] = ParseChords($"hotkeys.{property.Name}", property.Value);
            }

        var inputShortcuts = new List<InputShortcut>();
        if (root.TryGetProperty("inputShortcuts", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
            foreach (var entry in inputs.EnumerateArray())
            {
                string monitor = entry.TryGetProperty("monitor", out var m) && m.GetString() is { Length: > 0 } key
                    ? key : throw new FormatException("settings.json: every input shortcut needs a \"monitor\".");
                int code = entry.TryGetProperty("input", out var i) && i.TryGetInt32(out int c) && c is > 0 and < 256
                    ? c : throw new FormatException("settings.json: every input shortcut needs an \"input\" code.");
                string text = entry.TryGetProperty("hotkey", out var h) ? h.GetString() ?? "" : "";
                if (!HotkeyChord.TryParse(text, out var chord))
                    throw new FormatException($"settings.json: input shortcut \"{text}\" is not valid (example: Ctrl+Alt+1).");
                inputShortcuts.Add(new InputShortcut(monitor, code, chord));
            }

        var cycle = new Dictionary<string, IReadOnlyList<int>>();
        if (root.TryGetProperty("cycleInputs", out var cycleObject) && cycleObject.ValueKind == JsonValueKind.Object)
            foreach (var property in cycleObject.EnumerateObject())
                cycle[property.Name] = property.Value.EnumerateArray()
                    .Select(e => e.TryGetInt32(out int code) ? code : throw new FormatException("settings.json: cycleInputs holds input codes."))
                    .Distinct().ToArray();

        var defaults = new AppSettings();
        int step = !upgraded && root.TryGetProperty("brightnessStep", out var s) && s.TryGetInt32(out int parsed)
            ? parsed : defaults.BrightnessStep;
        if (step is < 1 or > 50) throw new FormatException("settings.json: brightnessStep must be between 1 and 50.");

        return new AppSettings
        {
            Hotkeys = hotkeys,
            InputShortcuts = inputShortcuts,
            InputShortcutsAssigned = inputShortcuts.Count > 0 ||
                root.TryGetProperty("inputShortcutsAssigned", out var assigned) && assigned.ValueKind == JsonValueKind.True,
            CycleInputs = cycle,
            BrightnessStep = step,
            StartMinimized = root.TryGetProperty("startMinimized", out var min) && min.ValueKind == JsonValueKind.True,
            ShowAdvanced = root.TryGetProperty("showAdvanced", out var adv) && adv.ValueKind == JsonValueKind.True
        };
    }

    /// <summary>"Ctrl+Alt+M", ["Ctrl+Alt+NumpadAdd", "Ctrl+Alt+Shift+Plus"], "" or null (disabled).</summary>
    private static IReadOnlyList<HotkeyChord> ParseChords(string where, JsonElement value)
    {
        IEnumerable<string?> texts = value.ValueKind switch
        {
            JsonValueKind.Null => [],
            JsonValueKind.String => [value.GetString()],
            JsonValueKind.Array => value.EnumerateArray().Select(e => e.GetString()),
            _ => throw new FormatException($"settings.json: {where} must be a shortcut or a list of shortcuts.")
        };
        var result = new List<HotkeyChord>();
        foreach (string? text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!HotkeyChord.TryParse(text, out var chord))
                throw new FormatException($"settings.json: {where} \"{text}\" is not a valid shortcut.");
            if (!result.Contains(chord)) result.Add(chord);
        }
        return result;
    }

    public string ToJson()
    {
        var hotkeys = new JsonObject();
        foreach (string action in HotkeyAction.All)
            hotkeys[action] = new JsonArray(For(action).Select(c => (JsonNode)JsonValue.Create(c.ToString())!).ToArray());
        var inputs = new JsonArray(InputShortcuts.Select(i => (JsonNode)new JsonObject
        {
            ["monitor"] = i.MonitorKey,
            ["input"] = i.InputCode,
            ["hotkey"] = i.Chord.ToString()
        }).ToArray());
        var cycle = new JsonObject();
        foreach (var (key, codes) in CycleInputs)
            cycle[key] = new JsonArray(codes.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        var root = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["hotkeys"] = hotkeys,
            ["inputShortcuts"] = inputs,
            ["inputShortcutsAssigned"] = InputShortcutsAssigned || InputShortcuts.Count > 0,
            ["cycleInputs"] = cycle,
            ["brightnessStep"] = BrightnessStep,
            ["startMinimized"] = StartMinimized,
            ["showAdvanced"] = ShowAdvanced
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// First run on a display with inputs and no input shortcuts anywhere: Ctrl+Alt+1..9 in input order, as 1.x did.
    /// Returns null when nothing needs to change.
    /// </summary>
    public AppSettings? WithDefaultInputShortcuts(string monitorKey, IReadOnlyList<int> inputs)
    {
        if (InputShortcutsAssigned || InputShortcuts.Count > 0 || inputs.Count == 0) return null;
        var taken = Hotkeys.Values.SelectMany(c => c).ToHashSet();
        var added = new List<InputShortcut>();
        int slot = 1;
        foreach (int code in inputs)
        {
            if (slot > 9) break;
            var chord = HotkeyChord.Parse($"Ctrl+Alt+{slot++}");
            if (!taken.Contains(chord)) added.Add(new InputShortcut(monitorKey, code, chord));
        }
        return this with { InputShortcuts = added, InputShortcutsAssigned = true };
    }

    /// <summary>Inputs Next input visits on a display: the marked ones (at least two), otherwise all of them.</summary>
    public IReadOnlyList<int> CycleFor(string monitorKey, IReadOnlyList<int> allInputs)
    {
        if (!CycleInputs.TryGetValue(monitorKey, out var marked)) return allInputs;
        var usable = allInputs.Where(marked.Contains).ToArray();
        return usable.Length >= 2 ? usable : allInputs;
    }

    public AppSettings WithCycleInput(string monitorKey, IReadOnlyList<int> allInputs, int code, bool included)
    {
        var current = CycleInputs.TryGetValue(monitorKey, out var marked) ? marked : allInputs;
        var next = included ? current.Append(code).Distinct() : current.Where(c => c != code);
        var map = new Dictionary<string, IReadOnlyList<int>>(CycleInputs)
        {
            [monitorKey] = allInputs.Where(next.Contains).ToArray()
        };
        return this with { CycleInputs = map };
    }

    /// <summary>Every shortcut with a readable owner, for registration and conflict checks.</summary>
    public IEnumerable<(string Owner, HotkeyChord Chord)> AllShortcuts(Func<InputShortcut, string> describeInput)
    {
        foreach (string action in HotkeyAction.All)
            foreach (var chord in For(action))
                yield return (HotkeyAction.Describe(action), chord);
        foreach (var input in InputShortcuts)
            yield return (describeInput(input), input.Chord);
    }

    /// <summary>Shortcuts used twice across settings and scenes, as readable messages.</summary>
    public IReadOnlyList<string> FindConflicts(IEnumerable<(string Owner, HotkeyChord? Chord)> others,
        Func<InputShortcut, string>? describeInput = null)
    {
        describeInput ??= i => $"Input {i.InputCode:X2}";
        var all = AllShortcuts(describeInput).ToList();
        foreach (var (owner, chord) in others)
            if (chord is HotkeyChord c) all.Add((owner, c));
        return all.GroupBy(x => x.Chord).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} is used by {string.Join(" and ", g.Select(x => x.Owner))}.").ToArray();
    }
}
