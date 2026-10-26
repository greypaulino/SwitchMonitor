using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class AutomationTests
{
    [Test]
    public static void ProgramsAreLinkedToOneProfileOnly()
    {
        const string json = """
            { "scenes": [ { "name": "Game", "apps": ["C:\\Games\\EldenRing.EXE", "eldenring.exe"], "settings": { "brightness": 80 } } ] }
            """;
        var game = SceneStore.Parse(json)[0];
        Assert.Sequence(["eldenring.exe"], game.Apps, "file name, lower case, once");
        Assert.True(game.IsLinkedTo(@"D:\Other\ELDENRING.exe"));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""
            { "scenes": [ { "name": "A", "apps": ["x.exe"], "settings": { "brightness": 1 } },
                          { "name": "B", "apps": ["X.EXE"], "settings": { "brightness": 2 } } ] }
            """));

        var line = new KeyValuePair<string, System.Text.Json.Nodes.JsonNode>("brightness", 70);
        string updated = SceneStore.Upsert(json, "Game", "Game", null, [line], ["Cyberpunk2077.exe"]);
        Assert.Sequence(["cyberpunk2077.exe"], SceneStore.Parse(updated)[0].Apps);
        string kept = SceneStore.Upsert(updated, "Game", "Game", null, [line]);
        Assert.Sequence(["cyberpunk2077.exe"], SceneStore.Parse(kept)[0].Apps, "null keeps the links");
        string cleared = SceneStore.Upsert(updated, "Game", "Game", null, [line], []);
        Assert.Equal(0, SceneStore.Parse(cleared)[0].Apps.Count);
    }

    [Test]
    public static void FadesMoveLevelsInSmallStepsAndEndOnTheTarget()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 12))" };
        fake.Values[0x10] = new VcpValue(80, 100);
        fake.Values[0x12] = new VcpValue(50, 100);
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        var scene = SceneStore.Parse("""{ "scenes": [ { "name": "N", "settings": { "brightness": 20, "contrast": 40 } } ] }""")[0];
        var delay = new RecordingDelay();
        var steps = SceneApplier.Apply(scene, [monitor], delay: delay, fade: TimeSpan.FromSeconds(3));
        Assert.True(steps.All(s => s.Status == SceneStepStatus.Applied));
        var brightness = fake.Writes.Where(w => w.Code == 0x10).Select(w => w.Value).ToArray();
        Assert.True(brightness.Length >= 5, $"several steps ({brightness.Length})");
        Assert.True(brightness.Zip(brightness.Skip(1)).All(p => p.Second <= p.First), "always going down, never jumping back");
        Assert.Equal(20u, brightness[^1], "ends exactly on the target");
        Assert.Equal(40u, fake.Values[0x12].Current);
        Assert.True(delay.Waits.Count(w => w == SceneApplier.FadeStep) >= 4, "paced");

        // Without a fade: one write per level, as before.
        fake.Writes.Clear();
        SceneApplier.Apply(SceneStore.Parse("""{ "scenes": [ { "name": "D", "settings": { "brightness": 70 } } ] }""")[0], [monitor],
            delay: new RecordingDelay());
        Assert.Equal(1, fake.Writes.Count);
    }
}
