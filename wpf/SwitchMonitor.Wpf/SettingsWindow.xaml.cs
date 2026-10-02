using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SwitchMonitor.Wpf;

public partial class SettingsWindow : Window
{
    private readonly Preferences preferences;
    private readonly Func<Task<IReadOnlyList<PhysicalMonitor>>> detect;
    private readonly Func<Task> apply;
    private readonly Action<bool> pauseHotkeys;
    private readonly ObservableCollection<SettingsPortRow> rows = [];
    private readonly Dictionary<string, List<int>> connected;
    private readonly Dictionary<string, Dictionary<int, string>> portShortcuts;
    private readonly Dictionary<string, string> globalShortcuts;
    private IReadOnlyList<PhysicalMonitor> monitors;
    private bool linked;
    private bool loading;
    private bool dirty;

    internal SettingsWindow(Preferences preferences, IReadOnlyList<PhysicalMonitor> monitors,
        Func<Task<IReadOnlyList<PhysicalMonitor>>> detect, Func<Task> apply, Action<bool> pauseHotkeys)
    {
        InitializeComponent();
        this.preferences = preferences;
        this.monitors = monitors;
        this.detect = detect;
        this.apply = apply;
        this.pauseHotkeys = pauseHotkeys;
        connected = preferences.ConnectedInputs.ToDictionary(p => p.Key, p => p.Value.ToList());
        portShortcuts = preferences.PortShortcuts.ToDictionary(p => p.Key,
            p => p.Value.ToDictionary(item => item.Key, item => item.Value));
        foreach (var monitor in monitors)
        {
            if (!portShortcuts.TryGetValue(monitor.Key, out var shortcuts))
                portShortcuts[monitor.Key] = shortcuts = [];
            int index = 0;
            foreach (int code in monitor.InputCodes)
            {
                if (!shortcuts.ContainsKey(code))
                    shortcuts[code] = monitor == monitors[0] && index < 9 ? $"Ctrl+Alt+{index + 1}" : "";
                index++;
            }
        }
        globalShortcuts = new(preferences.GlobalShortcuts);
        linked = preferences.LinkedBrightness;
        InputRows.ItemsSource = rows;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        Loaded += async (_, _) =>
        {
            FillMonitorChoice();
            await LoadSelectedMonitorAsync();
        };
        RenderGlobals();
    }

    private void FillMonitorChoice()
    {
        loading = true;
        string key = (MonitorChoice.SelectedItem as PhysicalMonitor)?.Key ?? preferences.SelectedMonitor;
        MonitorChoice.ItemsSource = monitors;
        MonitorChoice.DisplayMemberPath = nameof(PhysicalMonitor.Name);
        MonitorChoice.SelectedItem = monitors.FirstOrDefault(m => m.Key == key) ?? monitors.FirstOrDefault();
        MonitorChoice.IsEnabled = monitors.Count > 1;
        LinkButton.IsEnabled = monitors.Count > 1;
        loading = false;
    }

    private async Task LoadSelectedMonitorAsync()
    {
        rows.Clear();
        if (MonitorChoice.SelectedItem is not PhysicalMonitor monitor)
        {
            StatusText.Text = "No physical monitor detected.";
            return;
        }
        StatusText.Text = "Reading " + monitor.Name + "...";
        try
        {
            await Task.Run(monitor.ReadInputs);
            if (MonitorChoice.SelectedItem != monitor) return;
            if (!connected.TryGetValue(monitor.Key, out var marked))
                connected[monitor.Key] = marked = [];
            if (!portShortcuts.TryGetValue(monitor.Key, out var shortcuts))
                portShortcuts[monitor.Key] = shortcuts = [];
            bool first = monitor == monitors.FirstOrDefault();
            int index = 0;
            foreach (int code in monitor.InputCodes)
            {
                if (!shortcuts.TryGetValue(code, out string? chord))
                    shortcuts[code] = chord = first && index < 9 ? $"Ctrl+Alt+{index + 1}" : "";
                rows.Add(new SettingsPortRow(code, MonitorService.PortName(code), marked.Contains(code), chord));
                index++;
            }
            StatusText.Text = monitor.InputCodes.Count == 0
                ? "No input list was advertised by this monitor."
                : "Click a shortcut to record a new key combination.";
        }
        catch (Exception error) { StatusText.Text = "Could not read inputs: " + error.Message; }
    }

    private void RenderGlobals()
    {
        CycleShortcut.Content = Display("cycle");
        SettingsShortcut.Content = Display("settings");
        DownShortcut.Content = Display("brightnessDown");
        UpShortcut.Content = Display("brightnessUp");
        NextMonitorShortcut.Content = Display("brightnessNext");
        LinkButton.Content = linked ? "Linked" : "Independent";
    }

    private string Display(string kind) => globalShortcuts.GetValueOrDefault(kind) is { Length: > 0 } chord
        ? chord : "Click to assign";

    private void MarkDirty()
    {
        dirty = true;
        CloseButton.Visibility = Visibility.Collapsed;
        DirtyButtons.Visibility = Visibility.Visible;
    }

    private async void MonitorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && IsLoaded) await LoadSelectedMonitorAsync();
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (dirty)
        {
            StatusText.Text = "Save or cancel changes before detecting monitors again.";
            return;
        }
        StatusText.Text = "Detecting monitors...";
        try
        {
            monitors = await detect();
            FillMonitorChoice();
            await LoadSelectedMonitorAsync();
        }
        catch (Exception error) { StatusText.Text = "Detection failed: " + error.Message; }
    }

    private void Connected_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorChoice.SelectedItem is not PhysicalMonitor monitor) return;
        if (sender is System.Windows.Controls.CheckBox { DataContext: SettingsPortRow clickedRow } check)
            clickedRow.Connected = check.IsChecked == true;
        connected[monitor.Key] = rows.Where(row => row.Connected).Select(row => row.Code).ToList();
        MarkDirty();
    }

    private void PortShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: SettingsPortRow row } ||
            MonitorChoice.SelectedItem is not PhysicalMonitor monitor) return;
        var captured = Capture();
        if (captured is null) return;
        if (!ValidateShortcut(captured, monitor.Key, row.Code)) return;
        portShortcuts[monitor.Key][row.Code] = captured;
        row.Shortcut = captured;
        MarkDirty();
    }

    private void GlobalShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string kind }) return;
        var captured = Capture();
        if (captured is null) return;
        if (!ValidateShortcut(captured, null, null, kind)) return;
        globalShortcuts[kind] = captured;
        RenderGlobals();
        MarkDirty();
    }

    private string? Capture()
    {
        pauseHotkeys(true);
        try
        {
            var dialog = new ShortcutCaptureWindow(this);
            return dialog.ShowDialog() == true ? dialog.Result : null;
        }
        finally { pauseHotkeys(false); }
    }

    private bool ValidateShortcut(string value, string? monitorKey, int? code, string? kind = null)
    {
        if (value.Length == 0) return true;
        if (!ShortcutChord.TryParse(value, out var proposed))
        {
            StatusText.Text = "Use 2 to 4 keys with at least one modifier.";
            return false;
        }
        foreach (var entry in globalShortcuts)
            if (entry.Key != kind && ShortcutChord.TryParse(entry.Value, out var other) &&
                other.Identity == proposed.Identity)
            {
                StatusText.Text = value + " is already used by another global shortcut.";
                return false;
            }
        foreach (var monitor in portShortcuts)
            foreach (var entry in monitor.Value)
                if ((monitor.Key != monitorKey || entry.Key != code) &&
                    ShortcutChord.TryParse(entry.Value, out var other) && other.Identity == proposed.Identity)
                {
                    StatusText.Text = value + " is already used by another input.";
                    return false;
                }
        return true;
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (monitors.Count < 2) return;
        linked = !linked;
        RenderGlobals();
        MarkDirty();
    }

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (MonitorChoice.SelectedItem is not PhysicalMonitor monitor) return;
        try
        {
            StatusText.Text = "Checking connection...";
            var result = await Task.Run(() =>
            {
                var brightness = monitor.ReadBrightness();
                string input;
                try { input = MonitorService.PortName((int)monitor.ReadInput().Current); }
                catch { input = "unavailable"; }
                return $"{monitor.Name}: brightness {brightness.Current}/{brightness.Maximum}, input {input}.";
            });
            StatusText.Text = result;
        }
        catch (Exception error) { StatusText.Text = "Connection check failed: " + error.Message; }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = "SwitchMonitor-diagnostics.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var report = await Task.Run(() =>
            {
                var text = new StringBuilder("SwitchMonitor WPF monitor diagnostics\n");
                    text.AppendLine("Read-only report; no brightness or input commands were sent.");
                    text.AppendLine("AMD ADL driver DLL: " +
                        File.Exists(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll")));
                foreach (var monitor in monitors)
                {
                    text.AppendLine($"{monitor.Name} | key={monitor.Key} | GDI={monitor.DisplayName}");
                    text.AppendLine($"Hardware ID: {monitor.DeviceId}");
                    text.AppendLine($"LG identification: {monitor.IsLegacyLg}; standard DDC handle: {monitor.HasStandardDdc}; Intel backend: {monitor.HasIntelLegacyBackend}");
                    foreach (string device in MonitorService.DescribeWindowsDevices(monitor.DisplayName))
                        text.AppendLine(device);
                    try { text.AppendLine("Inputs: " + string.Join(", ", monitor.InputCodes.Select(MonitorService.PortName))); }
                    catch (Exception error) { text.AppendLine("Inputs error: " + error.Message); }
                    try { text.AppendLine("DDC/CI capabilities: " + monitor.ReadCapabilitiesForDiagnostics()); }
                    catch (Exception error) { text.AppendLine("DDC/CI capabilities error: " + error.Message); }
                    try { var b = monitor.ReadBrightness(); text.AppendLine($"Brightness: {b.Current}/{b.Maximum}"); }
                    catch (Exception error) { text.AppendLine("Brightness error: " + error.Message); }
                    try { var input = monitor.ReadInput(); text.AppendLine($"VCP 60 input: {input.Current}/{input.Maximum}"); }
                    catch (Exception error) { text.AppendLine("VCP 60 input error: " + error.Message); }
                }
                return text.ToString();
            });
            File.WriteAllText(dialog.FileName, report);
            StatusText.Text = "Diagnostics exported.";
        }
        catch (Exception error) { StatusText.Text = "Export failed: " + error.Message; }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (dirty) { StatusText.Text = "Save changes before creating a backup."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "JSON file (*.json)|*.json", FileName = "SwitchMonitor-settings.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(preferences,
                new JsonSerializerOptions { WriteIndented = true }));
            StatusText.Text = "Settings backup saved.";
        }
        catch (Exception error) { StatusText.Text = "Backup failed: " + error.Message; }
    }

    private async void ImportLegacy_Click(object sender, RoutedEventArgs e)
    {
        if (dirty) { StatusText.Text = "Save or cancel changes before importing."; return; }
        string defaultPath = LegacySettingsImporter.InstalledPath;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "SwitchMonitor INI (*.ini)|*.ini",
            FileName = File.Exists(defaultPath) ? defaultPath : "switchMonitor.ini",
            InitialDirectory = File.Exists(defaultPath)
                ? Path.GetDirectoryName(defaultPath) : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var preview = LegacySettingsImporter.Preview(picker.FileName, preferences, monitors);
            var dialog = new LegacyImportWindow(this, preview.Summary);
            if (dialog.ShowDialog() != true) return;
            if (File.Exists(Preferences.SettingsPath))
            {
                string backup = Path.Combine(Path.GetDirectoryName(Preferences.SettingsPath)!,
                    "settings-before-ahk-import.json");
                File.Copy(Preferences.SettingsPath, backup, true);
            }
            preferences.SelectedMonitor = preview.Proposed.SelectedMonitor;
            preferences.BrightnessMonitor = preview.Proposed.BrightnessMonitor;
            preferences.ConnectedInputs = preview.Proposed.ConnectedInputs;
            preferences.PortShortcuts = preview.Proposed.PortShortcuts;
            preferences.GlobalShortcuts = preview.Proposed.GlobalShortcuts;
            preferences.LinkedBrightness = preview.Proposed.LinkedBrightness;
            preferences.Save();
            await apply();
            Close();
        }
        catch (Exception error) { StatusText.Text = "Import failed: " + error.Message; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (dirty) { StatusText.Text = "Save or cancel changes before restoring."; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "JSON file (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var restored = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(dialog.FileName))
                ?? throw new InvalidDataException("The backup is empty.");
            ShortcutProfileValidator.Validate(restored);
            if (System.Windows.MessageBox.Show(this, "Replace the current WPF settings with this backup?", "Restore settings",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            preferences.ConnectedInputs = restored.ConnectedInputs;
            preferences.PortShortcuts = restored.PortShortcuts;
            preferences.GlobalShortcuts = restored.GlobalShortcuts;
            preferences.LinkedBrightness = restored.LinkedBrightness;
            preferences.SelectedMonitor = restored.SelectedMonitor;
            preferences.BrightnessMonitor = restored.BrightnessMonitor;
            preferences.Save();
            await apply();
            StatusText.Text = "Settings restored. Reopen Settings to view them.";
            Close();
        }
        catch (Exception error) { StatusText.Text = "Restore failed: " + error.Message; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ShortcutProfileValidator.Validate(globalShortcuts, portShortcuts);
            preferences.ConnectedInputs = connected;
            preferences.PortShortcuts = portShortcuts;
            preferences.GlobalShortcuts = globalShortcuts;
            preferences.LinkedBrightness = linked;
            if (MonitorChoice.SelectedItem is PhysicalMonitor current)
                preferences.SelectedMonitor = current.Key;
            preferences.Save();
            await apply();
            Close();
        }
        catch (Exception error) { StatusText.Text = "Save failed: " + error.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class SettingsPortRow(int code, string name, bool connected, string shortcut) : INotifyPropertyChanged
{
    private string shortcut = shortcut;
    public int Code { get; } = code;
    public string Name { get; } = name;
    public bool Connected { get; set; } = connected;
    public string Shortcut
    {
        get => string.IsNullOrEmpty(shortcut) ? "Click to assign" : shortcut;
        set { shortcut = value; PropertyChanged?.Invoke(this, new(nameof(Shortcut))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
