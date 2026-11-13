using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Remote;

/// <summary>A phone allowed to control the displays: its name and the hash of its secret token (the token stays on the phone).</summary>
/// <param name="Kind">"phone", or "computer" for another SwitchMonitor that controls this one.</param>
/// <param name="DeviceId">The other computer's <see cref="DeviceIdentity.Id"/> (computers only).</param>
public sealed record PairedPhone(string Id, string Name, string TokenHash, DateTimeOffset PairedAt, string Kind = "phone", string? DeviceId = null)
{
    public bool IsComputer => Kind == "computer";
}

/// <summary>
/// Phones paired with this computer (phones.json in the data folder). A phone pairs once with the 6-digit code shown in
/// Settings and gets a random token. The token never travels again: each connection the phone proves it holds it
/// (HMAC of fresh random numbers, see <see cref="Verify"/>), and the computer proves the same back. To check those
/// proofs the computer keeps the token encrypted for the Windows user (DPAPI), so the file alone, copied to another
/// account or computer, cannot be used to connect. Phones paired by version 1 (hash only) must pair again.
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
    private readonly Dictionary<string, byte[]> secrets = [];   // phone id -> token bytes (memory only)
    private string? code;
    private DateTimeOffset codeUntil;
    private int codeTries;
    private readonly Queue<DateTimeOffset> failures = new();
    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;

    /// <summary>Wrong codes in all, across every code and connection: this many in <see cref="FailureWindow"/> block pairing for that long.</summary>
    public const int MaxFailures = 15;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(15);

    /// <summary>True while too many wrong codes were tried (a new code cannot be asked for, and none is accepted).</summary>
    public bool IsBlocked(DateTimeOffset now) { lock (gate) return now < blockedUntil; }

    private void RecordFailure(DateTimeOffset now)
    {
        failures.Enqueue(now);
        while (failures.Count > 0 && now - failures.Peek() > FailureWindow) failures.Dequeue();
        if (failures.Count >= MaxFailures) { blockedUntil = now + FailureWindow; failures.Clear(); code = null; }
    }

    /// <summary>A text from another device made safe to show and to log: no control characters, trimmed, at most <paramref name="max"/> long.</summary>
    public static string CleanText(string? text, int max = 60)
    {
        string clean = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > max ? clean[..max] : clean;
    }

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
    /// Whether this is the code being shown (nothing is used up: <see cref="Pair"/> does that). A wrong one counts as a failure,
    /// like in <see cref="Pair"/>; it is how a device is asked about before the person is.
    /// </summary>
    public bool CheckCode(string offeredCode, DateTimeOffset now)
    {
        lock (gate)
        {
            if (now < blockedUntil || code is null || now >= codeUntil) return false;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offeredCode.Trim()), Encoding.ASCII.GetBytes(code))) return true;
            if (++codeTries >= MaxCodeTries) code = null;
            RecordFailure(now);
            return false;
        }
    }

    /// <summary>
    /// Pairs a phone with the code. Returns its new token (to send to the phone once), or null when the code is wrong,
    /// expired or used up; after <see cref="MaxCodeTries"/> wrong tries the code stops working.
    /// </summary>
    public string? Pair(string offeredCode, string phoneName, DateTimeOffset now, string kind = "phone", string? deviceId = null)
    {
        string token;
        lock (gate)
        {
            if (now < blockedUntil || code is null || now >= codeUntil) return null;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offeredCode.Trim()), Encoding.ASCII.GetBytes(code)))
            {
                if (++codeTries >= MaxCodeTries) code = null;
                RecordFailure(now);
                return null;
            }
            code = null;   // one phone per code
            token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            string name = CleanText(phoneName);
            if (name.Length == 0) name = "Phone";
            var phone = new PairedPhone(Guid.NewGuid().ToString("N")[..12], name, Hash(token), now, kind, deviceId);
            phones.Add(phone);
            secrets[phone.Id] = Encoding.UTF8.GetBytes(token);
            Save();
        }
        Changed?.Invoke();
        return token;
    }

    /// <summary>
    /// Pairs with a token this computer made itself (the other computer was given it): used when this computer starts the
    /// pairing, so the other one can control it back with no second code.
    /// </summary>
    public PairedPhone AddPaired(string name, string token, string kind, string? deviceId, DateTimeOffset now)
    {
        PairedPhone phone;
        lock (gate)
        {
            if (deviceId is not null) RemoveWhere(p => p.DeviceId == deviceId);
            string clean = string.IsNullOrWhiteSpace(name) ? "Computer" : name.Trim();
            phone = new PairedPhone(Guid.NewGuid().ToString("N")[..12], clean.Length > 60 ? clean[..60] : clean, Hash(token), now, kind, deviceId);
            phones.Add(phone);
            secrets[phone.Id] = Encoding.UTF8.GetBytes(token);
            Save();
        }
        Changed?.Invoke();
        return phone;
    }

    /// <summary>A fresh token to hand to another computer (see <see cref="AddPaired"/>).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Removes the computer paired under this device id (it forgot us, or we forget it). True when there was one.</summary>
    public bool RemoveByDevice(string deviceId)
    {
        bool removed;
        lock (gate) removed = RemoveWhere(p => p.DeviceId == deviceId);
        if (removed) Changed?.Invoke();
        return removed;
    }

    private bool RemoveWhere(Func<PairedPhone, bool> match)
    {
        bool any = false;
        foreach (var phone in phones.Where(match).ToArray())
        {
            secrets.Remove(phone.Id);
            phones.Remove(phone);
            any = true;
        }
        if (any) Save();
        return any;
    }

    /// <summary>The paired phone that owns <paramref name="token"/>, or null.</summary>
    public PairedPhone? Find(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string hash = Hash(token);
        lock (gate)
            return phones.FirstOrDefault(p => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(p.TokenHash), Encoding.ASCII.GetBytes(hash)));
    }

    /// <summary>
    /// The phone whose token made <paramref name="proof"/> = HMAC-SHA256(token, "phone:" + computer nonce + ":" + phone
    /// nonce), or null. <paramref name="answer"/> is the computer's proof for that phone: HMAC-SHA256(token, "computer:" +
    /// phone nonce + ":" + computer nonce), so the phone knows it talks to the computer it paired with.
    /// </summary>
    public PairedPhone? Verify(string computerNonce, string phoneNonce, string proof, out string? answer, string binding = "")
    {
        answer = null;
        string tail = binding.Length > 0 ? ":" + binding : "";
        byte[] offered;
        try { offered = Convert.FromBase64String(proof); }
        catch (FormatException) { return null; }
        if (offered.Length != 32 || phoneNonce.Length is < 16 or > 128) return null;
        lock (gate)
            foreach (var phone in phones)
            {
                if (!secrets.TryGetValue(phone.Id, out var key)) continue;
                if (!CryptographicOperations.FixedTimeEquals(Proof(key, $"phone:{computerNonce}:{phoneNonce}{tail}"), offered)) continue;
                answer = Convert.ToBase64String(Proof(key, $"computer:{phoneNonce}:{computerNonce}{tail}"));
                return phone;
            }
        return null;
    }

    /// <summary>HMAC-SHA256 of <paramref name="text"/> (UTF-8) with the token as key.</summary>
    public static byte[] Proof(byte[] token, string text) => HMACSHA256.HashData(token, Encoding.UTF8.GetBytes(text));

    /// <summary>A fresh random value for a challenge (base64 of 32 bytes).</summary>
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Whether this phone is still paired (removed in Settings: no).</summary>
    public bool IsPaired(string id) { lock (gate) return phones.Any(p => p.Id == id); }

    public bool Remove(string id)
    {
        bool removed;
        lock (gate)
        {
            secrets.Remove(id);
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
            {
                // Version 1 kept only the hash, which cannot answer the new challenge: those phones pair again.
                if ((string?)item["id"] is not { } id || (string?)item["tokenHash"] is not { Length: 64 } hash ||
                    (string?)item["token"] is not { Length: > 0 } sealedToken) continue;
                byte[]? token = Dpapi.Unprotect(sealedToken);
                if (token is null || Hash(Encoding.UTF8.GetString(token)) != hash) continue;   // another user or computer
                phones.Add(new PairedPhone(id, (string?)item["name"] ?? "Phone", hash,
                    DateTimeOffset.TryParse((string?)item["pairedAt"], out var at) ? at : DateTimeOffset.MinValue,
                    (string?)item["kind"] ?? "phone", (string?)item["deviceId"]));
                secrets[id] = token;
            }
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or IOException) { }
    }

    private void Save()
    {
        var list = new JsonArray(phones.Select(p => (JsonNode)new JsonObject
        {
            ["id"] = p.Id, ["name"] = p.Name, ["tokenHash"] = p.TokenHash,
            ["token"] = secrets.TryGetValue(p.Id, out var key) ? Dpapi.Protect(key) : null,
            ["pairedAt"] = p.PairedAt.ToString("O"),
            ["kind"] = p.Kind,
            ["deviceId"] = p.DeviceId
        }).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["version"] = 2, ["phones"] = list }.ToJsonString(new() { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <summary>Windows data protection (DPAPI) for the current user: what it seals only this Windows account can open.</summary>
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const int UiForbidden = 0x1;
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("SwitchMonitor phone token");

    public static string Protect(byte[] data) =>
        Convert.ToBase64String(Run(data, protect: true) ?? throw new CryptographicException("Windows could not protect the phone token."));

    public static byte[]? Unprotect(string text)
    {
        try { return Run(Convert.FromBase64String(text), protect: false); }
        catch (FormatException) { return null; }
    }

    private static byte[]? Run(byte[] data, bool protect)
    {
        var input = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
        var entropy = new Blob { Size = Purpose.Length, Data = Marshal.AllocHGlobal(Purpose.Length) };
        IntPtr entropyPointer = Marshal.AllocHGlobal(Marshal.SizeOf<Blob>());
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            Marshal.Copy(Purpose, 0, entropy.Data, Purpose.Length);
            Marshal.StructureToPtr(entropy, entropyPointer, false);
            bool ok = protect
                ? CryptProtectData(ref input, "SwitchMonitor", entropyPointer, IntPtr.Zero, IntPtr.Zero, UiForbidden, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, entropyPointer, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            Marshal.FreeHGlobal(entropyPointer);
        }
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
