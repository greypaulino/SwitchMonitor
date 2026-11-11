using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SwitchMonitor.App.Services;

/// <summary>
/// "Send files": one large drop target for each device of the group that is connected (computers and phones). Drop files on a device,
/// or click it to pick them. The device asks its person to accept before anything is saved.
/// </summary>
internal sealed class SendFilesWindow : PairWindow
{
    private readonly FileTransfer transfers;
    private readonly List<string> ready = [];
    private readonly TextBlock readyText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12) };
    private static SendFilesWindow? current;

    /// <summary>Opens the window (or adds to the one already open) with files already chosen, from the Explorer right-click.</summary>
    public static void Open(FileTransfer transfers, IEnumerable<string> files)
    {
        if (current is null)
        {
            current = new SendFilesWindow(transfers);
            current.Closed += (_, _) => current = null;
            current.Show();
        }
        current.Add(files);
        current.Activate();
    }

    private void Add(IEnumerable<string> files)
    {
        foreach (string f in files) if (!ready.Contains(f, StringComparer.OrdinalIgnoreCase)) ready.Add(f);
        readyText.Text = ready.Count == 0 ? "" : ready.Count == 1
            ? $"Ready to send: {System.IO.Path.GetFileName(ready[0])}. Click the device to send it to."
            : $"{ready.Count} files ready. Click the device to send them to.";
        readyText.Foreground = Brushes.White;
    }

    public SendFilesWindow(FileTransfer transfers) : base("Send files")
    {
        this.transfers = transfers;
        var panel = new StackPanel { Margin = new Thickness(26, 22, 26, 22), MinWidth = 340 };
        panel.Children.Add(Text("Send files to a device", 17, FontWeights.SemiBold));
        panel.Children.Add(Text("Drop files on a device below, or click it to choose them. They are saved in its Downloads\\SwitchMonitor folder.", 13, FontWeights.Normal, Muted, new Thickness(0, 6, 0, 14)));
        panel.Children.Add(readyText);
        var targets = transfers.Targets();
        if (targets.Count == 0)
            panel.Children.Add(Text("No other device of your group is connected right now.", 13, FontWeights.Normal, Muted, new Thickness(0, 4, 0, 4)));
        foreach (var target in targets) panel.Children.Add(Card(target));
        panel.Children.Add(new Button { Content = "Close", IsCancel = true, MinWidth = 110, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) });
        Content = panel;
        Loaded += (_, _) => Activate();
    }

    private Border Card(TransferTarget target)
    {
        var name = new TextBlock { Text = target.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        var kind = new TextBlock { Text = target.IsPhone ? "Phone" : "Computer", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        var status = new TextBlock { Text = "Drop files here or click", Foreground = Muted, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var inside = new StackPanel { Margin = new Thickness(16, 18, 16, 18) };
        inside.Children.Add(name);
        inside.Children.Add(kind);
        inside.Children.Add(status);
        var card = new Border
        {
            Child = inside, AllowDrop = true, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1.5), BorderBrush = (Brush)FindResource("LineBrush"),
            Background = Brushes.Transparent,
        };
        System.Windows.Automation.AutomationProperties.SetName(card, $"Send files to {target.Name}");
        card.DragEnter += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; card.BorderBrush = Brushes.White; e.Handled = true; };
        card.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        card.DragLeave += (_, _) => card.BorderBrush = (Brush)FindResource("LineBrush");
        card.Drop += async (_, e) =>
        {
            card.BorderBrush = (Brush)FindResource("LineBrush");
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await SendAll(target, paths, status);
        };
        card.MouseLeftButtonUp += async (_, _) =>
        {
            if (ready.Count > 0)
            {
                var chosen = ready.ToArray();
                ready.Clear();
                readyText.Text = "";
                await SendAll(target, chosen, status);
                return;
            }
            var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Files to send to " + target.Name };
            if (dialog.ShowDialog(this) == true) await SendAll(target, dialog.FileNames, status);
        };
        return card;
    }

    private async Task SendAll(TransferTarget target, string[] paths, TextBlock status)
    {
        int sent = 0;
        foreach (string path in paths)
        {
            string shortName = System.IO.Path.GetFileName(path);
            status.Foreground = Muted;
            var progress = new Progress<double>(p => status.Text = $"Sending {shortName}… {p:P0}");
            string? problem = await transfers.SendFileAsync(target, path, progress);
            if (problem is not null)
            {
                status.Foreground = (Brush)FindResource("WarningTextBrush");
                status.Text = $"{shortName}: {problem}";
                return;
            }
            sent++;
        }
        status.Foreground = Brushes.White;
        status.Text = sent == 1 ? "Sent." : $"{sent} files sent.";
    }
}
