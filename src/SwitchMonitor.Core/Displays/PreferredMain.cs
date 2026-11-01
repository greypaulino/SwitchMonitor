namespace SwitchMonitor.Core.Displays;

/// <summary>A display as the main-display rule sees it: its settings key, its Windows (GDI) name, and whether Windows uses it.</summary>
public sealed record DisplaySeat(string Key, string? GdiName, bool IsActive);

/// <summary>
/// The "Preferred" main display: when two or more displays are connected, the main display stays on the preferred
/// one; when it is not available, Windows' own choice stands (Windows moves the main display to another one).
/// </summary>
public static class PreferredMain
{
    /// <summary>
    /// The GDI name to make the main display, or null when nothing should change: no preference, the preferred
    /// display is not connected or not in use, it is the only display, or it already is the main display.
    /// </summary>
    public static string? Target(string? preferredKey, IEnumerable<DisplaySeat> displays, string? currentMain)
    {
        if (string.IsNullOrEmpty(preferredKey)) return null;
        var active = displays.Where(d => d.IsActive && !string.IsNullOrEmpty(d.GdiName)).ToArray();
        if (active.Length < 2) return null;
        var preferred = active.FirstOrDefault(d => string.Equals(d.Key, preferredKey, StringComparison.OrdinalIgnoreCase));
        if (preferred is null || string.Equals(preferred.GdiName, currentMain, StringComparison.OrdinalIgnoreCase)) return null;
        return preferred.GdiName;
    }
}
