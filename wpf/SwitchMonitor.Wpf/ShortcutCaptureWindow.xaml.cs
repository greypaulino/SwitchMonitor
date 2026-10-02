using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SwitchMonitor.Wpf;

public partial class ShortcutCaptureWindow : Window
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DateTime started = DateTime.UtcNow;
    public string? Result { get; private set; }

    public ShortcutCaptureWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
        };
        timer.Tick += (_, _) =>
        {
            double left = Math.Max(0, 3 - (DateTime.UtcNow - started).TotalSeconds);
            Countdown.Text = Result is null ? $"{Math.Ceiling(left)} seconds remaining"
                : $"{Result}  ·  {Math.Ceiling(left)} s";
            if (left > 0) return;
            timer.Stop();
            DialogResult = true;
        };
        Loaded += (_, _) => { Focus(); timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }

    private void Capture_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Delete)
        {
            Result = "";
            DialogResult = true;
            e.Handled = true;
            return;
        }
        if (ShortcutChord.TryFromKey(key, Keyboard.Modifiers, out var chord))
        {
            Result = chord.Label;
            Countdown.Text = Result + "  ·  recording";
            e.Handled = true;
        }
    }
}
