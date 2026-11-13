using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Scenes;

namespace SwitchMonitor.App.Services;

/// <summary>
/// Keeps the profiles the same on the computers of the group (when the switch in Settings > Devices is on). This computer sends its
/// profiles to the others when one connects, when the person changes a profile here and when the switch is turned on; what arrives is
/// joined with the local ones one profile at a time, the newest change winning (see <see cref="SceneSync"/>). A computer that applies what
/// it received does not send it on: every computer sends to all the others itself, so nothing loops.
/// </summary>
public sealed class ProfileSync
{
    private readonly MainViewModel main;
    private readonly ICoreLog log;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public ProfileSync(MainViewModel main, ICoreLog log)
    {
        this.main = main;
        this.log = log;
        delay.Tick += (_, _) => { delay.Stop(); PushNow(); };
        main.ProfilesEdited += Push;
        if (main.Peers is { } peers) peers.ComputerConnected += Push;
    }

    private bool Enabled => main.Settings.SyncProfiles;

    /// <summary>Sends this computer's profiles to every connected computer (a moment after the last change, so a burst goes once).</summary>
    public void Push()
    {
        if (!Enabled) return;
        delay.Stop();
        delay.Start();
    }

    private void PushNow()
    {
        if (!Enabled) return;
        try
        {
            var message = SceneSync.Snapshot(SceneStore.ReadText(), AppPaths.IconsDirectory, main.EdidOfLocalKey);
            message["type"] = "profiles";
            foreach (var computer in main.Peers?.ConnectedClients ?? []) _ = computer.SendAsync(message);
        }
        catch (Exception error) when (error is FormatException or JsonException or IOException or UnauthorizedAccessException)
        {
            log.Write(LogLevel.Warning, $"Profiles could not be sent: {error.Message}");
        }
    }

    /// <summary>Profiles arrived from a computer of the group.</summary>
    public void OnProfiles(JsonElement message)
    {
        if (!Enabled) return;
        JsonObject? theirs;
        try { theirs = JsonNode.Parse(message.GetRawText()) as JsonObject; }
        catch (JsonException) { return; }
        if (theirs is null) return;
        dispatcher.BeginInvoke(() =>
        {
            try
            {
                var (json, changed) = SceneSync.Merge(SceneStore.ReadText(), theirs, AppPaths.IconsDirectory, main.LocalKeyOfEdid);
                if (!changed) return;
                SceneStore.Write(json);
                main.ProfilesChanged(null, fromAnotherComputer: true);
                main.Report("Profiles updated from another computer.", true);
            }
            catch (Exception error) when (error is FormatException or JsonException or IOException or UnauthorizedAccessException)
            {
                log.Write(LogLevel.Warning, $"Profiles from another computer were not used: {error.Message}");
            }
        });
    }
}
