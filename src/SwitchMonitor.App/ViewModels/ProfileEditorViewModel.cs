using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using SwitchMonitor.Core;
using SwitchMonitor.App.Automation;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App.ViewModels;

/// <summary>A program linked to the profile being edited ("eldenring.exe").</summary>
public sealed record LinkedAppItem(string Exe, ICommand RemoveCommand);

/// <summary>A program with a window, offered by "Add app".</summary>
public sealed record RunningAppItem(string Exe, string Title, ICommand AddCommand)
{
    public string Label => string.Equals(Title, Exe, StringComparison.OrdinalIgnoreCase) ? Exe : $"{Exe} — {Title}";
}

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
        FixedText = original?.ToJsonString() ?? "";
        Options = [];
        RemoveCommand = new RelayCommand(() => editor.RemoveRow(this));
    }

    /// <summary>Drops a saved line this display does not have.</summary>
    public ICommand? RemoveCommand { get; }

    public MonitorFeature? Feature { get; }
    public string Key { get; set; }
    public string Name { get; }
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
        set
        {
            if (!Set(ref selected, value)) return;
            Touched();
            VolumeRow?.MuteChanged();
            if (!loading) editor.UpdateModeLocks();   // a new picture mode can show or hide other lines
        }
    }

    // ---- Volume with its speaker icon, as on the Displays tab (the Mute line is folded into it) ----

    private ProfileSettingRowViewModel? muteRow;

    /// <summary>On the volume line: the profile's Mute line, shown as the speaker icon instead of its own drop-down.</summary>
    public ProfileSettingRowViewModel? MuteRow
    {
        get => muteRow;
        set
        {
            if (!Set(ref muteRow, value)) return;
            if (value is not null) value.VolumeRow = this;
            Raise(nameof(HasMute));
            MuteChanged();
        }
    }

    /// <summary>On the Mute line: the volume line that shows it.</summary>
    public ProfileSettingRowViewModel? VolumeRow { get; private set; }

    public bool HasMute => MuteRow is not null;

    private (uint On, uint Off)? MuteValues =>
        MuteRow?.Feature is { } mute ? mute.Definition.OnOffValues(mute.AllowedValues) : null;

    public bool IsMuted => MuteValues is { } values && MuteRow!.Selected?.Value == values.On;

    /// <summary>Speaker glyphs of Segoe MDL2 Assets: muted / sound on (same as the Displays tab).</summary>
    public string SpeakerGlyph => IsMuted ? "\uE74F" : "\uE767";

    public string SpeakerToolTip => IsMuted ? "Muted in this profile: click to turn the sound on" : "Click to mute in this profile";

    public ICommand ToggleMuteCommand => new RelayCommand(() =>
    {
        if (MuteRow is not { } mute || MuteValues is not { } values) return;
        uint target = IsMuted ? values.Off : values.On;
        mute.Selected = mute.Options.FirstOrDefault(o => o.Value == target) ?? mute.Selected;
    });

    internal void MuteChanged()
    {
        Raise(nameof(IsMuted));
        Raise(nameof(SpeakerGlyph));
        Raise(nameof(SpeakerToolTip));
    }

    /// <summary>The row is drawn: shown by the picture mode and not folded into the volume line.</summary>
    public bool IsVisible => IsShown && VolumeRow is null;

    private bool isShown = true;

    /// <summary>
    /// False while the profile's picture mode makes the line meaningless (LG FreeSync outside the Custom and game modes,
    /// response time outside Custom): the line is hidden and not saved.
    /// </summary>
    public bool IsShown { get => isShown; set { if (Set(ref isShown, value)) Raise(nameof(IsVisible)); } }

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
/// The Profiles tab: Custom (the person's own settings, always first and never renamed), saved profiles, and "Add profile"
/// with the Work / Night / Game / Theater templates or a blank new profile. Edit name, values and "only this display",
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
    private bool isCustom;
    private string? template;

    public ProfileEditorViewModel(MainViewModel main, string? select)
    {
        this.main = main;
        foreach (var monitor in main.Monitors.Where(m => m.IsControllable)) Sources.Add(monitor);
        Profiles.Add(new ProfileListItem(MainViewModel.CustomName, "", "Your own current settings", IsCustom: true));
        foreach (var scene in main.SceneProfiles)
            Profiles.Add(new ProfileListItem(scene.Name, scene.Hotkey?.ToString() ?? "",
                string.Join(" · ", scene.Settings.Take(4).Select(s => s.ToString())) + (scene.Settings.Count > 4 ? " …" : "")));

        NewProfileCommand = new RelayCommand(() => _ = StartDraftAsync(null));
        AddTemplateCommand = new RelayCommand(name => _ = StartDraftAsync(name as string));
        CaptureCommand = new RelayCommand(_ => Capture(), _ => Source is not null);
        TryCommand = new RelayCommand(_ => _ = TryAsync(), _ => Source is not null && !isCustom);
        SaveCommand = new RelayCommand(_ => { if (isCustom) _ = ApplyCustomAsync(); else Save(); },
            _ => isCustom ? Source is not null : IsEditing);
        DeleteCommand = new RelayCommand(_ => Delete(), _ => originalName is not null);
        RevertCommand = new RelayCommand(_ => _ = isCustom ? StartCustomAsync() : IsNew ? StartDraftAsync(template) : LoadAsync(), _ => dirty);

        source = main.Selected is { IsControllable: true } chosen ? chosen : Sources.FirstOrDefault();
        // Custom (the current settings) is selected unless a saved profile was asked for.
        selectedProfile = Profiles.Skip(1).FirstOrDefault(p => string.Equals(p.Name, select, StringComparison.OrdinalIgnoreCase))
            ?? Profiles[0];
        if (selectedProfile.IsCustom) _ = StartCustomAsync();
        else _ = LoadAsync();
        if (Sources.Count == 0) Inform("No controllable display: values cannot be read or tried, but saved profiles can still be renamed or deleted.");
    }

    public ObservableCollection<ProfileListItem> Profiles { get; } = [];
    public ObservableCollection<MonitorViewModel> Sources { get; } = [];
    /// <summary>Normal settings, always part of the profile.</summary>
    public ObservableCollection<ProfileSettingRowViewModel> Rows { get; } = [];
    /// <summary>Red/green/blue and black levels, shown (and saved) only while the Advanced section is open.</summary>
    public ObservableCollection<ProfileSettingRowViewModel> AdvancedRows { get; } = [];
    /// <summary>Saved lines this display does not have (kept as written).</summary>
    public ObservableCollection<ProfileSettingRowViewModel> MissingRows { get; } = [];
    public bool HasMissingRows => MissingRows.Count > 0;

    public ICommand NewProfileCommand { get; }
    public ICommand AddTemplateCommand { get; }
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
            if (value.IsCustom) _ = StartCustomAsync();
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

    public bool IsEditing => isCustom || !string.IsNullOrWhiteSpace(Name);
    public bool IsNew => originalName is null;
    /// <summary>Custom is the person's own settings: always called Custom, applied rather than saved.</summary>
    public bool IsCustom => isCustom;
    public string Title => isCustom ? "CUSTOM · YOUR CURRENT SETTINGS"
        : IsNew ? (template is null ? "NEW PROFILE" : $"NEW PROFILE · {template.ToUpperInvariant()} TEMPLATE")
        : "EDIT PROFILE";
    public string SaveLabel => isCustom ? "Apply" : "Save";
    public string SaveToolTip => isCustom
        ? "Set these values on the display as your own settings (Custom)"
        : "Save the profile";

    public string Name
    {
        get => name;
        set
        {
            if (isCustom) value = MainViewModel.CustomName;   // the name of Custom never changes
            if (Set(ref name, value)) { MarkDirty(); Raise(nameof(IsEditing)); }
        }
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
            Raise(nameof(AdvancedGlyph)); Raise(nameof(AdvancedToolTip));
            MarkDirty();
        }
    }

    public bool HasAdvancedRows => allRows.Any(r => r.IsAvailable && r.IsAdvanced);

    /// <summary>Chevron of the ADVANCED heading: down while open, right while closed (Segoe MDL2 Assets).</summary>
    public string AdvancedGlyph => IncludeAdvanced ? "\uE70D" : "\uE76C";

    public string AdvancedToolTip => IncludeAdvanced
        ? "Red, green, blue and black levels are part of the profile. Click to leave them out."
        : "Red, green, blue and black levels are left as they are. Click to include them.";

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
    /// Custom: the current settings of the display, every normal setting listed (advanced ones on request).
    /// Its name is fixed; Apply sets the edited values on the display as the person's own settings.
    /// </summary>
    private async Task StartCustomAsync()
    {
        originalName = null;
        monitorFilter = null;
        template = null;
        building = true;
        try
        {
            isCustom = true;
            LoadApps([]);
            selectedProfile = Profiles[0];
            Raise(nameof(SelectedProfile));
            name = MainViewModel.CustomName;
            Raise(nameof(Name));
            OnlyThisDisplay = false;
            includeAdvanced = false;
        }
        finally { building = false; }
        await BuildRowsAsync(null, []);
        LoadCurrentValues(allRows);
        RebuildVisibleRows();
        dirty = false;
        RaiseHeader();
        Inform(Source is null
            ? "No display to read settings from."
            : $"These are the current settings of {Source.Name}. Change them and Apply, or use Add profile to keep a set.");
    }

    /// <summary>
    /// A new, unsaved profile: blank (the display's current values) or from a template, whose lines replace the
    /// current values. Nothing is saved until Save.
    /// </summary>
    private async Task StartDraftAsync(string? templateName, bool keepName = false)
    {
        var templateProfile = templateName is null ? null : SceneTemplates.Find(templateName);
        string typed = keepName ? Name : templateProfile is null ? "" : UniqueName(templateProfile.Name);
        originalName = null;
        monitorFilter = null;
        template = templateProfile?.Name;
        building = true;
        try
        {
            isCustom = false;
            LoadApps([]);
            selectedProfile = null;
            Raise(nameof(SelectedProfile));
            name = typed;
            Raise(nameof(Name));
            OnlyThisDisplay = false;
            includeAdvanced = false;
        }
        finally { building = false; }
        await BuildRowsAsync(templateProfile, templateProfile is null ? [] : SceneTemplates.RawSettings(templateProfile.Name));
        // Settings the template does not mention start as the display has them.
        LoadCurrentValues(allRows.Where(r => !r.Include));
        RebuildVisibleRows();
        dirty = true;
        RaiseHeader();
        Raise(nameof(IsEditing));
        Inform(templateProfile is null
            ? (Source is null ? "Type a name and Save." : $"Starts from the current settings of {Source.Name}. Type a name and Save.")
            : $"{templateProfile.Name} template: recommended values for {templateProfile.Name.ToLowerInvariant()}; the rest is what " +
              $"{Source?.Name ?? "the display"} has now. Try it, adjust, then Save.");
    }

    private void LoadCurrentValues(IEnumerable<ProfileSettingRowViewModel> rows)
    {
        foreach (var row in rows.Where(r => r.IsAvailable).ToArray())
        {
            var value = Source?.ShownValue(row.Feature!.Code);
            row.Load(value?.Percent, value?.Raw, included: true, edited: true);
        }
    }

    /// <summary>"Night", or "Night 2" when a profile already has that name.</summary>
    private string UniqueName(string wanted)
    {
        bool Taken(string candidate) => main.SceneProfiles.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase));
        if (!Taken(wanted)) return wanted;
        for (int i = 2; ; i++)
            if (!Taken($"{wanted} {i}")) return $"{wanted} {i}";
    }

    /// <summary>Loads the selected profile (or, with <paramref name="keepDraft"/>, rebuilds the rows for another display).</summary>
    private async Task LoadAsync(bool keepDraft = false)
    {
        if (keepDraft && isCustom)
        {
            await StartCustomAsync();
            return;
        }
        if (keepDraft && IsNew)
        {
            // Another display for a new profile: keep the typed name, read that display's values.
            await StartDraftAsync(template, keepName: true);
            return;
        }
        var profile = main.SceneProfiles.FirstOrDefault(s => string.Equals(s.Name, (keepDraft ? originalName : SelectedProfile?.Name), StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        building = true;
        try
        {
            isCustom = false;
            template = null;
            originalName = profile.Name;
            LoadApps(profile.Apps);
            monitorFilter = profile.MonitorFilter;
            Name = profile.Name;
            OnlyThisDisplay = !string.IsNullOrWhiteSpace(profile.MonitorFilter);
        }
        finally { building = false; }
        IReadOnlyList<KeyValuePair<string, JsonNode?>> raw = [];
        try { raw = SceneStore.RawSettings(SceneStore.ReadText(), profile.Name); }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            Inform($"scenes.json could not be read — {error.Message}", true);
        }
        await BuildRowsAsync(profile, raw);
        // A profile that already has advanced lines opens with the Advanced section shown.
        includeAdvanced = allRows.Any(r => r.IsAdvanced && r.Include);
        RebuildVisibleRows();
        Raise(nameof(IncludeAdvanced));
        Raise(nameof(AdvancedGlyph)); Raise(nameof(AdvancedToolTip));
        dirty = false;
        confirmingDelete = false;
        RaiseHeader();
        Inform(Source is null ? "" : $"Settings this profile did not have yet show what {Source.Name} has now; Save keeps them all.");
    }

    private void RaiseHeader()
    {
        Raise(nameof(IsNew));
        Raise(nameof(IsCustom));
        Raise(nameof(Title));
        Raise(nameof(SaveLabel));
        Raise(nameof(SaveToolTip));
        Raise(nameof(IsEditing));
        Raise(nameof(AdvancedGlyph));
        Raise(nameof(AdvancedToolTip));
        Raise(nameof(Hotkey));
        Raise(nameof(HotkeyText));
        Raise(nameof(DeleteLabel));
        Raise(nameof(OnlyThisDisplayLabel));
        RaiseAutomation();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>One row per control of the source display; the profile's own lines are ticked with their saved values.</summary>
    /// <param name="raw">The profile's lines as written (scenes.json or the template), in the same order as its settings.</param>
    private async Task BuildRowsAsync(SceneProfile? profile, IReadOnlyList<KeyValuePair<string, JsonNode?>> raw)
    {
        allRows.Clear();
        if (Source is { } display) await display.EnsureLoadedAsync();
        var controls = Source?.AllControls.Where(c => c is not ReadOnlyControlViewModel).ToArray() ?? [];
        var features = controls.Select(c => c.Feature).ToArray();
        foreach (var feature in features) allRows.Add(new ProfileSettingRowViewModel(this, feature));

        if (profile is not null)
        {
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
        AdvancedRows.Clear();
        MissingRows.Clear();
        foreach (var row in allRows.Where(r => r.IsAvailable && !r.IsAdvanced)) Rows.Add(row);
        if (includeAdvanced) foreach (var row in allRows.Where(r => r.IsAvailable && r.IsAdvanced)) AdvancedRows.Add(row);
        foreach (var row in allRows.Where(r => !r.IsAvailable)) MissingRows.Add(row);
        LinkVolumeAndMute();
        UpdateModeLocks();
        Raise(nameof(HasAdvancedRows));
        Raise(nameof(HasMissingRows));
    }

    /// <summary>Folds the Mute line into the volume line's speaker icon, as on the Displays tab.</summary>
    private void LinkVolumeAndMute()
    {
        var volume = allRows.FirstOrDefault(r => r.IsAvailable && r.Feature!.Code == VcpCode.AudioVolume);
        var mute = allRows.FirstOrDefault(r => r.IsAvailable && r.Feature!.Code == VcpCode.AudioMute);
        if (volume is not null && mute?.Feature is { } feature && feature.Definition.OnOffValues(feature.AllowedValues) is not null)
            volume.MuteRow = mute;
    }

    /// <summary>
    /// Hides the lines the profile's own picture mode does not allow (the same quirk rules as the Displays panel), so a
    /// Night profile in Reader does not list FreeSync or response time.
    /// </summary>
    public void UpdateModeLocks()
    {
        foreach (var row in allRows.Where(r => r.IsAvailable)) row.IsShown = AllowedByMode(row);
    }

    private bool AllowedByMode(ProfileSettingRowViewModel row)
    {
        foreach (var (code, values) in row.Feature!.Definition.EnabledWhen)
        {
            var mode = allRows.FirstOrDefault(r => r.IsAvailable && r.Feature!.Code == code);
            if (mode?.Selected?.Value is uint value && !values.Contains(value)) return false;
        }
        return true;
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
        foreach (var row in allRows.Where(r => !r.IsAvailable || ((!r.IsAdvanced || includeAdvanced) && r.IsShown)))
            if (row.ToNode() is { } node && keys.Add(row.Key)) lines.Add(new(row.Key, node));
        return lines;
    }

    /// <summary>Applies the profile as it is in the editor, without saving.</summary>
    private async Task TryAsync()
    {
        try
        {
            string draft = SceneStore.Upsert(SceneStore.ReadText(), originalName, Name, Filter, Lines(), AppList());
            var scene = SceneStore.Parse(draft).First(s => string.Equals(s.Name, Name.Trim(), StringComparison.OrdinalIgnoreCase));
            await main.ApplySceneAsync(scene);
            Inform($"\"{scene.Name}\" applied without saving. Save to keep it.");
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            Inform(error.Message, true);
        }
    }

    /// <summary>Custom: sets the values as they are here on the display, as the person's own settings.</summary>
    private async Task ApplyCustomAsync()
    {
        if (Source is null) return;
        try
        {
            // Parsed through a scratch file so the lines are read exactly as a saved profile would be.
            const string scratch = "Custom values";
            string draft = SceneStore.Upsert(SceneStore.DefaultJson, null, scratch, null, Lines());
            var settings = SceneStore.Parse(draft).First().Settings;
            await main.ApplyAsCustomAsync(new SceneProfile(MainViewModel.CustomName, settings), Source);
            dirty = false;
            CommandManager.InvalidateRequerySuggested();
            Inform($"Applied to {Source.Name} as your Custom settings.");
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            Inform(error.Message, true);
        }
    }

    // ---- Linked programs ----

    private readonly List<string> apps = [];

    /// <summary>Programs that turn this profile on while they are in front.</summary>
    public ObservableCollection<LinkedAppItem> LinkedApps { get; } = [];

    /// <summary>Filled each time the "Add app" menu opens.</summary>
    public ObservableCollection<RunningAppItem> RunningApps { get; } = [];

    /// <summary>Custom is the person's own settings: nothing turns it on, so it links no program.</summary>
    public bool CanAutomate => !isCustom;

    public bool HasLinkedApps => LinkedApps.Count > 0;

    public string LinkedAppsHint => apps.Count == 0
        ? "No program linked. Add one and this profile turns on, gradually, while that program is in front; when you leave it the displays go back to what they had."
        : "While one of these is in front, this profile is on (after 2 s, so Alt+Tab passes by). Leaving it brings back what the displays had.";

    public ICommand BrowseAppCommand => new RelayCommand(BrowseApp);

    /// <summary>Lists the programs with a window (called when the menu opens).</summary>
    public void RefreshRunningApps()
    {
        RunningApps.Clear();
        foreach (var (exe, title) in AppLinker.RunningApps())
            if (!apps.Contains(exe, StringComparer.OrdinalIgnoreCase))
                RunningApps.Add(new RunningAppItem(exe, title, new RelayCommand(() => AddApp(exe))));
    }

    private void LoadApps(IEnumerable<string> list)
    {
        apps.Clear();
        apps.AddRange(list);
        RebuildApps();
    }

    private void AddApp(string exe)
    {
        string name = SceneStore.AppName(exe);
        if (name.Length == 0 || apps.Contains(name, StringComparer.OrdinalIgnoreCase)) return;
        if (main.SceneProfiles.FirstOrDefault(p => p.IsLinkedTo(name) && !string.Equals(p.Name, originalName, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            Inform($"{name} is already linked to \"{other.Name}\": remove it there first.", true);
            return;
        }
        apps.Add(name);
        RebuildApps();
        MarkDirty();
        Inform($"{name} linked. Save to keep it.");
    }

    private void RemoveApp(string exe)
    {
        apps.RemoveAll(a => string.Equals(a, exe, StringComparison.OrdinalIgnoreCase));
        RebuildApps();
        MarkDirty();
    }

    private void RebuildApps()
    {
        LinkedApps.Clear();
        foreach (string exe in apps) LinkedApps.Add(new LinkedAppItem(exe, new RelayCommand(() => RemoveApp(exe))));
        Raise(nameof(HasLinkedApps));
        Raise(nameof(LinkedAppsHint));
    }

    private void BrowseApp()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Program that turns this profile on",
            Filter = "Programs (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() == true) AddApp(dialog.FileName);
    }

    private IReadOnlyList<string>? AppList() => isCustom ? null : apps.ToArray();

    private void RaiseAutomation() => Raise(nameof(CanAutomate));

    private void Save()
    {
        try
        {
            string updated = SceneStore.Upsert(SceneStore.ReadText(), originalName, Name, Filter, Lines(), AppList());
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
