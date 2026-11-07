using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

/// <summary>Profile changes that toggle FreeSync: levels fade, the screen is covered, FreeSync goes last, the cover is lifted.</summary>
internal static class TransitionTests
{
    private const string LgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";

    private static (MonitorDevice Lg, FakeTransport Fake) GameModeLg(uint freeSync)
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 15(0B 0E) F8(00 01)))" };
        fake.Values[0x10] = new VcpValue(50, 100);
        fake.Values[0x15] = new VcpValue(0x0E, 255);   // Custom (Game): FreeSync is available there
        fake.Values[0xF8] = new VcpValue(freeSync, 1);
        var lg = new MonitorDevice(TestMonitors.Identity(LgId), fake, QuirkDatabase.LoadEmbedded().Match(LgId), null, new RecordingDelay());
        return (lg, fake);
    }

    private static SceneProfile Scene(string freeSync) => SceneStore.Parse(
        $$"""{ "scenes": [ { "name": "G", "settings": { "FreeSync": "{{freeSync}}", "brightness": 90 } } ] }""")[0];

    [Test]
    public static void TheWholeProfileChangesUnderTheCover()
    {
        var (lg, fake) = GameModeLg(freeSync: 1);
        using var _ = lg;
        var events = new List<string>();
        int before = -1, after = -1;
        var hooks = new TransitionHooks
        {
            BeforeDisruptive = () => { before = fake.Writes.Count; events.Add("cover on"); },
            AfterDisruptive = () => { after = fake.Writes.Count; events.Add("cover off"); }
        };
        var steps = SceneApplier.Apply(Scene("Off"), [lg], delay: new RecordingDelay(), fade: SceneApplier.ProfileFade, hooks: hooks);
        Assert.Sequence(["cover on", "cover off"], events);
        Assert.Equal(0, before, "nothing is written before the screen is covered: no visible fade");
        Assert.Equal(fake.Writes.Count, after, "the cover stays until everything was written");
        Assert.True(fake.Writes.Any(w => w.Code == 0xF8) && fake.Writes.Any(w => w.Code == 0x10), "FreeSync and brightness both under the cover");
        Assert.Equal(2, fake.Writes.Count, "at once: one write each, no steps");
        Assert.Equal(0u, fake.Values[0xF8].Current);
        Assert.Equal(90u, fake.Values[0x10].Current);
        Assert.True(steps.All(s => s.Status is not (SceneStepStatus.Failed or SceneStepStatus.Mismatch)));
    }

    [Test]
    public static void NoCoverWhenFreeSyncAlreadyHasTheValue()
    {
        var (lg, fake) = GameModeLg(freeSync: 0);
        using var _ = lg;
        int calls = 0;
        var hooks = new TransitionHooks { BeforeDisruptive = () => calls++, AfterDisruptive = () => calls++ };
        SceneApplier.Apply(Scene("Off"), [lg], delay: new RecordingDelay(), hooks: hooks);
        Assert.Equal(0, calls, "nothing disruptive to do: no black, no fade");
        Assert.Equal(1, fake.Writes.Count, "only brightness is written, at once as before");
    }

    [Test]
    public static void TheCoverIsLiftedEvenWhenFreeSyncFails()
    {
        var (lg, fake) = GameModeLg(freeSync: 1);
        using var _ = lg;
        fake.OnWrite = (self, code, value) =>
        {
            if (code == 0xF8) throw new TransportException("fake", "the display did not answer");
            self.Values[code] = new VcpValue(value, self.Values[code].Maximum);
        };
        bool off = false;
        var steps = SceneApplier.Apply(Scene("Off"), [lg], delay: new RecordingDelay(),
            hooks: new TransitionHooks { BeforeDisruptive = () => { }, AfterDisruptive = () => off = true });
        Assert.True(off, "the screen is never left covered");
        Assert.True(steps.Any(s => s.Status == SceneStepStatus.Failed && s.Setting.Feature.Name == "FreeSync"), "and the failure is reported");
    }

    [Test]
    public static void WithoutHooksTheOrderIsUnchanged()
    {
        var (lg, fake) = GameModeLg(freeSync: 1);
        using var _ = lg;
        SceneApplier.Apply(Scene("Off"), [lg], delay: new RecordingDelay());
        Assert.Sequence([(byte)0xF8, (byte)0x10], fake.Writes.Select(w => w.Code), "choices first, levels last, as before");
    }

    [Test]
    public static void LevelsGlideInRealTimeWithoutRepeatingOrOvershooting()
    {
        var (lg, fake) = GameModeLg(freeSync: 0);
        using var _ = lg;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        SceneApplier.Apply(Scene("Off"), [lg], fade: TimeSpan.FromMilliseconds(400));   // no fake delay: real time
        var levels = fake.Writes.Where(w => w.Code == 0x10).Select(w => w.Value).ToArray();
        Assert.True(clock.ElapsedMilliseconds >= 350, "it took about the fade time");
        Assert.True(levels.Length > 3, "in several steps, not one jump");
        Assert.Equal(90u, levels[^1], "the last write is exactly the target");
        Assert.True(levels.Zip(levels.Skip(1), (a, b) => b >= a).All(x => x), "always rising: no overshoot or going back");
        Assert.Equal(levels.Length, levels.Distinct().Count(), "never the same value twice");
    }
}
