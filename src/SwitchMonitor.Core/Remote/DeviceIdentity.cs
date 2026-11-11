using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Remote;

/// <summary>
/// Who this computer is to the others: a random id made at the first run and kept (not the computer name or the hardware,
/// which change). Other computers pair with it and recognize it again by this id.
/// </summary>
public static class DeviceIdentity
{
    private static string? id;

    public static string Id => id ??= Load();

    private static string Load()
    {
        string path = Path.Combine(AppPaths.DataDirectory, "device.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["deviceId"]?.GetValue<string>() is { Length: >= 8 } saved) return saved;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException or InvalidOperationException) { }
        string made = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new JsonObject { ["deviceId"] = made }.ToJsonString());
        }
        catch (IOException) { }
        return made;
    }
}

/// <summary>What a computer that paired with this one by code gives so this one can be controlled back (see <see cref="RemoteServer.ComputerPaired"/>).</summary>
public sealed record CounterPairing(string Name, string Host, int Port, string Token, string DeviceId);

/// <summary>A SwitchMonitor found on the home network by <see cref="PeerDiscovery"/>.</summary>
public sealed record DiscoveredComputer(string Name, string Host, int Port, string DeviceId, int Protocol, string? Ecosystem = null);

/// <summary>Finds other computers running SwitchMonitor on the home network (UDP broadcast, the same call the phone makes).</summary>
public static class PeerDiscovery
{
    /// <summary>
    /// Asks the network and collects the answers for <paramref name="wait"/>. <paramref name="targets"/> replaces the broadcast
    /// addresses (tests aim at one address and port). This computer's own answer is left out.
    /// </summary>
    public static async Task<IReadOnlyList<DiscoveredComputer>> FindAsync(TimeSpan wait, IEnumerable<IPEndPoint>? targets = null, CancellationToken cancel = default, bool includeSelf = false)
    {
        var found = new Dictionary<string, DiscoveredComputer>();
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        byte[] question = Encoding.ASCII.GetBytes(RemoteServer.DiscoveryQuestion);
        foreach (var target in targets ?? BroadcastTargets())
        {
            try { await udp.SendAsync(question, target, cancel); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(wait);
        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(timeout.Token); }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { break; }
            if (!LocalNetwork.IsAllowed(received.RemoteEndPoint.Address)) continue;
            try
            {
                if (JsonNode.Parse(received.Buffer) is not JsonObject answer || (string?)answer["app"] != "SwitchMonitor") continue;
                string? device = (string?)answer["deviceId"];
                if (string.IsNullOrEmpty(device) || (!includeSelf && device == DeviceIdentity.Id)) continue;   // a phone-only (old) answer, or ourselves
                int port = answer["port"]?.GetValue<int>() ?? RemoteServer.DefaultPort;
                found[device] = new DiscoveredComputer((string?)answer["computer"] ?? received.RemoteEndPoint.Address.ToString(),
                    received.RemoteEndPoint.Address.ToString(), port, device, answer["protocol"]?.GetValue<int>() ?? 0, (string?)answer["ecosystem"]);
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        }
        return found.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The limited broadcast plus each home-network adapter's own broadcast address.</summary>
    public static IEnumerable<IPEndPoint> BroadcastTargets()
    {
        var seen = new HashSet<IPAddress> { IPAddress.Broadcast };
        yield return new IPEndPoint(IPAddress.Broadcast, RemoteServer.DiscoveryPort);
        foreach (var own in LocalNetwork.OwnAddresses())
        {
            byte[] bytes = own.GetAddressBytes();
            var mask = MaskOf(own);
            if (mask is null) continue;
            byte[] directed = bytes.Select((b, i) => (byte)(b | ~mask[i])).ToArray();
            var address = new IPAddress(directed);
            if (seen.Add(address)) yield return new IPEndPoint(address, RemoteServer.DiscoveryPort);
        }
    }

    private static byte[]? MaskOf(IPAddress address)
    {
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.Equals(address) && unicast.IPv4Mask is { } mask) return mask.GetAddressBytes();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return null;
    }
}
