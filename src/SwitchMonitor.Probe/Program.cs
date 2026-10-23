using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Scenes;
using SwitchMonitor.Core.Vcp;

// Diagnostic console.
//   (no arguments)   Read-only: displays, transport, capabilities, every control and its current value,
//                    plus a preview of each scene. Never writes to a monitor.
//   --roundtrip      WRITES: moves brightness, contrast and volume by one step and restores them.
//                    Never touches power or input. Requires --yes (the lab script asks first).
//   --snapshot FILE  Read-only: save every advertised VCP value of each active monitor to FILE (JSON).
//   --diff A B       Compare two snapshot files and print the codes that changed.
//   --log            Append the diagnostic log.

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (ArgValue(args, "--write-source") is string source)
    SwitchMonitor.Core.Transports.DdcWriteSettings.StandardWriteSource = byte.Parse(source, System.Globalization.NumberStyles.HexNumber);
var log = new MemoryLog();
var options = CoreOptions.FromDataDirectory();
var monitors = MonitorEnumerator.Enumerate(options, log: log);
int exitCode = 0;
try
{
    Console.WriteLine($"SwitchMonitor v2 probe - {monitors.Count} display(s), AMD writes {(options.AllowAmdWrites ? "enabled" : "disabled")}, " +
        $"write source 0x{SwitchMonitor.Core.Transports.DdcWriteSettings.StandardWriteSource:X2}");

    // --in-mode 15=1C : run the write tests with a choice control temporarily set (e.g. a game picture mode).
    (MonitorDevice Monitor, byte Code, uint Original)? modeOverride = null;
    if (ArgValue(args, "--in-mode") is string mode && args.Contains("--yes"))
    {
        var parts = mode.Split('=');
        byte modeCode = byte.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        uint modeValue = uint.Parse(parts[1], System.Globalization.NumberStyles.HexNumber);
        var target = monitors.FirstOrDefault(m => m.IsControllable && m.Features.Any(f => f.Code == modeCode));
        if (target is not null)
        {
            uint original = target.Read(modeCode).Current;
            target.SetChoice(modeCode, modeValue);
            for (int i = 0; i < 16 && target.Read(modeCode).Current != modeValue; i++) Thread.Sleep(500);
            Thread.Sleep(1500);
            modeOverride = (target, modeCode, original);
            Console.WriteLine($"Temporarily set {target.Name} 0x{modeCode:X2} = 0x{modeValue:X2} (was 0x{original:X2}).");
        }
    }
    try {
    if (ArgValue(args, "--diff") is string first)
    {
        string second = args[Array.IndexOf(args, "--diff") + 2];
        exitCode = PrintDiff(first, second);
    }
    else if (ArgValue(args, "--snapshot") is string snapshotPath)
    {
        var snapshots = monitors.Where(m => m.IsControllable).Select(VcpScanner.Take).ToArray();
        File.WriteAllText(snapshotPath, "[" + string.Join(",", snapshots.Select(VcpScanner.ToJson)) + "]");
        foreach (var snapshot in snapshots)
            Console.WriteLine($"  {snapshot.MonitorName}: {snapshot.Values.Count} values read, {snapshot.Errors.Count} unreadable");
        Console.WriteLine($"Snapshot saved to {snapshotPath}");
    }
    else if (ArgValue(args, "--try-choice") is string spec)
    {
        // --try-choice F7=02,15=30 --yes : visible write test, each value restored afterwards.
        if (!args.Contains("--yes")) { Console.WriteLine("--try-choice writes to monitors; add --yes to confirm."); return 2; }
        foreach (string item in spec.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=');
            byte code = byte.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
            uint value = uint.Parse(parts[1], System.Globalization.NumberStyles.HexNumber);
            foreach (var monitor in monitors.Where(m => m.IsControllable && m.Features.Any(f => f.Code == code)))
            {
                var result = ChoiceTrial.Run(monitor, code, value);
                Console.WriteLine("  " + result);
                if (!result.Passed) exitCode = 1;
            }
        }
        if (ArgValue(args, "--codes") is string sliderList)
        {
            byte[] sliders = sliderList.Split(',').Select(c => byte.Parse(c, System.Globalization.NumberStyles.HexNumber)).ToArray();
            foreach (var result in RoundTripTest.Run(monitors, sliders))
            {
                Console.WriteLine("  " + result);
                if (!result.Passed) exitCode = 1;
            }
        }
    }
    else if (args.Contains("--roundtrip"))
    {
        if (!args.Contains("--yes"))
        {
            Console.WriteLine("--roundtrip writes to monitors; add --yes to confirm.");
            return 2;
        }
        byte[] codes = ArgValue(args, "--codes") is string list
            ? list.Split(',').Select(c => byte.Parse(c, System.Globalization.NumberStyles.HexNumber)).ToArray()
            : RoundTripTest.SafeCodes;
        Console.WriteLine($"Round-trip write test ({string.Join(", ", codes.Select(c => c.ToString("X2")))}: +/-1 step, then restore)");
        foreach (var result in RoundTripTest.Run(monitors, codes))
        {
            Console.WriteLine("  " + result);
            if (!result.Passed) exitCode = 1;
        }
    }
    else
    {
        foreach (var monitor in monitors) Describe(monitor);
        PreviewScenes(monitors);
    }

    } finally {
        if (modeOverride is { } restore)
        {
            var (m, c, o) = restore;
            m.SetChoice(c, o);
            for (int i = 0; i < 16 && m.Read(c).Current != o; i++) Thread.Sleep(500);
            Console.WriteLine($"Restored {m.Name} 0x{c:X2} = 0x{m.Read(c).Current:X2} (expected 0x{o:X2}).");
        }
    }

    if (args.Contains("--log"))
    {
        Console.WriteLine();
        foreach (string entry in log.Entries) Console.WriteLine(entry);
    }
}
finally
{
    foreach (var monitor in monitors) monitor.Dispose();
}
return exitCode;

static void Describe(MonitorDevice monitor)
{
    var id = monitor.Identity;
    Console.WriteLine();
    Console.WriteLine($"{monitor.Name}{(id.IsPrimary ? " (main display)" : "")}{(id.IsBuiltIn ? " (built-in)" : "")}" +
        (monitor.IsActive ? "" : "  [OFF: Windows is not using this display]"));
    Console.WriteLine($"  Device:    {id.DeviceId}");
    Console.WriteLine($"  Source:    {(id.GdiName.Length > 0 ? id.GdiName : "-")}   PnP: {id.PnpId ?? "-"}");
    Console.WriteLine($"  Transport: {monitor.TransportId}{(monitor.Quirk is { } q ? $"   Quirk: {q.Id}" : "")}");
    if (!monitor.IsControllable) return;

    var caps = monitor.Capabilities;
    Console.WriteLine($"  Capabilities: {(caps.VcpCodes.Count > 0 ? caps.ToString() : "(not reported)")}");
    if (caps.Raw.Length > 0) Console.WriteLine($"  Raw:       {caps.Raw}");

    if (monitor.Inputs.Count > 0)
    {
        string current = "";
        try { current = monitor.DescribeInput((int)monitor.Read(VcpCode.InputSource).Current); }
        catch (Exception error) { current = $"read failed: {error.Message}"; }
        Console.WriteLine($"  Input:     {current}   (available: {string.Join(", ", monitor.Inputs.Select(monitor.DescribeInput))})");
    }

    foreach (var feature in monitor.Features)
    {
        string shown;
        try
        {
            var value = monitor.Read(feature.Code);
            uint top = feature.Definition.Maximum ?? value.Maximum;
            shown = feature.Kind == FeatureKind.Continuous
                ? $"{value.Current}/{top} ({(top == 0 ? 0 : Math.Round(value.Current * 100.0 / top, 1))}%)"
                : feature.Definition.DescribeValue(value.Current);
        }
        catch (Exception error) { shown = $"read failed: {error.Message}"; }
        string choices = feature.AllowedValues.Count > 0
            ? $"   (options: {string.Join(", ", feature.AllowedValues.Select(feature.Definition.DescribeValue))})"
            : "";
        string tag = feature.Definition.Risk switch
        {
            FeatureRisk.Unmapped => "   [unmapped, read-only]",
            FeatureRisk.ReadOnly => "   [read-only on this monitor]",
            _ => ""
        };
        Console.WriteLine($"  {feature.Name,-13} {shown}{choices}{tag}");
    }
}

static void PreviewScenes(IReadOnlyList<MonitorDevice> monitors)
{
    IReadOnlyList<SceneProfile> scenes;
    try { scenes = SceneStore.Parse(SceneStore.DefaultJson); }
    catch (FormatException error) { Console.WriteLine($"\nScenes: {error.Message}"); return; }
    Console.WriteLine();
    Console.WriteLine("Scene preview (default scenes, nothing is sent):");
    foreach (var scene in scenes)
        foreach (var step in SceneApplier.Preview(scene, monitors))
            Console.WriteLine($"  {scene.Name,-6} {step}");
}

static string? ArgValue(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static IReadOnlyList<VcpSnapshot> LoadSnapshots(string path)
{
    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
    return document.RootElement.EnumerateArray().Select(e => VcpScanner.FromJson(e.GetRawText())).ToArray();
}

int PrintDiff(string beforePath, string afterPath)
{
    var before = LoadSnapshots(beforePath);
    var after = LoadSnapshots(afterPath);
    int changed = 0;
    foreach (var a in before)
    {
        var b = after.FirstOrDefault(s => s.MonitorKey == a.MonitorKey);
        if (b is null) { Console.WriteLine($"{a.MonitorName}: missing from the second snapshot"); continue; }
        var monitor = monitors.FirstOrDefault(m => m.Key == a.MonitorKey);
        IReadOnlyList<MonitorFeature> features = monitor is null ? Array.Empty<MonitorFeature>() : monitor.Features;
        var changes = VcpScanner.Diff(a, b);
        Console.WriteLine($"{a.MonitorName}: {changes.Count} change(s)");
        foreach (var change in changes)
        {
            var feature = features.FirstOrDefault(f => f.Code == change.Code);
            string label = feature?.Name ?? VcpCode.Describe(change.Code);
            string named = feature is { Kind: not FeatureKind.Continuous } && change.After is uint after2
                ? $"  [{feature.Definition.DescribeValue(after2)}]" : "";
            Console.WriteLine($"  {label,-16} {change}{named}");
            changed++;
        }
    }
    return changed > 0 ? 0 : 3;
}

