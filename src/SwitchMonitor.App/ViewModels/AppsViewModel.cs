using System.Collections.ObjectModel;
using System.Windows.Media;
using SwitchMonitor.App.Automation;
using SwitchMonitor.Core.Scenes;

namespace SwitchMonitor.App.ViewModels;

/// <summary>A program of the Start menu, shown as a label with its icon.</summary>
public sealed record AppChip(string Exe, string Name, string Path, ImageSource? Icon, bool IsSystem = false)
{
    private readonly (Brush Fill, Brush Ink) colors = AppIcons.PillColors(Icon);

    /// <summary>The pill's color: the one that stands out in the program's icon.</summary>
    public Brush Fill => colors.Fill;
    public Brush Ink => colors.Ink;

    /// <summary>How the program opens on its display (see <see cref="OpenMode"/>); only used in a display's column.</summary>
    public string Mode { get; init; } = OpenMode.Normal;
}

/// <summary>One column of the Apps tab: the programs available (<see cref="Key"/> null) or the programs that open on one display.</summary>
public sealed class AppColumn(string title, string? key, string glyph, string? subtitle = null) : ObservableObject
{
    /// <summary>Icon of the column's title: the programs, a laptop screen or a monitor.</summary>
    public string Glyph { get; } = glyph;
    public string Title { get; } = title;
    public string? Subtitle { get; } = subtitle;
    /// <summary>Key of the local display, or null for the list of available programs.</summary>
    public string? Key { get; } = key;
    public bool IsPool => Key is null;
    public ObservableCollection<AppChip> Items { get; } = [];
    public string Hint => IsPool ? "" : "Drop a program here: it opens on this display.";
    public bool IsEmpty => Items.Count == 0;
    public void Touch() => Raise(nameof(IsEmpty));
}

/// <summary>
/// The Apps tab: the programs installed in one column and one column per display of this computer. Dragging a program to a
/// display makes it open there; dragging it back to the list frees it. Remote displays are not listed: windows can only be
/// moved on this computer.
/// </summary>
public sealed class AppsViewModel : ObservableObject
{
    private readonly MainViewModel main;
    private readonly AppPlacementStore store;
    private readonly AppUsage usage;
    private IReadOnlyList<AppChip> installed = [];
    private string category = "All";
    private string search = "";
    private bool loading;

    public AppsViewModel(MainViewModel main, AppPlacementStore store, AppUsage usage)
    {
        this.main = main;
        this.store = store;
        this.usage = usage;
        main.Monitors.CollectionChanged += (_, _) => Reload();
    }

    public ObservableCollection<AppColumn> Columns { get; } = [];

    public string Search { get => search; set { if (Set(ref search, value)) Rebuild(); } }
    /// <summary>Which programs the list shows: All, Used (the most used), System (Windows' own) or User (installed by the person).</summary>
    public string Category { get => category; set { if (Set(ref category, value)) { Raise(nameof(IsAll)); Raise(nameof(IsUsed)); Raise(nameof(IsSystem)); Raise(nameof(IsUser)); Rebuild(); } } }
    public bool IsAll { get => category == "All"; set { if (value) Category = "All"; } }
    public bool IsUsed { get => category == "Used"; set { if (value) Category = "Used"; } }
    public bool IsSystem { get => category == "System"; set { if (value) Category = "System"; } }
    public bool IsUser { get => category == "User"; set { if (value) Category = "User"; } }

    public bool IsLoading { get => loading; private set => Set(ref loading, value); }

    /// <summary>Reads the programs once (in the background) and lays the columns out; called when the tab opens.</summary>
    public async void Reload()
    {
        if (installed.Count == 0 && !IsLoading)
        {
            IsLoading = true;
            try
            {
                var list = await InstalledApps.AllAsync();
                installed = await Task.Run(() =>
                {
                    var chips = list.Select(a => new AppChip(a.Exe, a.Name, a.Path, AppIcons.IconOf(a.Path), a.IsSystem)).ToList();
                    // This program too: it is not in the Start menu list (that list leaves it out for linking profiles).
                    if (Environment.ProcessPath is { } own && File.Exists(own) && SceneStore.AppName(own) is { Length: > 0 } ownExe
                        && chips.All(c => !string.Equals(c.Exe, ownExe, StringComparison.OrdinalIgnoreCase)))
                        chips.Insert(0, new AppChip(ownExe, "SwitchMonitor", own, AppIcons.IconOf(own)));
                    return (IReadOnlyList<AppChip>)chips.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
            finally { IsLoading = false; }
        }
        Rebuild();
    }

    /// <summary>The displays of this computer with a picture now (the other computers' displays are not here).</summary>
    private MonitorViewModel[] LocalDisplays() =>
        main.Monitors.Where(m => !m.IsRemote && m.IsActive && !string.IsNullOrEmpty(m.Device.Identity.GdiName)).ToArray();

    private void Rebuild()
    {
        var displays = LocalDisplays();
        var keys = displays.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assigned = store.All;

        var pool = new AppColumn("Apps", null, "");
        string filter = search.Trim();
        IEnumerable<AppChip> source = category switch
        {
            "System" => installed.Where(a => a.IsSystem),
            "User" => installed.Where(a => !a.IsSystem),
            "Used" => installed.Where(a => usage.CountOf(a.Exe) > 0).OrderByDescending(a => usage.CountOf(a.Exe)),
            _ => installed
        };
        foreach (var app in source)
        {
            if (assigned.TryGetValue(app.Exe, out var place) && keys.Contains(place.Display)) continue;   // a program of a display not connected now stays in the list
            if (filter.Length > 0 && !app.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)) continue;
            pool.Items.Add(app);
        }
        var columns = new List<AppColumn> { pool };
        foreach (var display in displays)
        {
            var column = new AppColumn(display.Name, display.Key, display.DisplayGlyph, display.Device.Identity.IsBuiltIn ? "This computer's screen" : null);
            foreach (var app in installed.Where(a => assigned.TryGetValue(a.Exe, out var place) && string.Equals(place.Display, display.Key, StringComparison.OrdinalIgnoreCase)
                         && (filter.Length == 0 || a.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))))
                column.Items.Add(app with { Mode = assigned[app.Exe].Mode });
            columns.Add(column);
        }
        Columns.Clear();
        foreach (var column in columns) Columns.Add(column);
    }

    /// <summary>A program dropped on a column: on a display it opens there from now on; on the list it opens wherever Windows puts it.</summary>
    public void Move(AppChip app, AppColumn target)
    {
        store.Set(app.Exe, target.Key);
        Rebuild();
    }

    /// <summary>The mode chosen in a pill's selector.</summary>
    public void SetMode(AppChip app, string mode) => store.SetMode(app.Exe, mode);

    public IReadOnlyList<OpenModeOption> Modes => OpenMode.Options;

    /// <summary>The work area (screen pixels) of a display of this computer, by its key; null when it is not connected.</summary>
    public (int Left, int Top, int Width, int Height)? AreaOf(string key)
    {
        var monitor = main.Monitors.FirstOrDefault(m => !m.IsRemote && string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        if (monitor is null) return null;
        string gdi = monitor.Device.Identity.GdiName;
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => string.Equals(s.DeviceName, gdi, StringComparison.OrdinalIgnoreCase));
        return screen is null ? null : (screen.WorkingArea.Left, screen.WorkingArea.Top, screen.WorkingArea.Width, screen.WorkingArea.Height);
    }
}
