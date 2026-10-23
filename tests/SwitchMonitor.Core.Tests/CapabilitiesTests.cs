using SwitchMonitor.Core.Capabilities;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class CapabilitiesTests
{
    // Shape of a real LG 29WK600 reply (trimmed).
    private const string Lg =
        "(prot(monitor)type(LCD)model(29WK600)cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B) 16 18 1A 52 " +
        "60( 11 12 0F 10) AC AE B2 B6 C0 C6 C8 C9 D6(01 04) DF 62 8D F4 F5(01 02))mccs_ver(2.1)mswhql(1))";

    [Test]
    public static void ParsesSectionsAndVcpCodes()
    {
        var caps = MccsCapabilitiesParser.Parse(Lg);
        Assert.Equal("29WK600", caps.Model);
        Assert.Equal("LCD", caps.Type);
        Assert.Equal("2.1", caps.MccsVersion);
        Assert.True(caps.Supports(VcpCode.Brightness), "brightness");
        Assert.True(caps.Supports(VcpCode.AudioVolume), "volume");
        Assert.True(caps.Supports(0xF4), "vendor F4");
        Assert.True(!caps.Supports(0xDC), "display mode not advertised");
        Assert.Sequence([0x05, 0x08, 0x0B], caps.ValuesFor(VcpCode.ColorPreset), "color presets");
        Assert.Sequence([0x01, 0x04], caps.ValuesFor(VcpCode.PowerMode), "power modes");
        Assert.Sequence<int>([], caps.ValuesFor(VcpCode.Brightness), "continuous feature has no values");
    }

    [Test]
    public static void OrdersInputsHdmiFirst()
    {
        var caps = MccsCapabilitiesParser.Parse(Lg);
        Assert.Sequence([0x11, 0x12, 0x0F, 0x10], caps.InputSources);
    }

    [Test]
    public static void AcceptsMissingOuterParenthesesAndNulPadding()
    {
        var caps = MccsCapabilitiesParser.Parse("prot(monitor)type(lcd)model(U2415)vcp(10 12 60(0F 11 01))mccs_ver(2.1)\0\0");
        Assert.Equal("U2415", caps.Model);
        Assert.Sequence([0x11, 0x0F, 0x01], caps.InputSources);
    }

    [Test]
    public static void AcceptsUnbalancedTrailingGroup()
    {
        var caps = MccsCapabilitiesParser.Parse("(prot(monitor)model(X)vcp(10 12 60(11 12");
        Assert.True(caps.Supports(0x10) && caps.Supports(0x12), "codes before the break");
        Assert.Sequence([0x11, 0x12], caps.ValuesFor(0x60));
    }

    [Test]
    public static void SplitsRunTogetherHexBytes()
    {
        var caps = MccsCapabilitiesParser.Parse("(vcp(1012 60(0F11) 62))");
        Assert.Sequence(new byte[] { 0x10, 0x12, 0x60, 0x62 }, caps.VcpCodes);
        Assert.Sequence([0x0F, 0x11], caps.ValuesFor(0x60));
    }

    [Test]
    public static void IgnoresMalformedTokensAndDuplicates()
    {
        var caps = MccsCapabilitiesParser.Parse("(vcp(10 123 zz 60(11 11 12) 60(0F)))");
        Assert.Sequence(new byte[] { 0x10, 0x60 }, caps.VcpCodes);
        Assert.Sequence([0x11, 0x12, 0x0F], caps.ValuesFor(0x60), "values merged once");
    }

    [Test]
    public static void EmptyInputGivesEmptyCapabilities()
    {
        Assert.Equal(0, MccsCapabilitiesParser.Parse(null).VcpCodes.Count);
        Assert.Equal(0, MccsCapabilitiesParser.Parse("   ").VcpCodes.Count);
        Assert.Equal<string?>(null, MccsCapabilitiesParser.Parse("garbage").Model);
    }

    [Test]
    public static void VendorSectionsDoNotLeakIntoVcp()
    {
        var caps = MccsCapabilitiesParser.Parse("(prot(monitor)vcp(10)vcpname(10(Brightness))mccs_ver(2.2))");
        Assert.Sequence(new byte[] { 0x10 }, caps.VcpCodes);
        Assert.Equal("2.2", caps.MccsVersion);
    }
}
