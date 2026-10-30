using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Learning;

/// <summary>What one value did when written: the monitor kept it, rounded it to another, or ignored it.</summary>
public sealed record SweepResult(uint Written, uint? ReadBack)
{
    public bool Accepted => ReadBack == Written;
    public override string ToString() => Accepted ? $"{Written}: acepta"
        : ReadBack is uint other ? $"{Written}: lo cambia a {other}" : $"{Written}: no responde";
}

/// <summary>The value a setting has in one picture mode (read after switching to it).</summary>
public sealed record ModeValue(uint Mode, string ModeName, uint? Value);

/// <summary>Another code that moved when the setting was written (FreeSync also moves 0xF4 on the LG).</summary>
public sealed record LinkedChange(byte Code, uint? Before, uint? After);

/// <summary>
/// Measurements on a found setting that need no eyes: which values the monitor takes, what each picture mode uses,
/// whether other codes move with it. Every write goes through a <see cref="LearnSession"/>, so it is put back.
/// </summary>
public static class ValueStudy
{
    /// <summary>Values worth writing: the advertised list, every value of a short range, or steps across a long one.</summary>
    public static IReadOnlyList<uint> SweepValues(LearnCandidate candidate, uint step = 10)
    {
        if (candidate.AdvertisedValues.Count > 0) return candidate.AdvertisedValues;
        uint max = candidate.Value.Maximum;
        if (max <= 20) return Enumerable.Range(0, (int)max + 1).Select(v => (uint)v).ToArray();
        // Long ranges (some monitors report 65535): about 20 points across, never thousands of writes.
        step = Math.Max(Math.Max(1, step), max / 20);
        var values = new List<uint>();
        for (uint v = 0; v < max; v += step) values.Add(v);
        values.Add(max);
        // Odd values show whether the monitor rounds to steps (LG sharpness takes only multiples of 10).
        foreach (uint odd in new[] { 5u, max / 2 + 3 }) if (odd < max && !values.Contains(odd)) values.Add(odd);
        return values.Order().ToArray();
    }

    /// <summary>Writes each value, reads it back; the original comes back at the end.</summary>
    public static IReadOnlyList<SweepResult> Sweep(LearnSession session, byte code, IEnumerable<uint> values,
        Action<SweepResult>? progress = null, CancellationToken cancel = default)
    {
        var results = new List<SweepResult>();
        try
        {
            foreach (uint value in values)
            {
                cancel.ThrowIfCancellationRequested();
                var tried = session.Try(code, value);
                var result = new SweepResult(value, tried.ReadBack);
                results.Add(result);
                progress?.Invoke(result);
            }
        }
        finally { session.Revert(code); }
        return results;
    }

    /// <summary>Switches to each picture mode and reads the setting there (only the mode is written; it comes back).</summary>
    public static IReadOnlyList<ModeValue> ValuePerMode(LearnSession session, byte code, IDelay? delay = null,
        Action<ModeValue>? progress = null, CancellationToken cancel = default)
    {
        delay ??= ThreadDelay.Instance;
        var mode = SettingLearner.PictureMode(session.Monitor) ?? throw new InvalidOperationException("This display has no picture modes to switch.");
        var results = new List<ModeValue>();
        try
        {
            foreach (uint value in mode.AllowedValues)
            {
                cancel.ThrowIfCancellationRequested();
                session.Try(mode.Code, value);
                delay.Wait(LearnSession.ModeSettle);
                uint? read = null;
                try { read = session.Monitor.Read(code).Current; } catch (Exception error) when (error is not OutOfMemoryException) { }
                var result = new ModeValue(value, mode.Definition.DescribeValue(value), read);
                results.Add(result);
                progress?.Invoke(result);
            }
        }
        finally
        {
            session.Revert(mode.Code);
            delay.Wait(LearnSession.ModeSettle);
        }
        return results;
    }

    /// <summary>Reads <paramref name="watch"/>, writes <paramref name="value"/>, reads them again: the codes that moved.</summary>
    public static IReadOnlyList<LinkedChange> Links(LearnSession session, byte code, uint value, IEnumerable<byte> watch, IDelay? delay = null)
    {
        delay ??= ThreadDelay.Instance;
        var codes = watch.Where(c => c != code && !FeatureCatalog.IsVolatile(c)).Distinct().ToArray();
        var before = ReadAll(session.Monitor, codes);
        var changes = new List<LinkedChange>();
        try
        {
            session.Try(code, value);
            delay.Wait(TimeSpan.FromSeconds(1.5));
            var after = ReadAll(session.Monitor, codes);
            foreach (var (c, old) in before)
                if (after.GetValueOrDefault(c) != old) changes.Add(new LinkedChange(c, old, after.GetValueOrDefault(c)));
        }
        finally { session.Revert(code); }
        return changes;
    }

    private static Dictionary<byte, uint?> ReadAll(MonitorDevice monitor, IEnumerable<byte> codes)
    {
        var values = new Dictionary<byte, uint?>();
        foreach (byte c in codes)
        {
            try { values[c] = monitor.ReadOnce(c).Current; }
            catch (Exception error) when (error is not OutOfMemoryException) { values[c] = null; }
        }
        return values;
    }
}

public static class LearnedSettingsMerge
{
    /// <summary>
    /// Copies every learned control of <paramref name="sourceJson"/> (a quirks.user.json) into the file at
    /// <paramref name="destinationPath"/>, per monitor id; same code replaces. Checked before the file is written.
    /// </summary>
    public static int MergeInto(string sourceJson, string destinationPath)
    {
        var source = JsonNode.Parse(sourceJson) as JsonObject ?? throw new FormatException("Not a learned-settings file.");
        string? json = File.Exists(destinationPath) ? File.ReadAllText(destinationPath) : null;
        int count = 0;
        foreach (var monitor in (source["monitors"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string id = (string?)monitor["id"] ?? throw new FormatException("A monitor without id.");
            string name = (string?)monitor["name"] ?? id;
            string pnp = (monitor["pnpIds"] as JsonArray)?.Select(p => (string?)p).FirstOrDefault(p => p is { Length: 7 })
                ?? throw new FormatException($"{id} has no PnP ID.");
            var root = (json is null ? null : JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })) as JsonObject ?? new JsonObject { ["version"] = 1 };
            if (root["monitors"] is not JsonArray monitors) root["monitors"] = monitors = [];
            var entry = monitors.OfType<JsonObject>().FirstOrDefault(m => string.Equals((string?)m["id"], id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new JsonObject { ["id"] = id, ["name"] = name, ["pnpIds"] = new JsonArray(pnp) };
                monitors.Add(entry);
            }
            if (entry["controls"] is not JsonArray controls) entry["controls"] = controls = [];
            foreach (var control in (monitor["controls"] as JsonArray ?? []).OfType<JsonObject>())
            {
                string vcp = (string?)control["vcp"] ?? "";
                foreach (var old in controls.OfType<JsonObject>().Where(c => string.Equals((string?)c["vcp"], vcp, StringComparison.OrdinalIgnoreCase)).ToArray())
                    controls.Remove(old);
                controls.Add(control.DeepClone());
                count++;
            }
            json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        if (json is null) return 0;
        QuirkDatabase.Parse(json);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.WriteAllText(destinationPath + ".tmp", json);
        File.Move(destinationPath + ".tmp", destinationPath, overwrite: true);
        return count;
    }
}
