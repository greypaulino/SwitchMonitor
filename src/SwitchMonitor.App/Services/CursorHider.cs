using System.Runtime.InteropServices;

namespace SwitchMonitor.App.Services;

/// <summary>
/// While the mouse and keyboard are on the other computer, the pointer of this one has no business on screen: there is one pointer, and
/// it is over there. This swaps the system's pointer shapes for an invisible one and puts the person's own back afterwards (Windows
/// keeps the swap until it is undone, so every way out calls <see cref="Show"/>, and so does the start of the program, in case an earlier
/// run was closed in the middle of it).
///
/// The work is done on its own thread: swapping the shapes tells every window about it and can take long, and the callers include the
/// mouse hook, which Windows gives up on when it takes more than a fraction of a second (the pointer would then stall or never cross).
/// </summary>
public static class CursorHider
{
    private static readonly uint[] Shapes = [32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650];
    private static readonly object Gate = new();
    private static readonly AutoResetEvent Wake = new(false);
    private static Thread? worker;
    private static bool wanted;      // what the callers last asked for
    private static bool shown = true; // what is on screen (only the worker changes it)
    private static bool restoreAtStart;

    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr instance, int xHot, int yHot, int width, int height, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private const uint SpiSetCursors = 0x0057;

    public static void Hide() => Ask(hidden: true);

    public static void Show() => Ask(hidden: false);

    /// <summary>Puts the person's pointer scheme back at start, after a run that never got to (done in the background).</summary>
    public static void RestoreAtStart()
    {
        lock (Gate) { restoreAtStart = true; }
        Ask(hidden: false);
    }

    /// <summary>At exit there is no time to wait for the thread: the scheme is put back right here.</summary>
    public static void ShowNow()
    {
        lock (Gate) { wanted = false; if (shown) return; shown = true; }
        SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);
    }

    private static void Ask(bool hidden)
    {
        lock (Gate)
        {
            wanted = hidden;
            worker ??= new Thread(Work) { IsBackground = true, Name = "SwitchMonitor pointer" };
            if (!worker.IsAlive) worker.Start();
        }
        Wake.Set();
    }

    private static void Work()
    {
        while (true)
        {
            Wake.WaitOne();
            bool target, restore;
            lock (Gate) { target = wanted; restore = restoreAtStart; restoreAtStart = false; }
            if (target && shown)
            {
                byte[] and = new byte[128], xor = new byte[128];
                Array.Fill(and, (byte)0xFF);   // AND 1, XOR 0 = transparent
                IntPtr module = GetModuleHandle(null);
                foreach (uint shape in Shapes)
                {
                    IntPtr blank = CreateCursor(module, 0, 0, 32, 32, and, xor);
                    if (blank != IntPtr.Zero) SetSystemCursor(blank, shape);   // Windows owns (and frees) the handle
                }
                lock (Gate) shown = false;
            }
            else if (!target && (!shown || restore))
            {
                SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, 0);
                lock (Gate) shown = true;
            }
        }
    }
}
