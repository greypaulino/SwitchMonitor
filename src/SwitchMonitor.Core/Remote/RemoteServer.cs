using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Diagnostics;

namespace SwitchMonitor.Core.Remote;

/// <summary>A device that typed the right code and wants to join the ecosystem (see <see cref="RemoteServer.Enrolling"/>).</summary>
/// <param name="Verification">The 4-digit number of the connection, to compare with the one the other device shows.</param>
/// <param name="Pairing">True when it is a pairing by code (a phone or a computer to control this one), not joining the group.</param>
public sealed record EnrollRequest(string DeviceId, string Name, string Kind, string Address, string Verification = "", bool Pairing = false);

/// <summary>What the phone server shows and does; the app implements it on top of the brightness panel.</summary>
public interface IRemoteHost
{
    /// <summary>The name the phone shows for this computer.</summary>
    string ComputerName { get; }

    /// <summary>The web page served at "/" (a simple control page, also used for testing from a browser).</summary>
    string Page { get; }

    /// <summary>The current state as a JSON object (displays, profiles, picture modes, inputs).</summary>
    Task<JsonObject> StateAsync();

    /// <summary>Runs one command from a paired phone; returns an error text for the phone, or null.</summary>
    Task<string?> CommandAsync(string type, JsonElement message);

    /// <summary>Mouse and keyboard events of a paired COMPUTER that shares its input with this one (<paramref name="deviceId"/> is that computer).</summary>
    void InputEvent(JsonElement message, string? deviceId) { }

    /// <summary>The programs a phone may open or close from its Remote tab: {"allowed", "apps": [{id, name, running}]}.</summary>
    Task<JsonObject> AppsAsync(JsonElement request) => Task.FromResult(new JsonObject { ["allowed"] = false, ["apps"] = new JsonArray() });

    /// <summary>A computer or phone of the group copied some text (a "clip" message): the app puts it on this computer's clipboard.</summary>
    void ClipboardEvent(JsonElement message, string? deviceId) { }

    /// <summary>One piece ("blob" message) of an image or a file that a computer or phone of the group sends to this computer.</summary>
    void BlobEvent(JsonElement message, string? deviceId) { }

    /// <summary>A computer of the group sends its profiles ("profiles" message); only computers may (a phone never has this).</summary>
    void ProfilesEvent(JsonElement message, string deviceId) { }

    /// <summary>A computer of the group tells its screens or where it sits on the shared mouse's map ("share" message).</summary>
    void ShareEvent(JsonElement message, string deviceId) { }

    /// <summary>Another computer reads or writes one of this computer's displays ("dev" request); the answer is sent back.</summary>
    Task<JsonObject> DeviceRequestAsync(JsonElement message) => Task.FromResult(new JsonObject { ["ok"] = false, ["error"] = "Not supported." });
}

/// <summary>
/// Lets a phone on the home network control the displays. Plain .NET only: a TCP listener that serves the control page
/// over HTTP and upgrades "/ws" to a WebSocket (JSON messages both ways), plus a UDP reply so the phone can find the
/// computer. Only private (home network) addresses may connect, and every command needs a paired phone. The token
/// travels only once, when pairing; afterwards each connection starts with a random challenge the phone answers with
/// an HMAC of its token, and the computer answers the phone's own challenge the same way (see PhoneStore.Verify).
/// </summary>
public sealed class RemoteServer : IDisposable
{
    public const int DefaultPort = 47820;
    public const int DiscoveryPort = 47821;
    /// <summary>Version of the computer-to-computer protocol (the phone ignores it).</summary>
    public const int Protocol = 1;
    public const string DiscoveryQuestion = "SWITCHMONITOR?";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxMessage = 1024 * 1024;   // a shared clipboard text can be long
    private const int MaxSignInMessage = 128 * 1024;   // before signing in only a key, a code, a proof or a few certificates are sent

    /// <summary>Connections served at once (page loads, phones); more are closed at once (a flood on the network).</summary>
    public const int MaxConnections = 64;

    /// <summary>A connection must pair or prove it is a paired phone within this time, and within a few tries.</summary>
    public static readonly TimeSpan SignInTime = TimeSpan.FromSeconds(10);
    private const int MaxSignInErrors = 5;
    private int connections;
    /// <summary>Connections one address may hold at once (the loopback address is not limited): one device cannot take every slot.</summary>
    public const int MaxPerAddress = 8;
    private readonly ConcurrentDictionary<IPAddress, int> perAddress = new();
    private readonly ConcurrentDictionary<IPAddress, long> lastDiscovery = new();

    private readonly PhoneStore store;
    private readonly IRemoteHost host;
    private readonly ICoreLog log;
    private readonly int requestedPort;
    private readonly int discoveryPort;
    private readonly ConcurrentDictionary<Guid, Client> clients = new();
    private TcpListener? listener;
    private UdpClient? discovery;
    private CancellationTokenSource? stop;

    /// <summary>A connected, paired phone. <see cref="Sending"/> keeps replies and broadcasts from interleaving.</summary>
    private sealed class Client(WebSocket socket, PairedPhone phone, SemaphoreSlim sending)
    {
        public WebSocket Socket { get; } = socket;
        public PairedPhone Phone { get; } = phone;
        public SemaphoreSlim Sending { get; } = sending;
    }

    public RemoteServer(PhoneStore store, IRemoteHost host, ICoreLog? log = null, int port = DefaultPort, int discoveryPort = DiscoveryPort, Ecosystem? ecosystem = null)
    {
        this.discoveryPort = discoveryPort;
        this.store = store;
        this.host = host;
        this.log = log ?? NullLog.Instance;
        this.ecosystem = ecosystem;
        requestedPort = port;
        store.Changed += DropRemovedPhones;
        if (ecosystem is not null) ecosystem.Changed += DropRemovedPhones;
    }

    private readonly Ecosystem? ecosystem;

    /// <summary>
    /// The web page for phones without the app (a browser cannot do the encryption on a plain http address): off unless the person
    /// turns it on in Settings, and then the connections of that page (they send an Origin) are NOT encrypted. The app and the
    /// computers always are.
    /// </summary>
    public bool AllowUnencryptedPage { get; set; }

    /// <summary>
    /// A device typed the right code and asks to join the ecosystem: the app asks the person (name, kind, address) and answers
    /// true to let it in. No answer in a minute counts as no.
    /// </summary>
    public Func<EnrollRequest, Task<bool>>? Enrolling { get; set; }

    /// <summary>Whether a connected client still has the right to be here (a paired phone, or a member of the ecosystem).</summary>
    private bool StillAllowed(PairedPhone phone) =>
        phone.Id.StartsWith(MemberPrefix, StringComparison.Ordinal)
            ? ecosystem is not null && ecosystem.IsMember(phone.Id[MemberPrefix.Length..])
            : store.IsPaired(phone.Id);

    private const string MemberPrefix = "eco-";

    public bool IsRunning => listener is not null;

    /// <summary>The port in use (the requested one, or the one Windows chose for port 0).</summary>
    public int Port { get; private set; }

    /// <summary>Paired phones connected now.</summary>
    public int ConnectedCount => clients.Count;
    public IReadOnlyList<string> ConnectedPhones => clients.Values.Select(c => c.Phone.Name).ToArray();

    /// <summary>Raised (on a worker thread) when a phone connects or disconnects.</summary>
    public event Action? ClientsChanged;

    /// <summary>
    /// A computer paired with this one by code and handed over a token for this computer to control it back: its address,
    /// the port of its own server, its device id and name. The app stores it so the pairing works both ways.
    /// </summary>
    public event Action<CounterPairing>? ComputerPaired;

    /// <summary>
    /// Another computer asked to pair: this one made a pairing code at once and the app shows it on screen (name, code, when it
    /// stops working), so nobody has to start the pairing here by hand. The code is typed on the computer that asked.
    /// </summary>
    public event Action<string, string, DateTimeOffset>? PairRequested;

    private DateTimeOffset lastPairRequest = DateTimeOffset.MinValue;

    /// <summary>A connected computer asked this one to forget it (it was removed over there): its device id.</summary>
    public event Action<string>? ComputerForgot;

    public void Start(bool withDiscovery = true)
    {
        if (listener is not null) return;
        stop = new CancellationTokenSource();
        var tcp = new TcpListener(IPAddress.IPv6Any, requestedPort);
        tcp.Server.DualMode = true;
        tcp.Start();
        listener = tcp;
        Port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        _ = AcceptLoop(tcp, stop.Token);
        if (withDiscovery)
        {
            try
            {
                discovery = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort)) { EnableBroadcast = true };
                _ = DiscoveryLoop(discovery, stop.Token);
            }
            catch (SocketException error) { log.Write(LogLevel.Warning, "Phone: the computer cannot be found automatically (UDP port in use); the phone can still connect by address.", error); }
        }
        log.Write(LogLevel.Info, $"Phone access on: port {Port}.");
    }

    public void Stop()
    {
        if (listener is null) return;
        stop?.Cancel();
        listener.Stop();
        listener = null;
        discovery?.Dispose();
        discovery = null;
        foreach (var client in clients.Values) _ = CloseAsync(client, "SwitchMonitor stopped phone access.");
        clients.Clear();
        ClientsChanged?.Invoke();
        log.Write(LogLevel.Info, "Phone access off.");
    }

    public void Dispose()
    {
        store.Changed -= DropRemovedPhones;
        if (ecosystem is not null) ecosystem.Changed -= DropRemovedPhones;
        Stop();
    }

    /// <summary>Sends a message (the shared clipboard) to every connected phone; computers are reached through their own connection.</summary>
    public async Task SendToPhonesAsync(JsonObject message)
    {
        string json = message.ToJsonString();
        foreach (var client in clients.Values.Where(c => !c.Phone.IsComputer)) await SendAsync(client.Socket, client.Sending, json);
    }

    /// <summary>The phones connected now (id and name), where a file can be sent.</summary>
    public IReadOnlyList<PairedPhone> PhonesOnline => clients.Values.Where(c => !c.Phone.IsComputer).Select(c => c.Phone).ToArray();

    /// <summary>Sends a message to one connected phone (by <see cref="PairedPhone.Id"/>); false when it is not connected.</summary>
    public async Task<bool> SendToPhoneAsync(string phoneId, JsonObject message)
    {
        var client = clients.Values.FirstOrDefault(c => !c.Phone.IsComputer && c.Phone.Id == phoneId);
        if (client is null || client.Socket.State != WebSocketState.Open) return false;
        await SendAsync(client.Socket, client.Sending, message.ToJsonString());
        return true;
    }

    /// <summary>Sends the state to every connected phone (called by the app when something changed).</summary>
    public async Task BroadcastAsync(JsonObject state)
    {
        if (clients.IsEmpty) return;
        string json = Message("state", state);
        foreach (var client in clients.Values) await SendAsync(client.Socket, client.Sending, json);
    }

    // ---- Connections ----

    private async Task AcceptLoop(TcpListener tcp, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient connection;
            try { connection = await tcp.AcceptTcpClientAsync(cancel); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Serve(connection, cancel);
        }
    }

    private async Task Serve(TcpClient connection, CancellationToken cancel)
    {
        if (Interlocked.Increment(ref connections) > MaxConnections)
        {
            Interlocked.Decrement(ref connections);
            connection.Dispose();
            return;
        }
        IPAddress? ip = null;
        try { ip = (connection.Client.RemoteEndPoint as IPEndPoint)?.Address; }
        catch (Exception error) when (error is ObjectDisposedException or SocketException) { }
        if (ip is not null && ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        bool counted = ip is not null && !IPAddress.IsLoopback(ip);
        if (counted && perAddress.AddOrUpdate(ip!, 1, (_, n) => n + 1) > MaxPerAddress)
        {
            perAddress.AddOrUpdate(ip!, 0, (_, n) => Math.Max(0, n - 1));
            Interlocked.Decrement(ref connections);
            connection.Dispose();
            return;
        }
        try { await ServeOne(connection, cancel); }
        finally
        {
            if (counted) perAddress.AddOrUpdate(ip!, 0, (_, n) => Math.Max(0, n - 1));
            Interlocked.Decrement(ref connections);
        }
    }

    private async Task ServeOne(TcpClient connection, CancellationToken cancel)
    {
        using (connection)
        {
            connection.NoDelay = true;   // small answers go out at once (a slider drag): no waiting to be bundled with the next one
            if (connection.Client.RemoteEndPoint is not IPEndPoint remote || !LocalNetwork.IsAllowed(remote.Address))
            {
                log.Write(LogLevel.Warning, $"Phone: refused a connection from outside the home network ({connection.Client.RemoteEndPoint}).");
                return;
            }
            try
            {
                var stream = connection.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await ReadRequestAsync(stream, timeout.Token);
                if (request is null) return;
                var (method, path, headers) = request.Value;
                if (method != "GET") { await RespondAsync(stream, "405 Method Not Allowed", "text/plain", "Method not allowed"); return; }
                if (path is "/" or "/index.html")
                {
                    await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", AllowUnencryptedPage ? host.Page
                        : "<!doctype html><meta name=viewport content='width=device-width'><body style='font:16px system-ui;background:#111;color:#eee;padding:24px'>" +
                          "This page is turned off. Use the SwitchMonitor app, or turn on the web page in SwitchMonitor > Settings > Devices (it is not encrypted).");
                    return;
                }
                if (path == "/ws" && headers.TryGetValue("upgrade", out var upgrade) && upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)
                    && headers.TryGetValue("sec-websocket-key", out var key))
                {
                    // A web page in a browser sends its Origin; only this server's own page (same address) may open the socket.
                    // The phone app and the computers send none.
                    if (headers.TryGetValue("origin", out var origin) && !SameOrigin(origin, headers.GetValueOrDefault("host")))
                    {
                        log.Write(LogLevel.Warning, $"Phone: refused a web page from another site ({PhoneStore.CleanText(origin, 80)}) at {remote.Address}.");
                        await RespondAsync(stream, "403 Forbidden", "text/plain", "Forbidden");
                        return;
                    }
                    string accept = AcceptKey(key);
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                        $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
                    await stream.WriteAsync(head, cancel);
                    using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                    {
                        IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20)
                    });
                    await TalkAsync(socket, remote, cancel, plainAllowed: AllowUnencryptedPage && headers.ContainsKey("origin"));
                    return;
                }
                await RespondAsync(stream, "404 Not Found", "text/plain", "Not found");
            }
            catch (Exception error) when (error is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                log.Write(LogLevel.Debug, $"Phone: connection from {remote.Address} ended ({error.Message}).");
            }
        }
    }

    /// <summary>The WebSocket conversation: pair or hello first, then commands; the state comes after each change.</summary>
    private async Task TalkAsync(WebSocket socket, IPEndPoint remote, CancellationToken cancel, bool plainAllowed)
    {
        var sending = new SemaphoreSlim(1, 1);
        Client? client = null;
        Guid id = Guid.NewGuid();
        string nonce = PhoneStore.NewNonce();
        int errors = 0;
        // Not signed in after a few seconds or a few wrong tries: the connection is closed.
        using var signIn = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        signIn.CancelAfter(SignInTime);
        try
        {
            using var channelKey = SecureChannel.NewKey();
            await SendAsync(socket, sending, Message("challenge", new JsonObject { ["nonce"] = nonce, ["version"] = SecureChannel.Version, ["ecdh"] = SecureChannel.PublicKeyOf(channelKey) }));
            while (socket.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                if (client is null && errors >= MaxSignInErrors) break;
                string? text = await SecureChannel.ReceiveAsync(socket, client is null ? MaxSignInMessage : MaxMessage, client is null ? signIn.Token : cancel);
                if (text is null) break;
                JsonElement message;
                string type;
                try
                {
                    using var document = JsonDocument.Parse(text);
                    message = document.RootElement.Clone();
                    type = message.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                }
                catch (JsonException) { errors++; await SendAsync(socket, sending, Error("Not a JSON message.")); continue; }

                if (type == "key")
                {
                    // The other side's half of the key exchange: from the next message on, everything is encrypted.
                    if (SecureChannel.Of(socket) is not null || !SecureChannel.ServerAccept(socket, channelKey, nonce, message.TryGetProperty("ecdh", out var theirKey) ? theirKey.GetString() ?? "" : ""))
                    {
                        await SendAsync(socket, sending, Error("The key exchange failed."));
                        break;
                    }
                    await SendAsync(socket, sending, Message("ready", new JsonObject()));
                    continue;
                }
                if (SecureChannel.Of(socket) is null && !plainAllowed)
                {
                    errors++;
                    await SendAsync(socket, sending, Error("This computer needs an encrypted connection: update SwitchMonitor on the other device.", "encrypt"));
                    continue;
                }
                if (type == "pairRequest")
                {
                    var asked = DateTimeOffset.Now;
                    if (store.IsBlocked(asked))
                    {
                        await SendAsync(socket, sending, Error("Too many wrong codes. Pairing is paused for a few minutes."));
                        errors++;
                        continue;
                    }
                    if (asked - lastPairRequest < TimeSpan.FromSeconds(4))
                    {
                        await SendAsync(socket, sending, Error("A pairing request was just made. Wait a few seconds and try again."));
                        continue;
                    }
                    lastPairRequest = asked;
                    string who = message.TryGetProperty("name", out var rn) ? rn.GetString() ?? "" : "";
                    who = PhoneStore.CleanText(who);
                    if (who.Length == 0) who = remote.Address.ToString();
                    // A code already on screen stays (a device that is not typing it must not be able to cancel the one the person is
                    // reading): a request while it lasts only shows it again.
                    string shown = store.CurrentCode(asked) ?? store.NewCode(asked);
                    log.Write(LogLevel.Info, $"Pairing requested by \"{who}\" ({remote.Address}); the code is on screen.");
                    PairRequested?.Invoke(who, shown, store.CodeExpires);
                    await SendAsync(socket, sending, Message("pairRequested", new JsonObject()));
                    continue;
                }
                if (type == "pair")
                {
                    string code = message.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                    string name = PhoneStore.CleanText(message.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "");
                    if (store.IsBlocked(DateTimeOffset.Now))
                    {
                        errors++;
                        await SendAsync(socket, sending, Error("Too many wrong codes. Pairing is paused for a few minutes."));
                        continue;
                    }
                    string kind = message.TryGetProperty("kind", out var k) && k.GetString() == "computer" ? "computer" : "phone";
                    string? otherDevice = message.TryGetProperty("deviceId", out var di) ? di.GetString() : null;
                    var pairingAt = DateTimeOffset.Now;
                    if (!store.CheckCode(code, pairingAt))
                    {
                        errors++;
                        log.Write(LogLevel.Warning, $"Phone: wrong or expired pairing code from {remote.Address}.");
                        await SendAsync(socket, sending, Error("The code is wrong or expired. Show a new one in SwitchMonitor > Settings > Phone."));
                        continue;
                    }
                    // The right code is not enough: the person at this computer says yes, seeing who it is and the number to compare.
                    if (Enrolling is not null)
                    {
                        var asking = new EnrollRequest(otherDevice ?? "", PhoneStore.CleanText(name) is { Length: > 0 } shown ? shown : remote.Address.ToString(),
                            kind, remote.Address.ToString(), SecureChannel.Verification(socket), Pairing: true);
                        if (!await AskAsync(asking))
                        {
                            log.Write(LogLevel.Info, $"Pairing: \"{asking.Name}\" was not allowed.");
                            store.CancelCode();
                            await SendAsync(socket, sending, Error("The person at that computer said no (or did not answer)."));
                            continue;
                        }
                    }
                    string? token = store.Pair(code, name, DateTimeOffset.Now, kind, kind == "computer" ? otherDevice : null);
                    if (token is null)
                    {
                        errors++;
                        await SendAsync(socket, sending, Error("The code expired while waiting. Show a new one."));
                        continue;
                    }
                    client = Join(id, socket, sending, store.Find(token)!);
                    log.Write(LogLevel.Info, $"{(kind == "computer" ? "Computer" : "Phone")} \"{client.Phone.Name}\" paired ({remote.Address}).");
                    if (kind == "computer" && otherDevice is not null && message.TryGetProperty("counterToken", out var ct) && ct.GetString() is { Length: > 0 } counter
                        && message.TryGetProperty("port", out var cp) && cp.TryGetInt32(out int counterPort))
                        ComputerPaired?.Invoke(new CounterPairing(name, remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4().ToString() : remote.Address.ToString(),
                            counterPort, counter, otherDevice));
                    await SendAsync(socket, sending, Message("paired", new JsonObject
                    {
                        ["token"] = token, ["computer"] = host.ComputerName, ["deviceId"] = DeviceIdentity.Id, ["protocol"] = Protocol, ["port"] = Port
                    }));
                    await SendAsync(socket, sending, Message("state", await host.StateAsync()));
                    continue;
                }
                if (type == "enroll")
                {
                    if (await EnrollAsync(socket, sending, remote, message, nonce)) break;   // done: the newcomer signs in again as a member
                    errors++;
                    continue;
                }
                if (type == "member")
                {
                    // A member of the ecosystem signs in with its key: no token, no code. It sends what it knows of the ecosystem first.
                    var joined = await MemberSignInAsync(socket, sending, message, nonce);
                    if (joined is null) { errors++; continue; }
                    client = Join(id, socket, sending, joined);
                    log.Write(LogLevel.Debug, $"Ecosystem: \"{joined.Name}\" signed in ({remote.Address}).");
                    await SendAsync(socket, sending, Message("state", await host.StateAsync()));
                    continue;
                }
                if (type == "hello")
                {
                    // The phone's answer to our challenge, and its own challenge for us. A phone of version 1 sends its
                    // token instead: it is told to pair again (that token is not accepted any more).
                    string phoneNonce = message.TryGetProperty("nonce", out var pn) ? pn.GetString() ?? "" : "";
                    string proof = message.TryGetProperty("proof", out var pr) ? pr.GetString() ?? "" : "";
                    if (store.Verify(nonce, phoneNonce, proof, out string? answer, SecureChannel.Binding(socket)) is not { } phone)
                    {
                        errors++;
                        log.Write(LogLevel.Warning, $"Phone: a connection from {remote.Address} could not prove it is a paired phone.");
                        await SendAsync(socket, sending, Error("This phone is not paired (or was removed). Pair it again with a new code.", "unpaired"));
                        continue;
                    }
                    client = Join(id, socket, sending, phone);
                    await SendAsync(socket, sending, Message("welcome", new JsonObject { ["computer"] = host.ComputerName, ["proof"] = answer }));
                    await SendAsync(socket, sending, Message("state", await host.StateAsync()));
                    continue;
                }
                if (client is null || !StillAllowed(client.Phone))
                {
                    errors++;
                    await SendAsync(socket, sending, Error("Pair this phone first.", "unpaired"));
                    continue;
                }
                if (type == "dev")
                {
                    // Only a paired COMPUTER may read and write VCP codes; a phone has its own, narrower commands.
                    long requestId = message.TryGetProperty("id", out var ri) && ri.TryGetInt64(out long idValue) ? idValue : 0;
                    JsonObject result = client.Phone.IsComputer
                        ? await host.DeviceRequestAsync(message)
                        : new JsonObject { ["ok"] = false, ["error"] = "Only a paired computer can do this." };
                    result["id"] = requestId;
                    await SendAsync(socket, sending, Message("devResult", result));
                    continue;
                }
                if (type == "forget")
                {
                    // A computer that is removed over there tells us, so it cannot keep controlling this one.
                    if (client.Phone is { IsComputer: true, DeviceId: { } gone })
                    {
                        store.RemoveByDevice(gone);
                        ComputerForgot?.Invoke(gone);
                    }
                    continue;
                }
                if (type == "input" && message.TryGetProperty("op", out _))
                {
                    // A paired computer (sharing on) or a paired phone (when the person allowed it) may move the mouse and type here; the
                    // host checks which. A phone has no device id.
                    host.InputEvent(message, client.Phone.IsComputer ? client.Phone.DeviceId : null);
                    continue;
                }
                if (type == "clip")
                {
                    host.ClipboardEvent(message, client.Phone.IsComputer ? client.Phone.DeviceId : null);
                    continue;
                }
                if (type == "blob")
                {
                    host.BlobEvent(message, client.Phone.IsComputer ? client.Phone.DeviceId : null);
                    continue;
                }
                if (type == "share")
                {
                    if (client.Phone is { IsComputer: true, DeviceId: { } mapSender }) host.ShareEvent(message, mapSender);
                    continue;
                }
                if (type == "profiles")
                {
                    if (client.Phone is { IsComputer: true, DeviceId: { } sender }) host.ProfilesEvent(message, sender);
                    continue;
                }
                if (type == "apps")
                {
                    // The programs of this computer, for a phone's Remote tab (only a paired phone or computer is here).
                    await SendAsync(socket, sending, Message("apps", await host.AppsAsync(message)));
                    continue;
                }
                if (type == "get")
                {
                    await SendAsync(socket, sending, Message("state", await host.StateAsync()));
                    continue;
                }
                string? problem = await host.CommandAsync(type, message);
                if (problem is not null) await SendAsync(socket, sending, Error(problem));
            }
        }
        finally
        {
            if (clients.TryRemove(id, out var gone))
            {
                log.Write(LogLevel.Info, $"Phone \"{gone.Phone.Name}\" disconnected.");
                ClientsChanged?.Invoke();
            }
        }
    }

    // ---- The ecosystem: joining it with the code, and signing in as a member ----

    /// <summary>
    /// "enroll": a device proves it knows this computer's current code, the person here says yes, and the device is vouched for.
    /// True when it joined. Anything else is answered with an error and counts as a wrong try.
    /// </summary>
    private async Task<bool> EnrollAsync(WebSocket socket, SemaphoreSlim sending, IPEndPoint remote, JsonElement message, string nonce)
    {
        string Text(string name) => message.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        if (ecosystem is null || Enrolling is null) { await SendAsync(socket, sending, Error("This computer cannot add devices.")); return false; }
        string deviceId = Text("deviceId"), name = Text("name"), kind = Text("kind") == "phone" ? "phone" : "computer", publicKey = Text("key"), proof = Text("proof");
        if (deviceId.Length is < 8 or > 64 || publicKey.Length is 0 or > 400)
        {
            await SendAsync(socket, sending, Error("Not a valid request."));
            return false;
        }
        var now = DateTimeOffset.Now;
        if (ecosystem.IsLocked(now))
        {
            await SendAsync(socket, sending, Error("Too many wrong codes. Wait a minute and try again."));
            return false;
        }
        if (!ecosystem.CheckEnrollProof(proof, nonce, deviceId, publicKey, now, out string code, SecureChannel.Binding(socket)))
        {
            log.Write(LogLevel.Warning, $"Ecosystem: wrong or expired code from {remote.Address}.");
            await SendAsync(socket, sending, Error("The code is wrong or expired. Look at the one this computer shows now."));
            return false;
        }
        var request = new EnrollRequest(deviceId, PhoneStore.CleanText(name) is { Length: > 0 } shownName ? shownName : remote.Address.ToString(), kind,
            remote.Address.ToString(), SecureChannel.Verification(socket));
        bool allowed = await AskAsync(request);
        if (!allowed)
        {
            log.Write(LogLevel.Info, $"Ecosystem: \"{request.Name}\" was not allowed in.");
            await SendAsync(socket, sending, Error("The person at that computer said no (or did not answer)."));
            return false;
        }
        ecosystem.Admit(deviceId, request.Name, kind, publicKey, DateTimeOffset.UtcNow);
        log.Write(LogLevel.Info, $"Ecosystem: \"{request.Name}\" ({kind}) joined.");
        await SendAsync(socket, sending, Message("enrolled", new JsonObject
        {
            ["ecosystem"] = ecosystem.Export(),
            ["computer"] = host.ComputerName,
            ["deviceId"] = ecosystem.DeviceId,
            ["answer"] = Ecosystem.EnrollAnswer(code, nonce, deviceId, ecosystem.DeviceId, ecosystem.Id!, SecureChannel.Binding(socket))
        }));
        return true;
    }

    /// <summary>Asks the person here (up to a minute); no answer counts as no.</summary>
    private async Task<bool> AskAsync(EnrollRequest request)
    {
        if (Enrolling is null) return false;
        try
        {
            var asking = Enrolling(request);
            return await Task.WhenAny(asking, Task.Delay(TimeSpan.FromSeconds(60))) == asking && await asking;
        }
        catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { return false; }
    }

    /// <summary>
    /// "member": the other device sends what it knows of the ecosystem and signs the challenge. This computer learns that, checks the
    /// signature, and answers with its own signature and what it knows. Null (after an error was sent) when it is not a member.
    /// </summary>
    private async Task<PairedPhone?> MemberSignInAsync(WebSocket socket, SemaphoreSlim sending, JsonElement message, string nonce)
    {
        string Text(string name) => message.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        if (ecosystem is null || ecosystem.Id is null)
        {
            await SendAsync(socket, sending, Error("This computer has no ecosystem.", "notMember"));
            return null;
        }
        string deviceId = Text("deviceId"), theirNonce = Text("nonce"), signature = Text("signature");
        JsonNode? members = null, revoked = null;
        try
        {
            if (message.TryGetProperty("members", out var m)) members = JsonNode.Parse(m.GetRawText());
            if (message.TryGetProperty("revoked", out var r)) revoked = JsonNode.Parse(r.GetRawText());
        }
        catch (JsonException) { }
        ecosystem.Merge(members, revoked);
        if (theirNonce.Length is < 16 or > 128 || !ecosystem.Verify(deviceId, $"member:{nonce}:{theirNonce}:{deviceId}" + Tail(socket), signature) || ecosystem.Find(deviceId) is not { } cert)
        {
            await SendAsync(socket, sending, Error("This device is not in the ecosystem (or was removed).", "notMember"));
            return null;
        }
        var export = ecosystem.Export();
        await SendAsync(socket, sending, Message("memberWelcome", new JsonObject
        {
            ["deviceId"] = ecosystem.DeviceId,
            ["signature"] = ecosystem.SignText($"server:{theirNonce}:{nonce}:{ecosystem.DeviceId}" + Tail(socket)),
            ["members"] = export["members"]!.DeepClone(),
            ["revoked"] = export["revoked"]!.DeepClone(),
            ["computer"] = host.ComputerName
        }));
        return new PairedPhone(MemberPrefix + cert.DeviceId, cert.Name, "", DateTimeOffset.Now, cert.IsComputer ? "computer" : "phone", cert.IsComputer ? cert.DeviceId : null);
    }

    /// <summary>":binding" of this connection (nothing for the plain page): added to the text every signature covers.</summary>
    private static string Tail(WebSocket socket) => SecureChannel.Of(socket) is { } session ? ":" + session.Binding : "";

    private Client Join(Guid id, WebSocket socket, SemaphoreSlim sending, PairedPhone phone)
    {
        var client = new Client(socket, phone, sending);
        bool isNew = !clients.ContainsKey(id);
        clients[id] = client;
        if (isNew) ClientsChanged?.Invoke();
        return client;
    }

    /// <summary>A phone removed in Settings is disconnected at once.</summary>
    private void DropRemovedPhones()
    {
        foreach (var (id, client) in clients)
            if (!StillAllowed(client.Phone) && clients.TryRemove(id, out _))
            {
                _ = CloseAsync(client, "This phone was removed in SwitchMonitor.");
                ClientsChanged?.Invoke();
            }
    }

    private static async Task CloseAsync(Client client, string reason)
    {
        try
        {
            await SendAsync(client.Socket, client.Sending, Error(reason, "unpaired"));
            await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException) { }
    }

    // ---- Discovery: "SWITCHMONITOR?" on UDP 47821 gets the computer's name and port ----

    private async Task DiscoveryLoop(UdpClient udp, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try { received = await udp.ReceiveAsync(cancel); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            if (!LocalNetwork.IsAllowed(received.RemoteEndPoint.Address)) continue;
            if (Encoding.ASCII.GetString(received.Buffer).Trim() != DiscoveryQuestion) continue;
            // One answer per address every quarter of a second: this cannot be used to flood somebody else.
            var asker = received.RemoteEndPoint.Address;
            long nowTicks = Environment.TickCount64;
            if (lastDiscovery.TryGetValue(asker, out long before) && nowTicks - before < 250) continue;
            if (lastDiscovery.Count > 512) lastDiscovery.Clear();
            lastDiscovery[asker] = nowTicks;
            byte[] answer = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["app"] = "SwitchMonitor", ["computer"] = host.ComputerName, ["port"] = Port,
                ["deviceId"] = ecosystem?.DeviceId ?? DeviceIdentity.Id, ["protocol"] = Protocol, ["ecosystem"] = ecosystem?.Id
            }.ToJsonString());
            try { await udp.SendAsync(answer, received.RemoteEndPoint, cancel); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
        }
    }

    // ---- HTTP and WebSocket plumbing ----

    /// <summary>Sec-WebSocket-Accept for a client key (RFC 6455).</summary>
    public static string AcceptKey(string key) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers)?> ReadRequestAsync(Stream stream, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        int length = 0;
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length), cancel);
            if (read == 0) return null;
            length += read;
            int end = IndexOf(buffer, length, "\r\n\r\n"u8);
            if (end < 0) continue;
            string head = Encoding.ASCII.GetString(buffer, 0, end);
            var lines = head.Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            string path = first[1].Split('?')[0];
            return (first[0], path, headers);
        }
        return null;
    }

    /// <summary>The Origin of a page is the same site as the address it was fetched from (Host).</summary>
    private static bool SameOrigin(string origin, string? host) =>
        host is not null && Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase);

    private static int IndexOf(byte[] buffer, int length, ReadOnlySpan<byte> pattern) =>
        buffer.AsSpan(0, length).IndexOf(pattern);

    private static async Task RespondAsync(Stream stream, string status, string type, string body)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {content.Length}\r\n" +
            "Cache-Control: no-store\r\nX-Frame-Options: DENY\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: frame-ancestors 'none'\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head);
        await stream.WriteAsync(content);
        await stream.FlushAsync();
    }

    private static async Task SendAsync(WebSocket socket, SemaphoreSlim sending, string json)
    {
        await sending.WaitAsync();
        try
        {
            if (socket.State == WebSocketState.Open) await SecureChannel.SendAsync(socket, json);
        }
        catch (Exception error) when (error is WebSocketException or IOException or ObjectDisposedException) { }
        finally { sending.Release(); }
    }

    private static string Message(string type, JsonObject body)
    {
        body["type"] = type;
        return body.ToJsonString();
    }

    private static string Error(string text, string? code = null)
    {
        var body = new JsonObject { ["type"] = "error", ["error"] = text };
        if (code is not null) body["code"] = code;
        return body.ToJsonString();
    }
}
