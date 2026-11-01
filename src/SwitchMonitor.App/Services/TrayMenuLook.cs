using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace SwitchMonitor.App.Services;

/// <summary>
/// The notification-area menus drawn like the app's own menus: the interface color as ground, a hairline edge,
/// rounded corners, rounded highlight rows and the same text colors. Colors are read when a menu opens, so a new
/// interface color shows at once.
/// </summary>
public static class TrayMenuLook
{
    private const int Radius = 12;

    public static void Apply(Forms.ToolStripDropDown menu)
    {
        menu.Renderer = new Renderer();
        menu.Font = new Font("Segoe UI", 10f);
        menu.Padding = new Forms.Padding(5, 6, 5, 6);
        menu.DropShadowEnabled = false;
        if (menu is Forms.ToolStripDropDownMenu items)
        {
            items.ShowImageMargin = false;
            items.ShowCheckMargin = false;
        }
        menu.Opening += (_, _) =>
        {
            menu.BackColor = Ground;
            foreach (Forms.ToolStripItem item in menu.Items) Style(item);
        };
        menu.SizeChanged += (_, _) => Round(menu);
        menu.Opened += (_, _) => Round(menu);
    }

    private static void Style(Forms.ToolStripItem item)
    {
        item.ForeColor = Text;
        if (item is Forms.ToolStripSeparator) return;
        item.Padding = new Forms.Padding(6, 6, 10, 6);
        if (item is Forms.ToolStripMenuItem { DropDown: { } sub } menuItem && menuItem.HasDropDownItems && sub.Renderer is not Renderer)
            Apply(sub);
    }

    /// <summary>Rounded window shape (Windows 10 has no rounded menus of its own).</summary>
    private static void Round(Forms.Control menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0) return;
        using var path = RoundedRectangle(new Rectangle(0, 0, menu.Width, menu.Height), Radius);
        menu.Region = new Region(path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle box, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color FromBrush(string key, Color fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush { Color: var c }
            ? Color.FromArgb(255, c.R, c.G, c.B) : fallback;

    private static Color Ground => FromBrush("PopupBrush", Color.FromArgb(0x26, 0x26, 0x26));
    private static Color Text => Color.FromArgb(0xF7, 0xF7, 0xF7);
    private static Color Faint => Color.FromArgb(0x80, 0x80, 0x80);

    private static Color Blend(Color under, int alpha) =>
        Color.FromArgb(under.R + (255 - under.R) * alpha / 255, under.G + (255 - under.G) * alpha / 255, under.B + (255 - under.B) * alpha / 255);

    private sealed class Renderer : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var ground = new SolidBrush(Ground);
            e.Graphics.FillRectangle(ground, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var path = RoundedRectangle(box, Radius);
            using var edge = new Pen(Blend(Ground, 0x2A));
            e.Graphics.DrawPath(edge, path);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = RoundedRectangle(box, 8);
            using var hover = new SolidBrush(Blend(Ground, 0x1C));
            e.Graphics.FillPath(hover, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Faint;
            if (e.TextFormat.HasFlag(Forms.TextFormatFlags.Right)) e.TextColor = e.Item.Enabled ? Color.FromArgb(0xBD, 0xBD, 0xBD) : Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var line = new Pen(Blend(Ground, 0x16));
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(line, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderArrow(Forms.ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item?.Enabled == false ? Faint : Text;
            base.OnRenderArrow(e);
        }
    }
}
