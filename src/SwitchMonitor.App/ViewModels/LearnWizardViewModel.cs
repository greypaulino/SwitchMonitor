using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Learning;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App.ViewModels;

public enum LearnStep { Find = 1, Try, Describe, Modes, Save }

/// <summary>One step of the wizard's stepper bar.</summary>
public sealed class LearnStepItem(LearnStep step, string title) : ObservableObject
{
    private bool isCurrent, isDone;
    public LearnStep Step { get; } = step;
    public int Number => (int)Step;
    public string Title { get; } = title;
    public bool IsCurrent { get => isCurrent; set => Set(ref isCurrent, value); }
    public bool IsDone { get => isDone; set => Set(ref isDone, value); }
    public bool IsFirst => Step == LearnStep.Find;
}

/// <summary>What a found code does, as the person describes it (Describe step). Kind null: keep the guess.</summary>
public sealed record LearnFunction(string Name, LearnKind? Kind, bool Discards = false, bool IsOther = false)
{
    public const string OtherName = "Other…";

    public static IReadOnlyList<LearnFunction> All { get; } =
    [
        new("Nothing visible", null, Discards: true),
        new("Brightness", LearnKind.Level),
        new("Contrast", LearnKind.Level),
        new("Sharpness", LearnKind.Level),
        new("Color temperature", LearnKind.Options),
        new("Picture mode", LearnKind.Options),
        new("Response time", LearnKind.Options),
        new("FreeSync", LearnKind.Options),
        new("Volume", LearnKind.Level),
        new("Gamma", null),
        new("Black level", LearnKind.Level),
        new("Saturation", LearnKind.Level),
        new("Hue", LearnKind.Level),
        new(OtherName, null, IsOther: true)
    ];

    public override string ToString() => Name;
}

/// <summary>One value of a list setting: try it, then name it (blank leaves it out).</summary>
public sealed class LearnValueRow : ObservableObject
{
    public const string Skip = "—";
    public static IReadOnlyList<string> Names { get; } = [Skip, "Off", "On", "Normal", "Fast", "Faster", "Low", "Medium", "High", LearnFunction.OtherName];

    private readonly LearnCandidateViewModel owner;
    private string selectedName = Skip;
    private string customName = "";
    private bool isTried, isTrying;

    public LearnValueRow(LearnCandidateViewModel owner, uint value)
    {
        this.owner = owner;
        Value = value;
        TryCommand = new RelayCommand(_ => _ = owner.Wizard.TryValueAsync(owner, Value), _ => owner.Wizard.CanTry);
    }

    public uint Value { get; }
    public string Label => $"Value {Value}" + (Value == owner.Candidate.Value.Current ? " (now)" : "");
    public ICommand TryCommand { get; }
    public IReadOnlyList<string> NameOptions => Names;
    public bool IsTried { get => isTried; set => Set(ref isTried, value); }
    public bool IsTrying { get => isTrying; set => Set(ref isTrying, value); }

    public string SelectedName
    {
        get => selectedName;
        set { if (Set(ref selectedName, value)) { Raise(nameof(IsOther)); owner.Changed(); } }
    }

    public string CustomName
    {
        get => customName;
        set { if (Set(ref customName, value)) owner.Changed(); }
    }

    public bool IsOther => selectedName == LearnFunction.OtherName;

    /// <summary>The name to keep, or null when the value is left out.</summary>
    public string? FinalName => IsOther ? (customName.Trim() is { Length: > 0 } custom ? custom : null)
        : selectedName == Skip ? null : selectedName;
}

/// <summary>Whether the setting kept a test value in one picture mode (Modes step).</summary>
public sealed class LearnModeRow(uint mode, string name) : ObservableObject
{
    private string status = "Waiting";
    private bool? works;
    public uint Mode { get; } = mode;
    public string Name { get; } = name;
    public string Status { get => status; set => Set(ref status, value); }
    public bool? Works { get => works; set => Set(ref works, value); }
}

/// <summary>One found code as it goes through Try, Describe and Modes.</summary>
public sealed class LearnCandidateViewModel : ObservableObject
{
    private LearnFunction? function;
    private string customName = "";
    private LearnKind kind;
    private double handValue;
    private string lastTry = "";
    private bool? readBack;
    private bool modesChecked;

    public LearnCandidateViewModel(LearnWizardViewModel wizard, LearnCandidate candidate)
    {
        Wizard = wizard;
        Candidate = candidate;
        kind = candidate.GuessedKind;
        handValue = candidate.Value.Current;
        foreach (uint value in candidate.OptionValues) Values.Add(new LearnValueRow(this, value));
    }

    public LearnWizardViewModel Wizard { get; }
    public LearnCandidate Candidate { get; }
    public byte Code => Candidate.Code;
    public string Title => VcpCode.Describe(Code) is var name && !name.StartsWith("VCP", StringComparison.Ordinal)
        ? $"{Candidate.Label} · MCCS calls it “{name}”" : Candidate.Label;
    public string Facts => $"Now {Candidate.Value.Current} of {Candidate.Value.Maximum}" +
        (Candidate.Advertised ? " · listed by the monitor" : " · answered but not listed by the monitor");
    public uint Maximum => Candidate.Value.Maximum;

    /// <summary>What is known about the code, and where that comes from.</summary>
    public string Description => Candidate.Info.Description;
    public string? Source => Candidate.Info.Source is { } source ? $"Source: {source}" : null;
    public bool HasSource => Candidate.Info.Source is not null;
    public ObservableCollection<LearnValueRow> Values { get; } = [];
    public ObservableCollection<LearnModeRow> Modes { get; } = [];

    // ---- Try ----

    public bool IsLevel { get => kind == LearnKind.Level; set { if (value) Kind = LearnKind.Level; } }
    public bool IsOptions { get => kind == LearnKind.Options; set { if (value) Kind = LearnKind.Options; } }

    public LearnKind Kind
    {
        get => kind;
        set
        {
            if (!Set(ref kind, value)) return;
            Raise(nameof(IsLevel));
            Raise(nameof(IsOptions));
            Changed();
        }
    }

    /// <summary>The value by hand (Try step): written a moment after the slider stops.</summary>
    public double HandValue
    {
        get => handValue;
        set
        {
            value = Math.Round(Math.Clamp(value, 0, Maximum));
            if (Set(ref handValue, value)) Wizard.ScheduleHandWrite(this);
        }
    }

    public string HandText => $"{handValue:0}";

    internal void ShowHand(uint value)
    {
        handValue = value;
        Raise(nameof(HandValue));
        Raise(nameof(HandText));
    }

    public string LastTry { get => lastTry; set => Set(ref lastTry, value); }

    /// <summary>Whether a test value was read back (false: the monitor kept reporting the old one).</summary>
    public bool? ReadBack { get => readBack; set => Set(ref readBack, value); }

    // ---- Describe ----

    public IReadOnlyList<LearnFunction> Functions => LearnFunction.All;

    public LearnFunction? Function
    {
        get => function;
        set
        {
            if (!Set(ref function, value)) return;
            if (value?.Kind is LearnKind known) Kind = known;
            if (value?.Name == "FreeSync") Name(("Off", 0), ("On", 1));
            Raise(nameof(IsOther));
            Raise(nameof(Discards));
            Changed();
        }
    }

    public bool IsOther => function?.IsOther == true;
    public bool Discards => function?.Discards == true;

    public string CustomName
    {
        get => customName;
        set { if (Set(ref customName, value)) Changed(); }
    }

    /// <summary>The setting's name as it will appear in the panel.</summary>
    public string? FinalName => function is null || function.Discards ? null
        : function.IsOther ? (customName.Trim() is { Length: > 0 } name ? name : null) : function.Name;

    /// <summary>What is still missing before the setting can be kept (empty when it is ready).</summary>
    public string Missing => function is null ? "Choose what changed on the screen."
        : function.Discards ? ""
        : FinalName is null ? "Write the name of the setting."
        : kind == LearnKind.Options && !Values.Any(v => v.FinalName is not null) ? "Name at least one value."
        : "";

    public bool IsDescribed => function is not null && Missing.Length == 0;

    private void Name(params (string Name, uint Value)[] names)
    {
        foreach (var (name, value) in names)
            if (Values.FirstOrDefault(v => v.Value == value) is { SelectedName: LearnValueRow.Skip } row) row.SelectedName = name;
    }

    // ---- Modes ----

    public bool ModesChecked { get => modesChecked; set => Set(ref modesChecked, value); }

    /// <summary>The setting cannot be checked by reading it back (write-only), so its modes are not checked.</summary>
    public bool CannotCheckModes => readBack == false;

    public string ModesSummary
    {
        get
        {
            var works = Modes.Where(m => m.Works == true).ToArray();
            if (!modesChecked) return "";
            if (works.Length == 0) return "It kept the change in no picture mode: it will be offered in every mode.";
            if (works.Length == Modes.Count(m => m.Works is not null)) return "Works in every picture mode.";
            return $"Offered only in: {string.Join(", ", works.Select(m => m.Name))}.";
        }
    }

    internal void RaiseModes() => Raise(nameof(ModesSummary));

    public void Changed()
    {
        Raise(nameof(FinalName));
        Raise(nameof(Missing));
        Raise(nameof(IsDescribed));
        Raise(nameof(Summary));
        Wizard.RaiseLabels();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>One line for the Save step.</summary>
    public string Summary => kind == LearnKind.Level
        ? $"Level 0–{Maximum}" + (readBack == false ? " · write-only (the app remembers the last value)" : "")
        : "Options: " + string.Join(", ", Values.Where(v => v.FinalName is not null).Select(v => $"{v.FinalName} ({v.Value})"));

    public LearnedSetting? ToLearned(byte? modeCode)
    {
        if (!IsDescribed || FinalName is not { } name) return null;
        var works = Modes.Where(m => m.Works == true).Select(m => m.Mode).ToArray();
        bool restricted = modesChecked && works.Length > 0 && works.Length < Modes.Count(m => m.Works is not null);
        return new LearnedSetting(Code, name, kind,
            kind == LearnKind.Options ? Values.Where(v => v.FinalName is not null).Select(v => (v.Value, v.FinalName!)).ToArray() : [],
            NotAdvertised: !Candidate.Advertised,
            WriteOnly: readBack == false,
            Modes: restricted ? works : null,
            ModeCode: restricted ? modeCode : null);
    }
}

/// <summary>
/// Learn settings: a guided search for settings the app does not know yet on one display. Find (read-only scan),
/// then for each code found Try (a test value, put back by itself), Describe (what changed: a list, or Other… with a
/// name and values), Modes (automatic: in which picture modes it works), and finally Save (kept for this monitor model).
/// Whatever is left changed is put back when the wizard closes, however it closes.
/// </summary>
public sealed class LearnWizardViewModel : ObservableObject
{
    /// <summary>How long a test value stays on screen before it is put back.</summary>
    public static int HoldSeconds { get; set; } = 4;

    private readonly MainViewModel main;
    private readonly LearnSession session;
    private readonly CancellationTokenSource cancel = new();
    private readonly DispatcherTimer handTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private LearnCandidateViewModel? handCandidate;
    private LearnStep step = LearnStep.Find;
    private int index;
    private bool isBusy;
    private int progress;
    private string activity = "";
    private string? fault;
    private int countdown;
    private bool closed;
    private bool saved;

    public LearnWizardViewModel(MainViewModel main, MonitorViewModel monitor)
    {
        this.main = main;
        Monitor = monitor;
        session = new LearnSession(monitor.Device);
        Steps.Add(new LearnStepItem(LearnStep.Find, "Find"));
        Steps.Add(new LearnStepItem(LearnStep.Try, "Try"));
        Steps.Add(new LearnStepItem(LearnStep.Describe, "Describe"));
        Steps.Add(new LearnStepItem(LearnStep.Modes, "Modes"));
        Steps.Add(new LearnStepItem(LearnStep.Save, "Save"));
        PictureMode = SettingLearner.PictureMode(monitor.Device);
        NextCommand = new RelayCommand(_ => _ = NextAsync(), _ => CanNext);
        BackCommand = new RelayCommand(_ => _ = BackAsync(), _ => CanBack);
        SkipCommand = new RelayCommand(_ => _ = SkipAsync(), _ => CanSkip);
        TryCommand = new RelayCommand(_ => _ = TryLevelAsync(), _ => CanTry);
        PutBackCommand = new RelayCommand(_ => _ = PutBackAsync(), _ => CanTry);
        CheckModesCommand = new RelayCommand(_ => _ = CheckModesAsync(), _ => !isBusy && Current is { CannotCheckModes: false } && PictureMode is not null);
        SaveCommand = new RelayCommand(_ => _ = SaveAsync(), _ => !isBusy && !saved && Learned.Count > 0 && fault is null);
        CloseCommand = new RelayCommand(_ => main.IsDisplaysTab = true);
        handTimer.Tick += async (_, _) =>
        {
            handTimer.Stop();
            if (handCandidate is { } candidate) await WriteHandAsync(candidate);
        };
        UpdateSteps();
        _ = ScanAsync();
    }

    public MonitorViewModel Monitor { get; }
    public string DisplayName => Monitor.Name;
    public MonitorFeature? PictureMode { get; }
    public ObservableCollection<LearnStepItem> Steps { get; } = [];
    public ObservableCollection<LearnCandidateViewModel> Candidates { get; } = [];
    public ObservableCollection<LearnCandidateViewModel> Learned { get; } = [];

    /// <summary>Information the monitor reports (lists, ids, analog-only adjustments): shown, never tried.</summary>
    public ObservableCollection<LearnCandidateViewModel> ReadOnlyFound { get; } = [];
    public bool HasReadOnly => ReadOnlyFound.Count > 0;

    /// <summary>How many codes were left out because they can reboot, reset or rewrite the monitor.</summary>
    public string HiddenText { get; private set; } = "";
    public bool HasHidden => HiddenText.Length > 0;

    public ICommand NextCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand TryCommand { get; }
    public ICommand PutBackCommand { get; }
    public ICommand CheckModesCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CloseCommand { get; }

    public LearnStep Step
    {
        get => step;
        private set
        {
            if (!Set(ref step, value)) return;
            UpdateSteps();
        }
    }

    public bool IsFind => step == LearnStep.Find;
    public bool IsTry => step == LearnStep.Try;
    public bool IsDescribe => step == LearnStep.Describe;
    public bool IsModes => step == LearnStep.Modes;
    public bool IsSave => step == LearnStep.Save;
    /// <summary>Back and Skip only make sense while a found setting is gone through.</summary>
    public bool InCandidate => step is LearnStep.Try or LearnStep.Describe or LearnStep.Modes;

    public LearnCandidateViewModel? Current => index >= 0 && index < Candidates.Count ? Candidates[index] : null;

    /// <summary>"Setting 2 of 3" while the found codes are gone through.</summary>
    public string Position => Current is null || step is LearnStep.Find or LearnStep.Save ? "" : $"Setting {index + 1} of {Candidates.Count}";

    public bool IsBusy
    {
        get => isBusy;
        private set { if (Set(ref isBusy, value)) { Raise(nameof(CanTry)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public int Progress { get => progress; private set => Set(ref progress, value); }
    public string Activity { get => activity; private set => Set(ref activity, value); }
    public int Countdown { get => countdown; private set { if (Set(ref countdown, value)) Raise(nameof(CountdownText)); } }
    public string CountdownText => countdown > 0 ? $"Back to the old value in {countdown} s" : "";

    /// <summary>The monitor stopped answering: what could be put back was; only closing is left.</summary>
    public string? Fault { get => fault; private set { if (Set(ref fault, value)) Raise(nameof(HasFault)); } }
    public bool HasFault => fault is not null;

    public bool ScanDone { get; private set; }
    public bool NothingFound => ScanDone && Candidates.Count == 0;
    public bool CanTry => !isBusy && fault is null && Current is not null;
    public bool IsSaved => saved;

    public string NextLabel => step switch
    {
        LearnStep.Describe when Current?.Discards == true => "Leave it out",
        LearnStep.Modes or LearnStep.Describe when index == Candidates.Count - 1 && !WillCheckModes => "To summary",
        LearnStep.Save => "Done",
        _ => "Next"
    };

    private bool WillCheckModes => step == LearnStep.Describe && Current is { Discards: false, CannotCheckModes: false } && PictureMode is not null
        && PictureMode.Code != Current.Code;

    public string SkipLabel => step == LearnStep.Modes ? "Skip modes" : "Skip this setting";

    private bool CanNext => !isBusy && fault is null && step switch
    {
        LearnStep.Find => ScanDone && Candidates.Count > 0,
        LearnStep.Try => true,
        LearnStep.Describe => Current is { } c && (c.Discards || c.IsDescribed),
        LearnStep.Modes => true,
        LearnStep.Save => saved,
        _ => false
    };

    private bool CanBack => !isBusy && fault is null && step is LearnStep.Try or LearnStep.Describe or LearnStep.Modes;
    private bool CanSkip => !isBusy && fault is null && step is LearnStep.Try or LearnStep.Describe or LearnStep.Modes;

    // ---- Find ----

    private async Task ScanAsync()
    {
        IsBusy = true;
        Activity = "Reading every code the display answers. Nothing is changed.";
        try
        {
            var hidden = new List<LearnCandidate>();
            var all = await Monitor.RunExclusiveAsync(() => SettingLearner.Scan(Monitor.Device,
                code => Dispatch(() => Progress = code), cancel.Token, hidden));
            var found = all.Where(c => c.IsSetting).ToArray();
            foreach (var candidate in found) Candidates.Add(new LearnCandidateViewModel(this, candidate));
            foreach (var info in all.Where(c => !c.IsSetting)) ReadOnlyFound.Add(new LearnCandidateViewModel(this, info));
            if (hidden.Count > 0)
                HiddenText = $"{hidden.Count} code{(hidden.Count == 1 ? "" : "s")} hidden for safety ({string.Join(", ", hidden.Select(h => $"0x{h.Code:X2}"))}): " +
                             "known to reboot or reset the monitor or to rewrite its memory. They are never written.";
            Raise(nameof(HasReadOnly));
            Raise(nameof(HiddenText));
            Raise(nameof(HasHidden));
            Activity = found.Length == 0
                ? "Nothing new: every setting this display answers is already in the app."
                : $"Found {found.Length} possible setting{(found.Length == 1 ? "" : "s")} the app does not know yet. Next tries them one at a time.";
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is not OutOfMemoryException) { Activity = $"The scan stopped: {error.Message}"; }
        finally
        {
            ScanDone = true;
            Progress = 255;
            Raise(nameof(ScanDone));
            Raise(nameof(NothingFound));
            IsBusy = false;
        }
    }

    // ---- Try ----

    private async Task TryLevelAsync()
    {
        if (Current is not { } candidate) return;
        uint now = candidate.Candidate.Value.Current;
        uint size = Math.Max(1, Math.Min(CandidateTrial.Step, candidate.Maximum / 2));
        await TryValueAsync(candidate, CandidateTrial.TargetFor(now, candidate.Maximum, size));
    }

    /// <summary>Shows one test value for a few seconds, then puts the old one back.</summary>
    public async Task TryValueAsync(LearnCandidateViewModel candidate, uint value)
    {
        if (!CanTry) return;
        IsBusy = true;
        var row = candidate.Values.FirstOrDefault(v => v.Value == value);
        if (row is not null) row.IsTrying = true;
        try
        {
            var result = await Monitor.RunExclusiveAsync(() => session.Try(candidate.Code, value));
            candidate.ShowHand(value);
            candidate.ReadBack = result.Matched || candidate.ReadBack == true;
            candidate.LastTry = result.Matched
                ? $"Set to {value}. Look at the screen: did something change?"
                : $"Sent {value}, but the monitor still reports {result.ReadBack?.ToString() ?? "nothing"}. If the picture changed anyway, it is a write-only setting.";
            if (row is not null) row.IsTried = true;
            for (int s = HoldSeconds; s > 0; s--)
            {
                Countdown = s;
                await Task.Delay(1000);
                if (closed) return;
            }
            Countdown = 0;
            await Monitor.RunExclusiveAsync(() => session.Revert(candidate.Code));
            candidate.ShowHand(candidate.Candidate.Value.Current);
            candidate.LastTry += " It is back to the old value now.";
        }
        catch (LearnFaultException error) { Stop(error.Message); }
        catch (Exception error) when (error is not OutOfMemoryException) { candidate.LastTry = $"The test did not run: {error.Message}"; }
        finally
        {
            Countdown = 0;
            if (row is not null) row.IsTrying = false;
            IsBusy = false;
        }
    }

    internal void ScheduleHandWrite(LearnCandidateViewModel candidate)
    {
        handCandidate = candidate;
        handTimer.Stop();
        handTimer.Start();
    }

    private async Task WriteHandAsync(LearnCandidateViewModel candidate)
    {
        if (fault is not null || closed) return;
        uint value = (uint)candidate.HandValue;
        try
        {
            var result = await Monitor.RunExclusiveAsync(() => session.Try(candidate.Code, value));
            candidate.ReadBack = result.Matched || candidate.ReadBack == true;
            candidate.LastTry = $"Set by hand to {value}" + (result.Matched ? "." : $"; the monitor reports {result.ReadBack?.ToString() ?? "nothing"}.") +
                                " Put back, or going on, restores the old value.";
        }
        catch (LearnFaultException error) { Stop(error.Message); }
        catch (Exception error) when (error is not OutOfMemoryException) { candidate.LastTry = $"Not written: {error.Message}"; }
        candidate.ShowHand(value);
    }

    private async Task PutBackAsync()
    {
        if (Current is not { } candidate) return;
        handTimer.Stop();
        IsBusy = true;
        try
        {
            await Monitor.RunExclusiveAsync(() => session.Revert(candidate.Code));
            candidate.ShowHand(candidate.Candidate.Value.Current);
            candidate.LastTry = "Back to the old value.";
        }
        finally { IsBusy = false; }
    }

    // ---- Modes ----

    private async Task CheckModesAsync()
    {
        if (Current is not { } candidate || PictureMode is not { } mode) return;
        IsBusy = true;
        candidate.Modes.Clear();
        foreach (uint value in mode.AllowedValues) candidate.Modes.Add(new LearnModeRow(value, mode.Definition.DescribeValue(value)));
        if (candidate.Modes.Count > 0) candidate.Modes[0].Status = "Checking…";
        Activity = $"Switching through {candidate.Modes.Count} picture modes and trying the setting in each. The screen may flicker; the original mode comes back at the end.";
        try
        {
            await Monitor.RunExclusiveAsync(() => session.CheckModes(candidate.Code, candidate.Maximum, result => Dispatch(() =>
            {
                if (candidate.Modes.FirstOrDefault(m => m.Mode == result.Mode) is { } row)
                {
                    row.Works = result.Works;
                    row.Status = result.Detail;
                }
                int next = candidate.Modes.IndexOf(candidate.Modes.First(m => m.Mode == result.Mode)) + 1;
                if (next < candidate.Modes.Count) candidate.Modes[next].Status = "Checking…";
            }), cancel.Token));
            candidate.ModesChecked = true;
            Activity = "Done: the original picture mode and value are back.";
        }
        catch (OperationCanceledException) { return; }
        catch (LearnFaultException error) { Stop(error.Message); }
        catch (Exception error) when (error is not OutOfMemoryException) { Activity = $"The check stopped: {error.Message}"; }
        finally
        {
            candidate.RaiseModes();
            IsBusy = false;
        }
    }

    // ---- Moving between steps ----

    private async Task NextAsync()
    {
        switch (step)
        {
            case LearnStep.Find:
                index = 0;
                Step = LearnStep.Try;
                break;
            case LearnStep.Try:
                await PutBackQuietlyAsync();
                Step = LearnStep.Describe;
                break;
            case LearnStep.Describe:
                if (Current is { Discards: true }) { await NextCandidateAsync(); break; }
                if (WillCheckModes)
                {
                    Step = LearnStep.Modes;
                    if (Current is { ModesChecked: false }) await CheckModesAsync();
                }
                else await NextCandidateAsync();
                break;
            case LearnStep.Modes:
                await NextCandidateAsync();
                break;
            case LearnStep.Save:
                main.IsDisplaysTab = true;
                break;
        }
        UpdateSteps();
    }

    private async Task BackAsync()
    {
        await PutBackQuietlyAsync();
        switch (step)
        {
            case LearnStep.Try when index == 0: Step = LearnStep.Find; break;
            case LearnStep.Try: index--; Step = LearnStep.Describe; break;
            case LearnStep.Describe: Step = LearnStep.Try; break;
            case LearnStep.Modes: Step = LearnStep.Describe; break;
        }
        UpdateSteps();
    }

    private async Task SkipAsync()
    {
        if (step == LearnStep.Modes && Current is { } candidate)
        {
            candidate.ModesChecked = false;
            candidate.Modes.Clear();
        }
        else if (Current is { } skipped) skipped.Function = LearnFunction.All[0];   // left out
        await NextCandidateAsync();
        UpdateSteps();
    }

    private async Task NextCandidateAsync()
    {
        await PutBackQuietlyAsync();
        if (index < Candidates.Count - 1)
        {
            index++;
            Step = LearnStep.Try;
            return;
        }
        Learned.Clear();
        foreach (var candidate in Candidates.Where(c => c.ToLearned(PictureMode?.Code) is not null)) Learned.Add(candidate);
        Step = LearnStep.Save;
        Activity = Learned.Count == 0
            ? "Nothing to keep: every setting was left out. Close the wizard, or go back to describe one."
            : "Check the list, then Save: the settings appear in the panel like the others, for every display of this model.";
    }

    private async Task PutBackQuietlyAsync()
    {
        handTimer.Stop();
        if (session.Changed.Count == 0 || fault is not null) return;
        IsBusy = true;
        try { await Monitor.RunExclusiveAsync(session.RevertAll); }
        finally { IsBusy = false; }
        Current?.ShowHand(Current.Candidate.Value.Current);
    }

    // ---- Save ----

    private async Task SaveAsync()
    {
        var settings = Learned.Select(c => c.ToLearned(PictureMode?.Code)).OfType<LearnedSetting>().ToArray();
        if (settings.Length == 0) return;
        IsBusy = true;
        try
        {
            await PutBackQuietlyAsync();
            await Task.Run(() => LearnedSettingsStore.Save(Monitor.Device, settings));
            saved = true;
            Raise(nameof(IsSaved));
            Activity = $"Saved {settings.Length} setting{(settings.Length == 1 ? "" : "s")} for {Monitor.Name}. Done goes back to the display, with them in its panel.";
            main.Report($"Learned {string.Join(", ", settings.Select(s => s.Name))} for {Monitor.Name}.", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            Activity = $"Could not save: {error.Message}";
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(NextLabel));
        }
    }

    // ---- Ending ----

    /// <summary>Puts back everything still changed (called when the wizard's view is left, however it is left).</summary>
    public async Task CloseAsync()
    {
        if (closed) return;
        closed = true;
        handTimer.Stop();
        cancel.Cancel();
        try { await Monitor.RunExclusiveAsync(session.RevertAll); }
        catch (Exception error) when (error is not OutOfMemoryException) { main.Report($"Learn settings: could not put everything back — {error.Message}", false); }
        if (saved) await main.RefreshAsync();   // the displays pick up what was learned
    }

    /// <summary>The app is closing: put back at once, on this thread.</summary>
    public void CloseNow()
    {
        if (closed) return;
        closed = true;
        cancel.Cancel();
        try { session.RevertAll(); } catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    internal void RaiseLabels()
    {
        Raise(nameof(NextLabel));
        Raise(nameof(SkipLabel));
    }

    private void Stop(string message)
    {
        Fault = message;
        Activity = message;
        main.Report(message, false);
        CommandManager.InvalidateRequerySuggested();
    }

    private void UpdateSteps()
    {
        foreach (var item in Steps)
        {
            item.IsCurrent = item.Step == step;
            item.IsDone = item.Step < step;
        }
        Raise(nameof(Step));
        Raise(nameof(IsFind));
        Raise(nameof(IsTry));
        Raise(nameof(IsDescribe));
        Raise(nameof(IsModes));
        Raise(nameof(IsSave));
        Raise(nameof(InCandidate));
        Raise(nameof(Current));
        Raise(nameof(Position));
        Raise(nameof(NextLabel));
        Raise(nameof(SkipLabel));
        Raise(nameof(CanTry));
        CommandManager.InvalidateRequerySuggested();
    }

    private static void Dispatch(Action action) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
}
