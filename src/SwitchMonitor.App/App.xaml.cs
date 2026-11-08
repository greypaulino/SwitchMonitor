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
    private const string RestartedArgument = "--restarted";
    private EventWaitHandle? activateSignal;
    private FileLog? log;
    private MainViewModel? model;
    private MainWindow? window;
    private TrayIcon? tray;
    private SunTrayIcon? sunTray;
    private Services.DesktopStarKey? desktopStar;
    private SunPanel? sunPanel;
    private ViewModels.SunPanelViewModel? sunPanelModel;
    private HotkeyService? hotkeys;
    private bool shortcutsPaused;
    private bool exiting;
    private string? demoScenario;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Cover self-test (--test-cover [folder]): only the black cover, photographed; no window, no monitor.
        int coverAt = Array.FindIndex(e.Args, a => a.Equals("--test-cover", StringComparison.OrdinalIgnoreCase));
        if (coverAt >= 0)
        {
            string folder = e.Args.ElementAtOrDefault(coverAt + 1) is { Length: > 0 } given && !given.StartsWith("--") ? given : Path.GetTempPath();
            Services.CoverSelfTest.Run(Dispatcher, folder, () => Shutdown());
            return;
        }

        // Icon self-test (--test-icon [folder]): the program finds its own icon without a loose .ico beside it.
        int iconAt = Array.FindIndex(e.Args, x => x.Equals("--test-icon", StringComparison.OrdinalIgnoreCase));
        if (iconAt >= 0)
        {
            string folder = e.Args.ElementAtOrDefault(iconAt + 1) is { Length: > 0 } given && !given.StartsWith("--") ? given : Path.GetTempPath();
            Services.CoverSelfTest.IconCheck(folder, () => Shutdown());
            return;
        }

        // Corner self-test (--test-corner [folder]): the main window's rounded corner against the desktop behind it.
        int cornerAt = Array.FindIndex(e.Args, x => x.Equals("--test-corner", StringComparison.OrdinalIgnoreCase));
        if (cornerAt >= 0)
        {
            string folder = e.Args.ElementAtOrDefault(cornerAt + 1) is { Length: > 0 } given && !given.StartsWith("--") ? given : Path.GetTempPath();
            Theme.Apply(Core.Settings.InterfaceSettings.Load());
            Services.CoverSelfTest.Corner(Dispatcher, folder, () => Shutdown());
            return;
        }

        // Menu self-test (--test-menu [folder]): the tray menu cards, photographed.
        int menuAt = Array.FindIndex(e.Args, x => x.Equals("--test-menu", StringComparison.OrdinalIgnoreCase));
        if (menuAt >= 0)
        {
            string folder = e.Args.ElementAtOrDefault(menuAt + 1) is { Length: > 0 } given && !given.StartsWith("--") ? given : Path.GetTempPath();
            Theme.Apply(Core.Settings.InterfaceSettings.Load());
            Services.CoverSelfTest.Menu(Dispatcher, folder, () => Shutdown());
            return;
        }

        // Flow self-test (--test-flow [folder]): the real profile pipeline on the in-memory demo monitor.
        int flowAt = Array.FindIndex(e.Args, x => x.Equals("--test-flow", StringComparison.OrdinalIgnoreCase));
        if (flowAt >= 0)
        {
            string folder = e.Args.ElementAtOrDefault(flowAt + 1) is { Length: > 0 } given && !given.StartsWith("--") ? given : Path.GetTempPath();
            string data = Path.Combine(Path.GetTempPath(), "SwitchMonitor-v2-flowtest");
            Directory.CreateDirectory(data);
            Environment.SetEnvironmentVariable("SWITCHMONITOR_DATA_DIR", data);
            Services.FlowSelfTest.Run(Dispatcher, folder, () => Shutdown());
            return;
        }

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
        // Started again after a backup was restored: wait for the previous instance to finish closing.
        if (!first && e.Args.Contains(RestartedArgument, StringComparer.OrdinalIgnoreCase))
        {
            try { first = instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; }
        }
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

        Theme.Apply(Core.Settings.InterfaceSettings.Load());   // the saved look, before the window appears
        model = new MainViewModel(log, settings)
        {
            DeviceSource = demoScenario is { } scenario ? () => DemoMonitors.Create(scenario, log) : null
        };
        // FreeSync changes in a profile black the screen out: cover it (and fade) instead of letting it flash.
        model.Transition = new Services.BlackoutOverlay(Dispatcher, text => log?.Write(LogLevel.Info, text)).CreateHooks();
        window = new MainWindow { DataContext = model };
        if (demoScenario is not null) window.Title += $" — demo data ({demoScenario})";
        window.Closing += (_, args) =>
        {
            if (exiting) return;
            if (demoScenario is not null) { ExitApp(); return; }   // demo: no tray icon, closing ends it
            args.Cancel = true;   // closing the window keeps the app in the tray
            model.IsDisplaysTab = true;   // leaving the Shortcuts tab resumes global shortcuts
            window.HideAnimated();   // fades and shrinks first, then goes to the tray
        };

        // The sun (1.x "brillo"): always in the notification area, also in demo mode (it only changes demo displays there).
        var sunModel = sunPanelModel = new ViewModels.SunPanelViewModel(model);
        sunModel.OpenWindowRequested += () => { sunPanel?.HidePanel(); ShowWindow(); };
        sunPanel = new SunPanel(sunModel);
        trayMenu = new TrayMenu();
        sunTray = new SunTrayIcon(() => sunPanel.Toggle(), () => trayMenu.Toggle(SunMenu()));
        // * alone on the desktop or the taskbar opens the panel too (elsewhere it types a * as usual).
        if (demoScenario is null) desktopStar = new Services.DesktopStarKey(() => sunPanel.ShowPanel());
        // A phone on the home Wi-Fi (Settings > Phone); not in demo mode, which must not answer the network.
        if (demoScenario is null && log is not null) model.Phone = new Services.PhoneBridge(model, sunModel, log);

        if (demoScenario is not null)
        {
            // No tray icon and no global shortcuts: the normal instance owns them.
            model.IsShortcutFree = _ => true;
            model.Updates.Enabled = false;   // demo: nothing is asked of the network
            ShowWindow();
            _ = model.RefreshAsync();
            return;
        }

        tray = new TrayIcon(ShowWindow, () => trayMenu.Toggle(MainMenu()));
        StartUpdates();
        hotkeys = new HotkeyService();
        model.ShortcutsChanged += RegisterHotkeys;
        model.RestartRequested += () =>
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, RestartedArgument) { UseShellExecute = false });
            ExitApp();
        };
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

        // Started by Windows at sign-in: stay in the notification area.
        bool atSignIn = e.Args.Any(a => a.Equals(Services.StartWithWindows.StartupArgument, StringComparison.OrdinalIgnoreCase));
        if (!settings.StartMinimized && !atSignIn) ShowWindow();
        if (settingsProblem is not null) model.Report(settingsProblem, false);
        displaySet = CurrentDisplaySet();
        _ = StartAsync();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e)
    {
        ReapplyColorSoon();
        CheckDisplaySetSoon();
    }

    private string displaySet = "";
    private System.Windows.Threading.DispatcherTimer? displaySetTimer;

    /// <summary>The displays Windows uses, as one string ("\\.\DISPLAY1|\\.\DISPLAY2").</summary>
    private static string CurrentDisplaySet() =>
        string.Join("|", Services.MainDisplay.Active.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// A moment after Windows changes the displays: when one was connected or disconnected (not for a resolution or a
    /// main-display change), list the displays again and put the main display back on the preferred one.
    /// </summary>
    private void CheckDisplaySetSoon()
    {
        Dispatcher.BeginInvoke(() =>
        {
            displaySetTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            displaySetTimer.Stop();
            displaySetTimer.Tick -= OnDisplaySetTick;
            displaySetTimer.Tick += OnDisplaySetTick;
            displaySetTimer.Start();
        });
    }

    private async void OnDisplaySetTick(object? sender, EventArgs e)
    {
        displaySetTimer?.Stop();
        if (model is null) return;
        string now = CurrentDisplaySet();
        if (now == displaySet) return;
        displaySet = now;
        log?.Write(LogLevel.Info, $"Displays in use changed: {now}.");
        await model.RefreshAsync();
        if (demoScenario is null && model.ApplyPreferredMain()) sunPanelModel?.RefreshMain();
    }

    /// <summary>First listing of the displays, then the preferred main display (as when a display is connected).</summary>
    private async Task StartAsync()
    {
        if (model is null) return;
        await model.RefreshAsync();
        if (demoScenario is null && model.ApplyPreferredMain()) sunPanelModel?.RefreshMain();
    }

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

    private TrayMenu? trayMenu;

    /// <summary>
    /// Updates: a check 5 seconds after start and every 6 hours (and when the window opens after that long), a notice above
    /// the tray the first time a version is found, and the installer's "updated" note. Nothing installs without a click.
    /// </summary>
    private void StartUpdates()
    {
        if (model is null) return;
        var updates = model.Updates;
        updates.UpdateFound += text => tray?.Balloon("SwitchMonitor update", text, () => { ShowWindow(); model.IsSettingsTab = true; });
        updates.InstallStarted += ExitApp;
        updates.AnnounceCompletedUpdate();
        var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        first.Tick += (_, _) => { first.Stop(); if (updates.IsDue) _ = updates.CheckAsync(silent: true); };
        first.Start();
        var every = new System.Windows.Threading.DispatcherTimer { Interval = Core.Updates.UpdateEditions.Detect() == Core.Updates.UpdateEdition.Packaged ? TimeSpan.FromDays(30) : ViewModels.UpdateViewModel.CheckEvery };
        every.Tick += (_, _) => { if (updates.IsDue) _ = updates.CheckAsync(silent: true); };
        every.Start();
    }

    /// <summary>The sun icon's menu: Exit, Start With Windows and the update line.</summary>
    private IReadOnlyList<TrayMenuItem> SunMenu()
    {
        var items = new List<TrayMenuItem>
        {
            new("Exit", "\uE711", ExitApp),
            new("Start With Windows", "\uE71D", () => { if (model is not null) model.StartWithWindows = !model.StartWithWindows; },
                () => model?.StartWithWindows ?? false)
        };
        if (UpdateItem() is { } update) items.Add(update);
        return items;
    }

    /// <summary>The SwitchMonitor icon's menu: Exit, Shortcuts, Edit a Profile, Open and the update line.</summary>
    private IReadOnlyList<TrayMenuItem> MainMenu()
    {
        var items = new List<TrayMenuItem>
        {
            new("Exit", "\uE711", ExitApp),
            new("Shortcuts", Icons.Shortcuts, OpenShortcuts),
            new("Edit a Profile", Icons.Profiles, EditProfile),
            new("Open", "\uE8A7", ShowWindow)
        };
        if (UpdateItem() is { } update) items.Add(update);
        return items;
    }

    /// <summary>
    /// The update line of both menus, decided each time a menu opens: "Check Updates", or, once a newer version is known,
    /// "Update available > Install Now" (the portable edition opens the release page). None for the Store edition and demos.
    /// </summary>
    private TrayMenuItem? UpdateItem()
    {
        var updates = model?.Updates;
        if (updates is null || !updates.CanCheck) return null;
        return updates.IsAvailable
            ? new TrayMenuItem("Update available > Install Now", "\uE896", () => _ = updates.InstallAsync())
            : new TrayMenuItem("Check Updates", "\uE72C", () => _ = CheckUpdatesFromMenu(updates));
    }

    /// <summary>A check asked for from a tray menu: the answer comes as a notice above the tray (the menu has closed).</summary>
    private async Task CheckUpdatesFromMenu(ViewModels.UpdateViewModel updates)
    {
        bool announced = false;
        void Mark(string _) => announced = true;   // a new version already raises its own notice
        updates.UpdateFound += Mark;
        try { await updates.CheckAsync(silent: false); }
        finally { updates.UpdateFound -= Mark; }
        if (announced) return;
        string text = updates.IsAvailable
            ? $"SwitchMonitor {updates.AvailableVersion} is available. Choose \"Update available > Install Now\" in the tray menu."
            : updates.Status;
        if (text.Length > 0) tray?.Balloon("SwitchMonitor update", text, () => { ShowWindow(); if (model is not null) model.IsSettingsTab = true; });
    }

    /// <summary>Shows the window on its Profiles tab (tray menu).</summary>
    private void EditProfile()
    {
        ShowWindow();
        if (model is not null) model.IsProfilesTab = true;
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

        // Brightness keys also bring up the panel (without taking the keyboard), so the new level can be seen.
        foreach (var chord in settings.For(HotkeyAction.BrightnessUp))
            Add("brightness up", chord, () => { model.StepBrightness(+1); sunPanel?.ShowPanel(activate: false); },
                held => { model.HoldBrightness(+1, held); sunPanel?.ShowPanel(activate: false); });
        foreach (var chord in settings.For(HotkeyAction.BrightnessDown))
            Add("brightness down", chord, () => { model.StepBrightness(-1); sunPanel?.ShowPanel(activate: false); },
                held => { model.HoldBrightness(-1, held); sunPanel?.ShowPanel(activate: false); });
        foreach (var chord in settings.For(HotkeyAction.BrightnessPanel))
            Add("brightness panel", chord, () => sunPanel?.Toggle());
        foreach (var chord in settings.For(HotkeyAction.NextInput))
            Add("next input", chord, () => _ = model.NextInputAsync());
        foreach (var chord in settings.For(HotkeyAction.OpenWindow))
            Add("open window", chord, ShowWindow);
        foreach (var chord in settings.For(HotkeyAction.CustomProfile))
            Add("profile Custom", chord, () => _ = model.RestoreCustomAsync());
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
    /// <summary>Icons take the chosen icon style when they load (App.xaml text block styles).</summary>
    private void RestyleIcon(object sender, RoutedEventArgs e) => IconRestyler.OnLoaded(sender);

    private void FocusSlider(object sender, MouseButtonEventArgs e)
    {
        if (sender is Slider slider && !slider.IsKeyboardFocusWithin) slider.Focus();
    }

    private void ShowWindow()
    {
        if (window is null) return;
        window.ShowAnimated();
        if (model?.Updates is { IsDue: true } due) _ = due.CheckAsync(silent: true);   // opened after a long time: look again
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
        sunTray?.Dispose();
        trayMenu?.Close();
        desktopStar?.Dispose();
        model?.Phone?.Dispose();
        sunPanel?.Close();
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
