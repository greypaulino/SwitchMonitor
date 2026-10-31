using System.Runtime.InteropServices;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The Windows main display (1.x "M"): which display it is, and moving it to another one. The whole layout is applied
/// in one Windows call (display configuration API), moved so the new main display sits at 0,0; staging displays one
/// by one through ChangeDisplaySettingsEx failed on AMD when going back to the old main display (1.10.0).
/// </summary>
public static class MainDisplay
{
    /// <summary>GDI name of the main display ("\\.\DISPLAY1"), or null.</summary>
    public static string? Current => System.Windows.Forms.Screen.PrimaryScreen?.DeviceName;

    /// <summary>Active displays Windows extends the desktop to, by GDI name.</summary>
    public static IReadOnlyList<string> Active => System.Windows.Forms.Screen.AllScreens.Select(s => s.DeviceName).ToArray();

    /// <summary>The display can become the main one: there are at least two and it is part of the desktop.</summary>
    public static bool CanBeMain(string? gdiName) =>
        !string.IsNullOrEmpty(gdiName) && Active.Count >= 2 && Active.Contains(gdiName, StringComparer.OrdinalIgnoreCase)
        && Position(gdiName) is not null;

    public static bool IsMain(string? gdiName) =>
        !string.IsNullOrEmpty(gdiName) && string.Equals(Current, gdiName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Makes <paramref name="gdiName"/> the main display. Throws with a readable message when Windows refuses.</summary>
    public static void Set(string gdiName)
    {
        if (IsMain(gdiName)) return;
        if (!CanBeMain(gdiName)) throw new InvalidOperationException("This display cannot become the main display in the current mode.");
        var positions = new Dictionary<(int X, int Y), string>();
        foreach (string name in Active)
            if (Position(name) is { } p) positions[p] = name;
            else throw new InvalidOperationException("Windows could not read the display layout.");
        var target = positions.First(p => string.Equals(p.Value, gdiName, StringComparison.OrdinalIgnoreCase)).Key;

        byte[] paths = [], modes = [];
        uint pathCount = 0, modeCount = 0;
        int result = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            result = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount);
            if (result != 0) throw new InvalidOperationException($"Windows could not read the active display paths ({result}).");
            paths = new byte[pathCount * PathSize];
            modes = new byte[modeCount * ModeSize];
            result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result != ErrorInsufficientBuffer) break;
        }
        if (result != 0) throw new InvalidOperationException($"Windows could not query the display layout ({result}).");

        var moved = new HashSet<uint>();
        for (int i = 0; i < pathCount; i++)
        {
            uint modeIndex = BitConverter.ToUInt32(paths, i * PathSize + 12);   // sourceInfo.modeInfoIdx
            if (modeIndex >= modeCount || BitConverter.ToUInt32(modes, (int)modeIndex * ModeSize) != 1)
                throw new InvalidOperationException("Windows returned a display path without a source mode.");
            int offset = (int)modeIndex * ModeSize;
            int x = BitConverter.ToInt32(modes, offset + 28), y = BitConverter.ToInt32(modes, offset + 32);
            if (!positions.ContainsKey((x, y)))
                throw new InvalidOperationException("Windows returned a display position that does not match the active displays.");
            if (moved.Add(modeIndex))
            {
                BitConverter.TryWriteBytes(modes.AsSpan(offset + 28), x - target.X);
                BitConverter.TryWriteBytes(modes.AsSpan(offset + 32), y - target.Y);
            }
        }
        if (moved.Count != positions.Count) throw new InvalidOperationException("The active displays cannot be matched to the Windows display paths.");
        result = SetDisplayConfig(pathCount, paths, modeCount, modes, SdcValidate | SdcUseSuppliedDisplayConfig);
        if (result != 0) throw new InvalidOperationException($"Windows rejected the new display layout ({result}).");
        result = SetDisplayConfig(pathCount, paths, modeCount, modes, SdcApply | SdcUseSuppliedDisplayConfig | SdcSaveToDatabase);
        if (result != 0) throw new InvalidOperationException($"Windows could not apply the main display ({result}).");
        if (!IsMain(gdiName)) throw new InvalidOperationException("Windows did not confirm the new main display.");
    }

    /// <summary>Desktop position of a display (DEVMODE dmPosition), or null when Windows does not report it.</summary>
    private static (int X, int Y)? Position(string gdiName)
    {
        var mode = new byte[220];
        BitConverter.TryWriteBytes(mode.AsSpan(68), (ushort)mode.Length);   // dmSize
        if (!EnumDisplaySettingsEx(gdiName, EnumCurrentSettings, mode, 0)) return null;
        return (BitConverter.ToInt32(mode, 76), BitConverter.ToInt32(mode, 80));
    }

    private const int PathSize = 72, ModeSize = 64;
    private const uint QdcOnlyActivePaths = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int EnumCurrentSettings = -1;
    private const uint SdcApply = 0x80, SdcValidate = 0x40, SdcUseSuppliedDisplayConfig = 0x20, SdcSaveToDatabase = 0x200;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsExW")]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNum, [In, Out] byte[] devMode, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] byte[] paths, ref uint modeCount, [Out] byte[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint pathCount, [In] byte[] paths, uint modeCount, [In] byte[] modes, uint flags);
}
