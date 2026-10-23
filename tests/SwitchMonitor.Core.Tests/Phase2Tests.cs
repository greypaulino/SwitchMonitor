using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class FeatureTests
{
    private const string Caps = "(prot(monitor)vcp(10 12 14(05 08 0B) 16 18 1A 60(11 0F) 62 8D D6(01 04) DC(00 03 05)))";

    private static (MonitorDevice Monitor, FakeTransport Fake) Device(string caps = Caps)
    {
        var fake = new FakeTransport { Capabilities = caps };
        foreach (byte code in new byte[] { 0x10, 0x12, 0x16, 0x18, 0x1A, 0x62 }) fake.Values[code] = new VcpValue(50, 100);
        fake.Values[0x14] = new VcpValue(0x05, 0x0B);
        fake.Values[0x8D] = new VcpValue(2, 2);
        fake.Values[0xDC] = new VcpValue(0x00, 0x05);
        fake.Values[0x60] = new VcpValue(0x11, 0);
        return (new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay()), fake);
    }

    [Test]
    public static void FeaturesFollowCatalogOrderAndExcludeInput()
    {
        var (monitor, _) = Device();
        using var _m = monitor;
        Assert.Sequence(["Brightness", "Contrast", "Volume", "Mute", "Color preset", "Picture mode", "Red", "Green", "Blue", "Power"],
            monitor.Features.Select(f => f.Name));
    }

    [Test]
    public static void ChoiceFeaturesCarryAdvertisedValues()
    {
        var (monitor, _) = Device();
        using var _m = monitor;
        var preset = monitor.Features.Single(f => f.Code == VcpCode.ColorPreset);
        Assert.Sequence([0x05u, 0x08u, 0x0Bu], preset.AllowedValues);
        Assert.Sequence(["6500 K", "9300 K", "User 1"], preset.AllowedValues.Select(preset.Definition.DescribeValue));
        var mute = monitor.Features.Single(f => f.Code == VcpCode.AudioMute);
        Assert.Sequence([1u, 2u], mute.AllowedValues, "toggle defaults when caps list no values");
    }

    [Test]
    public static void SetChoiceWritesOnlyAdvertisedValues()
    {
        var (monitor, fake) = Device();
        using var _m = monitor;
        Assert.Equal(WriteOutcome.Verified, monitor.SetChoice(VcpCode.ColorPreset, 0x08));
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.SetChoice(VcpCode.ColorPreset, 0x06));
        Assert.Throws<ArgumentException>(() => monitor.SetChoice(VcpCode.Brightness, 1));
        Assert.Throws<ArgumentException>(() => monitor.SetChoice(VcpCode.InputSource, 0x0F));
        Assert.Throws<ArgumentException>(() => monitor.SetPercent(VcpCode.ColorPreset, 50));
        Assert.Sequence([((byte)0x14, 0x08u)], fake.Writes);
    }

    [Test]
    public static void UnsupportedFeatureIsRefused()
    {
        var (monitor, fake) = Device("(vcp(10 12))");
        using var _m = monitor;
        Assert.Throws<NotSupportedException>(() => monitor.SetChoice(VcpCode.ColorPreset, 0x05));
        Assert.Equal(0, fake.Writes.Count);
    }

    [Test]
    public static void CatalogFindsSceneNames()
    {
        Assert.Equal(VcpCode.ColorPreset, FeatureCatalog.FindByName("color preset")?.Code);
        Assert.Equal(VcpCode.ColorPreset, FeatureCatalog.FindByName("Color-Temperature")?.Code);
        Assert.Equal(VcpCode.DisplayMode, FeatureCatalog.FindByName("picture_mode")?.Code);
        Assert.Equal(VcpCode.RedGain, FeatureCatalog.FindByName("red")?.Code);
        Assert.Equal<FeatureDefinition?>(null, FeatureCatalog.FindByName("sharpness"));
    }
}

internal static class CapabilitiesOverDdcTests
{
    private static byte[] Fragment(ushort offset, string data)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(data);
        var reply = new List<byte> { 0x6E, (byte)(0x80 | (bytes.Length + 3)), 0xE3, (byte)(offset >> 8), (byte)offset };
        reply.AddRange(bytes);
        byte checksum = 0x50;
        foreach (byte b in reply) checksum ^= b;
        reply.Add(checksum);
        return reply.ToArray();
    }

    [Test]
    public static void BuildsCapabilitiesRequest()
    {
        // Standard request at offset 0: 6E 51 83 F3 00 00 checksum.
        var packet = DdcPacket.Build(DdcPacket.HostReadSource, DdcPacket.CapabilitiesRequestPayload(0));
        Assert.Equal("6E5183F30000", Convert.ToHexString(packet.AsSpan(0, 6)));
        byte x = 0;
        foreach (byte b in packet) x ^= b;
        Assert.Equal((byte)0, x);
    }

    [Test]
    public static void AssemblesFragmentsUntilEmptyReply()
    {
        const string full = "(prot(monitor)type(LCD)model(29WK600)vcp(10 12 60(11 12 0F)))";
        var requested = new List<ushort>();
        string text = DdcPacket.ReadCapabilities(offset =>
        {
            requested.Add(offset);
            string chunk = offset >= full.Length ? "" : full.Substring(offset, Math.Min(32, full.Length - offset));
            return Fragment(offset, chunk);
        }, "fake");
        Assert.Equal(full, text);
        Assert.Sequence(new ushort[] { 0, 32, (ushort)full.Length }, requested);
    }

    [Test]
    public static void RetriesBadFragmentThenFails()
    {
        int calls = 0;
        string text = DdcPacket.ReadCapabilities(offset =>
        {
            calls++;
            if (calls == 1) { var bad = Fragment(offset, "(vcp(10))"); bad[^1] ^= 1; return bad; }
            return Fragment(offset, offset == 0 ? "(vcp(10))" : "");
        }, "fake");
        Assert.Equal("(vcp(10))", text);

        Assert.Throws<TransportException>(() => DdcPacket.ReadCapabilities(_ => [0x6E, 0x80], "fake"));
    }

    [Test]
    public static void RejectsWrongOffset()
    {
        Assert.True(!DdcPacket.TryParseCapabilitiesReply(Fragment(32, "abc"), 0, out _), "offset mismatch");
        Assert.True(DdcPacket.TryParseCapabilitiesReply(Fragment(32, "abc"), 32, out var data) && data.Length == 3, "match");
    }

    [Test]
    public static void LgWithCapabilitiesExposesMoreControls()
    {
        const string id = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var fake = new FakeTransport { Capabilities = "(vcp(10 12 60(11 12 0F 10) 62))" };
        using var lg = new MonitorDevice(TestMonitors.Identity(id), fake, QuirkDatabase.LoadEmbedded().Match(id), null, new RecordingDelay());
        Assert.Sequence(["Brightness", "Contrast", "Volume"], lg.Features.Select(f => f.Name));
        Assert.Sequence([0x11, 0x12, 0x0F], lg.Inputs, "quirk still filters phantom DisplayPort 2");
    }
}

internal static class SceneTests
{
    [Test]
    public static void DefaultScenesParse()
    {
        var scenes = SceneStore.Parse(SceneStore.DefaultJson);
        Assert.Sequence(["Work", "Night", "Game"], scenes.Select(s => s.Name));
        var night = scenes[1];
        Assert.Equal(15.0, night.Settings[0].Percent);
        Assert.Equal<uint?>(0x05, night.Settings[1].Value, "6500 K");
        Assert.Equal<uint?>(0x05, scenes[2].Settings[1].Value, "Games");
    }

    [Test]
    public static void ParsesTogglesAndHexValues()
    {
        var scene = SceneStore.Parse("""
            { "scenes": [ { "name": "Quiet", "monitor": "LG",
              "settings": { "volume": 10, "mute": true, "color preset": "0x0B" } } ] }
            """)[0];
        Assert.Equal("LG", scene.MonitorFilter);
        Assert.Equal<uint?>(1, scene.Settings[1].Value, "mute on");
        Assert.Equal<uint?>(0x0B, scene.Settings[2].Value);
    }

    [Test]
    public static void RejectsUnsafeOrInvalidScenes()
    {
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "power": "Off (soft)" } } ] }"""));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "input": "HDMI 1" } } ] }"""));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "brightness": 140 } } ] }"""));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "sharpness": 5 } } ] }"""));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "color preset": "purple" } } ] }"""));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": {} } ] }"""));
        Assert.Throws<FormatException>(() => SceneStore.Parse(
            """{ "scenes": [ { "name": "A", "settings": { "brightness": 1 } }, { "name": "a", "settings": { "brightness": 2 } } ] }"""));
    }

    [Test]
    public static void ApplySkipsWhatTheMonitorLacks()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 14(08 0B)))" };
        fake.Values[0x10] = new VcpValue(80, 100);
        fake.Values[0x14] = new VcpValue(0x08, 0x0B);
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        var night = SceneStore.Parse(SceneStore.DefaultJson)[1];       // brightness 15, color preset 6500 K
        var game = SceneStore.Parse(SceneStore.DefaultJson)[2];        // brightness 100, picture mode Games

        var steps = SceneApplier.Apply(night, [monitor]);
        Assert.Sequence([SceneStepStatus.Applied, SceneStepStatus.Skipped], steps.Select(s => s.Status), "6500 K not advertised");
        Assert.Equal(15u, fake.Values[0x10].Current);

        steps = SceneApplier.Apply(game, [monitor]);
        Assert.Sequence([SceneStepStatus.Applied, SceneStepStatus.Skipped], steps.Select(s => s.Status), "no picture mode");
        Assert.Sequence([((byte)0x10, 15u), ((byte)0x10, 100u)], fake.Writes);
    }

    [Test]
    public static void PreviewSendsNothingAndInactiveMonitorsAreIgnored()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 12))" };
        fake.Values[0x10] = new VcpValue(50, 100);
        var off = new MonitorIdentity("k", "Off panel", "d", "", null, false, true, IsActive: false);
        using var active = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        using var inactive = new MonitorDevice(off, new FakeTransport { Capabilities = "(vcp(10))" }, null, null, new RecordingDelay());
        var work = SceneStore.Parse(SceneStore.DefaultJson)[0];
        var preview = SceneApplier.Preview(work, [active, inactive]);
        Assert.Equal(2, preview.Count, "only the active monitor");
        Assert.Equal(0, fake.Writes.Count, "preview wrote nothing");
    }

    [Test]
    public static void FailedStepDoesNotStopTheScene()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 12))", FailWritesRemaining = 100 };
        fake.Values[0x10] = new VcpValue(50, 100);
        fake.Values[0x12] = new VcpValue(50, 100);
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        var steps = SceneApplier.Apply(SceneStore.Parse(SceneStore.DefaultJson)[0], [monitor]);
        Assert.Sequence([SceneStepStatus.Failed, SceneStepStatus.Failed], steps.Select(s => s.Status));
    }
}

internal static class RoundTripTests
{
    [Test]
    public static void RestoresOriginalValues()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 12 62 D6(01 04)))" };
        fake.Values[0x10] = new VcpValue(50, 100);
        fake.Values[0x12] = new VcpValue(100, 100);   // at maximum: steps down instead
        fake.Values[0x62] = new VcpValue(0, 100);
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        var results = RoundTripTest.Run([monitor], new RecordingDelay());
        Assert.Equal(3, results.Count);
        Assert.True(results.All(r => r.Passed), string.Join("; ", results));
        Assert.Equal(50u, fake.Values[0x10].Current);
        Assert.Equal(100u, fake.Values[0x12].Current);
        Assert.Sequence([((byte)0x10, 51u), ((byte)0x10, 50u), ((byte)0x12, 99u), ((byte)0x12, 100u), ((byte)0x62, 1u), ((byte)0x62, 0u)],
            fake.Writes);
        Assert.True(fake.Writes.All(w => w.Code != VcpCode.PowerMode), "power never touched");
    }

    [Test]
    public static void ReportsIgnoredWrites()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10))", OnWrite = (_, _, _) => { } };
        fake.Values[0x10] = new VcpValue(50, 100);
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        var result = RoundTripTest.Run([monitor], new RecordingDelay()).Single();
        Assert.True(!result.Passed && result.Detail.Contains("not confirmed"), result.ToString());
    }
}

internal static class VendorControlTests
{
    private const string LgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";

    // Trimmed from the real LG 29WK600 reply read on DESKTOP-BPMFO0B.
    private const string LgCaps = "(prot(monitor)type(lcd)WK650cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B ) 16 18 1A 52 " +
        "60( 11 12 0F 10) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(00 01 02 02 03 04) F6(00 01 02) 4D 4E 4F " +
        "15(01 06 09 10 11 13 14 28 29 32  44 48) F7(00 01 02 03) F8(00 01) F9 EF FD(00 01) FE(00 01 02) FF)mccs_ver(2.1)mswhql(1))";

    private static MonitorDevice Lg(FakeTransport fake, QuirkDatabase? db = null) =>
        new(TestMonitors.Identity(LgId), fake, (db ?? QuirkDatabase.LoadEmbedded()).Match(LgId), null, new RecordingDelay());

    [Test]
    public static void LgVendorCodesAppearAfterStandardControls()
    {
        using var lg = Lg(new FakeTransport { Capabilities = LgCaps });
        var names = lg.Features.Select(f => f.Name).ToArray();
        Assert.Sequence(["Brightness", "Contrast", "Volume", "Mute", "Color preset", "Red", "Green", "Blue", "Power",
            "LG picture mode", "VCP 0x4D", "VCP 0x4E", "VCP 0x4F", "Vendor 0xEF", "Vendor 0xF5", "Vendor 0xF6", "Response time",
            "FreeSync", "Black stabilizer", "Vendor 0xFD", "Gamma", "Vendor 0xFF"], names);
        Assert.True(!names.Contains("Vendor 0xF4"), "input-switch code hidden");
        Assert.True(lg.Features.All(f => f.Code is not (0x04 or 0x05 or 0x08 or 0xC0 or 0xDF)), "commands and info hidden");
    }

    [Test]
    public static void PictureModeIsMappedAndWritableButVendorCodesAreReadOnly()
    {
        var fake = new FakeTransport { Capabilities = LgCaps };
        fake.Values[0x15] = new VcpValue(0x11, 0x48);
        fake.Values[0xF6] = new VcpValue(0x00, 0x02);
        using var lg = Lg(fake);
        var mode = lg.Features.Single(f => f.Code == 0x15);
        Assert.Equal(FeatureRisk.Safe, mode.Definition.Risk);
        Assert.Equal(10, mode.AllowedValues.Count, "the 10 real LG picture modes");
        Assert.Equal(WriteOutcome.Verified, lg.SetChoice(0x15, 0x01));
        var error = Assert.Throws<NotSupportedException>(() => lg.SetChoice(0xF6, 0x01));
        Assert.True(error.Message.Contains("read-only"), error.Message);
        Assert.Throws<NotSupportedException>(() => lg.SetPercent(0xEF, 50));   // unmapped continuous vendor code
        Assert.Sequence([((byte)0x15, 0x01u)], fake.Writes);
    }

    [Test]
    public static void UserMappingsOverlayEmbeddedQuirks()
    {
        var user = QuirkDatabase.Parse("""
            { "monitors": [ { "id": "lg-29wk600", "name": "LG 29WK600", "pnpIds": ["GSM7714"],
              "controls": [
                { "vcp": "15", "name": "Picture mode", "values": { "01": "Reader", "11": "Custom", "13": "Gamer 1" } },
                { "vcp": "F6", "name": "Response time", "values": { "00": "Normal", "01": "Fast", "02": "Faster" } } ] } ] }
            """);
        var merged = QuirkDatabase.LoadEmbedded().Merge(user);
        var quirk = merged.Match(LgId)!;
        Assert.Equal((byte?)0xF4, quirk.InputWriteVcp, "embedded input mapping kept");
        Assert.Sequence(new byte[] { 0x15, 0xF6, 0xF7, 0xF8, 0xF9, 0xFE }, quirk.Controls.Select(c => c.Code), "embedded controls kept, user F6 added");

        var fake = new FakeTransport { Capabilities = LgCaps };
        fake.Values[0xF6] = new VcpValue(0x00, 0x02);
        using var lg = Lg(fake, merged);
        var response = lg.Features.Single(f => f.Code == 0xF6);
        Assert.Equal("Response time", response.Name);
        Assert.Equal("Faster", response.Definition.DescribeValue(2));
        Assert.Equal(WriteOutcome.Verified, lg.SetChoice(0xF6, 0x02));
        Assert.Equal("Gamer 1", lg.Features.Single(f => f.Code == 0x15).Definition.DescribeValue(0x13));
    }

    [Test]
    public static void QuirkControlsCannotTargetCommandsInputOrPower()
    {
        foreach (string code in new[] { "04", "60", "D6" })
            Assert.Throws<FormatException>(() => QuirkDatabase.Parse(
                $$"""{ "monitors": [ { "id": "x", "name": "X", "pnpIds": ["ABC1234"], "controls": [ { "vcp": "{{code}}", "name": "Bad" } ] } ] }"""));
    }

    [Test]
    public static void SnapshotDiffFindsTheChangedSettingOnly()
    {
        var fake = new FakeTransport { Capabilities = LgCaps };
        foreach (byte code in new byte[] { 0x10, 0x12, 0x15, 0xC0, 0xAC, 0xF6 }) fake.Values[code] = new VcpValue(1, 100);
        using var lg = Lg(fake);
        var before = VcpScanner.Take(lg);
        Assert.True(!before.Values.ContainsKey(0x04) && !before.Errors.ContainsKey(0x04), "reset command never read");
        Assert.True(before.Errors.ContainsKey(0xF5), "unreadable codes recorded");

        fake.Values[0x15] = new VcpValue(0x13, 100);    // user picked another picture mode on the OSD
        fake.Values[0xC0] = new VcpValue(2, 100);       // usage timer ticked
        fake.Values[0xAC] = new VcpValue(9, 100);       // measured frequency moved
        var after = VcpScanner.Take(lg);
        var changes = VcpScanner.Diff(before, after);
        Assert.Equal(1, changes.Count, string.Join("; ", changes));
        Assert.Equal((byte)0x15, changes[0].Code);
        Assert.Equal<uint?>(0x13, changes[0].After);
        Assert.Equal(0, fake.Writes.Count, "snapshots never write");
    }

    [Test]
    public static void SnapshotJsonRoundTrips()
    {
        var snapshot = new VcpSnapshot("k|0", "LG", DateTimeOffset.Parse("2026-10-23T13:00:00-04:00"),
            new Dictionary<byte, VcpValue> { [0x15] = new(0x13, 0x48), [0x10] = new(50, 100) },
            new Dictionary<byte, string> { [0xF5] = "no reply" });
        var back = VcpScanner.FromJson(VcpScanner.ToJson(snapshot));
        Assert.Equal("k|0", back.MonitorKey);
        Assert.Equal(new VcpValue(0x13, 0x48), back.Values[0x15]);
        Assert.Equal("no reply", back.Errors[0xF5]);
        Assert.Equal(0, VcpScanner.Diff(snapshot, back).Count);
    }
}

internal static class LgPictureModeTests
{
    private const string LgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";

    [Test]
    public static void DecodesDecimalCapabilityValues()
    {
        // LG firmware lists decimal values as hex digits: "15(01 06 ... 48)" means 1, 6 ... 48.
        var db = QuirkDatabase.Parse("""
            { "monitors": [ { "id": "x", "name": "X", "pnpIds": ["ABC1234"],
              "controls": [ { "vcp": "15", "name": "Mode", "kind": "choice", "capabilityValues": "decimal",
                              "values": { "01": "Reader", "27": "HDR Effect" } } ] } ] }
            """);
        const string id = @"MONITOR\ABC1234\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var fake = new FakeTransport { Capabilities = "(vcp(10 15(01 06 28 48)))" };
        fake.Values[0x15] = new VcpValue(1, 255);
        using var monitor = new MonitorDevice(TestMonitors.Identity(id), fake, db.Match(id), null, new RecordingDelay());
        var mode = monitor.Features.Single(f => f.Code == 0x15);
        Assert.Sequence([1u, 6u, 28u, 39u, 48u], mode.AllowedValues, "decoded + named HDR Effect");
        Assert.Equal(WriteOutcome.Verified, monitor.SetChoice(0x15, 48));
        // The hex reading of "48" (0x48 = 72) is not a real mode.
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.SetChoice(0x15, 0x48));
    }

    [Test]
    public static void LgOffersOnlyItsTenRealPictureModes()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 15(01 06 09 10 11 13 14 28 29 32  44 48)))" };
        fake.Values[0x15] = new VcpValue(11, 255);
        using var lg = new MonitorDevice(TestMonitors.Identity(LgId), fake, QuirkDatabase.LoadEmbedded().Match(LgId), null, new RecordingDelay());
        var mode = lg.Features.Single(f => f.Code == 0x15);
        Assert.Sequence(["Reader", "Color Weakness", "Custom", "RTS Game", "Custom (Game)", "FPS Game 1", "FPS Game 2",
            "Photo", "HDR Effect", "Cinema"], mode.AllowedValues.Select(mode.Definition.DescribeValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => lg.SetChoice(0x15, 9));
        Assert.Equal(WriteOutcome.Verified, lg.SetChoice(0x15, 39));
    }

    [Test]
    public static void DecimalDigitsRejectsHexOnlyCodes()
    {
        Assert.Equal<uint?>(48, MonitorDevice.DecimalDigits(0x48));
        Assert.Equal<uint?>(1, MonitorDevice.DecimalDigits(0x01));
        Assert.Equal<uint?>(null, MonitorDevice.DecimalDigits(0x2A));
    }
}

internal static class BlackStabilizerTests
{
    [Test]
    public static void QuirkMaximumOverridesReportedMaximum()
    {
        const string id = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var fake = new FakeTransport { Capabilities = "(vcp(10 F9))" };
        fake.Values[0xF9] = new VcpValue(55, 255);     // the LG reports 255; the OSD slider is 0-100
        using var lg = new MonitorDevice(TestMonitors.Identity(id), fake, QuirkDatabase.LoadEmbedded().Match(id), null, new RecordingDelay());
        var feature = lg.Features.Single(f => f.Code == 0xF9);
        Assert.Equal("Black stabilizer", feature.Name);
        Assert.Equal<uint?>(100, feature.Definition.Maximum);
        Assert.Equal(FeatureRisk.ReadOnly, feature.Definition.Risk, "the LG ignores DDC writes to it");
        var error = Assert.Throws<NotSupportedException>(() => lg.SetPercent(0xF9, 60));
        Assert.True(error.Message.Contains("ignores DDC writes"), error.Message);
        Assert.Equal(0, fake.Writes.Count);
    }

    [Test]
    public static void MaximumOnlyForContinuousControls()
    {
        Assert.Throws<FormatException>(() => QuirkDatabase.Parse(
            """{ "monitors": [ { "id": "x", "name": "X", "pnpIds": ["ABC1234"], "controls": [ { "vcp": "F7", "name": "Bad", "kind": "choice", "maximum": 3 } ] } ] }"""));
    }
}

internal static class GammaTests
{
    [Test]
    public static void IgnoresWrongAdvertisedValues()
    {
        const string id = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var fake = new FakeTransport { Capabilities = "(vcp(10 F8(00 01) FE(00 01 02)))" };
        fake.Values[0xFE] = new VcpValue(3, 255);
        fake.Values[0xF8] = new VcpValue(1, 255);
        using var lg = new MonitorDevice(TestMonitors.Identity(id), fake, QuirkDatabase.LoadEmbedded().Match(id), null, new RecordingDelay());
        var gamma = lg.Features.Single(f => f.Code == 0xFE);
        Assert.Sequence([2u, 3u, 4u, 7u], gamma.AllowedValues, "advertised 00 and 01 are not real gamma modes");
        Assert.Equal("Mode 4", gamma.Definition.DescribeValue(7));
        Assert.Throws<NotSupportedException>(() => lg.SetChoice(0xFE, 7));   // read-only on the LG
        Assert.Equal(WriteOutcome.Verified, lg.SetChoice(0xF8, 0));        // FreeSync is writable
        Assert.Sequence([0u, 1u], lg.Features.Single(f => f.Code == 0xF8).AllowedValues, "FreeSync");
    }

    [Test]
    public static void IgnoreNeedsNamedValues()
    {
        Assert.Throws<FormatException>(() => QuirkDatabase.Parse(
            """{ "monitors": [ { "id": "x", "name": "X", "pnpIds": ["ABC1234"], "controls": [ { "vcp": "FE", "name": "G", "kind": "choice", "capabilityValues": "ignore" } ] } ] }"""));
    }
}
