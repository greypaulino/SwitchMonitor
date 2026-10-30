using SwitchMonitor.Core.Learning;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class LearnTests
{
    private static (MonitorDevice Monitor, FakeTransport Fake) Display()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 12 60(11 0F) DC(00 03 05) E2(00 01 02)))" };
        fake.Values[0x10] = new VcpValue(70, 100);
        fake.Values[0x12] = new VcpValue(50, 100);
        fake.Values[0x60] = new VcpValue(0x11, 0x0F);
        fake.Values[0xDC] = new VcpValue(0, 5);
        fake.Values[0xE2] = new VcpValue(1, 2);
        fake.Values[0x8A] = new VcpValue(50, 100);   // answered, not advertised
        return (new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay()), fake);
    }

    [Test]
    public static void TheScanFindsOnlyCodesTheAppDoesNotKnowAndNeverWrites()
    {
        var (monitor, fake) = Display();
        var found = SettingLearner.Scan(monitor);
        Assert.Sequence([0xE2, 0x8A], found.Select(c => (int)c.Code), "advertised first; known, input and power left out");
        Assert.Equal(LearnKind.Options, found[0].GuessedKind);
        Assert.Sequence([0u, 1u, 2u], found[0].OptionValues);
        Assert.Equal(LearnKind.Level, found[1].GuessedKind);
        Assert.Equal(false, found[1].Advertised);
        Assert.Equal(0, fake.Writes.Count, "read-only");
        Assert.True(SettingLearner.IsBlocked(0x04) && SettingLearner.IsBlocked(0x60) && SettingLearner.IsBlocked(0xD6) && SettingLearner.IsBlocked(0xCA));
    }

    [Test]
    public static void EveryTestValueIsPutBackAndDangerousCodesAreRefused()
    {
        var (monitor, fake) = Display();
        using (var session = new LearnSession(monitor, new RecordingDelay()))
        {
            var result = session.Try(0x8A, 90);
            Assert.True(result.Matched);
            session.Try(0xE2, 2);
            Assert.Sequence([0x8A, 0xE2], session.Changed.Select(c => (int)c).Order());
            Assert.True(session.Revert(0x8A));
            Assert.Equal(50u, fake.Values[0x8A].Current);
            Assert.Throws<InvalidOperationException>(() => session.Try(0x60, 0x0F));
        }   // closing puts back what is still changed
        Assert.Equal(1u, fake.Values[0xE2].Current);
        Assert.True(fake.Writes.All(w => w.Code != 0x60), "input never written");
    }

    [Test]
    public static void PictureModesAreTriedAutomaticallyAndTheOriginalComesBack()
    {
        var (monitor, fake) = Display();
        // 0x8A only changes in mode 3, like LG colors outside the Custom modes.
        fake.OnWrite = (f, code, value) =>
        {
            if (code == 0x8A && f.Values[0xDC].Current != 3) return;
            f.Values[code] = new VcpValue(value, f.Values.TryGetValue(code, out var old) ? old.Maximum : 0);
        };
        using var session = new LearnSession(monitor, new RecordingDelay());
        var seen = new List<ModeResult>();
        var results = session.CheckModes(0x8A, 100, seen.Add);
        Assert.Sequence([false, true, false], results.Select(r => r.Works == true));
        Assert.Equal(3, seen.Count, "progress for each mode");
        Assert.Equal(0u, fake.Values[0xDC].Current, "original mode back");
        Assert.Equal(50u, fake.Values[0x8A].Current, "original value back");
        Assert.Equal(0, session.Changed.Count);
    }

    [Test]
    public static void LearnedSettingsBecomeControlsOfThatModel()
    {
        var settings = new[]
        {
            new LearnedSetting(0x8A, "Saturation", LearnKind.Level, [], NotAdvertised: true, Modes: [3], ModeCode: 0xDC),
            new LearnedSetting(0xE2, "Overdrive", LearnKind.Options, [(0, "Off"), (2, "High")], NotAdvertised: false)
        };
        string json = LearnedSettingsStore.Upsert(null, "learned-del4321", "Test display", "DEL4321", settings);
        // Learning again replaces the same code instead of adding a second one.
        json = LearnedSettingsStore.Upsert(json, "learned-del4321", "Test display", "DEL4321",
            [settings[1] with { Values = [(0, "Off"), (1, "Normal"), (2, "High")] }]);
        var database = QuirkDatabase.Parse(json);
        var quirk = database.Match(TestMonitors.Identity().DeviceId);
        Assert.True(quirk is not null);
        Assert.Equal(2, quirk!.Controls.Count);

        var (_, fake) = Display();
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, quirk, null, new RecordingDelay());
        var saturation = monitor.Features.Single(f => f.Code == 0x8A);
        Assert.Equal("Saturation", saturation.Name);
        Assert.Equal(FeatureRisk.Safe, saturation.Definition.Risk);
        Assert.Equal(1, saturation.Definition.EnabledWhen.Count);
        var overdrive = monitor.Features.Single(f => f.Code == 0xE2);
        Assert.Sequence([0u, 1u, 2u], overdrive.AllowedValues);
        Assert.Equal("Normal", overdrive.Definition.DescribeValue(1));
        Assert.Equal(WriteOutcome.Verified, monitor.SetChoice(0xE2, 2));
        Assert.Throws<FormatException>(() => LearnedSettingsStore.Upsert(null, "x", "X", "DEL4321",
            [new LearnedSetting(0x60, "Input", LearnKind.Level, [], false)]));
    }

    [Test]
    public static void ValuesAreMeasuredPerModeAndLinkedCodesFound()
    {
        var (monitor, fake) = Display();
        // E2 only takes 0 and 2 (rounds 1 to 2); each picture mode brings its own E2; writing E2 = 2 also moves 0x12.
        fake.Values[0xE2] = new VcpValue(0, 2);
        fake.OnWrite = (f, code, value) =>
        {
            if (code == 0xE2 && value == 1) value = 2;
            if (code == 0xDC) f.Values[0xE2] = new VcpValue(value == 3 ? 2u : 0u, 2);
            if (code == 0xE2) f.Values[0x12] = new VcpValue(value == 2 ? 60u : 50u, 100);
            f.Values[code] = new VcpValue(value, f.Values.TryGetValue(code, out var old) ? old.Maximum : 0);
        };
        var candidate = SettingLearner.Scan(monitor).Single(c => c.Code == 0xE2);
        using var session = new LearnSession(monitor, new RecordingDelay());
        var sweep = ValueStudy.Sweep(session, 0xE2, ValueStudy.SweepValues(candidate));
        Assert.Sequence(["0: acepta", "1: lo cambia a 2", "2: acepta"], sweep.Select(r => r.ToString()));
        Assert.Equal(0u, fake.Values[0xE2].Current, "put back");

        var perMode = ValueStudy.ValuePerMode(session, 0xE2, new RecordingDelay());
        Assert.Sequence([0u, 2u, 0u], perMode.Select(m => m.Value ?? 99));
        Assert.Equal(0u, fake.Values[0xDC].Current, "mode put back");

        var links = ValueStudy.Links(session, 0xE2, 2, [0x10, 0x12, 0xE2], new RecordingDelay());
        Assert.Sequence([0x12], links.Select(l => (int)l.Code));
        Assert.Equal(0, session.Changed.Count);

        string source = LearnedSettingsStore.Upsert(null, "learned-del4321", "Test display", "DEL4321",
            [new LearnedSetting(0xE2, "Overdrive", LearnKind.Options, [(0, "Off"), (2, "On")], false)]);
        string path = Path.Combine(Path.GetTempPath(), $"learned-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal(1, LearnedSettingsMerge.MergeInto(source, path));
            Assert.Equal(1, LearnedSettingsMerge.MergeInto(source, path), "again: replaced, not doubled");
            Assert.Equal(1, QuirkDatabase.Parse(File.ReadAllText(path)).All.Single().Controls.Count);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public static void FoundCodesAreDescribedAndRiskyOnesHidden()
    {
        Assert.Equal(CodeCategory.Sensitive, CodeCatalog.Describe(0xF6, "GSM7714", 255).Category, "LG F6 reboots the monitor");
        Assert.Equal(CodeCategory.ReadOnly, CodeCatalog.Describe(0x4D, "GSM7714", 0xFFFF).Category, "LG picture mode list");
        Assert.Equal(CodeCategory.Setting, CodeCatalog.Describe(0xFA, "GSM7714", 255).Category, "LG motion blur");
        Assert.Equal(CodeCategory.ReadOnly, CodeCatalog.Describe(0x20, "DEL4321", 100).Category, "VGA position");
        Assert.Equal(CodeCategory.Setting, CodeCatalog.Describe(0xF6, "DEL4321", 255).Category, "vendor notes are per maker");
        Assert.Equal(CodeCategory.ReadOnly, CodeCatalog.Describe(0xE7, "DEL4321", 0xFFFF).Category, "a 16-bit range is data");

        var fake = new FakeTransport { Capabilities = "(vcp(10 12))" };
        fake.Values[0x10] = new VcpValue(70, 100);
        fake.Values[0x12] = new VcpValue(50, 100);
        fake.Values[0xF6] = new VcpValue(2, 255);
        fake.Values[0x20] = new VcpValue(0, 100);
        fake.Values[0xFA] = new VcpValue(0, 1);
        var lg = TestMonitors.Identity(@"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001");
        using var monitor = new MonitorDevice(lg, fake, null, null, new RecordingDelay());
        var hidden = new List<LearnCandidate>();
        var found = SettingLearner.Scan(monitor, hidden: hidden);
        Assert.Sequence([0xFA, 0x20], found.Select(c => (int)c.Code), "settings first, then information");
        Assert.Sequence([0xF6], hidden.Select(c => (int)c.Code));
        using var session = new LearnSession(monitor, new RecordingDelay());
        Assert.Throws<InvalidOperationException>(() => session.Try(0xF6, 1));
        Assert.True(fake.Writes.Count == 0, "nothing written");
    }
}
