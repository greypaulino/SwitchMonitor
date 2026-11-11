using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Inputs;
using SwitchMonitor.App.Automation;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Schedule;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App.ViewModels;

/// <summary>A button of the profile bar: a saved profile, or Custom (null scene) = the person's own settings.</summary>
public sealed class SceneViewModel : ObservableObject
{
    private bool isActive;

    public SceneViewModel(SceneProfile? scene, MainViewModel main)
    {
        Scene = scene;
        ApplyCommand = new RelayCommand(() => _ = scene is null ? main.RestoreCustomAsync() : main.ApplySceneAsync(scene));
        SaveCommand = new RelayCommand(main.SaveModifiedProfile);
    }

    private bool isModified;
    /// <summary>The active profile, with settings changed by hand since it was applied ("Work · custom").</summary>
    public bool IsModified { get => isModified; set { if (Set(ref isModified, value)) Raise(nameof(Label)); } }
    /// <summary>The name shown on the pill: "Work", or "Work · custom" once changed by hand.</summary>
    public string Label => isModified ? $"{Name} · custom" : Name;
    /// <summary>"Save?": writes the values changed by hand into this profile.</summary>
    public ICommand SaveCommand { get; }

    public SceneProfile? Scene { get; }
    public bool IsCustom => Scene is null;
    public string Name => Scene?.Name ?? MainViewModel.CustomName;
    public string Hotkey => Scene?.Hotkey?.ToString() ?? "";
    public IReadOnlyList<string> HotkeyKeys => Hotkey.Length == 0 ? [] : Hotkey.Split('+');
    public string Summary => Scene is null
        ? "Your own settings. After applying a profile, click Custom to get your settings back."
        : string.Join(" · ", Scene.Settings.Select(s => s.ToString()));

    /// <summary>The profile the displays are in now (Custom until one is applied).</summary>
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }

    public ICommand ApplyCommand { get; }

    /// <summary>The profile's color and icon (its own color, or the usual one for its name).</summary>
    public string Color => Icons.ColorForProfile(Name, Scene?.Color);
    public string Glyph => Icons.ForProfile(Name, Scene?.Icon);
    /// <summary>The profile's icon as drawn (its style's font and weight).</summary>
    public Icons.Mark Mark => Icons.ProfileMark(Name, Scene?.Icon);
    /// <summary>The same icon, solid (the brightness panel's small badges).</summary>
    public Icons.Mark SolidMark => Icons.SolidProfileMark(Name, Scene?.Icon);
    public void RefreshMark()
    {
        Raise(nameof(Mark));
        Raise(nameof(SolidMark));
    }

    private bool isShown = true;
    /// <summary>The profile applies to the display selected (Custom always does).</summary>
    public bool IsShown { get => isShown; set => Set(ref isShown, value); }
    /// <summary>The picture the profile chose as its icon, or null.</summary>
    public System.Windows.Media.ImageSource? IconImage => Icons.ProfileImage(Scene?.Icon);
    public System.Windows.Media.Brush ColorBrush => Icons.Brush(Color);
    public System.Windows.Media.Brush SoftBrush => Icons.Brush(Color, 0.18);
    public System.Windows.Media.Brush EdgeBrush => Icons.Brush(Color, 0.55);
}

public enum AppTab { Displays, Advanced, Profiles, Shortcuts, Apps, Settings, Learn }

public sealed partial class MainViewModel : ObservableObject, IInputCoordinator
{
    private readonly ICoreLog log;
    private readonly InputHistory history;
    private readonly Dictionary<string, InputReturnWatch> returnWatches = [];
    private readonly DispatcherTimer returnTimer;
    private readonly BrightnessSteps brightnessSteps;
    private readonly DispatcherTimer statusTimer;
    private readonly DispatcherTimer resetTimer;
    private bool confirmingReset;

    /// <summary>Reset to factory was clicked once; the next click within a few seconds sends it.</summary>
    public bool ConfirmingReset
    {
        get => confirmingReset;
        private set
        {
            if (!Set(ref confirmingReset, value)) return;
            if (!value) resetTimer.Stop();
            Raise(nameof(ResetLabel));
        }
    }

    public string ResetLabel => ConfirmingReset ? "Click again to reset" : "Reset to factory";
    private MonitorViewModel? selected;
    private string status = "Looking for displays…";
    private bool statusIsError;
    private bool isBusy;
    private string? shortcutWarning;
    private bool polling;
    private DateTime lastHoldStep;
    private readonly Core.Color.ColorStore colorStore = Core.Color.ColorStore.Load();
    private string activeProfile = CustomName;
    private Dictionary<string, List<SceneSetting>>? customSnapshot;
    /// <summary>The active saved profile was changed by hand since it was applied (shown as "custom", can be saved).</summary>
    private bool profileModified;
    /// <summary>What was changed by hand: the display and the control codes (brightness 0x10...).</summary>
    private MonitorViewModel? modifiedOn;
    private readonly HashSet<byte> modifiedCodes = [];

    /// <summary>Name of the always-present first profile: the person's own (hand-made) settings.</summary>
    public const string CustomName = SceneStore.CustomName;

    public MainViewModel(ICoreLog log, AppSettings settings)
    {
        this.log = log;
        Settings = settings;
        history = InputHistory.Load();
        brightnessSteps = new BrightnessSteps(settings.BrightnessStep);
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        ReloadValuesCommand = new RelayCommand(() => _ = Selected?.ReloadValuesAsync() ?? Task.CompletedTask);
        TogglePreferredCommand = new RelayCommand(p => { if (p is MonitorViewModel monitor) TogglePreferred(monitor); });
        LearnCommand = new RelayCommand(_ => OpenLearn(), _ => Selected is { IsControllable: true } && !IsBusy);
        // Writes factory values to the monitor: the first click only arms it, as Delete does on the Profiles tab.
        resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        resetTimer.Tick += (_, _) => ConfirmingReset = false;
        ResetAllCommand = new RelayCommand(() =>
        {
            if (!ConfirmingReset) { ConfirmingReset = true; resetTimer.Start(); return; }
            ConfirmingReset = false;
            _ = Selected?.ResetAsync(null) ?? Task.CompletedTask;
        });
        RetryShortcutsCommand = new RelayCommand(() => ShortcutsChanged?.Invoke());
        OpenShortcutsCommand = new RelayCommand(() => IsShortcutsTab = true);
        UndoProfileCommand = new RelayCommand(() => _ = RestoreCustomAsync());
        statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        statusTimer.Tick += (_, _) => { statusTimer.Stop(); Status = ""; };
        returnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        returnTimer.Tick += async (_, _) => await PollReturnWatchesAsync();
        Automation = AutomationStore.Load();
        Apps = new AppLinker(this, Automation, log);
        var placements = new AppPlacementStore();
        var usage = new AppUsage();
        AppsTab = new AppsViewModel(this, placements, usage);
        Placer = new AppPlacer(placements, usage, AppsTab.AreaOf);
    }

    /// <summary>Where displays come from: Windows by default, in-memory ones in demo mode (--demo).</summary>
    public Func<IReadOnlyList<MonitorDevice>>? DeviceSource { get; init; }

    /// <summary>
    /// How a profile change that toggles FreeSync hides the black-out (set by the app: a black cover over the screens).
    /// Null, or with demo displays (nothing really blacks out): the profile is applied as always.
    /// </summary>
    public TransitionHooks? Transition { get; set; }

    /// <summary>Only for the flow self-test: use the cover with the in-memory demo displays too.</summary>
    public bool CoverInDemo { get; set; }

    private readonly SemaphoreSlim applyGate = new(1, 1);   // one profile change at a time: displays never see two interleaved
    private int applySeq;                                    // the newest request; older ones waiting behind a running change are dropped

    /// <summary>
    /// What a profile change carries: the black cover for FreeSync (not with demo displays) and the progress of the levels,
    /// so the sliders move with the real, gradual change.
    /// </summary>
    private TransitionHooks HooksForProfile()
    {
        var cover = DeviceSource is null || CoverInDemo ? Transition : null;
        return new TransitionHooks
        {
            BeforeDisruptive = cover?.BeforeDisruptive,
            AfterDisruptive = cover?.AfterDisruptive,
            StableWithin = cover?.StableWithin ?? TimeSpan.FromSeconds(8),
            LevelMoved = FollowLevel
        };
    }

    private void FollowLevel(MonitorDevice device, byte code, uint raw)
    {
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;
        dispatcher.BeginInvoke(() => Monitors.FirstOrDefault(m => ReferenceEquals(m.Device, device))?.FollowLevel(code, raw));
    }

    /// <summary>Linked-programs switch (automation.json).</summary>
    public AutomationStore Automation { get; }

    /// <summary>Profiles turned on by the program in front.</summary>
    public AppLinker Apps { get; }

    /// <summary>⋯ menu switch: linked programs change profiles (saved in automation.json).</summary>
    public bool LinkedAppsEnabled
    {
        get => Apps.Enabled;
        set
        {
            if (Apps.Enabled == value) return;
            Apps.Enabled = value;
            Raise();
            Report(value ? "Linked apps switch profiles again." : "Linked apps paused: profiles change only by hand.", true);
        }
    }

    /// <summary>The profile the displays are in now ("Custom" for the person's own settings).</summary>
    public string ActiveProfile => activeProfile;

    public AppSettings Settings { get; private set; }

    private Services.PhoneBridge? phone;
    /// <summary>Settings > Phone (control from a phone on the home Wi-Fi); null in demo mode.</summary>
    public Services.PhoneBridge? Phone { get => phone; set => Set(ref phone, value); }

    private Services.PeerService? peers;
    /// <summary>Settings > Computers (control other computers from this one, their displays in the brightness panel); null in demo mode.</summary>
    public Services.PeerService? Peers { get => peers; set => Set(ref peers, value); }

    /// <summary>The clipboard shared with the group (Settings > Devices); null in demo mode.</summary>
    public Services.ClipboardSync? Clipboard { get; set; }
    public Services.FileTransfer? Transfers { get; set; }

    /// <summary>A Windows notification (title, text, what a click does); set by the app once the tray icon exists.</summary>
    public Action<string, string, Action>? Notify { get; set; }

    /// <summary>One mouse and keyboard for two paired computers (Settings > Devices).</summary>
    public Services.InputShareService? Share { get => share; set => Set(ref share, value); }
    private Services.InputShareService? share;
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public ObservableCollection<SceneViewModel> Scenes { get; } = [];
    public IReadOnlyList<SceneProfile> SceneProfiles { get; private set; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand ReloadValuesCommand { get; }

    // ---- Preferred main display ----

    /// <summary>Marks a display as the preferred main display, or clears the mark when it already is.</summary>
    public ICommand TogglePreferredCommand { get; }

    public void TogglePreferred(MonitorViewModel monitor)
    {
        bool clear = string.Equals(Settings.PreferredMain, monitor.Key, StringComparison.OrdinalIgnoreCase);
        UpdateSettings(Settings with { PreferredMain = clear ? null : monitor.Key }, shortcutsChanged: false);
        MarkPreferred();
        if (clear)
        {
            Report($"{monitor.Name} is no longer the preferred main display.", true);
            return;
        }
        Report($"{monitor.Name} is the preferred main display: it becomes the main display whenever it is connected.", true);
        ApplyPreferredMain();
    }

    /// <summary>Shows which display is preferred (star in the window and the brightness panel).</summary>
    public void MarkPreferred()
    {
        foreach (var monitor in Monitors)
            monitor.IsPreferredMain = string.Equals(Settings.PreferredMain, monitor.Key, StringComparison.OrdinalIgnoreCase);
        PreferredChanged?.Invoke();
    }

    public event Action? PreferredChanged;

    /// <summary>After the main display moved (here or from the brightness panel): subtitles and panel marks follow.</summary>
    public void MainDisplayChanged()
    {
        foreach (var monitor in Monitors) monitor.RaiseMainDisplay();
        PreferredChanged?.Invoke();
    }

    /// <summary>
    /// Moves the main display to the preferred one when it is connected, in use and not the main display yet.
    /// True when it moved.
    /// </summary>
    public bool ApplyPreferredMain()
    {
        var seats = Monitors.Select(m => new Core.Displays.DisplaySeat(m.Key, m.Device.Identity.GdiName, m.IsActive)).ToArray();
        // Windows' own list of displays in the desktop is the reference for "in use".
        var active = Services.MainDisplay.Active;
        seats = seats.Select(s => s with { IsActive = s.IsActive && active.Contains(s.GdiName ?? "", StringComparer.OrdinalIgnoreCase) }).ToArray();
        if (Core.Displays.PreferredMain.Target(Settings.PreferredMain, seats, Services.MainDisplay.Current) is not { } target) return false;
        var monitor = Monitors.First(m => string.Equals(m.Device.Identity.GdiName, target, StringComparison.OrdinalIgnoreCase));
        try
        {
            Services.MainDisplay.Set(target);
            MainDisplayChanged();
            Report($"{monitor.Name} is the main display again (preferred).", true);
            log.Write(LogLevel.Info, $"Preferred main display: moved the main display to {monitor.Name}.");
            return true;
        }
        catch (InvalidOperationException error)
        {
            Report($"Preferred main display: {error.Message}", false);
            return false;
        }
    }
    public ICommand ResetAllCommand { get; }
    public ICommand RetryShortcutsCommand { get; }
    public ICommand OpenShortcutsCommand { get; }

    // ---- Views: Displays | Advanced | Profiles | Shortcuts ----

    private AppTab tab = AppTab.Displays;
    private ShortcutEditorViewModel? shortcutEditor;
    private ProfileEditorViewModel? profileEditor;

    /// <summary>Tells whether a shortcut is free (set by the app, which owns the hotkey service).</summary>
    public Func<Core.Hotkeys.HotkeyChord, bool> IsShortcutFree { get; set; } = _ => true;

    /// <summary>Raised with true when global shortcuts must pause (Shortcuts tab shown) and false when they resume.</summary>
    public event Action<bool>? ShortcutsPaused;

    public AppTab Tab
    {
        get => tab;
        set
        {
            if (value == AppTab.Advanced && !ShowAdvanced) value = AppTab.Displays;
            if (tab == value) return;
            var previous = tab;
            tab = value;
            if (previous == AppTab.Profiles) ProfileEditor?.CancelHotkeyCapture();
            if (previous == AppTab.Shortcuts)
            {
                ShortcutEditor?.CancelCapture();
                ShortcutsPaused?.Invoke(false);
            }
            if (value == AppTab.Shortcuts)
            {
                ShortcutsPaused?.Invoke(true);   // before the editor tests which shortcuts are free
                ResetShortcutEditor();
            }
            if (value == AppTab.Profiles) ResetProfileEditor();
            if (value == AppTab.Apps) AppsTab.Reload();
            if (previous == AppTab.Learn && learn is { } wizard)
            {
                Learn = null;
                _ = wizard.CloseAsync();   // puts back whatever the wizard left changed
            }
            if (previous == AppTab.Settings && Interface.IsDirty)
            {
                Interface.Discard();   // a preview that was not saved does not stay
                Report("Settings not saved: the saved look is back.", true);
            }
            Raise();
            Raise(nameof(IsDisplaysTab));
            Raise(nameof(IsAdvancedTab));
            Raise(nameof(IsProfilesTab));
            Raise(nameof(IsShortcutsTab));
            Raise(nameof(IsAppsTab));
            Raise(nameof(IsSettingsTab));
            Raise(nameof(IsLearnTab));
            Raise(nameof(ShowsMonitorList));
        }
    }

    public bool IsDisplaysTab { get => tab == AppTab.Displays; set { if (value) Tab = AppTab.Displays; } }
    public bool IsAdvancedTab { get => tab == AppTab.Advanced; set { if (value) Tab = AppTab.Advanced; } }
    public bool IsProfilesTab { get => tab == AppTab.Profiles; set { if (value) Tab = AppTab.Profiles; } }
    public bool IsShortcutsTab { get => tab == AppTab.Shortcuts; set { if (value) Tab = AppTab.Shortcuts; } }
    // ---- Pages inside Settings ----

    private string settingsPage = "General";
    public string SettingsPage
    {
        get => settingsPage;
        set
        {
            if (!Set(ref settingsPage, value)) return;
            foreach (string name in new[] { nameof(IsSettingsGeneral), nameof(IsSettingsAppearance), nameof(IsSettingsDevices), nameof(IsSettingsMonitors), nameof(IsSettingsUpdates), nameof(IsSettingsAbout) })
                Raise(name);
        }
    }
    public bool IsSettingsGeneral { get => settingsPage == "General"; set { if (value) SettingsPage = "General"; } }
    public bool IsSettingsAppearance { get => settingsPage == "Appearance"; set { if (value) SettingsPage = "Appearance"; } }
    public bool IsSettingsDevices { get => settingsPage == "Devices"; set { if (value) SettingsPage = "Devices"; } }
    public bool IsSettingsMonitors { get => settingsPage == "Monitors"; set { if (value) SettingsPage = "Monitors"; } }
    public bool IsSettingsUpdates { get => settingsPage == "Updates"; set { if (value) SettingsPage = "Updates"; } }
    public bool IsSettingsAbout { get => settingsPage == "About"; set { if (value) SettingsPage = "About"; } }

    // ---- Features ----

    public IReadOnlyList<FeatureGroup> FeatureGroups => Features.Groups;

    /// <summary>Opens the Settings page where a feature is set up.</summary>
    public RelayCommand OpenFeaturePageCommand => openFeaturePage ??= new RelayCommand(p => { if (p is string page && page.Length > 0) SettingsPage = page; });
    private RelayCommand? openFeaturePage;

    /// <summary>Shows the welcome tour again, with every feature.</summary>
    public RelayCommand ShowTourCommand => showTour ??= new RelayCommand(() => TourRequested?.Invoke(true));
    private RelayCommand? showTour;
    public event Action<bool>? TourRequested;

    // ---- Language ----

    public IReadOnlyList<string> Languages { get; } = ["English", "Español"];

    /// <summary>The language of the app; applies to every open window at once and is kept in the settings.</summary>
    public string LanguageName
    {
        get => Loc.Language == "es" ? "Español" : "English";
        set
        {
            string code = value == "Español" ? "es" : "en";
            if (code == Loc.Language) return;
            UpdateSettings(Settings with { Language = code }, shortcutsChanged: false);
            Loc.Set(code);
            Raise();
        }
    }

    public bool IsAppsTab { get => tab == AppTab.Apps; set { if (value) Tab = AppTab.Apps; } }

    /// <summary>Which program opens on which display of this computer (the Apps tab), and the watch that moves their windows.</summary>
    public AppsViewModel AppsTab { get; }
    public AppPlacer Placer { get; }

    /// <summary>The app's own settings (gear button); pressing the gear again goes back to Displays.</summary>
    public bool IsSettingsTab
    {
        get => tab == AppTab.Settings;
        set => Tab = value ? AppTab.Settings : tab == AppTab.Settings ? AppTab.Displays : tab;
    }

    private InterfaceViewModel? interfaceSettings;
    private LearnWizardViewModel? learn;

    /// <summary>Learn settings (button in the display heading): a guided search for settings the app does not know yet.</summary>
    public ICommand LearnCommand { get; }

    public LearnWizardViewModel? Learn { get => learn; private set => Set(ref learn, value); }

    public bool IsLearnTab => tab == AppTab.Learn;

    private void OpenLearn()
    {
        if (Selected is not { IsControllable: true } monitor || tab == AppTab.Learn) return;
        Learn = new LearnWizardViewModel(this, monitor);
        Tab = AppTab.Learn;
    }

    /// <summary>Profiles that keep their usual icon follow the panel's icon style: redraw them.</summary>
    public void RefreshProfileMarks()
    {
        foreach (var scene in Scenes) scene.RefreshMark();
    }

    /// <summary>The Settings view: interface color, appearance, linked apps.</summary>
    public InterfaceViewModel Interface => interfaceSettings ??= new(Theme.Current, this);

    /// <summary>Updates (Settings > Updates, the banner and the tray notice).</summary>
    public UpdateViewModel Updates => updates ??= new(this);
    private UpdateViewModel? updates;

    /// <summary>A profile change is running (an update must not start in the middle of one).</summary>
    public bool IsApplyingProfile => applyGate.CurrentCount == 0;

    /// <summary>The display list on the left serves the Displays and Advanced tabs.</summary>
    public bool ShowsMonitorList => tab is AppTab.Displays or AppTab.Advanced;

    /// <summary>
    /// Color fine-tuning (red/green/blue and black levels) open on the Displays tab, and the advanced rows of the profile
    /// editor (saved in settings.json).
    /// </summary>
    public bool ShowAdvanced
    {
        get => Settings.ShowAdvanced;
        set
        {
            if (Settings.ShowAdvanced == value) return;
            UpdateSettings(Settings with { ShowAdvanced = value }, shortcutsChanged: false);
            Raise();
            if (!value && tab == AppTab.Advanced) Tab = AppTab.Displays;
        }
    }

    /// <summary>A profile was applied by hand or by a linked app, and the settings before it can come back.</summary>
    public bool CanUndoProfile => activeProfile != CustomName && customSnapshot is not null;

    public ICommand UndoProfileCommand { get; }

    /// <summary>The editor shown on the Profiles tab; rebuilt when the tab opens and after profiles change.</summary>
    public ProfileEditorViewModel? ProfileEditor { get => profileEditor; private set => Set(ref profileEditor, value); }

    private void ResetProfileEditor(string? select = null)
    {
        ProfileEditor = new ProfileEditorViewModel(this, select);
    }

    /// <summary>Called by the profile editor after scenes.json changed: reload profiles and shortcuts.</summary>
    public void ProfilesChanged(string? select)
    {
        LoadScenes();
        if (tab == AppTab.Profiles) ResetProfileEditor(select);
    }

    /// <summary>The editor shown on the Shortcuts tab; replaced after Save or Cancel.</summary>
    public ShortcutEditorViewModel? ShortcutEditor { get => shortcutEditor; private set => Set(ref shortcutEditor, value); }

    private void ResetShortcutEditor()
    {
        // Only what concerns the display selected on the Displays view; the rest is kept and counts as taken.
        var display = Selected ?? Monitors.FirstOrDefault(m => m.Inputs.Count > 0);
        var displays = DisplayInputsForEditor().Where(d => display is null || d.Key == display.Key).ToArray();
        var scenes = SceneProfiles.Where(s => display is null || s.AppliesTo(display.Device)).ToArray();
        var reserved = new Dictionary<Core.Hotkeys.HotkeyChord, string>();
        // Input shortcuts of the other displays do NOT reserve their combination: two displays may use the same one (Ctrl+Alt+1) and
        // it acts on the display under the mouse. Profile shortcuts of other displays stay reserved.
        foreach (var scene in SceneProfiles.Except(scenes))
            if (scene.Hotkey is { } chord) reserved.TryAdd(chord, $"profile {scene.Name} (another display)");
        var editor = new ShortcutEditorViewModel(Settings, displays, scenes, IsShortcutFree, reserved, display?.Name);
        editor.Saved += SaveShortcuts;
        editor.CloseRequested += () => { if (tab == AppTab.Shortcuts) ResetShortcutEditor(); };
        ShortcutEditor = editor;
    }

    public MonitorViewModel? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value)) return;
            ShowProfilesForSelected();
            if (value is not null) _ = value.EnsureLoadedAsync();
        }
    }

    public string Status { get => status; private set => Set(ref status, value); }
    public bool StatusIsError { get => statusIsError; private set => Set(ref statusIsError, value); }
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) Raise(nameof(ShowsSearchSkeleton)); } }

    /// <summary>Looking for displays with none listed yet: the list and the page show placeholders instead of an empty window.</summary>
    public bool ShowsSearchSkeleton => isBusy && Monitors.Count == 0;

    /// <summary>Shortcut problems stay visible (unlike the status line) until they are fixed.</summary>
    public string? ShortcutWarning
    {
        get => shortcutWarning;
        set { if (Set(ref shortcutWarning, value)) Raise(nameof(HasShortcutWarning)); }
    }

    public bool HasShortcutWarning => !string.IsNullOrEmpty(ShortcutWarning);

    /// <summary>Raised when shortcuts must be (re)registered: scenes loaded, settings changed, or Retry pressed.</summary>
    public event Action? ShortcutsChanged;

    /// <summary>Raised when "Return to previous input" may have become available or unavailable.</summary>
    public event Action? InputHistoryChanged;

    public void Report(string message, bool ok)
    {
        Status = message;
        StatusIsError = !ok;
        // A confirmation fades out after a while; work in progress ("…") and problems stay until the next message.
        statusTimer.Stop();
        if (ok && !message.EndsWith('…')) statusTimer.Start();
        log.Write(ok ? LogLevel.Info : LogLevel.Warning, message);
    }

    /// <summary>Keys of the Next input shortcut ("Ctrl", "Alt", "M"), shown in the Input heading.</summary>
    public IReadOnlyList<string> NextInputKeys =>
        Settings.For(HotkeyAction.NextInput) is [var first, ..] ? first.ToString().Split('+') : ["not set"];

    public void UpdateSettings(AppSettings updated, bool shortcutsChanged)
    {
        Settings = updated;
        Raise(nameof(NextInputKeys));
        try { updated.Save(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Report($"settings.json could not be saved — {error.Message}", false);
        }
        if (shortcutsChanged)
        {
            foreach (var input in Monitors.SelectMany(m => m.Inputs)) input.Refresh();
            ShortcutsChanged?.Invoke();
        }
    }

    /// <summary>A display of another computer joins the list (after the local ones).</summary>
    public MonitorViewModel AddRemote(MonitorDevice device)
    {
        var monitor = new MonitorViewModel(device, Report, this) { TogglePreferred = TogglePreferred };   // no cover: it would black out THIS screen
        Monitors.Add(monitor);
        _ = LoadRemoteAsync(monitor);
        return monitor;
    }

    /// <summary>Raised when a remote display has been read: the brightness panel picks up its slider.</summary>
    public event Action? RemoteLoaded;

    private async Task LoadRemoteAsync(MonitorViewModel monitor)
    {
        try { await monitor.EnsureLoadedAsync(); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        RemoteLoaded?.Invoke();
    }

    /// <summary>The display of another computer leaves the list (that computer is gone, or no longer offers it).</summary>
    public void RemoveRemote(MonitorViewModel monitor)
    {
        if (!Monitors.Remove(monitor)) return;
        if (ReferenceEquals(Selected, monitor)) Selected = Monitors.FirstOrDefault(m => m.IsControllable);
        monitor.Dispose();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Report("Looking for displays…", true);
        try
        {
            string? keep = Selected?.Device.Key;
            // Displays of other computers stay: they come and go with their computer's connection, not with this search.
            foreach (var old in Monitors.Where(m => !m.IsRemote).ToArray()) { old.Dispose(); Monitors.Remove(old); }
            Raise(nameof(ShowsSearchSkeleton));
            Selected = null;
            returnWatches.Clear();

            var devices = await Task.Run(() => DeviceSource?.Invoke() ?? MonitorEnumerator.Enumerate(CoreOptions.FromDataDirectory(), log: log));
            int place = 0;
            foreach (var device in devices.OrderByDescending(d => d.IsControllable).ThenByDescending(d => d.Identity.IsPrimary))
                Monitors.Insert(place++, new MonitorViewModel(device, Report, this) { TogglePreferred = TogglePreferred, TransitionSource = () => DeviceSource is null || CoverInDemo ? Transition : null });
            Raise(nameof(ShowsSearchSkeleton));
            // Panel names from the EDID for built-in screens (Windows display configuration, quick and read-only).
            var summaries = await Task.Run(() => BuiltInProbe.Displays());
            foreach (var monitor in Monitors.Where(m => m.Device.Identity.IsBuiltIn))
                monitor.Panel = summaries.FirstOrDefault(s => string.Equals(s.GdiName, monitor.Device.Identity.GdiName, StringComparison.OrdinalIgnoreCase))?.Panel;
            // Software color only for built-in panels: they have no color controls of their own. External monitors use
            // their own presets (a gamma tint on top of them would cost brightness and contrast).
            foreach (var monitor in Monitors.Where(m => m.IsActive && !string.IsNullOrEmpty(m.Device.Identity.GdiName)))
            {
                if (monitor.Device.Identity.IsBuiltIn)
                {
                    monitor.SoftwareColor = new SoftwareColorViewModel(monitor, colorStore, Report);
                    monitor.SoftwareColor.Reapply();
                }
                else if (!colorStore.Get(monitor.Key).IsNeutral)
                {
                    // A tint saved by an earlier version: undo it once and forget it.
                    Core.Color.SoftwareColor.Reset(monitor.Device.Identity.GdiName);
                    colorStore.Set(monitor.Key, new Core.Color.SoftwareColorSetting());
                }
            }

            Selected = Monitors.FirstOrDefault(m => m.Device.Key == keep) ?? Monitors.FirstOrDefault(m => m.IsControllable);
            Report(Monitors.Count == 0 ? "No displays found." : $"{Monitors.Count(m => m.IsControllable)} of {Monitors.Count} display(s) can be controlled.", Monitors.Count > 0);
            LoadScenes();
            MarkPreferred();
            // Load every controllable display in the background so shortcuts respond at once.
            foreach (var monitor in Monitors.Where(m => m.IsControllable)) _ = monitor.EnsureLoadedAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Report($"Could not list displays — {error.Message}", false);
        }
        finally
        {
            IsBusy = false;
            CommandManager.InvalidateRequerySuggested();   // Refresh displays is enabled again now, not at the next mouse move
        }
    }

    /// <summary>What the shortcut editor needs to list per-input rows.</summary>
    public IReadOnlyList<DisplayInputs> DisplayInputsForEditor() =>
        Monitors.Where(m => m.Inputs.Count > 0)
            .Select(m => new DisplayInputs(m.Key, m.Name, m.Inputs.Select(i => (i.Code, i.Label)).ToArray()))
            .ToArray();

    /// <summary>Pauses (or resumes) the global shortcuts while a shortcut is being recorded outside the Shortcuts tab.</summary>
    public void SetShortcutsPaused(bool paused) => ShortcutsPaused?.Invoke(paused);

    /// <summary>
    /// What already uses <paramref name="chord"/>: an app action, an input shortcut or another profile (the profile named
    /// <paramref name="exceptProfile"/> is the one being edited and does not count). Null when it is free of SwitchMonitor's own.
    /// </summary>
    public string? ShortcutOwner(Core.Hotkeys.HotkeyChord chord, string? exceptProfile)
    {
        foreach (var (action, chords) in Settings.Hotkeys)
            if (chords.Contains(chord)) return Core.Settings.HotkeyAction.Describe(action);
        if (Settings.InputShortcuts.FirstOrDefault(i => i.Chord == chord) is { } input) return DescribeInputShortcut(input);
        if (SceneProfiles.FirstOrDefault(s => s.Hotkey == chord && !string.Equals(s.Name, exceptProfile, StringComparison.OrdinalIgnoreCase)) is { } scene)
            return $"profile {scene.Name}";
        return null;
    }

    /// <summary>Saves edited shortcuts: settings.json for actions and inputs, scenes.json for scene shortcuts.</summary>
    public void SaveShortcuts(AppSettings updated, IReadOnlyDictionary<string, Core.Hotkeys.HotkeyChord?> sceneHotkeys)
    {
        UpdateSettings(updated, shortcutsChanged: false);
        foreach (var input in Monitors.SelectMany(m => m.Inputs)) input.Refresh();
        if (sceneHotkeys.Count > 0)
            try { SceneStore.SaveHotkeys(sceneHotkeys); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException)
            {
                Report($"Scene shortcuts could not be saved — {error.Message}", false);
                return;
            }
        LoadScenes();
        Report("Shortcuts saved.", true);
    }

    private void LoadScenes()
    {
        Scenes.Clear();
        Scenes.Add(new SceneViewModel(null, this));
        try
        {
            SceneProfiles = SceneStore.LoadOrCreate();
            foreach (var scene in SceneProfiles) Scenes.Add(new SceneViewModel(scene, this));
            if (!SceneProfiles.Any(s => s.Name == activeProfile)) activeProfile = CustomName;
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            SceneProfiles = [];
            Report($"Profiles (scenes.json): {error.Message}", false);
        }
        MarkActiveProfile();
        ShowProfilesForSelected();
        ShortcutsChanged?.Invoke();
    }

    /// <summary>The profile row shows the profiles that apply to the selected display.</summary>
    private void ShowProfilesForSelected()
    {
        foreach (var scene in Scenes)
            scene.IsShown = scene.Scene is null || selected is null || scene.Scene.AppliesTo(selected.Device);
    }

    private void MarkActiveProfile()
    {
        if (activeProfile == CustomName) ForgetModified();
        foreach (var scene in Scenes)
        {
            scene.IsActive = string.Equals(scene.Name, activeProfile, StringComparison.OrdinalIgnoreCase);
            scene.IsModified = scene.IsActive && !scene.IsCustom && profileModified;
        }
        Raise(nameof(CanUndoProfile));
        Raise(nameof(ProfileModified));
    }

    /// <summary>The active saved profile has values changed by hand (the pill says "custom" and offers "Save?").</summary>
    public bool ProfileModified => profileModified && activeProfile != CustomName;

    private void ForgetModified()
    {
        profileModified = false;
        modifiedOn = null;
        modifiedCodes.Clear();
    }

    /// <summary>
    /// A setting changed by hand. A saved profile stays the active one and shows "custom" (its values may now differ);
    /// "Save?" writes them into it, choosing it again brings its saved values back, Custom brings the person's own.
    /// </summary>
    /// <summary>
    /// After a FreeSync change made by hand: waits for the panel's own re-reads (0.4 s and 2 s) and for any profile running,
    /// then looks the displays up again (see <see cref="ApplySceneAsync"/>). Not with demo displays.
    /// </summary>
    public async void ReopenDisplaysAfterResync()
    {
        if (DeviceSource is not null) return;
        await Task.Delay(TimeSpan.FromSeconds(3));
        await applyGate.WaitAsync();
        try { await RefreshAsync(); }
        finally { applyGate.Release(); }
    }

    public void SettingChangedByHand(MonitorViewModel monitor, byte? code = null)
    {
        if (activeProfile == CustomName) return;
        modifiedOn = monitor;
        if (code is byte c) modifiedCodes.Add(c);
        if (profileModified) return;
        profileModified = true;
        MarkActiveProfile();
    }

    /// <summary>"Save?": the active profile takes the values the display shows now (what was changed by hand).</summary>
    public void SaveModifiedProfile()
    {
        var scene = SceneProfiles.FirstOrDefault(s => string.Equals(s.Name, activeProfile, StringComparison.OrdinalIgnoreCase));
        if (scene is null || !profileModified) return;
        var source = modifiedOn is { } edited && scene.AppliesTo(edited.Device) ? edited
            : Monitors.FirstOrDefault(m => m.IsControllable && m.IsLoaded && scene.AppliesTo(m.Device));
        if (source is null)
        {
            Report($"{scene.Name}: no display to take the values from.", false);
            return;
        }
        try
        {
            string json = SceneStore.ReadText();
            var raw = SceneStore.RawSettings(json, scene.Name);
            var lines = new List<KeyValuePair<string, System.Text.Json.Nodes.JsonNode>>();
            int changed = 0;
            // Lines are in file order, the same order the profile was read in.
            for (int i = 0; i < raw.Count; i++)
            {
                var (key, node) = raw[i];
                var keep = node?.DeepClone();
                var setting = i < scene.Settings.Count ? scene.Settings[i] : null;
                if (setting is not null && source.ShownValue(setting.Feature.Code) is { } now)
                {
                    bool same = setting.Feature.Kind == Core.Vcp.FeatureKind.Continuous
                        ? setting.Percent is double p && now.Percent is double q && Math.Abs(p - q) < 0.5
                        : setting.Candidates.Any(c => c.Value == now.Raw);
                    if (!same) { keep = SceneStore.ValueNode(setting.Feature, now.Percent, now.Raw); changed++; }
                }
                if (keep is not null) lines.Add(new(key, keep));
            }
            // A control changed by hand that the profile did not set (brightness in a profile without it) joins it.
            foreach (byte code in modifiedCodes)
            {
                if (scene.Settings.Any(s => s.Candidates.Any(c => c.Feature.Code == code))) continue;
                var control = source.AllControls.FirstOrDefault(c => c.Feature.Code == code);
                if (control is null || source.ShownValue(code) is not { } now) continue;
                var definition = control.Feature.Definition;
                lines.Add(new(definition.Name.ToLowerInvariant(), SceneStore.ValueNode(definition, now.Percent, now.Raw)));
                changed++;
            }
            if (changed == 0)
            {
                ForgetModified();
                MarkActiveProfile();
                Report($"{scene.Name} already has these values.", true);
                return;
            }
            SceneStore.Write(SceneStore.Upsert(json, scene.Name, scene.Name, scene.MonitorFilter, lines));
            ForgetModified();
            LoadScenes();
            Report($"{scene.Name} saved with the values of {source.Name}.", true);
        }
        catch (Exception error) when (error is FormatException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Report($"{scene.Name} could not be saved: {error.Message}", false);
        }
    }

    /// <summary>What each display shows now, as profile lines (modes and levels), so Custom can bring it back later.</summary>
    private Dictionary<string, List<SceneSetting>> CaptureCustom()
    {
        var snapshot = new Dictionary<string, List<SceneSetting>>();
        foreach (var monitor in Monitors.Where(m => m.IsControllable && m.IsLoaded))
        {
            var lines = CaptureDisplay(monitor);
            if (lines.Count > 0) snapshot[monitor.Key] = lines;
        }
        return snapshot;
    }

    /// <summary>What one display shows now, as profile lines.</summary>
    public List<SceneSetting> CaptureDisplay(MonitorViewModel monitor)
    {
        var lines = new List<SceneSetting>();
        foreach (var control in monitor.AllControls)
        {
            // Values locked by the current mode belong to that mode; restoring them elsewhere would be wrong.
            if (control is ReadOnlyControlViewModel || control.IsUnread || (!control.IsEnabled && control is not ToggleControlViewModel)) continue;
            if (control is SliderControlViewModel slider) lines.Add(new SceneSetting(control.Feature.Definition, slider.Value, null));
            else if (control.RawValue is uint raw) lines.Add(new SceneSetting(control.Feature.Definition, null, raw));
        }
        return lines;
    }

    /// <summary>Custom button: back to the settings the displays had before the last profile was applied.</summary>
    /// <param name="fade">Gradual return (linked programs); zero = at once.</param>
    public async Task RestoreCustomAsync(TimeSpan fade = default)
    {
        if (activeProfile == CustomName)
        {
            Report("Custom is already active: the displays show your own settings.", true);
            return;
        }
        var snapshot = customSnapshot;
        activeProfile = CustomName;
        customSnapshot = null;
        MarkActiveProfile();
        if (snapshot is null)
        {
            Report("Custom selected. Your previous settings were not kept (the app was restarted), so nothing was changed.", true);
            return;
        }
        Report("Restoring your Custom settings…", true);
        // Every display at the same time, each with its own saved settings.
        var work = snapshot
            .Select(entry => (Monitor: Monitors.FirstOrDefault(m => m.Key == entry.Key), Lines: entry.Value))
            .Where(entry => entry.Monitor is { IsControllable: true })
            .Select(async entry =>
            {
                var steps = await Task.Run(() => SceneApplier.Apply(new SceneProfile(CustomName, entry.Lines), [entry.Monitor!.Device], log, fade: fade > TimeSpan.Zero ? fade : SceneApplier.ProfileFade, hooks: HooksForProfile()));
                entry.Monitor!.ShowApplied(steps);
                return steps.Count(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch);
            });
        // One profile change at a time (a quick Theater then Custom used to write over each other): the last asked wins.
        int mine = Interlocked.Increment(ref applySeq);
        await applyGate.WaitAsync();
        try
        {
            if (mine != Volatile.Read(ref applySeq)) return;
            int failed = (await Task.WhenAll(work)).Sum();
            Report(failed == 0 ? "Custom: your settings are back." : $"Custom: your settings are back, {failed} could not be set.", failed == 0);
        }
        finally { applyGate.Release(); }
    }

    /// <summary>
    /// Custom edited on the Profiles tab: the values become the person's own settings on that display (Custom stays the
    /// active profile; nothing is saved to scenes.json).
    /// </summary>
    public async Task ApplyAsCustomAsync(SceneProfile settings, MonitorViewModel monitor)
    {
        activeProfile = CustomName;
        customSnapshot = null;
        MarkActiveProfile();
        Report($"Applying your Custom settings to {monitor.Name}…", true);
        int mine = Interlocked.Increment(ref applySeq);
        await applyGate.WaitAsync();
        try
        {
            if (mine != Volatile.Read(ref applySeq)) return;
            var steps = await Task.Run(() => SceneApplier.Apply(settings, [monitor.Device], log, fade: SceneApplier.ProfileFade, hooks: HooksForProfile()));
            int failed = steps.Count(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch);
            Report("Custom: " + Summary(steps), failed == 0);
            monitor.ShowApplied(steps);
        }
        finally { applyGate.Release(); }
    }

    /// <param name="fade">Gradual change of the levels (linked programs); zero = at once.</param>
    /// <param name="automatic">Set by a linked program (not chosen by hand).</param>
    public async Task ApplySceneAsync(SceneProfile scene, TimeSpan fade = default, bool automatic = false)
    {
        // Leaving Custom: remember the person's own settings so the Custom button can bring them back.
        if (activeProfile == CustomName) customSnapshot = CaptureCustom();
        activeProfile = scene.Name;
        ForgetModified();   // chosen again: its saved values come back
        MarkActiveProfile();
        var targets = Monitors.Where(m => m.IsControllable && scene.AppliesTo(m.Device)).ToArray();
        var devices = targets.Select(m => m.Device).ToArray();
        Report($"Applying profile {scene.Name}…", true);
        int mine = Interlocked.Increment(ref applySeq);
        await applyGate.WaitAsync();
        try
        {
            // A profile asked while another change was running (a quick double press): only the last one is applied, after
            // the running one finished, so the displays are never left half-way between two profiles.
            if (mine != Volatile.Read(ref applySeq)) return;
            var hooks = HooksForProfile();
            var steps = await Task.Run(() => SceneApplier.Apply(scene, devices, log, fade: fade > TimeSpan.Zero ? fade : SceneApplier.ProfileFade, hooks: hooks));
            int failed = steps.Count(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch);
            Report($"{scene.Name}: " + Summary(steps), failed == 0);
            foreach (var monitor in Monitors) monitor.ShowApplied(steps);
            // A FreeSync change makes the display sync again. On a Samsung Odyssey G5 (07/11/2026) the control opened before
            // it then rejected every later write (the second FreeSync change of a session always failed, even 90 s later; a
            // new session worked): looking the displays up again gives the next change a fresh control.
            bool resynced = steps.Any(s => (s.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed or SceneStepStatus.Mismatch)
                && SceneApplier.IsDisruptive(s.Setting));
            if (resynced && DeviceSource is null) await RefreshAsync();
        }
        finally { applyGate.Release(); }
    }

    /// <summary>
    /// "LG 29WK600: 2 changed, 7 already set, 1 failed · N5050 Laptop Screen: 1 changed". Lines a display has no control
    /// for (picture mode on a laptop) are not counted: the profile simply does not concern it.
    /// </summary>
    internal static string Summary(IReadOnlyList<SceneStep> steps)
    {
        var parts = new List<string>();
        foreach (var display in steps.GroupBy(s => s.Monitor))
        {
            int changed = display.Count(s => s.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed);
            int already = display.Count(s => s.Status == SceneStepStatus.AlreadySet);
            int notOffered = display.Count(s => s.Status == SceneStepStatus.Skipped && !SceneApplier.IsQuietSkip(s));
            var failed = display.Where(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch)
                .Select(s => s.Setting.Feature.Name).ToArray();
            var counts = new List<string>();
            if (changed > 0) counts.Add($"{changed} changed");
            if (already > 0) counts.Add($"{already} already set");
            if (notOffered > 0) counts.Add($"{notOffered} value{(notOffered == 1 ? "" : "s")} not offered");
            if (failed.Length > 0) counts.Add($"{failed.Length} failed ({string.Join(", ", failed)})");
            if (counts.Count == 0) continue;
            parts.Add($"{display.Key}: {string.Join(", ", counts)}");
        }
        return parts.Count == 0 ? "nothing to change" : string.Join(" · ", parts);
    }

    // ---- Brightness shortcuts (1.x behavior) ----

    /// <summary>The display brightness shortcuts act on: the selected one when it has brightness, else the first that does.</summary>
    /// <remarks>Uses what the panel already loaded, so the UI thread never waits for a capabilities read.</remarks>
    public MonitorViewModel? BrightnessTarget =>
        Selected is { } chosen && MayHaveBrightness(chosen)
            ? chosen
            : Monitors.FirstOrDefault(m => m.Brightness is not null) ?? Monitors.FirstOrDefault(MayHaveBrightness);

    private static bool MayHaveBrightness(MonitorViewModel m) => m.IsControllable && (m.Brightness is not null || !m.IsLoaded);
    private static bool MayHaveInputs(MonitorViewModel m) => m.IsControllable && (m.Inputs.Count > 0 || !m.IsLoaded);

    /// <summary>Key pressed: one point, or the next five-point mark on the third quick tap.</summary>
    public async void StepBrightness(int sign)
    {
        if (BrightnessTarget is not { } monitor) { Report("No display with brightness control.", false); return; }
        await monitor.EnsureLoadedAsync();
        if (monitor.Brightness is not { } slider) { Report($"{monitor.Name}: brightness could not be read.", false); return; }
        lastHoldStep = DateTime.UtcNow;
        slider.SetTarget(brightnessSteps.Tap((int)slider.Value, sign, DateTime.UtcNow));
    }

    /// <summary>Key held: next five-point mark after 300 ms, then ten-point marks.</summary>
    public void HoldBrightness(int sign, TimeSpan held)
    {
        if (held < BrightnessSteps.HoldFirst || DateTime.UtcNow - lastHoldStep < BrightnessSteps.HoldRepeat(held)) return;
        if (BrightnessTarget?.Brightness is not { } slider) return;
        lastHoldStep = DateTime.UtcNow;
        slider.SetTarget(BrightnessSteps.Hold((int)slider.Value, sign, held));
    }

    /// <summary>Ctrl+Alt+*: brightness shortcuts move to the next display.</summary>
    public void NextBrightnessTarget()
    {
        var candidates = Monitors.Where(MayHaveBrightness).ToList();
        if (candidates.Count == 0) { Report("No display with brightness control.", false); return; }
        int index = BrightnessTarget is { } current ? candidates.IndexOf(current) : -1;
        Selected = candidates[(index + 1) % candidates.Count];
        Report($"Brightness shortcuts now control {Selected!.Name}" + (candidates.Count == 1 ? " (the only one)." : "."), true);
    }

    // ---- Inputs (1.x behavior) ----

    /// <summary>The display Next input acts on: the selected one when it has inputs, else the first that does.</summary>
    public MonitorViewModel? InputTarget =>
        Selected is { } chosen && MayHaveInputs(chosen) ? chosen : Monitors.FirstOrDefault(MayHaveInputs);

    public async Task SwitchInputAsync(MonitorViewModel monitor, int code)
    {
        int? origin = monitor.CurrentInput ?? await monitor.ReadInputAsync();
        await SwitchAndRecordAsync(monitor, code, origin, null);
    }

    public async Task SwitchInputByKeyAsync(string monitorKey, int code)
    {
        var monitor = Monitors.FirstOrDefault(m => m.Key == monitorKey);
        if (monitor is null || !monitor.IsControllable)
        {
            Report("That shortcut belongs to a display that is not connected or not controllable now.", false);
            return;
        }
        await monitor.EnsureLoadedAsync();
        await SwitchInputAsync(monitor, code);
    }

    /// <summary>Ctrl+Alt+M: next marked input; pressed again within ten seconds, back to the previous one.</summary>
    private string? lastPointerKey;

    /// <summary>The controllable display under the mouse pointer (null on the taskbar or an unknown screen); remembers the last one.</summary>
    public MonitorViewModel? MonitorUnderPointer()
    {
        string? source = Services.PointerMonitor.GdiNameUnderPointer();
        var found = source is null ? null : Monitors.FirstOrDefault(m => m.IsControllable && string.Equals(m.Device.Identity.GdiName, source, StringComparison.OrdinalIgnoreCase));
        if (found is not null) lastPointerKey = found.Key;
        return found;
    }

    /// <summary>With several displays that have inputs, Next input acts on the one under the mouse (see ShortcutRouting).</summary>
    private MonitorViewModel? PointerInputTarget()
    {
        var candidates = Monitors.Where(MayHaveInputs).ToList();
        if (candidates.Count < 2) return null;
        string? key = Core.Hotkeys.ShortcutRouting.Choose(candidates.Select(m => m.Key).ToList(), MonitorUnderPointer()?.Key, lastPointerKey);
        return candidates.FirstOrDefault(m => m.Key == key);
    }

    /// <summary>An input shortcut several displays share (Ctrl+Alt+1): it switches the display under the mouse, as that display set it.</summary>
    public async Task SwitchInputByChordAsync(Core.Hotkeys.HotkeyChord chord)
    {
        var matches = Settings.InputShortcuts.Where(s => s.Chord == chord).ToList();
        var live = matches.Select(s => s.MonitorKey).Where(k => Monitors.Any(m => m.Key == k && m.IsControllable)).ToList();
        string? key = Core.Hotkeys.ShortcutRouting.Choose(live, MonitorUnderPointer()?.Key, lastPointerKey);
        if (key is null)
        {
            Report("That shortcut belongs to displays that are not connected or not controllable now.", false);
            return;
        }
        await SwitchInputByKeyAsync(key, matches.First(s => s.MonitorKey == key).InputCode);
    }

    public async Task NextInputAsync()
    {
        if ((PointerInputTarget() ?? InputTarget) is not { } monitor) { Report("No display with selectable inputs.", false); return; }
        await monitor.EnsureLoadedAsync();
        var last = history.Last(monitor.Key);
        int? current = last is not null && DateTime.UtcNow - last.AtUtc <= InputCycle.ReturnWindow
            ? null   // no need to read: the ten-second return wins
            : await monitor.ReadInputAsync();
        var decision = InputCycle.Decide(Settings.CycleFor(monitor.Key, monitor.Device.Inputs), current, last, DateTime.UtcNow);
        if (decision.Target is not int target)
        {
            Report($"{monitor.Name}: {decision.Message}", false);
            return;
        }
        string label = monitor.Device.DescribeInput(target);
        string what = decision.Reason switch
        {
            CycleReason.ReturnWithinWindow => $"back to {label}",
            CycleReason.ReturnToSavedOrigin => $"no answer from the display; trying to return to {label}",
            _ => $"Input {label}"
        };
        int? origin = decision.Reason == CycleReason.ReturnToSavedOrigin ? last?.Target : current ?? last?.Target;
        await SwitchAndRecordAsync(monitor, target, origin, what);
    }

    /// <summary>Tray menu: switch back to where the last change came from (survives restarts).</summary>
    public async Task ReturnToPreviousInputAsync()
    {
        if (InputTarget is not { } monitor || history.Last(monitor.Key) is not { } last)
        {
            Report("No previous input recorded.", false);
            return;
        }
        await monitor.EnsureLoadedAsync();
        await SwitchAndRecordAsync(monitor, last.Origin, last.Target, $"back to {monitor.Device.DescribeInput(last.Origin)}");
    }

    public bool CanReturnToPreviousInput => InputTarget is { } monitor && history.Last(monitor.Key) is not null;

    public string? PreviousInputLabel =>
        InputTarget is { } monitor && history.Last(monitor.Key) is { } last ? monitor.Device.DescribeInput(last.Origin) : null;

    private async Task SwitchAndRecordAsync(MonitorViewModel monitor, int code, int? origin, string? what)
    {
        // Recorded when the switch is sent, not when it is confirmed (the LG takes about five seconds), so pressing
        // Next input again meanwhile already counts as "within ten seconds" and goes back.
        var previous = history.Last(monitor.Key);
        bool recorded = origin is int from && from != code;
        if (recorded) history.Record(monitor.Key, new InputSwitch(origin!.Value, code, DateTime.UtcNow));
        if (!await monitor.SwitchToAsync(code, what))
        {
            if (recorded && previous is not null) history.Record(monitor.Key, previous);
            if (monitor.Device.ResendsInputOnReturn && previous is { } last && code == last.Origin)
                Report($"{monitor.Name}: this computer cannot control the monitor while it shows another input " +
                       "(Intel driver limit, same as 1.x). Switch back from the other computer or with the monitor's joystick.", false);
            return;
        }
        if (!recorded) return;
        InputHistoryChanged?.Invoke();
        if (monitor.Device.ResendsInputOnReturn)
        {
            returnWatches[monitor.Key] = new InputReturnWatch(origin!.Value, code, DateTime.UtcNow);
            returnTimer.Start();
        }
        else returnWatches.Remove(monitor.Key);
    }

    /// <summary>LG menu correction: see <see cref="InputReturnWatch"/>.</summary>
    private async Task PollReturnWatchesAsync()
    {
        if (polling) return;
        polling = true;
        try
        {
            foreach (var (key, watch) in returnWatches.ToArray())
            {
                var monitor = Monitors.FirstOrDefault(m => m.Key == key);
                if (monitor is null) { returnWatches.Remove(key); continue; }
                int? input = await Task.Run(() => monitor.Device.TryReadInputOnce());
                if (input is int seen) monitor.MarkCurrentInput(seen);
                switch (watch.Step(input, DateTime.UtcNow))
                {
                    case ReturnWatchStep.Resend:
                        returnWatches.Remove(key);
                        log.Write(LogLevel.Info, $"{monitor.Name}: back on {monitor.Device.DescribeInput(watch.Origin)}; sending it again to refresh the monitor menu.");
                        await monitor.SwitchToAsync(watch.Origin, $"menu corrected ({monitor.Device.DescribeInput(watch.Origin)} sent again)");
                        break;
                    case ReturnWatchStep.Stop:
                        returnWatches.Remove(key);
                        break;
                }
            }
            if (returnWatches.Count == 0) returnTimer.Stop();
        }
        finally { polling = false; }
    }

    // ---- IInputCoordinator: shortcuts and Next input marks per input button ----

    public string ShortcutFor(MonitorViewModel monitor, int code) =>
        Settings.InputShortcuts.FirstOrDefault(s => s.MonitorKey == monitor.Key && s.InputCode == code)?.Chord.ToString() ?? "";

    public bool IsInCycle(MonitorViewModel monitor, int code) =>
        !Settings.CycleInputs.TryGetValue(monitor.Key, out var marked) || marked.Contains(code);

    public void SetInCycle(MonitorViewModel monitor, int code, bool included)
    {
        var updated = Settings.WithCycleInput(monitor.Key, monitor.Device.Inputs, code, included);
        UpdateSettings(updated, shortcutsChanged: false);
        int count = Settings.CycleFor(monitor.Key, monitor.Device.Inputs).Count;
        Report(Settings.CycleInputs[monitor.Key].Count < 2
            ? $"{monitor.Name}: mark at least two inputs; until then Next input uses all {count}."
            : $"{monitor.Name}: Next input visits {string.Join(", ", Settings.CycleInputs[monitor.Key].Select(monitor.Device.DescribeInput))}.", true);
        foreach (var input in monitor.Inputs) input.Refresh();
    }

    /// <summary>First display with inputs and no input shortcuts anywhere: Ctrl+Alt+1..9, as 1.x did.</summary>
    public void InputsLoaded(MonitorViewModel monitor)
    {
        if (Settings.WithDefaultInputShortcuts(monitor.Key, monitor.Device.Inputs) is { } updated)
        {
            UpdateSettings(updated, shortcutsChanged: true);
            Report($"{monitor.Name}: input shortcuts " +
                string.Join(", ", updated.InputShortcuts.Select(s => $"{s.Chord} = {monitor.Device.DescribeInput(s.InputCode)}")) + ".", true);
        }
        InputHistoryChanged?.Invoke();
    }

    public int? HomeInput(MonitorViewModel monitor) =>
        Settings.HomeInputs.TryGetValue(monitor.Key, out int code) ? code : null;

    public void ForgetHomeInput(MonitorViewModel monitor)
    {
        if (!Settings.HomeInputs.ContainsKey(monitor.Key)) return;
        UpdateSettings(Settings.WithoutHomeInput(monitor.Key), shortcutsChanged: false);
        log.Write(LogLevel.Info, $"{monitor.Name}: the input learned for this computer was the other computer's; forgotten.");
    }

    public void LearnHomeInput(MonitorViewModel monitor, int code)
    {
        if (Settings.HomeInputs.ContainsKey(monitor.Key)) return;
        UpdateSettings(Settings.WithHomeInput(monitor.Key, code), shortcutsChanged: false);
        log.Write(LogLevel.Info, $"{monitor.Name}: {monitor.Device.DescribeInput(code)} is the input this computer uses (learned; homeInputs in settings.json).");
    }

    public string DescribeInputShortcut(InputShortcut shortcut)
    {
        var monitor = Monitors.FirstOrDefault(m => m.Key == shortcut.MonitorKey);
        return monitor is null ? $"input {shortcut.InputCode:X2}" : $"{monitor.Name} {monitor.Device.DescribeInput(shortcut.InputCode)}";
    }

    /// <summary>Windows resets the gamma ramp after sleep and when a display is reconnected: set saved tints again.</summary>
    public void ReapplySoftwareColor()
    {
        foreach (var monitor in Monitors) monitor.SoftwareColor?.Reapply();
    }

    public void Shutdown()
    {
        learn?.CloseNow();
        Apps.Stop();
        Placer.Stop();
        foreach (var monitor in Monitors) monitor.SoftwareColor?.Release();
        returnTimer.Stop();
        foreach (var monitor in Monitors) monitor.Dispose();
        Monitors.Clear();
    }
}
