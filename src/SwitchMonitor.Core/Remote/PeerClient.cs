using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Diagnostics;

namespace SwitchMonitor.Core.Remote;

/// <summary>
/// This computer as the controller of another one: the same conversation the phone has with <see cref="RemoteServer"/>
/// (challenge, hello with an HMAC proof, state pushes, commands), from C#. The other computer stays the only one that
/// touches its monitors; this client mirrors the state it publishes and sends commands to it.
/// </summary>
public sealed class PeerClient : IDisposable
{
    public static readonly TimeSpan PingEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(15);

    private readonly PairedComputer computer;
    private readonly byte[]? token;
    private readonly Ecosystem? ecosystem;
    private readonly ICoreLog log;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sending = new(1, 1);
    private ClientWebSocket? socket;
    private Task? loop;

    public PeerClient(PairedComputer computer, byte[] token, ICoreLog? log = null)
    {
        this.computer = computer;
        this.token = token;
        this.log = log ?? NullLog.Instance;
    }

    /// <summary>A member of the same ecosystem: signs in with its key, no token and no pairing.</summary>
    public PeerClient(PairedComputer computer, Ecosystem ecosystem, ICoreLog? log = null)
    {
        this.computer = computer;
        this.ecosystem = ecosystem;
        this.log = log ?? NullLog.Instance;
    }

    public PairedComputer Computer => computer;

    /// <summary>Signed in and heard from within <see cref="StaleAfter"/>.</summary>
    public bool Connected { get; private set; }

    /// <summary>The other computer says it no longer knows this one (removed there): do not keep trying.</summary>
    public bool Unpaired { get; private set; }

    /// <summary>The last state the other computer published (same shape as the phone's), or null before the first one.</summary>
    public JsonObject? State { get; private set; }

    /// <summary>Raised (on a worker thread) when the state or the connection changed.</summary>
    public event Action? Changed;

    /// <summary>Raised with the text the other computer answered to a command that failed.</summary>
    public event Action<string>? CommandFailed;

    public void Start() => loop ??= Task.Run(() => RunAsync(stop.Token));

    public void Dispose()
    {
        stop.Cancel();
        try { socket?.Dispose(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Sends one command (for example {"type":"brightness","display":"...","value":40}). False when not connected.</summary>
    public async Task<bool> SendAsync(JsonObject command)
    {
        var current = socket;
        if (!Connected || current is null || current.State != WebSocketState.Open) return false;
        return await SendRawAsync(current, command.ToJsonString());
    }

    // ---- Pairing (once): the code the other computer shows in Settings > Phone ----

    /// <summary>
    /// Pairs with the computer at <paramref name="host"/> using its code; returns its token, name and device id, or an error
    /// text. With <paramref name="counterToken"/> (and this computer's server <paramref name="myPort"/>) the other computer is
    /// also given a token to control this one back, so one pairing works both ways.
    /// </summary>
    public static async Task<(string? Token, string? ComputerName, string? DeviceId, string? Error)> PairAsync(
        string host, int port, string code, string myName, string? counterToken = null, int myPort = 0, CancellationToken cancel = default)
    {
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Bracket(host)}:{port}/ws"), timeout.Token);
            await ReceiveJsonAsync(socket, timeout.Token);   // the challenge
            var pair = new JsonObject { ["type"] = "pair", ["code"] = code.Trim(), ["name"] = myName };
            if (counterToken is not null)
            {
                pair["kind"] = "computer";
                pair["deviceId"] = DeviceIdentity.Id;
                pair["counterToken"] = counterToken;
                pair["port"] = myPort;
            }
            await SendRawAsync(socket, pair.ToJsonString());
            while (true)
            {
                var message = await ReceiveJsonAsync(socket, timeout.Token);
                if (message is null) return (null, null, null, "The other computer closed the connection.");
                string type = (string?)message["type"] ?? "";
                if (type == "error") return (null, null, null, (string?)message["error"] ?? "The code was not accepted.");
                if (type != "paired") continue;
                string? token = (string?)message["token"];
                if (string.IsNullOrEmpty(token)) return (null, null, null, "The other computer sent no token.");
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "paired", CancellationToken.None); } catch (WebSocketException) { }
                return (token, (string?)message["computer"], (string?)message["deviceId"], null);
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or UriFormatException or HttpRequestException)
        {
            return (null, null, null, $"Could not reach {host}:{port} ({error.Message}).");
        }
    }

    /// <summary>
    /// Asks the computer at <paramref name="host"/> to show a pairing code on its screen (it makes one itself): the person types
    /// that code here, and <see cref="PairAsync"/> finishes the pairing. Returns an error text, or null when the code is on its screen.
    /// </summary>
    public static async Task<string?> RequestPairAsync(string host, int port, string myName, CancellationToken cancel = default)
    {
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Bracket(host)}:{port}/ws"), timeout.Token);
            await ReceiveJsonAsync(socket, timeout.Token);   // the challenge
            await SendRawAsync(socket, new JsonObject { ["type"] = "pairRequest", ["name"] = myName }.ToJsonString());
            while (true)
            {
                var message = await ReceiveJsonAsync(socket, timeout.Token);
                if (message is null) return "The other computer closed the connection.";
                string type = (string?)message["type"] ?? "";
                if (type == "error") return (string?)message["error"] ?? "The other computer refused.";
                if (type != "pairRequested") continue;
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "asked", CancellationToken.None); } catch (WebSocketException) { }
                return null;
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or UriFormatException or HttpRequestException)
        {
            return $"Could not reach {host}:{port} ({error.Message}).";
        }
    }

    // ---- Joining an ecosystem: the code the other computer (or a phone's authenticator app) shows ----

    /// <summary>
    /// Joins the ecosystem of the computer at <paramref name="host"/>: proves with its code that the person is in front of it,
    /// waits for the person there to say yes, and takes in the ecosystem it sends. Null when it worked, else an error text.
    /// </summary>
    public static async Task<string?> EnrollAsync(string host, int port, Ecosystem me, string code, CancellationToken cancel = default)
    {
        using var socket = new ClientWebSocket();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));   // the person over there has up to a minute to click
        try
        {
            await socket.ConnectAsync(new Uri($"ws://{Bracket(host)}:{port}/ws"), timeout.Token);
            string nonce = (string?)(await ReceiveJsonAsync(socket, timeout.Token))?["nonce"] ?? "";
            if (nonce.Length == 0) return "The other computer did not answer as expected.";
            string clean = code.Replace(" ", "").Trim();
            await SendRawAsync(socket, new JsonObject
            {
                ["type"] = "enroll", ["deviceId"] = me.DeviceId, ["name"] = me.Name, ["kind"] = me.Kind, ["key"] = me.PublicKey,
                ["proof"] = Ecosystem.EnrollProof(clean, nonce, me.DeviceId, me.PublicKey)
            }.ToJsonString());
            while (true)
            {
                var reply = await ReceiveJsonAsync(socket, timeout.Token);
                if (reply is null) return "The other computer closed the connection.";
                string type = (string?)reply["type"] ?? "";
                if (type == "error") return (string?)reply["error"] ?? "The other computer refused.";
                if (type != "enrolled") continue;
                var shared = reply["ecosystem"] as JsonObject;
                string approver = (string?)reply["deviceId"] ?? "";
                if ((string?)reply["answer"] != Ecosystem.EnrollAnswer(clean, nonce, me.DeviceId, approver, (string?)shared?["id"] ?? ""))
                    return "That computer could not prove it showed the code.";
                return me.Join(shared, replace: true) ? null : "The ecosystem it sent is not valid.";
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or UriFormatException or HttpRequestException)
        {
            return $"Could not reach {host}:{port} ({error.Message}).";
        }
    }

    /// <summary>Tells the other computer to forget this one (this side is being removed). False when not connected.</summary>
    public Task<bool> ForgetAsync() => SendAsync(new JsonObject { ["type"] = "forget" });

    // ---- The connection ----

    private async Task RunAsync(CancellationToken cancel)
    {
        int attempt = 0;
        var random = new Random();
        while (!cancel.IsCancellationRequested && !Unpaired)
        {
            try
            {
                await ConnectOnceAsync(cancel);
                attempt = 0;
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or HttpRequestException or InvalidOperationException)
            {
                if (cancel.IsCancellationRequested) break;
                log.Write(LogLevel.Debug, $"Peer {computer.Name}: {error.Message}");
            }
            SetConnected(false);
            if (Unpaired || cancel.IsCancellationRequested) break;
            // 1 s growing to 30 s, with a little randomness so computers that start together do not knock in step.
            double seconds = Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5))) * (0.8 + random.NextDouble() * 0.4);
            try { await Task.Delay(TimeSpan.FromSeconds(seconds), cancel); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancel)
    {
        using var web = new ClientWebSocket();
        web.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await web.ConnectAsync(new Uri($"ws://{Bracket(computer.Host)}:{computer.Port}/ws"), timeout.Token);
            var challenge = await ReceiveJsonAsync(web, timeout.Token);
            string serverNonce = (string?)challenge?["nonce"] ?? "";
            if (serverNonce.Length == 0) throw new InvalidOperationException("No challenge from the other computer.");
            string myNonce = PhoneStore.NewNonce();
            if (ecosystem is not null)
            {
                // A member: sign the challenge with this device's key and tell what is known of the ecosystem; check the answer the same way.
                var known = ecosystem.Export();
                await SendRawAsync(web, new JsonObject
                {
                    ["type"] = "member", ["deviceId"] = ecosystem.DeviceId, ["nonce"] = myNonce,
                    ["signature"] = ecosystem.SignText($"member:{serverNonce}:{myNonce}:{ecosystem.DeviceId}"),
                    ["members"] = known["members"]!.DeepClone(), ["revoked"] = known["revoked"]!.DeepClone()
                }.ToJsonString());
                while (true)
                {
                    var reply = await ReceiveJsonAsync(web, timeout.Token) ?? throw new InvalidOperationException("Closed while signing in.");
                    string type = (string?)reply["type"] ?? "";
                    if (type == "error") throw new InvalidOperationException((string?)reply["error"] ?? "Refused.");   // not "unpaired": it may just not know us yet
                    if (type != "memberWelcome") continue;
                    ecosystem.Merge(reply["members"], reply["revoked"]);
                    string theirId = (string?)reply["deviceId"] ?? "";
                    if (computer.DeviceId is not null && theirId != computer.DeviceId) throw new InvalidOperationException("Another computer answered.");
                    if (!ecosystem.Verify(theirId, $"server:{myNonce}:{serverNonce}:{theirId}", (string?)reply["signature"] ?? ""))
                        throw new InvalidOperationException("The other computer could not prove it is in the ecosystem.");
                    break;
                }
            }
            else
            {
                string proof = Convert.ToBase64String(PhoneStore.Proof(token!, $"phone:{serverNonce}:{myNonce}"));
                await SendRawAsync(web, new JsonObject { ["type"] = "hello", ["nonce"] = myNonce, ["proof"] = proof }.ToJsonString());
                while (true)
                {
                    var reply = await ReceiveJsonAsync(web, timeout.Token) ?? throw new InvalidOperationException("Closed while signing in.");
                    string type = (string?)reply["type"] ?? "";
                    if (type == "error")
                    {
                        if ((string?)reply["code"] == "unpaired") { Unpaired = true; Changed?.Invoke(); }
                        throw new InvalidOperationException((string?)reply["error"] ?? "Refused.");
                    }
                    if (type != "welcome") continue;
                    string expected = Convert.ToBase64String(PhoneStore.Proof(token!, $"computer:{myNonce}:{serverNonce}"));
                    if ((string?)reply["proof"] != expected) throw new InvalidOperationException("The other computer could not prove who it is.");
                    break;
                }
            }
        }

        socket = web;
        lastHeard = DateTime.UtcNow;
        SetConnected(true);
        using var ping = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var pinger = PingLoopAsync(web, ping.Token);
        try
        {
            while (web.State == WebSocketState.Open && !cancel.IsCancellationRequested)
            {
                var message = await ReceiveJsonAsync(web, cancel);
                if (message is null) break;
                lastHeard = DateTime.UtcNow;
                switch ((string?)message["type"])
                {
                    case "state":
                        message.Remove("type");
                        State = message;
                        Changed?.Invoke();
                        break;
                    case "devResult":
                        if (message["id"]?.GetValue<long>() is long answered && waiting.TryRemove(answered, out var pending))
                        {
                            message.Remove("type");
                            pending.TrySetResult(message);
                        }
                        break;
                    case "error":
                        if ((string?)message["code"] == "unpaired")
                        {
                            if (ecosystem is not null) return;   // removed from the ecosystem: the app drops this client
                            Unpaired = true; Changed?.Invoke(); return;
                        }
                        CommandFailed?.Invoke((string?)message["error"] ?? "The command failed.");
                        break;
                }
            }
        }
        finally
        {
            ping.Cancel();
            FailWaiting();
            try { await pinger; } catch (Exception error) when (error is OperationCanceledException or WebSocketException) { }
            socket = null;
        }
    }

    private DateTime lastHeard = DateTime.UtcNow;
    private long nextRequest;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> waiting = new();

    /// <summary>
    /// Sends a "dev" request (read, write or capabilities of one display) and waits for its answer. Throws
    /// <see cref="TimeoutException"/> when the other computer does not answer in time and <see cref="InvalidOperationException"/>
    /// when the link is down.
    /// </summary>
    public async Task<JsonObject> CallAsync(JsonObject request, TimeSpan timeout)
    {
        long id = Interlocked.Increment(ref nextRequest);
        request["id"] = id;
        var source = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = source;
        try
        {
            if (!await SendAsync(request)) throw new InvalidOperationException("Not connected to " + computer.Name + ".");
            var finished = await Task.WhenAny(source.Task, Task.Delay(timeout));
            if (finished != source.Task) throw new TimeoutException("No answer from " + computer.Name + ".");
            return await source.Task;
        }
        finally { waiting.TryRemove(id, out _); }
    }

    private void FailWaiting()
    {
        foreach (var (id, source) in waiting.ToArray())
            if (waiting.TryRemove(id, out _)) source.TrySetException(new InvalidOperationException("The connection to " + computer.Name + " was lost."));
    }

    /// <summary>Asks for the state every few seconds; nothing heard for <see cref="StaleAfter"/> closes the connection.</summary>
    private async Task PingLoopAsync(ClientWebSocket web, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(PingEvery, cancel);
            if (DateTime.UtcNow - lastHeard > StaleAfter)
            {
                SetConnected(false);
                try { web.Abort(); } catch (ObjectDisposedException) { }
                return;
            }
            await SendRawAsync(web, "{\"type\":\"get\"}");
        }
    }

    private void SetConnected(bool value)
    {
        if (Connected == value) return;
        Connected = value;
        Changed?.Invoke();
    }

    private async Task<bool> SendRawAsync(ClientWebSocket web, string json)
    {
        await sending.WaitAsync();
        try
        {
            if (web.State != WebSocketState.Open) return false;
            await web.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            return true;
        }
        catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException) { return false; }
        finally { sending.Release(); }
    }

    private static async Task SendRawAsync(WebSocket web, string json) =>
        await web.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject?> ReceiveJsonAsync(WebSocket web, CancellationToken cancel)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await web.ReceiveAsync(buffer, cancel);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > 1024 * 1024) return null;
            if (!result.EndOfMessage) continue;
            try { return JsonNode.Parse(message.ToArray()) as JsonObject; }
            catch (JsonException) { return null; }
        }
    }

    private static string Bracket(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;
}
