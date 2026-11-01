using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace SwitchMonitor.App.Services;

/// <summary>
/// A bare * opens the brightness panel while no program is in use (the desktop or the taskbar has the keyboard).
/// Anywhere else * types as usual and the assigned shortcut (Ctrl+Alt+* by default) is needed. Watched with a
/// low-level keyboard hook: the key is only taken when it is used here.
/// </summary>
public sealed class DesktopStarKey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    private const int VK_MULTIPLY = 0x6A, VK_8 = 0x38, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    // The windows that have the keyboard when no program is in use.
    private static readonly HashSet<string> DesktopClasses =
        new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private readonly Action open;
    private readonly Dispatcher dispatcher;
    private readonly LowLevelKeyboardProc callback;   // kept alive: Windows calls it for as long as the hook exists
    private IntPtr hook;
    private int swallowedKey = -1;

    public DesktopStarKey(Action open)
    {
        this.open = open;
        dispatcher = Dispatcher.CurrentDispatcher;
        callback = OnKey;
        using var module = Process.GetCurrentProcess().MainModule;
        hook = SetWindowsHookEx(WH_KEYBOARD_LL, callback, GetModuleHandle(module?.ModuleName), 0);
    }

    private IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            int msg = message.ToInt32();
            int vk = Marshal.ReadInt32(data);
            if ((msg is WM_KEYUP or WM_SYSKEYUP) && vk == swallowedKey)
            {
                swallowedKey = -1;
                return 1;   // the release of a * that opened the panel
            }
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN && IsStar(vk) && NoProgramInUse())
            {
                swallowedKey = vk;
                dispatcher.BeginInvoke(open);
                return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    /// <summary>* alone: the keypad key, or Shift+8 on the main keyboard; never with Ctrl, Alt or Windows.</summary>
    private static bool IsStar(int vk)
    {
        if (Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN)) return false;
        return vk == VK_MULTIPLY || (vk == VK_8 && Down(VK_SHIFT));
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool NoProgramInUse()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return true;
        var name = new StringBuilder(64);
        return GetClassName(window, name, name.Capacity) > 0 && DesktopClasses.Contains(name.ToString());
    }

    public void Dispose()
    {
        if (hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(hook);
        hook = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc callback, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
