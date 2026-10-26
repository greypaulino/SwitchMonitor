using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwitchMonitor.App.Services;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.App;

public partial class App : Application
{
    private const string InstanceName = "SwitchMonitor.v2.SingleInstance";
    private Mutex? instance;
    private EventWaitHandle? activateSignal;
    private FileLog? log;
    private MainViewModel? model;
    private MainWindow? window;
    private TrayIcon? tray;
    private HotkeyService? hotkeys;
    private bool shortcutsPaused;
    private bool exiting;
    private string? demoScenario;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Demo mode (--demo [demo|worst|empty]): in-memory displays and a throwaway data folder, for looking at the
        // interface without touching a monitor or the real settings. It runs beside the normal instance.
        int demoAt = Array.FindIndex(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        if (demoAt >= 0)
        {
            demoScenario = e.Args.ElementAtOrDefault(demoAt + 1) is { } s && DemoMonitors.Scenarios.Contains(s) ? s : "demo";
            string data = Path.Combine(Path.GetTempPath(), "SwitchMonitor-v2-demo", demoScenario);
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, Core.Scenes.SceneStore.FileName), DemoMonitors.ScenesJson(demoScenario));
            Environment.SetEnvironmentVariable("SWITCHMONITOR_DATA_DIR", data);
        }

        // One instance: a second launch just brings the first window forward.
        string instanceName = demoScenario is null ? InstanceName : InstanceName + ".Demo";
        instance = new Mutex(true, instanceName, out bool first);
        activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + ".Activate");
        if (!first)
        {
            activateSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (activateSignal.WaitOne() && !exiting) Dispatcher.Invoke(ShowWindow);
        }) { IsBackground = true, Name = "SwitchMonitor activation" };
        listener.Start();

        log = new FileLog(AppPaths.LogDirectory);
        log.Write(LogLevel.Info, "SwitchMonitor v2 starting.");
        DispatcherUnhandledException += (_, args) =>
        {
            log.Write(LogLevel.Error, "Unhandled UI error.", args.Exception);
            model?.Report($"Unexpected error — {args.Exception.Message}", false);
            args.Handled = true;
        };

        AppSettings settings;
        string? settingsProblem = null;
        try { settings = AppSettings.LoadOrCreate(); }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException)
        {
            log.Write(LogLevel.Warning, "settings.json could not be read; using defaults.", error);
            settings = new AppSettings();
            settingsProblem = $"settings.json could not be read ({error.Message}); defaults are in use until it is fixed.";
        }

        model = new MainViewModel(log, settings)
        {
            DeviceSource = demoScenario is { } scenario ? () => DemoMonitors.Create(scenario, log) : null
        };
        window = new MainWindow { DataContext = model };
        if (demoScenario is not null) window.Title += $" — demo data ({demoScenario})";
        window.Closing += (_, args) =>
        {
            if (exiting) return;
            if (demoScenario is not null) { ExitApp(); return; }   // demo: no tray icon, closing ends it
            args.Cancel = true;   // closing the window keeps the app in the tray
            model.IsDisplaysTab = true;   // leaving the Shortcuts tab resumes global shortcuts
            window.Hide();
        };

        if (demoScenario is not null)
        {
            // No tray icon and no global shortcuts: the normal instance owns them.
            model.IsShortcutFree = _ => true;
            ShowWindow();
            _ = model.RefreshAsync();
            return;
        }

        tray = new TrayIcon(new TrayActions(
            Open: ShowWindow,
            NextInput: () => _ = model.NextInputAsync(),
            ReturnToPrevious: () => _ = model.ReturnToPreviousInputAsync(),
            Shortcuts: OpenShortcuts,
            Exit: ExitApp));
        tray.Opening += UpdateTray;
        hotkeys = new HotkeyService();
        model.ShortcutsChanged += RegisterHotkeys;
        model.IsShortcutFree = hotkeys.IsFree;
        model.ShortcutsPaused += paused =>
        {
            shortcutsPaused = paused;
            if (paused) hotkeys.UnregisterAll();
            else RegisterHotkeys();
        };

        // Windows resets the gamma ramp after sleep and when displays change: put the software color back.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        if (!settings.StartMinimized) ShowWindow();
        if (settingsProblem is not null) model.Report(settingsProblem, false);
        _ = model.RefreshAsync();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => ReapplyColorSoon();

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume) ReapplyColorSoon();
    }

    /// <summary>A moment later, so the driver has finished setting the display up again.</summary>
    private void ReapplyColorSoon()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            model?.ReapplySoftwareColor();
        };
        Dispatcher.BeginInvoke(timer.Start);
    }

    private void UpdateTray()
    {
        if (model is null || tray is null) return;
        tray.Update(model.Settings.For(HotkeyAction.NextInput).Select(c => c.ToString()).FirstOrDefault(),
            model.CanReturnToPreviousInput ? model.PreviousInputLabel : null,
            model.SceneProfiles, scene => _ = model.ApplySceneAsync(scene));
    }

    /// <summary>Shows the window on its Shortcuts tab (tray menu).</summary>
    private void OpenShortcuts()
    {
        ShowWindow();
        if (model is not null) model.IsShortcutsTab = true;
    }

    private void RegisterHotkeys()
    {
        if (hotkeys is null || model is null || shortcutsPaused) return;   // paused while the Shortcuts tab is open
        hotkeys.UnregisterAll();
        var settings = model.Settings;
        var failed = new List<string>();
        void Add(string owner, Core.Hotkeys.HotkeyChord chord, Action press, Action<TimeSpan>? hold = null)
        {
            if (!hotkeys.Register(chord, press, hold)) failed.Add($"{chord} ({owner})");
        }

        foreach (var chord in settings.For(HotkeyAction.BrightnessUp))
            Add("brightness up", chord, () => model.StepBrightness(+1), held => model.HoldBrightness(+1, held));
        foreach (var chord in settings.For(HotkeyAction.BrightnessDown))
            Add("brightness down", chord, () => model.StepBrightness(-1), held => model.HoldBrightness(-1, held));
        foreach (var chord in settings.For(HotkeyAction.NextMonitor))
            Add("next display", chord, model.NextBrightnessTarget);
        foreach (var chord in settings.For(HotkeyAction.NextInput))
            Add("next input", chord, () => _ = model.NextInputAsync());
        foreach (var chord in settings.For(HotkeyAction.OpenWindow))
            Add("open window", chord, ShowWindow);
        foreach (var input in settings.InputShortcuts)
            Add(model.DescribeInputShortcut(input), input.Chord, () => _ = model.SwitchInputByKeyAsync(input.MonitorKey, input.InputCode));
        foreach (var scene in model.SceneProfiles)
            if (scene.Hotkey is { } chord) Add($"profile {scene.Name}", chord, () => _ = model.ApplySceneAsync(scene));

        var conflicts = settings.FindConflicts(model.SceneProfiles.Select(s => ($"profile {s.Name}", s.Hotkey)), model.DescribeInputShortcut);
        var problems = new List<string>();
        if (conflicts.Count > 0) problems.Add("Shortcut conflict: " + string.Join(" ", conflicts));
        if (failed.Count > 0)
            problems.Add((OtherSwitchMonitorRunning()
                ? "SwitchMonitor 1.x is running and owns these shortcuts — exit it from its tray icon, then press Retry: "
                : "Another program owns these shortcuts: ") + string.Join(", ", failed) + ".");
        model.ShortcutWarning = problems.Count == 0 ? null : string.Join(" ", problems);
        if (problems.Count > 0) log?.Write(LogLevel.Warning, model.ShortcutWarning!);
        else log?.Write(LogLevel.Info, "All shortcuts registered.");
    }

    /// <summary>True when an older SwitchMonitor (1.x AutoHotkey or WPF preview) is running from another folder.</summary>
    private static bool OtherSwitchMonitorRunning()
    {
        string own = Environment.ProcessPath ?? "";
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string name = process.ProcessName;
                if (!name.StartsWith("SwitchMonitor", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("AutoHotkey", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    string path = process.MainModule?.FileName ?? "";
                    if (string.Equals(path, own, StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.StartsWith("SwitchMonitor", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains("SwitchMonitor", StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        return false;
    }

    /// <summary>Clicking a slider's track gives it keyboard focus, so the arrow keys move it afterwards.</summary>
    private void FocusSlider(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && !slider.IsKeyboardFocusWithin) slider.Focus();
    }

    private void ShowWindow()
    {
        if (window is null) return;
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void ExitApp()
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        exiting = true;
        activateSignal?.Set();
        window?.Close();
        hotkeys?.Dispose();
        tray?.Dispose();
        model?.Shutdown();
        log?.Write(LogLevel.Info, "SwitchMonitor v2 stopped.");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        activateSignal?.Dispose();
        base.OnExit(e);
    }
}
