using System.Collections.ObjectModel;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.App.Services;

/// <summary>A paired phone as Settings lists it.</summary>
public sealed record PhoneRow(string Id, string Name, string Detail, ICommand RemoveCommand);

/// <summary>
/// The phone side of the app (Settings > Phone): turns the phone server on or off, shows the pairing code and the paired
/// phones, and gives the phone the brightness panel — every display's brightness, link, profiles, picture modes,
/// inputs and the main display — through the same model the sun panel uses. Changes are sent to the phone as they
/// happen (checked a few times a second while a phone is connected).
/// </summary>
public sealed class PhoneBridge : ObservableObject, IRemoteHost, IDisposable
{
    private readonly MainViewModel main;
    private readonly SunPanelViewModel panel;
    private readonly ICoreLog log;
    private readonly Dispatcher dispatcher;
    private readonly PhoneStore store;
    private readonly RemoteServer server;
    private readonly DispatcherTimer push;
    private readonly DispatcherTimer codeClock;
    private string lastState = "";
    private string? code;
    private string? problem;

    public PhoneBridge(MainViewModel main, SunPanelViewModel panel, ICoreLog log)
    {
        this.main = main;
        this.panel = panel;
        this.log = log;
        dispatcher = Dispatcher.CurrentDispatcher;
        store = new PhoneStore();
        server = new RemoteServer(store, this, log);
        push = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _ = PushIfChangedAsync(), dispatcher);
        codeClock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickCode(), dispatcher);
        PairCommand = new RelayCommand(_ => NewCode(), _ => Enabled);
        CancelCodeCommand = new RelayCommand(_ => { store.CancelCode(); Code = null; });
        store.Changed += () => dispatcher.BeginInvoke(FillPhones);
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnAddressChanged;
        server.ClientsChanged += () => dispatcher.BeginInvoke(() =>
        {
            RaiseWhere();
            if (server.ConnectedCount > 0)
            {
                push.Start();
                _ = panel.RefreshAsync();   // the panel's rows pick up each display's brightness (read if needed)
            }
            else push.Stop();
            lastState = "";
        });
        FillPhones();
        if (main.Settings.PhoneAccess) TryStart();
    }

    // ---- Settings > Phone ----

    public bool Enabled
    {
        get => server.IsRunning;
        set
        {
            if (value == server.IsRunning) return;
            if (value) TryStart();
            else
            {
                server.Stop();
                store.CancelCode();
                Code = null;
                problem = null;
            }
            main.UpdateSettings(main.Settings with { PhoneAccess = server.IsRunning }, shortcutsChanged: false);
            Raise();
            RaiseWhere();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void TryStart()
    {
        try
        {
            server.Start();
            problem = null;
        }
        catch (SocketException error)
        {
            problem = $"Port {RemoteServer.DefaultPort} is in use by another program.";
            log.Write(LogLevel.Warning, "Phone access could not start.", error);
            main.Report($"Phone: {problem}", false);
        }
        Raise(nameof(Enabled));
        RaiseWhere();
    }

    // The computer's address can change while the app runs (Wi-Fi reconnects, the router hands out another one).
    private void OnAddressChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(RaiseWhere);
    private void RaiseWhere() { Raise(nameof(Status)); Raise(nameof(PageAddress)); }

    /// <summary>"On · 192.168.1.20 · 1 phone connected", or why it is off.</summary>
    public string Status
    {
        get
        {
            if (problem is not null) return problem;
            if (!server.IsRunning) return "Off. Turn it on to control the displays from a phone on this Wi-Fi.";
            string address = string.Join(", ", LocalNetwork.OwnAddresses().Select(a => a.ToString()));
            string where = address.Length == 0 ? "no home network found" : $"this computer is {address} (port {server.Port})";
            string who = server.ConnectedCount switch
            {
                0 => "no phone connected",
                1 => $"{server.ConnectedPhones[0]} connected",
                int n => $"{n} phones connected"
            };
            return $"On · {where} · {who}.";
        }
    }

    /// <summary>The address to open in a phone's browser (the control page), or null when off.</summary>
    public string? PageAddress => server.IsRunning && LocalNetwork.OwnAddresses().FirstOrDefault() is { } ip ? $"http://{ip}:{server.Port}/" : null;

    public string? Code { get => code; private set { if (Set(ref code, value)) { Raise(nameof(HasCode)); Raise(nameof(CodeHint)); } } }
    public bool HasCode => code is not null;
    public string CodeHint => code is null ? "" : $"Type it in the phone within {Math.Max(0, (int)(store.CodeExpires - DateTimeOffset.Now).TotalSeconds)} s.";

    public ICommand PairCommand { get; }
    public ICommand CancelCodeCommand { get; }
    public ObservableCollection<PhoneRow> Phones { get; } = [];
    public bool HasPhones => Phones.Count > 0;

    private void NewCode()
    {
        Code = store.NewCode(DateTimeOffset.Now);
        codeClock.Start();
    }

    private void TickCode()
    {
        if (store.CurrentCode(DateTimeOffset.Now) is null)
        {
            Code = null;
            codeClock.Stop();
            return;
        }
        Raise(nameof(CodeHint));
    }

    private void FillPhones()
    {
        if (code is not null && store.CurrentCode(DateTimeOffset.Now) is null) Code = null;   // used by a phone
        Phones.Clear();
        foreach (var phone in store.Phones)
            Phones.Add(new PhoneRow(phone.Id, phone.Name, $"Paired {phone.PairedAt.LocalDateTime:d}",
                new RelayCommand(_ =>
                {
                    store.Remove(phone.Id);
                    main.Report($"\"{phone.Name}\" can no longer control the displays.", true);
                })));
        Raise(nameof(HasPhones));
        RaiseWhere();
    }

    // ---- What the phone sees ----

    public string ComputerName => Environment.MachineName;

    public string Page => PhonePage.Html;

    public Task<JsonObject> StateAsync() => dispatcher.InvokeAsync(BuildState).Task;

    private async Task PushIfChangedAsync()
    {
        var state = BuildState();
        string json = state.ToJsonString();
        if (json == lastState) return;
        lastState = json;
        await server.BroadcastAsync(state);
    }

    private JsonObject BuildState()
    {
        var displays = new JsonArray();
        foreach (var row in panel.Rows)
        {
            if (row.IsOn && !row.IsReady) row.Attach();   // read since the panel was last open
            var inputs = new JsonArray();
            if (row.HasInputs)
                foreach (var input in row.Inputs)
                    inputs.Add(new JsonObject { ["code"] = input.Code, ["label"] = input.Label, ["current"] = input.IsCurrent, ["inCycle"] = input.InCycle });
            displays.Add(new JsonObject
            {
                ["id"] = row.Monitor.Key,
                ["name"] = row.Name,
                ["kind"] = row.Monitor.Device.Identity.IsBuiltIn ? "laptop" : "monitor",
                ["on"] = row.IsOn,
                ["ready"] = row.IsReady,
                ["brightness"] = row.IsReady ? row.Percent : (int?)null,
                ["selected"] = row.IsSelected,
                ["main"] = row.IsMain,
                ["preferred"] = row.IsPreferred,
                ["canBeMain"] = row.CanBeMain,
                ["inputs"] = inputs
            });
        }
        var profiles = new JsonArray();
        foreach (var scene in panel.Scenes.Where(s => s.IsShown))
            profiles.Add(new JsonObject
            {
                ["name"] = scene.Name,
                ["color"] = scene.Color,
                ["icon"] = Icons.ProfileIconId(scene.Name, scene.Scene?.Icon),
                ["active"] = scene.IsActive,
                ["custom"] = scene.IsCustom
            });
        JsonObject? modes = null;
        if (panel.ShowsModes && panel.PictureMode is { } mode)
        {
            var options = new JsonArray();
            foreach (var option in mode.Options) options.Add(new JsonObject { ["value"] = option.Value, ["label"] = option.Label });
            modes = new JsonObject { ["display"] = panel.ModesDisplayName, ["current"] = mode.Selected?.Value, ["options"] = options };
        }
        return new JsonObject
        {
            ["computer"] = ComputerName,
            ["linked"] = panel.Linked,
            ["canLink"] = panel.HasSeveral,
            ["displays"] = displays,
            ["profiles"] = profiles,
            ["modes"] = modes
        };
    }

    // ---- What the phone does ----

    public Task<string?> CommandAsync(string type, JsonElement message) => dispatcher.InvokeAsync(() => Run(type, message)).Task;

    private string? Run(string type, JsonElement message)
    {
        SunRow? Row() => message.TryGetProperty("display", out var d) && d.GetString() is { } id
            ? panel.Rows.FirstOrDefault(r => r.Monitor.Key == id) : null;
        int? Int(string name) => message.TryGetProperty(name, out var v) && v.TryGetInt32(out int n) ? n : null;
        bool? Bool(string name) => message.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

        switch (type)
        {
            case "brightness":
                if (Row() is not { IsReady: true } row) return "That display's brightness cannot be set now.";
                if (Int("value") is not int value) return "Missing brightness value.";
                row.MoveTo(Math.Clamp(value, 0, 100));
                return null;
            case "select":
                if (Row() is not { } chosen) return "Unknown display.";
                panel.Select(chosen);
                return null;
            case "link":
                if (Bool("on") is not bool on) return "Missing link value.";
                if (on && !panel.HasSeveral) return "Linking needs two displays in use.";
                panel.Linked = on;
                return null;
            case "profile":
                string? name = message.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (panel.Scenes.FirstOrDefault(s => s.Name == name) is not { } scene) return "Unknown profile.";
                scene.ApplyCommand.Execute(null);
                return null;
            case "mode":
                if (panel.PictureMode is not { } mode || !panel.ShowsModes) return "Picture modes are available while Custom is in use.";
                if (Int("value") is not int modeValue || mode.Options.FirstOrDefault(o => o.Value == modeValue) is not { } option)
                    return "Unknown picture mode.";
                mode.Selected = option;
                return null;
            case "input":
                if (Row() is not { HasInputs: true } monitor) return "That display has no inputs.";
                if (monitor.Inputs.FirstOrDefault(i => i.Code == Int("code")) is not { } input) return "Unknown input.";
                input.SelectCommand.Execute(null);
                return null;
            case "cycle":
                if (Row() is not { HasInputs: true } cycled) return "That display has no inputs.";
                if (cycled.Inputs.FirstOrDefault(i => i.Code == Int("code")) is not { } stop || Bool("on") is not bool keep) return "Unknown input.";
                stop.InCycle = keep;
                return null;
            case "main":
                if (Row() is not { CanBeMain: true } made) return "That display cannot be the main display now.";
                panel.MakeMain(made);
                return null;
            case "preferred":
                if (Row() is not { IsOn: true } preferred) return "Unknown display.";
                panel.TogglePreferred(preferred);
                return null;
            default:
                return $"Unknown command \"{type}\".";
        }
    }

    public void Dispose()
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        push.Stop();
        codeClock.Stop();
        server.Dispose();
    }
}
