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
        Assert.Sequence([0x0Bu], red.Definition.EnabledWhen[0].Values, "picture mode Custom");
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
