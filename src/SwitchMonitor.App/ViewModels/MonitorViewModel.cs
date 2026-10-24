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

    public string Subtitle => !Device.IsActive ? "Off — Windows is not using this display"
        : !Device.IsControllable ? "No control path on this computer"
        : $"{(Device.Identity.IsPrimary ? "Main display · " : "")}{Device.TransportId}";

    public ObservableCollection<InputViewModel> Inputs { get; } = [];
    public bool HasInputs => Inputs.Count > 0;
    public ObservableCollection<ControlViewModel> Controls { get; } = [];

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
                        if (FeatureCatalog.Find(control.Feature.Code) is not null) failed.Add($"{control.Name} ({error.Message})");
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
                Controls.Add(control);
            }
            Raise(nameof(HasInputs));
            LinkMuteAndVolume();
            if (unreadable.Count > 0)
            {
                LoadError = "Could not be read, so not shown: " + string.Join("; ", unreadable);
                report($"{Name}: {LoadError}", false);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            LoadError = error.Message;
            loaded = false;
        }
        finally { IsLoading = false; }
    }

    /// <summary>Re-reads every control (after a scene or a change made on the monitor's own menu).</summary>
    public async Task ReloadValuesAsync()
    {
        if (!loaded) return;
        var controls = Controls.Concat(Controls.OfType<SliderControlViewModel>().Select(s => (ControlViewModel?)s.Mute))
            .OfType<ControlViewModel>().ToArray();
        await RunExclusiveAsync(() =>
        {
            foreach (var control in controls)
                try { control.Load(Device); } catch (Exception error) when (error is not OutOfMemoryException) { }
            return true;
        });
        foreach (var control in controls) control.Publish();
        UpdateLocks();
    }

    private ControlViewModel Create(MonitorFeature feature)
    {
        if (feature.Definition.Risk is FeatureRisk.Unmapped or FeatureRisk.ReadOnly) return new ReadOnlyControlViewModel(this, feature);
        if (feature.Kind == FeatureKind.Continuous)
            return new SliderControlViewModel(this, feature)
            {
                ResetGroup = MonitorDevice.GroupOf(feature.Code) is { } group && Device.CanRestore(group) ? group : null
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
        foreach (var control in Controls)
            control.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ChoiceControlViewModel.Selected) or nameof(ToggleControlViewModel.IsOn)) UpdateLocks();
            };
        UpdateLocks();
    }

    /// <summary>Locks volume while muted and controls whose quirk rules are not met (LG red/green/blue outside Custom).</summary>
    private void UpdateLocks()
    {
        var all = Controls.ToList();
        foreach (var control in all)
        {
            string? reason = null;
            foreach (var (code, values) in control.Feature.Definition.EnabledWhen)
            {
                var source = all.FirstOrDefault(c => c.Feature.Code == code);
                if (source?.RawValue is uint raw && !values.Contains(raw))
                {
                    string allowed = string.Join(" or ", values.Select(source.Feature.Definition.DescribeValue));
                    reason = $"Adjustable only when {source.Name} is {allowed}.";
                    break;
                }
            }
            if (reason is null && control is SliderControlViewModel { Mute.IsOn: true }) reason = "Muted: click the speaker to unmute.";
            control.SetLock(reason);
        }
    }

    /// <summary>
    /// Factory values for the current mode: one control (the others in its group are put back) or, with null,
    /// brightness, contrast and colors together.
    /// </summary>
    public async Task ResetAsync(ControlViewModel? control)
    {
        DefaultsGroup[] groups = control?.ResetGroup is { } one
            ? new[] { one }
            : new[] { DefaultsGroup.LuminanceContrast, DefaultsGroup.Color }.Where(Device.CanRestore).ToArray();
        if (groups.Length == 0) return;
        string what = control is null ? "factory brightness, contrast and colors" : $"{control.Name} factory value";
        try
        {
            await RunExclusiveAsync(() =>
            {
                foreach (var group in groups) Device.RestoreDefaults(group, control?.Feature.Code);
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

    /// <summary>Runs one write and reports the outcome in the status bar. Returns false when it failed.</summary>
    public async Task<bool> RunAsync(string what, Func<MonitorDevice, WriteOutcome> action)
    {
        try
        {
            var outcome = await RunExclusiveAsync(() => action(Device));
            string message = outcome switch
            {
                WriteOutcome.Verified => $"{Name}: {what}",
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

    public SliderControlViewModel? Brightness =>
        Controls.OfType<SliderControlViewModel>().FirstOrDefault(c => c.Feature.Code == VcpCode.Brightness);

    private async Task<T> RunExclusiveAsync<T>(Func<T> work)
    {
        await gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { gate.Release(); }
    }

    public void Dispose() => Device.Dispose();
}
