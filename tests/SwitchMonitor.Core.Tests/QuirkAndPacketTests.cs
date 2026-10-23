using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class QuirkTests
{
    private const string LgDeviceId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0003";

    [Test]
    public static void EmbeddedDatabaseLoads()
    {
        var db = QuirkDatabase.LoadEmbedded();
        Assert.True(db.All.Count >= 1, "at least one quirk");
    }

    [Test]
    public static void MatchesLgByEitherPnpId()
    {
        var db = QuirkDatabase.LoadEmbedded();
        Assert.Equal("lg-29wk600", db.Match(LgDeviceId)?.Id);
        Assert.Equal("lg-29wk600", db.Match(@"MONITOR\GSM7715\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001")?.Id);
        Assert.Equal<MonitorQuirk?>(null, db.Match(@"MONITOR\DEL4321\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001"));
        Assert.Equal<MonitorQuirk?>(null, db.Match(null));
    }

    [Test]
    public static void LgInputsUseVendorCode()
    {
        var lg = QuirkDatabase.LoadEmbedded().Match(LgDeviceId)!;
        Assert.Sequence([0x11, 0x12, 0x0F], lg.Inputs.Select(i => i.Code));
        Assert.Equal(((byte)0xF4, 0x90u), lg.MapInputWrite(0x11), "HDMI 1");
        Assert.Equal(((byte)0xF4, 0x91u), lg.MapInputWrite(0x12), "HDMI 2");
        Assert.Equal(((byte)0xF4, 0xD0u), lg.MapInputWrite(0x0F), "DisplayPort 1");
        Assert.Throws<ArgumentOutOfRangeException>(() => lg.MapInputWrite(0x10));
    }

    [Test]
    public static void ExtractsPnpAndProductCode()
    {
        Assert.Equal("GSM7714", QuirkDatabase.ExtractPnpId(LgDeviceId));
        Assert.Equal("BOE0812", QuirkDatabase.ExtractPnpId(@"DISPLAY\BOE0812\4&2b3b0f1&0&UID8388688_0"));
        Assert.Equal<string?>(null, QuirkDatabase.ExtractPnpId(@"\\.\DISPLAY1"));
        Assert.Equal<int?>(0x7715, QuirkDatabase.ProductCode("GSM7715"));
        Assert.Equal<int?>(null, QuirkDatabase.ProductCode(null));
    }

    [Test]
    public static void RejectsDuplicatePnpIds()
    {
        const string json = """
            { "monitors": [
              { "id": "a", "name": "A", "pnpIds": ["ABC1234"] },
              { "id": "b", "name": "B", "pnpIds": ["abc1234"] } ] }
            """;
        Assert.Throws<FormatException>(() => QuirkDatabase.Parse(json));
    }

    [Test]
    public static void RejectsInputsWithoutWriteMapping()
    {
        const string json = """
            { "monitors": [ { "id": "a", "name": "A", "pnpIds": ["ABC1234"],
              "inputs": [ { "code": "11", "name": "HDMI 1" }, { "code": "12", "name": "HDMI 2" } ],
              "inputWrite": { "vcp": "F4", "values": { "11": "90" } } } ] }
            """;
        Assert.Throws<FormatException>(() => QuirkDatabase.Parse(json));
    }

    [Test]
    public static void StandardMonitorWritesVcp60()
    {
        const string json = """{ "monitors": [ { "id": "a", "name": "A", "pnpIds": ["ABC1234"] } ] }""";
        var quirk = QuirkDatabase.Parse(json).All[0];
        Assert.Equal((VcpCode.InputSource, 0x11u), quirk.MapInputWrite(0x11));
    }
}

internal static class PacketTests
{
    [Test]
    public static void BuildsStandardGetBrightnessPacket()
    {
        // Well-known DDC/CI "Get VCP 0x10" request: 6E 51 82 01 10 AC.
        var packet = DdcPacket.Build(DdcPacket.HostReadSource, DdcPacket.GetVcpPayload(VcpCode.Brightness));
        Assert.Equal("6E51820110AC", Convert.ToHexString(packet));
    }

    [Test]
    public static void BuildsSetPacketWithBigEndianValue()
    {
        var packet = DdcPacket.Build(DdcPacket.HostWriteSource, DdcPacket.SetVcpPayload(0xF4, 0x90));
        Assert.Equal("6E508403F40090", Convert.ToHexString(packet.AsSpan(0, 7)));
        byte checksum = 0;
        foreach (byte b in packet) checksum ^= b;
        Assert.Equal((byte)0, checksum, "XOR over the whole packet");
        Assert.Throws<ArgumentOutOfRangeException>(() => DdcPacket.SetVcpPayload(0x10, 0x1_0000));
    }

    [Test]
    public static void ParsesValidReply()
    {
        byte[] reply = Reply(0x10, current: 52, maximum: 100);
        Assert.True(DdcPacket.TryParseGetVcpReply(reply, 0x10, out var value), "valid reply");
        Assert.Equal(new VcpValue(52, 100), value);
    }

    [Test]
    public static void RejectsBadChecksumOrWrongFeature()
    {
        byte[] reply = Reply(0x10, 52, 100);
        Assert.True(!DdcPacket.TryParseGetVcpReply(reply, 0x60, out _), "wrong feature");
        reply[^1] ^= 0xFF;
        Assert.True(!DdcPacket.TryParseGetVcpReply(reply, 0x10, out _), "bad checksum");
        Assert.True(!DdcPacket.TryParseGetVcpReply(reply.AsSpan(0, 8), 0x10, out _), "short reply");
    }

    private static byte[] Reply(byte feature, ushort current, ushort maximum)
    {
        byte[] reply = [0x6E, 0x88, 0x02, 0x00, feature, 0x00,
            (byte)(maximum >> 8), (byte)maximum, (byte)(current >> 8), (byte)current, 0];
        byte checksum = 0x50;
        foreach (byte b in reply.AsSpan(0, 10)) checksum ^= b;
        reply[10] = checksum;
        return reply;
    }
}
