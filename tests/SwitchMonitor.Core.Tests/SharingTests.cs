using SwitchMonitor.Core.Displays;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Settings;
using SwitchMonitor.Core.Sharing;

namespace SwitchMonitor.Core.Tests;

internal static class SharingTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "switchmonitor-share-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public static void ThePreferredDisplayIsMadeMainOnlyWhenItIsConnectedAndNotMainYet()
    {
        DisplaySeat lg = new("lg", @"\\.\DISPLAY2", true), laptop = new("laptop", @"\\.\DISPLAY1", true);
        Assert.Equal(@"\\.\DISPLAY2", PreferredMain.Target("lg", [lg, laptop], @"\\.\DISPLAY1"), "LG back: it becomes main");
        Assert.Equal<string?>(null, PreferredMain.Target("lg", [lg, laptop], @"\\.\DISPLAY2"), "already main");
        Assert.Equal<string?>(null, PreferredMain.Target("lg", [lg with { IsActive = false }, laptop], @"\\.\DISPLAY1"), "LG gone: Windows' choice stays");
        Assert.Equal<string?>(null, PreferredMain.Target("lg", [lg], @"\\.\DISPLAY1"), "only one display");
        Assert.Equal<string?>(null, PreferredMain.Target(null, [lg, laptop], @"\\.\DISPLAY1"), "no preference");

        var settings = AppSettings.Parse(new AppSettings { PreferredMain = "lg" }.ToJson());
        Assert.Equal("lg", settings.PreferredMain);
        Assert.Equal<string?>(null, AppSettings.Parse(new AppSettings().ToJson()).PreferredMain);
    }

    [Test]
    public static void ABackupBringsEverySettingBackAndKeepsTheOneItReplaced()
    {
        string data = TempDir(), elsewhere = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"a\":1}");
            Directory.CreateDirectory(Path.Combine(data, "icons"));
            File.WriteAllBytes(Path.Combine(data, "icons", "Night.png"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(data, "logs"));
            File.WriteAllText(Path.Combine(data, "logs", "switchmonitor.log"), "log");
            File.WriteAllText(Path.Combine(data, "phones.json"), "mine");
            string file = Path.Combine(elsewhere, "mine" + Backup.Extension);
            Assert.Equal(2, Backup.Create(data, file, "test"), "logs and paired phones left out");

            File.WriteAllText(Path.Combine(data, "settings.json"), "{\"a\":2}");
            File.WriteAllText(Path.Combine(data, "scenes.json"), "{}");
            string safety = Backup.Restore(file, data, "test");
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
            Assert.True(!File.Exists(Path.Combine(data, "scenes.json")), "a file the backup did not have is gone");
            Assert.Sequence([1, 2, 3], File.ReadAllBytes(Path.Combine(data, "icons", "Night.png")).Select(b => (int)b));
            Assert.True(File.Exists(Path.Combine(data, "logs", "switchmonitor.log")), "logs untouched");
            Assert.True(File.Exists(safety), "the replaced settings are kept");
            Assert.Equal("mine", File.ReadAllText(Path.Combine(data, "phones.json")), "this computer's phones stay");

            // Someone else's backup that holds their paired phones: those phones are not let in.
            string theirs = Path.Combine(elsewhere, "theirs" + Backup.Extension);
            File.Copy(file, theirs);
            using (var zip = System.IO.Compression.ZipFile.Open(theirs, System.IO.Compression.ZipArchiveMode.Update))
                using (var writer = new StreamWriter(zip.CreateEntry("phones.json").Open())) writer.Write("theirs");
            Backup.Restore(theirs, data, "test");
            Assert.Equal("mine", File.ReadAllText(Path.Combine(data, "phones.json")), "a backup never brings phones");

            string notBackup = Path.Combine(elsewhere, "other.zip");
            using (var zip = System.IO.Compression.ZipFile.Open(notBackup, System.IO.Compression.ZipArchiveMode.Create))
                zip.CreateEntry("settings.json");
            Assert.Throws<FormatException>(() => Backup.Restore(notBackup, data, "test"));
            Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(data, "settings.json")), "nothing changed");
        }
        finally
        {
            Directory.Delete(data, true);
            Directory.Delete(elsewhere, true);
        }
    }

    [Test]
    public static void AProfileTravelsWithItsPictureButNotItsShortcut()
    {
        string from = TempDir(), to = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(from, "Night.png"), [9, 8, 7]);
            string scenes = """
                { "version": 4, "scenes": [ { "name": "Night", "hotkey": "Ctrl+Alt+N", "icon": "image:Night.png", "color": "#8B7CF6",
                  "settings": { "brightness": 30 } } ] }
                """;
            string shared = SceneStore.ExportProfile(scenes, "Night", from);
            Assert.True(!shared.Contains("Ctrl+Alt+N"), "shortcut stays");

            var (json, name) = SceneStore.ImportProfile(scenes, shared, to);
            Assert.Equal("Night (2)", name, "name in use");
            var imported = SceneStore.Parse(json).Single(s => s.Name == "Night (2)");
            Assert.True(imported.Hotkey is null);
            Assert.Sequence([9, 8, 7], File.ReadAllBytes(Path.Combine(to, "Night.png")).Select(b => (int)b));
            Assert.Throws<FormatException>(() => SceneStore.ImportProfile(scenes, "{\"app\":\"Other\"}", to));
        }
        finally
        {
            Directory.Delete(from, true);
            Directory.Delete(to, true);
        }
    }

    [Test]
    public static void TheSharedDatabaseIsInstalledOnlyWhenItsHashMatches()
    {
        string dir = TempDir();
        try
        {
            byte[] content = System.Text.Encoding.UTF8.GetBytes("{ \"monitors\": [] }");
            string path = Path.Combine(dir, "quirks.shared.json");
            Assert.Throws<FormatException>(() => SharedQuirks.Install(content, new string('0', 64), path));
            Assert.True(!File.Exists(path));
            Assert.Equal(0, SharedQuirks.Install(content, SharedQuirks.Hash(content) + "  quirks.json", path));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(dir, true); }

        // The published copy in database\ matches the hash published beside it.
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "database", "quirks.json"))) folder = folder.Parent;
        if (folder is null) return;   // tests run outside the repository
        byte[] published = File.ReadAllBytes(Path.Combine(folder.FullName, "database", "quirks.json"));
        string hash = File.ReadAllText(Path.Combine(folder.FullName, "database", "quirks.json.sha256"));
        Assert.True(SharedQuirks.Verify(published, hash) > 0, "database/quirks.json.sha256 is up to date");
    }

    [Test]
    public static void LearnedSettingsOfAModelCanBeSharedAndImported()
    {
        string dir = TempDir();
        try
        {
            string mine = Path.Combine(dir, "quirks.user.json"), theirs = Path.Combine(dir, "other.user.json");
            File.WriteAllText(mine, Learning.LearnedSettingsStore.Upsert(null, "learned-del4321", "Test display", "DEL4321",
                [new Learning.LearnedSetting(0xE2, "Overdrive", Learning.LearnKind.Options, [(0, "Off"), (2, "High")], false)]));
            string? file = SharedQuirks.ExportLearned("learned-del4321", mine);
            Assert.True(file is not null);
            Assert.Equal<string?>(null, SharedQuirks.ExportLearned("learned-gsm0000", mine), "nothing learned for it");
            Assert.Equal(1, SharedQuirks.ImportLearned(file!, theirs));
            Assert.Equal(1, QuirkDatabase.Parse(File.ReadAllText(theirs)).All.Single().Controls.Count);
            Assert.Throws<FormatException>(() => SharedQuirks.ImportLearned("{\"app\":\"SwitchMonitor\",\"kind\":\"profile\"}", theirs));
        }
        finally { Directory.Delete(dir, true); }
    }

    private static ShareScreen[] At(ShareRect[] rects, params (int X, int Y)[] offsets) =>
        rects.Select((r, i) => i < offsets.Length ? new ShareScreen(r, offsets[i].X, offsets[i].Y) : new ShareScreen(r, 0, 0)).ToArray();

    [Test]
    public static void TheMapDecidesWhereThePointerCrossesAndWhereItLands()
    {
        // This computer: two screens side by side (1920x1080 and 1366x768, tops aligned). The other: one 1920x1080 screen.
        ShareRect[] mine = [new(0, 0, 1920, 1080), new(1920, 0, 3286, 768)];
        ShareRect[] theirs = [new(0, 0, 1920, 1080)];

        // The other computer to the right of everything (origin at x = 3286): the right edge of screen 2 leads there; screen 1's does not.
        var land = ShareMap.Cross(At(mine), At(theirs, (3286, 0)), 3285, 400, 5, 0);
        Assert.True(land is not null, "right edge of screen 2");
        Assert.Equal(ShareMap.Inset, land!.Value.X, "comes in just inside its left edge");
        Assert.Equal(400, land.Value.Y, "at the same height");
        Assert.True(ShareMap.Cross(At(mine), At(theirs, (3286, 0)), 1000, 400, 5, 0) is null, "inside the screens: nothing happens");
        Assert.True(ShareMap.Cross(At(mine), At(theirs, (3286, 800)), 3285, 700, 5, 0) is null, "the other computer starts lower: nothing of it at this height");

        // The other computer ABOVE screen 1 (origin at 0,-1080): the top edge of screen 1 leads there, the top of screen 2 does not.
        Assert.True(ShareMap.Cross(At(mine), At(theirs, (0, -1080)), 500, 0, 0, -4) is { } up && up.Y == 1080 - 1 - ShareMap.Inset && up.X == 500, "top of screen 1");
        Assert.True(ShareMap.Cross(At(mine), At(theirs, (0, -1080)), 2500, 0, 0, -4) is null, "top of screen 2: no screen there");

        // And back, seen from the other computer: this one's screens are the "other" ones, at the opposite offsets.
        var returned = ShareMap.Cross(At(theirs), At(mine, (-3286, 0), (-3286, 0)), 0, 400, -5, 0);
        Assert.True(returned is not null && returned.Value.Y == 400 && returned.Value.X is >= 3200 and <= 3285 && returned.Value.Screen == 1, "left edge leads back to screen 2");

        // Screens of the other computer placed apart: each one has its own place (a second screen far above does not touch the first).
        ShareRect[] two = [new(0, 0, 1920, 1080), new(1920, 0, 3840, 1080)];
        var apart = At(two, (3286, 0), (3286, -3000));
        var toFirst = ShareMap.Cross(At(mine), apart, 3285, 400, 5, 0);
        Assert.True(toFirst is not null && toFirst.Value.Screen == 0, "meets the first screen of the other computer");
        Assert.True(ShareMap.Cross(At([new(0, 0, 100, 100)], (0, 0)), At(two, (100, -3000), (2020, -3000)), 99, 50, 5, 0) is null, "the second screen is nowhere near");

        // The pointer is on a screen that is not one of this computer's (a display that shows another computer): it leaves from the nearest.
        ShareRect[] real = [new(0, 0, 1920, 1080)];
        var fromAside = ShareMap.Cross(At(real), At(theirs, (1920, 0)), 2000, 500, 5, 0);
        Assert.True(fromAside is not null && fromAside.Value.Y == 500, "from beside the screen");
        Assert.Equal((1919, 500), ShareMap.Confine(real, 2500, 500), "put back on the nearest real screen");

        // Saved arrangements are read back (found by each screen's place and size, not by its number); a broken value is ignored.
        var arrangement = new ShareArrangement(new Dictionary<string, (int, int)> { [mine[1].Key] = (5, 6) }, new Dictionary<string, (int, int)> { [theirs[0].Key] = (3286, -20) }, 77);
        var saved = ShareMap.ParseLayout(ShareMap.ToJson(new Dictionary<string, ShareArrangement> { ["abc"] = arrangement }));
        Assert.Equal(3286, saved["abc"].TheirsAt(theirs[0]).X);
        Assert.Equal(6, saved["abc"].MineAt(mine[1]).Y);
        Assert.Equal(0, saved["abc"].MineAt(mine[0]).Y, "a screen it does not know sits at the origin");
        Assert.Equal(77, saved["abc"].At);
        Assert.Equal(-20, saved["abc"].Swapped().MineAt(theirs[0]).Y, "the other computer keeps the map the other way round");
        Assert.Equal(0, ShareMap.ParseLayout("not json").Count);

        // Nobody arranged them: the computers agree without talking (one puts the other on its right, the other on its left).
        var a = ShareMap.Default(mine, theirs, "aaa", "bbb");
        var b = ShareMap.Default(theirs, mine, "bbb", "aaa");
        Assert.Equal(3286, a.TheirsAt(theirs[0]).X);
        Assert.Equal(-3286, b.TheirsAt(mine[1]).X);
        // A screen that appears later is placed with the others.
        Assert.Equal(3286, new ShareArrangement(new Dictionary<string, (int, int)>(), new Dictionary<string, (int, int)>()).Completed(mine, theirs, a).TheirsAt(theirs[0]).X);
    }

    [Test]
    public static void DraggingAScreenSnapsToAnEdgeAndNeverOverlaps()
    {
        ShareRect[] fixedScreens = [new(0, 0, 1920, 1080)];
        ShareRect moving = new(0, 0, 1920, 1080);
        // Dropped a little past the right edge and a little low: it touches the edge and stays exactly as low as it was dropped.
        var (x, y) = ShareMap.Snap(fixedScreens, moving, 1950, 130, 60);
        Assert.Equal(1920, x, "touches the right edge");
        Assert.Equal(130, y, "any height along the edge");
        // Dropped on top of the other screen: pushed out the shortest way.
        var (px, py) = ShareMap.Snap(fixedScreens, moving, 1700, 100, 10);
        Assert.True(px >= 1920 || px <= -1920 || py >= 1080 || py <= -1080, "never over the other screen");
        // Dropped far away: left alone.
        Assert.Equal((4000, 3000), ShareMap.Snap(fixedScreens, moving, 4000, 3000, 60));
    }

    private static readonly System.Text.Json.Nodes.JsonObject[] None = [];
    private static readonly KeyValuePair<string, System.Text.Json.Nodes.JsonNode>[] Level =
        [new("brightness", System.Text.Json.Nodes.JsonValue.Create(40)!)];
    private static string NoEdid(string? _) => "";

    private static void At(long ms) => SceneSync.Clock = () => ms;

    [Test]
    public static void ProfilesJoinOneByOneAndTheNewestChangeWins()
    {
        string pictures = TempDir();
        try
        {
            // A has Work (old) and Night; B has Work (newer) and Game.
            At(1000);
            string a = SceneStore.Upsert(SceneStore.DefaultJson, null, "Work", null, [new("brightness", System.Text.Json.Nodes.JsonValue.Create(70)!)]);
            At(1500);
            a = SceneStore.Upsert(a, null, "Night", null, Level);
            At(2000);
            string b = SceneStore.Upsert(SceneStore.DefaultJson, null, "Work", null, [new("brightness", System.Text.Json.Nodes.JsonValue.Create(55)!)]);
            At(2100);
            b = SceneStore.Upsert(b, null, "Game", null, Level);

            var fromB = SceneSync.Snapshot(b, pictures, _ => null);
            var (merged, changed) = SceneSync.Merge(a, fromB, pictures, _ => null);
            Assert.True(changed, "B has something A lacks");
            var list = SceneStore.Parse(merged);
            Assert.Equal(3, list.Count, "Work, Night, Game");
            Assert.Equal(55.0, list.Single(s => s.Name == "Work").Settings[0].Percent ?? 0, "the newer Work wins");

            // The other way round: nothing newer than what B has, so only Night arrives.
            var fromA = SceneSync.Snapshot(a, pictures, _ => null);
            var (back, backChanged) = SceneSync.Merge(b, fromA, pictures, _ => null);
            Assert.True(backChanged);
            Assert.Equal(55.0, SceneStore.Parse(back).Single(s => s.Name == "Work").Settings[0].Percent ?? 0, "older Work does not replace");
            Assert.Equal(3, SceneStore.Parse(back).Count);
            // Again: nothing left to change (so two computers settle).
            Assert.True(!SceneSync.Merge(back, SceneSync.Snapshot(merged, pictures, _ => null), pictures, _ => null).Changed, "settled");
        }
        finally { Directory.Delete(pictures, true); SceneSync.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }

    [Test]
    public static void ADeletedProfileIsDeletedOnTheOtherComputerUnlessItWasChangedAfterwards()
    {
        string pictures = TempDir();
        try
        {
            At(1000);
            string a = SceneStore.Upsert(SceneStore.DefaultJson, null, "Night", null, Level);
            At(1100);
            a = SceneStore.Upsert(a, null, "Work", null, Level);
            string b = a;
            At(2000);
            a = SceneStore.Remove(a, "Night");
            var (b2, changed) = SceneSync.Merge(b, SceneSync.Snapshot(a, pictures, _ => null), pictures, _ => null);
            Assert.True(changed);
            Assert.Equal("Work", string.Join(",", SceneStore.Parse(b2).Select(s => s.Name)), "Night is gone on B too");
            // It does not come back from B (B's note says it was deleted).
            Assert.True(!SceneStore.Parse(SceneSync.Merge(a, SceneSync.Snapshot(b2, pictures, _ => null), pictures, _ => null).Json).Any(s => s.Name == "Night"), "stays deleted");

            // Changed on B after the deletion: it survives and returns to A.
            At(3000);
            string c = SceneStore.Upsert(b, "Night", "Night", null, Level);
            var (a2, back) = SceneSync.Merge(a, SceneSync.Snapshot(c, pictures, _ => null), pictures, _ => null);
            Assert.True(back);
            Assert.True(SceneStore.Parse(a2).Any(s => s.Name == "Night"), "newer than the deletion");

            // A renamed profile does not leave the old name behind.
            At(4000);
            string renamed = SceneStore.Upsert(c, "Night", "Evening", null, Level);
            var (b3, _) = SceneSync.Merge(c, SceneSync.Snapshot(renamed, pictures, _ => null), pictures, _ => null);
            Assert.Equal("Evening,Work", string.Join(",", SceneStore.Parse(b3).Select(s => s.Name).OrderBy(n => n)));
        }
        finally { Directory.Delete(pictures, true); SceneSync.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }

    [Test]
    public static void SyncedProfilesKeepLocalShortcutsAndFindTheirDisplayByEdid()
    {
        string pictures = TempDir();
        try
        {
            At(1000);
            string a = SceneStore.Upsert(SceneStore.DefaultJson, null, "Desk", "Odyssey", Level, monitorKey: "A-KEY");
            a = SceneStore.SetHotkeys(a, new Dictionary<string, Core.Hotkeys.HotkeyChord?> { ["Desk"] = Core.Hotkeys.HotkeyChord.TryParse("Ctrl+Alt+7", out var chord) ? chord : null });
            var sent = SceneSync.Snapshot(a, pictures, key => key == "A-KEY" ? "odyssey g5|h4zr" : null);
            Assert.True(!sent.ToJsonString().Contains("Ctrl+Alt+7"), "shortcuts are not sent");
            Assert.True(!sent.ToJsonString().Contains("A-KEY"), "the other computer's display key is not sent");

            // B has the same display under another key and its own profile with its own shortcut.
            At(500);
            string b = SceneStore.Upsert(SceneStore.DefaultJson, null, "Desk", "Odyssey", Level);
            b = SceneStore.SetHotkeys(b, new Dictionary<string, Core.Hotkeys.HotkeyChord?> { ["Desk"] = Core.Hotkeys.HotkeyChord.TryParse("Ctrl+Alt+8", out var mine) ? mine : null });
            var (merged, changed) = SceneSync.Merge(b, sent, pictures, edid => edid == "odyssey g5|h4zr" ? "B-KEY" : null);
            Assert.True(changed);
            var desk = SceneStore.Parse(merged).Single();
            Assert.Equal<string?>("B-KEY", desk.MonitorKey, "tied to the display here");
            Assert.Equal("Ctrl+Alt+8", desk.Hotkey?.ToString() ?? "", "own shortcut kept");

            // A computer without that display keeps the profile by name only.
            var (other, _) = SceneSync.Merge(SceneStore.DefaultJson, sent, pictures, _ => null);
            Assert.Equal<string?>(null, SceneStore.Parse(other).Single().MonitorKey);
            Assert.Equal<string?>("Odyssey", SceneStore.Parse(other).Single().MonitorFilter);
        }
        finally { Directory.Delete(pictures, true); SceneSync.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }

    [Test]
    public static void AProfileTheOtherComputerCannotReadIsSkippedAndTheRestJoins()
    {
        string pictures = TempDir();
        try
        {
            At(1000);
            var theirs = (System.Text.Json.Nodes.JsonObject)System.Text.Json.Nodes.JsonNode.Parse("""
                { "scenes": [
                    { "name": "Broken", "modified": 5000, "settings": { "no such control": 1 } },
                    { "name": "Good", "modified": 5000, "settings": { "brightness": 20 } } ], "removed": {} }
                """)!;
            var (json, changed) = SceneSync.Merge(SceneStore.DefaultJson, theirs, pictures, _ => null);
            Assert.True(changed);
            Assert.Equal("Good", string.Join(",", SceneStore.Parse(json).Select(s => s.Name)));
        }
        finally { Directory.Delete(pictures, true); SceneSync.Clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }
}
