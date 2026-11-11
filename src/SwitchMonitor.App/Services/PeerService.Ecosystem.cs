using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.App.Services;

/// <summary>A device of "My devices" as Settings lists it.</summary>
public sealed record MemberRow(string DeviceId, string Name, string Detail, string Glyph, bool CanRemove, ICommand RemoveCommand);

/// <summary>
/// "My devices": the computers and phones of one person recognise each other. A computer already in the group shows a code;
/// a new device types it and the person allows it (see <see cref="Ecosystem"/>). Members connect to each other by themselves,
/// found on the network by their device id, so there is nothing more to pair and a changed address does not matter.
/// </summary>
public sealed partial class PeerService
{
    private readonly Dictionary<string, PeerClient> memberClients = [];
    private IReadOnlyList<DiscoveredComputer> nearby = [];
    private DispatcherTimer? ecoClock;
    private int ecoTicks;

    private Ecosystem? Eco => main.Phone?.Eco;

    public ObservableCollection<MemberRow> Members { get; } = [];
    public ICommand? JoinGroupCommand { get; private set; }
    public ICommand? LeaveGroupCommand { get; private set; }
    public ICommand? CopyKeyCommand { get; private set; }

    /// <summary>Opens the window where files are dropped on a device of the group.</summary>
    public ICommand SendFilesCommand => new RelayCommand(_ =>
    {
        if (main.Transfers is not { } transfers) return;
        var window = new SendFilesWindow(transfers);
        if (System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible) is { } owner) window.Owner = owner;
        window.ShowDialog();
    });

    public bool InGroup => Eco?.InEcosystem == true;
    public bool NotInGroup => !InGroup;
    public bool HasMembers => Members.Count > 0;

    public string GroupStatus => Eco is not { InEcosystem: true } eco
        ? "Not set up yet. Allow a device to join with the code below, or join the group of another computer."
        : eco.Members.Count == 1 ? "Only this computer is in your group. Add a device with the code below, or join another group."
        : $"{eco.Members.Count} devices in your group.";

    /// <summary>The code a new device types, as "123 456".</summary>
    public string GroupCode => Eco is { } eco ? eco.Code(DateTimeOffset.Now) is { Length: 6 } code ? code[..3] + " " + code[3..] : "" : "";

    public string GroupCodeHint => Eco is { } eco ? $"Changes in {eco.SecondsLeft(DateTimeOffset.Now)} s. It is the same code an authenticator app makes with the setup key." : "";

    private void StartEcosystem()
    {
        if (Eco is not { } eco) return;
        JoinGroupCommand = new RelayCommand(_ => _ = JoinGroupAsync());
        LeaveGroupCommand = new RelayCommand(_ => LeaveGroup(), _ => InGroup);
        CopyKeyCommand = new RelayCommand(_ => CopyKey());
        eco.Changed += () => dispatcher.BeginInvoke(OnEcosystemChanged);
        ecoClock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => TickEcosystem(), dispatcher);
        OnEcosystemChanged();
    }

    private void StopEcosystem()
    {
        ecoClock?.Stop();
        foreach (var client in memberClients.Values) client.Dispose();
        memberClients.Clear();
    }

    private void TickEcosystem()
    {
        Raise(nameof(GroupCode));
        Raise(nameof(GroupCodeHint));
        // Every half minute, members that do not answer are looked for again (their address may have changed).
        if (++ecoTicks % 30 == 0 && InGroup && memberClients.Values.Any(c => !c.Connected)) _ = SearchAsync();
    }

    private void OnEcosystemChanged()
    {
        Raise(nameof(InGroup));
        Raise(nameof(NotInGroup));
        Raise(nameof(GroupStatus));
        CommandManager.InvalidateRequerySuggested();
        ReconcileMembers();
        if (InGroup && Eco!.Members.Any(m => m.IsComputer && m.DeviceId != Eco.DeviceId && nearby.All(n => n.DeviceId != m.DeviceId))) _ = SearchAsync();
    }

    /// <summary>One connection per member computer found on the network (and none for devices that left or were removed).</summary>
    private void ReconcileMembers()
    {
        if (Eco is not { } eco) return;
        var wanted = eco.InEcosystem
            ? eco.Members.Where(m => m.IsComputer && m.DeviceId != eco.DeviceId && store.FindByDevice(m.DeviceId) is null).ToDictionary(m => m.DeviceId)
            : [];
        foreach (var (device, client) in memberClients.ToArray())
            if (!wanted.ContainsKey(device)) { client.Dispose(); memberClients.Remove(device); }
        foreach (var (device, cert) in wanted)
        {
            if (nearby.FirstOrDefault(n => n.DeviceId == device) is not { } found) continue;
            if (memberClients.TryGetValue(device, out var existing))
            {
                if (existing.Computer.Host == found.Host && existing.Computer.Port == found.Port) continue;
                existing.Dispose();
                memberClients.Remove(device);
            }
            var computer = new PairedComputer("eco-" + device, cert.Name, found.Host, found.Port, DateTimeOffset.Now, device);
            var client = new PeerClient(computer, eco, log);
            client.Changed += () => dispatcher.BeginInvoke(Refresh);
            client.CommandFailed += text => dispatcher.BeginInvoke(() => main.Report($"{cert.Name}: {text}", false));
            memberClients[device] = client;
            client.Start();
        }
        live = clients.Values.Concat(memberClients.Values).ToArray();
        Refresh();
    }

    private void FillMembers()
    {
        Members.Clear();
        if (Eco is { InEcosystem: true } eco)
            foreach (var cert in eco.Members)
            {
                bool self = cert.DeviceId == eco.DeviceId;
                string detail = self ? Loc.T("This computer")
                    : !cert.IsComputer ? Loc.T("Phone")
                    : memberClients.TryGetValue(cert.DeviceId, out var client) && client.Connected ? Loc.T("Connected")
                    : store.FindByDevice(cert.DeviceId) is not null ? Loc.T("Paired by code")
                    : Loc.T("Not reachable");
                var removing = cert;
                Members.Add(new MemberRow(cert.DeviceId, cert.Name, detail, cert.IsComputer ? "" : "", !self,
                    new RelayCommand(_ => RemoveMember(removing))));
            }
        Raise(nameof(HasMembers));
        Raise(nameof(GroupStatus));
    }

    private void RemoveMember(MemberCert cert)
    {
        if (Eco is not { } eco || !eco.Revoke(cert.DeviceId, DateTimeOffset.UtcNow)) return;
        main.Report($"{cert.Name} is no longer one of your devices.", true);
    }

    private void LeaveGroup()
    {
        Eco?.Leave();
        main.Report("This computer left the group. The other devices keep it.", true);
    }

    private void CopyKey()
    {
        if (Eco is not { } eco) return;
        try { System.Windows.Clipboard.SetText(eco.SetupKey); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        main.Report("Setup key copied. In your authenticator app choose \"Enter a setup key\" and paste it.", true);
    }

    private async Task JoinGroupAsync()
    {
        if (Eco is not { } eco) return;
        if (eco.InEcosystem && eco.Members.Count > 1)
        {
            var sure = System.Windows.MessageBox.Show(Loc.T("This computer is in a group with other devices. Joining another group leaves this one. Continue?"),
                Loc.T("Join a group"), System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (sure != System.Windows.MessageBoxResult.Yes) return;
        }
        if (nearby.Count == 0) await SearchAsync();
        var window = new JoinGroupWindow(nearby.Where(n => n.DeviceId != eco.DeviceId).ToList(), async (target, code) => await PeerClient.EnrollAsync(target.Host, target.Port, eco, code));
        if (System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible) is { } owner) window.Owner = owner;
        window.ShowDialog();
        if (window.Joined) main.Report("This computer joined your group of devices.", true);
    }
}
