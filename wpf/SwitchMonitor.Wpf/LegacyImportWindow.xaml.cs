using System.Windows;
using System.Windows.Interop;

namespace SwitchMonitor.Wpf;

public partial class LegacyImportWindow : Window
{
    internal LegacyImportWindow(Window owner, string summary)
    {
        InitializeComponent();
        Owner = owner;
        SummaryText.Text = summary;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
