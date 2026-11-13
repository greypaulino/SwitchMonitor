using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Remote;

/// <summary>
/// A device that belongs to the ecosystem: its public key, signed by a computer that was already in it (the founder signs its own).
/// </summary>
/// <param name="Kind">"computer" or "phone". Only computers sign other devices in.</param>
/// <param name="IssuerId">The device id of the computer that vouched for this one (the same device for the founder).</param>
public sealed record MemberCert(string DeviceId, string Name, string Kind, string PublicKey, string IssuerId, DateTimeOffset IssuedAt, string Signature)
{
    public bool IsComputer => Kind == "computer";

    /// <summary>The text the issuer signed: everything that says who this is, and in which ecosystem.</summary>
    public string Payload(string ecosystemId) =>
        $"member|{ecosystemId}|{DeviceId}|{Name}|{Kind}|{PublicKey}|{IssuerId}|{IssuedAt.ToUnixTimeMilliseconds()}";

    public JsonObject ToJson() => new()
    {
        ["deviceId"] = DeviceId, ["name"] = Name, ["kind"] = Kind, ["key"] = PublicKey, ["issuer"] = IssuerId,
        ["at"] = IssuedAt.ToUnixTimeMilliseconds(), ["sig"] = Signature
    };

    public static MemberCert? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        try
        {
            string? id = (string?)o["deviceId"], name = (string?)o["name"], kind = (string?)o["kind"], key = (string?)o["key"], issuer = (string?)o["issuer"], sig = (string?)o["sig"];
            if (id is not { Length: >= 8 and <= 64 } || name is null || kind is not ("computer" or "phone") || key is null || issuer is null || sig is null) return null;
            return new MemberCert(id, name, kind, key, issuer, DateTimeOffset.FromUnixTimeMilliseconds(o["at"]!.GetValue<long>()), sig);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentOutOfRangeException or NullReferenceException) { return null; }
    }
}

/// <summary>A computer of the ecosystem took a device out; certificates issued before <see cref="At"/> stop counting.</summary>
public sealed record Revocation(string DeviceId, string IssuerId, DateTimeOffset At, string Signature)
{
    public string Payload(string ecosystemId) => $"revoke|{ecosystemId}|{DeviceId}|{IssuerId}|{At.ToUnixTimeMilliseconds()}";

    public JsonObject ToJson() => new() { ["deviceId"] = DeviceId, ["issuer"] = IssuerId, ["at"] = At.ToUnixTimeMilliseconds(), ["sig"] = Signature };

    public static Revocation? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        try
        {
            string? id = (string?)o["deviceId"], issuer = (string?)o["issuer"], sig = (string?)o["sig"];
            if (id is null || issuer is null || sig is null) return null;
            return new Revocation(id, issuer, DateTimeOffset.FromUnixTimeMilliseconds(o["at"]!.GetValue<long>()), sig);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or ArgumentOutOfRangeException or NullReferenceException) { return null; }
    }
}

/// <summary>
/// "My devices": the group of computers and phones that belong to one person, so they recognise each other without being paired
/// two by two. Every device has its own key pair (made at the first run; the private half is sealed for this Windows user). The
/// first computer founds the ecosystem; another device joins by typing the 6-digit code the founder (or any computer already in)
/// shows, which changes every 30 seconds and is the same kind of code an authenticator app makes (RFC 6238), and by a click of
/// approval on that computer. That computer then signs the newcomer's key (<see cref="MemberCert"/>). From then on two members
/// prove who they are by signing a challenge, and every connection carries the list of members, so the news spreads by itself.
/// A computer can take a device out (<see cref="Revoke"/>) and that too spreads. Nothing leaves the home network.
/// </summary>
public sealed class Ecosystem
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "ecosystem.json");

    public const int CodeSeconds = 30;
    public const int MaxCodeTries = 5;

    /// <summary>Certificates (and revocations) taken from one message: a group is a handful of devices.</summary>
    public const int MaxMerge = 64;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(1);

    private readonly string path;
    private readonly object gate = new();
    private readonly ECDsa key;
    private readonly byte[] totpSecret;
    private readonly Dictionary<string, MemberCert> certs = [];        // every certificate ever accepted (they vouch for others)
    private readonly Dictionary<string, Revocation> revoked = [];
    private string? id;
    private int failures;
    private DateTimeOffset lockedUntil = DateTimeOffset.MinValue;
    private long lastStep = -1;

    public Ecosystem(string? path = null, string? deviceId = null, string? name = null, string kind = "computer")
    {
        this.path = path ?? DefaultPath;
        DeviceId = deviceId ?? DeviceIdentity.Id;
        Name = name ?? Environment.MachineName;
        Kind = kind;
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        totpSecret = RandomNumberGenerator.GetBytes(20);
        if (!Load()) Save();
    }

    public string DeviceId { get; }
    public string Name { get; set; }
    public string Kind { get; }

    /// <summary>Raised when the members or the ecosystem change (on any thread).</summary>
    public event Action? Changed;

    /// <summary>This device's public key (base64 SubjectPublicKeyInfo).</summary>
    public string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>The ecosystem this device belongs to, or null.</summary>
    public string? Id { get { lock (gate) return id; } }

    public bool InEcosystem => Id is not null;

    /// <summary>The members that still count (revoked ones are left out), this device included.</summary>
    public IReadOnlyList<MemberCert> Members { get { lock (gate) return certs.Values.Where(Active).OrderBy(c => c.IssuedAt).ToArray(); } }

    public bool IsMember(string deviceId) { lock (gate) return certs.TryGetValue(deviceId, out var c) && Active(c); }

    public MemberCert? Find(string deviceId) { lock (gate) return certs.TryGetValue(deviceId, out var c) && Active(c) ? c : null; }

    // ---- Becoming a member ----

    /// <summary>Starts an ecosystem with this computer as its first member (nothing happens when it already belongs to one).</summary>
    public void Found()
    {
        lock (gate)
        {
            if (id is not null) return;
            id = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant()[..32];
            var root = Sign(new MemberCert(DeviceId, Clean(Name), Kind, PublicKey, DeviceId, DateTimeOffset.UtcNow, ""));
            certs[DeviceId] = root;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Vouches for a new device (a computer calls this once its person approved). Founds the ecosystem first when there is none.
    /// </summary>
    public MemberCert Admit(string deviceId, string name, string kind, string publicKey, DateTimeOffset now)
    {
        if (Kind != "computer") throw new InvalidOperationException("Only a computer can add devices.");
        Found();
        MemberCert cert;
        lock (gate)
        {
            cert = Sign(new MemberCert(deviceId, Clean(name), kind, publicKey, DeviceId, now, ""));
            certs[deviceId] = cert;
            Save();
        }
        Changed?.Invoke();
        return cert;
    }

    /// <summary>
    /// Takes in what the approving computer answered (<see cref="Export"/>): the ecosystem id and the signed members. True when this
    /// device's own certificate is in it and everything checks out; false (and nothing changes) otherwise.
    /// </summary>
    public bool Join(JsonObject? payload, bool replace = false)
    {
        if (payload is null || (string?)payload["id"] is not { Length: 32 } newId) return false;
        lock (gate)
        {
            if (id is not null && !replace) return false;
            string? oldId = id;
            var backup = (certs.ToDictionary(p => p.Key, p => p.Value), revoked.ToDictionary(p => p.Key, p => p.Value));
            id = newId;
            certs.Clear();
            revoked.Clear();
            MergeLocked(payload["members"] as JsonArray, payload["revoked"] as JsonArray);
            if (certs.TryGetValue(DeviceId, out var mine) && Active(mine) && mine.PublicKey == PublicKey)
            {
                Save();
            }
            else
            {
                id = oldId;
                certs.Clear(); revoked.Clear();
                foreach (var p in backup.Item1) certs[p.Key] = p.Value;
                foreach (var p in backup.Item2) revoked[p.Key] = p.Value;
                return false;
            }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Leaves the ecosystem (this device only; the others keep it).</summary>
    public void Leave()
    {
        lock (gate)
        {
            if (id is null) return;
            id = null;
            certs.Clear();
            revoked.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    // ---- Members, spreading and removing ----

    /// <summary>What to send another member: the ecosystem id, every certificate and every revocation.</summary>
    public JsonObject Export()
    {
        lock (gate)
            return new JsonObject
            {
                ["id"] = id,
                ["members"] = new JsonArray(certs.Values.Select(c => (JsonNode)c.ToJson()).ToArray()),
                ["revoked"] = new JsonArray(revoked.Values.Select(r => (JsonNode)r.ToJson()).ToArray())
            };
    }

    /// <summary>Takes in the members and revocations another member sent (each one checked). True when something new was learned.</summary>
    public bool Merge(JsonNode? members, JsonNode? revocations)
    {
        bool changed;
        lock (gate)
        {
            if (id is null) return false;
            changed = MergeLocked(members as JsonArray, revocations as JsonArray);
            if (changed) Save();
        }
        if (changed) Changed?.Invoke();
        return changed;
    }

    /// <summary>Takes a device out of the ecosystem for good (until a computer approves it again). Only a computer can; not itself (use <see cref="Leave"/>).</summary>
    public bool Revoke(string deviceId, DateTimeOffset now)
    {
        if (Kind != "computer" || deviceId == DeviceId) return false;
        lock (gate)
        {
            if (id is null || !certs.TryGetValue(deviceId, out var target) || !Active(target)) return false;
            var revocation = new Revocation(deviceId, DeviceId, now, "");
            revocation = revocation with { Signature = SignText(revocation.Payload(id)) };
            revoked[deviceId] = revocation;
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    private bool Active(MemberCert cert) => !revoked.TryGetValue(cert.DeviceId, out var r) || cert.IssuedAt > r.At;

    private MemberCert Sign(MemberCert unsigned) => unsigned with { Signature = SignText(unsigned.Payload(id!)) };

    /// <summary>Learns every certificate and revocation that checks out, repeating while a new one makes others checkable.</summary>
    private bool MergeLocked(JsonArray? members, JsonArray? revocations)
    {
        // At most MaxMerge of each per message, and what is already known is skipped before any signature is checked: a device
        // that has not signed in yet cannot make this computer spend seconds on thousands of forged certificates.
        var pendingCerts = (members ?? []).Take(MaxMerge).Select(MemberCert.FromJson).OfType<MemberCert>()
            .Where(c => !(certs.TryGetValue(c.DeviceId, out var known) && known.IssuedAt >= c.IssuedAt)).ToList();
        var pendingRevs = (revocations ?? []).Take(MaxMerge).Select(Revocation.FromJson).OfType<Revocation>()
            .Where(r => !(revoked.TryGetValue(r.DeviceId, out var known) && known.At >= r.At)).ToList();
        bool any = false, progress = true;
        while (progress)
        {
            progress = false;
            foreach (var cert in pendingCerts.ToArray())
            {
                if (!CertChecks(cert)) continue;
                pendingCerts.Remove(cert);
                if (certs.TryGetValue(cert.DeviceId, out var have) && have.IssuedAt >= cert.IssuedAt) continue;
                certs[cert.DeviceId] = cert;
                any = progress = true;
            }
            foreach (var rev in pendingRevs.ToArray())
            {
                if (!RevocationChecks(rev)) continue;
                pendingRevs.Remove(rev);
                if (revoked.TryGetValue(rev.DeviceId, out var have) && have.At >= rev.At) continue;
                revoked[rev.DeviceId] = rev;
                any = progress = true;
            }
        }
        return any;
    }

    private bool CertChecks(MemberCert cert)
    {
        if (id is null || !ValidKey(cert.PublicKey)) return false;
        if (cert.IssuerId == cert.DeviceId)
        {
            // The founder vouches for itself: its key must be the one the ecosystem id is made of.
            if (!cert.IsComputer || Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(cert.PublicKey))).ToLowerInvariant()[..32] != id) return false;
            return VerifyText(cert.PublicKey, cert.Payload(id), cert.Signature);
        }
        if (!certs.TryGetValue(cert.IssuerId, out var issuer) || !issuer.IsComputer) return false;
        if (revoked.TryGetValue(issuer.DeviceId, out var gone) && cert.IssuedAt > gone.At) return false;   // a removed computer cannot vouch any more
        return VerifyText(issuer.PublicKey, cert.Payload(id), cert.Signature);
    }

    private bool RevocationChecks(Revocation rev) =>
        id is not null && certs.TryGetValue(rev.IssuerId, out var issuer) && issuer.IsComputer && VerifyText(issuer.PublicKey, rev.Payload(id), rev.Signature);

    private static bool ValidKey(string publicKey)
    {
        try { using var probe = ECDsa.Create(); probe.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _); return true; }
        catch (Exception error) when (error is CryptographicException or FormatException) { return false; }
    }

    // ---- Proving who is who ----

    /// <summary>This device's signature (base64) of a text.</summary>
    public string SignText(string text) => Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256));

    /// <summary>Whether <paramref name="signature"/> is the signature of <paramref name="text"/> by the member <paramref name="deviceId"/>.</summary>
    public bool Verify(string deviceId, string text, string signature)
    {
        MemberCert? member;
        lock (gate) member = certs.TryGetValue(deviceId, out var c) && Active(c) ? c : null;
        return member is not null && VerifyText(member.PublicKey, text, signature);
    }

    private static bool VerifyText(string publicKey, string text, string signature)
    {
        try
        {
            using var other = ECDsa.Create();
            other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return other.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception error) when (error is CryptographicException or FormatException) { return false; }
    }

    // ---- The code that lets a new device in (the same kind an authenticator app makes) ----

    /// <summary>The 6-digit code for the 30 seconds <paramref name="now"/> is in.</summary>
    public string Code(DateTimeOffset now) => Totp.Code(totpSecret, Totp.Step(now));

    public int SecondsLeft(DateTimeOffset now) => CodeSeconds - (int)(now.ToUnixTimeSeconds() % CodeSeconds);

    /// <summary>The secret as an authenticator app asks for it (base32); the app then shows the same code.</summary>
    public string SetupKey => Totp.Base32(totpSecret);

    public string OtpAuthUri => $"otpauth://totp/SwitchMonitor:{Uri.EscapeDataString(Name)}?secret={SetupKey}&issuer=SwitchMonitor&digits=6&period={CodeSeconds}";

    /// <summary>True while too many wrong codes were tried (see <see cref="MaxCodeTries"/>): every code is refused for <see cref="LockTime"/>.</summary>
    public bool IsLocked(DateTimeOffset now) { lock (gate) return now < lockedUntil; }

    /// <summary>
    /// Checks the proof a joining device made with the code it was given (<see cref="EnrollProof"/>), for the current 30 seconds and
    /// the ones next to it. A code works once. <paramref name="answer"/> is this computer's proof back, so the newcomer knows it
    /// reached the computer that showed the code.
    /// </summary>
    public bool CheckEnrollProof(string proof, string nonce, string deviceId, string publicKey, DateTimeOffset now, out string code, string binding = "")
    {
        code = "";
        lock (gate)
        {
            if (now < lockedUntil) return false;
            long current = Totp.Step(now);
            byte[] offered;
            try { offered = Convert.FromBase64String(proof); }
            catch (FormatException) { return Fail(now); }
            string? match = null;
            long matchStep = 0;
            for (long step = current - 1; step <= current + 1; step++)
            {
                string candidate = Totp.Code(totpSecret, step);
                // Every candidate is compared (no early exit): the time taken says nothing.
                if (CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(EnrollProof(candidate, nonce, deviceId, publicKey, binding)), offered) && step > lastStep)
                { match = candidate; matchStep = step; }
            }
            if (match is null) return Fail(now);
            lastStep = matchStep;
            failures = 0;
            code = match;
            return true;
        }
    }

    private bool Fail(DateTimeOffset now)
    {
        if (++failures >= MaxCodeTries) { lockedUntil = now + LockTime; failures = 0; }
        return false;
    }

    /// <summary>What a joining device sends instead of the code itself: HMAC(code, nonce, who it is, its key).</summary>
    public static string EnrollProof(string code, string nonce, string deviceId, string publicKey, string binding = "") =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes($"enroll:{nonce}:{deviceId}:{publicKey}" + (binding.Length > 0 ? ":" + binding : ""))));

    /// <summary>What the approving computer sends back with the ecosystem, to show it knew the code.</summary>
    public static string EnrollAnswer(string code, string nonce, string deviceId, string approverId, string ecosystemId, string binding = "") =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes($"approver:{nonce}:{deviceId}:{approverId}:{ecosystemId}" + (binding.Length > 0 ? ":" + binding : ""))));

    // ---- Saved on disk (ecosystem.json); the private key and the code secret are sealed for this Windows user ----

    private static string Clean(string name)
    {
        string text = string.IsNullOrWhiteSpace(name) ? "Device" : name.Replace('|', ' ').Trim();
        return text.Length > 60 ? text[..60] : text;
    }

    private bool Load()
    {
        if (!File.Exists(path)) return false;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return false;
            // The key sealed by another Windows user (or another computer) cannot be opened: start again with this one.
            if ((string?)root["deviceKey"] is not { } sealedKey || Dpapi.Unprotect(sealedKey) is not { } pkcs8) return false;
            if ((string?)root["deviceId"] != DeviceId) return false;
            key.ImportPkcs8PrivateKey(pkcs8, out _);
            if ((string?)root["totp"] is { } sealedSecret && Dpapi.Unprotect(sealedSecret) is { Length: 20 } secret) secret.CopyTo(totpSecret, 0);
            if ((string?)root["id"] is { Length: 32 } saved)
            {
                id = saved;
                MergeLocked(root["members"] as JsonArray, root["revoked"] as JsonArray);
                if (!certs.TryGetValue(DeviceId, out var mine) || mine.PublicKey != PublicKey) { id = null; certs.Clear(); revoked.Clear(); }
            }
            return true;
        }
        catch (Exception error) when (error is JsonException or IOException or CryptographicException or FormatException or InvalidOperationException) { return false; }
    }

    private void Save()
    {
        var root = new JsonObject
        {
            ["version"] = 1,
            ["deviceId"] = DeviceId,
            ["deviceKey"] = Dpapi.Protect(key.ExportPkcs8PrivateKey()),
            ["totp"] = Dpapi.Protect(totpSecret),
            ["id"] = id,
            ["members"] = new JsonArray(certs.Values.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["revoked"] = new JsonArray(revoked.Values.Select(r => (JsonNode)r.ToJson()).ToArray())
        };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        catch (IOException) { }
    }
}

/// <summary>Time-based one-time codes (RFC 6238, HMAC-SHA1, 6 digits, 30 seconds): what Google or Microsoft Authenticator make.</summary>
public static class Totp
{
    public static long Step(DateTimeOffset now) => now.ToUnixTimeSeconds() / Ecosystem.CodeSeconds;

    public static string Code(byte[] secret, long step, int digits = 6)
    {
        byte[] counter = new byte[8];
        for (int i = 7; i >= 0; i--, step >>= 8) counter[i] = (byte)(step & 0xFF);
        byte[] hash = HMACSHA1.HashData(secret, counter);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % (int)Math.Pow(10, digits)).ToString("D" + digits);
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>RFC 4648 base32 without padding (how authenticator apps take a secret typed by hand).</summary>
    public static string Base32(byte[] data)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { text.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }
}
