using SwitchMonitor.Core.Hotkeys;
using SwitchMonitor.Core.Settings;

namespace SwitchMonitor.Core.Tests;

/// <summary>A shortcut several displays share acts on the display under the mouse; the same display twice is still a conflict.</summary>
internal static class ShortcutRoutingTests
{
    [Test]
    public static void TheDisplayUnderTheMouseWins()
    {
        string[] both = ["lg", "g5"];
        Assert.Equal("g5", ShortcutRouting.Choose(both, "g5", "lg"), "the pointer is on the G5");
        Assert.Equal("lg", ShortcutRouting.Choose(both, "lg", "g5"), "the pointer is on the LG");
        Assert.Equal("g5", ShortcutRouting.Choose(both, null, "g5"), "on the taskbar: the last display the pointer was on");
        Assert.Equal("g5", ShortcutRouting.Choose(both, "laptop", "g5"), "on a display without that shortcut: the last of them");
        Assert.Equal("lg", ShortcutRouting.Choose(both, "laptop", "other"), "nothing known: the first");
        Assert.True(ShortcutRouting.Choose([], "lg", "lg") is null, "no display has it");
    }

    [Test]
    public static void TwoDisplaysMayShareAnInputShortcut()
    {
        var chord = HotkeyChord.Parse("Ctrl+Alt+1");
        var settings = new AppSettings
        {
            InputShortcuts = [new InputShortcut("lg", 0x11, chord), new InputShortcut("g5", 0x11, chord)]
        };
        Assert.Equal(0, settings.FindConflicts([]).Count, "different displays: not a conflict");

        var twice = new AppSettings
        {
            InputShortcuts = [new InputShortcut("lg", 0x11, chord), new InputShortcut("lg", 0x12, chord)]
        };
        Assert.Equal(1, twice.FindConflicts([]).Count, "the same display twice is");

        var against = settings.FindConflicts([("profile Work", chord)]);
        Assert.Equal(1, against.Count, "an input shortcut against a profile shortcut is");
        Assert.True(against[0].Contains("profile Work"), "and the message names it");
    }
}
