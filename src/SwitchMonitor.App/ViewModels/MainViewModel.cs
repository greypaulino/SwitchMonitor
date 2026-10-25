using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Inputs;
using SwitchMonitor.Core.Scenes;
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
    }

    public SceneProfile? Scene { get; }
    public bool IsCustom => Scene is null;
    public string Name => Scene?.Name ?? MainViewModel.CustomName;
    public string Hotkey => Scene?.Hotkey?.ToString() ?? "";
    public string Summary => Scene is null
        ? "Your own settings. Changing any setting by hand selects Custom; after applying a profile, click Custom to get your settings back."
        : string.Join(" · ", Scene.Settings.Select(s => s.ToString()));

    /// <summary>The profile the displays are in now (Custom until one is applied).</summary>
    public bool IsActive { get => isActive; set => Set(ref isActive, value); }

    public ICommand ApplyCommand { get; }
}

public enum AppTab { Displays, Advanced, Profiles, Shortcuts }

public sealed class MainViewModel : ObservableObject, IInputCoordinator
{
    private readonly ICoreLog log;
    private readonly InputHistory history;
    private readonly Dictionary<string, InputReturnWatch> returnWatches = [];
    private readonly DispatcherTimer returnTimer;
    private readonly BrightnessSteps brightnessSteps;
    private MonitorViewModel? selected;
    private string status = "Looking for displays…";
    private bool statusIsError;
    private bool isBusy;
    private string? shortcutWarning;
    private bool polling;
    private DateTime lastHoldStep;
    private string activeProfile = CustomName;
    private Dictionary<string, List<SceneSetting>>? customSnapshot;

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
        ResetAllCommand = new RelayCommand(() => _ = Selected?.ResetAsync(null) ?? Task.CompletedTask);
        RetryShortcutsCommand = new RelayCommand(() => ShortcutsChanged?.Invoke());
        OpenShortcutsCommand = new RelayCommand(() => IsShortcutsTab = true);
        returnTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        returnTimer.Tick += async (_, _) => await PollReturnWatchesAsync();
    }

    public AppSettings Settings { get; private set; }
    public ObservableCollection<MonitorViewModel> Monitors { get; } = [];
    public ObservableCollection<SceneViewModel> Scenes { get; } = [];
    public IReadOnlyList<SceneProfile> SceneProfiles { get; private set; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand ReloadValuesCommand { get; }
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
            Raise();
            Raise(nameof(IsDisplaysTab));
            Raise(nameof(IsAdvancedTab));
            Raise(nameof(IsProfilesTab));
            Raise(nameof(IsShortcutsTab));
            Raise(nameof(ShowsMonitorList));
        }
    }

    public bool IsDisplaysTab { get => tab == AppTab.Displays; set { if (value) Tab = AppTab.Displays; } }
    public bool IsAdvancedTab { get => tab == AppTab.Advanced; set { if (value) Tab = AppTab.Advanced; } }
    public bool IsProfilesTab { get => tab == AppTab.Profiles; set { if (value) Tab = AppTab.Profiles; } }
    public bool IsShortcutsTab { get => tab == AppTab.Shortcuts; set { if (value) Tab = AppTab.Shortcuts; } }

    /// <summary>The display list on the left serves the Displays and Advanced tabs.</summary>
    public bool ShowsMonitorList => tab is AppTab.Displays or AppTab.Advanced;

    /// <summary>"Advanced" switch: shows the Advanced tab and the advanced rows of the profile editor (saved in settings.json).</summary>
    public bool ShowAdvanced
    {
        get => Settings.ShowAdvanced;
        set
        {
            if (Settings.ShowAdvanced == value) return;
            UpdateSettings(Settings with { ShowAdvanced = value }, shortcutsChanged: false);
            Raise();
            if (!value && tab == AppTab.Advanced) Tab = AppTab.Displays;
            Report(value ? "Advanced settings shown: red/green/blue and black levels are on the Advanced tab." : "Advanced settings hidden.", true);
        }
    }

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
        var editor = new ShortcutEditorViewModel(Settings, DisplayInputsForEditor(), SceneProfiles, IsShortcutFree);
        editor.Saved += SaveShortcuts;
        editor.CloseRequested += () => { if (tab == AppTab.Shortcuts) ResetShortcutEditor(); };
        ShortcutEditor = editor;
    }

    public MonitorViewModel? Selected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value) && value is not null)
                _ = value.EnsureLoadedAsync();
        }
    }

    public string Status { get => status; private set => Set(ref status, value); }
    public bool StatusIsError { get => statusIsError; private set => Set(ref statusIsError, value); }
    public bool IsBusy { get => isBusy; private set => Set(ref isBusy, value); }

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
        log.Write(ok ? LogLevel.Info : LogLevel.Warning, message);
    }

    public void UpdateSettings(AppSettings updated, bool shortcutsChanged)
    {
        Settings = updated;
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

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Report("Looking for displays…", true);
        try
        {
            string? keep = Selected?.Device.Key;
            foreach (var old in Monitors) old.Dispose();
            Monitors.Clear();
            Selected = null;
            returnWatches.Clear();

            var devices = await Task.Run(() => MonitorEnumerator.Enumerate(CoreOptions.FromDataDirectory(), log: log));
            foreach (var device in devices.OrderByDescending(d => d.IsControllable).ThenByDescending(d => d.Identity.IsPrimary))
                Monitors.Add(new MonitorViewModel(device, Report, this));

            Selected = Monitors.FirstOrDefault(m => m.Device.Key == keep) ?? Monitors.FirstOrDefault(m => m.IsControllable);
            Report(Monitors.Count == 0 ? "No displays found." : $"{Monitors.Count(m => m.IsControllable)} of {Monitors.Count} display(s) can be controlled.", Monitors.Count > 0);
            LoadScenes();
            // Load every controllable display in the background so shortcuts respond at once.
            foreach (var monitor in Monitors.Where(m => m.IsControllable)) _ = monitor.EnsureLoadedAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Report($"Could not list displays — {error.Message}", false);
        }
        finally { IsBusy = false; }
    }

    /// <summary>What the shortcut editor needs to list per-input rows.</summary>
    public IReadOnlyList<DisplayInputs> DisplayInputsForEditor() =>
        Monitors.Where(m => m.Inputs.Count > 0)
            .Select(m => new DisplayInputs(m.Key, m.Name, m.Inputs.Select(i => (i.Code, i.Label)).ToArray()))
            .ToArray();

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
        ShortcutsChanged?.Invoke();
    }

    private void MarkActiveProfile()
    {
        foreach (var scene in Scenes) scene.IsActive = string.Equals(scene.Name, activeProfile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Changing a setting by hand makes Custom the active profile again (the saved settings before it are dropped).</summary>
    public void SettingChangedByHand(MonitorViewModel monitor)
    {
        if (activeProfile == CustomName) return;
        activeProfile = CustomName;
        customSnapshot = null;
        MarkActiveProfile();
    }

    /// <summary>What each display shows now, as profile lines (modes and levels), so Custom can bring it back later.</summary>
    private Dictionary<string, List<SceneSetting>> CaptureCustom()
    {
        var snapshot = new Dictionary<string, List<SceneSetting>>();
        foreach (var monitor in Monitors.Where(m => m.IsControllable && m.IsLoaded))
        {
            var lines = new List<SceneSetting>();
            foreach (var control in monitor.AllControls)
            {
                // Values locked by the current mode belong to that mode; restoring them elsewhere would be wrong.
                if (control is ReadOnlyControlViewModel || (!control.IsEnabled && control is not ToggleControlViewModel)) continue;
                if (control is SliderControlViewModel slider) lines.Add(new SceneSetting(control.Feature.Definition, slider.Value, null));
                else if (control.RawValue is uint raw) lines.Add(new SceneSetting(control.Feature.Definition, null, raw));
            }
            if (lines.Count > 0) snapshot[monitor.Key] = lines;
        }
        return snapshot;
    }

    /// <summary>Custom button: back to the settings the displays had before the last profile was applied.</summary>
    public async Task RestoreCustomAsync()
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
        int failed = 0;
        foreach (var (key, lines) in snapshot)
        {
            var monitor = Monitors.FirstOrDefault(m => m.Key == key);
            if (monitor is null || !monitor.IsControllable) continue;
            var steps = await Task.Run(() => SceneApplier.Apply(new SceneProfile(CustomName, lines), [monitor.Device], log));
            failed += steps.Count(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch);
            monitor.ShowApplied(steps);
        }
        Report(failed == 0 ? "Custom: your settings are back." : $"Custom: your settings are back, {failed} could not be set.", failed == 0);
    }

    public async Task ApplySceneAsync(SceneProfile scene)
    {
        // Leaving Custom: remember the person's own settings so the Custom button can bring them back.
        if (activeProfile == CustomName) customSnapshot = CaptureCustom();
        activeProfile = scene.Name;
        MarkActiveProfile();
        var devices = Monitors.Where(m => m.IsControllable).Select(m => m.Device).ToArray();
        Report($"Applying profile {scene.Name}…", true);
        var steps = await Task.Run(() => SceneApplier.Apply(scene, devices, log));
        int applied = steps.Count(s => s.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed);
        int already = steps.Count(s => s.Status == SceneStepStatus.AlreadySet);
        int skipped = steps.Count(s => s.Status == SceneStepStatus.Skipped);
        int failed = steps.Count(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch);
        Report($"{scene.Name}: {applied} changed" + (already > 0 ? $", {already} already set" : "") +
               (skipped > 0 ? $", {skipped} not available on a display" : "") +
               (failed > 0 ? $", {failed} failed" : ""), failed == 0);
        foreach (var monitor in Monitors) monitor.ShowApplied(steps);
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
    public async Task NextInputAsync()
    {
        if (InputTarget is not { } monitor) { Report("No display with selectable inputs.", false); return; }
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

    public string DescribeInputShortcut(InputShortcut shortcut)
    {
        var monitor = Monitors.FirstOrDefault(m => m.Key == shortcut.MonitorKey);
        return monitor is null ? $"input {shortcut.InputCode:X2}" : $"{monitor.Name} {monitor.Device.DescribeInput(shortcut.InputCode)}";
    }

    public void Shutdown()
    {
        returnTimer.Stop();
        foreach (var monitor in Monitors) monitor.Dispose();
        Monitors.Clear();
    }
}
