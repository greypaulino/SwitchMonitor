using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Remote;
using SwitchMonitor.Core.Sharing;

namespace SwitchMonitor.App.Services;

/// <summary>
/// One mouse and keyboard for two paired computers (Settings > Devices > Share mouse and keyboard). Push the pointer against
/// the edge on the side where the other computer sits and it continues there, with the keyboard; push it against the opposite
/// edge there and it comes back. The events travel over the same authenticated connection that pairing made.
///
/// Sending side: two low-level hooks (on their own thread, so a busy window never stalls the system's input) swallow the
/// mouse and keyboard while the other computer has them, and queue what happened. Receiving side: the events are injected
/// with SendInput; the pointer position is tracked here (not the sender's), so the other computer's own speed settings apply.
/// Safe exits: the connection dropping, no word for four seconds, the pointer leaving, or Ctrl+Alt+Shift+Esc on the sender.
/// </summary>
public sealed class InputShareService : ObservableObject, IDisposable
{
    private readonly MainViewModel main;
    private readonly ICoreLog log;

    public InputShareService(MainViewModel main, ICoreLog log)
    {
        this.main = main;
        this.log = log;
        mouseProc = MouseHook;
        keyProc = KeyHook;
        watchdog = new Timer(_ => Watch(), null, 1000, 1000);
        if (Enabled) StartHooks();
    }

    // ---- Settings ----

    public bool Enabled
    {
        get => main.Settings.ShareInput;
        set
        {
            if (value == main.Settings.ShareInput) return;
            main.UpdateSettings(main.Settings with { ShareInput = value }, shortcutsChanged: false);
            if (value) StartHooks(); else StopHooks();
            Raise();
        }
    }

    /// <summary>True when this program runs as administrator: only then can it control windows that do too.</summary>
    public bool IsElevated { get; } = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    public bool CanElevate => !IsElevated;
    private RelayCommand? elevate;
    public RelayCommand ElevateCommand => elevate ??= new RelayCommand(() => main.RequestRestart(true));
    public string ElevatedText => Loc.T(IsElevated
        ? "Running as administrator: windows that run as administrator can be controlled too."
        : "Windows that run as administrator do not accept shared input unless this program runs as administrator too.");

    public string SideName
    {
        get => main.Settings.ShareSide.ToString();
        set
        {
            if (!Enum.TryParse<ShareSide>(value, out var side) || side == main.Settings.ShareSide) return;
            main.UpdateSettings(main.Settings with { ShareSide = side }, shortcutsChanged: false);
            Raise();
        }
    }

    public string Hint => Loc.Language == "es"
        ? "Empuja el puntero contra el borde derecho o izquierdo de tus pantallas para seguir en la otra computadora; empújalo contra el borde por el que entró para volver. Actívalo en las dos computadoras. Ctrl+Alt+Shift+Esc devuelve el teclado y el mouse al instante."
        : "Push the pointer against the right or left edge of your screens to continue on the other computer; push it back against the edge it came in by to return. "
          + "Turn this on in both computers. Ctrl+Alt+Shift+Esc brings the keyboard and mouse back at once.";

    // ---- Sending side: the hooks ----

    private readonly HookProc mouseProc, keyProc;
    private Thread? hookThread;
    private uint hookThreadId;
    private volatile bool remote;                  // this computer's mouse and keyboard are on the other computer now
    private PeerClient? target;
    private int anchorX, anchorY, lastX, lastY, settle;
    private double entryFraction;
    private ShareSide mySide;
    private readonly HashSet<int> down = [];       // keys held down on this computer (hook thread)
    private int[] heldAtEntry = [];
    private Channel<JsonObject>? outbox;

    private void StartHooks()
    {
        if (hookThread is not null) return;
        var ready = new ManualResetEventSlim();
        hookThread = new Thread(() =>
        {
            hookThreadId = GetCurrentThreadId();
            IntPtr module = GetModuleHandle(null);
            IntPtr mouse = SetWindowsHookEx(WhMouseLl, mouseProc, module, 0);
            IntPtr keys = SetWindowsHookEx(WhKeyboardLl, keyProc, module, 0);
            if (mouse == IntPtr.Zero || keys == IntPtr.Zero) log.Write(LogLevel.Warning, "Share input: Windows did not accept the mouse/keyboard watch.");
            ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse);
            if (keys != IntPtr.Zero) UnhookWindowsHookEx(keys);
        }) { IsBackground = true, Name = "SwitchMonitor input share" };
        hookThread.Start();
        ready.Wait(2000);
        outbox = Channel.CreateUnbounded<JsonObject>();
        _ = Task.Run(() => SendLoopAsync(outbox));
        log.Write(LogLevel.Info, "Share input: on.");
    }

    private void StopHooks()
    {
        LeaveLocal(null);
        outbox?.Writer.TryComplete();
        outbox = null;
        if (hookThread is not null) PostThreadMessage(hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        hookThread = null;
        log.Write(LogLevel.Info, "Share input: off.");
    }

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        int x = Marshal.ReadInt32(lParam, 0), y = Marshal.ReadInt32(lParam, 4);
        uint data = (uint)Marshal.ReadInt32(lParam, 8), flags = (uint)Marshal.ReadInt32(lParam, 12);
        int message = (int)wParam;
        if ((flags & 0x03) != 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);   // injected (ours or another program's): never shared

        if (!remote)
        {
            if (message == WmMouseMove)
            {
                int dx = x - lastX, dy = y - lastY;
                lastX = x; lastY = y;
                var area = Screens();
                // The side is not set: the edge the pointer is pushed against (right or left) is the way to the other computer.
                foreach (var side in new[] { ShareSide.Right, ShareSide.Left })
                    if (ShareEdge.Crossed(side, x, y, dx, dy, area) && Enter(side, x, y, area)) return (IntPtr)1;
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // The other computer has the mouse: this pointer stays at the anchor; what it tried to do is sent over.
        switch (message)
        {
            case WmMouseMove:
                int moveX = x - anchorX, moveY = y - anchorY;
                if (settle > 0) { settle--; if (Math.Abs(moveX) > 150 || Math.Abs(moveY) > 150) { SetCursorPos(anchorX, anchorY); break; } }
                if (moveX != 0 || moveY != 0)
                {
                    SetCursorPos(anchorX, anchorY);
                    Send(new JsonObject { ["op"] = "move", ["dx"] = moveX, ["dy"] = moveY });
                }
                break;
            case WmLButtonDown: Button("l", true); break;
            case WmLButtonUp: Button("l", false); break;
            case WmRButtonDown: Button("r", true); break;
            case WmRButtonUp: Button("r", false); break;
            case WmMButtonDown: Button("m", true); break;
            case WmMButtonUp: Button("m", false); break;
            case WmXButtonDown: Button((data >> 16) == 1 ? "x1" : "x2", true); break;
            case WmXButtonUp: Button((data >> 16) == 1 ? "x1" : "x2", false); break;
            case WmMouseWheel: Send(new JsonObject { ["op"] = "wheel", ["d"] = (short)(data >> 16), ["h"] = false }); break;
            case WmMouseHWheel: Send(new JsonObject { ["op"] = "wheel", ["d"] = (short)(data >> 16), ["h"] = true }); break;
        }
        return (IntPtr)1;
    }

    private void Button(string button, bool pressed) => Send(new JsonObject { ["op"] = "btn", ["b"] = button, ["down"] = pressed });

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        int vk = Marshal.ReadInt32(lParam, 0), scan = Marshal.ReadInt32(lParam, 4), flags = Marshal.ReadInt32(lParam, 8);
        if ((flags & 0x10) != 0) return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);   // injected
        bool up = (flags & 0x80) != 0;
        if (!remote)
        {
            lock (down) { if (up) down.Remove(vk); else down.Add(vk); }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
        // Ctrl+Alt+Shift+Esc: the way back, whatever the other computer is doing.
        if (!up && vk == 0x1B && Held(0xA2, 0xA3) && Held(0xA4, 0xA5) && Held(0xA0, 0xA1))
        {
            Send(new JsonObject { ["op"] = "leave" });
            LeaveLocal(null);
            return (IntPtr)1;
        }
        lock (down) { if (up) down.Remove(vk); else down.Add(vk); }   // kept up to date: modifiers for the way back
        Send(new JsonObject { ["op"] = "key", ["vk"] = vk, ["sc"] = scan, ["ext"] = (flags & 1) != 0, ["up"] = up });
        return (IntPtr)1;
    }

    private bool Held(int a, int b) { lock (down) return down.Contains(a) || down.Contains(b); }

    private bool Enter(ShareSide side, int x, int y, ShareBounds area)
    {
        var client = Peers()?.ConnectedClient(null);
        if (client is null) return false;   // nobody to pass it to: it stays here
        target = client;
        mySide = side;
        entryFraction = ShareEdge.Fraction(side, x, y, area);
        anchorX = GetSystemMetrics(SmCxScreen) / 2;
        anchorY = GetSystemMetrics(SmCyScreen) / 2;
        settle = 2;
        lock (down) heldAtEntry = down.ToArray();
        remote = true;
        SetCursorPos(anchorX, anchorY);
        Send(new JsonObject { ["op"] = "enter", ["side"] = side.ToString(), ["f"] = entryFraction });
        log.Write(LogLevel.Debug, $"Share input: the pointer went to {client.Computer.Name}.");
        return true;
    }

    /// <summary>The mouse and keyboard are back on this computer: the pointer comes in at the edge where it left.</summary>
    private void LeaveLocal(double? fraction)
    {
        if (!remote) return;
        remote = false;
        var (x, y) = ShareEdge.Landing(mySide, fraction ?? entryFraction, Screens());
        SetCursorPos(x, y);
        lastX = x; lastY = y;
        // A key that was down when the pointer left and was let go meanwhile must not stay pressed here.
        foreach (int vk in heldAtEntry)
            if ((GetAsyncKeyState(vk) & 0x8000) == 0) KeyEvent(vk, 0, false, true);
        lock (down) down.Clear();
        log.Write(LogLevel.Debug, "Share input: the pointer is back.");
    }

    private void Send(JsonObject message)
    {
        message["type"] = "input";
        outbox?.Writer.TryWrite(message);
    }

    private async Task SendLoopAsync(Channel<JsonObject> box)
    {
        var reader = box.Reader;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var message))
                {
                    // Moves that piled up while the last one was on its way go as one.
                    if ((string?)message["op"] == "move")
                    {
                        int dx = (int)message["dx"]!, dy = (int)message["dy"]!;
                        while (reader.TryPeek(out var next) && (string?)next["op"] == "move" && reader.TryRead(out next))
                        {
                            dx += (int)next["dx"]!; dy += (int)next["dy"]!;
                        }
                        message["dx"] = dx; message["dy"] = dy;
                    }
                    var client = target;
                    if (client is null || !await client.SendAsync(message)) { if (remote) LeaveLocal(null); }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { }
    }

    private PeerService? Peers() => main.Peers;

    // ---- Receiving side ----

    private readonly object gate = new();
    private bool controlled;
    private string? from;
    private ShareSide entrySide;
    private double posX, posY;
    private long lastHeard;
    private readonly HashSet<int> pressedKeys = [];
    private readonly HashSet<string> pressedButtons = [];

    /// <summary>An event from the paired computer (its server passed it on).</summary>
    public void OnInput(JsonElement message, string? deviceId)
    {
        string op = message.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        int Int(string name) => message.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
        lock (gate)
        {
            lastHeard = Environment.TickCount64;
            if (op == "leave")
            {
                if (controlled && from == deviceId) { controlled = false; ReleaseAll(); }
                if (remote && target?.Computer.DeviceId == deviceId)
                    LeaveLocal(message.TryGetProperty("f", out var f) && f.TryGetDouble(out double fraction) ? fraction : null);
                return;
            }
            if (op == "deny")
            {
                if (remote && target?.Computer.DeviceId == deviceId) LeaveLocal(null);
                return;
            }
            if (!Enabled)
            {
                if (op == "enter") Reply(deviceId, new JsonObject { ["op"] = "deny" });
                return;
            }
            var area = Screens();
            switch (op)
            {
                case "enter":
                    if (remote) { Reply(deviceId, new JsonObject { ["op"] = "deny" }); return; }
                    ReleaseAll();
                    entrySide = Enum.TryParse<ShareSide>(message.TryGetProperty("side", out var s) ? s.GetString() : null, out var theirs)
                        ? ShareEdge.Opposite(theirs) : ShareEdge.Opposite(main.Settings.ShareSide);
                    double along = message.TryGetProperty("f", out var fr) && fr.TryGetDouble(out double value) ? value : 0.5;
                    var (x, y) = ShareEdge.Landing(entrySide, along, area);
                    posX = x; posY = y;
                    controlled = true;
                    from = deviceId;
                    MoveTo(x, y, area);
                    break;
                case "move" when controlled && from == deviceId:
                    int dx = Int("dx"), dy = Int("dy");
                    posX = Math.Clamp(posX + dx, area.Left, area.Right - 1);
                    posY = Math.Clamp(posY + dy, area.Top, area.Bottom - 1);
                    MoveTo((int)posX, (int)posY, area);
                    if (ShareEdge.Crossed(entrySide, (int)posX, (int)posY, dx, dy, area))
                    {
                        controlled = false;
                        ReleaseAll();
                        Reply(deviceId, new JsonObject { ["op"] = "leave", ["f"] = ShareEdge.Fraction(entrySide, (int)posX, (int)posY, area) });
                    }
                    break;
                case "btn" when controlled && from == deviceId:
                    string b = message.TryGetProperty("b", out var bn) ? bn.GetString() ?? "" : "";
                    bool pressed = message.TryGetProperty("down", out var dn) && dn.ValueKind == JsonValueKind.True;
                    if (pressed) pressedButtons.Add(b); else pressedButtons.Remove(b);
                    MouseButton(b, pressed);
                    break;
                case "wheel" when controlled && from == deviceId:
                    MouseWheel(Int("d"), message.TryGetProperty("h", out var h) && h.ValueKind == JsonValueKind.True);
                    break;
                case "key" when controlled && from == deviceId:
                    int vk = Int("vk");
                    bool up = message.TryGetProperty("up", out var u) && u.ValueKind == JsonValueKind.True;
                    bool ext = message.TryGetProperty("ext", out var e) && e.ValueKind == JsonValueKind.True;
                    if (up) pressedKeys.Remove(vk); else pressedKeys.Add(vk);
                    KeyEvent(vk, Int("sc"), ext, up);
                    break;
            }
        }
    }

    private readonly object phonePointer = new();
    private double phoneX, phoneY;      // where the phone's pointer is, in fractions of a pixel
    private double pendX, pendY;        // what the phone asked for that the pointer has not walked yet
    private readonly ManualResetEventSlim glideWake = new(false);
    private Thread? glideThread;
    private volatile bool glideStop;

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    /// <summary>Starts from where the pointer really is (somebody may have moved it with a real mouse).</summary>
    private void SyncPhonePointer()
    {
        GetCursorPos(out var at);
        if (Math.Abs(at.X - phoneX) > 2 || Math.Abs(at.Y - phoneY) > 2) { phoneX = at.X; phoneY = at.Y; }
    }

    /// <summary>Walks a share of what is pending (1.0 = all of it). Call with <see cref="phonePointer"/> held.</summary>
    private void StepGlide(double share)
    {
        if (pendX == 0 && pendY == 0) return;
        var screens = Screens();
        double sx = pendX * share, sy = pendY * share;
        pendX -= sx; pendY -= sy;
        if (Math.Abs(pendX) < 0.05) pendX = 0;
        if (Math.Abs(pendY) < 0.05) pendY = 0;
        phoneX = Math.Clamp(phoneX + sx, screens.Left, screens.Right - 1);
        phoneY = Math.Clamp(phoneY + sy, screens.Top, screens.Bottom - 1);
        MoveTo((int)Math.Round(phoneX), (int)Math.Round(phoneY), screens);
        // Shared mouse and keyboard is on: the phone's pointer, pushed against the right or left edge, continues on the other computer
        // exactly as a real mouse does (and the way back is the same).
        if (hookThread is not null && !remote && !controlled && (sx != 0 || sy != 0))
            lock (gate)
            {
                int px = (int)Math.Round(phoneX), py = (int)Math.Round(phoneY);
                foreach (var side in new[] { ShareSide.Right, ShareSide.Left })
                    if (ShareEdge.Crossed(side, px, py, Math.Sign(sx), 0, screens) && Enter(side, px, py, screens)) { pendX = pendY = 0; break; }
            }
    }

    /// <summary>Every ~4 ms while there is something pending: a third of it, so a burst becomes an even run of small steps.</summary>
    private void GlideLoop()
    {
        timeBeginPeriod(1);   // Windows sleeps in 15 ms slices unless asked for 1 ms
        try
        {
            while (!glideStop)
            {
                glideWake.Wait();
                if (glideStop) break;
                Thread.Sleep(4);
                lock (phonePointer)
                {
                    StepGlide(0.35);
                    if (pendX == 0 && pendY == 0) glideWake.Reset();
                }
            }
        }
        finally { timeEndPeriod(1); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    /// <summary>An event from a paired phone (its Mouse and keyboard screen): applied here at once, with the pointer speed of this computer.</summary>
    public void OnPhoneInput(JsonElement message)
    {
        string op = message.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        int Int(string name) => message.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : 0;
        bool Flag(string name) => message.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        if (remote)
        {
            // The phone's pointer went to the other computer: what the phone does goes there (the way back comes from that computer).
            switch (op)
            {
                case "move": Send(new JsonObject { ["op"] = "move", ["dx"] = Math.Clamp(Int("dx"), -2000, 2000), ["dy"] = Math.Clamp(Int("dy"), -2000, 2000) }); break;
                case "btn": Button(message.TryGetProperty("b", out var rb) ? rb.GetString() ?? "" : "", Flag("down")); break;
                case "wheel": Send(new JsonObject { ["op"] = "wheel", ["d"] = Math.Clamp(Int("d"), -1200, 1200), ["h"] = Flag("h") }); break;
                case "key": Send(new JsonObject { ["op"] = "key", ["vk"] = Int("vk"), ["sc"] = 0, ["ext"] = Flag("ext"), ["up"] = Flag("up") }); break;
            }
            return;
        }
        switch (op)
        {
            case "move":
                lock (phonePointer)
                {
                    // The phone's steps arrive in bursts (its screen reports about 60 times a second, the network adds jitter):
                    // they pile up here and a fast beat (glide) walks the pointer through them, so it moves evenly.
                    if (pendX == 0 && pendY == 0) SyncPhonePointer();
                    pendX += Math.Clamp(Int("dx"), -2000, 2000);
                    pendY += Math.Clamp(Int("dy"), -2000, 2000);
                    glideWake.Set();
                    if (glideThread is null) { glideThread = new Thread(GlideLoop) { IsBackground = true, Name = "PhonePointer", Priority = ThreadPriority.AboveNormal }; glideThread.Start(); }
                }
                break;
            case "btn":
                lock (phonePointer) { StepGlide(1.0); }   // the click lands where the finger stopped
                MouseButton(message.TryGetProperty("b", out var b) ? b.GetString() ?? "" : "", Flag("down"));
                break;
            case "wheel":
                MouseWheel(Math.Clamp(Int("d"), -1200, 1200), Flag("h"));
                break;
            case "key":
                KeyEvent(Int("vk"), 0, Flag("ext"), Flag("up"));
                break;
            case "text":
                if (message.TryGetProperty("s", out var s) && s.GetString() is { Length: > 0 and <= 200 } typed)
                    foreach (char c in typed)
                    {
                        Inject(new Input { type = 1, u = new InputUnion { ki = new KeyInput { vk = 0, scan = c, flags = 0x0004 } } });
                        Inject(new Input { type = 1, u = new InputUnion { ki = new KeyInput { vk = 0, scan = c, flags = 0x0004 | 0x0002 } } });
                    }
                break;
        }
    }

    private void Reply(string? deviceId, JsonObject message)
    {
        message["type"] = "input";
        var client = Peers()?.ConnectedClient(deviceId);
        if (client is not null) _ = client.SendAsync(message);
    }

    private void ReleaseAll()
    {
        foreach (int vk in pressedKeys.ToArray()) KeyEvent(vk, 0, false, true);
        foreach (string b in pressedButtons.ToArray()) MouseButton(b, false);
        pressedKeys.Clear();
        pressedButtons.Clear();
    }

    /// <summary>Every second: a computer that stopped talking (cable, sleep, crash) does not keep the other one's input.</summary>
    private void Watch()
    {
        lock (gate)
        {
            if (controlled && Environment.TickCount64 - lastHeard > 4000) { controlled = false; ReleaseAll(); log.Write(LogLevel.Info, "Share input: the other computer went quiet; the keyboard and mouse are yours again."); }
        }
        if (remote)
        {
            if (target is not { Connected: true }) LeaveLocal(null);
            else Send(new JsonObject { ["op"] = "tick" });
        }
    }

    private readonly Timer watchdog;

    public void Dispose()
    {
        watchdog.Dispose();
        glideStop = true;
        glideWake.Set();
        if (hookThread is not null) StopHooks();
        lock (gate) { if (controlled) { controlled = false; ReleaseAll(); } }
    }

    // ---- Windows ----

    private static ShareBounds Screens()
    {
        int left = GetSystemMetrics(SmXVirtualScreen), top = GetSystemMetrics(SmYVirtualScreen);
        return new ShareBounds(left, top, left + GetSystemMetrics(SmCxVirtualScreen), top + GetSystemMetrics(SmCyVirtualScreen));
    }

    private static void MoveTo(int x, int y, ShareBounds area)
    {
        int nx = (int)((x - area.Left) * 65535.0 / Math.Max(1, area.Width - 1));
        int ny = (int)((y - area.Top) * 65535.0 / Math.Max(1, area.Height - 1));
        Inject(new Input { type = 0, u = new InputUnion { mi = new MouseInput { dx = nx, dy = ny, flags = 0x0001 | 0x8000 | 0x4000 } } });
    }

    private static void MouseButton(string button, bool pressed)
    {
        (uint flags, uint data) = (button, pressed) switch
        {
            ("l", true) => (0x0002u, 0u), ("l", false) => (0x0004u, 0u),
            ("r", true) => (0x0008u, 0u), ("r", false) => (0x0010u, 0u),
            ("m", true) => (0x0020u, 0u), ("m", false) => (0x0040u, 0u),
            ("x1", true) => (0x0080u, 1u), ("x1", false) => (0x0100u, 1u),
            ("x2", true) => (0x0080u, 2u), ("x2", false) => (0x0100u, 2u),
            _ => (0u, 0u)
        };
        if (flags != 0) Inject(new Input { type = 0, u = new InputUnion { mi = new MouseInput { mouseData = data, flags = flags } } });
    }

    private static void MouseWheel(int delta, bool horizontal) =>
        Inject(new Input { type = 0, u = new InputUnion { mi = new MouseInput { mouseData = (uint)delta, flags = horizontal ? 0x1000u : 0x0800u } } });

    /// <summary>Presses and releases one key (the media keys of a phone's Remote tab).</summary>
    public void Tap(int vk)
    {
        KeyEvent(vk, 0, false, false);
        KeyEvent(vk, 0, false, true);
    }

    private static void KeyEvent(int vk, int scan, bool extended, bool up) =>
        Inject(new Input
        {
            type = 1,
            u = new InputUnion { ki = new KeyInput { vk = (ushort)vk, scan = (ushort)scan, flags = (extended ? 1u : 0u) | (up ? 2u : 0u) } }
        });

    private static void Inject(Input input) => SendInput(1, [input], Marshal.SizeOf<Input>());

    private const int WhKeyboardLl = 13, WhMouseLl = 14;
    private const int WmMouseMove = 0x200, WmLButtonDown = 0x201, WmLButtonUp = 0x202, WmRButtonDown = 0x204, WmRButtonUp = 0x205,
        WmMButtonDown = 0x207, WmMButtonUp = 0x208, WmMouseWheel = 0x20A, WmXButtonDown = 0x20B, WmXButtonUp = 0x20C, WmMouseHWheel = 0x20E;
    private const uint WmQuit = 0x0012;
    private const int SmCxScreen = 0, SmCyScreen = 1, SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int dx, dy; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput mi; [FieldOffset(0)] public KeyInput ki; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint type; public InputUnion u; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
