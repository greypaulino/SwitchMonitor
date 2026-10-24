using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Inputs;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App.ViewModels;

public sealed class SceneViewModel(SceneProfile scene, MainViewModel main) : ObservableObject
{
    public SceneProfile Scene { get; } = scene;
    public string Name => Scene.Name;
    public string Hotkey => Scene.Hotkey?.ToString() ?? "";
    public string Summary => string.Join(" · ", Scene.Settings.Select(s => s.ToString()));
    public ICommand ApplyCommand { get; } = new RelayCommand(() => _ = main.ApplySceneAsync(scene));
}

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

    // ---- Views: Displays | Shortcuts ----

    private bool shortcutsTab;
    private ShortcutEditorViewModel? shortcutEditor;

    /// <summary>Tells whether a shortcut is free (set by the app, which owns the hotkey service).</summary>
    public Func<Core.Hotkeys.HotkeyChord, bool> IsShortcutFree { get; set; } = _ => true;

    /// <summary>Raised with true when global shortcuts must pause (Shortcuts tab shown) and false when they resume.</summary>
    public event Action<bool>? ShortcutsPaused;

    public bool IsDisplaysTab
    {
        get => !shortcutsTab;
        set { if (value) IsShortcutsTab = false; }
    }

    public bool IsShortcutsTab
    {
        get => shortcutsTab;
        set
        {
            if (shortcutsTab == value) return;
            shortcutsTab = value;
            if (value)
            {
                ShortcutsPaused?.Invoke(true);   // before the editor tests which shortcuts are free
                ResetShortcutEditor();
            }
            else
            {
                ShortcutEditor?.CancelCapture();
                ShortcutsPaused?.Invoke(false);
            }
            Raise();
            Raise(nameof(IsDisplaysTab));
        }
    }

    /// <summary>The editor shown on the Shortcuts tab; replaced after Save or Cancel.</summary>
    public ShortcutEditorViewModel? ShortcutEditor { get => shortcutEditor; private set => Set(ref shortcutEditor, value); }

    private void ResetShortcutEditor()
    {
        var editor = new ShortcutEditorViewModel(Settings, DisplayInputsForEditor(), SceneProfiles, IsShortcutFree);
        editor.Saved += SaveShortcuts;
        editor.CloseRequested += () => { if (shortcutsTab) ResetShortcutEditor(); };
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
        try
        {
            SceneProfiles = SceneStore.LoadOrCreate();
            foreach (var scene in SceneProfiles) Scenes.Add(new SceneViewModel(scene, this));
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            SceneProfiles = [];
            Report($"Profiles (scenes.json): {error.Message}", false);
        }
        ShortcutsChanged?.Invoke();
    }

    public async Task ApplySceneAsync(SceneProfile scene)
    {
        var devices = Monitors.Where(m => m.IsControllable).Select(m => m.Device).ToArray();
        Report($"Applying profile {scene.Name}…", true);
        var steps = await Task.Run(() => SceneApplier.Apply(scene, devices, log));
        int applied = steps.Count(s => s.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed);
        int skipped = steps.Count(s => s.Status == SceneStepStatus.Skipped);
        int failed = steps.Count(s => s.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch);
        Report($"{scene.Name}: {applied} applied" + (skipped > 0 ? $", {skipped} not available on this display" : "") +
               (failed > 0 ? $", {failed} failed" : ""), failed == 0);
        foreach (var monitor in Monitors)
        {
            await monitor.ReloadValuesAsync();
            monitor.RefreshSoon();   // and once more when late mode values have settled
        }
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
