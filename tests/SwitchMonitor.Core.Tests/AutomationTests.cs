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
    public static void ProfilesKeepTheirColor()
    {
        const string json = """{ "scenes": [ { "name": "Work", "color": "#4c8dff", "settings": { "brightness": 50 } },
                                              { "name": "Odd", "color": "blue", "settings": { "brightness": 40 } } ] }""";
        var scenes = SceneStore.Parse(json);
        Assert.Equal("#4C8DFF", scenes[0].Color, "normalized to upper case");
        Assert.Equal<string?>(null, scenes[1].Color, "anything but #RRGGBB is ignored");
        var line = new KeyValuePair<string, System.Text.Json.Nodes.JsonNode>("brightness", 60);
        string recolored = SceneStore.Upsert(json, "Work", "Work", null, [line], color: "#8B7CF6");
        Assert.Equal("#8B7CF6", SceneStore.Parse(recolored)[0].Color);
        Assert.Equal("#8B7CF6", SceneStore.Parse(SceneStore.Upsert(recolored, "Work", "Work", null, [line]))[0].Color, "null keeps it");
        Assert.Equal<string?>(null, SceneStore.Parse(SceneStore.Upsert(recolored, "Work", "Work", null, [line], color: ""))[0].Color);
        Assert.Sequence(["#4C8DFF", "#8B7CF6", "#FF6B6B", "#F5B544"], SceneTemplates.All.Select(t => t.Color ?? ""), "template colors");
    }

    [Test]
    public static void ProfilesKeepTheirIcon()
    {
        const string json = """{ "scenes": [ { "name": "Work", "icon": "glyph:e821", "settings": { "brightness": 50 } },
                                              { "name": "Odd", "icon": "image:../evil.png", "settings": { "brightness": 40 } } ] }""";
        var scenes = SceneStore.Parse(json);
        Assert.Equal("glyph:E821", scenes[0].Icon, "glyph code in upper case");
        Assert.Equal<string?>(null, scenes[1].Icon, "no folders in an image name");
        Assert.Equal("image:cat.png", SceneStore.NormalizeIcon("image:cat.png"));
        Assert.Equal<string?>(null, SceneStore.NormalizeIcon("image:cat.exe"));
        Assert.Equal<string?>(null, SceneStore.NormalizeIcon("glyph:12"));
        Assert.Equal("icon:duotone:game", SceneStore.NormalizeIcon("ICON:Duotone:Game"));
        Assert.Equal("icon:line:game", SceneStore.NormalizeIcon("icon:fluent:game"), "earlier styles become Line");
        Assert.Equal<string?>(null, SceneStore.NormalizeIcon("icon:neon:game"), "unknown style");
        Assert.Equal<string?>(null, SceneStore.NormalizeIcon("icon:line:../x"));
        var line = new KeyValuePair<string, System.Text.Json.Nodes.JsonNode>("brightness", 60);
        string changed = SceneStore.Upsert(json, "Work", "Work", null, [line], icon: "image:desk.png");
        Assert.Equal("image:desk.png", SceneStore.Parse(changed)[0].Icon);
        Assert.Equal("image:desk.png", SceneStore.Parse(SceneStore.Upsert(changed, "Work", "Work", null, [line]))[0].Icon, "null keeps it");
        Assert.Equal<string?>(null, SceneStore.Parse(SceneStore.Upsert(changed, "Work", "Work", null, [line], icon: ""))[0].Icon);
    }

    [Test]
    public static void InterfaceLookIsKeptAndDamagedValuesFallBack()
    {
        var look = Settings.InterfaceSettings.Parse("""{ "color": "5f86b8", "appearance": "Translucent" }""");
        Assert.Equal("#5F86B8", look.Color);
        Assert.Equal(Settings.Appearance.Translucent, look.Appearance);
        var again = Settings.InterfaceSettings.Parse(look.ToJson());
        Assert.Equal(look, again, "round trip");
        Assert.True(look.PointerLight, "pointer light on by default");
        Assert.Equal("line", look.IconStyle);
        Assert.Equal("fill", Settings.InterfaceSettings.Parse("""{ "iconStyle": "Fill" }""").IconStyle);
        Assert.Equal("line", Settings.InterfaceSettings.Parse("""{ "iconStyle": "bold" }""").IconStyle, "styles that are gone fall back to Line");
        Assert.Equal(false, Settings.InterfaceSettings.Parse("""{ "pointerLight": false }""").PointerLight);
        Assert.Equal(Settings.Appearance.Matte, Settings.InterfaceSettings.Parse("""{ "appearance": "matte" }""").Appearance);
        var odd = Settings.InterfaceSettings.Parse("""{ "color": "teal", "appearance": "shiny" }""");
        Assert.Equal(Settings.InterfaceSettings.DefaultColor, odd.Color);
        Assert.Equal(Settings.Appearance.Glossy, odd.Appearance, "unknown names fall back to the default");
        Assert.Equal(Settings.Appearance.Glossy, Settings.InterfaceSettings.Parse("""{ "appearance": "7" }""").Appearance, "numbers outside the list");
        Assert.Equal(new Settings.InterfaceSettings(), Settings.InterfaceSettings.Load(Path.Combine(Path.GetTempPath(), "missing-interface.json")));
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
