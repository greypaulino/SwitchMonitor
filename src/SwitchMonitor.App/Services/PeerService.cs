using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.App.ViewModels;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.App.Services;

/// <summary>A paired computer as Settings lists it.</summary>
public sealed record ComputerRow(string Id, string Name, string Detail, ICommand RemoveCommand);

/// <summary>
/// Controls other computers from this one (Settings > Computers) and lists their displays in the brightness panel. Each
/// paired computer has a <see cref="PeerClient"/>; the ones that are connected show their displays, the others are hidden.
/// The other computer needs Settings > Phone turned on: it is the same connection the phone uses.
/// </summary>
public sealed partial class PeerService : ObservableObject, IDisposable
{
    private readonly MainViewModel main;
    private readonly ICoreLog log;
    private readonly Dispatcher dispatcher;
    private readonly PeerStore store = new();
    private readonly Dictionary<string, PeerClient> clients = [];
    private string host = "";
    private string? status;
    private bool pairing;
    private bool searching;
    private DiscoveredRow? selected;

    public PeerService(MainViewModel main, ICoreLog log)
    {
        this.main = main;
        this.log = log;
        dispatcher = Dispatcher.CurrentDispatcher;
        ConnectCommand = new RelayCommand(_ => _ = ConnectByAddressAsync(), _ => !pairing && host.Trim().Length > 0);
        SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => !searching);
        store.Changed += () => dispatcher.BeginInvoke(Reconnect);
        Reconnect();
        // The other computers pair back through this one's server: a computer that paired with us by code (we learn how to reach it),
        // or one that asks to be forgotten.
        if (main.Phone is { } phone)
        {
            phone.ComputerPaired += paired =>
            {
                store.Add(paired.Name, paired.Host, paired.Port, paired.Token, DateTimeOffset.Now, paired.DeviceId);
                main.Report($"Computer {paired.Name} paired: its displays and this computer's can control each other.", true);
                _ = SearchAsync();
            };
            // Another computer clicked this one in its list: the code is made and shown here, nobody presses "pair" on this side.
            phone.PairRequested += (who, shown, until) => ShowRequest(phone, who, shown);
            phone.ComputerForgot += device =>
            {
                if (store.FindByDevice(device) is { } gone) store.Remove(gone.Id);
                _ = SearchAsync();
            };
        }
        // Computers nearby, looked up a moment after start so the list is ready when Settings opens.
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = SearchAsync());
        StartEcosystem();
    }

    /// <summary>A computer found on the network that is not paired yet.</summary>
    public sealed record DiscoveredRow(string Name, string Host, int Port, string DeviceId)
    {
        public string Detail => $"{Host}";
    }

    public ObservableCollection<DiscoveredRow> Discovered { get; } = [];
    public bool HasDiscovered => Discovered.Count > 0;
    public ICommand SearchCommand { get; }
    public bool Searching { get => searching; private set { if (Set(ref searching, value)) { Raise(nameof(SearchLabel)); CommandManager.InvalidateRequerySuggested(); } } }
    public string SearchLabel => searching ? "Searching…" : "Search";
    public string NothingFound => Searching ? "Looking on this network…" : "No other computer with SwitchMonitor answered. It must be open, on the same network, with its Settings > Devices access on.";
    public bool ShowsNothingFound => !HasDiscovered;

    /// <summary>Clicking a computer in the list starts the pairing: it shows a code, and a pop-up here asks for it.</summary>
    public DiscoveredRow? Selected
    {
        get => selected;
        set
        {
            if (!Set(ref selected, value) || value is null) return;
            Status = null;
            var row = value;
            dispatcher.BeginInvoke(() => { Selected = null; _ = PairWithAsync(row); });
        }
    }

    private PairShowWindow? request;

    /// <summary>The pop-up on the computer that was asked: who wants to pair, and the code to type over there.</summary>
    private void ShowRequest(PhoneBridge phone, string who, string shown)
    {
        request?.Close();
        request = new PairShowWindow(who, shown, phone.ActiveCodeExpiry, phone.CancelPairCode);
        request.Closed += (_, _) => request = null;
        request.Show();
    }

    /// <summary>Asks the home network which computers run SwitchMonitor and are not paired yet.</summary>
    public async Task SearchAsync()
    {
        if (searching) return;
        Searching = true;
        Raise(nameof(NothingFound));
        try
        {
            var found = await PeerDiscovery.FindAsync(TimeSpan.FromSeconds(2));
            nearby = found;
            string? keep = selected?.DeviceId;
            Discovered.Clear();
            foreach (var computer in found.Where(c => store.FindByDevice(c.DeviceId) is null))
                Discovered.Add(new DiscoveredRow(computer.Name, computer.Host, computer.Port, computer.DeviceId));
            Selected = Discovered.FirstOrDefault(d => d.DeviceId == keep);
        }
        catch (Exception error) when (error is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            log.Write(LogLevel.Warning, "Searching for computers failed.", error);
        }
        finally
        {
            Searching = false;
            ReconcileMembers();
            Raise(nameof(HasDiscovered));
            Raise(nameof(ShowsNothingFound));
            Raise(nameof(NothingFound));
        }
    }

    public ObservableCollection<ComputerRow> Computers { get; } = [];
    /// <summary>The displays of other computers that are in the list now, by the key of the display (see Refresh).</summary>
    private readonly Dictionary<string, MonitorViewModel> remote = [];
    public bool HasComputers => Computers.Count > 0;

    public ICommand ConnectCommand { get; }

    public string Host { get => host; set { if (Set(ref host, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string? Status { get => status; private set { if (Set(ref status, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => status is not null;

    /// <summary>The address typed by hand (for a computer the search did not find).</summary>
    private Task ConnectByAddressAsync()
    {
        string address = host.Trim();
        int port = RemoteServer.DefaultPort;
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.Count(c => c == ':') == 1 && int.TryParse(address[(colon + 1)..], out int typed))
        {
            port = typed;
            address = address[..colon];
        }
        return PairWithAsync(new DiscoveredRow(address, address, port, ""));
    }

    /// <summary>
    /// Pairs with a computer: asks it to show a code on its screen, then a pop-up asks for that code here. Once paired, each
    /// computer can control the other.
    /// </summary>
    private async Task PairWithAsync(DiscoveredRow target)
    {
        if (pairing) return;
        pairing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            // This computer's server must be on: the other one connects back to it with the token handed over here.
            if (main.Phone is not { } phone || !phone.EnsureOn())
            {
                Status = "Turn on \"Allow control from the network\" first.";
                return;
            }
            Status = $"Asking {target.Name} to show a code…";
            string? refused = await PeerClient.RequestPairAsync(target.Host, target.Port, Environment.MachineName);
            if (refused is not null) { Status = refused; return; }
            Status = $"Type the code {target.Name} is showing.";
            string? pairedName = null;
            // The window pairs by itself with the sixth digit and shows "Connected!" when it is done.
            async Task<string?> PairAsync(string code)
            {
                string counter = PhoneStore.NewToken();
                var result = await PeerClient.PairAsync(target.Host, target.Port, code, Environment.MachineName, counter, phone.ServerPort);
                if (result.Token is null || result.DeviceId is null) return result.Error ?? "Could not pair.";
                pairedName = result.ComputerName ?? target.Name;
                phone.AddComputer(pairedName, counter, result.DeviceId);
                store.Add(pairedName, target.Host, target.Port, result.Token, DateTimeOffset.Now, result.DeviceId);
                return null;
            }
            var window = new PairCodeWindow(target.Name, PairAsync);
            if (System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible) is { } owner) window.Owner = owner;
            window.ShowDialog();
            if (pairedName is null) { Status = null; return; }
            Status = $"Paired with {pairedName}.";
            Host = "";
            main.Report($"Computer {pairedName} paired: its displays and this computer's can control each other.", true);
            _ = SearchAsync();
        }
        finally
        {
            pairing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>One client per paired computer; the list in Settings follows the store.</summary>
    private volatile PeerClient[] live = [];

    /// <summary>A connected paired computer (the one with this device id, or any): read from other threads, so from a copy.</summary>
    public PeerClient? ConnectedClient(string? deviceId) =>
        live.FirstOrDefault(c => c.Connected && (deviceId is null || c.Computer.DeviceId == deviceId));

    private void Reconnect()
    {
        var current = store.Computers;
        foreach (var (id, client) in clients.ToArray())
            if (current.All(c => c.Id != id)) { client.Dispose(); clients.Remove(id); }
        foreach (var computer in current)
        {
            if (clients.ContainsKey(computer.Id) || store.TokenOf(computer.Id) is not { } token) continue;
            var client = new PeerClient(computer, token, log);
            client.Changed += () => dispatcher.BeginInvoke(Refresh);
            client.CommandFailed += text => dispatcher.BeginInvoke(() => main.Report($"{computer.Name}: {text}", false));
            clients[computer.Id] = client;
            client.Start();
        }
        live = clients.Values.Concat(memberClients.Values).ToArray();
        Refresh();
    }

    private void Refresh()
    {
        Computers.Clear();
        foreach (var computer in store.Computers)
        {
            clients.TryGetValue(computer.Id, out var client);
            string detail = client is null ? Loc.T("Not connected")
                : client.Unpaired ? Loc.T("Removed there: pair it again")
                : client.Connected ? $"{Loc.T("Connected")} · {computer.Host}"
                : $"{Loc.T("Not reachable")} · {computer.Host}";
            string id = computer.Id;
            var removing = computer;
            Computers.Add(new ComputerRow(id, computer.Name, detail, new RelayCommand(() => _ = RemoveAsync(removing))));
        }
        Raise(nameof(HasComputers));

        SyncDisplays();
        FillMembers();
    }

    /// <summary>
    /// The displays each connected computer publishes become ordinary displays of this one (a network transport): they sit in the
    /// list and the brightness panel with every function a local one has. A computer that is not connected, or no longer offers a
    /// display, takes it out of the list.
    /// </summary>
    private void SyncDisplays()
    {
        var wanted = new Dictionary<string, (PairedComputer Computer, PeerClient Client, RemoteDeviceInfo Info)>();
        foreach (var client in clients.Values.Concat(memberClients.Values))
        {
            if (client is not { Connected: true, State: { } state } || state["devices"] is not JsonArray list) continue;
            foreach (var item in list.OfType<JsonObject>())
                if (RemoteDeviceInfo.FromJson(item) is { } info)
                    wanted[RemoteKey(client.Computer, info)] = (client.Computer, client, info);
        }
        foreach (var (key, monitor) in remote.ToArray())
            if (!wanted.ContainsKey(key))
            {
                remote.Remove(key);
                main.RemoveRemote(monitor);
            }
        foreach (var (key, entry) in wanted)
        {
            if (remote.ContainsKey(key)) continue;
            var client = entry.Client;
            var device = RemoteMonitors.Create(entry.Info, entry.Computer.DeviceId ?? entry.Computer.Id, entry.Computer.Name,
                request => client.CallAsync(request, TimeSpan.FromSeconds(6)).GetAwaiter().GetResult(), log);
            remote[key] = main.AddRemote(device);
        }
    }

    /// <summary>Stable across restarts: the other computer's device id, not the local pairing id.</summary>
    private static string RemoteKey(PairedComputer computer, RemoteDeviceInfo info) => $"{computer.DeviceId ?? computer.Id}|{info.Key}";

    /// <summary>Removes a computer in both directions: it is told to forget this one (when it answers), then forgotten here.</summary>
    private async Task RemoveAsync(PairedComputer computer)
    {
        if (clients.TryGetValue(computer.Id, out var client) && client.Connected)
        {
            try { await client.ForgetAsync(); await Task.Delay(150); }
            catch (Exception error) when (error is InvalidOperationException or System.Net.WebSockets.WebSocketException) { }
        }
        if (computer.DeviceId is not null) main.Phone?.ForgetComputer(computer.DeviceId);
        store.Remove(computer.Id);
        main.Report($"{computer.Name} removed: the two computers no longer control each other.", true);
        _ = SearchAsync();
    }

    public void Dispose()
    {
        foreach (var monitor in remote.Values.ToArray()) main.RemoveRemote(monitor);
        remote.Clear();
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
        StopEcosystem();
    }
}
