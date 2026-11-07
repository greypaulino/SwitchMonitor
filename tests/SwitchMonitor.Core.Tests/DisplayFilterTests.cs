using SwitchMonitor.Core.Scenes;

namespace SwitchMonitor.Core.Tests;

/// <summary>Profiles tied to a display: by its stable key when there is one, by name otherwise (older profiles).</summary>
internal static class DisplayFilterTests
{
    private const string FirstId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
    private const string SecondId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0002";   // the same model: same name

    private static MonitorDevice Display(string id) =>
        new(TestMonitors.Identity(id), new FakeTransport { Capabilities = "(vcp(10))" }, null, null, new RecordingDelay());

    private static readonly KeyValuePair<string, System.Text.Json.Nodes.JsonNode> Line = new("brightness", 60);

    [Test]
    public static void AKeyTellsTwoIdenticalMonitorsApart()
    {
        using var first = Display(FirstId);
        using var second = Display(SecondId);
        string json = SceneStore.Upsert(SceneStore.DefaultJson, null, "Desk", "Test display", [Line], monitorKey: first.Key);
        var desk = SceneStore.Parse(json)[0];
        Assert.Equal(first.Key, desk.MonitorKey);
        Assert.True(desk.AppliesTo(first), "its own display");
        Assert.True(!(desk.AppliesTo(second)), "same name, other display: hidden for it");
        Assert.True(desk.IsDisplaySpecific);
    }

    [Test]
    public static void ProfilesWithoutAKeyStillMatchByName()
    {
        using var first = Display(FirstId);
        using var second = Display(SecondId);
        var byName = SceneStore.Parse("""{ "scenes": [ { "name": "Old", "monitor": "Test", "settings": { "brightness": 50 } } ] }""")[0];
        Assert.True(byName.AppliesTo(first));
        Assert.True(byName.AppliesTo(second), "a name cannot tell them apart: it behaves as before");
        var everywhere = SceneStore.Parse("""{ "scenes": [ { "name": "All", "settings": { "brightness": 50 } } ] }""")[0];
        Assert.True(everywhere.AppliesTo(first) && everywhere.AppliesTo(second));
        Assert.True(!(everywhere.IsDisplaySpecific), "a global profile belongs to every display");
    }

    [Test]
    public static void TheKeyIsKeptOrClearedAsAsked()
    {
        using var first = Display(FirstId);
        string tied = SceneStore.Upsert(SceneStore.DefaultJson, null, "Desk", "Test display", [Line], monitorKey: first.Key);
        Assert.Equal(first.Key, SceneStore.Parse(SceneStore.Upsert(tied, "Desk", "Desk", "Test display", [Line]))[0].MonitorKey, "null keeps it");
        var everywhere = SceneStore.Parse(SceneStore.Upsert(tied, "Desk", "Desk", null, [Line], monitorKey: ""))[0];
        Assert.Equal<string?>(null, everywhere.MonitorKey, "unticking 'only this display' removes the key");
        Assert.Equal<string?>(null, everywhere.MonitorFilter);
        var orphan = SceneStore.Parse(tied)[0];
        Assert.Equal<string?>(null, SceneStore.Parse(SceneStore.Upsert(tied, "Desk", "Desk", "Test display", [Line], monitorKey: ""))[0].MonitorKey,
            "an empty key removes it even with a name filter");
        Assert.Equal("Desk", orphan.Name, "a profile whose display is gone is still there: nothing deletes it");
    }

    [Test]
    public static void SharedProfilesDoNotCarryThisComputersDisplayKey()
    {
        using var first = Display(FirstId);
        string tied = SceneStore.Upsert(SceneStore.DefaultJson, null, "Desk", "Test display", [Line], monitorKey: first.Key);
        string shared = SceneStore.ExportProfile(tied, "Desk", Path.GetTempPath());
        Assert.True(!(shared.Contains("monitorKey")), "exported file");
        var (imported, _) = SceneStore.ImportProfile(SceneStore.DefaultJson, shared, Path.GetTempPath());
        Assert.Equal<string?>(null, SceneStore.Parse(imported)[0].MonitorKey, "imported profile");
    }
}
