using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Remote;

/// <summary>A phone allowed to control the displays: its name and the hash of its secret token (the token stays on the phone).</summary>
public sealed record PairedPhone(string Id, string Name, string TokenHash, DateTimeOffset PairedAt);

/// <summary>
/// Phones paired with this computer (phones.json in the data folder). A phone pairs once with the 6-digit code shown in
/// Settings and gets a random token; only the token's SHA-256 is kept here, so the file alone cannot be used to connect.
/// </summary>
public sealed class PhoneStore
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "phones.json");

    /// <summary>How long a pairing code works, and how many wrong tries cancel it.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);
    public const int MaxCodeTries = 5;

    private readonly string path;
    private readonly object gate = new();
    private readonly List<PairedPhone> phones = [];
    private string? code;
    private DateTimeOffset codeUntil;
    private int codeTries;

    public PhoneStore(string? path = null)
    {
        this.path = path ?? DefaultPath;
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<PairedPhone> Phones { get { lock (gate) return phones.ToArray(); } }

    // ---- Pairing code ----

    /// <summary>A new 6-digit code, valid for <see cref="CodeLifetime"/> (any earlier code stops working).</summary>
    public string NewCode(DateTimeOffset now)
    {
        lock (gate)
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            codeUntil = now + CodeLifetime;
            codeTries = 0;
            return code;
        }
    }

    /// <summary>The code being shown, or null when there is none (expired, used or cancelled).</summary>
    public string? CurrentCode(DateTimeOffset now) { lock (gate) return code is not null && now < codeUntil ? code : null; }

    public DateTimeOffset CodeExpires { get { lock (gate) return codeUntil; } }

    public void CancelCode() { lock (gate) code = null; }

    /// <summary>
    /// Pairs a phone with the code. Returns its new token (to send to the phone once), or null when the code is wrong,
    /// expired or used up; after <see cref="MaxCodeTries"/> wrong tries the code stops working.
    /// </summary>
    public string? Pair(string offeredCode, string phoneName, DateTimeOffset now)
    {
        string token;
        lock (gate)
        {
            if (code is null || now >= codeUntil) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offeredCode.Trim()), Encoding.ASCII.GetBytes(code)))
            {
                if (++codeTries >= MaxCodeTries) code = null;
                return null;
            }
            code = null;   // one phone per code
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string name = string.IsNullOrWhiteSpace(phoneName) ? "Phone" : phoneName.Trim();
            if (name.Length > 60) name = name[..60];
            phones.Add(new PairedPhone(Guid.NewGuid().ToString("N")[..12], name, Hash(token), now));
            Save();
        }
        Changed?.Invoke();
        return token;
    }

    /// <summary>The paired phone that owns <paramref name="token"/>, or null.</summary>
    public PairedPhone? Find(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string hash = Hash(token);
        lock (gate)
            return phones.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.TokenHash), Encoding.ASCII.GetBytes(hash)));
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (gate)
        {
            removed = phones.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["phones"] is not JsonArray list) return;
            foreach (var item in list.OfType<JsonObject>())
                if ((string?)item["id"] is { } id && (string?)item["tokenHash"] is { Length: 64 } hash)
                    phones.Add(new PairedPhone(id, (string?)item["name"] ?? "Phone", hash,
                        DateTimeOffset.TryParse((string?)item["pairedAt"], out var at) ? at : DateTimeOffset.MinValue));
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException) { }
    }

    private void Save()
    {
        var list = new JsonArray(phones.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["name"] = p.Name, ["tokenHash"] = p.TokenHash, ["pairedAt"] = p.PairedAt.ToString("O")
        }).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["version"] = 1, ["phones"] = list }.ToJsonString(new() { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Who may connect: only devices on the home network (private addresses) and this computer.</summary>
public static class LocalNetwork
{
    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (address.GetAddressBytes()[0] & 0xFE) == 0xFC;   // fc00::/7
        byte[] b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>This computer's addresses on the home network (what the phone types when it cannot find the computer).</summary>
    public static IReadOnlyList<IPAddress> OwnAddresses()
    {
        // The real home network first: an adapter with a router (default gateway), Wi-Fi or Ethernet, not a virtual one
        // (Hyper-V, WSL, VirtualBox, VMware, the Android emulator...), whose addresses a phone can never reach.
        var found = new List<(IPAddress Address, int Rank)>();
        try
        {
            foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                var properties = adapter.GetIPProperties();
                bool router = properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                string label = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                bool virtualAdapter = new[] { "virtual", "vethernet", "hyper-v", "wsl", "vmware", "virtualbox", "vbox", "loopback", "docker", "emulator" }
                    .Any(label.Contains);
                int rank = (router ? 0 : 2) + (virtualAdapter ? 4 : 0)
                    + (adapter.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? 0 : 1);
                foreach (var unicast in properties.UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && IsAllowed(unicast.Address)
                        && !IPAddress.IsLoopback(unicast.Address) && unicast.Address.GetAddressBytes()[0] != 169)
                        found.Add((unicast.Address, rank));
            }
        }
        catch (System.Net.NetworkInformation.NetworkInformationException) { }
        return found.OrderBy(f => f.Rank).Select(f => f.Address).ToList();
    }
}
