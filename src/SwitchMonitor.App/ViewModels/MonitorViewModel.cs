using System.Collections.ObjectModel;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Scenes;
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

    /// <summary>The display belongs to another computer (this one controls it over the network).</summary>
    public bool IsRemote => Device.Identity.IsRemote;

    /// <summary>The computer that owns the display, or null for a display on this computer.</summary>
    public string? ComputerName => Device.Identity.RemoteComputer;

    /// <summary>True once the panel has been read from the monitor.</summary>
    public bool IsLoaded => loaded && loading is { IsCompleted: true };

    /// <summary>The input last read or set, when known.</summary>
    public int? CurrentInput => Inputs.FirstOrDefault(i => i.IsCurrent)?.Code;

    public MonitorDevice Device { get; }
    /// <summary>Brand and model ("LG 29WK600", "Laptop DELL N5050").</summary>
    public string Name => DisplayNames.For(Device);
    public bool IsActive => Device.IsActive;
    public bool IsControllable => Device.IsControllable && !unavailable && !heldElsewhere;

    private bool unavailable;
    private bool stopped;

    /// <summary>What the page says in place of the controls.</summary>
    public string NothingToAdjustHint => IsUnavailable
        ? "This display is not answering: it is probably showing another computer. It comes back here by itself when that computer lets go of it."
        : "Nothing to adjust here right now. Turn the display on or reconnect it, then use Refresh displays.";

    /// <summary>
    /// The display is on and connected here but does not answer: the monitor is showing another computer's input, or that
    /// computer holds its DDC/CI. Not an error: it is checked again every few seconds and comes back by itself.
    /// </summary>
    public bool IsUnavailable => unavailable || heldElsewhere;

    /// <summary>The display answers by itself (it is not flagged unavailable on its own account): used to match it with the same display on another computer.</summary>
    public bool AnswersItself => !unavailable;

    private bool heldElsewhere;

    /// <summary>
    /// Another computer proved it has this display (it detects what the display shows and publishes it) while this one cannot tell: it is
    /// shown as in use by another computer and its controls are locked, like a laptop panel Windows is not using.
    /// </summary>
    public void SetHeldElsewhere(bool held)
    {
        if (heldElsewhere == held) return;
        heldElsewhere = held;
        Services.Trace.Write($"{Name}: held by another computer = {held}");
        foreach (string name in new[] { nameof(IsUnavailable), nameof(NothingToAdjustHint), nameof(IsControllable), nameof(Subtitle), nameof(DisplayGlyph), nameof(Details), nameof(ShowsSkeleton), nameof(HasInputs), nameof(HasAdvanced), nameof(HasColorTuning), nameof(CanResetAll) })
            Raise(name);
    }

    private string? panel;

    /// <summary>For built-in screens: the panel from its EDID ("CMO XM5XGN156B6 · 15.6″ · 1366×768").</summary>
    public string? Panel
    {
        get => panel;
        set { if (Set(ref panel, value)) Raise(nameof(Subtitle)); }
    }

    /// <summary>The Windows main display now (asked from Windows; the identity only says what it was when listed).</summary>
    public bool IsMainDisplay
    {
        get
        {
            string gdi = Device.Identity.GdiName;
            try
            {
                if (!string.IsNullOrEmpty(gdi) && Services.MainDisplay.Active.Contains(gdi, StringComparer.OrdinalIgnoreCase))
                    return Services.MainDisplay.IsMain(gdi);
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            return Device.Identity.IsPrimary;   // demo displays and displays Windows is not using
        }
    }

    /// <summary>The main display moved: the subtitles say so again.</summary>
    public void RaiseMainDisplay() => Raise(nameof(Subtitle));

    private bool isPreferredMain;

    /// <summary>The preferred main display (star): made the main display whenever it is connected.</summary>
    public bool IsPreferredMain
    {
        get => isPreferredMain;
        set { if (Set(ref isPreferredMain, value)) { Raise(nameof(Subtitle)); Raise(nameof(PreferredToolTip)); } Raise(nameof(PreferredSwitch)); }
    }

    /// <summary>Set by the main view model: what the star does.</summary>
    internal Action<MonitorViewModel>? TogglePreferred { get; set; }

    /// <summary>The star button (two-way): clicking asks the main view model, which sets <see cref="IsPreferredMain"/>.</summary>
    public bool PreferredSwitch
    {
        get => IsPreferredMain;
        set { if (value != IsPreferredMain) TogglePreferred?.Invoke(this); Raise(); }
    }

    public string PreferredToolTip => IsPreferredMain
        ? "Preferred main display: it becomes the main display whenever it is connected. Click to stop preferring it."
        : "Prefer this display as the main display: whenever it is connected, it becomes the main display.";

    public string Subtitle => !Device.IsActive ? "Off — Windows is not using this display"
        : IsUnavailable ? (IsRemote ? $"Unavailable on {ComputerName}" : "Unavailable — in use by another computer")
        : !Device.IsControllable ? "No control path on this computer"
        : string.Join(" · ", new[]
          {
              IsRemote ? ComputerName : null,
              IsMainDisplay ? "Main display" : null,
              IsPreferredMain ? "Preferred" : null,
              Device.Identity.IsBuiltIn ? "Built-in" : IsMainDisplay ? null : "External",
              Device.Identity.IsBuiltIn ? Panel : null
          }.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Tooltip of the display: full name and how the app talks to it (ddc-ci, intel-cui, amd-adl, wmi).</summary>
    /// <summary>Laptop screen, external monitor, or one Windows is not using.</summary>
    public string DisplayGlyph => Device.Identity.IsBuiltIn ? Icons.BuiltIn : Device.IsActive && !IsUnavailable ? Icons.External : Icons.ExternalOff;

    public string Details => Device.IsControllable ? $"{Name}\nControlled through {Device.TransportId}" : Name;

    public ObservableCollection<InputViewModel> Inputs { get; } = [];
    public bool HasInputs => Inputs.Count > 0 && !IsUnavailable;
    public ObservableCollection<ControlViewModel> Controls { get; } = [];

    /// <summary>Red/green/blue and black levels: shown on the Advanced tab only (see <see cref="FeatureCatalog.IsAdvanced"/>).</summary>
    public ObservableCollection<ControlViewModel> AdvancedControls { get; } = [];

    public bool HasAdvanced => AdvancedControls.Count > 0 && !IsUnavailable;

    private SoftwareColorViewModel? softwareColor;

    /// <summary>Color made by the graphics card (Advanced tab); null for displays Windows is not using.</summary>
    public SoftwareColorViewModel? SoftwareColor { get => softwareColor; set { if (Set(ref softwareColor, value)) { Raise(nameof(HasSoftwareColor)); Raise(nameof(HasColorTuning)); Raise(nameof(NoAdvancedText)); } } }
    public bool HasSoftwareColor => SoftwareColor is not null;

    /// <summary>The "Color fine-tuning" section on the Displays tab has something to show.</summary>
    public bool HasColorTuning => (HasAdvanced || HasSoftwareColor) && !IsUnavailable;
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

    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) Raise(nameof(ShowsSkeleton)); } }

    /// <summary>
    /// While the controls are being read for the first time: the page shows grey placeholders the size of the Input and
    /// Settings cards, so nothing jumps when they arrive.
    /// </summary>
    public bool ShowsSkeleton => IsControllable && Controls.Count == 0 && loadError is null && (isLoading || !loaded);
    public string? LoadError { get => loadError; private set { if (Set(ref loadError, value)) Raise(nameof(ShowsSkeleton)); } }

    /// <summary>Builds the panel the first time the monitor is selected (reading every control takes a few seconds on GPU transports).</summary>
    public Task EnsureLoadedAsync()
    {
        if (loaded || !IsControllable) return loading ?? Task.CompletedTask;
        loaded = true;
        return loading = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await LoadControlsAsync();
        }
        finally
        {
            Raise(nameof(Name));   // the model is known once the capabilities have been read
        }
    }

    private async Task LoadControlsAsync()
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
                        else { notShown.Add(control.Name); failedControls.Add(control); }
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

            // Nothing answered at all (and no laptop panel, whose first read can fail by itself): not an error, the display is
            // busy with another computer. Show it as unavailable and look again every few seconds.
            if (!Device.Identity.IsBuiltIn && currentInput is null && !controls.Any(c => !c.IsUnread && !c.Feature.Definition.WriteOnly))
            {
                notShown.Clear();
                loaded = false;
                SetUnavailable(true);
                _ = WatchAvailabilityAsync();
                return;
            }

            // The display answers but shows another computer's input (the LG keeps answering DDC on every input): it is not
            // this computer's display now, so it counts as unavailable here and is not published to the other computers.
            if (currentInput is int shown && Device.AnswersOnOtherInputs && ShowsAnotherComputerNow(shown))
            {
                notShown.Clear();
                loaded = false;
                SetUnavailable(true);
                _ = WatchAvailabilityAsync();
                return;
            }

            _ = WatchHealthAsync();
            // The display answers and shows this computer's picture: this is the input this computer is plugged into.
            if (currentInput is int own && Device.ResendsInputOnReturn && Device.IsActive) this.inputs.LearnHomeInput(this, own);
            foreach (var (code, label) in inputs)
                Inputs.Add(new InputViewModel(this, code, label, this.inputs) { IsCurrent = code == currentInput });
            this.inputs.InputsLoaded(this);
            // Picture mode first (it decides what the others can do), then brightness, then the rest.
            foreach (var control in controls.OrderBy(c => SettingOrder.Rank(c.Feature.Definition)))
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
                if (failedControls.Count > 0) _ = RetryFailedControlsAsync();
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

    private void SetUnavailable(bool value)
    {
        if (unavailable == value) return;
        Services.Trace.Write($"{Name}{(IsRemote ? " (remote)" : "")}: unavailable = {value}; reached from {new System.Diagnostics.StackFrame(1).GetMethod()?.Name}");
        unavailable = value;
        LoadError = null;
        foreach (string name in new[] { nameof(IsUnavailable), nameof(NothingToAdjustHint), nameof(IsControllable), nameof(Subtitle), nameof(DisplayGlyph), nameof(Details), nameof(ShowsSkeleton), nameof(HasInputs), nameof(HasAdvanced), nameof(HasColorTuning), nameof(CanResetAll) })
            Raise(name);
    }

    private bool watching;

    /// <summary>The display stopped answering: its page is emptied, it shows as unavailable and is looked at again.</summary>
    private void GoUnavailable()
    {
        ForgetControls();
        SetUnavailable(true);
        _ = WatchAvailabilityAsync();
    }

    private bool ProbeAnswers()
    {
        Device.ForgetFailedCapabilities();
        foreach (byte code in new[] { VcpCode.Brightness, VcpCode.InputSource })
            try { Device.ReadOnce(code); return true; }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        return false;
    }

    /// <summary>It answers, but reports an input other than the one this computer is plugged into (only for displays that keep answering).</summary>
    private bool ShowsAnotherComputer() =>
        Device.AnswersOnOtherInputs && Device.TryReadInputOnce() is int shown && ShowsAnotherComputerNow(shown);

    /// <summary>
    /// The display answers, but is it showing this computer? Windows knows how this computer is plugged in (HDMI, DisplayPort...):
    /// an input of another kind is the other computer's. When that does not decide (same kind on both), the input learned for
    /// this computer is compared.
    /// </summary>
    private bool ShowsAnotherComputerNow(int shown)
    {
        // A display of another computer is judged by that computer (it does not publish a display it is not showing): this computer's
        // connectors say nothing about it, and answering "yes" here made the display vanish from the panel the moment it was picked.
        if (IsRemote) return false;
        if (Core.Diagnostics.BuiltInProbe.InputFitsConnector(Core.Diagnostics.BuiltInProbe.ConnectorOf(Device.Identity.GdiName), shown) is bool fits)
            return !fits;
        return this.inputs.HomeInput(this) is int own && shown != own;
    }

    private bool healthWatching;

    /// <summary>
    /// A display can stop answering without the app doing anything: its own menu switched it to another computer. Every 30
    /// seconds a quiet read; two misses in a row (so one slow answer does not count) and it shows as unavailable.
    /// </summary>
    private async Task WatchHealthAsync()
    {
        if (healthWatching || Device.Identity.IsBuiltIn) return;
        healthWatching = true;
        try
        {
            while (!stopped)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                if (stopped) break;
                if (unavailable || !loaded || isLoading || !Device.IsControllable) continue;
                if (await RunExclusiveAsync(ProbeAnswers)) continue;
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (stopped || unavailable || await RunExclusiveAsync(ProbeAnswers)) continue;
                GoUnavailable();
            }
        }
        finally { healthWatching = false; }
    }

    private void ForgetControls()
    {
        Inputs.Clear();
        Controls.Clear();
        AdvancedControls.Clear();
        notShown.Clear();
        failedControls.Clear();
        loaded = false;
    }

    /// <summary>While unavailable: every 8 seconds one quiet read; when the display answers, its controls are loaded.</summary>
    private async Task WatchAvailabilityAsync()
    {
        if (watching) return;
        watching = true;
        try
        {
            while (unavailable && !stopped)
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                if (!unavailable || stopped) break;
                bool answers = await RunExclusiveAsync(ProbeAnswers);
                if (!answers || await RunExclusiveAsync(ShowsAnotherComputer)) continue;
                ForgetControls();
                SetUnavailable(false);
                report($"{Name} is available again.", true);
                await EnsureLoadedAsync();
                Raise(nameof(Name));
                // Another computer handed the display back by the standard code: its menu still shows the other port. Sending
                // this computer's own input again (the vendor command) refreshes it (08/11/2026, LG 29WK600 from an AMD PC).
                if (Device.ResendsInputOnReturn && this.inputs.HomeInput(this) is int home && Device.Inputs.Contains(home))
                {
                    await Task.Delay(TimeSpan.FromSeconds(1.5));
                    if (!stopped && !unavailable)
                        await SwitchToAsync(home, $"menu corrected ({Device.DescribeInput(home)} sent again)");
                }
            }
        }
        finally { watching = false; }
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
    private readonly List<ControlViewModel> failedControls = [];

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

    /// <summary>
    /// Controls left out because their first read failed (the display was still waking up, or answering badly) are read again
    /// after 3, 8, 16 and 30 seconds; the ones that answer join the panel in their place. Nothing is reported.
    /// </summary>
    private async Task RetryFailedControlsAsync()
    {
        foreach (int seconds in new[] { 3, 5, 8, 14 })
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            if (stopped || unavailable || failedControls.Count == 0) return;
            var pending = failedControls.ToArray();
            var read = await RunExclusiveAsync(() =>
            {
                var ok = new List<ControlViewModel>();
                foreach (var control in pending)
                    try { control.Load(Device); ok.Add(control); } catch (Exception error) when (error is not OutOfMemoryException) { }
                return ok;
            });
            foreach (var control in read)
            {
                failedControls.Remove(control);
                notShown.Remove(control.Name);
                control.Publish();
                var target = FeatureCatalog.IsAdvanced(control.Feature.Code) ? AdvancedControls : Controls;
                int rank = SettingOrder.Rank(control.Feature.Definition);
                int at = target.Count;
                for (int i = 0; i < target.Count; i++)
                    if (SettingOrder.Rank(target[i].Feature.Definition) > rank) { at = i; break; }
                target.Insert(at, control);
            }
            if (read.Count > 0) { UpdateLocks(); ClearLoadErrorWhenAllRead(); Raise(nameof(HasAdvanced)); }
        }
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
            if (reason is null && control.ReportedLocked) reason = "The monitor has locked this control: one of its own modes (eye saver, motion blur reduction) is on.";
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

    public bool CanResetAll => Device.IsControllable && !IsUnavailable &&
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
        string text = what ?? $"Input {label}";
        try
        {
            var outcome = await RunExclusiveAsync(() => Device.SetInput(code));
            // The write went out but the display does not confirm (or says another input): when it was switched to an input
            // another computer uses, it stops answering here. That is the point of the switch, not a failure.
            if (!Device.Identity.IsBuiltIn && outcome == WriteOutcome.Unverified)
            {
                report($"{Name}: {text} (sent; the display is probably showing another computer now)", true);
                GoUnavailable();
                return true;
            }
            report(outcome == WriteOutcome.Mismatch
                ? $"{Name}: {text} — the display stayed on its input. It usually does not switch to an input with no signal: check that the other computer is on and connected there."
                : Describe(outcome, text), outcome != WriteOutcome.Mismatch);
            if (outcome != WriteOutcome.Mismatch) MarkCurrentInput(code);
            // Confirmed and still answering: this is an input this computer has the picture on (the one it is plugged into).
            if (outcome == WriteOutcome.Applied && Device.AnswersOnOtherInputs) this.inputs.LearnHomeInput(this, code);
            return outcome != WriteOutcome.Mismatch;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            report($"{Name}: {text} failed — {error.Message}", false);
            return false;
        }
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

    /// <summary>A write the person made on the panel (slider, list, switch, reset): the active profile shows "custom".</summary>
    public Task<bool> RunByHandAsync(string what, Func<MonitorDevice, WriteOutcome> action, byte? code = null)
    {
        inputs.SettingChangedByHand(this, code);
        // FreeSync re-syncs the link and the screen goes black for a moment: cover it, write, wait for the display, uncover.
        if (code is byte c && TransitionSource?.Invoke() is { } hooks &&
            Device.Features.FirstOrDefault(f => f.Code == c) is { } feature && SceneApplier.IsDisruptive(feature.Name))
            return RunDisruptiveByHandAsync(what, hooks, c, action);
        return RunAsync(what, action);
    }

    private async Task<bool> RunDisruptiveByHandAsync(string what, TransitionHooks hooks, byte code, Func<MonitorDevice, WriteOutcome> action)
    {
        bool ok = await RunAsync(what, device => WithCover(hooks, device, code, action));
        if (ok) inputs.ReopenDisplaysAfterResync();
        return ok;
    }

    /// <summary>A gradual change wrote a level to this display: the matching slider follows it (nothing is written back).</summary>
    public void FollowLevel(byte code, uint raw) =>
        AllControls.OfType<SliderControlViewModel>().FirstOrDefault(c => c.Feature.Code == code)?.Follow(raw);

    /// <summary>The cover for a change made by hand (the same hooks a profile uses); the cover always comes off.</summary>
    private static WriteOutcome WithCover(TransitionHooks hooks, MonitorDevice device, byte code, Func<MonitorDevice, WriteOutcome> action)
    {
        hooks.BeforeDisruptive?.Invoke();
        try
        {
            var outcome = action(device);
            SceneApplier.WaitUntilAnswering(device, code, Core.Reliability.ThreadDelay.Instance, hooks.StableWithin);
            Thread.Sleep(1200);   // the picture is back and steady: a margin before uncovering
            return outcome;
        }
        finally { hooks.AfterDisruptive?.Invoke(); }
    }

    /// <summary>The cover to use for a change that blacks the screen out (set by the app); null: none (demo displays).</summary>
    public Func<TransitionHooks?>? TransitionSource { get; init; }

    private string Describe(WriteOutcome outcome, string what) => outcome switch
    {
        WriteOutcome.Verified => $"{Name}: {what}",
        WriteOutcome.Applied => $"{Name}: {what} (sent; this monitor does not report it back)",
        WriteOutcome.Unverified => $"{Name}: {what} (sent; the monitor did not confirm)",
        _ => $"{Name}: {what} — the monitor kept another value"
    };

    /// <summary>Runs one write and reports the outcome in the status bar. Returns false when it failed.</summary>
    public async Task<bool> RunAsync(string what, Func<MonitorDevice, WriteOutcome> action)
    {
        try
        {
            var outcome = await RunExclusiveAsync(() => action(Device));
            report(Describe(outcome, what), outcome != WriteOutcome.Mismatch);
            // A write the display could not confirm, and it no longer answers at all: it went to another computer.
            if (outcome == WriteOutcome.Unverified && !Device.Identity.IsBuiltIn && !unavailable && !await RunExclusiveAsync(ProbeAnswers))
                GoUnavailable();
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
        foreach (var step in steps.Where(s => s.Monitor == Device.Name))   // steps carry the device name, not the shown one
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

    /// <summary>Runs monitor work off the UI thread, after any other command for this display (Learn settings uses it too).</summary>
    public async Task<T> RunExclusiveAsync<T>(Func<T> work)
    {
        await gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        stopped = true;
        Device.Dispose();
    }

    /// <summary>The display name, for drop-downs whose template shows the item as text.</summary>
    public override string ToString() => Name;
}
