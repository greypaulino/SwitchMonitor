using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.App.Services;
using SwitchMonitor.Core.Inputs;

namespace SwitchMonitor.App.ViewModels;

/// <summary>One display in the sun panel: its brightness slider (the same one the window shows), or an OFF row.</summary>
public sealed class SunRow : ObservableObject
{
    private readonly SunPanelViewModel owner;
    private SliderControlViewModel? slider;
    private bool isSelected, isMain;

    public SunRow(MonitorViewModel monitor, SunPanelViewModel owner)
    {
        Monitor = monitor;
        this.owner = owner;
        SelectCommand = new RelayCommand(() => owner.Select(this));
        MakeMainCommand = new RelayCommand(() => owner.MakeMain(this));
        Attach();
    }

    public MonitorViewModel Monitor { get; }
    public string Name => Monitor.Name;
    public string Glyph => Monitor.DisplayGlyph;

    /// <summary>Windows is not using the display (or it cannot be controlled): a compact OFF row, skipped by selection.</summary>
    public bool IsOff => !Monitor.IsControllable;
    public bool IsOn => !IsOff;

    public SliderControlViewModel? Slider => slider;
    public bool IsReady => slider is not null && !IsOff;

    public ICommand SelectCommand { get; }
    public ICommand MakeMainCommand { get; }

    /// <summary>The display + and - act on (highlighted, green dot).</summary>
    public bool IsSelected { get => isSelected; set { if (Set(ref isSelected, value)) Raise(nameof(IsLit)); } }

    /// <summary>Green dot: the selected display, or every display while they are linked.</summary>
    public bool IsLit => !IsOff && (isSelected || owner.Linked);
    internal void RaiseLit() => Raise(nameof(IsLit));

    /// <summary>Windows main display (circled M, filled).</summary>
    public bool IsMain { get => isMain; set => Set(ref isMain, value); }
    public bool CanBeMain => owner.HasSeveral && MainDisplay.CanBeMain(Monitor.Device.Identity.GdiName);
    public string MainToolTip => IsMain ? "Main display of Windows"
        : CanBeMain ? "Make this the main display of Windows" : "Cannot be the main display in the current mode";
    internal void RaiseMain() { Raise(nameof(CanBeMain)); Raise(nameof(MainToolTip)); }

    private string? typed;

    /// <summary>Digits typed for this display (applied a moment after the last one); null when not typing.</summary>
    public string? Typed { get => typed; set { if (Set(ref typed, value)) { Raise(nameof(IsTyping)); Raise(nameof(ValueText)); } } }
    public bool IsTyping => typed is not null;

    /// <summary>Monitors with inputs get the inputs button (laptop panels have none).</summary>
    public bool HasInputs => IsOn && Monitor.HasInputs;
    public ObservableCollection<InputViewModel> Inputs => Monitor.Inputs;

    public string ValueText => typed is { } digits ? $"{digits}%" : IsOff ? "OFF" : slider?.ValueText ?? "…";

    /// <summary>The slider's position; moving it here also moves the linked displays.</summary>
    public double Level
    {
        get => slider?.Shown ?? 0;
        set
        {
            if (slider is null) return;
            slider.Shown = value;
            owner.Follow(this);
        }
    }

    public int Percent => (int)Math.Round(slider?.Value ?? 0);

    /// <summary>Moves to <paramref name="percent"/> (shortcut, wheel, typed value) and lets the linked displays follow.</summary>
    public void MoveTo(int percent)
    {
        if (slider is null) return;
        slider.SetTarget(Math.Clamp(percent, 0, 100));
        owner.Follow(this);
    }

    /// <summary>Picks up the brightness control once the display has been read.</summary>
    public void Attach()
    {
        var found = IsOff ? null : Monitor.Brightness;
        if (ReferenceEquals(found, slider)) return;
        if (slider is not null) slider.PropertyChanged -= OnSliderChanged;
        slider = found;
        if (slider is not null) slider.PropertyChanged += OnSliderChanged;
        Raise(nameof(Slider));
        Raise(nameof(HasInputs));
        Raise(nameof(IsReady));
        Raise(nameof(Level));
        Raise(nameof(ValueText));
    }

    public void Detach()
    {
        if (slider is not null) slider.PropertyChanged -= OnSliderChanged;
    }

    private void OnSliderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SliderControlViewModel.Shown)) Raise(nameof(Level));
        if (e.PropertyName is nameof(SliderControlViewModel.ValueText) or nameof(SliderControlViewModel.Value)) Raise(nameof(ValueText));
    }
}

/// <summary>
/// The sun panel (tray), with the 1.x brightness panel's behavior: one slider per display (OFF rows for displays
/// Windows is not using), the selected display highlighted (hover or * moves it), + and - step its brightness (taps one
/// point, quick taps to marks of five, held to marks of ten), digits type a value (applied one second after the last
/// digit, Enter at once, Esc cancels), * held one second links or unlinks, the chain shows one shared slider for linked
/// displays (linking first raises every display to the highest level), a circled M marks and changes the Windows main
/// display (M held half a second moves it to the selected display), and the profiles of the selected display.
/// </summary>
/// <summary>One picture mode in the side list; IsPicked marks the one chosen (sent 1 second after the pick).</summary>
public sealed class ModeItem(ChoiceOption option) : ObservableObject
{
    private bool isPicked;
    public ChoiceOption Option { get; } = option;
    public string Label => Option.Label;
    public string? Glyph => Option.Glyph;
    public bool HasGlyph => Option.HasGlyph;
    public bool IsPicked { get => isPicked; set => Set(ref isPicked, value); }
}

public sealed class SunPanelViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly BrightnessSteps steps = new();
    private readonly DispatcherTimer typingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer modeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ChoiceOption? pendingMode;
    private bool following;
    private SunRow? selected;
    private SunRow? typingRow;

    public SunPanelViewModel(MainViewModel main)
    {
        this.main = main;
        OpenWindowCommand = new RelayCommand(() => OpenWindowRequested?.Invoke());
        typingTimer.Tick += (_, _) => CommitTyped();
        modeTimer.Tick += (_, _) => CommitMode();
        main.Monitors.CollectionChanged += (_, _) => Rebuild();
        main.Scenes.CollectionChanged += (_, _) => WatchScenes();
        main.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.Selected)) SyncSelection(); };
        WatchScenes();
        Rebuild();
    }

    public ObservableCollection<SunRow> Rows { get; } = [];
    public ObservableCollection<SceneViewModel> Scenes => main.Scenes;
    public ICommand OpenWindowCommand { get; }
    public event Action? OpenWindowRequested;

    /// <summary>Raised when the highlight should move (selection or layout changed).</summary>
    public event Action? SelectionMoved;

    private IEnumerable<SunRow> OnRows => Rows.Where(r => r.IsOn);
    public bool HasRows => Rows.Count > 0;

    /// <summary>Linking and the main display need two displays Windows is using.</summary>
    public bool HasSeveral => OnRows.Count() > 1;

    public SunRow? Selected => selected;

    // ---- Picture mode dropdown (Custom only) ----

    private ChoiceControlViewModel? watchedMode;

    private static ChoiceControlViewModel? PictureModeOf(MonitorViewModel monitor) => monitor.Controls.OfType<ChoiceControlViewModel>()
        .FirstOrDefault(c => c.Feature.Code == Core.Vcp.VcpCode.DisplayMode || c.Name.Contains("picture mode", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The picture mode shown: the selected display's, or, when it has none (a laptop panel), the first display that has
    /// one. Read once the display's controls are read.
    /// </summary>
    public ChoiceControlViewModel? PictureMode =>
        (selected is { IsOn: true } chosen ? PictureModeOf(chosen.Monitor) : null)
        ?? Rows.Where(r => r.IsOn).Select(r => PictureModeOf(r.Monitor)).FirstOrDefault(m => m is not null);

    /// <summary>Name of the display whose picture modes are listed.</summary>
    public string ModesDisplayName => Rows.FirstOrDefault(r => ReferenceEquals(PictureModeOf(r.Monitor), PictureMode))?.Name ?? "";

    /// <summary>Custom (the person's own settings) is in use: the picture mode can be chosen here.</summary>
    public bool IsCustomActive => Scenes.FirstOrDefault(s => s.IsCustom)?.IsActive == true;

    public bool ShowsModes => IsCustomActive && PictureMode is { Options.Count: > 1 };
    public ObservableCollection<ModeItem> ModeItems { get; } = [];

    /// <summary>The mode picked in the dropdown. It is sent to the display 1 second after the last pick.</summary>
    public ChoiceOption? PendingMode
    {
        get => pendingMode;
        set
        {
            if (value is null || ReferenceEquals(value, pendingMode)) return;
            pendingMode = value;
            Raise();
            MarkPicked();
            modeTimer.Stop();
            if (!ReferenceEquals(value, watchedMode?.Selected)) modeTimer.Start();
        }
    }

    /// <summary>A picked mode is waiting for its second: the panel stays open.</summary>
    public bool IsModePending => modeTimer.IsEnabled;

    private void MarkPicked()
    {
        foreach (var item in ModeItems) item.IsPicked = ReferenceEquals(item.Option, pendingMode);
    }

    private void CommitMode()
    {
        modeTimer.Stop();
        // To the display it was picked for (watchedMode), even if the selection has moved since.
        if (watchedMode is { } mode && pendingMode is not null && !ReferenceEquals(mode.Selected, pendingMode))
            mode.Selected = pendingMode;
    }

    private void ShowCurrentMode()
    {
        if (modeTimer.IsEnabled) return;
        pendingMode = PictureMode?.Selected;
        Raise(nameof(PendingMode));
        MarkPicked();
    }

    private void RaiseModes()
    {
        if (!ReferenceEquals(watchedMode, PictureMode))
        {
            if (modeTimer.IsEnabled) CommitMode();
            if (watchedMode is not null) watchedMode.PropertyChanged -= OnModeChanged;
            watchedMode = PictureMode;
            if (watchedMode is not null) watchedMode.PropertyChanged += OnModeChanged;
        }
        Raise(nameof(PictureMode));
        Raise(nameof(ModesDisplayName));
        Raise(nameof(IsCustomActive));
        Raise(nameof(ShowsModes));
        var options = PictureMode?.Options;
        if (options is null || ModeItems.Count != options.Count || ModeItems.Where((m, i) => !ReferenceEquals(m.Option, options[i])).Any())
        {
            ModeItems.Clear();
            foreach (var option in options ?? []) ModeItems.Add(new ModeItem(option));
        }
        ShowCurrentMode();
        MarkPicked();
    }

    private void OnModeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChoiceControlViewModel.Selected)) ShowCurrentMode();
    }

    private void WatchScenes()
    {
        foreach (var scene in Scenes)
        {
            scene.PropertyChanged -= OnSceneChanged;
            scene.PropertyChanged += OnSceneChanged;
        }
        RaiseModes();
    }

    private void OnSceneChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SceneViewModel.IsActive)) RaiseModes();
    }

    // ---- Linked ----

    /// <summary>Moving one display's brightness moves the others to the same level (saved in settings.json).</summary>
    public bool Linked
    {
        get => main.Settings.LinkBrightness && HasSeveral;
        set
        {
            if (Linked == value || (value && !HasSeveral)) return;
            CancelTyped();
            if (value) MatchHighest();
            main.UpdateSettings(main.Settings with { LinkBrightness = value }, shortcutsChanged: false);
            RaiseLinked();
            main.Report(value ? "Brightness linked: every display follows." : "Brightness unlinked: each display on its own.", true);
        }
    }

    public bool Unlinked => !Linked;

    /// <summary>Linked: the first display's slider stands for all of them.</summary>
    public SunRow? LeadRow => OnRows.FirstOrDefault(r => r.IsReady) ?? OnRows.FirstOrDefault();
    public IEnumerable<SunRow> LinkedRows => OnRows;

    public string LinkToolTip => Linked ? "Linked: one slider for every display. Click (or hold *) to unlink."
        : "Not linked: each display moves on its own. Click (or hold *) to link them.";

    private void RaiseLinked()
    {
        Raise(nameof(Linked));
        Raise(nameof(Unlinked));
        Raise(nameof(LinkToolTip));
        Raise(nameof(LeadRow));
        Raise(nameof(LinkedRows));
        foreach (var row in Rows) row.RaiseLit();
        SelectionMoved?.Invoke();
    }

    /// <summary>Linking starts from the brightest display, so nothing gets darker by surprise (1.9.0).</summary>
    private void MatchHighest()
    {
        var ready = OnRows.Where(r => r.IsReady).ToArray();
        if (ready.Length == 0) return;
        int highest = ready.Max(r => r.Percent);
        following = true;
        try { foreach (var row in ready.Where(r => r.Percent < highest)) row.MoveTo(highest); }
        finally { following = false; }
    }

    /// <summary>Linked: the other displays take the level of <paramref name="lead"/>.</summary>
    public void Follow(SunRow lead)
    {
        if (!Linked || following || lead.Slider is not { } source) return;
        following = true;
        try
        {
            foreach (var row in OnRows.Where(r => r != lead && r.Slider is not null))
                row.Slider!.SetTarget((int)Math.Round(source.Value));
        }
        finally { following = false; }
    }

    // ---- Selection ----

    /// <summary>Hover or click: this display is the one + and - and the shortcuts act on.</summary>
    public void Select(SunRow row)
    {
        if (row.IsOff || row == selected) return;
        CommitTyped();
        main.Selected = row.Monitor;   // the brightness shortcuts follow it too (1.x)
    }

    /// <summary>* tapped: the next display Windows is using.</summary>
    public void SelectNext()
    {
        var on = OnRows.ToList();
        if (on.Count == 0) return;
        int index = selected is null ? -1 : on.IndexOf(selected);
        Select(on[(index + 1) % on.Count]);
    }

    private void SyncSelection()
    {
        var target = Rows.FirstOrDefault(r => r.Monitor == main.BrightnessTarget && r.IsOn) ?? OnRows.FirstOrDefault();
        if (target == selected && target?.IsSelected == true) return;
        foreach (var row in Rows) row.IsSelected = row == target;
        selected = target;
        Raise(nameof(Selected));
        RaiseModes();
        SelectionMoved?.Invoke();
    }

    // ---- Keyboard ----

    private SunRow? ActingRow => Linked ? LeadRow : selected;

    /// <summary>+ or - pressed (not held): one point, or the next mark of five on the third quick tap.</summary>
    public void Tap(int sign)
    {
        CancelTyped();
        if (ActingRow is not { IsReady: true } row) return;
        row.MoveTo(steps.Tap(row.Percent, sign, DateTime.UtcNow));
    }

    /// <summary>+ or - held: marks of five, then of ten.</summary>
    public void Hold(int sign, TimeSpan held)
    {
        if (ActingRow is not { IsReady: true } row || held < BrightnessSteps.HoldFirst) return;
        row.MoveTo(BrightnessSteps.Hold(row.Percent, sign, held));
    }

    /// <summary>A digit: typing a value for the selected display (0-100).</summary>
    public void Digit(int digit)
    {
        if (ActingRow is not { IsReady: true } row) return;
        if (typingRow is not null && typingRow != row) CancelTyped();
        string candidate = (row.Typed ?? "") + digit;
        if (candidate.Length > 3 || int.Parse(candidate) > 100) return;
        if (candidate.Length > 1 && candidate[0] == '0') candidate = candidate.TrimStart('0') is { Length: > 0 } t ? t : "0";
        typingRow = row;
        row.Typed = candidate;
        typingTimer.Stop();
        typingTimer.Start();   // applied one second after the last digit (1.10.0)
    }

    public bool IsTyping => typingRow is not null;

    /// <summary>Enter, or a second without another digit: apply the typed value.</summary>
    public void CommitTyped()
    {
        typingTimer.Stop();
        if (typingRow is not { Typed: { } digits } row) { typingRow = null; return; }
        typingRow = null;
        row.Typed = null;
        row.MoveTo(int.Parse(digits));
    }

    /// <summary>Esc: forget the typed value. Returns false when nothing was being typed.</summary>
    public bool CancelTyped()
    {
        typingTimer.Stop();
        if (typingRow is null) return false;
        typingRow.Typed = null;
        typingRow = null;
        return true;
    }

    // ---- Main display ----

    public void MakeMain(SunRow row)
    {
        string? name = row.Monitor.Device.Identity.GdiName;
        if (string.IsNullOrEmpty(name) || MainDisplay.IsMain(name)) return;
        try
        {
            MainDisplay.Set(name);
            main.Report($"{row.Monitor.Name} is now the main display.", true);
        }
        catch (InvalidOperationException error) { main.Report($"Main display: {error.Message}", false); }
        RefreshMain();
    }

    /// <summary>M held: the selected display becomes the main one (or, when it already is, the next one that can).</summary>
    public void SwitchMain()
    {
        if (!HasSeveral) return;
        var target = selected is { CanBeMain: true, IsMain: false } chosen ? chosen : OnRows.FirstOrDefault(r => r.CanBeMain && !r.IsMain);
        if (target is not null) MakeMain(target);
    }

    public void RefreshMain()
    {
        foreach (var row in Rows)
        {
            row.IsMain = MainDisplay.IsMain(row.Monitor.Device.Identity.GdiName);
            row.RaiseMain();
        }
    }

    // ---- Opening ----

    /// <summary>Called when the panel opens: read the displays not read yet, then show their sliders.</summary>
    public async Task RefreshAsync()
    {
        SyncSelection();
        RefreshMain();
        RaiseLinked();
        foreach (var row in Rows.Where(r => r.IsOn).ToArray())
        {
            row.Attach();
            if (row.IsReady) continue;
            try { await row.Monitor.EnsureLoadedAsync(); } catch (Exception error) when (error is not OutOfMemoryException) { }
            row.Attach();
        }
        Raise(nameof(LeadRow));
        RaiseModes();
    }

    public void Closed()
    {
        CommitTyped();
        if (modeTimer.IsEnabled) CommitMode();   // closing does not lose a picked mode
    }

    private void Rebuild()
    {
        foreach (var row in Rows) row.Detach();
        Rows.Clear();
        selected = null;
        foreach (var monitor in main.Monitors) Rows.Add(new SunRow(monitor, this));
        Raise(nameof(HasRows));
        Raise(nameof(HasSeveral));
        SyncSelection();
        RaiseLinked();
    }
}
