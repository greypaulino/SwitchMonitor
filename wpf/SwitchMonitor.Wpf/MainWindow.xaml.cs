using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace SwitchMonitor.Wpf;

public partial class MainWindow : Window
{
    private readonly Preferences preferences = Preferences.Load();
    private readonly ObservableCollection<PortRow> ports = [];
    private readonly ObservableCollection<BrightnessRow> displayedBrightness = [];
    private readonly List<BrightnessRow> physicalBrightness = [];
    private readonly Dictionary<string, (PhysicalMonitor Monitor, double Value, uint Maximum)> pendingBrightness = [];
    private readonly DispatcherTimer brightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private readonly DispatcherTimer displayChangeTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer brightnessHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private readonly SemaphoreSlim brightnessGate = new(1, 1);
    private readonly ReturnSyncCoordinator returnSync;
    private readonly Dictionary<int, Action> hotkeyActions = [];
    private readonly List<int> registeredHotkeys = [];
    private IReadOnlyList<PhysicalMonitor> monitors = [];
    private PhysicalMonitor? selected;
    private Forms.NotifyIcon? tray;
    private Forms.ContextMenuStrip? trayMenu;
    private Forms.NotifyIcon? sunTray;
    private BrightnessFlyout? brightnessFlyout;
    private SettingsWindow? settingsWindow;
    private ShortcutsWindow? shortcutsWindow;
    private AboutWindow? aboutWindow;
    private HwndSource? source;
    private bool updatingUi;
    private bool exiting;
    private bool refreshing;
    private bool initialLoadComplete;
    private bool showSettingsWhenReady;
    private bool checkingUpdates;
    private bool installingUpdate;
    private bool updateNoticeActive;
    private AvailableUpdate? availableUpdate;
    private readonly bool startHidden = Environment.GetCommandLineArgs()
        .Skip(1).Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase));
    private uint heldBrightnessKey;
    private int heldBrightnessDirection;
    private long brightnessHeldFrom;
    private long nextBrightnessStep;

    public MainWindow()
    {
        InitializeComponent();
        returnSync = new ReturnSyncCoordinator(message =>
        {
            if (!exiting && !Dispatcher.HasShutdownStarted)
                Dispatcher.BeginInvoke(() =>
                {
                    StatusText.Text = message;
                    tray?.ShowBalloonTip(3500, "SwitchMonitor", message, Forms.ToolTipIcon.Info);
                });
        });
        PortList.ItemsSource = ports;
        BrightnessRows.ItemsSource = displayedBrightness;
        LinkBrightness.IsChecked = preferences.LinkedBrightness;
        displayChangeTimer.Tick += async (_, _) =>
        {
            displayChangeTimer.Stop();
            await RefreshMonitorsAsync();
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        brightnessTimer.Tick += async (_, _) =>
        {
            brightnessTimer.Stop();
            await ApplyBrightnessAsync();
        };
        brightnessHoldTimer.Tick += (_, _) => PollBrightnessHold();
        updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(true);
        SourceInitialized += (_, _) =>
        {
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source?.AddHook(HotkeyMessage);
            SetDarkTitle();
        };
        Loaded += async (_, _) =>
        {
            InitializeTray();
            ShowUpdateCompletion();
            await RefreshMonitorsAsync();
            initialLoadComplete = true;
            if (!startHidden || showSettingsWhenReady) ShowSettings();
            Hide();
            updateTimer.Start();
            _ = CheckForUpdatesAfterStartupAsync();
        };
        Closing += (_, e) =>
        {
            if (!exiting)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            brightnessTimer.Stop();
            returnSync.Dispose();
            brightnessHoldTimer.Stop();
            updateTimer.Stop();
            displayChangeTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            UnregisterHotkeys();
            source?.RemoveHook(HotkeyMessage);
            tray?.Dispose();
            trayMenu?.Dispose();
            sunTray?.Dispose();
            brightnessFlyout?.Close();
            settingsWindow?.Close();
            shortcutsWindow?.Close();
            aboutWindow?.Close();
            foreach (var monitor in monitors) monitor.Dispose();
        };
    }

    private void SetDarkTitle()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    private void InitializeTray()
    {
        string iconPath = Path.Combine(AppContext.BaseDirectory, "monitor-switch.ico");
        trayMenu = new Forms.ContextMenuStrip { ShowItemToolTips = true };
        tray = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application,
            Text = "SwitchMonitor WPF preview",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        RebuildTrayMenu();
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ShowSettings();
        };
        tray.BalloonTipClicked += async (_, _) =>
        {
            if (updateNoticeActive) await InstallAvailableUpdateAsync();
        };
        brightnessFlyout = new BrightnessFlyout(displayedBrightness,
            () => preferences.LinkedBrightness, () => monitors.Count > 1,
            ToggleBrightnessLink, ShowSettings, QueueBrightness, SelectBrightnessRow);
        string sunPath = Path.Combine(AppContext.BaseDirectory, "brightness-sun.ico");
        sunTray = new Forms.NotifyIcon
        {
            Icon = File.Exists(sunPath) ? new Icon(sunPath) : SystemIcons.Information,
            Text = "SwitchMonitor WPF brightness preview",
            Visible = true
        };
        sunTray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) brightnessFlyout?.Toggle();
        };
    }

    private void RebuildTrayMenu()
    {
        if (trayMenu is null) return;
        trayMenu.Items.Clear();
        if (monitors.Count == 0)
            trayMenu.Items.Add(new Forms.ToolStripMenuItem("No monitor detected") { Enabled = false });
        else if (monitors.Count == 1)
            trayMenu.Items.Add(new Forms.ToolStripMenuItem(monitors[0].Name) { Enabled = false });
        else
            foreach (var monitor in monitors)
            {
                var item = new Forms.ToolStripMenuItem(monitor.Name)
                {
                    Checked = selected?.Key == monitor.Key
                };
                item.Click += (_, _) =>
                {
                    MonitorChoice.SelectedItem = monitor;
                };
                trayMenu.Items.Add(item);
            }
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Settings", null, (_, _) => ShowSettings());
        trayMenu.Items.Add("Shortcuts", null, (_, _) => ShowShortcuts());
        string nextChord = preferences.GlobalShortcuts.GetValueOrDefault("cycle", "");
        trayMenu.Items.Add($"Next input ({(nextChord.Length > 0 ? nextChord : "no shortcut")})",
            null, async (_, _) => await NextInputAsync());
        trayMenu.Items.Add("Brightness control", null, (_, _) => brightnessFlyout?.Reveal());
        if (availableUpdate is not null)
            trayMenu.Items.Add("Update available! Click to install", null,
                async (_, _) => await InstallAvailableUpdateAsync());
        else
        {
            var checkItem = new Forms.ToolStripMenuItem(
                checkingUpdates ? "Checking for updates..." : "Check for updates")
            {
                Enabled = !checkingUpdates
            };
            checkItem.Click += async (_, _) => await CheckForUpdatesAsync(false);
            trayMenu.Items.Add(checkItem);
        }
        trayMenu.Items.Add("About", null, (_, _) => ShowAbout());
        var startup = new Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = StartupManager.IsEnabled(),
            Enabled = StartupManager.CanEnable || StartupManager.IsEnabled(),
            ToolTipText = "Available after installing a stable WPF build."
        };
        startup.Click += (_, _) => ToggleStartup();
        trayMenu.Items.Add(startup);
        trayMenu.Items.Add("Exit", null, (_, _) => { exiting = true; Close(); });
    }

    private void ToggleStartup()
    {
        try
        {
            StartupManager.SetEnabled(!StartupManager.IsEnabled());
            RebuildTrayMenu();
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(error.Message, "Start with Windows",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task CheckForUpdatesAfterStartupAsync()
    {
        await Task.Delay(5000);
        if (!exiting) await CheckForUpdatesAsync(true);
    }

    private void ShowUpdateCompletion()
    {
        string marker = Path.Combine(Path.GetDirectoryName(Preferences.SettingsPath)!, "update-complete.txt");
        if (!File.Exists(marker)) return;
        string version;
        try
        {
            version = File.ReadAllText(marker).Trim();
            File.Delete(marker);
        }
        catch (IOException) { return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$")) return;
        var popup = new Window
        {
            Title = "SwitchMonitor update",
            Width = 330,
            Height = 100,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(37, 37, 38)),
            Foreground = System.Windows.Media.Brushes.White,
            Content = new TextBlock
            {
                Text = $"SwitchMonitor WPF {version} was installed.",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            },
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        popup.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); popup.Close(); };
        timer.Start();
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (checkingUpdates || exiting) return;
        checkingUpdates = true;
        RebuildTrayMenu();
        try
        {
            var result = await UpdateService.CheckAsync();
            if (exiting) return;
            bool newlyFound = result is not null && result.Version != availableUpdate?.Version;
            availableUpdate = result;
            if (result is null) updateNoticeActive = false;
            if (newlyFound)
            {
                updateNoticeActive = true;
                tray?.ShowBalloonTip(5000, "SwitchMonitor update",
                    $"SwitchMonitor WPF {result!.Version.ToString(3)} is available. Click here to update.",
                    Forms.ToolTipIcon.Info);
            }
            else if (!silent)
            {
                tray?.ShowBalloonTip(3500, "SwitchMonitor update",
                    result is null ? "No compatible WPF update is available." :
                    $"Version {result.Version.ToString(3)} is ready to install from the tray menu.",
                    Forms.ToolTipIcon.Info);
            }
        }
        catch (Exception error)
        {
            if (!silent && !exiting)
                System.Windows.MessageBox.Show(error.Message, "Check for updates",
                    MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            checkingUpdates = false;
            if (!exiting) RebuildTrayMenu();
        }
    }

    private async Task InstallAvailableUpdateAsync()
    {
        if (availableUpdate is null || installingUpdate || exiting) return;
        updateNoticeActive = false;
        string helper = Path.Combine(AppContext.BaseDirectory, "Update-Wpf.ps1");
        string installedFlag = Path.Combine(AppContext.BaseDirectory, "installed-wpf.flag");
        if (!File.Exists(installedFlag) || !File.Exists(helper))
        {
            System.Windows.MessageBox.Show("Automatic installation is available from the installed WPF edition. " +
                "This preview will keep running without changing the installed AutoHotkey edition.",
                "SwitchMonitor update", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        installingUpdate = true;
        try
        {
            tray?.ShowBalloonTip(3500, "SwitchMonitor update",
                $"Downloading SwitchMonitor WPF {availableUpdate.Version.ToString(3)}...", Forms.ToolTipIcon.Info);
            string installer = await UpdateService.DownloadVerifiedAsync(availableUpdate);
            tray?.ShowBalloonTip(3500, "SwitchMonitor update",
                "Download verified. Installing update...", Forms.ToolTipIcon.Info);
            var process = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            process.ArgumentList.Add("-NoProfile");
            process.ArgumentList.Add("-ExecutionPolicy");
            process.ArgumentList.Add("Bypass");
            process.ArgumentList.Add("-WindowStyle");
            process.ArgumentList.Add("Hidden");
            process.ArgumentList.Add("-File");
            process.ArgumentList.Add(helper);
            process.ArgumentList.Add("-Installer");
            process.ArgumentList.Add(installer);
            process.ArgumentList.Add("-ExpectedHash");
            process.ArgumentList.Add(availableUpdate.Sha256);
            process.ArgumentList.Add("-ProcessId");
            process.ArgumentList.Add(Environment.ProcessId.ToString());
            process.ArgumentList.Add("-AppDir");
            process.ArgumentList.Add(AppContext.BaseDirectory);
            if (Process.Start(process) is null)
                throw new InvalidOperationException("Could not launch the update installer.");
            exiting = true;
            Close();
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(error.Message, "SwitchMonitor update",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            installingUpdate = false;
        }
    }

    private void ShowAbout()
    {
        if (aboutWindow is { IsVisible: true })
        {
            aboutWindow.Activate();
            return;
        }
        aboutWindow = new AboutWindow();
        aboutWindow.Closed += (_, _) => aboutWindow = null;
        aboutWindow.Show();
    }

    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowSettings()
    {
        if (settingsWindow is { IsVisible: true })
        {
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(preferences, monitors,
            async () => { await RefreshMonitorsAsync(); return monitors; },
            RefreshMonitorsAsync,
            paused => { if (paused) UnregisterHotkeys(); else RegisterHotkeys(); });
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
    }

    internal void RequestShowSettings()
    {
        if (!initialLoadComplete)
        {
            showSettingsWhenReady = true;
            return;
        }
        ShowSettings();
    }

    private void ShowShortcuts()
    {
        if (shortcutsWindow is { IsVisible: true })
        {
            shortcutsWindow.Activate();
            return;
        }
        shortcutsWindow = new ShortcutsWindow(preferences, monitors, SwitchInputAsync, ShowSettings,
            monitor => MonitorChoice.SelectedItem = monitor);
        shortcutsWindow.Closed += (_, _) => shortcutsWindow = null;
        shortcutsWindow.Show();
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (exiting) return;
        Dispatcher.BeginInvoke(() =>
        {
            displayChangeTimer.Stop();
            displayChangeTimer.Start();
        });
    }

    private async Task RefreshMonitorsAsync()
    {
        if (refreshing) return;
        refreshing = true;
        updatingUi = true;
        StatusText.Text = "Finding physical monitors...";
        MonitorChoice.IsEnabled = false;
        try
        {
            var found = await Task.Run(MonitorService.Enumerate);
            shortcutsWindow?.Close();
            brightnessTimer.Stop();
            pendingBrightness.Clear();
            await brightnessGate.WaitAsync();
            try
            {
                foreach (var old in monitors) old.Dispose();
            }
            finally { brightnessGate.Release(); }
            physicalBrightness.Clear();
            displayedBrightness.Clear();
            monitors = found;
            await Task.Run(() =>
            {
                foreach (var device in found)
                    try { device.ReadInputs(); } catch { }
            });
            MonitorChoice.ItemsSource = monitors;
            MonitorChoice.DisplayMemberPath = nameof(PhysicalMonitor.Name);
            MonitorChoice.SelectedItem = monitors.FirstOrDefault(m => m.Key == preferences.SelectedMonitor)
                ?? monitors.FirstOrDefault();
            MonitorChoice.IsEnabled = monitors.Count > 1;
            LinkBrightness.IsEnabled = monitors.Count > 1;
            LinkBrightness.IsChecked = preferences.LinkedBrightness;
            if (!monitors.Any(m => m.Key == preferences.BrightnessMonitor))
                preferences.BrightnessMonitor = monitors.FirstOrDefault()?.Key ?? "";
            if (monitors.Count == 0)
            {
                selected = null;
                ports.Clear();
                StatusText.Text = "Windows did not expose a physical monitor through DDC/CI.";
            }
            RebuildTrayMenu();
        }
        catch (Exception error)
        {
            StatusText.Text = "Monitor detection failed: " + error.Message;
        }
        finally
        {
            updatingUi = false;
        }
        try
        {
            if (MonitorChoice.SelectedItem is PhysicalMonitor monitor)
            {
                await LoadMonitorAsync(monitor);
                await LoadBrightnessRowsAsync();
            }
        }
        finally { refreshing = false; }
    }

    private async Task LoadMonitorAsync(PhysicalMonitor monitor)
    {
        selected = monitor;
        preferences.SelectedMonitor = monitor.Key;
        SavePreferences();
        updatingUi = true;
        ports.Clear();
        UpdateBrightnessActive();
        InputHint.Text = "Reading monitor capabilities...";
        StatusText.Text = "Reading " + monitor.Name + "...";
        try
        {
            var data = await Task.Run(() =>
            {
                monitor.ReadInputs();
                int currentInput = 0;
                try { currentInput = (int)monitor.ReadInput().Current; }
                catch (InvalidOperationException) { }
                return currentInput;
            });
            if (selected != monitor) return;
            var saved = preferences.ConnectedInputs.GetValueOrDefault(monitor.Key);
            foreach (int code in monitor.InputCodes)
                ports.Add(new PortRow(code, MonitorService.PortName(code), saved?.Contains(code) ?? false,
                    !monitor.IsLegacyLg || monitor.HasIntelLegacyBackend || monitor.HasAmdLegacyBackend));
            InputHint.Text = monitor.IsLegacyLg
                ? monitor.HasIntelLegacyBackend
                    ? "LG 29WK600 uses the validated Intel driver path. Confirm switches on the monitor menu."
                    : monitor.HasAmdLegacyBackend
                        ? "LG 29WK600 uses AMD ADL2 (experimental). Confirm the selected display and switch on the monitor menu."
                        : "LG 29WK600 input commands need a supported GPU DDC backend."
                : ports.Count == 0
                    ? "No input list was advertised over DDC/CI. No input command will be guessed."
                    : "Mark the connected inputs for Next. Ctrl+Alt+1, 2, 3 select the first three inputs.";
            NextButton.IsEnabled = ports.Any(p => p.Connected && p.CanSwitch);
            StatusText.Text = data > 0
                ? $"Current input: {MonitorService.PortName(data)}"
                : "Monitor ready. Input reading may be unavailable on this connection.";
        }
        catch (Exception error)
        {
            StatusText.Text = "Monitor read failed: " + error.Message;
        }
        finally
        {
            updatingUi = false;
            RegisterHotkeys();
            RebuildTrayMenu();
        }
    }

    private async void MonitorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingUi && MonitorChoice.SelectedItem is PhysicalMonitor monitor)
        {
            preferences.BrightnessMonitor = monitor.Key;
            await LoadMonitorAsync(monitor);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshMonitorsAsync();

    private void Connected_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null) return;
        preferences.ConnectedInputs[selected.Key] = ports.Where(p => p.Connected).Select(p => p.Code).ToList();
        NextButton.IsEnabled = ports.Any(p => p.Connected && p.CanSwitch);
        SavePreferences();
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: int code }) await SwitchInputAsync(code);
    }

    private Task SwitchInputAsync(int code) => selected is null ? Task.CompletedTask : SwitchInputAsync(selected, code);

    private async Task SwitchInputAsync(PhysicalMonitor monitor, int code)
    {
        try
        {
            returnSync.Cancel(monitor.Key);
            int previous = 0;
            if (monitor.IsLegacyLg && monitor.HasIntelLegacyBackend)
            {
                try { previous = (int)(await Task.Run(monitor.ReadInput)).Current; }
                catch { }
            }
            StatusText.Text = $"Requesting {MonitorService.PortName(code)} on {monitor.Name}...";
            await Task.Run(() => monitor.SetInput(code));
            returnSync.Arm(monitor, previous, code);
            StatusText.Text = $"Requested {MonitorService.PortName(code)}. Check the monitor menu to confirm.";
        }
        catch (Exception error) { StatusText.Text = error.Message; }
    }

    private async void Next_Click(object sender, RoutedEventArgs e) => await NextInputAsync();

    private async Task NextInputAsync()
    {
        var monitor = selected;
        if (monitor is null) return;
        var candidates = ports.Where(p => p.Connected && p.CanSwitch).ToArray();
        if (candidates.Length == 0)
        {
            StatusText.Text = "Mark at least one connected input first.";
            return;
        }
        int current;
        try { current = (int)(await Task.Run(monitor.ReadInput)).Current; }
        catch (Exception error)
        {
            StatusText.Text = "Cannot read the current input. No switch sent: " + error.Message;
            return;
        }
        if (selected != monitor) return;
        if (!monitor.InputCodes.Contains(current))
        {
            StatusText.Text = $"Unknown current input ({current}). No switch sent.";
            return;
        }
        int position = Array.FindIndex(monitor.InputCodes.ToArray(), code => code == current);
        for (int step = 1; step <= monitor.InputCodes.Count; step++)
        {
            int code = monitor.InputCodes[(position + step) % monitor.InputCodes.Count];
            if (!candidates.Any(candidate => candidate.Code == code)) continue;
            if (code == current)
            {
                StatusText.Text = "No other marked input is available.";
                return;
            }
            await SwitchInputAsync(monitor, code);
            return;
        }
    }

    private async Task LoadBrightnessRowsAsync()
    {
        var current = monitors;
        var readings = await Task.Run(() => current.Select(monitor =>
        {
            try
            {
                var (value, maximum) = monitor.ReadBrightness();
                return (Monitor: monitor, Value: maximum == 0 ? 0d : 100d * value / maximum,
                    Maximum: maximum);
            }
            catch { return (Monitor: monitor, Value: 0d, Maximum: 0u); }
        }).ToArray());
        if (!ReferenceEquals(current, monitors)) return;
        physicalBrightness.Clear();
        foreach (var reading in readings)
        {
            var row = new BrightnessRow(reading.Monitor.Name, reading.Value,
                reading.Maximum > 0, [new BrightnessTarget(reading.Monitor, reading.Maximum)]);
            physicalBrightness.Add(row);
        }
        RefreshBrightnessView();
    }

    private void RefreshBrightnessView()
    {
        bool previous = updatingUi;
        updatingUi = true;
        displayedBrightness.Clear();
        if (preferences.LinkedBrightness && physicalBrightness.Count > 1)
        {
            var supported = physicalBrightness.Where(row => row.Supported).ToArray();
            displayedBrightness.Add(new BrightnessRow(
                string.Join(Environment.NewLine, physicalBrightness.Select(row => row.Name)),
                supported.FirstOrDefault()?.Value ?? 0, supported.Length > 0,
                supported.SelectMany(row => row.Targets).ToArray()) { Active = true, ShowDot = true });
        }
        else
        {
            foreach (var row in physicalBrightness)
            {
                row.ShowDot = physicalBrightness.Count > 1;
                row.Active = row.Targets[0].Monitor.Key == preferences.BrightnessMonitor;
                displayedBrightness.Add(row);
            }
        }
        updatingUi = previous;
        brightnessFlyout?.RefreshLink();
    }

    private void UpdateBrightnessActive()
    {
        if (physicalBrightness.Count > 0) RefreshBrightnessView();
    }

    private void SelectBrightnessRow(BrightnessRow row)
    {
        if (row.Targets.Count == 0 || preferences.LinkedBrightness) return;
        preferences.BrightnessMonitor = row.Targets[0].Monitor.Key;
        SavePreferences();
        UpdateBrightnessActive();
    }

    private void ToggleBrightnessLink()
    {
        if (monitors.Count < 2) return;
        preferences.LinkedBrightness = !preferences.LinkedBrightness;
        LinkBrightness.IsChecked = preferences.LinkedBrightness;
        SavePreferences();
        RefreshBrightnessView();
        brightnessFlyout?.RefreshLink();
    }

    private void BrightnessRow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrightnessRow row } ||
            preferences.LinkedBrightness || row.Targets.Count == 0) return;
        preferences.BrightnessMonitor = row.Targets[0].Monitor.Key;
        SavePreferences();
        UpdateBrightnessActive();
    }

    private void NextBrightnessMonitor()
    {
        if (preferences.LinkedBrightness || physicalBrightness.Count < 2) return;
        int index = physicalBrightness.FindIndex(row => row.Targets[0].Monitor.Key == preferences.BrightnessMonitor);
        var next = physicalBrightness[(index + 1) % physicalBrightness.Count];
        preferences.BrightnessMonitor = next.Targets[0].Monitor.Key;
        SavePreferences();
        UpdateBrightnessActive();
        StatusText.Text = "Brightness target: " + next.Name;
        brightnessFlyout?.Reveal();
    }

    private void LinkBrightness_Click(object sender, RoutedEventArgs e)
    {
        preferences.LinkedBrightness = LinkBrightness.IsChecked == true;
        SavePreferences();
        RefreshBrightnessView();
        brightnessFlyout?.RefreshLink();
    }

    private void BrightnessRowSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingUi || sender is not Slider { DataContext: BrightnessRow row } || !row.Supported) return;
        QueueBrightness(row, e.NewValue);
    }

    private void QueueBrightness(BrightnessRow row, double value)
    {
        if (updatingUi || !row.Supported) return;
        foreach (var target in row.Targets)
            pendingBrightness[target.Monitor.Key] = (target.Monitor, value, target.Maximum);
        brightnessTimer.Stop();
        brightnessTimer.Start();
    }

    private async Task ApplyBrightnessAsync()
    {
        if (pendingBrightness.Count == 0) return;
        var changes = pendingBrightness.Values.ToArray();
        pendingBrightness.Clear();
        await brightnessGate.WaitAsync();
        try
        {
            var failures = new List<string>();
            foreach (var change in changes)
            {
                if (!monitors.Contains(change.Monitor)) continue;
                try
                {
                    await Task.Run(() => change.Monitor.SetBrightnessPercent(change.Value, change.Maximum));
                }
                catch (Exception error)
                {
                    failures.Add($"{change.Monitor.Name}: {error.Message}");
                }
            }
            StatusText.Text = failures.Count == 0
                ? $"Brightness: {changes[0].Value:0}%"
                : "Brightness failed: " + string.Join("; ", failures);
        }
        finally { brightnessGate.Release(); }
    }

    private void SavePreferences()
    {
        try { preferences.Save(); }
        catch (IOException error) { StatusText.Text = "Could not save settings: " + error.Message; }
        catch (UnauthorizedAccessException error) { StatusText.Text = "Could not save settings: " + error.Message; }
    }

    private void RegisterHotkeys()
    {
        if (source is null) return;
        UnregisterHotkeys();
        IntPtr hwnd = source.Handle;
        void Add(int id, uint modifiers, uint key, Action action)
        {
            if (!Native.RegisterHotKey(hwnd, id, modifiers, key)) return;
            registeredHotkeys.Add(id);
            hotkeyActions[id] = action;
        }
        int id = 100;
        foreach (var monitor in monitors)
        {
            for (int i = 0; i < monitor.InputCodes.Count; i++)
            {
                int code = monitor.InputCodes[i];
                string chordText = preferences.PortShortcuts.GetValueOrDefault(monitor.Key)?.GetValueOrDefault(code)
                    ?? (monitor == monitors.FirstOrDefault() && i < 9 ? $"Ctrl+Alt+{i + 1}" : "");
                if (ShortcutChord.TryParse(chordText, out var chord))
                    Add(id++, chord.Modifiers, chord.VirtualKey,
                        () => _ = SwitchInputAsync(monitor, code));
            }
        }
        void AddGlobal(int hotkeyId, string kind, Action action)
        {
            if (preferences.GlobalShortcuts.TryGetValue(kind, out var value) &&
                ShortcutChord.TryParse(value, out var chord))
                Add(hotkeyId, chord.Modifiers, chord.VirtualKey, action);
        }
        AddGlobal(1, "cycle", () => _ = NextInputAsync());
        AddGlobal(2, "settings", ShowSettings);
        void AddBrightness(int hotkeyId, string kind, int direction)
        {
            if (preferences.GlobalShortcuts.TryGetValue(kind, out var value) &&
                ShortcutChord.TryParse(value, out var chord))
                Add(hotkeyId, chord.Modifiers, chord.VirtualKey,
                    () => BeginBrightnessHold(direction, chord.VirtualKey));
        }
        AddBrightness(3, "brightnessUp", 1);
        AddBrightness(4, "brightnessDown", -1);
        AddGlobal(5, "brightnessNext", NextBrightnessMonitor);
        // The main keyboard's Shift+8 is an alternative to numpad Multiply.
        if (preferences.GlobalShortcuts.GetValueOrDefault("brightnessNext") == "Ctrl+Alt+NumpadMultiply")
            Add(6, 0x7, '8', NextBrightnessMonitor);
        if (registeredHotkeys.Count == 0)
            StatusText.Text += " The existing SwitchMonitor may be using these shortcuts.";
    }

    private void UnregisterHotkeys()
    {
        brightnessHoldTimer.Stop();
        heldBrightnessKey = 0;
        if (source is null) return;
        foreach (int id in registeredHotkeys) Native.UnregisterHotKey(source.Handle, id);
        registeredHotkeys.Clear();
        hotkeyActions.Clear();
    }

    private void ChangeBrightness(int delta)
    {
        var row = preferences.LinkedBrightness && displayedBrightness.Count == 1
            ? displayedBrightness[0]
            : displayedBrightness.FirstOrDefault(item => item.Targets.Any(target =>
                target.Monitor.Key == preferences.BrightnessMonitor));
        if (row?.Supported == true)
        {
            row.Value = Math.Clamp(row.Value + delta, 0, 100);
            QueueBrightness(row, row.Value);
            brightnessFlyout?.Reveal();
        }
    }

    private void BeginBrightnessHold(int direction, uint virtualKey)
    {
        if (heldBrightnessKey != 0) return;
        heldBrightnessKey = virtualKey;
        heldBrightnessDirection = direction;
        brightnessHeldFrom = Environment.TickCount64;
        nextBrightnessStep = brightnessHeldFrom + 250;
        ChangeBrightness(direction);
        brightnessHoldTimer.Start();
    }

    private void PollBrightnessHold()
    {
        if (heldBrightnessKey == 0 || (Native.GetAsyncKeyState((int)heldBrightnessKey) & 0x8000) == 0)
        {
            heldBrightnessKey = 0;
            brightnessHoldTimer.Stop();
            return;
        }
        long now = Environment.TickCount64;
        if (now < nextBrightnessStep) return;
        long held = now - brightnessHeldFrom;
        ChangeBrightness(heldBrightnessDirection * (held >= 2000 ? 10 : 5));
        nextBrightnessStep = now + (held >= 2000 ? 130 : 190);
    }

    private IntPtr HotkeyMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x312 || !hotkeyActions.TryGetValue(wParam.ToInt32(), out Action? action))
            return IntPtr.Zero;
        action();
        handled = true;
        return IntPtr.Zero;
    }
}

internal sealed class PortRow(int code, string name, bool connected, bool canSwitch)
{
    public int Code { get; } = code;
    public string Name { get; } = name;
    public bool Connected { get; set; } = connected;
    public bool CanSwitch { get; } = canSwitch;
}

internal readonly record struct BrightnessTarget(PhysicalMonitor Monitor, uint Maximum);

internal sealed class BrightnessRow(string name, double value, bool supported, IReadOnlyList<BrightnessTarget> targets)
    : INotifyPropertyChanged
{
    private double value = value;
    private bool active;
    public string Name { get; } = name;
    public bool Supported { get; } = supported;
    public IReadOnlyList<BrightnessTarget> Targets { get; } = targets;
    public bool ShowDot { get; set; }
    public bool Active
    {
        get => active;
        set { active = value; PropertyChanged?.Invoke(this, new(nameof(Active))); }
    }
    public double Value
    {
        get => value;
        set { this.value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
