using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Remote;

namespace SwitchMonitor.Core.Tests;

internal static class EcosystemTests
{
    private sealed class QuietHost(string name) : IRemoteHost
    {
        public string ComputerName => name;
        public string Page => "<html></html>";
        public Task<JsonObject> StateAsync() => Task.FromResult(new JsonObject { ["linked"] = false, ["devices"] = new JsonArray() });
        public Task<string?> CommandAsync(string type, JsonElement message) => Task.FromResult<string?>(null);
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), $"eco-{Guid.NewGuid():N}.json");
    private static string NewId() => Guid.NewGuid().ToString("N");

    [Test]
    public static void TheCodeIsTheSameOneAnAuthenticatorAppMakes()
    {
        // RFC 6238 test vectors (SHA-1), last six digits.
        byte[] secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, 59 / 30));
        Assert.Equal("081804", Totp.Code(secret, 1111111109 / 30));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(secret), "the key an authenticator app is given");
    }

    [Test]
    public static void ANewCodeAtOnceReplacesTheOneOnScreen()
    {
        string pa = Temp();
        try
        {
            var a = new Ecosystem(pa, NewId(), "Desk");
            a.Found();
            var now = DateTimeOffset.Now;
            string before = a.Code(now);
            string id = NewId(), key = a.PublicKey;
            string fresh = a.NewCode(now);
            Assert.True(fresh != before, "another code, without waiting for the next 30 seconds");
            Assert.Equal(fresh, a.Code(now), "it is the one on screen now");
            Assert.True(a.SecondsLeft(now) > Ecosystem.CodeSeconds, "it lasts until time reaches its step");
            Assert.True(!a.CheckEnrollProof(Ecosystem.EnrollProof(before, "n", id, key), "n", id, key, now, out _), "the old code stops working");
            Assert.True(a.CheckEnrollProof(Ecosystem.EnrollProof(fresh, "n", id, key), "n", id, key, now, out _), "the new one works");
        }
        finally { File.Delete(pa); }
    }

    [Test]
    public static void ADeviceThatLeavesIsTakenOutByTheOthers()
    {
        string pa = Temp(), pb = Temp(), pc = Temp();
        try
        {
            var a = new Ecosystem(pa, NewId(), "Desk");
            var b = new Ecosystem(pb, NewId(), "Laptop");
            var c = new Ecosystem(pc, NewId(), "Pixel", "phone");
            a.Admit(b.DeviceId, b.Name, b.Kind, b.PublicKey, DateTimeOffset.UtcNow);
            b.Join(a.Export());
            a.Admit(c.DeviceId, c.Name, c.Kind, c.PublicKey, DateTimeOffset.UtcNow);
            var export = a.Export();
            c.Join(export);
            b.Merge(export["members"], export["revoked"]);
            Assert.True(a.IsMember(c.DeviceId) && b.IsMember(c.DeviceId));

            // The phone says it is leaving, signed with its own key: both computers drop it.
            var leaving = c.CreateLeave(DateTimeOffset.UtcNow.AddSeconds(1))!;
            var message = new JsonArray(leaving.ToJson());
            Assert.True(a.Merge(null, message) && !a.IsMember(c.DeviceId), "Desk drops it");
            Assert.True(b.Merge(null, message) && !b.IsMember(c.DeviceId), "Laptop drops it");
            Assert.Equal(2, a.Members.Count);

            // Somebody else cannot make a device leave in its name.
            var forged = leaving with { DeviceId = b.DeviceId, IssuerId = b.DeviceId };
            Assert.True(!a.Merge(null, new JsonArray(forged.ToJson())) && a.IsMember(b.DeviceId), "a leave needs the device's own signature");

            // It can join again later.
            a.Admit(c.DeviceId, c.Name, c.Kind, c.PublicKey, DateTimeOffset.UtcNow.AddSeconds(5));
            Assert.True(a.IsMember(c.DeviceId), "joined again");
        }
        finally { foreach (string p in new[] { pa, pb, pc }) File.Delete(p); }
    }

    [Test]
    public static void ThousandsOfForgedCertificatesAreNotAllChecked()
    {
        string pa = Temp(), pb = Temp();
        try
        {
            var a = new Ecosystem(pa, NewId(), "Desk");
            var b = new Ecosystem(pb, NewId(), "Laptop");
            a.Admit(b.DeviceId, b.Name, b.Kind, b.PublicKey, DateTimeOffset.UtcNow);
            var forged = new JsonArray();
            for (int i = 0; i < 3000; i++)
                forged.Add(new JsonObject { ["deviceId"] = "forged-" + i.ToString("D8"), ["name"] = "x", ["kind"] = "phone", ["key"] = b.PublicKey,
                    ["issuer"] = a.DeviceId, ["at"] = 1700000000000L + i, ["sig"] = Convert.ToBase64String(new byte[64]) });
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Assert.True(!a.Merge(forged, new JsonArray()), "nothing forged is believed");
            Assert.True(timer.ElapsedMilliseconds < 500, $"a message of forged certificates costs little ({timer.ElapsedMilliseconds} ms)");
            Assert.Equal(2, a.Members.Count);
        }
        finally { foreach (string p in new[] { pa, pb }) File.Delete(p); }
    }

    [Test]
    public static void ADeviceJoinsAndTheOthersLearnAboutItThroughAnyMember()
    {
        string pa = Temp(), pb = Temp(), pc = Temp();
        try
        {
            var a = new Ecosystem(pa, NewId(), "Desk");
            var b = new Ecosystem(pb, NewId(), "Laptop");
            var c = new Ecosystem(pc, NewId(), "Pixel", "phone");
            Assert.True(!a.InEcosystem && !a.IsMember(a.DeviceId));

            // Desk vouches for Laptop (founding the ecosystem), Laptop (a member now) vouches for the phone.
            a.Admit(b.DeviceId, b.Name, b.Kind, b.PublicKey, DateTimeOffset.UtcNow);
            Assert.True(a.InEcosystem && a.IsMember(b.DeviceId));
            Assert.True(b.Join(a.Export()), "Laptop takes in what Desk sent");
            Assert.True(b.IsMember(a.DeviceId) && b.IsMember(b.DeviceId));
            b.Admit(c.DeviceId, c.Name, c.Kind, c.PublicKey, DateTimeOffset.UtcNow);
            Assert.True(c.Join(b.Export()), "the phone joins through Laptop, with the founder's key in the chain");
            Assert.True(!a.IsMember(c.DeviceId), "Desk does not know the phone yet");

            // The next time Laptop and Desk talk, the news travels.
            var export = b.Export();
            Assert.True(a.Merge(export["members"], export["revoked"]));
            Assert.True(a.IsMember(c.DeviceId));
            Assert.True(a.Verify(c.DeviceId, "hello", c.SignText("hello")), "its signature is accepted everywhere");
            Assert.True(!a.Verify(c.DeviceId, "hello", b.SignText("hello")), "someone else's is not");

            // A phone cannot vouch for anyone; a made-up certificate is not believed.
            Assert.Throws<InvalidOperationException>(() => c.Admit(NewId(), "x", "phone", c.PublicKey, DateTimeOffset.UtcNow));
            var stranger = new Ecosystem(Temp(), NewId(), "Stranger");
            stranger.Found();
            var other = stranger.Export();
            Assert.True(!a.Merge(other["members"], other["revoked"]), "another ecosystem's members are ignored");
            Assert.True(!a.IsMember(stranger.DeviceId));

            // Everything survives a restart.
            var again = new Ecosystem(pa, a.DeviceId, "Desk");
            Assert.Equal(a.Id, again.Id);
            Assert.Equal(3, again.Members.Count);
            Assert.True(!File.ReadAllText(pa).Contains("PRIVATE"), "no key in clear");
        }
        finally { foreach (string p in new[] { pa, pb, pc }) File.Delete(p); }
    }

    [Test]
    public static void ARemovedDeviceStaysOutUntilItIsApprovedAgain()
    {
        string pa = Temp(), pb = Temp(), pc = Temp();
        try
        {
            var a = new Ecosystem(pa, NewId(), "Desk");
            var b = new Ecosystem(pb, NewId(), "Laptop");
            var c = new Ecosystem(pc, NewId(), "Spare");
            a.Admit(b.DeviceId, b.Name, b.Kind, b.PublicKey, DateTimeOffset.UtcNow);
            b.Join(a.Export());
            a.Admit(c.DeviceId, c.Name, c.Kind, c.PublicKey, DateTimeOffset.UtcNow);
            c.Join(a.Export());
            b.Merge(a.Export()["members"], null);
            Assert.True(b.IsMember(c.DeviceId));

            Assert.True(a.Revoke(c.DeviceId, DateTimeOffset.UtcNow.AddSeconds(1)));
            Assert.True(!a.IsMember(c.DeviceId));
            Assert.True(!a.Revoke(a.DeviceId, DateTimeOffset.UtcNow), "a computer leaves by itself, it is not removed");

            // The removal spreads, and the old certificate cannot bring the device back through a member that never heard of it.
            var export = a.Export();
            Assert.True(b.Merge(export["members"], export["revoked"]));
            Assert.True(!b.IsMember(c.DeviceId));
            var old = c.Export();
            b.Merge(old["members"], old["revoked"]);
            Assert.True(!b.IsMember(c.DeviceId), "revoked stays revoked");

            // A computer approves it again: a newer certificate counts.
            a.Admit(c.DeviceId, c.Name, c.Kind, c.PublicKey, DateTimeOffset.UtcNow.AddSeconds(10));
            Assert.True(a.IsMember(c.DeviceId));
        }
        finally { foreach (string p in new[] { pa, pb, pc }) File.Delete(p); }
    }

    [Test]
    public static void TheCodeWorksOnceAndTooManyWrongOnesLockIt()
    {
        string pa = Temp();
        try
        {
            var a = new Ecosystem(pa, NewId(), "Desk");
            var now = DateTimeOffset.UtcNow;
            string code = a.Code(now);
            string key = new Ecosystem(Temp(), NewId(), "X").PublicKey, id = NewId();
            Assert.True(!a.CheckEnrollProof(Ecosystem.EnrollProof("000000" == code ? "111111" : "000000", "n", id, key), "n", id, key, now, out _), "wrong code");
            Assert.True(a.CheckEnrollProof(Ecosystem.EnrollProof(code, "n", id, key), "n", id, key, now, out string used));
            Assert.Equal(code, used);
            Assert.True(!a.CheckEnrollProof(Ecosystem.EnrollProof(code, "n", id, key), "n", id, key, now, out _), "a code is used once");
            Assert.True(!a.CheckEnrollProof(Ecosystem.EnrollProof(code, "other", id, key), "n", id, key, now.AddSeconds(Ecosystem.CodeSeconds), out _), "bound to the challenge");

            var b = new Ecosystem(Temp(), NewId(), "Desk 2");
            for (int i = 0; i < Ecosystem.MaxCodeTries; i++) b.CheckEnrollProof(Ecosystem.EnrollProof("999999", "n", id, key), "n", id, key, now, out _);
            Assert.True(b.IsLocked(now));
            Assert.True(!b.CheckEnrollProof(Ecosystem.EnrollProof(b.Code(now), "n", id, key), "n", id, key, now, out _), "locked even for the right code");
            Assert.True(!b.IsLocked(now + Ecosystem.LockTime + TimeSpan.FromSeconds(1)));
        }
        finally { File.Delete(pa); }
    }

    [Test]
    public static void ComputersJoinWithTheCodeAndThenSignInAsMembers() => Join().GetAwaiter().GetResult();

    private static async Task Join()
    {
        string pa = Temp(), pb = Temp(), pstore = Temp();
        var desk = new Ecosystem(pa, NewId(), "Desk");
        var laptop = new Ecosystem(pb, NewId(), "Laptop");
        var asked = new List<EnrollRequest>();
        bool answer = false;
        using var server = new RemoteServer(new PhoneStore(pstore), new QuietHost("Desk"), port: 0, ecosystem: desk)
        {
            Enrolling = request => { asked.Add(request); return Task.FromResult(answer); }
        };
        server.Start(withDiscovery: false);
        try
        {
            string wrong = desk.Code(DateTimeOffset.Now) == "000000" ? "111111" : "000000";
            Assert.True(await PeerClient.EnrollAsync("127.0.0.1", server.Port, laptop, wrong) is not null, "wrong code");
            Assert.Equal(0, asked.Count);

            Assert.True(await PeerClient.EnrollAsync("127.0.0.1", server.Port, laptop, desk.Code(DateTimeOffset.Now)) is not null, "the person said no");
            Assert.Equal(1, asked.Count);
            Assert.Equal("Laptop", asked[0].Name);
            Assert.True(!laptop.InEcosystem && !desk.IsMember(laptop.DeviceId), "nobody got in");

            // A new code: the earlier one was used up. Spaces in what was typed do not matter.
            answer = true;
            await Task.Delay(1100);
            var now = DateTimeOffset.Now;
            string code = desk.Code(now);
            string? joined = null;
            // The code of the refused try is used up: wait for the next one (at most 30 s).
            for (int tries = 0; tries < 2; tries++)
            {
                joined = await PeerClient.EnrollAsync("127.0.0.1", server.Port, laptop, code[..3] + " " + code[3..]);
                if (joined is null) break;
                await Task.Delay((Ecosystem.CodeSeconds - (int)(DateTimeOffset.Now.ToUnixTimeSeconds() % Ecosystem.CodeSeconds) + 1) * 1000);
                code = desk.Code(DateTimeOffset.Now);
            }
            Assert.Equal<string?>(null, joined, "joined");
            Assert.True(laptop.InEcosystem && laptop.Id == desk.Id && desk.IsMember(laptop.DeviceId));

            // Now Laptop signs in as a member (no token, no pairing) and gets the state.
            using var peer = new PeerClient(new PairedComputer("eco-" + desk.DeviceId, "Desk", "127.0.0.1", server.Port, DateTimeOffset.Now, desk.DeviceId), laptop);
            peer.Start();
            for (int i = 0; i < 50 && !peer.Connected; i++) await Task.Delay(100);
            Assert.True(peer.Connected, "signed in with its key");
            Assert.Equal(1, server.ConnectedCount);

            // Removed by the desk: told and disconnected.
            desk.Revoke(laptop.DeviceId, DateTimeOffset.UtcNow.AddSeconds(1));
            for (int i = 0; i < 50 && server.ConnectedCount > 0; i++) await Task.Delay(100);
            Assert.Equal(0, server.ConnectedCount);
        }
        finally { foreach (string p in new[] { pa, pb, pstore }) File.Delete(p); }
    }
}
