using SwitchMonitor.Core.Scenes;
using Forms = System.Windows.Forms;

namespace SwitchMonitor.App.Services;

public sealed record TrayActions(Action Open, Action NextInput, Action ReturnToPrevious, Action Shortcuts, Action Exit);

/// <summary>Notification-area icon with the 1.x menu: open, next input, return to previous input, profiles, exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem nextInput;
    private readonly Forms.ToolStripMenuItem returnToPrevious;
    private readonly Forms.ToolStripMenuItem scenesMenu = new("Profiles");

    /// <summary>Raised just before the menu opens, so its items can reflect the current state.</summary>
    public event Action? Opening;

    public TrayIcon(TrayActions actions)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SwitchMonitor", null, (_, _) => actions.Open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        nextInput = new Forms.ToolStripMenuItem("Next input", null, (_, _) => actions.NextInput());
        returnToPrevious = new Forms.ToolStripMenuItem("Return to previous input", null, (_, _) => actions.ReturnToPrevious());
        menu.Items.Add(nextInput);
        menu.Items.Add(returnToPrevious);
        menu.Items.Add(scenesMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Shortcuts…", null, (_, _) => actions.Shortcuts());
        menu.Items.Add("Exit", null, (_, _) => actions.Exit());
        menu.Opening += (_, _) => Opening?.Invoke();

        string iconPath = Path.Combine(AppContext.BaseDirectory, "monitor-switch.ico");
        icon = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Text = "SwitchMonitor v2 (preview)",
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => actions.Open();
        scenesMenu.Enabled = false;
        returnToPrevious.Enabled = false;
    }

    /// <param name="nextInputShortcut">Shown beside Next input, e.g. "Ctrl+Alt+M".</param>
    /// <param name="previousInput">Label of the input to return to, or null when there is none.</param>
    public void Update(string? nextInputShortcut, string? previousInput, IEnumerable<SceneProfile> scenes, Action<SceneProfile> apply)
    {
        nextInput.ShortcutKeyDisplayString = nextInputShortcut ?? "";
        returnToPrevious.Text = previousInput is null ? "Return to previous input" : $"Return to previous input ({previousInput})";
        returnToPrevious.Enabled = previousInput is not null;

        scenesMenu.DropDownItems.Clear();
        foreach (var scene in scenes)
        {
            var item = new Forms.ToolStripMenuItem(scene.Name, null, (_, _) => apply(scene))
            {
                ShortcutKeyDisplayString = scene.Hotkey?.ToString() ?? ""
            };
            scenesMenu.DropDownItems.Add(item);
        }
        scenesMenu.Enabled = scenesMenu.DropDownItems.Count > 0;
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
