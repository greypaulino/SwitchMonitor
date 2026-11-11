using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.Core.Tests;

/// <summary>One computer controls another through the phone protocol: pair with the code, mirror the state, send commands.</summary>
internal static class PeerTests
{
    private sealed class Host : IRemoteHost
    {
        public List<string> Commands { get; } = [];
        public string ComputerName => "Desk PC";
        public string Page => "<html></html>";
        public Task<JsonObject> StateAsync() => Task.FromResult(new JsonObject
        {
            ["computer"] = "Desk PC",
            ["displays"] = new JsonArray(new JsonObject { ["id"] = "lg|0", ["name"] = "LG 29WK600", ["brightness"] = 40 })
        });
        public Task<string?> CommandAsync(string type, JsonElement message)
        {
            Commands.Add(type + ":" + (message.TryGetProperty("value", out var v) ? v.GetInt32() : -1));
            return Task.FromResult<string?>(null);
        }
    }

    private static string Temp(string name) => Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}.json");

    private static async Task<bool> UntilAsync(Func<bool> condition, int seconds = 8)
    {
        for (int i = 0; i < seconds * 20 && !condition(); i++) await Task.Delay(50);
        return condition();
    }

    [Test]
    public static void PeersAreSealedAndKeptAcrossRestarts()
    {
        string path = Temp("peers");
        try
        {
            var store = new PeerStore(path);
            var computer = store.Add("Desk PC", "192.168.1.20", 47820, "secret-token", DateTimeOffset.Now);
            Assert.True(!File.ReadAllText(path).Contains("secret-token"), "the token is not written in clear");
            var again = new PeerStore(path);
            Assert.Equal("Desk PC", again.Computers.Single().Name);
            Assert.Equal("secret-token", System.Text.Encoding.UTF8.GetString(again.TokenOf(computer.Id)!));
            Assert.Equal(1, new PeerStore(path).Add("Desk PC again", "192.168.1.20", 47820, "other", DateTimeOffset.Now) is not null ? 1 : 0);
            Assert.Equal(1, again.Computers.Count, "same host and port is the same computer");
            Assert.True(again.Remove(computer.Id));
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void AComputerPairsMirrorsTheStateAndSendsCommands() => Run().GetAwaiter().GetResult();

    private static async Task Run()
    {
        string phones = Temp("phones"), peers = Temp("peers");
        var host = new Host();
        var phoneStore = new PhoneStore(phones);
        using var server = new RemoteServer(phoneStore, host, port: 0);
        server.Start(withDiscovery: false);
        try
        {
            var bad = await PeerClient.PairAsync("127.0.0.1", server.Port, "000000", "Laptop");
            Assert.True(bad.Token is null && bad.Error is not null, "no code was shown: refused");

            string code = phoneStore.NewCode(DateTimeOffset.Now);
            var paired = await PeerClient.PairAsync("127.0.0.1", server.Port, code, "Laptop");
            Assert.True(paired.Token is not null, "the code pairs: " + paired.Error);
            Assert.Equal("Desk PC", paired.ComputerName);

            var peerStore = new PeerStore(peers);
            var computer = peerStore.Add(paired.ComputerName!, "127.0.0.1", server.Port, paired.Token!, DateTimeOffset.Now);
            using var client = new PeerClient(computer, peerStore.TokenOf(computer.Id)!);
            client.Start();
            Assert.True(await UntilAsync(() => client.Connected && client.State is not null), "signed in and got the state");
            Assert.Equal("LG 29WK600", (string?)client.State!["displays"]![0]!["name"]);

            Assert.True(await client.SendAsync(new JsonObject { ["type"] = "brightness", ["display"] = "lg|0", ["value"] = 55 }));
            Assert.True(await UntilAsync(() => host.Commands.Contains("brightness:55")), "the other computer received the command");

            // Removed on the other computer: it is told so and this side stops trying.
            phoneStore.Remove(phoneStore.Phones.Single().Id);
            Assert.True(await UntilAsync(() => client.Unpaired), "removed there: not paired any more");
        }
        finally
        {
            server.Stop();
            File.Delete(phones);
            File.Delete(peers);
        }
    }

    [Test]
    public static void OnePairingWorksBothWaysAndCanBeForgotten() => BothWays().GetAwaiter().GetResult();

    private static async Task BothWays()
    {
        string phonesA = Temp("phonesA"), phonesB = Temp("phonesB"), peersA = Temp("peersA"), peersB = Temp("peersB");
        var storeA = new PhoneStore(phonesA);
        var storeB = new PhoneStore(phonesB);
        var hostA = new Host();
        var hostB = new Host();
        int discoveryPort;
        using (var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            discoveryPort = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var serverA = new RemoteServer(storeA, hostA, port: 0, discoveryPort: 0);
        using var serverB = new RemoteServer(storeB, hostB, port: 0, discoveryPort: discoveryPort);
        serverA.Start(withDiscovery: false);
        serverB.Start(withDiscovery: true);
        var peerStoreA = new PeerStore(peersA);
        var peerStoreB = new PeerStore(peersB);
        CounterPairing? counter = null;
        string? forgot = null;
        serverB.ComputerPaired += c => counter = c;
        serverB.ComputerForgot += d => forgot = d;
        try
        {
            // Discovery: B answers the question with its device id and port.
            var found = await PeerDiscovery.FindAsync(TimeSpan.FromMilliseconds(700),
                [new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, discoveryPort)], includeSelf: true);
            Assert.Equal(1, found.Count, "B is found");
            Assert.Equal(serverB.Port, found[0].Port);
            Assert.Equal(RemoteServer.Protocol, found[0].Protocol);

            // A asks; B makes the code itself and shows it (no "pair a phone" button on B).
            string? shownCode = null;
            serverB.PairRequested += (who, shown, until) => shownCode = who + ":" + shown;
            Assert.Equal<string?>(null, await PeerClient.RequestPairAsync("127.0.0.1", serverB.Port, "Laptop"), "B accepted the request");
            Assert.True(shownCode is { } text && text.StartsWith("Laptop:") && text.Length == "Laptop:".Length + 6, "B shows a 6-digit code for Laptop");
            Assert.True(await PeerClient.RequestPairAsync("127.0.0.1", serverB.Port, "Laptop") is not null, "a second request right away is refused");

            // A pairs with B using that code; B is handed a token to control A back.
            string code = shownCode!.Split(':')[1];
            string counterToken = PhoneStore.NewToken();
            storeA.AddPaired("Desk PC", counterToken, "computer", found[0].DeviceId, DateTimeOffset.Now);
            var paired = await PeerClient.PairAsync("127.0.0.1", serverB.Port, code, "Laptop", counterToken, serverA.Port);
            Assert.True(paired.Token is not null, "paired: " + paired.Error);
            Assert.True(await UntilAsync(() => counter is not null), "B learned how to reach A");
            Assert.Equal(serverA.Port, counter!.Port);
            Assert.Equal(DeviceIdentity.Id, counter.DeviceId);
            Assert.True(storeB.Phones.Single().IsComputer, "B lists A as a computer, not a phone");

            var computerB = peerStoreA.Add("Desk PC", "127.0.0.1", serverB.Port, paired.Token!, DateTimeOffset.Now, paired.DeviceId);
            var computerA = peerStoreB.Add(counter.Name, "127.0.0.1", counter.Port, counter.Token, DateTimeOffset.Now, counter.DeviceId);
            using var aToB = new PeerClient(computerB, peerStoreA.TokenOf(computerB.Id)!);
            using var bToA = new PeerClient(computerA, peerStoreB.TokenOf(computerA.Id)!);
            aToB.Start();
            bToA.Start();
            Assert.True(await UntilAsync(() => aToB.Connected && aToB.State is not null), "A controls B");
            Assert.True(await UntilAsync(() => bToA.Connected && bToA.State is not null), "and B controls A, with no second code");

            // A removes B: B is told, and can no longer control A.
            Assert.True(await aToB.ForgetAsync());
            Assert.True(await UntilAsync(() => forgot is not null), "B was told to forget A");
            Assert.Equal(0, storeB.Phones.Count, "B no longer lists A");
        }
        finally
        {
            serverA.Stop();
            serverB.Stop();
            foreach (string file in new[] { phonesA, phonesB, peersA, peersB }) File.Delete(file);
        }
    }

    private sealed class OwnerHost(MonitorDevice device) : IRemoteHost
    {
        public string ComputerName => "Desk PC";
        public string Page => "<html></html>";
        public Task<JsonObject> StateAsync() => Task.FromResult(new JsonObject { ["devices"] = DeviceHost.Inventory([device], d => "LG 29WK600") });
        public Task<string?> CommandAsync(string type, JsonElement message) => Task.FromResult<string?>("No commands here.");
        public Task<JsonObject> DeviceRequestAsync(JsonElement message) =>
            Task.FromResult(DeviceHost.Handle(key => key == device.Key ? device : null, message));
    }

    [Test]
    public static void ARemoteDisplayIsAnOrdinaryDeviceOverTheNetwork() => RemoteDevice().GetAwaiter().GetResult();

    private static async Task RemoteDevice()
    {
        string phones = Temp("phones"), peers = Temp("peers");
        string id = @"MONITOR\GSM7714\{x} 1";
        var fake = new FakeTransport { Id = "intel-cui", Capabilities = "(prot(monitor)type(lcd)model(29WK600)vcp(10 12 60(11 0F)))" };
        fake.Values[0x10] = new Vcp.VcpValue(40, 100);
        fake.Values[0x12] = new Vcp.VcpValue(70, 100);
        fake.Values[0x60] = new Vcp.VcpValue(0x0F, 0);
        fake.OnWrite = (t, code, value) => t.Values[code] = new Vcp.VcpValue(value, t.Values.TryGetValue(code, out var old) ? old.Maximum : 0);
        using var owned = new MonitorDevice(TestMonitors.Identity(id), fake, Quirks.QuirkDatabase.LoadEmbedded().Match(id), null, new RecordingDelay());

        var phoneStore = new PhoneStore(phones);
        using var server = new RemoteServer(phoneStore, new OwnerHost(owned), port: 0);
        server.Start(withDiscovery: false);
        try
        {
            string token = PhoneStore.NewToken();
            phoneStore.AddPaired("Laptop", token, "computer", "laptop-device", DateTimeOffset.Now);
            var peerStore = new PeerStore(peers);
            var computer = peerStore.Add("Desk PC", "127.0.0.1", server.Port, token, DateTimeOffset.Now, "desk-device");
            using var client = new PeerClient(computer, peerStore.TokenOf(computer.Id)!);
            client.Start();
            Assert.True(await UntilAsync(() => client.Connected && client.State is not null), "connected with the inventory");

            var info = RemoteDeviceInfo.FromJson((JsonObject)client.State!["devices"]![0]!)!;
            Assert.Equal("intel-cui", info.TransportId, "the owner's transport is passed on");
            using var remote = RemoteMonitors.Create(info, computer.Id, computer.Name,
                request => client.CallAsync(request, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.True(remote.Identity.IsRemote && remote.Identity.RemoteComputer == "Desk PC");
            Assert.Equal("LG 29WK600", remote.Quirk?.Name, "the same model file applies over there");
            Assert.True(remote.Supports(0x10), "capabilities came over the network");
            Assert.True(remote.Inputs.Count == 3, "and so did the LG's three inputs");

            Assert.Equal<uint>(40, remote.Read(0x10).Current, "a read goes to the owner");
            remote.SetPercent(0x10, 65);
            Assert.True(fake.Writes.Any(w => w.Code == 0x10 && w.Value == 65), "a write reaches the owner's real transport");
            Assert.Equal<uint>(65, remote.Read(0x10).Current);

            // The owner's own transport rules still decide how an input is written (the LG on Intel: vendor F4).
            remote.SetInput(0x11);
            Assert.True(fake.Writes.Any(w => w.Code == 0xF4 && w.Value == 0x90), "input goes by the LG's own command");

            // Latency of a slider drag over the protocol itself (loopback: the cost of the messages, not of the Wi-Fi).
            var times = new List<double>();
            for (int i = 0; i < 60; i++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                remote.SetPercent(0x10, 10 + i);
                times.Add(watch.Elapsed.TotalMilliseconds);
            }
            var reads = new List<double>();
            for (int i = 0; i < 30; i++) { var w = System.Diagnostics.Stopwatch.StartNew(); remote.Read(0x10); reads.Add(w.Elapsed.TotalMilliseconds); }
            reads.Sort();
            Console.WriteLine($"      remote read over loopback: median {reads[reads.Count / 2]:F1} ms; calls per write: {fake.Writes.Count}");
            times.Sort();
            double median = times[times.Count / 2], slowest = times[^1];
            Console.WriteLine($"      remote brightness write over loopback: median {median:F1} ms, slowest {slowest:F1} ms");
            Assert.True(median < 50, $"a remote write takes {median:F0} ms at the median (more than 50 ms of protocol overhead)");
        }
        finally
        {
            server.Stop();
            File.Delete(phones);
            File.Delete(peers);
        }
    }
}
