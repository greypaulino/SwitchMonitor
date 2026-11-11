namespace SwitchMonitor.Core.Hotkeys;

/// <summary>
/// Which display a shortcut shared by several displays acts on: the one under the mouse pointer; when the pointer is on
/// a display that has no such shortcut (a laptop panel, the taskbar), the last of them it was on; else the first.
/// </summary>
public static class ShortcutRouting
{
    public static string? Choose(IReadOnlyList<string> candidateKeys, string? pointerKey, string? lastPointerKey)
    {
        if (candidateKeys.Count == 0) return null;
        if (pointerKey is not null && candidateKeys.Contains(pointerKey)) return pointerKey;
        if (lastPointerKey is not null && candidateKeys.Contains(lastPointerKey)) return lastPointerKey;
        return candidateKeys[0];
    }
}
