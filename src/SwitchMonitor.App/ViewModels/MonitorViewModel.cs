using System.Collections.ObjectModel;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App.ViewModels;

/// <summary>One display in the list. Every monitor command runs off the UI thread, one at a time per display.</summary>
public sealed class MonitorViewModel : ObservableObject
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Action<string, bool> report;
    private readonly IInputCoordinator inputs;
    private bool loaded;
    private Task? loading;
    private bool isLoading;
    private string? loadError;

    public MonitorViewModel(MonitorDevice device, Action<string, bool> report, IInputCoordinator inputs)
    {
        Device = device;
        this.report = report;
        this.inputs = inputs;
    }

    public string Key => Device.Key;

    /// <summary>True once the panel has been read from the monitor.</summary>
    public bool IsLoaded => loaded && loading is { IsCompleted: true };

    /// <summary>The input last read or set, when known.</summary>
    public int? CurrentInput => Inputs.FirstOrDefault(i => i.IsCurrent)?.Code;

    public MonitorDevice Device { get; }
    public string Name => Device.Name;
    public bool IsActive => Device.IsActive;
    public bool IsControllable => Device.IsControllable;

    private string? panel;

    /// <summary>For built-in screens: the panel from its EDID ("CMO XM5XGN156B6 · 15.6″ · 1366×768").</summary>
    public string? Panel
    {
        get => panel;
        set { if (Set(ref panel, value)) Raise(nameof(Subtitle)); }
    }

    public string Subtitle => !Device.IsActive ? "Off — Windows is not using this display"
        : !Device.IsControllable ? "No control path on this computer"
        : string.Join(" · ", new[]
          {
              Device.Identity.IsPrimary ? "Main display" : null,
              Device.Identity.IsBuiltIn ? "Built-in" : Device.Identity.IsPrimary ? null : "External",
              Device.Identity.IsBuiltIn ? Panel : null
          }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Tooltip of the display: full name and how the app talks to it (ddc-ci, intel-cui, amd-adl, wmi).</summary>
    public string Details => Device.IsControllable ? $"{Name}\nControlled through {Device.TransportId}" : Name;

    public ObservableCollection<InputViewModel> Inputs { get; } = [];
    public bool HasInputs => Inputs.Count > 0;
    public ObservableCollection<ControlViewModel> Controls { get; } = [];

    /// <summary>Red/green/blue and black levels: shown on the Advanced tab only (see <see cref="FeatureCatalog.IsAdvanced"/>).</summary>
    public ObservableCollection<ControlViewModel> AdvancedControls { get; } = [];

    public bool HasAdvanced => AdvancedControls.Count > 0;

    private SoftwareColorViewModel? softwareColor;

    /// <summary>Color made by the graphics card (Advanced tab); null for displays Windows is not using.</summary>
    public SoftwareColorViewModel? SoftwareColor { get => softwareColor; set { if (Set(ref softwareColor, value)) { Raise(nameof(HasSoftwareColor)); Raise(nameof(HasColorTuning)); Raise(nameof(NoAdvancedText)); } } }
    public bool HasSoftwareColor => SoftwareColor is not null;

    /// <summary>The "Color fine-tuning" section on the Displays tab has something to show.</summary>
    public bool HasColorTuning => HasAdvanced || HasSoftwareColor;
    public string NoAdvancedText => HasSoftwareColor
        ? "This display has no color controls of its own; the software color below works on it."
        : "This display has no color controls the app can change.";

    /// <summary>A change made by hand outside the monitor's own controls (software color): Custom becomes active.</summary>
    public void SettingChangedByHand() => inputs.SettingChangedByHand(this);
    public bool HasNoAdvanced => loaded && !isLoading && LoadError is null && AdvancedControls.Count == 0;

    private string? advancedHint;

    /// <summary>Why some advanced controls are hidden right now (LG red/green/blue outside the Custom picture modes).</summary>
    public string? AdvancedHint { get => advancedHint; private set => Set(ref advancedHint, value); }

    /// <summary>Every control of the panel, both tabs, plus mute (shown as the speaker of the volume slider).</summary>
    public IEnumerable<ControlViewModel> AllControls =>
        Controls.Concat(AdvancedControls)
            .Concat(Controls.OfType<SliderControlViewModel>().Select(s => (ControlViewModel?)s.Mute).OfType<ControlViewModel>());

    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public string? LoadError { get => loadError; private set => Set(ref loadError, value); }

    /// <summary>Builds the panel the first time the monitor is selected (reading every control takes a few seconds on GPU transports).</summary>
    public Task EnsureLoadedAsync()
    {
        if (loaded || !Device.IsControllable) return loading ?? Task.CompletedTask;
        loaded = true;
        return loading = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var (inputs, currentInput, controls, unreadable) = await RunExclusiveAsync(() =>
            {
                // Only what can be changed: power is never one click away, and read-only or unmapped vendor codes
                // (VCP 0x4D, gamma on the LG...) belong to the learn tools, not the everyday panel.
                var features = Device.Features.Where(f => f.Definition.Risk == FeatureRisk.Safe).ToArray();
                var built = new List<ControlViewModel>();
                var failed = new List<string>();
                foreach (var control in features.Select(Create))
                {
                    // A control that cannot be read (e.g. LG 0xFD) is left out of the panel rather than shown wrong,
                    // but standard controls are reported so a broken path (WMI, DDC) is visible.
                    try { control.Load(Device); built.Add(control); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        if (FeatureCatalog.Find(control.Feature.Code) is null) continue;
                        // A standard slider (brightness above all) stays on the panel, dimmed, and is read again
                        // shortly: on the laptop the first read after sign-in can fail and works seconds later.
                        if (control is SliderControlViewModel) { control.IsUnread = true; built.Add(control); }
                        else notShown.Add(control.Name);
                        failed.Add($"{control.Name} ({error.Message})");
                    }
                }
                int? current = null;
                if (Device.Inputs.Count > 0)
                    try { current = (int)Device.Read(VcpCode.InputSource).Current; }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                var inputList = Device.Inputs.Select(code => (code, Device.DescribeInput(code))).ToArray();
                return (inputList, current, built, failed);
            });

            foreach (var (code, label) in inputs)
                Inputs.Add(new InputViewModel(this, code, label, this.inputs) { IsCurrent = code == currentInput });
            this.inputs.InputsLoaded(this);
            foreach (var control in controls)
            {
                control.Publish();
                (FeatureCatalog.IsAdvanced(control.Feature.Code) ? AdvancedControls : Controls).Add(control);
            }
            Raise(nameof(HasInputs));
            Raise(nameof(HasAdvanced));
            Raise(nameof(HasColorTuning));
            LinkMuteAndVolume();
            if (unreadable.Count > 0)
            {
                bool retrying = controls.Any(c => c.IsUnread);
                LoadError = (retrying ? "Could not be read yet, trying again: " : "Could not be read, so not shown: ") +
                            string.Join("; ", unreadable);
                report($"{Name}: {LoadError}", false);
                if (retrying) _ = RetryUnreadAsync();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LoadError = error.Message;
            loaded = false;
        }
        finally
        {
            IsLoading = false;
            Raise(nameof(HasNoAdvanced));
        }
    }

    /// <summary>Re-reads every control (after a scene or a change made on the monitor's own menu).</summary>
    public async Task ReloadValuesAsync()
    {
        if (!loaded) return;
        var controls = AllControls.ToArray();
        var readable = await RunExclusiveAsync(() =>
        {
            var read = new List<ControlViewModel>();
            foreach (var control in controls)
                try { control.Load(Device); read.Add(control); } catch (Exception error) when (error is not OutOfMemoryException) { }
            return read;
        });
        foreach (var control in controls) if (!control.IsUnread || readable.Contains(control)) control.Publish();
        foreach (var control in readable) control.IsUnread = false;
        ClearLoadErrorWhenAllRead();
        UpdateLocks();
    }

    private bool retrying;
    /// <summary>Standard controls left out of the panel because they could not be read (not retried).</summary>
    private readonly List<string> notShown = [];

    /// <summary>
    /// Reads the controls that could not be read at load time again, a few times with growing pauses
    /// (2, 4, 8, 16, 30 s). Also run by the retry link and by brightness shortcuts while the value is unknown.
    /// </summary>
    public async Task RetryUnreadAsync()
    {
        if (retrying) return;
        retrying = true;
        try
        {
            foreach (int seconds in new[] { 0, 2, 4, 8, 16, 30 })
            {
                if (seconds > 0) await Task.Delay(TimeSpan.FromSeconds(seconds));
                var unread = AllControls.Where(c => c.IsUnread).ToArray();
                if (unread.Length == 0) break;
                var read = await RunExclusiveAsync(() =>
                {
                    var ok = new List<ControlViewModel>();
                    foreach (var control in unread)
                        try { control.Load(Device); ok.Add(control); } catch (Exception error) when (error is not OutOfMemoryException) { }
                    return ok;
                });
                foreach (var control in read)
                {
                    control.Publish();
                    control.IsUnread = false;
                    report($"{Name}: {control.Name} read after a retry.", true);
                }
                if (read.Count > 0) { UpdateLocks(); ClearLoadErrorWhenAllRead(); }
            }
            if (AllControls.Any(c => c.IsUnread))
                LoadError = "Could not be read: " + string.Join(", ", AllControls.Where(c => c.IsUnread).Select(c => c.Name)) +
                            ". Use Retry next to it.";
        }
        finally { retrying = false; }
    }

    private void ClearLoadErrorWhenAllRead()
    {
        if (LoadError is null || !loaded || AllControls.Any(c => c.IsUnread) ||
            !LoadError.StartsWith("Could not be read", StringComparison.Ordinal)) return;
        LoadError = notShown.Count > 0 ? "Could not be read, so not shown: " + string.Join(", ", notShown) : null;
    }

    private ControlViewModel Create(MonitorFeature feature)
    {
        if (feature.Definition.Risk is FeatureRisk.Unmapped or FeatureRisk.ReadOnly) return new ReadOnlyControlViewModel(this, feature);
        if (feature.Kind == FeatureKind.Continuous)
            return new SliderControlViewModel(this, feature)
            {
                ResetGroup = MonitorDevice.GroupOf(feature.Code) is { } group && Device.CanRestore(group) ? group : null,
                ResetValue = feature.Definition.DefaultValue
            };
        if (feature.Definition.OnOffValues(feature.AllowedValues.ToArray()) is { } pair)
            return new ToggleControlViewModel(this, feature, pair.On, pair.Off);
        return new ChoiceControlViewModel(this, feature);
    }

    /// <summary>
    /// Mute becomes the speaker icon of the volume slider (its own row disappears), and every control listens for the
    /// changes that lock or unlock others.
    /// </summary>
    private void LinkMuteAndVolume()
    {
        var mute = Controls.OfType<ToggleControlViewModel>().FirstOrDefault(c => c.Feature.Code == VcpCode.AudioMute);
        var volume = Controls.OfType<SliderControlViewModel>().FirstOrDefault(c => c.Feature.Code == VcpCode.AudioVolume);
        if (mute is not null && volume is not null)
        {
            volume.Mute = mute;
            Controls.Remove(mute);
            mute.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ToggleControlViewModel.IsOn)) UpdateLocks(); };
        }
        foreach (var control in Controls.Concat(AdvancedControls))
            control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ChoiceControlViewModel.Selected) or nameof(ToggleControlViewModel.IsOn)) UpdateLocks();
            };
        UpdateLocks();
    }

    /// <summary>
    /// Dims volume while muted and controls whose quirk rules are not met in the current mode (LG sharpness outside the
    /// Custom picture modes); quirk controls marked "whenLocked": "hide" disappear instead (LG red/green/blue).
    /// </summary>
    private void UpdateLocks()
    {
        var all = Controls.Concat(AdvancedControls).ToList();
        foreach (var control in all)
        {
            string? reason = null;
            bool modeRule = false;
            foreach (var (code, values) in control.Feature.Definition.EnabledWhen)
            {
                var source = all.FirstOrDefault(c => c.Feature.Code == code);
                if (source?.RawValue is uint raw && !values.Contains(raw))
                {
                    string allowed = string.Join(" or ", values.Select(source.Feature.Definition.DescribeValue));
                    reason = $"Adjustable only when {source.Name} is {allowed}.";
                    modeRule = true;
                    break;
                }
            }
            if (reason is null && control is SliderControlViewModel { Mute.IsOn: true }) reason = "Muted: click the speaker to unmute.";
            control.SetLock(reason, hide: modeRule && control.Feature.Definition.HideWhenLocked);
        }
        AdvancedHint = AdvancedControls.FirstOrDefault(c => !c.IsShown)?.LockReason is { } why
            ? $"Red, green, blue and black levels are hidden. {why}"
            : null;
    }

    /// <summary>
    /// Factory values for the current mode: one control (the others in its group are put back) or, with null,
    /// brightness, contrast and colors together.
    /// </summary>
    public async Task ResetAsync(ControlViewModel? control)
    {
        DefaultsGroup[] groups = control is null
            ? new[] { DefaultsGroup.LuminanceContrast, DefaultsGroup.Color }.Where(Device.CanRestore).ToArray()
            : control.ResetGroup is { } one ? new[] { one } : [];
        // Controls without a factory command but with a known default (LG black levels): written directly.
        byte[] direct = control is null
            ? Controls.Concat(AdvancedControls).Where(c => c.ResetGroup is null && c.ResetValue is not null && c.IsEnabled)
                .Select(c => c.Feature.Code).ToArray()
            : control.ResetGroup is null && control.ResetValue is not null ? [control.Feature.Code] : [];
        if (groups.Length == 0 && direct.Length == 0) return;
        string what = control is null ? "factory brightness, contrast and colors" : $"{control.Name} factory value";
        inputs.SettingChangedByHand(this);
        try
        {
            await RunExclusiveAsync(() =>
            {
                foreach (var group in groups) Device.RestoreDefaults(group, control?.Feature.Code);
                foreach (byte code in direct) Device.RestoreDefaultValue(code);
                return true;
            });
            report($"{Name}: {what} restored for the current mode.", true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            report($"{Name}: {what} could not be restored — {error.Message}", false);
        }
        await ReloadValuesAsync();
    }

    public bool CanResetAll => Device.IsControllable &&
        (Device.CanRestore(DefaultsGroup.LuminanceContrast) || Device.CanRestore(DefaultsGroup.Color));

    private CancellationTokenSource? refreshSoon;

    /// <summary>
    /// Re-reads every control after a mode, preset or profile change: right away, and again two seconds later because
    /// the monitor loads some of that mode's values a moment later.
    /// </summary>
    public void RefreshSoon()
    {
        refreshSoon?.Cancel();
        var cts = refreshSoon = new CancellationTokenSource();
        _ = RefreshLaterAsync(cts.Token);
    }

    private async Task RefreshLaterAsync(CancellationToken token)
    {
        // A quick read shows most new values at once; a second one catches what the monitor loads later.
        foreach (var wait in new[] { TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2) })
        {
            try { await Task.Delay(wait, token); }
            catch (TaskCanceledException) { return; }
            await ReloadValuesAsync();
        }
    }

    /// <summary>Sends an input switch. History and the LG menu correction are handled by <see cref="IInputCoordinator"/>.</summary>
    public async Task<bool> SwitchToAsync(int code, string? what = null)
    {
        string label = Device.DescribeInput(code);
        bool ok = await RunAsync(what ?? $"Input {label}", device => device.SetInput(code));
        if (ok) MarkCurrentInput(code);
        return ok;
    }

    /// <summary>Reads the current input once (no retries). Null when the display does not answer.</summary>
    public async Task<int?> ReadInputAsync()
    {
        if (!Device.IsControllable || Device.Inputs.Count == 0) return null;
        int? current = await RunExclusiveAsync(() => Device.TryReadInputOnce());
        if (current is int code) MarkCurrentInput(code);
        return current;
    }

    public void MarkCurrentInput(int code)
    {
        foreach (var input in Inputs) input.IsCurrent = input.Code == code;
    }

    /// <summary>A write the person made on the panel (slider, list, switch, reset): Custom becomes the active profile.</summary>
    public Task<bool> RunByHandAsync(string what, Func<MonitorDevice, WriteOutcome> action)
    {
        inputs.SettingChangedByHand(this);
        return RunAsync(what, action);
    }

    /// <summary>Runs one write and reports the outcome in the status bar. Returns false when it failed.</summary>
    public async Task<bool> RunAsync(string what, Func<MonitorDevice, WriteOutcome> action)
    {
        try
        {
            var outcome = await RunExclusiveAsync(() => action(Device));
            string message = outcome switch
            {
                WriteOutcome.Verified => $"{Name}: {what}",
                WriteOutcome.Applied => $"{Name}: {what} (sent; this monitor does not report it back)",
                WriteOutcome.Unverified => $"{Name}: {what} (sent; the monitor did not confirm)",
                _ => $"{Name}: {what} — the monitor kept another value"
            };
            report(message, outcome != WriteOutcome.Mismatch);
            return outcome != WriteOutcome.Mismatch;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            report($"{Name}: {what} failed — {error.Message}", false);
            return false;
        }
    }

    /// <summary>
    /// After a profile: shows what was applied (or was already there) at once. Only when a mode or preset really
    /// changed is the monitor read again, because the new mode brings its own values for the other controls.
    /// </summary>
    public void ShowApplied(IEnumerable<Core.Scenes.SceneStep> steps)
    {
        bool modeChanged = false;
        foreach (var step in steps.Where(s => s.Monitor == Name))
        {
            if (step.Status is not (Core.Scenes.SceneStepStatus.Applied or Core.Scenes.SceneStepStatus.AlreadySet
                or Core.Scenes.SceneStepStatus.Unconfirmed)) continue;
            var control = AllControls.FirstOrDefault(c => c.Feature.Code == step.Setting.Feature.Code);
            control?.Show(step.Setting.Percent, step.Setting.Value);
            if (step.Setting.Percent is null && step.Status != Core.Scenes.SceneStepStatus.AlreadySet) modeChanged = true;
        }
        UpdateLocks();
        if (modeChanged) RefreshSoon();
    }

    /// <summary>
    /// The value the panel shows now for a control, for "capture current values" in the profile editor:
    /// a percentage for sliders, a raw value otherwise. Null when the control is not on the panel.
    /// </summary>
    public (double? Percent, uint? Raw)? ShownValue(byte code)
    {
        var control = AllControls.FirstOrDefault(c => c.Feature.Code == code);
        return control switch
        {
            SliderControlViewModel { IsUnread: true } => null,
            SliderControlViewModel slider => (slider.Value, null),
            ToggleControlViewModel or ChoiceControlViewModel when control.RawValue is uint raw => (null, raw),
            _ => null
        };
    }

    public SliderControlViewModel? Brightness =>
        Controls.OfType<SliderControlViewModel>().FirstOrDefault(c => c.Feature.Code == VcpCode.Brightness);

    private async Task<T> RunExclusiveAsync<T>(Func<T> work)
    {
        await gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { gate.Release(); }
    }

    public void Dispose() => Device.Dispose();

    /// <summary>The display name, for drop-downs whose template shows the item as text.</summary>
    public override string ToString() => Name;
}
