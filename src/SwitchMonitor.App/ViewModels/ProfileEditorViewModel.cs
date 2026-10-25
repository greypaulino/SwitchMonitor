using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App.ViewModels;

/// <summary>One profile in the list on the left of the Profiles tab.</summary>
public sealed record ProfileListItem(string Name, string Hotkey, string Detail, bool IsCustom = false);

/// <summary>
/// One setting of the profile being edited: a control of the display the values come from, or a line of the saved profile
/// that this display does not have (kept as written).
/// </summary>
public sealed class ProfileSettingRowViewModel : ObservableObject
{
    private readonly ProfileEditorViewModel editor;
    private bool include;
    private double percent;
    private ChoiceOption? selected;
    private bool loading;

    /// <summary>A control of the source display.</summary>
    public ProfileSettingRowViewModel(ProfileEditorViewModel editor, MonitorFeature feature)
    {
        this.editor = editor;
        Feature = feature;
        Key = feature.Name;
        Name = feature.Name;
        IsAdvanced = FeatureCatalog.IsAdvanced(feature.Code);
        Group = IsAdvanced ? "ADVANCED" : "SETTINGS";
        Options = feature.Kind == FeatureKind.Continuous
            ? []
            : new(feature.AllowedValues.Select(v => new ChoiceOption(v, feature.Definition.DescribeValue(v))));
    }

    /// <summary>A saved line this display does not offer: shown, kept when saving, never edited.</summary>
    public ProfileSettingRowViewModel(ProfileEditorViewModel editor, string key, JsonNode? original)
    {
        this.editor = editor;
        Key = key;
        Name = key;
        Original = original;
        include = true;
        Group = "NOT ON THIS DISPLAY";
        FixedText = original?.ToJsonString() ?? "";
        Options = [];
        RemoveCommand = new RelayCommand(() => editor.RemoveRow(this));
    }

    /// <summary>Drops a saved line this display does not have.</summary>
    public ICommand? RemoveCommand { get; }

    public MonitorFeature? Feature { get; }
    public string Key { get; set; }
    public string Name { get; }
    public string Group { get; }
    public bool IsAdvanced { get; }
    public bool IsAvailable => Feature is not null;
    public bool IsContinuous => Feature?.Kind == FeatureKind.Continuous;
    public bool IsChoice => Feature is not null && Feature.Kind != FeatureKind.Continuous;
    public string? FixedText { get; }
    public ObservableCollection<ChoiceOption> Options { get; }

    /// <summary>The value as saved in scenes.json, reused when the row was not changed (keeps lists of alternatives).</summary>
    public JsonNode? Original { get; set; }

    /// <summary>True once the value was changed here, so it is written fresh instead of reusing <see cref="Original"/>.</summary>
    public bool Edited { get; private set; }

    public bool Include
    {
        get => include;
        set { if (Set(ref include, value) && !loading) editor.MarkDirty(); }
    }

    public double Percent
    {
        get => percent;
        set
        {
            double step = Feature?.Definition.Step is uint s ? s * 100.0 / (Feature.Definition.Maximum ?? 100) : 1;
            if (!Set(ref percent, Math.Clamp(Math.Round(value / step) * step, 0, 100))) return;
            Raise(nameof(PercentText));
            Touched();
        }
    }

    public string PercentText => $"{Percent:0}%";

    public ChoiceOption? Selected
    {
        get => selected;
        set { if (Set(ref selected, value)) Touched(); }
    }

    private void Touched()
    {
        if (loading) return;
        Edited = true;
        editor.MarkDirty();
    }

    /// <summary>Sets the value without counting it as an edit (loading a profile or the display's current values).</summary>
    public void Load(double? newPercent, uint? raw, bool? included = null, bool edited = false)
    {
        loading = true;
        try
        {
            if (newPercent is double p) Percent = p;
            if (raw is uint r) Selected = Options.FirstOrDefault(o => o.Value == r) ?? AddOption(r);
            if (included is bool i) Include = i;
            Edited = edited;
        }
        finally { loading = false; }
    }

    private ChoiceOption AddOption(uint raw)
    {
        var option = new ChoiceOption(raw, Feature?.Definition.DescribeValue(raw) ?? $"0x{raw:X2}");
        Options.Add(option);
        return option;
    }

    /// <summary>The JSON value to save.</summary>
    public JsonNode? ToNode()
    {
        if (Feature is null || (!Edited && Original is not null)) return Original?.DeepClone();
        return SceneStore.ValueNode(Feature.Definition, IsContinuous ? Percent : null, IsContinuous ? null : Selected?.Value);
    }
}

/// <summary>
/// The Profiles tab: list, create from the current values of a display, edit name, values and "only this display",
/// try without saving, save, delete. Shortcuts for profiles are edited on the Shortcuts tab.
/// </summary>
public sealed class ProfileEditorViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly List<ProfileSettingRowViewModel> allRows = [];
    private ProfileListItem? selectedProfile;
    private MonitorViewModel? source;
    private string? originalName;
    private string name = "";
    private bool onlyThisDisplay;
    private string? monitorFilter;
    private bool dirty;
    private bool confirmingDelete;
    private string message = "";
    private bool messageIsError;
    private bool includeAdvanced;
    private bool building;

    public ProfileEditorViewModel(MainViewModel main, string? select)
    {
        this.main = main;
        foreach (var monitor in main.Monitors.Where(m => m.IsControllable)) Sources.Add(monitor);
        Profiles.Add(new ProfileListItem(MainViewModel.CustomName, "", "Your current settings · name them to save a profile", IsCustom: true));
        foreach (var scene in main.SceneProfiles)
            Profiles.Add(new ProfileListItem(scene.Name, scene.Hotkey?.ToString() ?? "",
                string.Join(" · ", scene.Settings.Take(4).Select(s => s.ToString())) + (scene.Settings.Count > 4 ? " …" : "")));

        NewCommand = new RelayCommand(() =>
        {
            if (SelectedProfile?.IsCustom == true) _ = StartNewAsync();
            else SelectedProfile = Profiles[0];
        });
        CaptureCommand = new RelayCommand(_ => Capture(), _ => Source is not null);
        TryCommand = new RelayCommand(_ => _ = TryAsync(), _ => Source is not null);
        SaveCommand = new RelayCommand(_ => Save(), _ => IsEditing);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => originalName is not null);
        RevertCommand = new RelayCommand(_ => _ = IsNew ? StartNewAsync() : LoadAsync(), _ => dirty);

        source = main.Selected is { IsControllable: true } chosen ? chosen : Sources.FirstOrDefault();
        // Custom (the current settings) is selected unless a saved profile was asked for.
        selectedProfile = Profiles.Skip(1).FirstOrDefault(p => string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase))
            ?? Profiles[0];
        if (selectedProfile.IsCustom) _ = StartNewAsync();
        else _ = LoadAsync();
        if (Sources.Count == 0) Inform("No controllable display: values cannot be read or tried, but saved profiles can still be renamed or deleted.");
    }

    public ObservableCollection<ProfileListItem> Profiles { get; } = [];
    public ObservableCollection<MonitorViewModel> Sources { get; } = [];
    public ObservableCollection<ProfileSettingRowViewModel> Rows { get; } = [];

    public ICommand NewCommand { get; }
    public ICommand CaptureCommand { get; }
    public ICommand TryCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RevertCommand { get; }

    public ProfileListItem? SelectedProfile
    {
        get => selectedProfile;
        set
        {
            if (!Set(ref selectedProfile, value) || value is null) return;
            if (value.IsCustom) _ = StartNewAsync();
            else _ = LoadAsync();
        }
    }

    /// <summary>The display whose controls and current values the editor uses.</summary>
    public MonitorViewModel? Source
    {
        get => source;
        set
        {
            if (!Set(ref source, value)) return;
            Raise(nameof(OnlyThisDisplayLabel));
            _ = LoadAsync(keepDraft: true);
        }
    }

    public bool IsEditing => !string.IsNullOrWhiteSpace(Name);
    public bool IsNew => originalName is null;
    public string Title => IsNew ? "CUSTOM · YOUR CURRENT SETTINGS" : "EDIT PROFILE";

    public string Name
    {
        get => name;
        set { if (Set(ref name, value)) { MarkDirty(); Raise(nameof(IsEditing)); } }
    }

    public bool OnlyThisDisplay
    {
        get => onlyThisDisplay;
        set { if (Set(ref onlyThisDisplay, value)) MarkDirty(); }
    }

    public string OnlyThisDisplayLabel => $"Only for {monitorFilter ?? Source?.Name ?? "this display"}";

    public string Hotkey => originalName is null ? "" : main.SceneProfiles.FirstOrDefault(s => s.Name == originalName)?.Hotkey?.ToString() ?? "";
    public string HotkeyText => Hotkey.Length > 0 ? $"Shortcut: {Hotkey} (change it on the Shortcuts tab)" : "No shortcut (add one on the Shortcuts tab after saving)";

    public string DeleteLabel => confirmingDelete ? "Click again to delete" : "Delete";

    public string Message { get => message; private set => Set(ref message, value); }
    public bool MessageIsError { get => messageIsError; private set => Set(ref messageIsError, value); }

    public void MarkDirty()
    {
        if (building) return;
        dirty = true;
        confirmingDelete = false;
        Raise(nameof(DeleteLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// Every normal setting belongs to the profile. Red/green/blue and black levels are added with the Advanced
    /// section at the end of the list: shown = part of the profile, hidden = left out.
    /// </summary>
    public bool IncludeAdvanced
    {
        get => includeAdvanced;
        set
        {
            if (!Set(ref includeAdvanced, value)) return;
            RebuildVisibleRows();
            Raise(nameof(AdvancedButtonText));
            MarkDirty();
        }
    }

    public bool HasAdvancedRows => allRows.Any(r => r.IsAdvanced);

    public string AdvancedButtonText => IncludeAdvanced
        ? "▾  Advanced color settings (included; click to leave them out)"
        : "▸  Advanced color settings: red, green, blue and black levels";

    public ICommand ToggleAdvancedCommand => new RelayCommand(() => IncludeAdvanced = !IncludeAdvanced);

    public void RemoveRow(ProfileSettingRowViewModel row)
    {
        allRows.Remove(row);
        RebuildVisibleRows();
        MarkDirty();
    }

    private void Inform(string text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    /// <summary>
    /// Custom: the current settings of the display, every normal setting ticked (advanced ones can be ticked too).
    /// Typing a name and saving turns them into a profile.
    /// </summary>
    private async Task StartNewAsync(bool keepName = false)
    {
        string typed = keepName ? Name : "";
        originalName = null;
        monitorFilter = null;
        building = true;
        try
        {
            selectedProfile = Profiles[0];
            Raise(nameof(SelectedProfile));
            Name = typed;
            OnlyThisDisplay = false;
        }
        finally { building = false; }
        building = true;
        try { includeAdvanced = false; }
        finally { building = false; }
        await BuildRowsAsync(null);
        foreach (var row in allRows.Where(r => r.IsAvailable))
        {
            var value = Source?.ShownValue(row.Feature!.Code);
            row.Load(value?.Percent, value?.Raw, included: true, edited: true);
        }
        RebuildVisibleRows();
        dirty = true;
        RaiseHeader();
        Raise(nameof(IsEditing));
        Inform(Source is null
            ? "No display to read settings from."
            : $"These are the current settings of {Source.Name}. Type a name and Save to keep them as a profile.");
    }


    /// <summary>Loads the selected profile (or, with <paramref name="keepDraft"/>, rebuilds the rows for another display).</summary>
    private async Task LoadAsync(bool keepDraft = false)
    {
        if (keepDraft && IsNew)
        {
            // Another display for Custom: keep the typed name, read that display's values.
            await StartNewAsync(keepName: true);
            return;
        }
        var profile = main.SceneProfiles.FirstOrDefault(s => string.Equals(s.Name, (keepDraft ? originalName : SelectedProfile?.Name), StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        building = true;
        try
        {
            originalName = profile.Name;
            monitorFilter = profile.MonitorFilter;
            Name = profile.Name;
            OnlyThisDisplay = !string.IsNullOrWhiteSpace(profile.MonitorFilter);
        }
        finally { building = false; }
        await BuildRowsAsync(profile);
        // A profile that already has advanced lines opens with the Advanced section shown.
        includeAdvanced = allRows.Any(r => r.IsAdvanced && r.Include);
        RebuildVisibleRows();
        Raise(nameof(IncludeAdvanced));
        Raise(nameof(AdvancedButtonText));
        dirty = false;
        confirmingDelete = false;
        RaiseHeader();
        Inform(Source is null ? "" : $"Settings this profile did not have yet show what {Source.Name} has now; Save keeps them all.");
    }

    private void RaiseHeader()
    {
        Raise(nameof(IsNew));
        Raise(nameof(Title));
        Raise(nameof(Hotkey));
        Raise(nameof(HotkeyText));
        Raise(nameof(DeleteLabel));
        Raise(nameof(OnlyThisDisplayLabel));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>One row per control of the source display; the profile's own lines are ticked with their saved values.</summary>
    private async Task BuildRowsAsync(SceneProfile? profile)
    {
        allRows.Clear();
        if (Source is { } display) await display.EnsureLoadedAsync();
        var controls = Source?.AllControls.Where(c => c is not ReadOnlyControlViewModel).ToArray() ?? [];
        var features = controls.Select(c => c.Feature).ToArray();
        foreach (var feature in features) allRows.Add(new ProfileSettingRowViewModel(this, feature));

        if (profile is not null)
        {
            IReadOnlyList<KeyValuePair<string, JsonNode?>> raw = [];
            try { raw = SceneStore.RawSettings(SceneStore.ReadText(), profile.Name); }
            catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
            {
                Inform($"scenes.json could not be read — {error.Message}", true);
            }
            for (int i = 0; i < profile.Settings.Count; i++)
            {
                var setting = profile.Settings[i];
                var line = i < raw.Count ? raw[i] : new KeyValuePair<string, JsonNode?>(setting.Feature.Name, null);
                if (SceneApplier.FeatureFor(features, setting) is { } match &&
                    allRows.FirstOrDefault(r => r.Feature == match.Feature) is { } row)
                {
                    row.Key = line.Key;
                    row.Original = line.Value;
                    row.Load(match.Setting.Percent, match.Setting.Value, included: true);
                }
                else allRows.Add(new ProfileSettingRowViewModel(this, line.Key, line.Value));
            }
        }
        // Lines the profile does not include show what the display has now.
        foreach (var row in allRows.Where(r => r.IsAvailable && !r.Include))
        {
            var value = Source?.ShownValue(row.Feature!.Code);
            row.Load(value?.Percent, value?.Raw, included: false);
        }
        RebuildVisibleRows();
    }

    /// <summary>Normal rows, then the advanced ones when the Advanced section is shown, then lines this display lacks.</summary>
    private void RebuildVisibleRows()
    {
        Rows.Clear();
        foreach (var row in allRows.Where(r => r.IsAvailable && !r.IsAdvanced)) Rows.Add(row);
        if (includeAdvanced) foreach (var row in allRows.Where(r => r.IsAvailable && r.IsAdvanced)) Rows.Add(row);
        foreach (var row in allRows.Where(r => !r.IsAvailable)) Rows.Add(row);
        Raise(nameof(HasAdvancedRows));
    }

    /// <summary>Copies what the source display shows now into every row (ticked or not).</summary>
    private void Capture()
    {
        if (Source is null) return;
        foreach (var row in allRows.Where(r => r.IsAvailable))
        {
            var value = Source.ShownValue(row.Feature!.Code);
            row.Load(value?.Percent, value?.Raw, edited: true);
        }
        MarkDirty();
        Inform($"Current values of {Source.Name} copied. Save to keep them.");
    }

    private string? Filter => OnlyThisDisplay ? monitorFilter ?? Source?.Name : null;

    private List<KeyValuePair<string, JsonNode>> Lines()
    {
        var lines = new List<KeyValuePair<string, JsonNode>>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in allRows.Where(r => !r.IsAvailable || !r.IsAdvanced || includeAdvanced))
            if (row.ToNode() is { } node && keys.Add(row.Key)) lines.Add(new(row.Key, node));
        return lines;
    }

    /// <summary>Applies the profile as it is in the editor, without saving.</summary>
    private async Task TryAsync()
    {
        try
        {
            string draft = SceneStore.Upsert(SceneStore.ReadText(), originalName, Name, Filter, Lines());
            var scene = SceneStore.Parse(draft).First(s => string.Equals(s.Name, Name.Trim(), StringComparison.OrdinalIgnoreCase));
            await main.ApplySceneAsync(scene);
            Inform($"\"{scene.Name}\" applied without saving. Save to keep it.");
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            Inform(error.Message, true);
        }
    }

    private void Save()
    {
        try
        {
            string updated = SceneStore.Upsert(SceneStore.ReadText(), originalName, Name, Filter, Lines());
            SceneStore.Write(updated);
            string saved = Name.Trim();
            main.Report($"Profile \"{saved}\" saved.", true);
            main.ProfilesChanged(saved);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Inform(error.Message, true);
        }
    }

    private void Delete()
    {
        if (originalName is null) return;
        if (!confirmingDelete)
        {
            confirmingDelete = true;
            Raise(nameof(DeleteLabel));
            Inform($"Click again to delete \"{originalName}\" and its shortcut.");
            return;
        }
        try
        {
            SceneStore.Write(SceneStore.Remove(SceneStore.ReadText(), originalName));
            main.Report($"Profile \"{originalName}\" deleted.", true);
            main.ProfilesChanged(null);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Inform(error.Message, true);
        }
    }
}
