using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Remote;

/// <summary>A computer this one controls (and shows in its panel): where it is and the name it gave.</summary>
public sealed record PairedComputer(string Id, string Name, string Host, int Port, DateTimeOffset PairedAt, string? DeviceId = null);

/// <summary>
/// The computers this one has paired with as their controller. The counterpart of <see cref="PhoneStore"/>: there the
/// other side holds a token for this computer, here this computer holds the token the other one gave. Tokens are sealed
/// with DPAPI (this Windows user, this computer only) and never written in clear.
/// </summary>
public sealed class PeerStore
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "peers.json");

    private readonly string path;
    private readonly object gate = new();
    private readonly List<PairedComputer> computers = [];
    private readonly Dictionary<string, byte[]> tokens = [];

    public PeerStore(string? path = null)
    {
        this.path = path ?? DefaultPath;
        Load();
    }

    public event Action? Changed;

    public IReadOnlyList<PairedComputer> Computers { get { lock (gate) return computers.ToArray(); } }

    /// <summary>The token to prove this computer to <paramref name="id"/>, or null.</summary>
    public byte[]? TokenOf(string id) { lock (gate) return tokens.TryGetValue(id, out var token) ? token : null; }

    /// <summary>Adds (or replaces, same host and port) a paired computer with the token it handed over.</summary>
    public PairedComputer Add(string name, string host, int port, string token, DateTimeOffset now, string? deviceId = null)
    {
        PairedComputer computer;
        lock (gate)
        {
            var same = computers.FirstOrDefault(c => (deviceId is not null && c.DeviceId == deviceId)
                || (string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase) && c.Port == port));
            if (same is not null) { computers.Remove(same); tokens.Remove(same.Id); }
            string clean = string.IsNullOrWhiteSpace(name) ? host : name.Trim();
            computer = new PairedComputer(Guid.NewGuid().ToString("N")[..12], clean.Length > 60 ? clean[..60] : clean, host, port, now, deviceId);
            computers.Add(computer);
            tokens[computer.Id] = Encoding.UTF8.GetBytes(token);
            Save();
        }
        Changed?.Invoke();
        return computer;
    }

    public PairedComputer? FindByDevice(string deviceId) { lock (gate) return computers.FirstOrDefault(c => c.DeviceId == deviceId); }

    public bool Remove(string id)
    {
        lock (gate)
        {
            if (computers.RemoveAll(c => c.Id == id) == 0) return false;
            tokens.Remove(id);
            Save();
        }
        Changed?.Invoke();
        return true;
    }

    private void Load()
    {
        if (!File.Exists(path)) return;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path))?["computers"] is not JsonArray list) return;
            foreach (var item in list.OfType<JsonObject>())
            {
                if ((string?)item["id"] is not { } id || (string?)item["host"] is not { Length: > 0 } host ||
                    (string?)item["token"] is not { Length: > 0 } sealedToken) continue;
                byte[]? token = Dpapi.Unprotect(sealedToken);
                if (token is null) continue;   // another Windows user or computer: pair again
                computers.Add(new PairedComputer(id, (string?)item["name"] ?? host, host,
                    item["port"]?.GetValue<int>() ?? RemoteServer.DefaultPort,
                    DateTimeOffset.TryParse((string?)item["pairedAt"], out var at) ? at : DateTimeOffset.MinValue,
                    (string?)item["deviceId"]));
                tokens[id] = token;
            }
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidOperationException or FormatException) { }
    }

    private void Save()
    {
        var list = new JsonArray(computers.Select(c => (JsonNode)new JsonObject
        {
            ["id"] = c.Id, ["name"] = c.Name, ["host"] = c.Host, ["port"] = c.Port,
            ["token"] = tokens.TryGetValue(c.Id, out var key) ? Dpapi.Protect(key) : null,
            ["pairedAt"] = c.PairedAt.ToString("O"),
            ["deviceId"] = c.DeviceId
        }).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["version"] = 1, ["computers"] = list }.ToJsonString(new() { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
