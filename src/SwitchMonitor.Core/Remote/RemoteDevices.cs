using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Remote;

/// <summary>A display of the computer that publishes it: what the others need to build the same display with a network transport.</summary>
public sealed record RemoteDeviceInfo(
    string Key, string Name, string DeviceId, string? PnpId, bool IsBuiltIn, string TransportId, bool SnapsToLevels,
    IReadOnlyList<uint>? Levels, int? Home = null, bool Detects = false)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["key"] = Key, ["name"] = Name, ["deviceId"] = DeviceId, ["pnp"] = PnpId, ["builtIn"] = IsBuiltIn,
            ["transport"] = TransportId, ["snaps"] = SnapsToLevels
        };
        if (Home is int home) json["home"] = home;   // the input the owner is plugged into (displays that answer on every input)
        if (Detects) json["detects"] = true;   // this computer can tell whether the display shows it (so its offer of the display is reliable)
        if (Levels is { Count: > 0 }) json["levels"] = new JsonArray(Levels.Select(l => (JsonNode)JsonValue.Create((int)l)!).ToArray());
        return json;
    }

    public static RemoteDeviceInfo? FromJson(JsonObject json)
    {
        if ((string?)json["key"] is not { Length: > 0 } key || (string?)json["deviceId"] is not { } deviceId) return null;
        return new RemoteDeviceInfo(key, (string?)json["name"] ?? key, deviceId, (string?)json["pnp"], json["builtIn"]?.GetValue<bool>() == true,
            (string?)json["transport"] ?? "peer", json["snaps"]?.GetValue<bool>() == true,
            json["levels"] is JsonArray list ? list.Select(n => (uint)(n?.GetValue<int>() ?? 0)).ToArray() : null,
            json["home"] is JsonValue h ? h.GetValue<int>() : null, json["detects"]?.GetValue<bool>() == true);
    }
}

/// <summary>
/// The computer that owns a monitor lets the others read and write its VCP codes: the owner executes them on the real device,
/// the others see an ordinary display whose transport is this link. Everything the interface does for a local display (inputs,
/// picture modes, brightness, color, profiles) then works for a remote one without a second implementation.
/// </summary>
public static class DeviceHost
{
    /// <summary>The inventory published in the state: this computer's own displays.</summary>
    public static JsonArray Inventory(IEnumerable<MonitorDevice> devices, Func<MonitorDevice, string> displayName, Func<MonitorDevice, int?>? home = null) =>
        new(devices.Where(d => d.IsControllable).Select(d => (JsonNode)new RemoteDeviceInfo(d.Key, displayName(d), d.Identity.DeviceId,
            d.Identity.PnpId, d.Identity.IsBuiltIn, d.TransportId, d.SnapsToLevels, d.BrightnessLevels, home?.Invoke(d), d.AnswersOnOtherInputs && home?.Invoke(d) is not null).ToJson()).ToArray());

    /// <summary>Runs one request ("caps", "read" or "write" on one device) and returns the answer (always with ok true or false).</summary>
    public static JsonObject Handle(Func<string, MonitorDevice?> find, JsonElement request)
    {
        string op = request.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "";
        string key = request.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
        if (find(key) is not { IsControllable: true } device) return Fail("That display is not available on this computer now.");
        try
        {
            switch (op)
            {
                case "caps":
                    return new JsonObject { ["ok"] = true, ["text"] = device.ReadCapabilitiesText() };
                case "read":
                {
                    byte code = (byte)request.GetProperty("code").GetInt32();
                    var value = device.ReadOnce(code);   // one try: the asking computer has its own retries
                    return new JsonObject { ["ok"] = true, ["current"] = (long)value.Current, ["max"] = (long)value.Maximum };
                }
                case "write":
                {
                    byte code = (byte)request.GetProperty("code").GetInt32();
                    uint value = (uint)request.GetProperty("value").GetInt64();
                    device.Write(code, value, quiet: true);
                    return new JsonObject { ["ok"] = true };
                }
                default:
                    return Fail($"Unknown operation \"{op}\".");
            }
        }
        catch (Exception error) when (error is TransportException or NotSupportedException or ArgumentException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException)
        {
            return Fail(error.Message);
        }
    }

    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };
}

/// <summary>Builds a display that lives on another computer.</summary>
public static class RemoteMonitors
{
    /// <summary>
    /// A <see cref="MonitorDevice"/> whose transport sends every read and write to the owner. <paramref name="call"/> sends one
    /// request and waits for its answer (it may block: the interface already calls devices from worker threads).
    /// </summary>
    public static MonitorDevice Create(RemoteDeviceInfo info, string computerId, string computerName, Func<JsonObject, JsonObject> call,
        ICoreLog? log = null, QuirkDatabase? quirks = null)
    {
        var identity = new MonitorIdentity($"peer:{computerId}|{info.Key}", info.Name, info.DeviceId, "", info.PnpId, false, info.IsBuiltIn)
        {
            Label = info.IsBuiltIn ? info.Name : null,
            RemoteComputer = computerName
        };
        quirks ??= QuirkDatabase.LoadDefault();
        var transport = new PeerTransport(info, call);
        return new MonitorDevice(identity, transport, quirks.Match(info.DeviceId), log);
    }
}

/// <summary>The network as a monitor transport: each call is one request to the computer that owns the display.</summary>
internal sealed class PeerTransport(RemoteDeviceInfo info, Func<JsonObject, JsonObject> call) : IMonitorTransport
{
    public string Id => info.TransportId;   // the owner's transport: the quirks that depend on it (LG on AMD) apply as there
    public bool SupportsWrites => true;
    public bool SnapsToLevels => info.SnapsToLevels;
    public IReadOnlyList<uint>? BrightnessLevels => info.Levels;

    public string? ReadCapabilities()
    {
        var answer = Ask(new JsonObject { ["op"] = "caps" });
        return (string?)answer["text"];
    }

    public VcpValue Read(byte code)
    {
        var answer = Ask(new JsonObject { ["op"] = "read", ["code"] = (int)code });
        return new VcpValue((uint)(answer["current"]?.GetValue<long>() ?? 0), (uint)(answer["max"]?.GetValue<long>() ?? 0));
    }

    public void Write(byte code, uint value) =>
        Ask(new JsonObject { ["op"] = "write", ["code"] = (int)code, ["value"] = (long)value });

    private JsonObject Ask(JsonObject request)
    {
        request["type"] = "dev";
        request["device"] = info.Key;
        JsonObject answer;
        try { answer = call(request); }
        catch (Exception error) when (error is TimeoutException or InvalidOperationException or IOException or System.Net.WebSockets.WebSocketException)
        {
            throw new TransportException(Id, "The other computer did not answer: " + error.Message, error);
        }
        if (answer["ok"]?.GetValue<bool>() != true) throw new TransportException(Id, (string?)answer["error"] ?? "The other computer refused.");
        return answer;
    }

    public void Dispose() { }
}
