using System.Text;
using SwitchMonitor.Core.Displays;

namespace SwitchMonitor.Core.Tests;

/// <summary>The monitor's own name and serial number, read from a made-up EDID block.</summary>
internal static class EdidTests
{
    private static byte[] Block(string? name, string? serialText, uint numericSerial)
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        BitConverter.GetBytes(numericSerial).CopyTo(edid, 12);
        edid[16] = 12;      // week
        edid[17] = 33;      // 1990 + 33 = 2023
        edid[54] = 0x01;    // a timing descriptor first, as on a real monitor
        edid[55] = 0x01;
        void Text(int offset, byte tag, string text)
        {
            edid[offset + 3] = tag;
            byte[] bytes = Encoding.ASCII.GetBytes((text + "\n").PadRight(13));
            Array.Copy(bytes, 0, edid, offset + 5, 13);
        }
        if (serialText is not null) Text(72, 0xFF, serialText);
        if (name is not null) Text(90, 0xFC, name);
        return edid;
    }

    [Test]
    public static void TheEdidGivesTheMonitorItsOwnNameAndSerial()
    {
        var info = MonitorEdid.Parse(Block("Odyssey G5", "H4ZR600017", 0))!;
        Assert.Equal("Odyssey G5", info.Name);
        Assert.Equal("H4ZR600017", info.Serial, "the text serial wins");
        Assert.Equal(2023, info.Year);
        Assert.Equal(12, info.Week);
        Assert.Equal("Odyssey G5, serial H4ZR600017, week 12 of 2023", info.Describe());

        var numeric = MonitorEdid.Parse(Block("HDR WFHD", null, 0x0007D1AB))!;
        Assert.Equal("0007D1AB", numeric.Serial, "no text serial: the 4-byte number is used");

        var none = MonitorEdid.Parse(Block("Some Monitor", null, 1))!;
        Assert.True(none.Serial is null, "0 and 1 mean 'not numbered': no serial is made up");
        Assert.True(none.Describe().Contains("no serial number"), "says so");
    }

    [Test]
    public static void ABrokenEdidIsNeverTrusted()
    {
        Assert.True(MonitorEdid.Parse(null) is null);
        Assert.True(MonitorEdid.Parse(new byte[64]) is null, "too short");
        Assert.True(MonitorEdid.Parse(new byte[128]) is null, "no EDID header");
        Assert.True(MonitorEdid.ReadFor("not a device id") is null);
        Assert.True(MonitorEdid.ReadFor(null) is null);
    }
}
