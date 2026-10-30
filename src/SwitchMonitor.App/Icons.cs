using System.Windows;
using System.Windows.Media;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.App;

/// <summary>
/// Glyphs of the Windows icon font (Segoe MDL2 Assets, present on Windows 10 and 11) for everything the app can name:
/// settings, their values, sections, display types and profiles. One place, so the same thing always has the same icon.
/// </summary>
public static class Icons
{
    // Sections and views
    public const string Displays = "";       // TVMonitor
    public const string Profiles = "";       // Tag
    public const string Shortcuts = "";      // KeyboardClassic
    public const string Input = "";          // Connect
    public const string Settings = "";       // Setting
    public const string ColorTuning = "";    // Color
    public const string LinkedApps = "";     // AllApps
    public const string Missing = "";        // Warning
    public const string NextInput = "";      // Switch
    public const string App = "";            // AllApps (program without its own icon)

    // Display types
    public const string BuiltIn = "";        // DeviceLaptopNoPic
    public const string External = "";       // TVMonitor
    public const string ExternalOff = "";    // DeviceMonitorNoPic

    /// <summary>
    /// Outline of a video connector, drawn in a 24 × 24 box (the Windows icon font has none): HDMI's chamfered
    /// trapezoid, DisplayPort's one cut corner, USB-C's pill, VGA's D-sub with two pin rows, DVI with its flat blade.
    /// </summary>
    public static Geometry ConnectorShape(string label)
    {
        string value = label.ToLowerInvariant();
        string data = value switch
        {
            _ when value.Contains("usb") || value.Contains("type-c") || value.Contains("thunderbolt") =>
                "M7,8.5 H17 A3.5,3.5 0 0 1 17,15.5 H7 A3.5,3.5 0 0 1 7,8.5 Z M9,12 H15",
            _ when value.Contains("displayport") || value.StartsWith("dp") =>
                "M6,7.5 H21.5 V16.5 H2.5 V11 Z M7,12 H17",
            _ when value.Contains("vga") || value.Contains("analog") || value.Contains("d-sub") =>
                "M2.5,7.5 H21.5 L18.5,16.5 H5.5 Z M7,10.5 H17 M8.5,13.5 H15.5",
            _ when value.Contains("dvi") =>
                "M2.5,7.5 H18 V16.5 H2.5 Z M20.5,9.5 V14.5 M5.5,10.5 H15 M5.5,13.5 H15",
            _ => "M2.5,8 H21.5 V13 L18.5,16 H5.5 L2.5,13 Z M6.5,11 H17.5"   // HDMI and anything else
        };
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Icon of a setting, by VCP code (the same code means the same thing on every monitor).</summary>
    public static string ForFeature(FeatureDefinition feature) => feature.Code switch
    {
        VcpCode.Brightness => "",          // Brightness
        VcpCode.Contrast => "",            // FullCircleMask: half light, half dark
        VcpCode.ColorPreset => "",         // Light
        VcpCode.AudioVolume => "",         // Volume
        VcpCode.AudioMute => "",           // Mute
        VcpCode.DisplayMode or 0x15 when IsPictureMode(feature) => "",   // Picture
        0x87 => "",                        // ZoomIn: detail
        0xF7 when Named(feature, "response") => "\uEC4A",   // SpeedHigh: a speedometer
        0xF8 when Named(feature, "freesync") => "",   // Sync
        _ when Named(feature, "black level") => "",
        // Learned settings carry the usual names, so they get the usual icons on any code.
        _ when Named(feature, "brightness") => "\uE706",
        _ when Named(feature, "sharpness") => "\uE8A3",
        _ when Named(feature, "volume") => "\uE767",
        _ when Named(feature, "response time") => "\uEC4A",
        _ when Named(feature, "freesync") => "\uE895",
        _ when Named(feature, "picture mode") => "\uE8B9",
        _ when Named(feature, "color temperature") => "\uE793",
        _ when Named(feature, "gamma") => "",
        _ => ""
    };

    /// <summary>An icon drawn in a 24 × 24 box: an outline (stroked) and an optional filled part.</summary>
    public sealed record DrawnIcon(Geometry Outline, Geometry? Fill)
    {
        public bool HasFill => Fill is not null;
    }

    /// <summary>
    /// Icons the Windows icon font lacks, drawn as on monitor menus and in the common icon sets: contrast is a circle half
    /// light, half dark; sharpness the sharpen triangle (half filled, an edge between light and dark); picture mode a
    /// picture (frame, hills and sun).
    /// </summary>
    public static DrawnIcon? DrawnFor(FeatureDefinition feature) => feature.Code switch
    {
        VcpCode.Contrast => Drawn("M12,4 A8,8 0 1 1 11.99,4 Z", "M12,4 A8,8 0 0 1 12,20 Z"),
        // Color temperature: a thermometer (tube, bulb, and its filled column)
        VcpCode.ColorPreset => Drawn("M10,14.2 V5.5 A2,2 0 0 1 14,5.5 V14.2 A4,4 0 1 1 10,14.2 Z M15.8,6.5 H17.5 M15.8,9.5 H17.5",
            "M12,14.6 m-2.2,0 a2.2,2.2 0 1 0 4.4,0 a2.2,2.2 0 1 0 -4.4,0 Z M11.25,9 H12.75 V15 H11.25 Z"),
        0x87 => Drawn("M12,4 L20.5,19 H3.5 Z", "M12,4 L12,19 H3.5 Z"),
        VcpCode.DisplayMode or 0x15 when IsPictureMode(feature) =>
            Drawn("M3.5,5.5 H20.5 V18.5 H3.5 Z M3.5,16 L9,10.5 L13,14.5 L15.5,12 L20.5,17", "M15,8 A1.75,1.75 0 1 1 14.99,8 Z"),
        _ => null
    };

    private static DrawnIcon Drawn(string outline, string? fill)
    {
        var line = Geometry.Parse(outline);
        line.Freeze();
        Geometry? solid = null;
        if (fill is not null)
        {
            solid = Geometry.Parse(fill);
            solid.Freeze();
        }
        return new DrawnIcon(line, solid);
    }

    /// <summary>Red, green and blue (gains and black levels) are shown as a dot of their color instead of a glyph.</summary>
    public static Color? DotFor(FeatureDefinition feature)
    {
        string name = feature.Name.ToLowerInvariant();
        bool black = name.Contains("black level");
        if (name.EndsWith("red", StringComparison.Ordinal)) return black ? Color.FromRgb(0x8A, 0x2E, 0x2E) : Color.FromRgb(0xFF, 0x5C, 0x5C);
        if (name.EndsWith("green", StringComparison.Ordinal)) return black ? Color.FromRgb(0x2B, 0x6E, 0x3C) : Color.FromRgb(0x4C, 0xD9, 0x7B);
        if (name.EndsWith("blue", StringComparison.Ordinal)) return black ? Color.FromRgb(0x2B, 0x45, 0x8A) : Color.FromRgb(0x5C, 0x9D, 0xFF);
        return null;
    }

    /// <summary>Icon of a value ("Reader", "FPS Game 1", "Warm", "Fast"...), or null when the text says enough.</summary>
    public static string? ForValue(FeatureDefinition feature, string label)
    {
        string value = label.ToLowerInvariant();
        if (IsPictureMode(feature))
            return value switch
            {
                _ when value.Contains("game") || value.StartsWith("fps") || value.StartsWith("rts") => "",   // Game
                _ when value.Contains("custom") || value.Contains("user") => "",                             // Edit
                _ when value.Contains("reader") || value.Contains("read") => "",                            // ReadingMode
                _ when value.Contains("photo") => "",                                                       // Photo
                _ when value.Contains("cinema") || value.Contains("movie") => "",                           // Movies
                _ when value.Contains("hdr") => "",                                                         // Light
                _ when value.Contains("weak") => "",                                                        // View
                _ when value.Contains("productivity") => "",                                                // Work
                _ when value.Contains("sport") => "",                                                       // Walk
                _ => ""                                                                                     // TVMonitor
            };
        if (feature.Code == 0xF7 && Named(feature, "response"))
            return value switch
            {
                // Speed as chevrons: none, one, two, three (faster = more)
                "off" => "\uE738", "normal" => "\uE76C", "fast" => "\uE76C\uE76C", "faster" => "\uE76C\uE76C\uE76C", _ => null
            };
        return null;
    }

    /// <summary>Place of a value in its list when the monitor's order is not the natural one (response time from slow to
    /// fast), or null to keep the monitor's order.</summary>
    public static int? ValueRank(FeatureDefinition feature, string label)
    {
        if (feature.Code != 0xF7 || !Named(feature, "response")) return null;
        return label.ToLowerInvariant() switch { "off" => 0, "normal" => 1, "fast" => 2, "faster" => 3, _ => 4 };
    }

    /// <summary>The name shown for a setting: the volume is the monitor's own speakers, not Windows' volume.</summary>
    public static string DisplayName(FeatureDefinition feature, string name) =>
        feature.Code == VcpCode.AudioVolume ? "Monitor volume" : name;

    /// <summary>Swatch color of a value (Warm / Cool and color temperatures), or null.</summary>
    public static Color? SwatchFor(FeatureDefinition feature, string label)
    {
        if (feature.Code != VcpCode.ColorPreset) return null;
        string value = label.ToLowerInvariant();
        if (value.Contains("warm") || value.StartsWith("4000") || value.StartsWith("5000")) return Color.FromRgb(0xFF, 0xB8, 0x6B);
        if (value.StartsWith("6500") || value.Contains("srgb") || value.Contains("native")) return Color.FromRgb(0xFF, 0xF1, 0xDE);
        if (value.Contains("cool") || value.StartsWith("7500") || value.StartsWith("8200") || value.StartsWith("9300")
            || value.StartsWith("10000") || value.StartsWith("11500")) return Color.FromRgb(0xA9, 0xC8, 0xFF);
        return null;
    }

    /// <summary>Icon of a profile by its usual name; anything else gets a tag.</summary>
    public static string ForProfile(string name)
    {
        string value = name.ToLowerInvariant();
        return value switch
        {
            "custom" => "",                                          // Contact: your own settings
            _ when value.Contains("work") || value.Contains("office") => "",
            _ when value.Contains("night") || value.Contains("sleep") || value.Contains("read") => "",   // QuietHours (moon)
            _ when value.Contains("game") || value.Contains("gaming") => "",
            _ when value.Contains("theater") || value.Contains("cinema") || value.Contains("movie") => "",
            _ when value.Contains("photo") => "",
            _ => ""
        };
    }

    /// <summary>
    /// A glyph as drawn in one icon style: its text and font; Duotone adds a soft second layer (the filled shape, drawn at
    /// low opacity under the outline).
    /// </summary>
    public sealed record Mark(string Text, FontFamily Font, FontWeight Weight, string? Secondary = null)
    {
        public bool HasSecondary => Secondary is not null;
    }

    /// <summary>One icon in every style: Windows icon font (Line), Phosphor Fill and Duotone (two layers), and the
    /// pictograph of Classic (empty when there is none: Line is used then).</summary>
    public sealed record IconDef(string Id, string Name, string Line, string Classic, string Fill, string DuotoneBack,
        string DuotoneFront, bool InLibrary);

    /// <summary>The icon styles, for the panel and for profiles.</summary>
    public static IReadOnlyList<(string Key, string Name, string Description)> IconStyles { get; } =
    [
        ("line", "Line", "Thin outlines (Windows icons)"),
        ("fill", "Fill", "Solid shapes (Phosphor)"),
        ("duotone", "Duotone", "Outline over a soft fill (Phosphor)"),
        ("classic", "Classic", "Pictographs, like emoji in one color")
    ];

    private static readonly FontFamily LineFont = new("Segoe MDL2 Assets");
    private static readonly FontFamily ClassicFont = new("Segoe UI Symbol, Segoe UI Emoji");
    private static readonly FontFamily FillFont = new(new Uri("pack://application:,,,/"), "./Fonts/#Phosphor-Fill");
    private static readonly FontFamily DuotoneFont = new(new Uri("pack://application:,,,/"), "./Fonts/#Phosphor-Duotone");

    /// <summary>Every icon the app can restyle: the profile library and the panel's own icons.</summary>
    public static IReadOnlyList<IconDef> Catalog { get; } =
    [
        new("work", "Work", "\uE821", "\U0001F4BC", "\uE0EE", "\uE0EE", "\uE0EF", true),
        new("night", "Night", "\uE708", "\U0001F319", "\uE330", "\uE330", "\uE331", true),
        new("game", "Game", "\uE7FC", "\U0001F3AE", "\uE26E", "\uE26E", "\uE26F", true),
        new("movie", "Movies", "\uE8B2", "\U0001F3AC", "\uE8C2", "\uE8C2", "\uE8C3", true),
        new("photo", "Photo", "\uE91B", "\U0001F4F7", "\uE10E", "\uE10E", "\uE10F", true),
        new("reading", "Reading", "\uE736", "\U0001F4D6", "\uE0E6", "\uE0E6", "\uE0E7", true),
        new("person", "Person", "\uE77B", "\U0001F464", "\uE4C2", "\uE4C2", "\uE4C3", true),
        new("people", "People", "\uE716", "\U0001F465", "\uE4D6", "\uE4D6", "\uE4D7", true),
        new("sun", "Bright", "\uE706", "\u2600", "\uE472", "\uE472", "\uE473", true),
        new("bulb", "Light", "\uE793", "\U0001F4A1", "\uE2DC", "\uE2DC", "\uE2DD", true),
        new("palette", "Color", "\uE790", "\U0001F3A8", "\uE6C8", "\uE6C8", "\uE6C9", true),
        new("edit", "Edit", "\uE70F", "\u270F", "\uE3B4", "\uE3B4", "\uE3B5", true),
        new("tag", "Tag", "\uE8EC", "\U0001F3F7", "\uE478", "\uE478", "\uE479", true),
        new("sport", "Sport", "\uE805", "\U0001F3C3", "\uE730", "\uE730", "\uE731", true),
        new("monitor", "Monitor", "\uE7F4", "\U0001F5A5", "\uE32E", "\uE32E", "\uE32F", true),
        new("laptop", "Laptop", "\uE7F8", "\U0001F4BB", "\uE586", "\uE586", "\uE587", true),
        new("sound", "Sound", "\uE767", "\U0001F50A", "\uE44A", "\uE44A", "\uE44B", true),
        new("headphones", "Headphones", "\uE7F6", "\U0001F3A7", "\uE2A6", "\uE2A6", "\uE2A7", true),
        new("music", "Music", "\uE8D6", "\U0001F3B5", "\uE340", "\uE340", "\uE341", true),
        new("mic", "Microphone", "\uE720", "\U0001F3A4", "\uE326", "\uE326", "\uE327", true),
        new("video", "Video", "\uE714", "\U0001F4F9", "\uE4DA", "\uE4DA", "\uE4DB", true),
        new("home", "Home", "\uE80F", "\U0001F3E0", "\uE2C2", "\uE2C2", "\uE2C3", true),
        new("star", "Favorite", "\uE734", "\u2B50", "\uE46A", "\uE46A", "\uE46B", true),
        new("globe", "Web", "\uE774", "\U0001F310", "\uE288", "\uE288", "\uE289", true),
        new("mail", "Mail", "\uE715", "\u2709", "\uE214", "\uE214", "\uE215", true),
        new("chat", "Chat", "\uE8BD", "\U0001F4AC", "\uE168", "\uE168", "\uE169", true),
        new("calendar", "Calendar", "\uE787", "\U0001F4C5", "\uE108", "\uE108", "\uE109", true),
        new("document", "Document", "\uE7C3", "\U0001F4C4", "\uE23A", "\uE23A", "\uE23B", true),
        new("library", "Library", "\uE8F1", "\U0001F4DA", "\uE758", "\uE758", "\uE759", true),
        new("school", "Study", "\uE7BE", "\U0001F393", "\uE62C", "\uE62C", "\uE62D", true),
        new("shop", "Shopping", "\uE719", "\U0001F6CD", "\uE416", "\uE416", "\uE417", true),
        new("bank", "Finance", "\uE825", "\U0001F3E6", "\uE0B4", "\uE0B4", "\uE0B5", true),
        new("pin", "Place", "\uE707", "\U0001F4CD", "\uE316", "\uE316", "\uE317", true),
        new("cloud", "Cloud", "\uE753", "\u2601", "\uE1AA", "\uE1AA", "\uE1AB", true),
        new("eye", "Eyes", "\uE890", "\U0001F441", "\uE220", "\uE220", "\uE221", true),
        new("smile", "Fun", "\uE76E", "\U0001F60A", "\uE436", "\uE436", "\uE437", true),
        new("flag", "Goal", "\uE7C1", "\U0001F6A9", "\uE244", "\uE244", "\uE245", true),
        new("power", "Power", "\uE7E8", "\U0001F50C", "\uE946", "\uE946", "\uE947", true),
        new("mute", "Muted", "\uE74F", "\U0001F507", "\uE45A", "\uE45A", "\uE45B", false),
        new("picture", "Picture", "\uE8B9", "\U0001F5BC", "\uE2CA", "\uE2CA", "\uE2CB", false),
        new("monitor-off", "Display off", "\uE7FB", "", "\uE32E", "\uE32E", "\uE32F", false),
        new("settings", "Settings", "\uE713", "\u2699", "\uE270", "\uE270", "\uE271", false),
        new("keyboard", "Keyboard", "\uE765", "\u2328", "\uE2D8", "\uE2D8", "\uE2D9", false),
        new("apps", "Apps", "\uE71D", "", "\uE464", "\uE464", "\uE465", false),
        new("input", "Input", "\uE703", "\U0001F50C", "\uEB5A", "\uEB5A", "\uEB5B", false),
        new("switch", "Switch", "\uE8AB", "\U0001F500", "\uE0A0", "\uE0A0", "\uE0A1", false),
        new("sync", "Sync", "\uE895", "\U0001F504", "\uE094", "\uE094", "\uE095", false),
        new("refresh", "Refresh", "\uE72C", "\U0001F504", "\uE036", "\uE036", "\uE037", false),
        new("undo", "Undo", "\uE7A7", "\u21A9", "\uE038", "\uE038", "\uE039", false),
        new("add", "Add", "\uE710", "", "\uE3D4", "\uE3D4", "\uE3D5", false),
        new("remove", "Remove", "\uE711", "", "\uE4F6", "\uE4F6", "\uE4F7", false),
        new("down", "Down", "\uE70D", "", "\uE136", "\uE136", "\uE137", false),
        new("right", "Right", "\uE76C", "", "\uE13A", "\uE13A", "\uE13B", false),
        new("warning", "Warning", "\uE7BA", "\u26A0", "\uE4E0", "\uE4E0", "\uE4E1", false),
        new("help", "Help", "\uE897", "\u2753", "\uE3E8", "\uE3E8", "\uE3EB", false),
        new("folder", "Folder", "\uE838", "\U0001F4C2", "\uE256", "\uE256", "\uE257", false),
        new("check", "Check", "\uE73E", "", "\uE182", "\uE182", "\uE183", false),
        new("window", "Window", "\uE8A7", "", "\uE5DA", "\uE5DA", "\uE5DB", false),
        new("speed", "Speed", "\uEC4A", "\U0001F3CE", "\uE628", "\uE628", "\uE629", false),
        new("import", "Import", "\uE8B5", "\U0001F4E5", "\uE20C", "\uE20C", "\uE20D", false),
        new("minus", "Minus", "\uE738", "", "\uE32A", "\uE32A", "\uE32B", false),
        new("search", "Search", "\uE721", "\U0001F50D", "\uE30C", "\uE30C", "\uE30D", false),
        new("zoom", "Detail", "\uE8A3", "\U0001F50E", "\uE310", "\uE310", "\uE311", false),
        new("timer", "Timer", "\uE916", "\u23F1", "\uE492", "\uE492", "\uE493", false),

    ];

    private static readonly Dictionary<string, IconDef> byId = Catalog.ToDictionary(i => i.Id);
    private static readonly Dictionary<char, IconDef> byLine = Catalog.GroupBy(i => i.Line[0]).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The icons offered to profiles.</summary>
    public static IEnumerable<IconDef> ProfileLibrary => Catalog.Where(i => i.InLibrary);

    /// <summary>An icon in a style.</summary>
    public static Mark MarkFor(IconDef icon, string style) => style switch
    {
        "fill" => new Mark(icon.Fill, FillFont, FontWeights.Normal),
        "duotone" => new Mark(icon.DuotoneFront, DuotoneFont, FontWeights.Normal, icon.DuotoneBack),
        "classic" when icon.Classic.Length > 0 => new Mark(icon.Classic, ClassicFont, FontWeights.Normal),
        _ => new Mark(icon.Line, LineFont, FontWeights.Normal)
    };

    /// <summary>A library icon in a style, or null for an unknown id.</summary>
    public static Mark? LibraryMark(string style, string id) => byId.TryGetValue(id, out var icon) ? MarkFor(icon, style) : null;

    // Small marks (check, plus, chevrons...) stay single-layer in Duotone: a soft fill behind them only blurs them.
    private static readonly HashSet<string> plainMarks = ["check", "add", "remove", "down", "right", "minus", "refresh", "undo", "switch", "sync"];

    /// <summary>
    /// A panel glyph (Windows icon font text, one or more characters) in the panel's style, or null when it has no
    /// counterpart (it stays as it is).
    /// </summary>
    public static Mark? Restyle(string text, string style)
    {
        if (style == "line" || text.Length is 0 or > 3) return null;
        var marks = new List<Mark>();
        foreach (char c in text)
        {
            if (!byLine.TryGetValue(c, out var icon)) return null;
            var mark = MarkFor(icon, style);
            marks.Add(plainMarks.Contains(icon.Id) ? mark with { Secondary = null } : mark);
        }
        if (marks.Any(m => !ReferenceEquals(m.Font, marks[0].Font))) return null;
        return new Mark(string.Concat(marks.Select(m => m.Text)), marks[0].Font, marks[0].Weight,
            marks.All(m => m.Secondary is not null) ? string.Concat(marks.Select(m => m.Secondary)) : null);
    }

    /// <summary>Phosphor draws a little smaller than the Windows icon font at the same size: its glyphs are scaled up.</summary>
    public static double SizeFactor(string style) => style is "fill" or "duotone" ? 1.15 : 1.0;

    /// <summary>True for a character of the Windows icon font that the catalog knows.</summary>
    public static bool IsKnownGlyph(string text) => text.Length is > 0 and <= 3 && text.All(byLine.ContainsKey);

    /// <summary>The style used for panel icons and for profiles that keep their usual icon (set by the Theme).</summary>
    public static string PanelStyle { get; set; } = "line";

    /// <summary>Old style names ("fluent", "bold") are the Line style now.</summary>
    public static string StyleKey(string style) => IconStyles.Any(s => s.Key == style) ? style : "line";

    /// <summary>
    /// The icon a profile shows: the one it chose ("icon:fill:game", or "glyph:E7FC" from earlier versions), or the
    /// usual one of its name in the panel's style.
    /// </summary>
    public static Mark ProfileMark(string name, string? icon)
    {
        if (icon is not null && icon.StartsWith("icon:", StringComparison.Ordinal) && icon.Split(':') is [_, var style, var id] &&
            LibraryMark(StyleKey(style), id) is { } chosen)
            return chosen;
        string glyph = ForProfile(name, icon);
        return Restyle(glyph, PanelStyle) ?? new Mark(glyph, LineFont, FontWeights.Normal);
    }

    /// <summary>Icon of a profile: the glyph it chose ("glyph:E7FC"), or the usual one of its name.</summary>
    public static string ForProfile(string name, string? icon) =>
        icon is { Length: 10 } && icon.StartsWith("glyph:", StringComparison.Ordinal) &&
        int.TryParse(icon.AsSpan(6), System.Globalization.NumberStyles.HexNumber, null, out int code)
            ? ((char)code).ToString() : ForProfile(name);

    private static readonly Dictionary<string, ImageSource?> profileImages = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The picture a profile chose ("image:name.png" in the icons folder), or null.</summary>
    public static ImageSource? ProfileImage(string? icon)
    {
        if (icon is null || !icon.StartsWith("image:", StringComparison.Ordinal)) return null;
        string path = System.IO.Path.Combine(Core.AppPaths.IconsDirectory, icon[6..]);
        if (profileImages.TryGetValue(path, out var known)) return known;
        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // the file is not kept open
                bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
                bitmap.DecodePixelWidth = 96;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException) { }
        profileImages[path] = image;
        return image;
    }


    /// <summary>Colors offered for profiles (first five: Custom and the templates).</summary>
    public static IReadOnlyList<string> ProfileColors { get; } =
        ["#C9CCD6", "#4C8DFF", "#8B7CF6", "#FF6B6B", "#F5B544", "#4CD97B", "#2DD4BF", "#F472B6", "#FB923C"];

    /// <summary>The color of a profile: its own, or the one its name usually gets (Work blue, Night violet...).</summary>
    public static string ColorForProfile(string name, string? own)
    {
        if (own is { Length: 7 }) return own;
        string value = name.ToLowerInvariant();
        return value switch
        {
            "custom" => "#C9CCD6",
            _ when value.Contains("work") || value.Contains("office") => "#4C8DFF",
            _ when value.Contains("night") || value.Contains("sleep") => "#8B7CF6",
            _ when value.Contains("game") || value.Contains("gaming") => "#FF6B6B",
            _ when value.Contains("theater") || value.Contains("cinema") || value.Contains("movie") => "#F5B544",
            _ => ProfileColors[5 + Math.Abs(StableHash(value)) % 4]
        };
    }

    public static SolidColorBrush Brush(string hex, double opacity = 1)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private static int StableHash(string text)
    {
        int hash = 17;
        foreach (char c in text) hash = unchecked(hash * 31 + c);
        return hash == int.MinValue ? 0 : hash;
    }

    private static bool IsPictureMode(FeatureDefinition feature) =>
        feature.Code == VcpCode.DisplayMode || feature.Name.Contains("picture mode", StringComparison.OrdinalIgnoreCase);

    private static bool Named(FeatureDefinition feature, string text) =>
        feature.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
}
