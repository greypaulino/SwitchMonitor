using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class RetryTests
{
    [Test]
    public static void BackoffGrowsAndIsCapped()
    {
        var policy = new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), Multiplier = 2, MaxDelay = TimeSpan.FromMilliseconds(300) };
        Assert.Sequence([0.0, 100, 200, 300, 300], Enumerable.Range(1, 5).Select(a => policy.DelayBefore(a).TotalMilliseconds));
    }

    [Test]
    public static void RetriesTransientFailuresThenSucceeds()
    {
        var delay = new RecordingDelay();
        int calls = 0;
        int result = RetryPolicy.Default.Execute("test", () =>
        {
            if (++calls < 3) throw new TransportException("fake", "busy");
            return 42;
        }, delay);
        Assert.Equal(42, result);
        Assert.Equal(3, calls, "calls");
        Assert.Sequence([0.0, 60, 120], delay.Waits.Select(w => w.TotalMilliseconds));
    }

    [Test]
    public static void GivesUpAfterMaxAttempts()
    {
        int calls = 0;
        var policy = new RetryPolicy { MaxAttempts = 3 };
        Assert.Throws<TransportException>(() => policy.Execute("test", () => { calls++; throw new TransportException("fake", "dead"); }, new RecordingDelay()));
        Assert.Equal(3, calls);
    }

    [Test]
    public static void DoesNotRetryProgrammingErrors()
    {
        int calls = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetryPolicy.Default.Execute("test", () => { calls++; throw new ArgumentOutOfRangeException("x"); }, new RecordingDelay()));
        Assert.Equal(1, calls);
    }

    [Test]
    public static void PercentMapsToReportedMaximum()
    {
        Assert.Equal(25u, VcpValue.FromPercent(50, 50));
        Assert.Equal(50u, VcpValue.FromPercent(150, 50), "clamped high");
        Assert.Equal(0u, VcpValue.FromPercent(-5, 50), "clamped low");
        Assert.Equal(50.0, new VcpValue(25, 50).Percent);
        Assert.Throws<InvalidOperationException>(() => VcpValue.FromPercent(50, 0));
    }
}

internal static class DeviceTests
{
    private static MonitorDevice Standard(FakeTransport fake, RecordingDelay? delay = null, ICoreLog? log = null) =>
        new(TestMonitors.Identity(), fake, null, log, delay ?? new RecordingDelay());

    private static MonitorDevice Lg(FakeTransport fake, RecordingDelay? delay = null)
    {
        const string id = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        return new(TestMonitors.Identity(id), fake, QuirkDatabase.LoadEmbedded().Match(id), null, delay ?? new RecordingDelay());
    }

    [Test]
    public static void StandardInputSwitchUsesVcp60AndVerifies()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 60(11 12 0F)))" };
        fake.Values[0x60] = new VcpValue(0x11, 0);
        using var monitor = Standard(fake);
        Assert.Sequence([0x11, 0x12, 0x0F], monitor.Inputs);
        Assert.Equal(WriteOutcome.Verified, monitor.SetInput(0x0F));
        Assert.Sequence([((byte)0x60, 0x0Fu)], fake.Writes);
    }

    [Test]
    public static void LgInputSwitchUsesVendorCode()
    {
        // The LG reports the standard code on 0x60 but only switches through 0xF4.
        var fake = new FakeTransport
        {
            OnWrite = (f, code, value) =>
            {
                if (code == 0xF4) f.Values[0x60] = new VcpValue(value switch { 0x90 => 0x11u, 0x91 => 0x12u, _ => 0x0Fu }, 0);
            }
        };
        fake.Values[0x60] = new VcpValue(0x11, 0);
        using var monitor = Lg(fake);
        Assert.Equal("LG 29WK600", monitor.Name);
        Assert.Equal("DisplayPort 1", monitor.DescribeInput(0x0F));
        Assert.Equal(WriteOutcome.Verified, monitor.SetInput(0x0F));
        Assert.Sequence([((byte)0xF4, 0xD0u)], fake.Writes);
    }

    [Test]
    public static void LgIgnoresPhantomDisplayPort2FromCapabilities()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 60(11 12 0F 10)))" };
        using var monitor = Lg(fake);
        Assert.Sequence([0x11, 0x12, 0x0F], monitor.Inputs);
        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.SetInput(0x10));
        Assert.Equal(0, fake.Writes.Count, "nothing sent");
    }

    [Test]
    public static void UnreadableAfterSwitchIsUnverifiedNotAnError()
    {
        // Typical: once the monitor shows the other PC, this computer can no longer read it.
        var fake = new FakeTransport { Capabilities = "(vcp(60(11 12)))" };
        fake.FailRead = _ => fake.Writes.Count > 0 ? new TransportException("fake", "no reply") : null;
        fake.Values[0x60] = new VcpValue(0x11, 0);
        using var monitor = Standard(fake);
        Assert.Equal(WriteOutcome.Unverified, monitor.SetInput(0x12));
    }

    [Test]
    public static void StuckValueIsMismatch()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(60(11 12)))", OnWrite = (_, _, _) => { } };
        fake.Values[0x60] = new VcpValue(0x11, 0);
        var log = new MemoryLog();
        using var monitor = Standard(fake, log: log);
        Assert.Equal(WriteOutcome.Mismatch, monitor.SetInput(0x12));
        Assert.True(log.Entries.Any(e => e.Contains("expected 18")), "mismatch logged");
    }

    [Test]
    public static void BusyPanelIsRetriedWithBackoff()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10))", FailWritesRemaining = 2 };
        fake.Values[0x10] = new VcpValue(10, 50);
        var delay = new RecordingDelay();
        using var monitor = Standard(fake, delay);
        Assert.Equal(WriteOutcome.Verified, monitor.SetPercent(VcpCode.Brightness, 50));
        Assert.Equal(new VcpValue(25, 50), fake.Values[0x10]);
        Assert.True(delay.Waits.Count(w => w > TimeSpan.Zero) >= 2, "waited between attempts");
    }

    [Test]
    public static void SupportsUsesCapabilitiesOrQuirk()
    {
        using var standard = Standard(new FakeTransport { Capabilities = "(vcp(10 12 62))" });
        Assert.True(standard.Supports(VcpCode.Contrast) && standard.Supports(VcpCode.AudioVolume), "advertised");
        Assert.True(!standard.Supports(VcpCode.PowerMode), "not advertised");

        using var lg = Lg(new FakeTransport());   // GPU transports return no capabilities string
        Assert.True(lg.Supports(VcpCode.Brightness) && lg.Supports(VcpCode.InputSource), "quirk-validated features");
        Assert.True(!lg.Supports(VcpCode.Contrast), "unvalidated feature hidden");
    }

    [Test]
    public static void DisabledWritesAreRefused()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10))", SupportsWrites = false };
        fake.Values[0x10] = new VcpValue(10, 100);
        using var monitor = Standard(fake);
        Assert.Equal(10u, monitor.Read(VcpCode.Brightness).Current);
        Assert.Throws<NotSupportedException>(() => monitor.Write(VcpCode.Brightness, 50));
        Assert.Equal(0, fake.Writes.Count);
    }

    [Test]
    public static void DisposeReleasesTransport()
    {
        var fake = new FakeTransport();
        var monitor = Standard(fake);
        monitor.Dispose();
        Assert.True(fake.Disposed, "transport disposed");
        Assert.Throws<ObjectDisposedException>(() => monitor.Read(VcpCode.Brightness));
    }
}

internal static class LogTests
{
    [Test]
    public static void RotatesAndKeepsBoundedFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "switchmonitor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileLog(dir, maxBytes: 400, keepFiles: 3);
            for (int i = 0; i < 60; i++) log.Write(LogLevel.Info, $"line {i} " + new string('x', 40));
            var files = Directory.GetFiles(dir).Select(f => Path.GetFileName(f)).Order().ToArray();
            Assert.Sequence(["switchmonitor.1.log", "switchmonitor.2.log", "switchmonitor.log"], files);
            Assert.True(File.ReadAllText(log.CurrentPath).Contains("line 59"), "newest line in current file");
            Assert.True(new FileInfo(log.CurrentPath).Length <= 400, "size bound");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public static void DebugIsFilteredByDefault()
    {
        string dir = Path.Combine(Path.GetTempPath(), "switchmonitor-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new FileLog(dir);
            log.Write(LogLevel.Debug, "hidden");
            log.Write(LogLevel.Warning, "shown", new TransportException("fake", "detail"));
            string text = File.ReadAllText(log.CurrentPath);
            Assert.True(!text.Contains("hidden") && text.Contains("shown") && text.Contains("TransportException: detail"), text);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
