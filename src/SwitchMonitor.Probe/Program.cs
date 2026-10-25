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
//                    Add --deep to also read the codes the monitor does not advertise.
//   --deep-scan [FILE] Read-only: every code 0x01-0xFF each monitor answers, read twice; optional JSON copy.
//   --diff A B       Compare two snapshot files and print the codes that changed.
//   --verify 87,6C --yes  WRITES: guided check of codes found by the deep scan. Each value is moved, read back,
//                    held on screen while the person answers, then restored. Never writes reset/input/power/OSD.
//   --watch 0C,0B --options Warm,Medium,Cool,Warm  Read-only: the person sets each option in the monitor's menu
//                    and the codes are read after each one, to see whether a code follows that setting.
//                    --to V moves to V (in steps of --step when given, e.g. 50 -> 40 -> 30). --step N moves by N (LG sharpness: 10). --under Warm,Cool repeats the check once per option
//                    the person sets in the monitor's menu (e.g. whether Color Temp locks red/green/blue).
//   --capture-presets Custom,Warm,Medium,Cool [--yes]  For each Color Temp the person picks in the monitor's menu:
//                    read 0x14 and red/green/blue over DDC, and ask for the values the menu shows. With --yes, then try
//                    to rebuild each preset in Custom with those values (always put back afterwards).
//   --report FILE    Also append the results of --verify / --watch to FILE.
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
    else if (args.Contains("--deep-scan"))
    {
        string? jsonPath = ArgValue(args, "--deep-scan") is string deepPath && !deepPath.StartsWith("--") ? deepPath : null;
        var snapshots = new List<VcpSnapshot>();
        foreach (var monitor in monitors.Where(m => m.IsControllable))
        {
            Console.WriteLine();
            Console.WriteLine($"{monitor.Name} [{monitor.TransportId}] - deep scan (read-only, two passes)...");
            var entries = VcpScanner.DeepScan(monitor);
            var known = monitor.Features.ToDictionary(f => f.Code);
            Console.WriteLine($"  {"Code",-6}{"Name",-30}{"Value",-16}{"Advertised",-12}Notes");
            foreach (var e in entries)
            {
                string name = known.TryGetValue(e.Code, out var f) ? f.Name : VcpCode.Describe(e.Code);
                string value = $"{e.Value.Current}/{e.Value.Maximum}";
                var notes = new List<string>();
                if (!e.Stable) notes.Add("changes by itself");
                if (e.NeverWritten) notes.Add("never written");
                if (known.ContainsKey(e.Code)) notes.Add("in the panel or mapped");
                Console.WriteLine($"  0x{e.Code:X2}  {name,-30}{value,-16}{(e.Advertised ? "yes" : "NO"),-12}{string.Join(", ", notes)}");
            }
            int extra = entries.Count(e => !e.Advertised);
            Console.WriteLine($"  {entries.Count} codes answered, {extra} of them not advertised.");
            snapshots.Add(VcpScanner.Take(monitor, deep: true));
        }
        if (jsonPath is not null)
        {
            File.WriteAllText(jsonPath, "[" + string.Join(",", snapshots.Select(VcpScanner.ToJson)) + "]");
            Console.WriteLine($"Snapshot saved to {jsonPath}");
        }
    }
    else if (ArgValue(args, "--verify") is string verifyList)
    {
        if (!args.Contains("--yes")) { Console.WriteLine("--verify writes to monitors; add --yes to confirm."); return 2; }
        uint? step = ArgValue(args, "--step") is string stepText ? uint.Parse(stepText) : null;
        uint? to = ArgValue(args, "--to") is string toText ? uint.Parse(toText) : null;
        string[] under = (ArgValue(args, "--under") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        exitCode = Verify(monitors, ParseCodes(verifyList), ArgValue(args, "--report"), step, under, to);
    }
    else if (ArgValue(args, "--capture-presets") is string presetList)
    {
        exitCode = CapturePresets(monitors, presetList.Split(',', StringSplitOptions.RemoveEmptyEntries), ArgValue(args, "--report"),
            args.Contains("--yes"));
    }
    else if (ArgValue(args, "--watch") is string watchList)
    {
        string[] menuOptions = (ArgValue(args, "--options") ?? "Warm,Medium,Cool,Warm").Split(',', StringSplitOptions.RemoveEmptyEntries);
        exitCode = Watch(monitors, ParseCodes(watchList), menuOptions, ArgValue(args, "--report"));
    }
    else if (ArgValue(args, "--snapshot") is string snapshotPath)
    {
        bool deep = args.Contains("--deep");
        var snapshots = monitors.Where(m => m.IsControllable).Select(m => VcpScanner.Take(m, deep)).ToArray();
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

static byte[] ParseCodes(string list) =>
    list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c => byte.Parse(c, System.Globalization.NumberStyles.HexNumber)).ToArray();

static void Report(string? path, string line)
{
    Console.WriteLine(line);
    if (path is not null) File.AppendAllText(path, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
}

static MonitorDevice? Answering(IReadOnlyList<MonitorDevice> monitors, byte code)
{
    foreach (var monitor in monitors.Where(m => m.IsControllable))
    {
        try { monitor.ReadOnce(code); return monitor; }
        catch (Exception) { }
    }
    return null;
}

static string Hint(byte code, uint target) => code switch
{
    0x87 => $"Nitidez enviada: {target}. Fijate en la IMAGEN, no en el menu: los bordes del texto deberian verse mas " +
            "suaves. (El menu del LG sigue mostrando 50 aunque el cambio se vea; eso ya lo sabemos.)",
    0x16 or 0x18 or 0x1A =>
        $"El programa acaba de subir el canal {(code == 0x16 ? "ROJO" : code == 0x18 ? "VERDE" : "AZUL")} a {target}. " +
        $"Abre el menu del monitor > Imagen > Ajustar color: si ese canal muestra {target} y la imagen cambio de tono " +
        "respecto a como estaba, responde s. Si el canal aparece en gris (bloqueado) o no cambio nada, responde n. " +
        "Cierra el menu antes de responder.",
    0x6C => "Mira las zonas negras (el fondo de esta ventana): deberian verse con un tono ROJIZO.",
    0x6E => "Mira las zonas negras (el fondo de esta ventana): deberian verse con un tono VERDOSO.",
    0x70 => "Mira las zonas negras (el fondo de esta ventana): deberian verse con un tono AZULADO.",
    _ => "Mira el monitor y el menu: algo deberia haber cambiado."
};

static bool? AskYesNo(string question)
{
    while (true)
    {
        Console.Write(question + " [s = si / n = no / o = omitir]: ");
        string answer = (Console.ReadLine() ?? "o").Trim().ToLowerInvariant();
        if (answer is "s" or "si" or "sí" or "y" or "yes") return true;
        if (answer is "n" or "no") return false;
        if (answer is "o" or "") return null;
    }
}

static int Verify(IReadOnlyList<MonitorDevice> monitors, byte[] codes, string? report, uint? step = null, string[]? under = null, uint? to = null)
{
    string[] rounds = under is { Length: > 0 } ? under : [""];
    int failures = 0;
    (MonitorDevice Monitor, byte Code, uint Original)? pending = null;
    // Closing with Ctrl+C must not leave a test value on the monitor.
    ConsoleCancelEventHandler onCancel = (_, e) =>
    {
        if (pending is { } p)
        {
            Console.WriteLine($"\nRestoring 0x{p.Code:X2} to {p.Original} before exiting...");
            CandidateTrial.Restore(p.Monitor, p.Code, p.Original, null, out uint _);
        }
    };
    Console.CancelKeyPress += onCancel;
    try
    {
        foreach (string round in rounds)
        {
        string label = round.Length > 0 ? $"[{round}] " : "";
        if (round.Length > 0)
        {
            Console.WriteLine();
            string about = round.ToLowerInvariant() switch
            {
                "warm" => " (calido)",
                "medium" => " (intermedio)",
                "cool" => " (frio, azulado)",
                "custom" => " (el modo normal, donde se ajustan los canales de color)",
                _ => ""
            };
            Console.WriteLine($"Ahora vamos a ver si con Temp. color = {round}{about} se pueden ajustar los canales de color.");
            Console.Write($"Pon \"{round}\" en el menu del monitor, cierra el menu y pulsa Enter (o escribe 'o' para omitir): ");
            if ((Console.ReadLine() ?? "").Trim().Equals("o", StringComparison.OrdinalIgnoreCase)) { Report(report, $"  {label}skipped"); continue; }
            Thread.Sleep(1500);
        }
        foreach (byte code in codes)
        {
            Console.WriteLine();
            var monitor = Answering(monitors, code);
            if (monitor is null) { Report(report, $"SKIP  {label}0x{code:X2} {VcpCode.Describe(code)} - no display answers it"); failures++; continue; }
            var result = CandidateTrial.Run(monitor, code,
                prompt =>
                {
                    if (prompt.StartsWith("RESTORED"))
                    {
                        Console.WriteLine(prompt);
                        Console.WriteLine("  El monitor no informa este valor, asi que solo tus ojos pueden confirmar que volvio.");
                        return AskYesNo("  ¿La imagen volvio a verse como al principio?");
                    }
                    Console.WriteLine(prompt);
                    uint target = uint.Parse(prompt.Split("-> ")[1].Split(' ')[0]);
                    Console.WriteLine("  " + Hint(code, target));
                    Console.WriteLine("  (El valor de prueba se queda en pantalla hasta que respondas; luego se restaura.)");
                    return AskYesNo("  ¿Ves el cambio?");
                },
                applied: original => pending = (monitor, code, original),
                settled: () => pending = null,
                step: step,
                to: to);
            Report(report, "  " + label + result);
            if (!result.Passed) failures++;
        }
        }
    }
    finally { Console.CancelKeyPress -= onCancel; }
    return failures == 0 ? 0 : 1;
}

static (uint R, uint G, uint B)? AskRgb(string question)
{
    while (true)
    {
        Console.Write(question);
        string text = (Console.ReadLine() ?? "").Trim();
        if (text.Length == 0) return null;
        var parts = text.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 && parts.All(p => uint.TryParse(p, out uint v) && v <= 100))
            return (uint.Parse(parts[0]), uint.Parse(parts[1]), uint.Parse(parts[2]));
        Console.WriteLine("  Escribe tres numeros de 0 a 100, por ejemplo 50,45,38, o solo Enter si no se ven.");
    }
}

static int CapturePresets(IReadOnlyList<MonitorDevice> monitors, string[] presets, string? report, bool rebuild)
{
    byte[] rgb = [VcpCode.RedGain, VcpCode.GreenGain, VcpCode.BlueGain];
    byte[] codes = [VcpCode.ColorPreset, .. rgb];
    var monitor = Answering(monitors, VcpCode.RedGain);
    if (monitor is null) { Report(report, "SKIP  no display answers red gain"); return 1; }
    string About(string p) => p.ToLowerInvariant() switch
    {
        "warm" => "calido", "medium" => "intermedio entre calido y frio", "cool" => "frio, azulado",
        "custom" => "el modo normal", _ => ""
    };

    // 1) Read-only: what the monitor reports and what its own menu shows for each factory Color Temp.
    Console.WriteLine();
    Console.WriteLine($"{monitor.Name}: solo lectura. Cada Temp. color la pone el fabricante; aqui solo la leemos.");
    var captured = new List<(string Preset, MenuObservation Ddc, (uint R, uint G, uint B)? Menu)>();
    foreach (string preset in presets)
    {
        Console.WriteLine();
        Console.Write($"Pon Temp. color = {preset} ({About(preset)}) en el menu del monitor, cierra el menu y pulsa Enter (o 'o' para omitir): ");
        if ((Console.ReadLine() ?? "").Trim().Equals("o", StringComparison.OrdinalIgnoreCase)) continue;
        Thread.Sleep(1500);
        var ddc = MenuWatch.Observe(monitor, preset, codes);
        Console.WriteLine("  El monitor informa por DDC: " +
            $"preset 0x14={ddc.Values[VcpCode.ColorPreset]?.ToString() ?? "-"}, rojo={ddc.Values[VcpCode.RedGain]?.ToString() ?? "-"}, " +
            $"verde={ddc.Values[VcpCode.GreenGain]?.ToString() ?? "-"}, azul={ddc.Values[VcpCode.BlueGain]?.ToString() ?? "-"}");
        var menu = AskRgb($"  Abre Imagen > Ajustar color. Si el menu muestra Rojo, Verde y Azul (aunque esten en gris), escribelos como R,G,B; si no se ven, Enter: ");
        captured.Add((preset, ddc, menu));
    }

    Report(report, $"  Color Temp presets on {monitor.Name}:");
    Report(report, $"    {"Preset",-10}{"0x14",-6}{"DDC R,G,B",-14}Menu R,G,B");
    foreach (var (preset, ddc, menu) in captured)
        Report(report, $"    {preset,-10}{(ddc.Values[VcpCode.ColorPreset]?.ToString() ?? "-"),-6}" +
            $"{string.Join(",", rgb.Select(c => ddc.Values[c]?.ToString() ?? "-")),-14}" +
            (menu is { } m ? $"{m.R},{m.G},{m.B}" : "not shown"));
    var ddcTriples = captured.Select(c => string.Join(",", rgb.Select(code => c.Ddc.Values[code]))).Distinct().Count();
    Report(report, ddcTriples > 1
        ? "  DDC reports different red/green/blue per preset: the preset can be read automatically."
        : "  DDC reports the same red/green/blue for every preset: presets can only be learned from the menu values.");

    // 2) Optional: rebuild each preset from its menu values and let the person compare.
    var custom = captured.FirstOrDefault(c => c.Preset.Equals("Custom", StringComparison.OrdinalIgnoreCase));
    if (!rebuild || custom.Preset is null) return 0;
    uint?[] originals = rgb.Select(c => custom.Ddc.Values[c]).ToArray();
    if (originals.Any(v => v is null)) { Report(report, "  Rebuild skipped: Custom red/green/blue could not be read."); return 1; }
    int failures = 0;
    void PutBack() { for (int i = 0; i < rgb.Length; i++) CandidateTrial.Restore(monitor, rgb[i], originals[i]!.Value, null, out uint _); }
    ConsoleCancelEventHandler onCancel = (_, _) => { Console.WriteLine("\nRestaurando rojo/verde/azul de Custom..."); PutBack(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        foreach (var (preset, _, menu) in captured)
        {
            if (menu is not { } values || preset.Equals("Custom", StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine();
            Console.Write($"Pon Temp. color = {preset} en el menu, mira bien el tono, cierra el menu y pulsa Enter: ");
            Console.ReadLine();
            uint[] target = [values.R, values.G, values.B];
            for (int i = 0; i < rgb.Length; i++) monitor.Write(rgb[i], target[i]);
            Thread.Sleep(1500);
            Console.WriteLine($"  El programa puso rojo/verde/azul = {values.R},{values.G},{values.B} (el menu pasara a Temp. color = Custom).");
            bool? same = AskYesNo($"  ¿El tono se ve igual que {preset}?");
            PutBack();
            Report(report, $"  {(same == true ? "PASS" : "FAIL")}  rebuild {preset} from {values.R},{values.G},{values.B}: " +
                (same switch { true => "looks the same", false => "looks different", null => "not checked" }));
            if (same != true) failures++;
        }
    }
    finally { Console.CancelKeyPress -= onCancel; }
    Console.WriteLine("Rojo/verde/azul de Custom restaurados. Deja Temp. color = Custom en el menu.");
    return failures == 0 ? 0 : 1;
}

static int Watch(IReadOnlyList<MonitorDevice> monitors, byte[] codes, string[] options, string? report)
{
    var monitor = Answering(monitors, codes[0]);
    if (monitor is null) { Report(report, $"SKIP  0x{codes[0]:X2} - no display answers it"); return 1; }
    Console.WriteLine();
    Console.WriteLine($"{monitor.Name}: solo lectura. Cambia cada opcion en el menu del monitor y pulsa Enter.");
    var observations = new List<MenuObservation>();
    foreach (string option in options)
    {
        Console.Write($"  Pon \"{option}\" en el menu del monitor, cierra el menu y pulsa Enter (o escribe 'o' para omitir): ");
        if ((Console.ReadLine() ?? "").Trim().Equals("o", StringComparison.OrdinalIgnoreCase)) continue;
        Thread.Sleep(1500);
        var seen = MenuWatch.Observe(monitor, option, codes);
        observations.Add(seen);
        Console.WriteLine("    " + string.Join("  ", seen.Values.Select(v => $"0x{v.Key:X2}={v.Value?.ToString() ?? "-"}")));
    }
    Report(report, $"  Menu watch on {monitor.Name}:");
    Report(report, $"    {"Option",-12}" + string.Join("", codes.Select(c => $"0x{c:X2}".PadRight(8))));
    foreach (var o in observations)
        Report(report, $"    {o.Setting,-12}" + string.Join("", codes.Select(c => (o.Values[c]?.ToString() ?? "-").PadRight(8))));
    int failures = 0;
    bool tracks = MenuWatch.Tracks(observations, codes[0], out string reason);
    Report(report, $"  {(tracks ? "PASS" : "FAIL")}  0x{codes[0]:X2} follows the menu option: {reason}");
    if (!tracks) failures++;
    return failures == 0 ? 0 : 1;
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
            string extra = a.IsAdvertised(change.Code) ? "" : "  (not advertised)";
            Console.WriteLine($"  {label,-16} {change}{named}{extra}");
            changed++;
        }
    }
    return changed > 0 ? 0 : 3;
}

