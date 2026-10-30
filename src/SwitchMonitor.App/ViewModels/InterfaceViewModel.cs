using System.Collections.ObjectModel;
using System.Windows.Input;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App.ViewModels;

/// <summary>One interface color suggestion (Settings view).</summary>
public sealed class InterfaceColorChoice(string name, string hex, InterfaceViewModel owner) : ObservableObject
{
    private bool isSelected;
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public System.Windows.Media.Brush Brush { get; } = Icons.Brush(hex);
    public string ToolTip => $"{Name} ({Hex})";
    public bool IsSelected { get => isSelected; set => Set(ref isSelected, value); }
    public ICommand SelectCommand => new RelayCommand(() => owner.Color = Hex);
}

/// <summary>One icon style offered for the panel, with a sample drawn in it.</summary>
public sealed class InterfaceIconStyle(string key, string name, string description, InterfaceViewModel owner) : ObservableObject
{
    private static readonly string[] SampleIds = ["sun", "palette", "game", "settings"];
    private readonly Icons.Mark[] sample = SampleIds.Select(id => Icons.LibraryMark(key, id)!).ToArray();

    public string Key { get; } = key;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string SampleText => string.Join("  ", sample.Select(m => m.Text));
    public string? SampleBack => sample[0].Secondary is null ? null : string.Join("  ", sample.Select(m => m.Secondary));
    public System.Windows.Media.FontFamily SampleFont => sample[0].Font;
    public bool IsSelected
    {
        get => owner.IconStyle == Key;
        set { if (value) owner.IconStyle = Key; }
    }
    public void Refresh() => Raise(nameof(IsSelected));
}

/// <summary>
/// The Settings view: interface color, appearance and whether linked apps switch profiles. Changes show at once as a
/// preview; Save keeps them (interface.json, automation.json), Discard or leaving the view puts back what was saved.
/// </summary>
public sealed class InterfaceViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private InterfaceSettings saved;
    private InterfaceSettings look;
    private bool linkedApps;

    public InterfaceViewModel(InterfaceSettings saved, MainViewModel main)
    {
        this.main = main;
        this.saved = look = saved;
        linkedApps = main.LinkedAppsEnabled;
        foreach (var (name, hex) in Theme.Presets) Colors.Add(new InterfaceColorChoice(name, hex, this));
        foreach (var (key, name, description) in Icons.IconStyles) IconStyleOptions.Add(new InterfaceIconStyle(key, name, description, this));
        SaveCommand = new RelayCommand(_ => Save(), _ => IsDirty);
        DiscardCommand = new RelayCommand(_ => Discard(), _ => IsDirty);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults);
        Refresh();
    }

    public ObservableCollection<InterfaceColorChoice> Colors { get; } = [];
    public ICommand SaveCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand RestoreDefaultsCommand { get; }

    /// <summary>"#RRGGBB": the hue of the window's ground (gray: none).</summary>
    public string Color
    {
        get => look.Color;
        set
        {
            if (InterfaceSettings.NormalizeColor(value) is not { } hex || hex == look.Color) return;
            Preview(look with { Color = hex });
        }
    }

    /// <summary>The color was picked freely: the "any color" swatch shows it, ringed.</summary>
    public bool IsCustomColor => !Colors.Any(c => c.Hex == look.Color);
    public System.Windows.Media.Brush CustomColorBrush => IsCustomColor ? Icons.Brush(look.Color) : System.Windows.Media.Brushes.Transparent;
    public string ColorName => Colors.FirstOrDefault(c => c.Hex == look.Color)?.Name ?? look.Color;

    public bool IsGlossy { get => look.Appearance == Appearance.Glossy; set { if (value) Preview(look with { Appearance = Appearance.Glossy }); } }
    public bool IsTranslucent { get => look.Appearance == Appearance.Translucent; set { if (value) Preview(look with { Appearance = Appearance.Translucent }); } }
    public bool IsMatte { get => look.Appearance == Appearance.Matte; set { if (value) Preview(look with { Appearance = Appearance.Matte }); } }

    /// <summary>Styles of the panel's icons (Appearance), each with a sample of four icons drawn in it.</summary>
    public ObservableCollection<InterfaceIconStyle> IconStyleOptions { get; } = [];

    public string IconStyle
    {
        get => look.IconStyle;
        set { if (look.IconStyle != value) Preview(look with { IconStyle = value }); }
    }

    /// <summary>A soft light follows the pointer over the window.</summary>
    public bool PointerLight
    {
        get => look.PointerLight;
        set { if (look.PointerLight != value) Preview(look with { PointerLight = value }); }
    }

    /// <summary>Linked programs switch profiles (applied on Save).</summary>
    public bool LinkedAppsEnabled
    {
        get => linkedApps;
        set
        {
            if (linkedApps == value) return;
            linkedApps = value;
            Refresh();
        }
    }

    public bool IsDirty => look != saved || linkedApps != main.LinkedAppsEnabled;

    public string SaveHint => IsDirty
        ? "Unsaved changes: you see them now; Save keeps them, Discard puts back the saved look."
        : "Saved. Changes show at once; Save keeps them for the next start.";

    private void Preview(InterfaceSettings updated)
    {
        look = updated;
        Theme.Apply(updated);
        main.RefreshProfileMarks();
        Refresh();
    }

    private void Save()
    {
        look.Save();
        saved = look;
        if (main.LinkedAppsEnabled != linkedApps) main.LinkedAppsEnabled = linkedApps;
        main.Report("Settings saved.", true);
        Refresh();
    }

    /// <summary>Back to what was saved (also when the view is left without saving).</summary>
    public void Discard()
    {
        if (!IsDirty) return;
        look = saved;
        linkedApps = main.LinkedAppsEnabled;
        Theme.Apply(saved);
        main.RefreshProfileMarks();
        Refresh();
    }

    /// <summary>Every setting of this view back to its default, as a preview: Save keeps it.</summary>
    private void RestoreDefaults()
    {
        linkedApps = true;
        Preview(new InterfaceSettings());
    }

    private void Refresh()
    {
        foreach (var choice in Colors) choice.IsSelected = choice.Hex == look.Color;
        Raise(nameof(Color));
        Raise(nameof(IsCustomColor));
        Raise(nameof(CustomColorBrush));
        Raise(nameof(ColorName));
        Raise(nameof(IsGlossy));
        Raise(nameof(IsTranslucent));
        Raise(nameof(IsMatte));
        Raise(nameof(PointerLight));
        Raise(nameof(IconStyle));
        foreach (var option in IconStyleOptions) option.Refresh();
        Raise(nameof(LinkedAppsEnabled));
        Raise(nameof(IsDirty));
        Raise(nameof(SaveHint));
        CommandManager.InvalidateRequerySuggested();
    }
}
