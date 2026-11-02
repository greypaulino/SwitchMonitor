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
        using var server = new RemoteServer(store, host, port: 0);
        server.Start(withDiscovery: false);
        try
        {
            using (var http = new System.Net.Http.HttpClient())
                Assert.True((await http.GetStringAsync($"http://127.0.0.1:{server.Port}/")).Contains("control page"), "the page is served");

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            Assert.Equal("challenge", (string?)(await Receive(socket))["type"], "the computer speaks first");
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
                await Receive(old);   // challenge
                await Send(old, new { type = "hello", token });
                Assert.Equal("unpaired", (string?)(await Receive(old))["code"], "the token is never accepted as such");
                await Send(old, new { type = "hello", nonce = PhoneStore.NewNonce(), proof = PhoneStore.NewNonce() });
                Assert.Equal("unpaired", (string?)(await Receive(old))["code"], "a wrong proof");
            }

            // Later: the phone answers the challenge with its token (never sent) and checks the computer's answer.
            using var again = new ClientWebSocket();
            await again.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
            string challenge = (string)(await Receive(again))["nonce"]!;
            string mine = PhoneStore.NewNonce();
            byte[] key = Encoding.UTF8.GetBytes(token);
            await Send(again, new { type = "hello", nonce = mine, proof = Convert.ToBase64String(PhoneStore.Proof(key, $"phone:{challenge}:{mine}")) });
            var welcome = await Receive(again);
            Assert.Equal("welcome", (string?)welcome["type"]);
            Assert.Equal(Convert.ToBase64String(PhoneStore.Proof(key, $"computer:{mine}:{challenge}")), (string?)welcome["proof"],
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

    private static Task Send(ClientWebSocket socket, object message) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> Receive(ClientWebSocket socket)
    {
        var buffer = new byte[16384];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return (JsonObject)JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count))!;
    }
}
