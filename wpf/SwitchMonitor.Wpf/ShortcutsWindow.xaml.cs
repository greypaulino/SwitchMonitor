using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace SwitchMonitor.Wpf;

public partial class ShortcutsWindow : Window
{
    private readonly Preferences preferences;
    private readonly IReadOnlyList<PhysicalMonitor> monitors;
    private readonly Func<PhysicalMonitor, int, Task> switchInput;
    private readonly Action openSettings;
    private readonly Action<PhysicalMonitor> selectMonitor;
    private readonly ObservableCollection<ShortcutViewRow> rows = [];
    private bool loading;

    internal ShortcutsWindow(Preferences preferences, IReadOnlyList<PhysicalMonitor> monitors,
        Func<PhysicalMonitor, int, Task> switchInput, Action openSettings,
        Action<PhysicalMonitor> selectMonitor)
    {
        InitializeComponent();
        this.preferences = preferences;
        this.monitors = monitors;
        this.switchInput = switchInput;
        this.openSettings = openSettings;
        this.selectMonitor = selectMonitor;
        InputRows.ItemsSource = rows;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        loading = true;
        MonitorChoice.ItemsSource = monitors;
        MonitorChoice.DisplayMemberPath = nameof(PhysicalMonitor.Name);
        MonitorChoice.SelectedItem = monitors.FirstOrDefault(m => m.Key == preferences.SelectedMonitor)
            ?? monitors.FirstOrDefault();
        MonitorChoice.IsEnabled = monitors.Count > 1;
        loading = false;
        RenderRows();
    }

    private void RenderRows()
    {
        rows.Clear();
        var monitor = MonitorChoice.SelectedItem as PhysicalMonitor;
        if (monitor is not null)
        {
            for (int index = 0; index < monitor.InputCodes.Count; index++)
            {
                int code = monitor.InputCodes[index];
                string chord = preferences.PortShortcuts.GetValueOrDefault(monitor.Key)?.GetValueOrDefault(code)
                    ?? (monitor == monitors.FirstOrDefault() && index < 9 ? $"Ctrl+Alt+{index + 1}" : "");
                string name = MonitorService.PortName(code);
                rows.Add(new ShortcutViewRow(code, name,
                    chord.Length > 0 ? chord : "Switch to " + name));
            }
        }
        if (rows.Count == 0) rows.Add(new ShortcutViewRow(null, "No shortcuts saved", "Open Settings"));
        string cycle = preferences.GlobalShortcuts.GetValueOrDefault("cycle", "");
        string settings = preferences.GlobalShortcuts.GetValueOrDefault("settings", "");
        Footer.Text = $"Next input: {(cycle.Length > 0 ? cycle : "None")}    Settings: {(settings.Length > 0 ? settings : "None")}";
        Height = Math.Max(430, Math.Min(670, 340 + rows.Count * 50));
    }

    private void MonitorChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || MonitorChoice.SelectedItem is not PhysicalMonitor monitor) return;
        selectMonitor(monitor);
        RenderRows();
    }

    private async void Input_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: ShortcutViewRow row }) return;
        if (row.Code is null)
        {
            Close();
            openSettings();
            return;
        }
        if (MonitorChoice.SelectedItem is PhysicalMonitor monitor)
            await switchInput(monitor, row.Code.Value);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed record ShortcutViewRow(int? Code, string Name, string Label);
