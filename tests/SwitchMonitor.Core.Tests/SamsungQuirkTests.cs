using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

/// <summary>What was learned about the Samsung Odyssey G5 by changing its menu by hand (not advertised by the monitor).</summary>
internal static class SamsungQuirkTests
{
    private const string G5 = @"MONITOR\SAM7488\{4d36e96e-e325-11ce-bfc1-08002be10318}\0008";

    private static MonitorDevice Monitor(FakeTransport fake) =>
        new(TestMonitors.Identity(G5), fake, QuirkDatabase.LoadEmbedded().Match(G5), null, new RecordingDelay());

    private static FakeTransport Fake()
    {
        // The capabilities string the G5 really answers: no 0x2D in it.
        var fake = new FakeTransport { Capabilities = "(prot(monitor)type(lcd)model(LEMON23)cmds(01 02 03 07 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08 0B 0C) 16 18 1A 52 60(11 0F) 62 87 8D CC)mccs_ver(2.0))" };
        fake.Values[0x10] = new VcpValue(8, 50);
        fake.Values[0x12] = new VcpValue(24, 50);
        fake.Values[0x87] = new VcpValue(10, 20);
        fake.Values[0x2D] = new VcpValue(9, 10);
        fake.Values[0x2F] = new VcpValue(5, 10);
        return fake;
    }

    [Test]
    public static void TheG5OffersItsPictureModesEvenThoughItDoesNotAdvertiseThem()
    {
        using var monitor = Monitor(Fake());
        Assert.Equal("Samsung Odyssey G5", monitor.Quirk!.Name);
        var modes = monitor.Features.FirstOrDefault(f => f.Code == 0x2D);
        Assert.True(modes is not null, "0x2D appears although the capabilities leave it out");
        Assert.Equal("Picture mode", modes!.Name);
        Assert.Equal(10, modes.AllowedValues.Count, "the ten modes of the menu");
        Assert.Equal("FPS", modes.Definition.DescribeValue(6));
        Assert.Equal("Custom", modes.Definition.DescribeValue(9));
        Assert.True(monitor.Features.Any(f => f.Code == 0x12), "contrast is still there (the interface orders the list: picture mode first)");
        var response = monitor.Features.FirstOrDefault(f => f.Code == 0xF3);
        Assert.True(response is not null, "response time (0xF3) appears although the capabilities leave it out");
        Assert.Equal(4, response!.AllowedValues.Count, "Standard, Faster, Extreme, Extreme (MBR)");
        Assert.Equal("Extreme (MBR)", response.Definition.DescribeValue(3));
        var sync = monitor.Features.FirstOrDefault(f => f.Code == 0xF2);
        Assert.True(sync is not null, "FreeSync (0xF2) appears although the capabilities leave it out");
        Assert.Equal("FreeSync", sync!.Name, "the same name as the LG's, so the Game profile and the black cover recognise it");
        Assert.True(sync.Definition.OnOffValues(sync.AllowedValues.ToArray()) is { On: 1, Off: 0 }, "a plain on/off switch");
        var lag = monitor.Features.FirstOrDefault(f => f.Code == 0x0C);
        Assert.True(lag is not null, "Low Input Lag (0x0C) appears although the capabilities leave it out");
        Assert.Equal("Low Input Lag", lag!.Name, "named by what the menu does, not by the standard's name for 0x0C");
        Assert.Equal(FeatureRisk.ReadOnly, lag.Definition.Risk, "the monitor ignores DDC writes to it: shown, never written");
        var tone = monitor.Features.FirstOrDefault(f => f.Code == 0x14);
        Assert.Equal("Color tone", tone!.Name, "the menu's own names, not the standard color preset ones");
        Assert.Equal("Warm 2", tone.Definition.DescribeValue(3));
        var refresh = monitor.Features.FirstOrDefault(f => f.Code == 0x2E);
        Assert.True(refresh is not null, "refresh rate (0x2E) appears although the capabilities leave it out");
        Assert.Equal(FeatureRisk.ReadOnly, refresh!.Definition.Risk, "the monitor ignores DDC writes to it");
        Assert.Equal("144 Hz", refresh.Definition.DescribeValue(2));
    }

    [Test]
    public static void TheG5SharpnessIsAnOrdinarySliderAndPictureModeWrites()
    {
        var fake = Fake();
        using var monitor = Monitor(fake);
        var sharpness = monitor.Features.First(f => f.Code == 0x87);
        Assert.Equal(FeatureKind.Continuous, sharpness.Kind);
        Assert.Equal(FeatureRisk.Safe, sharpness.Definition.Risk);
        Assert.Equal(WriteOutcome.Verified, monitor.SetChoice(0x2D, 6), "FPS");
        Assert.True(fake.Writes.Last() == ((byte)0x2D, 6u), "0x2D = 6 was sent");
        // Black Equalizer: a slider of 11 steps (0 to 10) on 0x2F, which the monitor does not advertise.
        var equalizer = monitor.Features.First(f => f.Code == 0x2F);
        Assert.Equal(FeatureKind.Continuous, equalizer.Kind);
        Assert.Equal("Black Equalizer", equalizer.Name);
        monitor.SetPercent(0x2F, 50);
        Assert.True(fake.Writes.Last() == ((byte)0x2F, 5u), "50 % of 0-10 is 5");
        monitor.SetPercent(0x2F, 100);
        Assert.True(fake.Writes.Last() == ((byte)0x2F, 10u), "100 % is 10");
    }
}
