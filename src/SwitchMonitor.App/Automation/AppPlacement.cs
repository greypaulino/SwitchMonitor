using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Scenes;

namespace SwitchMonitor.App.Automation;

/// <summary>A way to open a window, with the symbol that stands for it.</summary>
public sealed record OpenModeOption(string Name, string Symbol, string Font);

/// <summary>How a program's window opens on its display.</summary>
public static class OpenMode
{
    public const string Normal = "Normal", Maximized = "Maximized", Half = "Half", Minimized = "Minimized";
    public static IReadOnlyList<string> All { get; } = [Normal, Maximized, Half, Minimized];
    /// <summary>The choices with their symbol: restore, maximize, half the screen (left half filled), minimize.</summary>
    public static IReadOnlyList<OpenModeOption> Options { get; } =
    [
        new(Normal, "", "Segoe MDL2 Assets"),
        new(Maximized, "", "Segoe MDL2 Assets"),
        new(Half, "◧", "Segoe UI Symbol"),
        new(Minimized, "", "Segoe MDL2 Assets")
    ];

    public static string Clean(string? mode) => All.FirstOrDefault(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase)) ?? Normal;
}

/// <summary>How often each program has been in front (counted here), for the "most used" list; kept in app-usage.json.</summary>
public sealed class AppUsage
{
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
    private string? last;
    private bool dirty;

    public AppUsage(string? path = null)
    {
        this.path = path ?? Path.Combine(AppPaths.DataDirectory, "app-usage.json");
        try
        {
            if (File.Exists(this.path) && JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(this.path)) is { } read)
                foreach (var (exe, count) in read) counts[exe] = count;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>A program came to the front (the same one twice in a row counts once).</summary>
    public void Record(string exe)
    {
        lock (gate)
        {
            if (string.Equals(last, exe, StringComparison.OrdinalIgnoreCase)) return;
            last = exe;
            counts[exe] = counts.GetValueOrDefault(exe) + 1;
            dirty = true;
        }
    }

    public int CountOf(string exe) { lock (gate) return counts.GetValueOrDefault(exe); }

    public void Save()
    {
        lock (gate)
        {
            if (!dirty) return;
            dirty = false;
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(counts)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Where a program opens: the key of a local display and how the window opens there.</summary>
public sealed record AppPlace(string Display, string Mode);

/// <summary>Which display each program opens on ("game.exe" → display and mode), kept in app-placements.json.</summary>
public sealed class AppPlacementStore
{
    private readonly string path;
    private readonly object gate = new();
    private readonly Dictionary<string, AppPlace> places = new(StringComparer.OrdinalIgnoreCase);

    public AppPlacementStore(string? path = null)
    {
        this.path = path ?? Path.Combine(AppPaths.DataDirectory, "app-placements.json");
        try
        {
            if (File.Exists(this.path) && JsonNode.Parse(File.ReadAllText(this.path)) is JsonObject root)
                foreach (var (exe, value) in root)
                {
                    // The first version kept only the display ("game.exe": "key"); now an object with the mode too.
                    if (value is JsonValue text && text.TryGetValue<string>(out var display)) places[exe] = new AppPlace(display, OpenMode.Normal);
                    else if (value is JsonObject entry && entry["display"]?.GetValue<string>() is { Length: > 0 } shown)
                        places[exe] = new AppPlace(shown, OpenMode.Clean(entry["mode"]?.GetValue<string>()));
                }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
    }

    public IReadOnlyDictionary<string, AppPlace> All { get { lock (gate) return new Dictionary<string, AppPlace>(places, StringComparer.OrdinalIgnoreCase); } }

    public AppPlace? PlaceOf(string exe) { lock (gate) return places.GetValueOrDefault(exe); }

    public bool IsEmpty { get { lock (gate) return places.Count == 0; } }

    /// <summary>Opens <paramref name="exe"/> on that display from now on; null puts it back to wherever Windows opens it.</summary>
    public void Set(string exe, string? displayKey)
    {
        lock (gate)
        {
            if (displayKey is null) places.Remove(exe);
            else places[exe] = new AppPlace(displayKey, places.GetValueOrDefault(exe)?.Mode ?? OpenMode.Normal);
            Save();
        }
    }

    public void SetMode(string exe, string mode)
    {
        lock (gate)
        {
            if (!places.TryGetValue(exe, out var place)) return;
            places[exe] = place with { Mode = OpenMode.Clean(mode) };
            Save();
        }
    }

    private void Save()
    {
        try
        {
            var root = new JsonObject();
            foreach (var (exe, place) in places) root[exe] = new JsonObject { ["display"] = place.Display, ["mode"] = place.Mode };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Opens programs on the display chosen for them: when a window of such a program appears or first comes to the front (Windows
/// tells the app, no polling) it is moved there and opened the way chosen (as it is, maximized, half the screen or minimized).
/// Programs place their own window a moment after showing it, so it is checked again twice in the next seconds. Windows already
/// open are left alone, and so is a window the person moves afterwards.
/// </summary>
public sealed class AppPlacer
{
    private readonly AppPlacementStore store;
    private readonly Func<string, (int Left, int Top, int Width, int Height)?> areaOf;
    private readonly WinEventProc callback;
    private readonly IntPtr showHook, frontHook;
    private readonly string ownExe = SceneStore.AppName(Environment.ProcessPath ?? "switchmonitor.exe");
    private readonly HashSet<IntPtr> busy = [];
    private readonly AppUsage usage;
    private readonly Timer saver;

    /// <param name="areaOf">The work area of a local display by its key (null when it is not connected now).</param>
    public AppPlacer(AppPlacementStore store, AppUsage usage, Func<string, (int, int, int, int)?> areaOf)
    {
        this.store = store;
        this.usage = usage;
        this.areaOf = areaOf;
        saver = new Timer(_ => usage.Save(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        callback = OnWindow;   // kept in a field: Windows calls it for as long as the hooks live
        showHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, callback, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        frontHook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, callback, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
    }

    public void Stop()
    {
        saver.Dispose();
        usage.Save();
        if (showHook != IntPtr.Zero) UnhookWinEvent(showHook);
        if (frontHook != IntPtr.Zero) UnhookWinEvent(frontHook);
    }

    private void OnWindow(IntPtr hookHandle, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || childId != 0 || window == IntPtr.Zero) return;
        if (eventType == EventSystemForeground && AppLinker.ExeOf(window) is { } front) usage.Record(front);
        if (store.IsEmpty) return;
        if (GetAncestor(window, GaRoot) != window || GetWindow(window, GwOwner) != IntPtr.Zero) return;   // a main window, not a dialog of one
        long style = GetWindowLongPtr(window, GwlStyle).ToInt64(), extended = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        if ((style & WsVisible) == 0 || (extended & WsExToolWindow) != 0) return;   // no tooltips, menus or hidden helpers
        string? exe = AppLinker.ExeOf(window);
        if (exe is null || exe == ownExe || store.PlaceOf(exe) is null) return;
        lock (busy) { if (!busy.Add(window)) return; }
        _ = PlaceAsync(window, exe);
    }

    /// <summary>This program's own window opened (its events are not delivered to it): placed like any other program.</summary>
    public void PlaceNow(IntPtr window)
    {
        if (window == IntPtr.Zero || store.PlaceOf(ownExe) is null) return;
        lock (busy) { if (!busy.Add(window)) return; }
        _ = PlaceAsync(window, ownExe);
    }

    private async Task PlaceAsync(IntPtr window, string exe)
    {
        try
        {
            foreach (int wait in new[] { 350, 1200, 2500 })
            {
                await Task.Delay(wait);
                if (!IsWindow(window) || store.PlaceOf(exe) is not { } place || areaOf(place.Display) is not { } area) return;
                Apply(window, place.Mode, area);
            }
        }
        finally { lock (busy) busy.Remove(window); }
    }

    private static void Apply(IntPtr window, string mode, (int Left, int Top, int Width, int Height) area)
    {
        if (!GetWindowRect(window, out var rect)) return;
        bool maximized = IsZoomed(window), minimized = IsIconic(window);
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width < 120 || height < 80) return;   // still a splash or a helper
        bool onDisplay = Inside(rect, area);

        switch (mode)
        {
            case OpenMode.Maximized:
                if (maximized && onDisplay) return;
                if (maximized || minimized) ShowWindow(window, SwRestore);
                MoveCentered(window, rect, area);
                ShowWindow(window, SwMaximize);
                break;
            case OpenMode.Half:
                int halfWidth = area.Width / 2;
                if (!maximized && !minimized && Math.Abs(rect.Left - area.Left) < 12 && Math.Abs(width - halfWidth) < 24 && Math.Abs(height - area.Height) < 24) return;
                if (maximized || minimized) ShowWindow(window, SwRestore);
                SetWindowPos(window, IntPtr.Zero, area.Left, area.Top, halfWidth, area.Height, SwpNoZOrder | SwpNoActivate);
                break;
            case OpenMode.Minimized:
                if (minimized) return;
                if (!onDisplay) MoveCentered(window, rect, area);   // it comes back to the right display when restored
                ShowWindow(window, SwShowMinNoActive);
                break;
            default:
                if (minimized || onDisplay) return;
                MoveCentered(window, rect, area);
                break;
        }
    }

    private static bool Inside(Rect rect, (int Left, int Top, int Width, int Height) area)
    {
        int centerX = rect.Left + (rect.Right - rect.Left) / 2, centerY = rect.Top + (rect.Bottom - rect.Top) / 2;
        return centerX >= area.Left && centerX < area.Left + area.Width && centerY >= area.Top && centerY < area.Top + area.Height;
    }

    private static void MoveCentered(IntPtr window, Rect rect, (int Left, int Top, int Width, int Height) area)
    {
        int width = Math.Min(rect.Right - rect.Left, area.Width), height = Math.Min(rect.Bottom - rect.Top, area.Height);
        int x = area.Left + (area.Width - width) / 2, y = area.Top + (area.Height - height) / 2;
        SetWindowPos(window, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
    }

    private const uint EventObjectShow = 0x8002, EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;
    private const uint GaRoot = 2, GwOwner = 4;
    private const int GwlStyle = -16, GwlExStyle = -20;
    private const long WsVisible = 0x10000000, WsExToolWindow = 0x80;
    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010;
    private const int SwMaximize = 3, SwShowMinNoActive = 7, SwRestore = 9;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
