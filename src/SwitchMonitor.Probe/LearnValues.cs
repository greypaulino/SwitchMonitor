using SwitchMonitor.Core;
using SwitchMonitor.Core.Learning;
using SwitchMonitor.Core.Vcp;

/// <summary>
/// --learn-values: guided study of the settings the app does not know yet, on the real monitor.
/// Measures without eyes (values the monitor takes, value per picture mode, codes that move with it, modes where it
/// can change), then names each value with the person: from the monitor's menu to the app, or from the app to the menu.
/// Every change is put back. The result is a learned-settings file the app reads (quirks.user.json format).
/// </summary>
internal static class LearnValues
{
    private static readonly string[] Functions =
    [
        "Brightness", "Contrast", "Sharpness", "Color temperature", "Picture mode", "Response time", "FreeSync",
        "Volume", "Gamma", "Black level", "Saturation", "Hue"
    ];

    private static readonly string[] ValueNames = ["Off", "On", "Normal", "Fast", "Faster", "Low", "Medium", "High"];

    private static string? report;

    private static string ValueHint => "\n  Atajos: " + string.Join("  ", ValueNames.Select((n, i) => $"{i + 1}={n}"));

    public static int Run(IReadOnlyList<MonitorDevice> monitors, string? reportPath, string outPath, byte[]? only)
    {
        report = reportPath;
        var monitor = monitors.FirstOrDefault(m => m.IsControllable);
        if (monitor is null) { Say("No hay ninguna pantalla que se pueda controlar."); return 1; }
        if (monitor.Identity.PnpId is null) { Say($"{monitor.Name} no informa su modelo: no se puede guardar lo aprendido."); return 1; }
        Say($"Pantalla: {monitor.Name} [{monitor.TransportId}] modelo {monitor.Identity.PnpId}");
        Say("Paso 1: buscando ajustes que la app no conoce (solo lectura, ~1 minuto)...");
        var hidden = new List<LearnCandidate>();
        var all = SettingLearner.Scan(monitor, c => { if (c % 32 == 0) Console.Write($"\r  0x{c:X2} de 0xFF"); }, default, hidden);
        Console.WriteLine();
        foreach (var c in all.Where(c => !c.IsSetting)) Say($"  (solo lectura) {c.Label}: {c.Info.Description}");
        foreach (var c in hidden) Say($"  (oculto, sensible) {c.Label}: {c.Info.Description}");
        IReadOnlyList<LearnCandidate> found = all.Where(c => c.IsSetting).ToArray();
        if (only is { Length: > 0 }) found = found.Where(c => only.Contains(c.Code)).ToArray();
        if (found.Count == 0) { Say("No hay ajustes nuevos: todo lo que responde ya lo conoce la app."); return 0; }
        foreach (var c in found) Say($"  {c.Label}  ahora {c.Value.Current}/{c.Value.Maximum}  {(c.Advertised ? "anunciado" : "no anunciado")}  {c.Info.Description}");
        var watch = found.Select(c => c.Code).Concat(monitor.Features.Select(f => f.Code)).Distinct().ToArray();

        var learned = new List<LearnedSetting>();
        using var session = new LearnSession(monitor);
        try
        {
            foreach (var candidate in found)
            {
                Say("");
                Say($"=== {candidate.Label} (ahora {candidate.Value.Current} de {candidate.Value.Maximum})");
                string answer = Ask("Estudiar este ajuste? [s] si, [n] saltar, [q] terminar", "s");
                if (answer == "q") break;
                if (answer != "s") continue;
                var setting = Study(session, candidate, watch);
                if (setting is not null) learned.Add(setting);
            }
        }
        catch (LearnFaultException error) { Say(error.Message); }
        finally { session.RevertAll(); }

        if (learned.Count == 0) { Say("Nada que guardar."); return 0; }
        LearnedSettingsStore.Save(monitor, learned, outPath);
        Say("");
        Say($"Guardado: {learned.Count} ajuste(s) en {outPath}");
        foreach (var s in learned)
            Say($"  0x{s.Code:X2} {s.Name}: {(s.Kind == LearnKind.Level ? "nivel" : string.Join(", ", s.Values.Select(v => $"{v.Value}={v.Name}")))}" +
                (s.Modes is { } m ? $"  (solo en modos {string.Join(",", m.Select(x => x.ToString("X2")))})" : ""));
        return 0;
    }

    private static LearnedSetting? Study(LearnSession session, LearnCandidate candidate, byte[] watch)
    {
        var monitor = session.Monitor;
        byte code = candidate.Code;

        // A first look: one change, put back after a few seconds.
        uint size = Math.Max(1, Math.Min(40, candidate.Value.Maximum / 2));
        uint target = SwitchMonitor.Core.Diagnostics.CandidateTrial.TargetFor(candidate.Value.Current, candidate.Value.Maximum, size);
        var first = session.Try(code, target);
        Say($"  Prueba: {candidate.Value.Current} -> {target} ({(first.Matched ? "el monitor lo confirma" : $"el monitor sigue diciendo {first.ReadBack}")}). Mira la pantalla...");
        Thread.Sleep(3000);
        session.Revert(code);
        if (Ask("  Viste algun cambio en la pantalla? [s/n]", "n") != "s")
        {
            Say("  Sin cambio visible: se deja fuera.");
            return null;
        }

        // Measurements without eyes.
        IReadOnlyList<SweepResult> sweep = [];
        if (Ask("  Barrer sus valores (escribe cada uno, lo lee y lo devuelve)? [s/n]", "s") == "s")
        {
            var values = ValueStudy.SweepValues(candidate);
            Say($"  Barrido de {values.Count} valores...");
            sweep = ValueStudy.Sweep(session, code, values, r => Say($"    {r}"));
            var rounded = sweep.Where(r => !r.Accepted && r.ReadBack is not null).ToArray();
            if (rounded.Length > 0) Say($"  Redondea {rounded.Length} valor(es): puede que solo acepte pasos.");
        }
        var mode = SettingLearner.PictureMode(monitor);
        IReadOnlyList<ModeValue> perMode = [];
        IReadOnlyList<ModeResult> modeChecks = [];
        if (mode is not null && mode.Code != code)
        {
            Say($"  Valor en cada modo de imagen ({mode.AllowedValues.Count} modos, la pantalla puede parpadear)...");
            perMode = ValueStudy.ValuePerMode(session, code, progress: v => Say($"    {v.ModeName}: {v.Value?.ToString() ?? "no responde"}"));
            if (first.Matched)
            {
                Say("  En que modos se puede cambiar...");
                modeChecks = session.CheckModes(code, candidate.Value.Maximum, r => Say($"    {r.ModeName}: {r.Detail}"));
            }
        }
        var links = ValueStudy.Links(session, code, target, watch);
        Say(links.Count == 0 ? "  No mueve otros codigos."
            : "  Al cambiarlo tambien cambian: " + string.Join(", ", links.Select(l => $"0x{l.Code:X2} {l.Before}->{l.After?.ToString() ?? "?"}")));

        // Naming, with the person.
        string name = AskFunction();
        bool options = candidate.GuessedKind == LearnKind.Options;
        options = Ask($"  Es un nivel (barra 0-{candidate.Value.Maximum}) o una lista de opciones? [n] nivel, [o] opciones", options ? "o" : "n") == "o";
        var named = new List<(uint, string)>();
        if (options)
        {
            string how = Ask("  Como nombrar los valores? [m] cambio en el menu del monitor y la app lee, [a] la app cambia y yo leo el menu", "m");
            named = how == "a" ? FromApp(session, code, candidate, sweep) : FromMenu(monitor, code);
            if (named.Count == 0) { Say("  Ningun valor con nombre: se deja fuera."); return null; }
        }

        var works = modeChecks.Where(r => r.Works == true).Select(r => r.Mode).ToArray();
        bool restricted = works.Length > 0 && works.Length < modeChecks.Count(r => r.Works is not null);
        Report("");
        Report($"=== 0x{code:X2} = {name} ({(options ? "opciones" : "nivel")})");
        foreach (var r in sweep) Report($"  barrido {r}");
        foreach (var v in perMode) Report($"  modo {v.ModeName}: {v.Value}");
        foreach (var r in modeChecks) Report($"  cambiable en {r.ModeName}: {r.Detail}");
        foreach (var l in links) Report($"  ligado 0x{l.Code:X2}: {l.Before} -> {l.After}");
        foreach (var (v, n) in named) Report($"  valor {v} = {n}");
        return new LearnedSetting(code, name, options ? LearnKind.Options : LearnKind.Level, named,
            NotAdvertised: !candidate.Advertised, WriteOnly: !first.Matched && !options,
            Modes: restricted ? works : null, ModeCode: restricted ? mode?.Code : null);
    }

    /// <summary>The person sets each option in the monitor's own menu; the app reads which value it gave.</summary>
    private static List<(uint, string)> FromMenu(MonitorDevice monitor, byte code)
    {
        var named = new List<(uint, string)>();
        Say("  En el menu del monitor, pon una opcion de este ajuste, CIERRA el menu, y escribe aqui el nombre que mostraba.");
        Say("  Repite con cada opcion. Al final repite la PRIMERA para comprobar. Enter vacio = terminar." + ValueHint);
        while (true)
        {
            string label = Ask("  Nombre de la opcion puesta (Enter = terminar)", "");
            if (label.Length == 0) break;
            Thread.Sleep(3000);   // the LG applies menu changes a moment after the menu closes
            uint value;
            try { value = monitor.Read(code).Current; }
            catch (Exception error) { Say($"  No se pudo leer: {error.Message}"); continue; }
            var same = named.FirstOrDefault(n => string.Equals(n.Item2, label, StringComparison.OrdinalIgnoreCase));
            if (same.Item2 is not null)
            {
                Say(same.Item1 == value ? $"  Comprobado: {label} vuelve a dar {value}." : $"  OJO: {label} antes dio {same.Item1} y ahora {value}.");
                continue;
            }
            if (named.Any(n => n.Item1 == value)) Say($"  OJO: el valor {value} ya tenia nombre; puede que este codigo no siga a ese menu.");
            Say($"  {label} = {value}");
            named.Add((value, label));
        }
        return named;
    }

    /// <summary>The app sets each value the monitor accepts; the person reads its name in the monitor's menu.</summary>
    private static List<(uint, string)> FromApp(LearnSession session, byte code, LearnCandidate candidate, IReadOnlyList<SweepResult> sweep)
    {
        var values = sweep.Count > 0 ? sweep.Where(r => r.Accepted).Select(r => r.Written).ToList() : candidate.OptionValues.ToList();
        var named = new List<(uint, string)>();
        Say("  La app pone cada valor. Abre el menu del monitor, mira el nombre de la opcion y escribelo (Enter vacio = sin nombre)." + ValueHint);
        try
        {
            foreach (uint value in values)
            {
                var tried = session.Try(code, value);
                string label = Ask($"  Valor {value}{(tried.Matched ? "" : $" (el monitor dice {tried.ReadBack})")}: nombre en el menu", "");
                if (label.Length > 0) named.Add((value, label));
            }
        }
        finally { session.Revert(code); }
        return named;
    }

    private static string AskFunction()
    {
        Say("  Que es? " + string.Join("  ", Functions.Select((f, i) => $"[{i + 1}] {f}")) + "  [0] Otro");
        while (true)
        {
            string answer = Ask("  Numero", "0");
            if (int.TryParse(answer, out int n) && n >= 1 && n <= Functions.Length) return Functions[n - 1];
            if (answer == "0")
            {
                string custom = Ask("  Nombre del ajuste", "");
                if (custom.Length > 0) return custom;
            }
        }
    }

    private static string Ask(string question, string fallback)
    {
        Console.Write($"{question}{(fallback.Length > 0 ? $" ({fallback})" : "")}: ");
        string answer = (Console.ReadLine() ?? "").Trim();
        // Value names: a number picks from the list (1 Off ... 8 High).
        if (question.Contains("nombre", StringComparison.OrdinalIgnoreCase) && int.TryParse(answer, out int pick) && pick >= 1 && pick <= ValueNames.Length
            && !question.Contains("ajuste", StringComparison.OrdinalIgnoreCase))
            answer = ValueNames[pick - 1];
        return answer.Length == 0 ? fallback : answer.ToLowerInvariant() is "s" or "n" or "q" or "m" or "a" or "o" ? answer.ToLowerInvariant() : answer;
    }

    private static void Say(string line) => Report(line);

    private static void Report(string line)
    {
        Console.WriteLine(line);
        if (report is not null) File.AppendAllText(report, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }
}
