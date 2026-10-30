using System.Collections.ObjectModel;
using System.Windows.Input;
using SwitchMonitor.Core.Hotkeys;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App.ViewModels;

public enum ShortcutKind { Action, Input, Scene }

/// <summary>What the editor needs to know about a display: its key, name and inputs.</summary>
public sealed record DisplayInputs(string Key, string Name, IReadOnlyList<(int Code, string Label)> Inputs);

/// <summary>One recorded shortcut shown as a chip, with its problem (conflict, owned by another program) if any.</summary>
public sealed class ChordChipViewModel : ObservableObject
{
    private string? problem;
    private bool blocking;

    public ChordChipViewModel(ShortcutRowViewModel row, HotkeyChord chord, ShortcutEditorViewModel editor)
    {
        Row = row;
        Chord = chord;
        RemoveCommand = new RelayCommand(() => editor.Remove(this));
    }

    public ShortcutRowViewModel Row { get; }
    public HotkeyChord Chord { get; }
    public string Text => Chord.ToString().Replace("+", " + ");
    /// <summary>One keycap per key: Ctrl · Alt · 1.</summary>
    public IReadOnlyList<string> Keys => Chord.ToString().Split('+');
    public ICommand RemoveCommand { get; }

    public string? Problem
    {
        get => problem;
        set { if (Set(ref problem, value)) Raise(nameof(HasProblem)); }
    }

    public bool HasProblem => Problem is not null;

    /// <summary>True for duplicates (cannot be saved); false for warnings such as "used by another program".</summary>
    public bool IsBlocking { get => blocking; set => Set(ref blocking, value); }
}

/// <summary>One action, input or scene and its shortcuts.</summary>
public sealed class ShortcutRowViewModel : ObservableObject
{
    private bool isCapturing;

    public ShortcutRowViewModel(ShortcutEditorViewModel editor, string group, string name, ShortcutKind kind, bool allowMultiple)
    {
        Group = group;
        Name = name;
        Kind = kind;
        AllowMultiple = allowMultiple;
        AddCommand = new RelayCommand(() => editor.BeginCapture(this));
        Chords.CollectionChanged += (_, _) => Raise(nameof(AddLabel));
    }

    public string Group { get; }
    public string Name { get; }
    public ShortcutKind Kind { get; }
    public bool AllowMultiple { get; }
    public string? Action { get; init; }
    public string? MonitorKey { get; init; }
    public int InputCode { get; init; }
    public string? SceneName { get; init; }

    public ObservableCollection<ChordChipViewModel> Chords { get; } = [];

    /// <summary>Keys held so far while recording, one keycap each (Ctrl, Alt…), before the final key arrives.</summary>
    public ObservableCollection<string> PendingKeys { get; } = [];

    /// <summary>Icon of the row's category: general actions, profiles, or the display whose input it selects.</summary>
    public string GroupGlyph => Group == "Profiles" ? Icons.Profiles : Kind switch
    {
        ShortcutKind.Scene => Icons.Profiles,
        ShortcutKind.Input => Icons.Input,
        _ => Icons.Settings
    };

    /// <summary>Icon of the row itself: what the action does, the profile's own icon; inputs draw their connector.</summary>
    public string RowGlyph => Kind switch
    {
        ShortcutKind.Scene => Icons.ForProfile(Name, Icon),
        ShortcutKind.Input => "",
        _ => Action switch
        {
            HotkeyAction.BrightnessUp => "\uE706",     // Brightness
            HotkeyAction.BrightnessDown => "\uE708",   // QuietHours: the moon, dimmer
            HotkeyAction.NextMonitor => Icons.External,
            HotkeyAction.NextInput => Icons.NextInput,
            HotkeyAction.OpenWindow => "\uE8A7",       // OpenInNewWindow
            HotkeyAction.CustomProfile => Icons.ForProfile(SceneStore.CustomName),
            _ => Icons.Settings
        }
    };

    public bool HasShape => Kind == ShortcutKind.Input;
    public System.Windows.Media.Geometry? Shape => Kind == ShortcutKind.Input ? Icons.ConnectorShape(Name) : null;

    /// <summary>The icon a profile chose ("glyph:…" / "image:…"), or null.</summary>
    public string? Icon { get; init; }
    public System.Windows.Media.ImageSource? IconImage => Icons.ProfileImage(Icon);
    /// <summary>Profile rows: the profile's icon as drawn.</summary>
    public Icons.Mark Mark => Icons.ProfileMark(Action == HotkeyAction.CustomProfile ? SceneStore.CustomName : Name, Icon);

    /// <summary>"#RRGGBB" of a profile row (its circle), null for the others.</summary>
    public string? Color { get; init; }
    public bool HasColor => Color is not null;
    public System.Windows.Media.Brush? ColorBrush => Color is null ? null : Icons.Brush(Color);

    public ICommand AddCommand { get; }

    public bool IsCapturing
    {
        get => isCapturing;
        set { if (Set(ref isCapturing, value)) Raise(nameof(AddLabel)); }
    }

    public string AddLabel => IsCapturing ? "Press keys…" : !AllowMultiple && Chords.Count > 0 ? "Change" : "+ Add";
}

/// <summary>
/// Shortcut editor. Global shortcuts are paused while it is open, so a shortcut can be recorded by pressing it and
/// "free" can be tested against every other program. Duplicates block saving; shortcuts another program owns only warn.
/// </summary>
public sealed class ShortcutEditorViewModel : ObservableObject
{
    private readonly Func<HotkeyChord, bool> isFree;
    private readonly IReadOnlyList<DisplayInputs> displays;
    private readonly AppSettings original;
    private readonly Dictionary<HotkeyChord, bool> freeCache = [];
    private ShortcutRowViewModel? captureRow;
    private string message = "Click + Add, then press the keys. Ctrl, Alt or Win must be part of every shortcut.";
    private bool messageIsError;
    private bool hasBlockingProblems;

    private readonly IReadOnlyDictionary<HotkeyChord, string> reserved;
    private readonly HashSet<string> shownKeys = [];

    /// <param name="displays">The display whose input shortcuts are edited (the one selected on the Displays view).</param>
    /// <param name="scenes">The profiles that apply to that display.</param>
    /// <param name="reserved">Shortcuts of other displays and profiles: not shown, kept, and still taken.</param>
    /// <param name="displayName">Name of the display the view is about, for its heading.</param>
    public ShortcutEditorViewModel(AppSettings settings, IReadOnlyList<DisplayInputs> displays, IReadOnlyList<SceneProfile> scenes,
        Func<HotkeyChord, bool> isFree, IReadOnlyDictionary<HotkeyChord, string>? reserved = null, string? displayName = null)
    {
        original = settings;
        this.reserved = reserved ?? new Dictionary<HotkeyChord, string>();
        DisplayName = displayName;
        this.displays = displays;
        this.isFree = isFree;
        SaveCommand = new RelayCommand(_ => Save(), _ => !HasBlockingProblems);
        CancelCommand = new RelayCommand(() => CloseRequested?.Invoke());
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);

        foreach (string action in HotkeyAction.All.Where(a => a != HotkeyAction.CustomProfile))
        {
            var row = new ShortcutRowViewModel(this, "General", HotkeyAction.Describe(action), ShortcutKind.Action, true) { Action = action };
            foreach (var chord in settings.For(action)) row.Chords.Add(new ChordChipViewModel(row, chord, this));
            Rows.Add(row);
        }

        // Only the selected display's inputs; other displays keep their shortcuts untouched (saved as they are).
        foreach (var display in displays.Where(d => d.Inputs.Count > 0))
        {
            shownKeys.Add(display.Key);
            foreach (var (code, label) in display.Inputs)
                Rows.Add(InputRow($"Inputs · {display.Name}", label, display.Key, code, settings));
        }

        // Custom first: the person's own settings, kept with the app's shortcuts (it is not in scenes.json).
        var custom = new ShortcutRowViewModel(this, "Profiles", HotkeyAction.Describe(HotkeyAction.CustomProfile), ShortcutKind.Action, false)
        {
            Action = HotkeyAction.CustomProfile,
            Color = Icons.ColorForProfile(SceneStore.CustomName, null)
        };
        foreach (var chord in settings.For(HotkeyAction.CustomProfile).Take(1)) custom.Chords.Add(new ChordChipViewModel(custom, chord, this));
        Rows.Add(custom);

        foreach (var scene in scenes)
        {
            var row = new ShortcutRowViewModel(this, "Profiles", scene.Name, ShortcutKind.Scene, false)
            {
                SceneName = scene.Name, Color = Icons.ColorForProfile(scene.Name, scene.Color), Icon = scene.Icon
            };
            if (scene.Hotkey is { } chord) row.Chords.Add(new ChordChipViewModel(row, chord, this));
            Rows.Add(row);
        }
        Validate();
    }

    private ShortcutRowViewModel InputRow(string group, string label, string key, int code, AppSettings settings)
    {
        var row = new ShortcutRowViewModel(this, group, label, ShortcutKind.Input, true) { MonitorKey = key, InputCode = code };
        foreach (var shortcut in settings.InputShortcuts.Where(s => s.MonitorKey == key && s.InputCode == code))
            row.Chords.Add(new ChordChipViewModel(row, shortcut.Chord, this));
        return row;
    }

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = [];

    /// <summary>The display these shortcuts are shown for, or null.</summary>
    public string? DisplayName { get; }
    public string Heading => DisplayName is null ? "Shortcuts" : $"Shortcuts for {DisplayName}";
    public bool HasDisplayName => DisplayName is not null;
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RestoreDefaultsCommand { get; }

    public string Message { get => message; private set => Set(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => Set(ref messageIsError, value); }
    public bool HasBlockingProblems { get => hasBlockingProblems; private set => Set(ref hasBlockingProblems, value); }
    public bool IsCapturing => captureRow is not null;

    /// <summary>Raised with the new settings and the scene shortcuts (scene name → shortcut or null).</summary>
    public event Action<AppSettings, IReadOnlyDictionary<string, HotkeyChord?>>? Saved;
    public event Action? CloseRequested;

    public void BeginCapture(ShortcutRowViewModel row)
    {
        CancelCapture();
        captureRow = row;
        row.IsCapturing = true;
        Raise(nameof(IsCapturing));
        Inform($"Press the new shortcut for \"{row.Name}\". Esc cancels.");
    }

    public void CancelCapture()
    {
        if (captureRow is null) return;
        captureRow.PendingKeys.Clear();
        captureRow.IsCapturing = false;
        captureRow = null;
        Raise(nameof(IsCapturing));
        Inform("Click + Add, then press the keys. Ctrl, Alt or Win must be part of every shortcut.");
    }

    /// <summary>Shows the modifiers held so far while recording.</summary>
    public void ShowPending(HotkeyModifiers modifiers)
    {
        if (captureRow is null) return;
        captureRow.PendingKeys.Clear();
        foreach (var (flag, label) in new[] { (HotkeyModifiers.Ctrl, "Ctrl"), (HotkeyModifiers.Alt, "Alt"),
                     (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Win, "Win") })
            if (modifiers.HasFlag(flag)) captureRow.PendingKeys.Add(label);
        string held = modifiers == HotkeyModifiers.None ? "" : modifiers.ToString().Replace(", ", " + ") + " + …";
        Inform($"\"{captureRow.Name}\": {held}");
    }

    public void Capture(HotkeyChord chord)
    {
        if (captureRow is not { } row) return;
        if (!chord.IsAllowed)
        {
            Inform($"{chord} would block normal typing: include Ctrl, Alt or Win. Try again or press Esc.", error: true);
            return;
        }
        if (row.Chords.Any(c => c.Chord == chord))
        {
            CancelCapture();
            return;
        }
        row.PendingKeys.Clear();
        if (!row.AllowMultiple) row.Chords.Clear();
        row.Chords.Add(new ChordChipViewModel(row, chord, this));
        row.IsCapturing = false;
        captureRow = null;
        Raise(nameof(IsCapturing));
        Validate();
        if (!HasBlockingProblems && !MessageIsError) Inform($"\"{row.Name}\": {chord} added. Save to keep it.");
    }

    public void Remove(ChordChipViewModel chip)
    {
        chip.Row.Chords.Remove(chip);
        Validate();
    }

    private void RestoreDefaults()
    {
        CancelCapture();
        foreach (var row in Rows)
        {
            row.Chords.Clear();
            if (row.Kind == ShortcutKind.Action && row.Action is { } action)
                foreach (var chord in AppSettings.DefaultHotkeys[action]) row.Chords.Add(new ChordChipViewModel(row, chord, this));
        }
        // 1.x defaults: Ctrl+Alt+1..9 on the first display with inputs.
        if (displays.FirstOrDefault(d => d.Inputs.Count > 0) is { } first &&
            new AppSettings().WithDefaultInputShortcuts(first.Key, first.Inputs.Select(i => i.Code).ToArray()) is { } defaults)
            foreach (var shortcut in defaults.InputShortcuts)
                if (Rows.FirstOrDefault(r => r.Kind == ShortcutKind.Input && r.MonitorKey == shortcut.MonitorKey && r.InputCode == shortcut.InputCode) is { } row)
                    row.Chords.Add(new ChordChipViewModel(row, shortcut.Chord, this));
        Validate();
        if (!HasBlockingProblems) Inform("Defaults restored (profiles have none). Save to keep them.");
    }

    /// <summary>Marks duplicates (blocking) and shortcuts another program or Windows already owns (warning).</summary>
    private void Validate()
    {
        var chips = Rows.SelectMany(r => r.Chords).ToList();
        foreach (var chip in chips)
        {
            var others = chips.Where(c => c != chip && c.Chord == chip.Chord).Select(c => c.Row.Name)
                .Concat(reserved.TryGetValue(chip.Chord, out string? owner) ? [owner] : []).Distinct().ToArray();
            if (others.Length > 0)
            {
                chip.Problem = $"Also used by {string.Join(", ", others)}";
                chip.IsBlocking = true;
            }
            else
            {
                chip.Problem = IsFree(chip.Chord) ? null : "Used by Windows or another program (for example SwitchMonitor 1.x)";
                chip.IsBlocking = false;
            }
        }
        HasBlockingProblems = chips.Any(c => c.IsBlocking);
        int taken = chips.Count(c => c.HasProblem && !c.IsBlocking);
        if (HasBlockingProblems) Inform("Some shortcuts are used twice (red). Change or remove them to save.", error: true);
        else if (taken > 0) Inform($"{taken} shortcut(s) are taken by another program and will not work until it releases them.", error: true);
        else if (MessageIsError) Inform("No conflicts.");
        CommandManager.InvalidateRequerySuggested();
    }

    private bool IsFree(HotkeyChord chord)
    {
        if (!freeCache.TryGetValue(chord, out bool free)) freeCache[chord] = free = isFree(chord);
        return free;
    }

    private void Inform(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    private void Save()
    {
        if (HasBlockingProblems) return;
        var hotkeys = Rows.Where(r => r.Kind == ShortcutKind.Action && r.Action is not null)
            .ToDictionary(r => r.Action!, r => (IReadOnlyList<HotkeyChord>)r.Chords.Select(c => c.Chord).ToArray());
        var inputs = Rows.Where(r => r.Kind == ShortcutKind.Input)
            .SelectMany(r => r.Chords.Select(c => new InputShortcut(r.MonitorKey!, r.InputCode, c.Chord)))
            .Concat(original.InputShortcuts.Where(s => !shownKeys.Contains(s.MonitorKey)))   // other displays: kept
            .ToArray();
        var scenes = Rows.Where(r => r.Kind == ShortcutKind.Scene && r.SceneName is not null)
            .ToDictionary(r => r.SceneName!, r => r.Chords.Select(c => (HotkeyChord?)c.Chord).FirstOrDefault());
        Saved?.Invoke(original with { Hotkeys = hotkeys, InputShortcuts = inputs, InputShortcutsAssigned = true }, scenes);
        CloseRequested?.Invoke();
    }
}
