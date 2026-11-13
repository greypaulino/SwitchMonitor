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
        Eco = new Ecosystem();
        Task.Run(CheckFirewall);
        server = new RemoteServer(store, this, log, ecosystem: Eco) { Enrolling = AskToAdmit, AllowUnencryptedPage = main.Settings.PhoneWebPage };
        push = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _ = PushIfChangedAsync(), dispatcher);
        codeClock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickCode(), dispatcher);
        PairCommand = new RelayCommand(_ => NewCode(), _ => Enabled);
        CancelCodeCommand = new RelayCommand(_ => { store.CancelCode(); Code = null; });
        store.Changed += () => dispatcher.BeginInvoke(FillPhones);
        server.ComputerPaired += paired => dispatcher.BeginInvoke(() => ComputerPaired?.Invoke(paired));
        server.PairRequested += (who, shown, until) => dispatcher.BeginInvoke(() => PairRequested?.Invoke(who, shown, until));
        server.ComputerForgot += device => dispatcher.BeginInvoke(() => ComputerForgot?.Invoke(device));
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnAddressChanged;
        server.ClientsChanged += () => dispatcher.BeginInvoke(() =>
        {
            RaiseWhere();
            DevicesChanged?.Invoke();
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

    // ---- My devices: the group of computers and phones of one person ----

    /// <summary>This device's place in the ecosystem (its key, its members, the code that lets a new one in).</summary>
    public Ecosystem Eco { get; }

    /// <summary>A device typed this computer's code: asks the person (a pop-up on top) and answers when they decided.</summary>
    private Task<bool> AskToAdmit(EnrollRequest request)
    {
        var done = new TaskCompletionSource<bool>();
        dispatcher.BeginInvoke(() =>
        {
            var window = new EnrollApproveWindow(request.Name, request.Kind, request.Address, request.Verification, request.Pairing);
            window.Closed += (_, _) => done.TrySetResult(window.Allowed);
            window.Show();
        });
        return done.Task;
    }

    // ---- Pairing with other computers (the Devices panel) ----

    /// <summary>The port this computer's server listens on (other computers need it to control this one back).</summary>
    public int ServerPort => server.Port;

    /// <summary>Another computer paired with this one by code and handed over a token to control it back.</summary>
    public event Action<CounterPairing>? ComputerPaired;

    /// <summary>Another computer asked to pair: its name, the code made for it, and when the code stops working.</summary>
    public event Action<string, string, DateTimeOffset>? PairRequested;

    /// <summary>When the code on screen stops working, or null once it was used, cancelled or has expired.</summary>
    public DateTimeOffset? ActiveCodeExpiry() => store.CurrentCode(DateTimeOffset.Now) is null ? null : store.CodeExpires;

    public void CancelPairCode() => store.CancelCode();

    /// <summary>A computer that controls this one asked to be forgotten (it removed this computer).</summary>
    public event Action<string>? ComputerForgot;

    /// <summary>Turns the server on when it is off (pairing a computer needs it, so the other one can answer back). True when running.</summary>
    public bool EnsureOn()
    {
        if (!server.IsRunning) Enabled = true;
        return server.IsRunning;
    }

    /// <summary>The other computer may control this one with <paramref name="token"/> (this computer made it and handed it over).</summary>
    public void AddComputer(string name, string token, string deviceId) =>
        store.AddPaired(name, token, "computer", deviceId, DateTimeOffset.Now);

    /// <summary>Stops a computer from controlling this one.</summary>
    public void ForgetComputer(string deviceId) => store.RemoveByDevice(deviceId);

    // ---- Settings > Phone ----

    /// <summary>A paired phone may be this computer's mouse and keyboard (Settings > Devices).</summary>
    public bool PhoneInput
    {
        get => main.Settings.PhoneInput;
        set
        {
            if (value == main.Settings.PhoneInput) return;
            main.UpdateSettings(main.Settings with { PhoneInput = value }, shortcutsChanged: false);
            Raise();
        }
    }

    /// <summary>A paired phone may open and close programs and turn this computer off (Settings > Devices).</summary>
    public bool PhoneRemote
    {
        get => main.Settings.PhoneRemote;
        set
        {
            if (value == main.Settings.PhoneRemote) return;
            main.UpdateSettings(main.Settings with { PhoneRemote = value }, shortcutsChanged: false);
            Raise();
        }
    }

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

    // ---- Windows Firewall: reachable on home networks only ----

    private string firewallStatus = "";

    /// <summary>What the firewall rule says now (read when asked: reading needs no permission).</summary>
    public string FirewallStatus { get => firewallStatus; private set { if (firewallStatus == value) return; firewallStatus = value; Raise(); } }

    private ICommand? firewallCommand;
    public ICommand FirewallCommand => firewallCommand ??= new RelayCommand(_ => _ = SetFirewallAsync());

    private bool firewallOn;

    /// <summary>The switch: on while the rule (home networks only) is set; turning it on or off asks Windows for permission.</summary>
    public bool FirewallOn
    {
        get => firewallOn;
        set
        {
            if (value == firewallOn) return;
            _ = value ? SetFirewallAsync() : RemoveFirewallAsync();
        }
    }

    public void CheckFirewall()
    {
        bool set = FirewallRule.IsSet();
        FirewallStatus = set
            ? "Done: other devices can reach this program on home networks only."
            : "Not set: on a public network (a café, a hotel) other devices could try to reach it.";
        if (firewallOn != set) { firewallOn = set; Raise(nameof(FirewallOn)); }
        else Raise(nameof(FirewallOn));   // a refused change puts the switch back
    }

    private async Task RemoveFirewallAsync()
    {
        FirewallStatus = "Waiting for Windows to allow the change…";
        bool ok = await FirewallRule.RemoveAsync();
        CheckFirewall();
        if (!ok && FirewallRule.IsSet()) FirewallStatus = "It was not changed (the permission was refused or Windows could not do it).";
    }

    private async Task SetFirewallAsync()
    {
        FirewallStatus = "Waiting for Windows to allow the change…";
        bool ok = await FirewallRule.SetAsync();
        CheckFirewall();
        if (!ok && !FirewallRule.IsSet()) FirewallStatus = "It was not changed (the permission was refused or Windows could not do it).";
    }
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
        foreach (var phone in store.Phones.Where(p => !p.IsComputer))   // computers are listed by the Computers service
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PowerStatus { public byte AcLine; public byte BatteryFlag; public byte BatteryPercent; public byte SystemFlag; public int LifeTime; public int FullLifeTime; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    private static readonly Lazy<bool> portable = new(() =>
    {
        try { return GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryFlag != 255; }   // 128 = no battery
        catch { return false; }
    });

    /// <summary>Whether this is a laptop (it has a battery): the phone draws a laptop or a PC next to the name.</summary>
    public static bool IsPortable => portable.Value;

    public string Page => PhonePage.Html;

    /// <summary>Mouse and keyboard from a paired computer that shares its input (Settings > Devices).</summary>
    public void InputEvent(JsonElement message, string? deviceId)
    {
        // A phone's input, or a phone's input another computer of the group passed on ("phone": true).
        bool fromPhone = deviceId is null || (message.TryGetProperty("phone", out var relayed) && relayed.ValueKind == JsonValueKind.True);
        if (!fromPhone) { main.Share?.OnInput(message, deviceId); return; }
        // The phone may aim at any computer of the group ("target": its device id): this one passes it on.
        if (deviceId is null && message.TryGetProperty("target", out var aim) && aim.GetString() is { Length: > 0 } target && target != Eco.DeviceId)
        {
            if (main.Peers?.ConnectedClient(target) is { } peer && JsonNode.Parse(message.GetRawText()) is JsonObject copy)
            {
                copy.Remove("target");
                copy["phone"] = true;
                _ = peer.SendAsync(copy);
            }
            return;
        }
        if (main.Settings.PhoneInput) main.Share?.OnPhoneInput(message);
    }

    public Task<JsonObject> StateAsync() => dispatcher.InvokeAsync(BuildState).Task;

    /// <summary>Another computer reads or writes one of this computer's displays: found here, run on a worker thread.</summary>
    public async Task<JsonObject> DeviceRequestAsync(JsonElement message)
    {
        string key = message.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
        var device = await dispatcher.InvokeAsync(() => main.Monitors.FirstOrDefault(m => !m.IsRemote && m.Device.Key == key)?.Device);
        return await Task.Run(() => DeviceHost.Handle(_ => device, message));
    }

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
        foreach (var row in panel.Rows)   // every display is the same to the phone: this computer's and the other computers'
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
                ["remote"] = row.IsRemote,
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
                ["custom"] = scene.IsCustom,
                ["modified"] = scene.IsModified
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
            ["portable"] = IsPortable,
            ["devices"] = DeviceHost.Inventory(main.Monitors.Where(m => !m.IsRemote && m.IsControllable && m.IsLoaded && !m.IsUnavailable && m.Device.IsActive).Select(m => m.Device), Core.DisplayNames.For,
                d => main.Monitors.FirstOrDefault(m => ReferenceEquals(m.Device, d)) is { } vm ? main.HomeInput(vm) : null),
            ["linked"] = panel.Linked,
            ["canLink"] = panel.HasSeveral,
            ["displays"] = displays,
            ["profiles"] = profiles,
            ["modes"] = modes,
            ["volume"] = VolumeState(),
            ["volumeFor"] = panel.Rows.FirstOrDefault(r => r.IsSelected)?.Name
        };
    }

    /// <summary>
    /// The computer whose sound the phone's volume slider moves: the one that owns the display selected on the phone (like the picture
    /// modes, the volume follows the selection). Null for a display of this computer.
    /// </summary>
    private PeerClient? VolumeOwner()
    {
        var selected = panel.Rows.FirstOrDefault(r => r.IsSelected);
        if (selected is not { IsRemote: true }) return null;
        string key = selected.Monitor.Key;   // "peer:{device id}|{display}"
        if (!key.StartsWith("peer:", StringComparison.Ordinal)) return null;
        int bar = key.IndexOf('|');
        return main.Peers?.ConnectedClient(bar > 5 ? key[5..bar] : key[5..]);
    }

    private JsonNode? VolumeState()
    {
        if (VolumeOwner() is { } owner) return owner.State?["volume"]?.DeepClone();
        return WindowsVolume.Read() is { } sound ? new JsonObject { ["percent"] = sound.Percent, ["muted"] = sound.Muted } : null;
    }

    // ---- What the phone does ----

    public Task<string?> CommandAsync(string type, JsonElement message) =>
        type is "launch" or "closeapp" or "power" ? RemoteAsync(type, message) : dispatcher.InvokeAsync(() => Run(type, message)).Task;

    private const string RemoteOff = "Turn on \"Phone as remote\" in SwitchMonitor > Settings > Devices on that computer first.";

    /// <summary>The programs a phone may open or close (the Start menu's), with whether each one is running.</summary>
    public async Task<JsonObject> AppsAsync(JsonElement request)
    {
        if (!main.Settings.PhoneRemote) return new JsonObject { ["allowed"] = false, ["apps"] = new JsonArray() };
        var apps = await Automation.InstalledApps.AllAsync();
        var running = System.Diagnostics.Process.GetProcesses().Select(p => p.ProcessName.ToLowerInvariant() + ".exe").ToHashSet();
        // The icons the phone asked for (its favorites): small PNGs, base64.
        var wanted = request.TryGetProperty("icons", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(i => i.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        var icons = new JsonObject();
        foreach (var app in apps.Where(a => wanted.Contains(a.Exe)))
            if (IconPng(app.Path) is { } png) icons[app.Exe] = png;
        return new JsonObject
        {
            ["allowed"] = true,
            ["apps"] = new JsonArray(apps.Select(a => (JsonNode)new JsonObject { ["id"] = a.Exe, ["name"] = a.Name, ["running"] = running.Contains(a.Exe.ToLowerInvariant()) }).ToArray()),
            ["icons"] = icons
        };
    }

    private static string? IconPng(string path)
    {
        try
        {
            if (AppIcons.IconOf(path) is not System.Windows.Media.Imaging.BitmapSource bitmap) return null;
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException) { return null; }
    }

    // ---- Shared clipboard ----

    /// <summary>The text copied on this computer goes on to the phones that are connected (computers are reached by their own connection).</summary>
    public Task SendToPhonesAsync(JsonObject message) => server.SendToPhonesAsync(message);

    public void ClipboardEvent(JsonElement message, string? deviceId) => main.Clipboard?.OnClip(message);

    /// <summary>A device signed in to this computer's server or left it (a computer that opened after this one shows up here first).</summary>
    public event Action? DevicesChanged;

    public void ProfilesEvent(JsonElement message, string deviceId) => main.ProfileSync?.OnProfiles(message);

    public void ShareEvent(JsonElement message, string deviceId) => main.Share?.OnShare(message, deviceId);

    public bool SyncProfiles
    {
        get => main.Settings.SyncProfiles;
        set
        {
            if (value == main.Settings.SyncProfiles) return;
            main.UpdateSettings(main.Settings with { SyncProfiles = value }, shortcutsChanged: false);
            Raise();
            if (value) main.ProfileSync?.Push();
        }
    }

    public void BlobEvent(JsonElement message, string? deviceId) => main.Transfers?.OnBlob(message);

    /// <summary>The phones connected now, and a way to send to one of them (files and images).</summary>
    public IReadOnlyList<Core.Remote.PairedPhone> PhonesOnline => server.PhonesOnline;
    public Task<bool> SendToPhoneAsync(string phoneId, JsonObject message) => server.SendToPhoneAsync(phoneId, message);

    /// <summary>The right-click "Send with SwitchMonitor…" of Windows Explorer.</summary>
    public bool ExplorerMenu
    {
        get => main.Settings.ExplorerMenu;
        set
        {
            if (value == main.Settings.ExplorerMenu) return;
            main.UpdateSettings(main.Settings with { ExplorerMenu = value }, shortcutsChanged: false);
            Services.ExplorerMenu.Apply(value, value ? main.Transfers?.Targets().Select(t => (t.Id, t.Name)) : null);
            Raise();
        }
    }

    /// <summary>The web page for phones without the app: not encrypted (a browser cannot do it), so off unless the person turns it on.</summary>
    public bool PhoneWebPage
    {
        get => main.Settings.PhoneWebPage;
        set
        {
            if (value == main.Settings.PhoneWebPage) return;
            main.UpdateSettings(main.Settings with { PhoneWebPage = value }, shortcutsChanged: false);
            server.AllowUnencryptedPage = value;
            Raise();
        }
    }

    public bool SharedClipboard
    {
        get => main.Settings.SharedClipboard;
        set
        {
            if (value == main.Settings.SharedClipboard) return;
            main.UpdateSettings(main.Settings with { SharedClipboard = value }, shortcutsChanged: false);
            Raise();
        }
    }

    /// <summary>Open or close a program, or turn the computer off (needs "Phone as remote"). Off waits 10 s so someone here can cancel it.</summary>
    private async Task<string?> RemoteAsync(string type, JsonElement message)
    {
        if (!main.Settings.PhoneRemote) return RemoteOff;
        string Text(string name) => message.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        try
        {
            switch (type)
            {
                case "launch":
                {
                    var apps = await Automation.InstalledApps.AllAsync();
                    if (apps.FirstOrDefault(a => a.Exe.Equals(Text("id"), StringComparison.OrdinalIgnoreCase)) is not { } app) return "That program is not in the Start menu of this computer.";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app.Path) { UseShellExecute = true });
                    return null;
                }
                case "closeapp":
                {
                    // Only a program of this computer's Start menu (the same list the phone shows) can be closed.
                    var known = await Automation.InstalledApps.AllAsync();
                    if (known.FirstOrDefault(a => a.Exe.Equals(Text("id"), StringComparison.OrdinalIgnoreCase)) is not { } listed) return "That program is not in the Start menu of this computer.";
                    string name = System.IO.Path.GetFileNameWithoutExtension(listed.Exe);
                    if (name.Length == 0) return "Unknown program.";
                    int closed = 0;
                    foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
                        using (process) { try { if (process.CloseMainWindow()) closed++; } catch (InvalidOperationException) { } }
                    return closed > 0 ? null : "That program is not open.";
                }
                default:
                    if (Text("action") == "cancel") { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false }); return null; }
                    if (Text("action") != "shutdown") return "Unknown action.";
                    log.Write(LogLevel.Warning, "A phone asked to turn this computer off: it goes off in 10 s.");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 10 /c \"SwitchMonitor: turned off from a phone. Run shutdown /a to cancel.\"") { CreateNoWindow = true, UseShellExecute = false });
                    return null;
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            return "Windows could not do that: " + error.Message;
        }
    }

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
            case "media":
            {
                // The media keys of a phone's Remote tab (like a keyboard's): needs the phone to be allowed as mouse and keyboard.
                if (!main.Settings.PhoneInput) return "Turn on \"Phone as mouse and keyboard\" in SwitchMonitor > Settings > Devices on that computer first.";
                string key = message.TryGetProperty("key", out var mk) ? mk.GetString() ?? "" : "";
                int vk = key switch { "playpause" => 0xB3, "next" => 0xB0, "prev" => 0xB1, "stop" => 0xB2, "volup" => 0xAF, "voldown" => 0xAE, "mute" => 0xAD, "seekfwd" or "seekback" => -1, _ => 0 };
                if (vk == 0) return "Unknown media key.";
                // The program that is playing (Brave on YouTube, Spotify…) first; with none, the media keys and the Windows volume.
                _ = Task.Run(async () =>
                {
                    bool handled = key is "volup" or "voldown" or "mute" ? await ActiveMedia.VolumeAsync(key) : await ActiveMedia.CommandAsync(key);
                    if (!handled && vk > 0) main.Share?.Tap(vk);
                });
                return null;
            }
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
            case "volume":
                if (Int("value") is not int level) return "Missing volume value.";
                if (VolumeOwner() is { } volumeOwner) { _ = volumeOwner.SendAsync(new JsonObject { ["type"] = "volume", ["value"] = level }); return null; }
                return WindowsVolume.SetPercent(level) ? null : "No sound output to set.";
            case "mute":
                if (Bool("on") is not bool silent) return "Missing mute value.";
                if (VolumeOwner() is { } muteOwner) { _ = muteOwner.SendAsync(new JsonObject { ["type"] = "mute", ["on"] = silent }); return null; }
                return WindowsVolume.SetMuted(silent) ? null : "No sound output to set.";
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
