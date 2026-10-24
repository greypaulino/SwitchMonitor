using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SwitchMonitor.Core.Hotkeys;

namespace SwitchMonitor.App.Services;

/// <summary>
/// Global shortcuts through RegisterHotKey on a message-only window. A shortcut another program (for example
/// SwitchMonitor 1.x) already owns cannot be registered; that is reported, not fatal.
/// Shortcuts registered with a hold handler are polled while the key stays down, so holding works the same on every
/// keyboard repeat setting (Windows key repeat is turned off with MOD_NOREPEAT).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(40);

    private readonly HwndSource window;
    private readonly Dictionary<int, Registration> registrations = [];
    private readonly DispatcherTimer holdTimer;
    private Registration? held;
    private DateTime heldSince;
    private int nextId = 1;

    private sealed record Registration(HotkeyChord Chord, Action Press, Action<TimeSpan>? Hold);

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("SwitchMonitorHotkeys")
        {
            Width = 0, Height = 0, WindowStyle = 0,
            ParentWindow = new IntPtr(-3)   // HWND_MESSAGE: invisible, never activated
        };
        window = new HwndSource(parameters);
        window.AddHook(WndProc);
        holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = PollInterval };
        holdTimer.Tick += (_, _) => PollHold();
    }

    /// <summary>
    /// Registers a shortcut. <paramref name="hold"/> (optional) is called repeatedly with the time the key has been
    /// down, starting after the first poll. Returns false when Windows refuses the shortcut (already taken).
    /// </summary>
    public bool Register(HotkeyChord chord, Action press, Action<TimeSpan>? hold = null)
    {
        int id = nextId++;
        if (!RegisterHotKey(window.Handle, id, (uint)chord.Modifiers | MOD_NOREPEAT, chord.VirtualKey))
            return false;
        registrations[id] = new Registration(chord, press, hold);
        return true;
    }

    /// <summary>
    /// True when no other program (or Windows) owns the shortcut. Only meaningful while this service has nothing
    /// registered, as during shortcut editing.
    /// </summary>
    public bool IsFree(HotkeyChord chord)
    {
        const int ProbeId = 0xBFFE;
        if (!RegisterHotKey(window.Handle, ProbeId, (uint)chord.Modifiers | MOD_NOREPEAT, chord.VirtualKey)) return false;
        UnregisterHotKey(window.Handle, ProbeId);
        return true;
    }

    public void UnregisterAll()
    {
        StopHold();
        foreach (int id in registrations.Keys) UnregisterHotKey(window.Handle, id);
        registrations.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && registrations.TryGetValue(wParam.ToInt32(), out var registration))
        {
            handled = true;
            StopHold();
            registration.Press();
            if (registration.Hold is not null)
            {
                held = registration;
                heldSince = DateTime.UtcNow;
                holdTimer.Start();
            }
        }
        return IntPtr.Zero;
    }

    private void PollHold()
    {
        if (held is null || (GetAsyncKeyState((int)held.Chord.VirtualKey) & 0x8000) == 0)
        {
            StopHold();
            return;
        }
        held.Hold!(DateTime.UtcNow - heldSince);
    }

    private void StopHold()
    {
        holdTimer.Stop();
        held = null;
    }

    public void Dispose()
    {
        UnregisterAll();
        window.RemoveHook(WndProc);
        window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
