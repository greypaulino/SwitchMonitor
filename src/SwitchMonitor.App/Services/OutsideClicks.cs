using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SwitchMonitor.App.Services;

/// <summary>
/// While the brightness panel is open: a mouse button pressed anywhere outside it (another program, the desktop, the
/// taskbar) closes it. Windows only reports "deactivated" when the panel had the keyboard, and the taskbar does not
/// always take it, so the clicks themselves are watched with a low-level mouse hook. Clicks are never taken.
/// </summary>
public sealed class OutsideClicks : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;

    private readonly Func<IEnumerable<IntPtr>> windows;
    private readonly Action outside;
    private readonly Dispatcher dispatcher;
    private readonly LowLevelMouseProc callback;
    private IntPtr hook;

    /// <param name="windows">The panel's windows (the panel and its open side panels); a click inside one of them is not outside.</param>
    public OutsideClicks(Func<IEnumerable<IntPtr>> windows, Action outside)
    {
        this.windows = windows;
        this.outside = outside;
        dispatcher = Dispatcher.CurrentDispatcher;
        callback = OnMouse;
    }

    public bool IsWatching => hook != IntPtr.Zero;

    public void Start()
    {
        if (hook != IntPtr.Zero) return;
        using var module = Process.GetCurrentProcess().MainModule;
        hook = SetWindowsHookEx(WH_MOUSE_LL, callback, GetModuleHandle(module?.ModuleName), 0);
    }

    public void Stop()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt32() is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var at = Marshal.PtrToStructure<Point>(data);
            dispatcher.BeginInvoke(() =>
            {
                foreach (IntPtr window in windows())
                    if (GetWindowRect(window, out var rect) && at.X >= rect.Left && at.X < rect.Right && at.Y >= rect.Top && at.Y < rect.Bottom)
                        return;
                outside();
            });
        }
        return CallNextHookEx(hook, code, message, data);
    }

    public void Dispose() => Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);
}
