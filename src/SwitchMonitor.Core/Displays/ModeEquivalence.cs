using System.Text.Json;

namespace SwitchMonitor.Core.Displays;

/// <summary>
/// Picture modes have different names on every monitor ("FPS Game 1" on one, "FPS" on another). Linked displays share
/// one list of logical modes; this table says which native mode of each display stands for each logical one.
/// A display with no equivalent for a logical mode is skipped when that mode is chosen.
/// Built-in defaults; <c>mode-equivalence.json</c> in the data folder ({"Game": ["Game", "Custom (Game)"]}) replaces a group.
/// </summary>
public sealed class ModeEquivalence
{
    public const string FileName = "mode-equivalence.json";

    private static readonly (string Logical, string[] Native)[] Defaults =
    [
        ("Custom", ["Custom"]),
        ("Standard", ["Standard", "Normal", "Default"]),
        ("Game", ["Game", "Games", "Game Standard", "Custom (Game)"]),
        ("FPS", ["FPS", "FPS Game 1", "FPS 1"]),
        ("RTS", ["RTS", "RTS Game", "RTS Game 1"]),
        ("Cinema", ["Cinema", "Movie", "Entertain", "Entertainment"]),
        ("Photo", ["Photo", "Graphic", "sRGB"]),
        ("Reader", ["Reader", "Text", "Office"]),
        ("Vivid", ["Vivid", "Dynamic"]),
        ("Sports", ["Sports", "Sport"]),
        ("Eco", ["Eco", "Eye Saver"]),
    ];

    private readonly List<(string Logical, string[] Native)> groups;

    public ModeEquivalence(IEnumerable<(string Logical, string[] Native)>? table = null)
    {
        groups = [.. table ?? Defaults];
    }

    /// <summary>The defaults, with the groups of the file (when it exists and reads) replacing the built-in ones.</summary>
    public static ModeEquivalence Load(string? directory = null)
    {
        var table = Defaults.ToList();
        try
        {
            string path = Path.Combine(directory ?? AppPaths.DataDirectory, FileName);
            if (File.Exists(path)
                && JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path)) is { } custom)
            {
                foreach (var (logical, native) in custom)
                {
                    int at = table.FindIndex(g => Same(g.Logical, logical));
                    if (at >= 0) table[at] = (table[at].Logical, native); else table.Add((logical, native));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new ModeEquivalence(table);
    }

    /// <summary>The logical mode a native mode name stands for, or null when the table does not know it.</summary>
    public string? LogicalOf(string nativeLabel) =>
        groups.FirstOrDefault(g => g.Native.Any(n => Same(n, nativeLabel)) || Same(g.Logical, nativeLabel)).Logical;

    /// <summary>The native mode of a display that stands for <paramref name="logical"/>: the first candidate it has, or null.</summary>
    public string? NativeFor(string logical, IEnumerable<string> displayLabels)
    {
        var have = displayLabels.ToList();
        var group = groups.FirstOrDefault(g => Same(g.Logical, logical));
        if (group.Logical is null) return have.FirstOrDefault(l => Same(l, logical));
        foreach (string candidate in group.Native.Prepend(group.Logical))
            if (have.FirstOrDefault(l => Same(l, candidate)) is { } found) return found;
        return null;
    }

    /// <summary>
    /// The logical modes the linked displays have in common with at least one other display, in table order; a mode only
    /// one display has is left out (choosing it would change nothing else). Unknown native names are kept as their own.
    /// </summary>
    public IReadOnlyList<string> SharedModes(IReadOnlyList<IReadOnlyList<string>> displays)
    {
        var order = groups.Select(g => g.Logical).ToList();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var labels in displays)
            foreach (string logical in labels.Select(l => LogicalOf(l) ?? l).Distinct(StringComparer.OrdinalIgnoreCase))
                counts[logical] = counts.GetValueOrDefault(logical) + 1;
        return counts.Where(c => c.Value > 1)
            .OrderBy(c => { int i = order.FindIndex(o => Same(o, c.Key)); return i < 0 ? int.MaxValue : i; })
            .ThenBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Select(c => c.Key)
            .ToList();
    }

    private static bool Same(string a, string b) => Normal(a) == Normal(b);

    private static string Normal(string s) => new([.. s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
}
