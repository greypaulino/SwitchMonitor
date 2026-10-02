using System.Windows;
using System.Windows.Interop;

namespace SwitchMonitor.Wpf;

public partial class AboutWindow : Window
{
    internal AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = "WPF migration preview · version " +
            typeof(AboutWindow).Assembly.GetName().Version?.ToString(3);
        DataText.Text = "Settings: " + Preferences.SettingsPath;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
