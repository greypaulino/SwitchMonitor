using System.Collections.ObjectModel;
using System.Windows.Input;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App.ViewModels;

/// <summary>One row in the controls panel, generated from a <see cref="MonitorFeature"/>.</summary>
public abstract class ControlViewModel(MonitorViewModel owner, MonitorFeature feature) : ObservableObject
{
    protected MonitorViewModel Owner { get; } = owner;
    public MonitorFeature Feature { get; } = feature;
    public string Name => Feature.Name;

    private string? note;
    /// <summary>Small hint under the control (read-only reason, last error).</summary>
    public string? Note { get => note; protected set => Set(ref note, value); }

    private bool isEnabled = true;
    private string? lockReason;

    /// <summary>False while another control makes this one meaningless (volume while muted, RGB outside Custom).</summary>
    public bool IsEnabled { get => isEnabled; private set => Set(ref isEnabled, value); }

    /// <summary>Why the control is locked, shown as a tooltip.</summary>
    public string? LockReason { get => lockReason; private set => Set(ref lockReason, value); }

    public void SetLock(string? reason)
    {
        LockReason = reason;
        IsEnabled = reason is null;
    }

    /// <summary>The raw value shown now, for rules such as "only in picture mode Custom" (null for sliders).</summary>
    public virtual uint? RawValue => null;

    /// <summary>The factory-default group that can reset this control, if the monitor offers it.</summary>
    public DefaultsGroup? ResetGroup { get; init; }
    public bool CanReset => ResetGroup is not null;
    public ICommand ResetCommand => new RelayCommand(() => _ = Owner.ResetAsync(this));

    /// <summary>Reads the current value from the monitor (called on a worker thread).</summary>
    public abstract void Load(MonitorDevice device);

    /// <summary>Pushes the loaded value to bindings (called on the UI thread after <see cref="Load"/>).</summary>
    public abstract void Publish();
}

/// <summary>Continuous control shown as a 0-100 % slider. Writes are debounced while dragging.</summary>
public sealed class SliderControlViewModel(MonitorViewModel owner, MonitorFeature feature) : ControlViewModel(owner, feature)
{
    private double value;
    private double loaded;
    private bool publishing;
    private CancellationTokenSource? pending;
    private ToggleControlViewModel? mute;

    /// <summary>The mute switch shown as a speaker icon in place of this slider's name (volume only).</summary>
    public ToggleControlViewModel? Mute
    {
        get => mute;
        set
        {
            if (mute is not null) mute.PropertyChanged -= OnMuteChanged;
            mute = value;
            if (mute is not null) mute.PropertyChanged += OnMuteChanged;
            Raise();
            Raise(nameof(HasMute));
            Raise(nameof(SpeakerGlyph));
        }
    }

    public bool HasMute => Mute is not null;

    /// <summary>Segoe MDL2 Assets: E767 = speaker, E74F = muted speaker.</summary>
    public string SpeakerGlyph => Mute?.IsOn == true ? "\uE74F" : "\uE767";

    public ICommand ToggleMuteCommand => new RelayCommand(() => { if (Mute is { } m) m.IsOn = !m.IsOn; });

    private void OnMuteChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToggleControlViewModel.IsOn)) Raise(nameof(SpeakerGlyph));
    }

    public double Value
    {
        get => value;
        set
        {
            if (!Set(ref this.value, Math.Round(value))) return;
            Raise(nameof(ValueText));
            if (!publishing) ScheduleWrite(this.value);
        }
    }

    public string ValueText => $"{Value:0}%";

    public override void Load(MonitorDevice device)
    {
        var raw = device.Read(Feature.Code);
        uint top = Feature.Definition.Maximum ?? raw.Maximum;
        loaded = top == 0 ? 0 : Math.Round(raw.Current * 100.0 / top);
    }

    public override void Publish()
    {
        publishing = true;
        try { Value = loaded; }
        finally { publishing = false; }
    }

    /// <summary>Used by brightness shortcuts: moves the slider at once; the write follows the usual short debounce.</summary>
    public void SetTarget(int percent) => Value = Math.Clamp(percent, 0, 100);

    private void ScheduleWrite(double target)
    {
        pending?.Cancel();
        var cts = pending = new CancellationTokenSource();
        _ = WriteLaterAsync(target, cts.Token);
    }

    private async Task WriteLaterAsync(double target, CancellationToken token)
    {
        try { await Task.Delay(150, token); }
        catch (TaskCanceledException) { return; }
        await Owner.RunAsync($"{Name} {target:0}%", device => device.SetPercent(Feature.Code, target));
    }
}

public sealed record ChoiceOption(uint Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Choice or toggle control shown as a drop-down (picture mode, response time, FreeSync...).</summary>
public sealed class ChoiceControlViewModel : ControlViewModel
{
    private ChoiceOption? selected;
    private uint loaded;
    private bool publishing;

    public ChoiceControlViewModel(MonitorViewModel owner, MonitorFeature feature) : base(owner, feature)
    {
        Options = new(feature.AllowedValues.Select(v => new ChoiceOption(v, feature.Definition.DescribeValue(v))));
    }

    public ObservableCollection<ChoiceOption> Options { get; }

    public override uint? RawValue => Selected?.Value;

    public ChoiceOption? Selected
    {
        get => selected;
        set
        {
            var previous = selected;
            if (!Set(ref selected, value) || publishing || value is null) return;
            _ = ApplyAsync(value, previous);
        }
    }

    public override void Load(MonitorDevice device) => loaded = device.Read(Feature.Code).Current;

    public override void Publish()
    {
        publishing = true;
        try
        {
            var match = Options.FirstOrDefault(o => o.Value == loaded);
            if (match is null)
            {
                // The monitor reports a value outside the known list (e.g. a transitional state). Show it, don't offer it.
                match = new ChoiceOption(loaded, Feature.Definition.DescribeValue(loaded) + " (current)");
                Options.Insert(0, match);
            }
            Selected = match;
        }
        finally { publishing = false; }
    }

    private async Task ApplyAsync(ChoiceOption option, ChoiceOption? previous)
    {
        bool ok = await Owner.RunAsync($"{Name}: {option.Label}", device => device.SetChoice(Feature.Code, option.Value));
        if (!ok && previous is not null)
        {
            publishing = true;
            try { Selected = previous; }
            finally { publishing = false; }
        }
        // A picture mode or preset loads its own brightness, contrast, colors...: show them without "Read again".
        if (ok) Owner.RefreshSoon();
    }
}

/// <summary>Two-option control (FreeSync On/Off, Mute) shown as a switch with ON / OFF beside it.</summary>
public sealed class ToggleControlViewModel(MonitorViewModel owner, MonitorFeature feature, uint onValue, uint offValue)
    : ControlViewModel(owner, feature)
{
    private bool isOn;
    private uint loaded;
    private bool publishing;

    public bool IsOn
    {
        get => isOn;
        set
        {
            bool previous = isOn;
            if (!Set(ref isOn, value)) return;
            Raise(nameof(StateText));
            if (!publishing) _ = ApplyAsync(value, previous);
        }
    }

    public string StateText => IsOn ? "ON" : "OFF";

    public override uint? RawValue => IsOn ? onValue : offValue;

    public override void Load(MonitorDevice device) => loaded = device.Read(Feature.Code).Current;

    public override void Publish()
    {
        publishing = true;
        try { IsOn = loaded == onValue; }
        finally { publishing = false; }
    }

    private async Task ApplyAsync(bool on, bool previous)
    {
        uint raw = on ? onValue : offValue;
        bool ok = await Owner.RunAsync($"{Name}: {(on ? "ON" : "OFF")}", device => device.SetChoice(Feature.Code, raw));
        if (!ok)
        {
            publishing = true;
            try { IsOn = previous; }
            finally { publishing = false; }
        }
        else Owner.RefreshSoon();
    }
}

/// <summary>Control the app can show but not change (unmapped vendor code, or ignored by the monitor).</summary>
public sealed class ReadOnlyControlViewModel : ControlViewModel
{
    private string text = "…";
    private string loaded = "";

    public ReadOnlyControlViewModel(MonitorViewModel owner, MonitorFeature feature) : base(owner, feature)
    {
        Note = feature.Definition.Risk == FeatureRisk.ReadOnly
            ? "This monitor ignores changes to this setting."
            : "Unknown vendor setting. Use learn mode to map it.";
    }

    public string Text { get => text; private set => Set(ref text, value); }

    public override void Load(MonitorDevice device)
    {
        var raw = device.Read(Feature.Code);
        if (Feature.Kind == FeatureKind.Continuous)
        {
            uint top = Feature.Definition.Maximum ?? raw.Maximum;
            loaded = top == 0 ? raw.Current.ToString() : $"{Math.Round(raw.Current * 100.0 / top)}%";
        }
        else loaded = Feature.Definition.DescribeValue(raw.Current);
    }

    public override void Publish() => Text = loaded;
}

/// <summary>What input buttons need from the app: switching with history, shortcuts and the Next input marks.</summary>
public interface IInputCoordinator
{
    Task SwitchInputAsync(MonitorViewModel monitor, int code);
    string ShortcutFor(MonitorViewModel monitor, int code);
    bool IsInCycle(MonitorViewModel monitor, int code);
    void SetInCycle(MonitorViewModel monitor, int code, bool included);
    void InputsLoaded(MonitorViewModel monitor);
}

/// <summary>One input button (HDMI 1, DisplayPort 1 ...) with its shortcut and its Next input mark.</summary>
public sealed class InputViewModel : ObservableObject
{
    private readonly MonitorViewModel owner;
    private readonly IInputCoordinator coordinator;
    private bool isCurrent;

    public InputViewModel(MonitorViewModel owner, int code, string label, IInputCoordinator coordinator)
    {
        this.owner = owner;
        this.coordinator = coordinator;
        Code = code;
        Label = label;
        SelectCommand = new RelayCommand(() => _ = coordinator.SwitchInputAsync(owner, code));
    }

    public int Code { get; }
    public string Label { get; }
    public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
    public ICommand SelectCommand { get; }

    public string Shortcut => coordinator.ShortcutFor(owner, Code);

    /// <summary>1.x "connected" box: Next input only visits checked inputs.</summary>
    public bool InCycle
    {
        get => coordinator.IsInCycle(owner, Code);
        set
        {
            coordinator.SetInCycle(owner, Code, value);
            Raise();
        }
    }

    public void Refresh()
    {
        Raise(nameof(Shortcut));
        Raise(nameof(InCycle));
    }
}
