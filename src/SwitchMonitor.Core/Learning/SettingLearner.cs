using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Learning;

/// <summary>How a learned setting is shown: a 0..maximum slider, or a list of named values.</summary>
public enum LearnKind { Level, Options }

/// <summary>A code the monitor answers that the app does not know yet (found by <see cref="SettingLearner.Scan"/>).</summary>
public sealed record LearnCandidate(byte Code, VcpValue Value, bool Advertised, IReadOnlyList<uint> AdvertisedValues)
{
    /// <summary>A value list in the capabilities string, or a short range (0-10), reads as options; anything else as a level.</summary>
    public LearnKind GuessedKind => AdvertisedValues.Count > 0 || Value.Maximum is > 0 and <= 10 ? LearnKind.Options : LearnKind.Level;

    /// <summary>Values offered when the setting is a list: the advertised ones, or 0..maximum (at most 16 of them).</summary>
    public IReadOnlyList<uint> OptionValues => AdvertisedValues.Count > 0
        ? AdvertisedValues
        : Enumerable.Range(0, (int)Math.Min(Value.Maximum, 15) + 1).Select(v => (uint)v).ToArray();

    public string Label => $"VCP 0x{Code:X2}";

    /// <summary>What is known about the code (description, and whether it may be a setting).</summary>
    public CodeInfo Info { get; init; } = new("", CodeCategory.Setting);

    public bool IsSetting => Info.Category == CodeCategory.Setting;
}

/// <summary>What the monitor reported after one test write.</summary>
public sealed record LearnTry(byte Code, uint Written, uint? ReadBack)
{
    public bool Matched => ReadBack == Written;
}

/// <summary>Whether a setting kept a test value in one picture mode.</summary>
public sealed record ModeResult(uint Mode, string ModeName, bool? Works, string Detail);

/// <summary>The monitor stopped answering during a test: everything that could be put back was, and the wizard stops.</summary>
public sealed class LearnFaultException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Finding settings the app does not know yet, for any monitor: a read-only scan for codes it answers, then guarded
/// test writes that are always put back. Codes that could leave the person without a picture or wipe settings
/// (resets, power, input, OSD lock and language, timers) are never written.
/// </summary>
public static class SettingLearner
{
    /// <summary>Codes the wizard never offers nor writes, on any monitor.</summary>
    public static bool IsBlocked(byte code) =>
        VcpCode.IsNeverWritten(code) || FeatureCatalog.IsCommand(code) || FeatureCatalog.IsInformational(code)
        || FeatureCatalog.IsVolatile(code)
        || code is VcpCode.InputSource or VcpCode.PowerMode or 0xCA or 0xCC or 0xC8 or 0xF4 or 0x00;

    /// <summary>Codes this display already shows as a named control (standard, quirk or learned before).</summary>
    public static IReadOnlySet<byte> KnownCodes(MonitorDevice monitor)
    {
        var known = new HashSet<byte>(FeatureCatalog.All.Select(f => f.Code));
        foreach (var control in monitor.Quirk?.Controls ?? []) known.Add(control.Code);
        foreach (var feature in monitor.Features.Where(f => f.Definition.Risk != FeatureRisk.Unmapped)) known.Add(feature.Code);
        if (monitor.Quirk?.InputWriteVcp is byte input) known.Add(input);
        return known;
    }

    /// <summary>
    /// Reads every code 0x01-0xFF once (one attempt each), then the ones that answered a second time; codes whose value
    /// moved on its own, blocked codes and known codes are left out. Never writes.
    /// </summary>
    /// <param name="progress">Called with the code just read (1-255).</param>
    /// <remarks>
    /// Each code carries what is known about it (<see cref="CodeCatalog"/>). Codes known to reboot, reset or rewrite the
    /// monitor's memory are left out of the result (counted in <paramref name="hidden"/>); read-only information stays,
    /// marked as such, so it can be shown but is never offered for a test.
    /// </remarks>
    public static IReadOnlyList<LearnCandidate> Scan(MonitorDevice monitor, Action<int>? progress = null, CancellationToken cancel = default,
        ICollection<LearnCandidate>? hidden = null)
    {
        var advertised = monitor.Capabilities.VcpCodes.ToHashSet();
        var known = KnownCodes(monitor);
        var first = new Dictionary<byte, VcpValue>();
        for (int c = 1; c <= 255; c++)
        {
            cancel.ThrowIfCancellationRequested();
            byte code = (byte)c;
            progress?.Invoke(c);
            if (IsBlocked(code) || known.Contains(code)) continue;
            try { first[code] = monitor.ReadOnce(code); }
            catch (Exception error) when (error is not OutOfMemoryException and not OperationCanceledException) { }
        }
        var result = new List<LearnCandidate>();
        foreach (var (code, value) in first)
        {
            cancel.ThrowIfCancellationRequested();
            if (value.Maximum == 0) continue;
            VcpValue again;
            try { again = monitor.ReadOnce(code); }
            catch (Exception error) when (error is not OutOfMemoryException) { continue; }
            if (again.Current != value.Current) continue;   // moves by itself: a timer or a measurement
            var values = advertised.Contains(code)
                ? monitor.Capabilities.ValuesFor(code).Select(v => (uint)v).Order().ToArray()
                : [];
            var candidate = new LearnCandidate(code, value, advertised.Contains(code), values)
            {
                Info = CodeCatalog.Describe(code, monitor.Identity.PnpId, value.Maximum)
            };
            if (candidate.Info.Category == CodeCategory.Sensitive) hidden?.Add(candidate);
            else result.Add(candidate);
        }
        return result.OrderBy(c => c.IsSetting ? 0 : 1).ThenBy(c => c.Advertised ? 0 : 1).ThenBy(c => c.Code).ToArray();
    }

    /// <summary>The display's picture mode control, when it has one the wizard may switch (standard 0xDC or a quirk's).</summary>
    public static MonitorFeature? PictureMode(MonitorDevice monitor) =>
        monitor.Features.FirstOrDefault(f =>
            (f.Code == VcpCode.DisplayMode || f.Name.Contains("picture mode", StringComparison.OrdinalIgnoreCase))
            && f.Kind == FeatureKind.Choice && f.Definition.Risk == FeatureRisk.Safe && f.AllowedValues.Count > 1);

    internal static byte? PictureModeSafe(MonitorDevice monitor)
    {
        try { return PictureMode(monitor)?.Code; }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }
}

/// <summary>
/// One run of the wizard on one display. Every code it changes is remembered with its value before the first change and
/// is put back by <see cref="RevertAll"/> (also on Dispose), however the wizard ends. If the monitor stops answering, the
/// session puts back what it can and refuses any further write.
/// </summary>
public sealed class LearnSession(MonitorDevice monitor, IDelay? delay = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<byte, uint> originals = [];
    private readonly IDelay delay = delay ?? ThreadDelay.Instance;

    /// <summary>How long a picture mode gets to load before the setting is tested in it.</summary>
    public static TimeSpan ModeSettle { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long a test value must stay before it counts as kept (some monitors undo writes after a moment).</summary>
    public static TimeSpan KeepCheck { get; set; } = TimeSpan.FromSeconds(2);

    public MonitorDevice Monitor => monitor;
    public string? Fault { get; private set; }
    public bool IsFaulted => Fault is not null;

    /// <summary>Codes changed and not put back yet.</summary>
    public IReadOnlyCollection<byte> Changed { get { lock (gate) return originals.Keys.ToArray(); } }

    /// <summary>The value a code had before the wizard first touched it (read now if it was not touched yet).</summary>
    public uint Remember(byte code)
    {
        lock (gate)
        {
            Guard(code);
            if (originals.TryGetValue(code, out uint value)) return value;
            try { value = monitor.Read(code).Current; }
            catch (Exception error) when (error is not OutOfMemoryException) { throw Stop($"0x{code:X2} could not be read", error); }
            originals[code] = value;
            return value;
        }
    }

    /// <summary>Writes a test value and reads it back (waiting up to ~2 s, as some monitors apply writes late).</summary>
    public LearnTry Try(byte code, uint value)
    {
        lock (gate)
        {
            Remember(code);
            Write(code, value);
            return new LearnTry(code, value, ReadBack(code, value));
        }
    }

    /// <summary>Puts one code back to the value it had before the wizard.</summary>
    public bool Revert(byte code)
    {
        lock (gate)
        {
            if (!originals.TryGetValue(code, out uint value)) return true;
            bool back = Diagnostics.CandidateTrial.Restore(monitor, code, value, delay, out _);
            originals.Remove(code);
            return back;
        }
    }

    /// <summary>Puts every changed code back: picture mode first (it can reload the others), then the rest.</summary>
    public bool RevertAll()
    {
        lock (gate)
        {
            bool all = true;
            var mode = SettingLearner.PictureModeSafe(monitor);
            foreach (byte code in originals.Keys.OrderBy(c => c == mode ? 0 : 1).ToArray())
            {
                if (code == mode && originals.Count > 1) delay.Wait(ModeSettle);   // let the mode load before the rest
                all &= Revert(code);
            }
            return all;
        }
    }

    /// <summary>
    /// Switches through every picture mode and checks whether <paramref name="code"/> keeps a test value in each one;
    /// each mode's own value is put back before the next, and the original mode and value at the end.
    /// </summary>
    public IReadOnlyList<ModeResult> CheckModes(byte code, uint maximum, Action<ModeResult>? progress = null, CancellationToken cancel = default)
    {
        lock (gate)
        {
            var mode = SettingLearner.PictureMode(monitor) ?? throw new InvalidOperationException("This display has no picture modes to switch.");
            if (mode.Code == code) return [];
            Remember(code);
            Remember(mode.Code);
            var results = new List<ModeResult>();
            try
            {
                foreach (uint value in mode.AllowedValues)
                {
                    cancel.ThrowIfCancellationRequested();
                    string name = mode.Definition.DescribeValue(value);
                    Write(mode.Code, value);
                    delay.Wait(ModeSettle);
                    uint own;
                    try { own = monitor.Read(code).Current; }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        var unread = new ModeResult(value, name, null, "does not answer in this mode");
                        results.Add(unread);
                        progress?.Invoke(unread);
                        continue;
                    }
                    uint target = Diagnostics.CandidateTrial.TargetFor(own, maximum, Math.Max(1, Math.Min(Diagnostics.CandidateTrial.Step, maximum / 2)));
                    if (target == own) target = own == 0 ? Math.Min(1, maximum) : own - 1;
                    Write(code, target);
                    uint? first = ReadBack(code, target);
                    delay.Wait(KeepCheck);
                    uint? kept = null;
                    try { kept = monitor.Read(code).Current; } catch (Exception error) when (error is not OutOfMemoryException) { }
                    bool works = first == target && kept == target;
                    try { monitor.Write(code, own); ReadBack(code, own); } catch (Exception error) when (error is not OutOfMemoryException) { }
                    var result = new ModeResult(value, name, works,
                        works ? "keeps the change" : first == target ? $"goes back to {kept} by itself" : $"ignores the change (reads {first?.ToString() ?? "nothing"})");
                    results.Add(result);
                    progress?.Invoke(result);
                }
            }
            finally
            {
                Revert(mode.Code);
                delay.Wait(ModeSettle);
                Revert(code);
            }
            return results;
        }
    }

    public void Dispose() => RevertAll();

    private void Write(byte code, uint value)
    {
        Guard(code);
        try { monitor.Write(code, value); }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Stop($"0x{code:X2} refused the write", error); }
    }

    private uint? ReadBack(byte code, uint expected)
    {
        uint? last = null;
        int failures = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            delay.Wait(TimeSpan.FromMilliseconds(Math.Min(100 + attempt * 100, 600)));
            try
            {
                last = monitor.Read(code).Current;
                if (last == expected) return last;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (++failures >= 3) throw Stop($"the monitor stopped answering after a write to 0x{code:X2}", error);
            }
        }
        return last;
    }

    private void Guard(byte code)
    {
        if (Fault is not null) throw new LearnFaultException(Fault);
        if (SettingLearner.IsBlocked(code) || CodeCatalog.Describe(code, monitor.Identity.PnpId, 0).Category == CodeCategory.Sensitive)
            throw new InvalidOperationException($"VCP 0x{code:X2} is never written.");
    }

    private LearnFaultException Stop(string what, Exception error)
    {
        Fault = $"Stopped: {what}. Everything changed was put back where the monitor allowed it.";
        foreach (byte code in originals.Keys.ToArray())
        {
            Diagnostics.CandidateTrial.Restore(monitor, code, originals[code], delay, out _);
            originals.Remove(code);
        }
        return new LearnFaultException(Fault, error);
    }
}

/// <summary>A setting described by the person, ready to be kept for this monitor model.</summary>
public sealed record LearnedSetting(
    byte Code, string Name, LearnKind Kind, IReadOnlyList<(uint Value, string Name)> Values,
    bool NotAdvertised, bool WriteOnly = false, IReadOnlyList<uint>? Modes = null, byte? ModeCode = null);

/// <summary>
/// Keeps learned settings in quirks.user.json (merged over the built-in quirks at start), per monitor model:
/// the model's built-in quirk id when there is one, otherwise "learned-" and its PnP ID.
/// </summary>
public static class LearnedSettingsStore
{
    public static string QuirkId(MonitorDevice monitor) =>
        monitor.Quirk?.Id ?? $"learned-{(monitor.Identity.PnpId ?? throw new InvalidOperationException("This display does not report its model, so settings cannot be kept for it.")).ToLowerInvariant()}";

    /// <summary>The file's JSON with these settings added (same code: replaced). Checked by the quirk parser before it is returned.</summary>
    public static string Upsert(string? existing, string quirkId, string modelName, string pnpId, IEnumerable<LearnedSetting> settings)
    {
        var root = (existing is { Length: > 0 } ? JsonNode.Parse(existing, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true
        }) : null) as JsonObject ?? new JsonObject { ["version"] = 1 };
        if (root["monitors"] is not JsonArray monitors) root["monitors"] = monitors = [];
        var entry = monitors.OfType<JsonObject>().FirstOrDefault(m =>
            string.Equals((string?)m["id"], quirkId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            entry = new JsonObject { ["id"] = quirkId, ["name"] = modelName, ["pnpIds"] = new JsonArray(pnpId.ToUpperInvariant()) };
            monitors.Add(entry);
        }
        else if (entry["pnpIds"] is JsonArray ids && !ids.Any(i => string.Equals((string?)i, pnpId, StringComparison.OrdinalIgnoreCase)))
            ids.Add(pnpId.ToUpperInvariant());
        if (entry["controls"] is not JsonArray controls) entry["controls"] = controls = [];
        foreach (var setting in settings)
        {
            string vcp = setting.Code.ToString("X2");
            foreach (var old in controls.OfType<JsonObject>().Where(c => string.Equals((string?)c["vcp"], vcp, StringComparison.OrdinalIgnoreCase)).ToArray())
                controls.Remove(old);
            controls.Add(ToJson(setting));
        }
        string json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        QuirkDatabase.Parse(json);   // never keep a file the app could not read back
        return json;
    }

    public static void Save(MonitorDevice monitor, IEnumerable<LearnedSetting> settings, string? path = null)
    {
        path ??= QuirkDatabase.UserFilePath;
        string pnp = monitor.Identity.PnpId ?? throw new InvalidOperationException("This display does not report its model, so settings cannot be kept for it.");
        string? existing = File.Exists(path) ? File.ReadAllText(path) : null;
        string json = Upsert(existing, QuirkId(monitor), monitor.Name, pnp, settings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, overwrite: true);
    }

    private static JsonObject ToJson(LearnedSetting setting)
    {
        var control = new JsonObject
        {
            ["vcp"] = setting.Code.ToString("X2"),
            ["name"] = setting.Name.Trim(),
            ["kind"] = setting.Kind == LearnKind.Level ? "continuous" : "choice"
        };
        if (setting.Kind == LearnKind.Options)
        {
            var values = new JsonObject();
            foreach (var (value, name) in setting.Values) values[value.ToString("X2")] = name.Trim();
            control["values"] = values;
            control["capabilityValues"] = "ignore";   // only the values the person named
        }
        if (setting.NotAdvertised) control["notAdvertised"] = true;
        if (setting.WriteOnly && setting.Kind == LearnKind.Level) control["writeOnly"] = true;
        if (setting.Modes is { Count: > 0 } modes && setting.ModeCode is byte mode)
            control["enabledWhen"] = new JsonObject { [mode.ToString("X2")] = new JsonArray(modes.Select(m => (JsonNode)m.ToString("X2")).ToArray()) };
        control["learned"] = true;
        return control;
    }
}
