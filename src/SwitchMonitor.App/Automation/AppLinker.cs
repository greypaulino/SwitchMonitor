using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Schedule;

namespace SwitchMonitor.App.Automation;

/// <summary>
/// Linked programs: when a program linked to a profile comes to the front, that profile is applied gradually; when
/// another program comes to the front (or it closes), the displays go back gradually to what they had before.
/// Windows tells the app about every foreground change (no polling); a short wait lets Alt+Tab pass without switching.
/// </summary>
public sealed class AppLinker
{
    private readonly MainViewModel main;
    private readonly AutomationStore store;
    private readonly ICoreLog log;
    private readonly DispatcherTimer settle;
    private readonly WinEventProc callback;
    private readonly IntPtr hook;
    private readonly string ownExe = SceneStore.AppName(Environment.ProcessPath ?? "switchmonitor.exe");
    private string? pendingExe;
    private SceneProfile? current;
    private string? returnTo;
    private bool switching;

    /// <summary>How long a program must stay in front before its profile is applied (Alt+Tab passes by).</summary>
    public static TimeSpan SettleTime { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Length of the gradual change in and out of a linked profile.</summary>
    public static TimeSpan Fade { get; } = TimeSpan.FromSeconds(3);

    public AppLinker(MainViewModel main, AutomationStore store, ICoreLog log)
    {
        this.main = main;
        this.store = store;
        this.log = log;
        settle = new DispatcherTimer { Interval = SettleTime };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            _ = EvaluateAsync(pendingExe);
        };
        callback = OnForeground;   // kept in a field: Windows calls it for as long as the hook lives
        hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, callback, 0, 0,
            WinEventOutOfContext | WinEventSkipOwnProcess);
        if (hook == IntPtr.Zero) log.Write(LogLevel.Warning, "Linked programs: Windows did not accept the foreground watch.");
    }

    /// <summary>A linked program's profile is on now.</summary>
    public bool IsActive => current is not null;

    public bool Enabled
    {
        get => store.AppsEnabled;
        set
        {
            store.SetAppsEnabled(value);
            if (!value && current is not null) _ = LeaveAsync();
        }
    }

    public void Stop()
    {
        settle.Stop();
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
    }

    private void OnForeground(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        string? exe = ExeOf(window);
        if (exe is null || exe == ownExe) return;   // the app itself (editing a profile) changes nothing
        pendingExe = exe;
        settle.Stop();
        settle.Start();
    }

    private async Task EvaluateAsync(string? exe)
    {
        if (exe is null || switching) return;
        var target = store.AppsEnabled ? main.SceneProfiles.FirstOrDefault(s => s.IsLinkedTo(exe)) : null;
        if (target?.Name == current?.Name) return;
        switching = true;
        try
        {
            if (target is null) await LeaveAsync();
            else
            {
                // Remember what to come back to only when entering from "no linked program".
                if (current is null) returnTo = main.ActiveProfile;
                current = target;
                log.Write(LogLevel.Info, $"{exe} in front: profile {target.Name}.");
                await main.ApplySceneAsync(target, Fade, automatic: true);
            }
        }
        finally { switching = false; }
        // The foreground may have changed again while the profile was being applied.
        if (pendingExe != exe) await EvaluateAsync(pendingExe);
    }

    private async Task LeaveAsync()
    {
        if (current is null) return;
        log.Write(LogLevel.Info, $"Linked program left the front: back to {returnTo ?? MainViewModel.CustomName}.");
        current = null;
        var back = returnTo is null ? null
            : main.SceneProfiles.FirstOrDefault(s => string.Equals(s.Name, returnTo, StringComparison.OrdinalIgnoreCase));
        returnTo = null;
        if (back is not null) await main.ApplySceneAsync(back, Fade, automatic: true);
        else await main.RestoreCustomAsync(Fade);
    }

    /// <summary>"game.exe" for a window, or null (desktop, closed process, no access).</summary>
    internal static string? ExeOf(IntPtr window) => PathOf(window) is { } full ? SceneStore.AppName(full) : null;

    /// <summary>Full path of the program that owns a window, or null.</summary>
    internal static string? PathOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0) return null;
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(process); }
    }

    /// <summary>Programs with a window now, for "Add app" (name, window title and full path), sorted by name.</summary>
    public static IReadOnlyList<(string Exe, string Title, string Path)> RunningApps()
    {
        var result = new Dictionary<string, (string Title, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle == IntPtr.Zero || PathOf(process.MainWindowHandle) is not { } full) continue;
                string exe = SceneStore.AppName(full);
                string title = string.IsNullOrWhiteSpace(process.MainWindowTitle) ? exe : process.MainWindowTitle.Trim();
                result.TryAdd(exe, (title, full));
            }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            finally { process.Dispose(); }
        }
        string own = SceneStore.AppName(Environment.ProcessPath ?? "");
        return result.Where(p => p.Key != own && p.Key != "explorer.exe")
            .OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Title, p.Value.Path)).ToArray();
    }

    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
}
