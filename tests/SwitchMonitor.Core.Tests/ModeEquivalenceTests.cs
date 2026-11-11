using SwitchMonitor.Core.Displays;

namespace SwitchMonitor.Core.Tests;

/// <summary>Linked displays share logical picture modes; each display maps them to its own native names.</summary>
internal static class ModeEquivalenceTests
{
    private static readonly string[] Lg = ["Custom", "Reader", "Cinema", "Photo", "FPS Game 1", "RTS Game", "Vivid"];
    private static readonly string[] G5 = ["Custom", "Standard", "Game", "Cinema", "Eco"];

    [Test]
    public static void NativeNamesMapToLogicalModes()
    {
        var table = new ModeEquivalence();
        Assert.Equal("FPS", table.LogicalOf("FPS Game 1"), "LG name");
        Assert.Equal("Cinema", table.LogicalOf("movie"), "case and synonym");
        Assert.True(table.LogicalOf("Nothing Known") is null, "unknown stays unknown");
    }

    [Test]
    public static void EachDisplayGetsItsOwnName()
    {
        var table = new ModeEquivalence();
        Assert.Equal("FPS Game 1", table.NativeFor("FPS", Lg), "LG has FPS Game 1");
        Assert.True(table.NativeFor("FPS", G5) is null, "G5 has no FPS: skipped");
        Assert.Equal("Cinema", table.NativeFor("Cinema", G5), "both have Cinema");
    }

    [Test]
    public static void OnlyModesSharedByTwoDisplaysAreListed()
    {
        var shared = new ModeEquivalence().SharedModes([Lg, G5]);
        Assert.Equal("Custom,Cinema", string.Join(",", shared), "Custom and Cinema are on both");
    }

    [Test]
    public static void AFileReplacesAGroup()
    {
        string dir = Path.Combine(Path.GetTempPath(), "modeeq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, ModeEquivalence.FileName), "{\"Game\": [\"FPS Game 1\"]}");
            var table = ModeEquivalence.Load(dir);
            Assert.Equal("FPS Game 1", table.NativeFor("Game", Lg), "Game now maps to the LG's FPS mode");
            File.WriteAllText(Path.Combine(dir, ModeEquivalence.FileName), "not json");
            Assert.Equal("Cinema", ModeEquivalence.Load(dir).NativeFor("Cinema", Lg), "a broken file falls back to the defaults");
        }
        finally { Directory.Delete(dir, true); }
    }
}
