using System.Runtime.InteropServices;

namespace SwitchMonitor.App.Services;

/// <summary>The Windows display the mouse pointer is on (shortcuts shared by several displays act on that one).</summary>
internal static class PointerMonitor
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx { public int Size; public Rect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    /// <summary>The display source under the pointer (for example \\.\DISPLAY2), or null when it cannot be told.</summary>
    public static string? GdiNameUnderPointer()
    {
        if (!GetCursorPos(out var point)) return null;
        IntPtr monitor = MonitorFromPoint(point, 0);   // MONITOR_DEFAULTTONULL
        if (monitor == IntPtr.Zero) return null;
        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfo(monitor, ref info) ? info.Device : null;
    }
}
