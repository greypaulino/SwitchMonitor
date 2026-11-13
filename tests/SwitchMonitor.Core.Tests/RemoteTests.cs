using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.Core.Tests;

internal static class RemoteTests
{
    private sealed class FakeHost : IRemoteHost
    {
        public List<string> Commands { get; } = [];
        public List<string> Clips { get; } = [];
        public void ClipboardEvent(JsonElement message, string? deviceId) => Clips.Add(message.GetProperty("text").GetString() ?? "");
        public List<string> Blobs { get; } = [];
        public void BlobEvent(JsonElement message, string? deviceId) => Blobs.Add($"{message.GetProperty("purpose").GetString()}:{message.GetProperty("index").GetInt32()}/{message.GetProperty("total").GetInt32()}");
        public string ComputerName => "Test PC";
        public string Page => "<html>control page</html>";
        public Task<JsonObject> StateAsync() => Task.FromResult(new JsonObject { ["linked"] = false, ["displays"] = new JsonArray() });

        public Task<string?> CommandAsync(string type, JsonElement message)
        {
            Commands.Add(type + ":" + (message.TryGetProperty("value", out var v) ? v.GetInt32() : -1));
            return Task.FromResult(type == "brightness" ? null : "Unknown command.");
        }
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"phones-{Guid.NewGuid():N}.json");

    [Test]
    public static void APhonePairsOnceWithTheCodeAndOnlyItsTokenHashIsKept()
    {
        string path = TempFile();
        try
        {
            var now = DateTimeOffset.Now;
            var store = new PhoneStore(path);
            string code = store.NewCode(now);
            Assert.Equal(6, code.Length);
            Assert.Equal<string?>(null, store.Pair("000000" == code ? "111111" : "000000", "Pixel", now), "wrong code");
            string? token = store.Pair(code, "Pixel", now);
            Assert.True(token is not null);
            Assert.Equal<string?>(null, store.Pair(code, "Other", now), "a code pairs one phone");
            Assert.Equal("Pixel", store.Find(token)!.Name);
            Assert.True(!File.ReadAllText(path).Contains(token!), "the token itself is not saved");
            Assert.Equal("Pixel", new PhoneStore(path).Find(token)!.Name, "kept across restarts");

            string late = store.NewCode(now);
            Assert.Equal<string?>(null, store.Pair(late, "Late", now + PhoneStore.CodeLifetime), "expired");
            string guessed = store.NewCode(now);
            for (int i = 0; i < PhoneStore.MaxCodeTries; i++) store.Pair(guessed == "123456" ? "654321" : "123456", "Guess", now);
            Assert.Equal<string?>(null, store.Pair(guessed, "Guess", now), "too many wrong tries cancel the code");

            Assert.True(store.Remove(store.Find(token)!.Id));
            Assert.Equal<PairedPhone?>(null, store.Find(token));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void OnlyTheHomeNetworkMayConnect()
    {
        foreach (string ok in new[] { "192.168.1.20", "10.0.2.2", "172.20.1.1", "127.0.0.1", "::1", "fe80::1", "::ffff:192.168.0.5" })
            Assert.True(LocalNetwork.IsAllowed(IPAddress.Parse(ok)), ok);
        foreach (string no in new[] { "8.8.8.8", "172.32.0.1", "100.64.0.1", "2001:4860::8888" })
            Assert.True(!LocalNetwork.IsAllowed(IPAddress.Parse(no)), no);
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", RemoteServer.AcceptKey("dGhlIHNhbXBsZSBub25jZQ=="), "RFC 6455 example");
    }

    [Test]
    public static void APairedPhoneGetsTheStateAndSendsCommands() => Run().GetAwaiter().GetResult();

    private static async Task Run()
    {
        string path = TempFile();
        var host = new FakeHost();
        var store = new PhoneStore(path);
        using var server = new RemoteServer(store, host, port: 0) { AllowUnencryptedPage = true };
        server.Start(withDiscovery: false);
        try
        {
            using (var http = new System.Net.Http.HttpClient())
                Assert.True((await http.GetStringAsync($"http://127.0.0.1:{server.Port}/")).Contains("control page"), "the page is served");

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            Assert.Equal("challenge", (string?)(await Open(socket))["type"], "the computer speaks first, and the keys are exchanged");
            await Send(socket, new { type = "brightness", value = 40 });
            Assert.Equal("unpaired", (string?)(await Receive(socket))["code"], "commands need pairing");
            Assert.Equal(0, host.Commands.Count);

            await Send(socket, new { type = "pair", code = store.NewCode(DateTimeOffset.Now), name = "Pixel" });
            var paired = await Receive(socket);
            Assert.Equal("paired", (string?)paired["type"]);
            string token = (string)paired["token"]!;
            Assert.Equal("state", (string?)(await Receive(socket))["type"]);
            Assert.Equal(1, server.ConnectedCount);

            await Send(socket, new { type = "brightness", value = 40 });
            await Send(socket, new { type = "nonsense" });
            Assert.Equal("error", (string?)(await Receive(socket))["type"], "unknown command reported");
            Assert.Sequence(["brightness:40", "nonsense:-1"], host.Commands);

            await server.BroadcastAsync(new JsonObject { ["linked"] = true });
            Assert.Equal(true, (bool?)(await Receive(socket))["linked"], "changes are pushed");
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            // Sending the token itself (version 1) no longer works, nor a made-up proof.
            using (var old = new ClientWebSocket())
            {
                await old.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
                await Open(old);   // challenge and keys
                await Send(old, new { type = "hello", token });
                Assert.Equal("unpaired", (string?)(await Receive(old))["code"], "the token is never accepted as such");
                await Send(old, new { type = "hello", nonce = PhoneStore.NewNonce(), proof = PhoneStore.NewNonce() });
                Assert.Equal("unpaired", (string?)(await Receive(old))["code"], "a wrong proof");
            }

            // Later: the phone answers the challenge with its token (never sent) and checks the computer's answer.
            using var again = new ClientWebSocket();
            await again.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            string challenge = (string)(await Open(again))["nonce"]!;
            string tail = ":" + SecureChannel.Binding(again);
            string mine = PhoneStore.NewNonce();
            byte[] key = Encoding.UTF8.GetBytes(token);
            await Send(again, new { type = "hello", nonce = mine, proof = Convert.ToBase64String(PhoneStore.Proof(key, $"phone:{challenge}:{mine}{tail}")) });
            var welcome = await Receive(again);
            Assert.Equal("welcome", (string?)welcome["type"]);
            Assert.Equal(Convert.ToBase64String(PhoneStore.Proof(key, $"computer:{mine}:{challenge}{tail}")), (string?)welcome["proof"],
                "the computer proves it holds the token too");
            await Receive(again);   // state
            store.Remove(store.Find(token)!.Id);
            Assert.Equal("unpaired", (string?)(await Receive(again))["code"], "removed in Settings: told and disconnected");
        }
        finally
        {
            server.Stop();
            File.Delete(path);
        }
    }

    [Test]
    public static void ADisplayInputCommandIsNotAMouseEvent() => DisplayInput().GetAwaiter().GetResult();

    private static async Task DisplayInput()
    {
        string path = TempFile();
        var host = new FakeHost();
        var store = new PhoneStore(path);
        using var server = new RemoteServer(store, host, port: 0);
        server.Start(withDiscovery: false);
        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            await Open(socket);
            await Send(socket, new { type = "pair", code = store.NewCode(DateTimeOffset.Now), name = "Pixel" });
            await Receive(socket);   // paired
            await Receive(socket);   // state
            // The phone's "switch this display to that input" is a command; a pointer step ("op") is not.
            await Send(socket, new { type = "input", display = "D1", code = 17 });
            await Send(socket, new { type = "input", op = "move", dx = 3, dy = 4 });
            await Send(socket, new { type = "brightness", value = 20 });
            Assert.Equal("error", (string?)(await Receive(socket))["type"], "the input command reached the host (which does not know it)");
            await Send(socket, new { type = "clip", id = "1", text = "copied on the phone" });
            await Task.Delay(300);   // the brightness command and the clip have no answer to wait for
            Assert.Sequence(["copied on the phone"], host.Clips);
            // A piece of a file or image goes to the host, and an answer-less message does not break the connection.
            await Send(socket, new { type = "blob", id = "f1", purpose = "file", name = "a.txt", size = 3, index = 0, total = 1, data = "YWJj" });
            await Task.Delay(300);
            Assert.Sequence(["file:0/1"], host.Blobs);
            Assert.Sequence(["input:-1", "brightness:20"], host.Commands);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void ManyWrongCodesPausePairingForEveryone()
    {
        string path = TempFile();
        try
        {
            var now = DateTimeOffset.Now;
            var store = new PhoneStore(path);
            // Fresh codes keep arriving (a new request each time) and each gets its five wrong tries: the total is what counts.
            for (int round = 0; round < 3; round++)
            {
                string code = store.NewCode(now);
                for (int i = 0; i < PhoneStore.MaxCodeTries; i++) store.Pair(code == "123456" ? "654321" : "123456", "Guess", now);
            }
            Assert.True(store.IsBlocked(now), "15 wrong codes in a few minutes pause pairing");
            string good = store.NewCode(now);
            Assert.Equal<string?>(null, store.Pair(good, "Late", now), "even the right code is refused while paused");
            Assert.True(!store.IsBlocked(now + PhoneStore.FailureWindow + TimeSpan.FromSeconds(1)), "and it ends");
            Assert.Equal("Name with newline", PhoneStore.CleanText("Name with\r\nnewline".Replace("\r\n", " ")), "plain text stays");
            Assert.Equal("ab", PhoneStore.CleanText("a\r\n\tb"), "control characters are removed (no forged log lines)");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void PairingAsksThePersonAndBothSidesShowTheSameNumber() => Approval().GetAwaiter().GetResult();

    private static async Task Approval()
    {
        string path = TempFile();
        var store = new PhoneStore(path);
        EnrollRequest? seen = null;
        bool answer = false;
        using var server = new RemoteServer(store, new FakeHost(), port: 0) { Enrolling = request => { seen = request; return Task.FromResult(answer); } };
        server.Start(withDiscovery: false);
        var uri = new Uri($"ws://127.0.0.1:{server.Port}/ws");
        try
        {
            // The right code, but the person says no: nothing is paired.
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(uri, CancellationToken.None);
                await Open(socket);
                string mine = SecureChannel.Verification(socket);
                Assert.Equal(4, mine.Length, "a 4-digit number");
                await Send(socket, new { type = "pair", code = store.NewCode(DateTimeOffset.Now), name = "Phone\r\nFAKE" });
                Assert.True(((string?)(await Receive(socket))["error"] ?? "").Contains("said no"), "refused by the person");
                Assert.True(seen is { Pairing: true }, "it is a pairing question");
                Assert.Equal(mine, seen!.Verification, "the number the person sees is the one the phone shows");
                Assert.Equal("PhoneFAKE", seen.Name, "no control characters in a name");
                Assert.Equal(0, store.Phones.Count, "nothing was paired");
            }
            // The person says yes: it pairs.
            answer = true;
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(uri, CancellationToken.None);
                await Open(socket);
                await Send(socket, new { type = "pair", code = store.NewCode(DateTimeOffset.Now), name = "Pixel" });
                Assert.Equal("paired", (string?)(await Receive(socket))["type"], "allowed: paired");
                Assert.Equal(1, store.Phones.Count);
            }
            // A wrong code is refused before the person is bothered.
            seen = null;
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(uri, CancellationToken.None);
                await Open(socket);
                string code = store.NewCode(DateTimeOffset.Now);
                await Send(socket, new { type = "pair", code = code == "000000" ? "111111" : "000000", name = "Guess" });
                Assert.Equal("error", (string?)(await Receive(socket))["type"]);
                Assert.True(seen is null, "no question for a wrong code");
            }
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void ABrowserPageFromAnotherSiteCannotOpenTheSocket() => Origin().GetAwaiter().GetResult();

    private static async Task Origin()
    {
        string path = TempFile();
        using var server = new RemoteServer(new PhoneStore(path), new FakeHost(), port: 0);
        server.Start(withDiscovery: false);
        try
        {
            using var foreign = new ClientWebSocket();
            foreign.Options.SetRequestHeader("Origin", "http://evil.example.com");
            bool refused = false;
            try { await foreign.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None); }
            catch (WebSocketException) { refused = true; }
            Assert.True(refused, "a page of another site is refused");

            using var own = new ClientWebSocket();
            own.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{server.Port}");
            await own.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            Assert.Equal("challenge", (string?)(await Receive(own))["type"], "the server's own page is let in");

            using var app = new ClientWebSocket();   // the phone app and the computers send no Origin
            await app.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            Assert.Equal("challenge", (string?)(await Receive(app))["type"], "a client with no Origin is let in");
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void TheChannelIsEncryptedAndRefusesPlainReplayedOrChangedMessages() => Encrypted().GetAwaiter().GetResult();

    private static async Task Encrypted()
    {
        string path = TempFile();
        using var server = new RemoteServer(new PhoneStore(path), new FakeHost(), port: 0);
        server.Start(withDiscovery: false);
        var uri = new Uri($"ws://127.0.0.1:{server.Port}/ws");
        try
        {
            // The challenge carries the server's key; without a key exchange nothing is served.
            using (var plain = new ClientWebSocket())
            {
                await plain.ConnectAsync(uri, CancellationToken.None);
                var challenge = await Receive(plain);
                Assert.True(((string?)challenge["ecdh"] ?? "").Length > 60, "the challenge has the server's key");
                Assert.Equal(SecureChannel.Version, (int)challenge["version"]!, "version 3");
                await Send(plain, new { type = "pair", code = "123456", name = "Plain" });
                Assert.Equal("encrypt", (string?)(await Receive(plain))["code"], "a plain message is refused: encryption is needed");
            }

            // A client that never saw the key in the challenge cannot go on: it does not fall back to plain.
            using (var stripped = new ClientWebSocket())
            {
                await stripped.ConnectAsync(uri, CancellationToken.None);
                var challenge = await Receive(stripped);
                challenge.Remove("ecdh");
                bool refused = false;
                try { await SecureChannel.StartAsync(stripped, challenge); }
                catch (InvalidOperationException) { refused = true; }
                Assert.True(refused, "no key in the challenge: the client stops (no downgrade)");
            }

            // Once the keys are exchanged: what travels is binary and not readable, a frame cannot be replayed or changed,
            // and a plain message is no longer accepted.
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(uri, CancellationToken.None);
                await Open(socket);
                var session = SecureChannel.Of(socket)!;
                byte[] frame = session.Encrypt("{\"type\":\"get\"}");
                Assert.True(!Encoding.UTF8.GetString(frame).Contains("get"), "the message is not readable on the wire");
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
                Assert.Equal("unpaired", (string?)(await Receive(socket))["code"], "an encrypted message is understood");
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);   // the same frame again
                bool closed = false;
                try { await Receive(socket); } catch (Exception error) when (error is InvalidOperationException or WebSocketException) { closed = true; }
                Assert.True(closed, "a replayed frame closes the connection");
            }
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(uri, CancellationToken.None);
                await Open(socket);
                byte[] frame = SecureChannel.Of(socket)!.Encrypt("{\"type\":\"get\"}");
                frame[^3] ^= 1;   // one bit changed
                await socket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
                bool closed = false;
                try { await Receive(socket); } catch (Exception error) when (error is InvalidOperationException or WebSocketException) { closed = true; }
                Assert.True(closed, "a changed frame closes the connection");
            }
            using (var socket = new ClientWebSocket())
            {
                await socket.ConnectAsync(uri, CancellationToken.None);
                await Open(socket);
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"get\"}"), WebSocketMessageType.Text, true, CancellationToken.None);
                bool closed = false;
                try { await Receive(socket); } catch (Exception error) when (error is InvalidOperationException or WebSocketException) { closed = true; }
                Assert.True(closed, "a plain message after the keys closes the connection");
            }

            // The web page is off unless the person turned it on.
            using var http = new System.Net.Http.HttpClient();
            Assert.True(!(await http.GetStringAsync($"http://127.0.0.1:{server.Port}/")).Contains("control page"), "the page is off by default");
        }
        finally { File.Delete(path); }
    }

    private static Task Send(ClientWebSocket socket, object message) => SecureChannel.SendAsync(socket, JsonSerializer.Serialize(message));

    private static async Task<JsonObject> Receive(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        string text = await SecureChannel.ReceiveAsync(socket, 4 * 1024 * 1024, timeout.Token) ?? throw new InvalidOperationException("The connection closed.");
        return (JsonObject)JsonNode.Parse(text)!;
    }

    /// <summary>The challenge, then the key exchange and the server's "ready": from here on every message is encrypted. Returns the challenge.</summary>
    private static async Task<JsonObject> Open(ClientWebSocket socket)
    {
        var challenge = await Receive(socket);
        await SecureChannel.StartAsync(socket, challenge);
        Assert.Equal("ready", (string?)(await Receive(socket))["type"], "the server answers the key exchange");
        return challenge;
    }
}
