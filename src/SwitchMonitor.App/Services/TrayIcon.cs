using Forms = System.Windows.Forms;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The SwitchMonitor icon in the notification area: a double click opens the window, a right click opens its menu card
/// (drawn by <see cref="TrayMenu"/>, above the taskbar so it never covers the icon).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private Action? balloonClick;

    public TrayIcon(Action open, Action menu)
    {
        icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "SwitchMonitor v2 (preview)",
            Visible = true
        };
        icon.DoubleClick += (_, _) => open();
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Right) menu(); };
        icon.BalloonTipClicked += (_, _) => balloonClick?.Invoke();
    }

    /// <summary>A notice above the tray; a click on it runs <paramref name="onClick"/> (the menu stays as it is).</summary>
    public void Balloon(string title, string text, Action onClick)
    {
        balloonClick = onClick;
        icon.ShowBalloonTip(10000, title, text, Forms.ToolTipIcon.Info);
    }

    /// <summary>
    /// The program's own icon: from inside the exe (works in the single-file publish, where no .ico sits beside it), else the
    /// icon file beside it, else the exe's file icon; the generic application icon only as a last resort.
    /// </summary>
    internal static System.Drawing.Icon LoadIcon()
    {
        try
        {
            if (System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/monitor-switch.ico")) is { } resource)
                using (resource.Stream) return new System.Drawing.Icon(resource.Stream);
        }
        catch (Exception error) when (error is IOException or ArgumentException or UriFormatException) { }
        string file = Path.Combine(AppContext.BaseDirectory, "monitor-switch.ico");
        if (File.Exists(file)) return new System.Drawing.Icon(file);
        try { if (Environment.ProcessPath is { } exe && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } own) return own; }
        catch (Exception error) when (error is ArgumentException or IOException) { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
