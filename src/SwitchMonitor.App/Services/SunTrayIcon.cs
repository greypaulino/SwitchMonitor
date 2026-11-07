using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The sun in the notification area (1.x "brillo"): a click opens the brightness panel, a right click opens its menu card.
/// The icon is drawn at run time from the brightness glyph, in white, so it reads on the dark taskbar.
/// </summary>
public sealed class SunTrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Icon picture;

    public SunTrayIcon(Action toggle, Action menu)
    {
        picture = DrawSun();
        icon = new Forms.NotifyIcon { Icon = picture, Text = "SwitchMonitor brightness", Visible = true };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) toggle();
            else if (e.Button == Forms.MouseButtons.Right) menu();
        };
    }

    /// <summary>
    /// A bold sun that fills the icon (a solid disc and eight thick rounded rays), drawn large and scaled down by Windows,
    /// so it reads as clearly as the other tray icons; the font glyph was thin and small there.
    /// </summary>
    private static Icon DrawSun()
    {
        int size = Math.Max(32, Forms.SystemInformation.SmallIconSize.Width * 2);
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float c = size / 2f;
            float disc = size * 0.25f;
            g.FillEllipse(Brushes.White, c - disc, c - disc, disc * 2, disc * 2);
            using var ray = new Pen(Color.White, size * 0.11f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                float inner = size * 0.36f, outer = size * 0.45f;
                g.DrawLine(ray, c + (float)Math.Cos(angle) * inner, c + (float)Math.Sin(angle) * inner,
                    c + (float)Math.Cos(angle) * outer, c + (float)Math.Sin(angle) * outer);
            }
        }
        IntPtr handle = bitmap.GetHicon();
        using var temporary = Icon.FromHandle(handle);
        var copy = (Icon)temporary.Clone();
        DestroyIcon(handle);
        return copy;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        picture.Dispose();
    }
}
