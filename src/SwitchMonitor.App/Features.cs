namespace SwitchMonitor.App;

/// <summary>One thing SwitchMonitor does: listed in Settings > About and, by category, in the welcome tour.</summary>
/// <param name="Order">Grows with each feature added; the welcome tour marks the ones above what was seen last.</param>
/// <param name="Page">The Settings page where it is turned on or set up ("" when it has none).</param>
public sealed record FeatureInfo(int Order, string Glyph, string Title, string Text, string Page = "")
{
    public bool HasPage => Page.Length > 0;
}

/// <summary>A general category of features, with the icon that stands for it.</summary>
public sealed record FeatureGroup(string Glyph, string Title, string Text, IReadOnlyList<FeatureInfo> Items)
{
    public bool HasNew(int seen) => Items.Any(f => f.Order > seen);
}

public static class Features
{
    // The text of each is a few words. (Order 10 was the language: not a feature, so it is gone.)
    public static readonly IReadOnlyList<FeatureInfo> All =
    [
        new(1, "\uE7F4", "All your displays", "Brightness, inputs, picture modes"),
        new(2, "\uE790", "Profiles", "One click, a shortcut or a program"),
        new(3, "\uE765", "Keyboard shortcuts", "From anywhere in Windows", "General"),
        new(4, "\uE706", "Tray panel", "Brightness next to the clock", "General"),
        new(5, "\uE8EA", "Control from your phone", "Displays from the couch", "Devices"),
        new(6, "\uE774", "Remote computers", "Displays of your other computers", "Devices"),
        new(7, "\uE8A7", "Apps on a display", "Programs open where you choose"),
        new(8, "\uE962", "Shared mouse and keyboard", "One mouse for every computer", "Devices"),
        new(9, "\uE82F", "Learn your monitor", "Find settings the app lacks", "Monitors"),
        new(11, "\uE8EA", "Phone as mouse and keyboard", "A touchpad in your pocket", "Devices"),
        new(12, "\uE716", "My devices", "Your computers and phones, one group", "Devices"),
        new(13, "\uE16F", "Shared clipboard", "Copy on one device, paste on another", "Devices"),
    ];

    private static FeatureInfo Item(int order) => All.First(f => f.Order == order);

    /// <summary>The same features in five general categories (what the welcome tour and the About page show).</summary>
    public static readonly IReadOnlyList<FeatureGroup> Groups =
    [
        new("\uE7F4", "Your displays", "Brightness, inputs, modes", [Item(1), Item(2), Item(9)]),
        new("\uE765", "Shortcuts and panel", "Keys and a tray panel", [Item(3), Item(4)]),
        new("\uE8A7", "Apps", "Open where you want", [Item(7)]),
        new("\uE716", "Your devices", "One group, connected", [Item(12), Item(13), Item(6)]),
        new("\uE8EA", "Phone, mouse and keyboard", "One mouse for all", [Item(5), Item(11), Item(8)]),
    ];

    public static int Latest => All.Max(f => f.Order);
}
