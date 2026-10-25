using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Hotkeys;
using SwitchMonitor.Core.Inputs;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Settings;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

internal static class HotkeyTests
{
    [Test]
    public static void ParsesAndFormatsCommonShortcuts()
    {
        var chord = HotkeyChord.Parse("Ctrl+Alt+1");
        Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, chord.Modifiers);
        Assert.Equal(0x31u, chord.VirtualKey);
        Assert.Equal("Ctrl+Alt+1", chord.ToString());

        Assert.Equal(0x6Bu, HotkeyChord.Parse("ctrl+alt+numpadadd").VirtualKey);
        Assert.Equal("Ctrl+Alt+NumpadAdd", HotkeyChord.Parse("Alt+Ctrl+Add").ToString(), "canonical order and name");
        Assert.Equal(0x7Bu, HotkeyChord.Parse("Win+Shift+F12").VirtualKey);
        Assert.Equal(0x4Du, HotkeyChord.Parse("Ctrl+Alt+Shift+M").VirtualKey);
        Assert.Equal(0xBBu, HotkeyChord.Parse("Ctrl+Alt+Shift+Equals").VirtualKey, "the = / + key");
        Assert.Equal("Ctrl+Alt+Minus", HotkeyChord.Parse("Ctrl+Alt+OemMinus").ToString());
    }

    [Test]
    public static void RejectsUnsafeOrMalformedShortcuts()
    {
        foreach (string text in new[] { "", "M", "Shift+M", "Ctrl+Ctrl+M", "Ctrl+Alt+NotAKey", "Hyper+M", "Ctrl+Alt+" })
            Assert.True(!HotkeyChord.TryParse(text, out _), $"\"{text}\" should be rejected");
        Assert.Throws<FormatException>(() => HotkeyChord.Parse("Shift+1"));
    }
}

internal static class SettingsTests
{
    [Test]
    public static void DefaultsMatchVersionOne()
    {
        var settings = new AppSettings();
        Assert.Sequence(["Ctrl+Alt+NumpadAdd", "Ctrl+Alt+Shift+Plus"], settings.For(HotkeyAction.BrightnessUp).Select(c => c.ToString()));
        Assert.Sequence(["Ctrl+Alt+NumpadSubtract", "Ctrl+Alt+Minus"], settings.For(HotkeyAction.BrightnessDown).Select(c => c.ToString()));
        Assert.Sequence(["Ctrl+Alt+NumpadMultiply", "Ctrl+Alt+Shift+8"], settings.For(HotkeyAction.NextMonitor).Select(c => c.ToString()));
        Assert.Sequence(["Ctrl+Alt+M"], settings.For(HotkeyAction.NextInput).Select(c => c.ToString()));
        Assert.Sequence(["Ctrl+Alt+Shift+M"], settings.For(HotkeyAction.OpenWindow).Select(c => c.ToString()));
        Assert.Equal(1, settings.BrightnessStep, "one point per tap");
        Assert.Equal(0, settings.FindConflicts([]).Count);
    }

    [Test]
    public static void RoundTripsThroughJson()
    {
        var settings = new AppSettings
        {
            InputShortcuts = [new InputShortcut("key|0", 0x11, HotkeyChord.Parse("Ctrl+Alt+1"))],
            CycleInputs = new Dictionary<string, IReadOnlyList<int>> { ["key|0"] = [0x11, 0x0F] },
            BrightnessStep = 2
        };
        var back = AppSettings.Parse(settings.ToJson(), out bool upgraded);
        Assert.True(!upgraded, "current version");
        Assert.Equal(settings.InputShortcuts[0], back.InputShortcuts[0]);
        Assert.Sequence([0x11, 0x0F], back.CycleInputs["key|0"]);
        Assert.Equal(2, back.BrightnessStep);
        Assert.Sequence(settings.For(HotkeyAction.BrightnessUp), back.For(HotkeyAction.BrightnessUp));
    }

    [Test]
    public static void FirstPreviewFileIsUpgradedToVersionOneDefaults()
    {
        var settings = AppSettings.Parse("""
            { "version": 1, "hotkeys": { "brightnessUp": "Ctrl+Alt+NumpadAdd", "brightnessDown": "Ctrl+Alt+NumpadSubtract",
              "openWindow": "Ctrl+Alt+Shift+M" }, "brightnessStep": 5, "startMinimized": false }
            """, out bool upgraded);
        Assert.True(upgraded);
        Assert.Equal(2, settings.For(HotkeyAction.BrightnessUp).Count, "main keyboard alias added");
        Assert.Equal(1, settings.BrightnessStep);
    }

    [Test]
    public static void AcceptsSingleShortcutsListsAndDisabling()
    {
        var settings = AppSettings.Parse("""
            { "version": 2, "hotkeys": { "brightnessUp": "", "nextInput": ["Ctrl+Alt+N", "Ctrl+Alt+M"], "openWindow": "Ctrl+Alt+O" } }
            """);
        Assert.Equal(0, settings.For(HotkeyAction.BrightnessUp).Count);
        Assert.Equal(2, settings.For(HotkeyAction.NextInput).Count);
        Assert.Equal("Ctrl+Alt+O", settings.For(HotkeyAction.OpenWindow)[0].ToString());
        Assert.Equal(2, settings.For(HotkeyAction.BrightnessDown).Count, "missing keeps the default");
    }

    [Test]
    public static void RejectsBadValues()
    {
        Assert.Throws<FormatException>(() => AppSettings.Parse("""{ "version": 2, "hotkeys": { "brightnessUp": "Q" } }"""));
        Assert.Throws<FormatException>(() => AppSettings.Parse("""{ "version": 2, "hotkeys": { "jump": "Ctrl+Alt+J" } }"""));
        Assert.Throws<FormatException>(() => AppSettings.Parse("""{ "version": 2, "brightnessStep": 0 }"""));
        Assert.Throws<FormatException>(() => AppSettings.Parse("""{ "version": 2, "inputShortcuts": [ { "monitor": "k", "input": 17, "hotkey": "1" } ] }"""));
    }

    [Test]
    public static void AssignsCtrlAltDigitsToTheFirstDisplayOnce()
    {
        var settings = new AppSettings();
        var updated = settings.WithDefaultInputShortcuts("lg|0", [0x11, 0x12, 0x0F])!;
        Assert.Sequence(["Ctrl+Alt+1", "Ctrl+Alt+2", "Ctrl+Alt+3"], updated.InputShortcuts.Select(s => s.Chord.ToString()));
        Assert.Sequence([0x11, 0x12, 0x0F], updated.InputShortcuts.Select(s => s.InputCode));
        Assert.Equal<AppSettings?>(null, updated.WithDefaultInputShortcuts("other|0", [0x11]), "only the first display");
        var cleared = AppSettings.Parse((updated with { InputShortcuts = [] }).ToJson());
        Assert.Equal<AppSettings?>(null, cleared.WithDefaultInputShortcuts("lg|0", [0x11]), "removing all of them is respected");
        Assert.Equal(0, updated.FindConflicts([]).Count);
    }

    [Test]
    public static void FindsConflictsWithScenesAndInputs()
    {
        var settings = new AppSettings().WithDefaultInputShortcuts("lg|0", [0x11])!;
        var conflicts = settings.FindConflicts([("scene Night", HotkeyChord.Parse("Ctrl+Alt+Minus")), ("scene Work", HotkeyChord.Parse("Ctrl+Alt+1"))]);
        Assert.Equal(2, conflicts.Count);
        Assert.True(conflicts.Any(c => c.Contains("Brightness down") && c.Contains("scene Night")), string.Join(" | ", conflicts));
        Assert.True(conflicts.Any(c => c.Contains("scene Work")), string.Join(" | ", conflicts));
    }

    [Test]
    public static void CycleUsesMarkedInputsOnlyWhenTwoOrMore()
    {
        int[] all = [0x11, 0x12, 0x0F];
        var settings = new AppSettings();
        Assert.Sequence(all, settings.CycleFor("lg", all), "unmarked: every input");
        settings = settings.WithCycleInput("lg", all, 0x12, included: false);
        Assert.Sequence([0x11, 0x0F], settings.CycleFor("lg", all), "1.x: HDMI 2 skipped");
        settings = settings.WithCycleInput("lg", all, 0x0F, included: false);
        Assert.Sequence(all, settings.CycleFor("lg", all), "one mark is not a cycle");
        settings = settings.WithCycleInput("lg", all, 0x12, included: true);
        Assert.Sequence([0x11, 0x12], settings.CycleFor("lg", all), "kept in input order");
    }
}

internal static class FactoryDefaultsTests
{
    private static (FakeTransport Fake, MonitorDevice Monitor) Monitor()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(05 08 10 12 16 18 1A))" };
        fake.Values[0x10] = new VcpValue(30, 100);
        fake.Values[0x12] = new VcpValue(80, 100);
        fake.OnWrite = (f, code, value) =>
        {
            if (code == 0x05) { f.Values[0x10] = new VcpValue(70, 100); f.Values[0x12] = new VcpValue(60, 100); }
            else f.Values[code] = new VcpValue(value, 100);
        };
        return (fake, new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay()));
    }

    [Test]
    public static void RestoresTheWholeGroup()
    {
        var (fake, monitor) = Monitor();
        using (monitor)
        {
            Assert.True(monitor.CanRestore(DefaultsGroup.LuminanceContrast) && monitor.CanRestore(DefaultsGroup.Color));
            var after = monitor.RestoreDefaults(DefaultsGroup.LuminanceContrast);
            Assert.Equal(70u, after[0x10]);
            Assert.Equal(60u, fake.Values[0x12].Current);
            Assert.True(!monitor.Features.Any(f => f.Code is 0x05 or 0x08), "factory commands are never controls");
        }
    }

    [Test]
    public static void RestoresOneControlAndPutsTheOthersBack()
    {
        var (fake, monitor) = Monitor();
        using (monitor)
        {
            monitor.RestoreDefaults(DefaultsGroup.LuminanceContrast, only: 0x10);
            Assert.Equal(70u, fake.Values[0x10].Current, "brightness reset");
            Assert.Equal(80u, fake.Values[0x12].Current, "contrast kept");
            Assert.Equal<DefaultsGroup?>(DefaultsGroup.Color, MonitorDevice.GroupOf(0x18));
            Assert.Equal<DefaultsGroup?>(null, MonitorDevice.GroupOf(0x62), "volume has no factory command");
        }
    }

    [Test]
    public static void NotOfferedWithoutTheCommand()
    {
        using var monitor = new MonitorDevice(TestMonitors.Identity(), new FakeTransport { Capabilities = "(vcp(10 12))" }, null, null, new RecordingDelay());
        Assert.True(!monitor.CanRestore(DefaultsGroup.LuminanceContrast));
        Assert.Throws<NotSupportedException>(() => monitor.RestoreDefaults(DefaultsGroup.LuminanceContrast));
    }

    [Test]
    public static void LgColorsAreOnlyAdjustableInCustom()
    {
        const string lgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var fake = new FakeTransport { Capabilities = "(vcp(10 16 18 1A 15(01)))" };
        using var lg = new MonitorDevice(TestMonitors.Identity(lgId), fake, QuirkDatabase.LoadEmbedded().Match(lgId), null, new RecordingDelay());
        var red = lg.Features.Single(f => f.Code == 0x16);
        Assert.Equal("Red", red.Name);
        Assert.Sequence([(byte)0x15], red.Definition.EnabledWhen.Select(r => r.Code));
        Assert.Sequence([0x0Bu, 0x0Eu], red.Definition.EnabledWhen[0].Values, "picture modes Custom and Custom (Game)");
    }
}

internal static class DeepScanTests
{
    [Test]
    public static void ReadsCodesTheMonitorDoesNotAdvertiseButNeverCommands()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10 12))" };
        fake.Values[0x10] = new VcpValue(40, 100);
        fake.Values[0x12] = new VcpValue(50, 100);
        fake.Values[0x87] = new VcpValue(50, 100);   // sharpness, not advertised (LG 29WK600)
        fake.Values[0x04] = new VcpValue(0, 1);      // a reset command: must never be read
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());

        var normal = VcpScanner.Take(monitor);
        Assert.True(!normal.Values.ContainsKey(0x87), "normal snapshot: advertised codes only");

        var deep = VcpScanner.Take(monitor, deep: true);
        Assert.True(deep.Values.ContainsKey(0x87), "deep snapshot finds sharpness");
        Assert.True(!deep.Values.ContainsKey(0x04), "commands are never read");
        Assert.True(!deep.IsAdvertised(0x87) && deep.IsAdvertised(0x10));
        Assert.Equal(0, deep.Errors.Count, "unanswered extra codes are not errors");
        Assert.Equal(0, fake.Writes.Count, "read-only");

        var back = VcpScanner.FromJson(VcpScanner.ToJson(deep));
        Assert.True(!back.IsAdvertised(0x87), "advertised list survives the JSON round trip");

        var entries = VcpScanner.DeepScan(monitor);
        Assert.Sequence([(byte)0x10, (byte)0x12, (byte)0x87], entries.Select(e => e.Code));
        Assert.True(entries.All(e => e.Stable));
        Assert.True(VcpCode.IsNeverWritten(0xCC) && VcpCode.IsNeverWritten(0x60) && !VcpCode.IsNeverWritten(0x87));
        Assert.Equal("Sharpness", VcpCode.Describe(0x87));
    }
}

internal static class CandidateCheckTests
{
    private static MonitorDevice Lg(FakeTransport fake) =>
        new(TestMonitors.Identity(), fake, null, null, new RecordingDelay());

    [Test]
    public static void ACandidatePassesOnlyWithReadBackRestoreAndConfirmation()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10))" };
        fake.Values[0x87] = new VcpValue(50, 100);
        using var monitor = Lg(fake);
        uint? heldOnScreen = null;
        var result = CandidateTrial.Run(monitor, 0x87, _ => { heldOnScreen = fake.Values[0x87].Current; return true; }, new RecordingDelay());

        Assert.True(result.Passed, result.ToString());
        Assert.Equal(90u, heldOnScreen ?? 0u, "the test value stays on screen while the person answers");
        Assert.Equal(50u, fake.Values[0x87].Current, "restored");
        Assert.Sequence([((byte)0x87, 90u), ((byte)0x87, 50u)], fake.Writes);

        var notSeen = CandidateTrial.Run(monitor, 0x87, _ => false, new RecordingDelay());
        Assert.True(!notSeen.Passed && notSeen.ReadBackMatched && notSeen.Restored, "not seen = no proof");
    }

    [Test]
    public static void ALockedCandidateFailsAndIsStillRestored()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10))" };
        fake.Values[0x6C] = new VcpValue(50, 100);
        fake.OnWrite = (_, _, _) => { };   // accepted but ignored, as in a locked picture mode
        using var monitor = Lg(fake);
        var result = CandidateTrial.Run(monitor, 0x6C, _ => true, new RecordingDelay());
        Assert.True(!result.Passed && !result.ReadBackMatched && result.Restored, result.ToString());
    }

    [Test]
    public static void AWriteOnlyControlIsProvenByEyeBothWays()
    {
        // LG sharpness: the picture changes, but DDC and the menu keep reporting 50.
        var fake = new FakeTransport { Capabilities = "(vcp(10))" };
        fake.Values[0x87] = new VcpValue(50, 100);
        fake.OnWrite = (_, _, _) => { };
        using var monitor = Lg(fake);
        var questions = new List<string>();
        var result = CandidateTrial.Run(monitor, 0x87, q => { questions.Add(q); return true; }, new RecordingDelay(), step: 10, to: 10);
        Assert.True(result.WriteOnly && !result.Passed, result.ToString());
        Assert.Equal(2, questions.Count, "asked to see the change and to see it go back");
        Assert.Sequence([40u, 30u, 20u, 10u], fake.Writes.Take(4).Select(w => w.Value), "one menu step per write");
        Assert.Equal(50u, fake.Writes[^1].Value, "put back to 50");
    }

    [Test]
    public static void CommandsAndOsdCodesAreNeverWritten()
    {
        var fake = new FakeTransport { Capabilities = "(vcp(10))" };
        fake.Values[0xCC] = new VcpValue(0, 255);
        using var monitor = Lg(fake);
        var result = CandidateTrial.Run(monitor, 0xCC, _ => true, new RecordingDelay());
        Assert.True(!result.Passed);
        Assert.Equal(0, fake.Writes.Count, "OSD language is refused");
        Assert.Equal(10u, CandidateTrial.TargetFor(5, 10), "stays inside the range");
        Assert.Equal(60u, CandidateTrial.TargetFor(100, 100), "moves down near the top");
    }

    [Test]
    public static void MenuWatchNeedsOneRepeatableValuePerOption()
    {
        MenuObservation O(string name, uint? v) => new(name, new Dictionary<byte, uint?> { [0x0C] = v });
        Assert.True(MenuWatch.Tracks([O("Warm", 20), O("Medium", 35), O("Cool", 50), O("Warm", 20)], 0x0C, out _));
        Assert.True(!MenuWatch.Tracks([O("Warm", 35), O("Medium", 35), O("Cool", 35), O("Warm", 35)], 0x0C, out _), "never moves");
        Assert.True(!MenuWatch.Tracks([O("Warm", 20), O("Medium", 35), O("Warm", 22)], 0x0C, out _), "not repeatable");
        Assert.True(!MenuWatch.Tracks([O("Warm", 20), O("Medium", 35)], 0x0C, out _), "nothing repeated");
        Assert.True(!MenuWatch.Tracks([O("Warm", null), O("Warm", null)], 0x0C, out _), "no answer");
    }
}

internal static class ProfileEditingTests
{
    private const string Json = """
        { "version": 3, "scenes": [
          { "name": "Work", "hotkey": "Ctrl+Alt+W", "settings": { "brightness": 70, "contrast": 70 } },
          { "name": "Game", "settings": { "brightness": 100, "picture mode": ["FPS Game 1", "Games"] } } ] }
        """;

    private static FeatureDefinition Lg(byte code) =>
        QuirkDatabase.LoadEmbedded().All.SelectMany(q => q.Controls).First(c => c.Code == code);

    [Test]
    public static void ANewProfileIsAddedWithValuesWrittenByName()
    {
        var lines = new List<KeyValuePair<string, System.Text.Json.Nodes.JsonNode>>
        {
            new("Brightness", SceneStore.ValueNode(FeatureCatalog.Brightness, 40.4, null)),
            new("Picture mode", SceneStore.ValueNode(Lg(0x15), null, 0x0B)),
            new("Black level red", SceneStore.ValueNode(Lg(0x6C), 60, null))
        };
        string updated = SceneStore.Upsert(Json, null, " Reading ", "LG 29WK600", lines);
        var reading = SceneStore.Parse(updated).Single(s => s.Name == "Reading");
        Assert.Equal("LG 29WK600", reading.MonitorFilter);
        Assert.Equal(40.0, reading.Settings[0].Percent ?? -1, "rounded percentage");
        Assert.Equal<uint?>(0x0B, reading.Settings[1].Value, "LG Custom written as its name");
        Assert.True(updated.Contains("\"Custom\""), "values are saved by name, readable in the file");
        Assert.Equal(3, SceneStore.Parse(updated).Count);
    }

    [Test]
    public static void EditingKeepsTheShortcutAndUntouchedLists()
    {
        var game = SceneStore.RawSettings(Json, "game");
        Assert.Sequence(["brightness", "picture mode"], game.Select(g => g.Key));
        // Rename Game and change only the brightness: the picture-mode list must survive as written.
        var lines = new List<KeyValuePair<string, System.Text.Json.Nodes.JsonNode>>
        {
            new("brightness", SceneStore.ValueNode(FeatureCatalog.Brightness, 90, null)),
            new(game[1].Key, game[1].Value!)
        };
        string updated = SceneStore.Upsert(Json, "Game", "Games night", null, lines);
        var scenes = SceneStore.Parse(updated);
        Assert.Sequence(["Work", "Games night"], scenes.Select(s => s.Name), "renamed in place");
        Assert.Equal(1, scenes[1].Settings[1].Alternatives.Count, "FPS Game 1 then Games is kept");

        string work = SceneStore.Upsert(Json, "Work", "Work", null,
            [new("brightness", SceneStore.ValueNode(FeatureCatalog.Brightness, 55, null))]);
        Assert.Equal("Ctrl+Alt+W", SceneStore.Parse(work)[0].Hotkey?.ToString(), "shortcut kept");
    }

    [Test]
    public static void BadEditsAreRefusedAndDeleteRemovesTheProfile()
    {
        var one = new List<KeyValuePair<string, System.Text.Json.Nodes.JsonNode>>
            { new("brightness", SceneStore.ValueNode(FeatureCatalog.Brightness, 50, null)) };
        Assert.Throws<FormatException>(() => SceneStore.Upsert(Json, null, "work", null, one));   // name taken
        Assert.Throws<FormatException>(() => SceneStore.Upsert(Json, null, "  ", null, one));     // no name
        Assert.Throws<FormatException>(() => SceneStore.Upsert(Json, null, "Empty", null, []));   // nothing ticked
        Assert.Throws<FormatException>(() => SceneStore.Upsert(Json, null, "custom", null, one));  // Custom is the app's own
        var left = SceneStore.Parse(SceneStore.Remove(Json, "WORK"));
        Assert.Sequence(["Game"], left.Select(s => s.Name));
    }

    [Test]
    public static void LaptopPanelsAcceptTheNearestBrightnessLevel()
    {
        // N5050 over WMI: 15 % becomes 18 %, the closest level the panel has.
        var fake = new FakeTransport { Capabilities = "(vcp(10))", SnapsToLevels = true };
        fake.Values[0x10] = new VcpValue(50, 100);
        fake.OnWrite = (t, code, value) => t.Values[code] = new VcpValue(value == 15 ? 18u : value, 100);
        using var laptop = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        Assert.Equal(WriteOutcome.Verified, laptop.SetPercent(0x10, 15), "nearest level counts as done");

        var desktop = new FakeTransport { Capabilities = "(vcp(10))" };
        desktop.Values[0x10] = new VcpValue(50, 100);
        desktop.OnWrite = (t, code, value) => t.Values[code] = new VcpValue(value == 15 ? 18u : value, 100);
        using var monitor = new MonitorDevice(TestMonitors.Identity(), desktop, null, null, new RecordingDelay());
        Assert.Equal(WriteOutcome.Mismatch, monitor.SetPercent(0x10, 15), "a DDC monitor that keeps another value is still a mismatch");
    }

    [Test]
    public static void AdvancedControlsAndTheSwitch()
    {
        Assert.True(FeatureCatalog.IsAdvanced(0x16) && FeatureCatalog.IsAdvanced(0x6C) && FeatureCatalog.IsAdvanced(0x70));
        Assert.True(!FeatureCatalog.IsAdvanced(0x10) && !FeatureCatalog.IsAdvanced(0x87), "brightness and sharpness stay on Displays");
        var settings = AppSettings.Parse(new AppSettings { ShowAdvanced = true }.ToJson());
        Assert.True(settings.ShowAdvanced, "the Advanced switch is remembered");
        Assert.True(!AppSettings.Parse(new AppSettings().ToJson()).ShowAdvanced, "off by default");
    }
}

internal static class ProfileSwitchTests
{
    [Test]
    public static void ValuesTheMonitorAlreadyHasAreNotSentAgain()
    {
        const string lgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var fake = new FakeTransport { Capabilities = "(vcp(10 15(01 06) 62 8D(01 02)))" };
        fake.Values[0x10] = new VcpValue(100, 100);
        fake.Values[0x15] = new VcpValue(0x1C, 255);   // already FPS Game 1
        fake.Values[0x62] = new VcpValue(40, 100);
        fake.Values[0x8D] = new VcpValue(0x01, 2);     // already muted
        using var lg = new MonitorDevice(TestMonitors.Identity(lgId), fake, QuirkDatabase.LoadEmbedded().Match(lgId), null, new RecordingDelay());
        var game = SceneStore.Parse("""
            { "scenes": [ { "name": "Game", "settings": { "picture mode": "FPS Game 1", "mute": true, "brightness": 100, "volume": 60 } } ] }
            """)[0];
        var delay = new RecordingDelay();
        var steps = SceneApplier.Apply(game, [lg], delay: delay);

        Assert.Sequence([SceneStepStatus.AlreadySet, SceneStepStatus.AlreadySet, SceneStepStatus.AlreadySet, SceneStepStatus.Applied],
            steps.Select(s => s.Status));
        Assert.Sequence([((byte)0x62, 60u)], fake.Writes, "only the volume changed");
        Assert.True(!delay.Waits.Contains(SceneApplier.ModeSettle), "no mode change, so no wait");
    }
}

internal static class ShortcutEditingTests
{
    [Test]
    public static void OnlyShortcutsWithCtrlAltOrWinAreAllowed()
    {
        Assert.True(new HotkeyChord(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x31).IsAllowed);
        Assert.True(new HotkeyChord(HotkeyModifiers.Win, 0x4D).IsAllowed);
        Assert.True(!new HotkeyChord(HotkeyModifiers.Shift, 0x31).IsAllowed, "Shift alone");
        Assert.True(!new HotkeyChord(HotkeyModifiers.None, 0x31).IsAllowed, "bare key");
        Assert.True(!new HotkeyChord(HotkeyModifiers.Ctrl, 0xA2).IsAllowed, "a modifier key itself");
        Assert.Equal("Ctrl+Alt+Slash", new HotkeyChord(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0xBF).ToString());
    }

    [Test]
    public static void SceneShortcutsAreWrittenBackKeepingTheRest()
    {
        string json = """
            { "version": 2, "scenes": [
              { "name": "Work", "hotkey": "Ctrl+Alt+W", "settings": { "brightness": 70 } },
              { "name": "Night", "settings": { "brightness": 15, "color preset": "6500 K" } } ] }
            """;
        string updated = SceneStore.SetHotkeys(json, new Dictionary<string, HotkeyChord?>
        {
            ["work"] = null,
            ["Night"] = HotkeyChord.Parse("Ctrl+Alt+Shift+N"),
            ["Missing"] = HotkeyChord.Parse("Ctrl+Alt+X")
        });
        var scenes = SceneStore.Parse(updated);
        Assert.Equal<HotkeyChord?>(null, scenes[0].Hotkey, "removed");
        Assert.Equal("Ctrl+Alt+Shift+N", scenes[1].Hotkey?.ToString());
        Assert.Equal(15.0, scenes[1].Settings[0].Percent, "settings kept");
        Assert.Throws<FormatException>(() => SceneStore.SetHotkeys(json, new Dictionary<string, HotkeyChord?>
        {
            ["Work"] = HotkeyChord.Parse("Ctrl+Alt+1"),
            ["Night"] = HotkeyChord.Parse("Ctrl+Alt+1")
        }));
    }
}

internal static class InputCycleTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);

    [Test]
    public static void GoesToTheNextMarkedInput()
    {
        var decision = InputCycle.Decide([0x11, 0x0F], 0x11, null, Now);
        Assert.Equal<int?>(0x0F, decision.Target);
        Assert.Equal(CycleReason.Next, decision.Reason);
        Assert.Equal<int?>(0x11, InputCycle.Decide([0x11, 0x0F], 0x0F, null, Now).Target, "wraps around");
        Assert.Equal<int?>(0x11, InputCycle.Decide([0x11, 0x0F], 0x12, null, Now).Target, "unmarked current: first marked");
    }

    [Test]
    public static void PressingAgainWithinTenSecondsGoesBack()
    {
        var last = new InputSwitch(0x11, 0x0F, Now.AddSeconds(-4));
        var decision = InputCycle.Decide([0x11, 0x12, 0x0F], 0x0F, last, Now);
        Assert.Equal<int?>(0x11, decision.Target);
        Assert.Equal(CycleReason.ReturnWithinWindow, decision.Reason);
        Assert.Equal(CycleReason.Next, InputCycle.Decide([0x11, 0x12, 0x0F], 0x0F, last, Now.AddSeconds(7)).Reason, "after 10 s");
    }

    [Test]
    public static void UnreadableDisplayTriesTheSavedOriginOnce()
    {
        var last = new InputSwitch(0x11, 0x0F, Now.AddMinutes(-5));
        var decision = InputCycle.Decide([0x11, 0x0F], null, last, Now);
        Assert.Equal<int?>(0x11, decision.Target);
        Assert.Equal(CycleReason.ReturnToSavedOrigin, decision.Reason);
        var unknown = InputCycle.Decide([0x11, 0x0F], null, null, Now);
        Assert.Equal<int?>(null, unknown.Target, "never guesses");
    }

    [Test]
    public static void HistorySurvivesARestart()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sm-state-{Guid.NewGuid():N}.json");
        try
        {
            var history = new InputHistory(path);
            history.Record("lg|0", new InputSwitch(0x11, 0x0F, Now));
            var loaded = InputHistory.Load(path).Last("lg|0");
            Assert.Equal(new InputSwitch(0x11, 0x0F, Now), loaded);
            File.WriteAllText(path, "{ broken");
            Assert.Equal<InputSwitch?>(null, InputHistory.Load(path).Last("lg|0"), "damaged file is ignored");
        }
        finally { File.Delete(path); }
    }
}

internal static class ReturnWatchTests
{
    private static readonly DateTime Start = new(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);

    [Test]
    public static void ResendsAfterLosingContactAndTwoOriginalReads()
    {
        var watch = new InputReturnWatch(0x11, 0x0F, Start);
        Assert.Equal(ReturnWatchStep.Wait, watch.Step(0x0F, Start.AddSeconds(2)), "switch not visible yet");
        Assert.Equal(ReturnWatchStep.Wait, watch.Step(null, Start.AddSeconds(4)), "other computer has the monitor");
        Assert.Equal(ReturnWatchStep.Wait, watch.Step(0x11, Start.AddSeconds(30)), "first original read");
        Assert.Equal(ReturnWatchStep.Resend, watch.Step(0x11, Start.AddSeconds(32)));
        Assert.Equal(ReturnWatchStep.Stop, watch.Step(0x11, Start.AddSeconds(34)), "only once");
    }

    [Test]
    public static void StopsWhenItComesBackElsewhereOrWithoutLosingContact()
    {
        var other = new InputReturnWatch(0x11, 0x0F, Start);
        other.Step(null, Start.AddSeconds(2));
        Assert.Equal(ReturnWatchStep.Stop, other.Step(0x12, Start.AddSeconds(4)), "came back on HDMI 2");

        var clean = new InputReturnWatch(0x11, 0x0F, Start);
        Assert.Equal(ReturnWatchStep.Stop, clean.Step(0x11, Start.AddSeconds(2)), "never lost contact");

        var flicker = new InputReturnWatch(0x11, 0x0F, Start);
        flicker.Step(null, Start.AddSeconds(2));
        flicker.Step(0x11, Start.AddSeconds(4));
        Assert.Equal(ReturnWatchStep.Wait, flicker.Step(null, Start.AddSeconds(6)), "lost again resets the count");
        Assert.Equal(ReturnWatchStep.Wait, flicker.Step(0x11, Start.AddSeconds(8)));

        var old = new InputReturnWatch(0x11, 0x0F, Start);
        Assert.Equal(ReturnWatchStep.Stop, old.Step(null, Start.AddMinutes(31)), "times out");
    }

    [Test]
    public static void OnlyTheLgOnIntelNeedsIt()
    {
        const string lgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var quirk = QuirkDatabase.LoadEmbedded().Match(lgId);
        using var intel = new MonitorDevice(TestMonitors.Identity(lgId), new FakeTransport { Id = "intel-cui" }, quirk, null, new RecordingDelay());
        using var amd = new MonitorDevice(TestMonitors.Identity(lgId), new FakeTransport { Id = "amd-adl" }, quirk, null, new RecordingDelay());
        using var generic = new MonitorDevice(TestMonitors.Identity(), new FakeTransport { Id = "intel-cui" }, null, null, new RecordingDelay());
        Assert.True(intel.ResendsInputOnReturn, "LG on Intel");
        Assert.True(!amd.ResendsInputOnReturn, "LG on AMD");
        Assert.True(!generic.ResendsInputOnReturn, "other monitors");
    }

    [Test]
    public static void QuietReadReturnsNullInsteadOfThrowing()
    {
        var fake = new FakeTransport { FailRead = _ => new Transports.TransportException("fake", "busy") };
        using var monitor = new MonitorDevice(TestMonitors.Identity(), fake, null, null, new RecordingDelay());
        Assert.Equal<int?>(null, monitor.TryReadInputOnce());
        fake.FailRead = null;
        fake.Values[VcpCode.InputSource] = new VcpValue(0x11, 0);
        Assert.Equal<int?>(0x11, monitor.TryReadInputOnce());
    }
}

internal static class BrightnessStepTests
{
    private static readonly DateTime T = new(2026, 10, 24, 15, 0, 0, DateTimeKind.Utc);

    [Test]
    public static void TapsMoveOnePointAndTheThirdQuickTapJumps()
    {
        var steps = new BrightnessSteps();
        Assert.Equal(44, steps.Tap(43, +1, T));
        Assert.Equal(45, steps.Tap(44, +1, T.AddMilliseconds(100)));
        Assert.Equal(50, steps.Tap(45, +1, T.AddMilliseconds(200)), "third quick tap: next five-point mark");
        Assert.Equal(51, steps.Tap(50, +1, T.AddMilliseconds(900)), "slow tap: one point again");
        Assert.Equal(50, steps.Tap(51, -1, T.AddMilliseconds(950)), "direction change restarts the count");
        Assert.Equal(0, steps.Tap(0, -1, T.AddSeconds(5)), "clamped");
    }

    [Test]
    public static void HoldingMovesToFiveThenTenPointMarks()
    {
        Assert.Equal(45, BrightnessSteps.Hold(43, +1, TimeSpan.FromMilliseconds(320)));
        Assert.Equal(50, BrightnessSteps.Hold(45, +1, TimeSpan.FromMilliseconds(400)));
        Assert.Equal(60, BrightnessSteps.Hold(50, +1, TimeSpan.FromMilliseconds(600)));
        Assert.Equal(40, BrightnessSteps.Hold(43, -1, TimeSpan.FromMilliseconds(700)));
        Assert.Equal(30, BrightnessSteps.Hold(40, -1, TimeSpan.FromMilliseconds(900)));
        Assert.Equal(100, BrightnessSteps.Hold(97, +1, TimeSpan.FromSeconds(2)));
    }
}

internal static class SceneHotkeyTests
{
    [Test]
    public static void DefaultScenesLeaveCtrlAltDigitsToInputs()
    {
        var scenes = SceneStore.Parse(SceneStore.DefaultJson);
        Assert.True(scenes.All(s => s.Hotkey is null), "no scene shortcuts by default");
        var settings = new AppSettings().WithDefaultInputShortcuts("lg|0", [0x11, 0x12, 0x0F])!;
        Assert.Equal(0, settings.FindConflicts(scenes.Select(s => (s.Name, s.Hotkey))).Count);
    }

    [Test]
    public static void OnlyTheUneditedPreviewFileIsReplaced()
    {
        Assert.True(SceneStore.IsPreviewDefault(SceneStore.PreviewDefaultJson.Replace("  ", " ")), "whitespace ignored");
        Assert.True(!SceneStore.IsPreviewDefault(SceneStore.PreviewDefaultJson.Replace("70", "65")), "edited file kept");
        Assert.True(!SceneStore.IsPreviewDefault(SceneStore.DefaultJson));
    }

    [Test]
    public static void RejectsDuplicateOrInvalidSceneShortcuts()
    {
        Assert.Throws<FormatException>(() => SceneStore.Parse("""
            { "scenes": [ { "name": "A", "hotkey": "Ctrl+Alt+1", "settings": { "brightness": 1 } },
                          { "name": "B", "hotkey": "Alt+Ctrl+1", "settings": { "brightness": 2 } } ] }
            """));
        Assert.Throws<FormatException>(() => SceneStore.Parse("""
            { "scenes": [ { "name": "A", "hotkey": "1", "settings": { "brightness": 1 } } ] }
            """));
        var noHotkey = SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "brightness": 1 } } ] }""")[0];
        Assert.Equal<HotkeyChord?>(null, noHotkey.Hotkey);
    }

    [Test]
    public static void GameSceneUsesTheLgPictureModeOnlyOnTheLg()
    {
        const string lgId = @"MONITOR\GSM7714\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001";
        var game = SceneStore.Parse(SceneStore.DefaultJson)[2];
        Assert.Equal("Picture mode", game.Settings[1].Feature.Name);
        Assert.Equal<uint?>(0x1C, game.Settings[1].Value, "FPS Game 1");

        var lgFake = new FakeTransport { Capabilities = "(vcp(10 15(01 06)))" };
        lgFake.Values[0x10] = new VcpValue(50, 100);
        lgFake.Values[0x15] = new VcpValue(0x0B, 255);
        using var lg = new MonitorDevice(TestMonitors.Identity(lgId), lgFake, QuirkDatabase.LoadEmbedded().Match(lgId), null, new RecordingDelay());
        var delay = new RecordingDelay();
        var steps = SceneApplier.Apply(game, [lg], delay: delay);
        Assert.Sequence([SceneStepStatus.Applied, SceneStepStatus.Applied], steps.Select(s => s.Status));
        Assert.Equal(0x1Cu, lgFake.Values[0x15].Current);
        Assert.Sequence([(byte)0x15, (byte)0x10], lgFake.Writes.Select(w => w.Code), "picture mode before brightness");
        Assert.True(delay.Waits.Contains(SceneApplier.ModeSettle), "waits for the mode to load before setting brightness");

        // A standard monitor gets the MCCS Games mode; one whose vendor code 0x15 means something else is left alone.
        var otherFake = new FakeTransport { Capabilities = "(vcp(10 15(01 1C) DC(00 05)))" };
        otherFake.Values[0x10] = new VcpValue(50, 100);
        otherFake.Values[0x15] = new VcpValue(0x01, 255);
        otherFake.Values[0xDC] = new VcpValue(0x00, 0x05);
        using var other = new MonitorDevice(TestMonitors.Identity(), otherFake, null, null, new RecordingDelay());
        steps = SceneApplier.Apply(game, [other], delay: new RecordingDelay());
        Assert.Equal(SceneStepStatus.Applied, steps[1].Status);
        Assert.Equal("Games", steps[1].Setting.Feature.DescribeValue(steps[1].Setting.Value!.Value));
        Assert.Equal(0x01u, otherFake.Values[0x15].Current, "vendor 0x15 untouched");
        Assert.Equal(0x05u, otherFake.Values[0xDC].Current);
    }

    [Test]
    public static void OldNamesAndPreviewFilesStillWork()
    {
        var scene = SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "LG picture mode": "Cinema" } } ] }""")[0];
        Assert.Equal<uint?>(0x30, scene.Settings[0].Value, "alias of Picture mode");
        Assert.True(SceneStore.IsPreviewDefault(SceneStore.Preview2DefaultJson), "24/10 default is replaced");
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "picture mode": ["Nope"] } } ] }"""));
    }

    [Test]
    public static void TwoOptionControlsBecomeSwitches()
    {
        Assert.Equal<(uint, uint)?>((1u, 2u), FeatureCatalog.Mute.OnOffValues([1u, 2u]), "muted = on");
        var freeSync = QuirkDatabase.LoadEmbedded().All.SelectMany(q => q.Controls).Single(c => c.Name == "FreeSync");
        Assert.Equal<(uint, uint)?>((1u, 0u), freeSync.OnOffValues([0u, 1u]));
        Assert.Equal<(uint, uint)?>(null, FeatureCatalog.ColorPreset.OnOffValues([5u, 8u]), "6500 K / 9300 K is not on/off");
    }

    [Test]
    public static void ReadOnlyVendorControlsCannotBeInAScene()
    {
        Assert.Throws<FormatException>(() => SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "gamma": "Mode 1" } } ] }"""));
        var scene = SceneStore.Parse("""{ "scenes": [ { "name": "A", "settings": { "freesync": "On", "response time": "Fast" } } ] }""")[0];
        Assert.Sequence([0x01u, 0x02u], scene.Settings.Select(s => s.Value ?? 0));
    }
}
