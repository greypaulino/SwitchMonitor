using SwitchMonitor.Core.Displays;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Settings;

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
}
