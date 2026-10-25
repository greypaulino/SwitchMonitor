using System.Globalization;
using System.Text.Json;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Diagnostics;

/// <summary>Every advertised VCP value of one display at one moment (read-only).</summary>
public sealed record VcpSnapshot(
    string MonitorKey,
    string MonitorName,
    DateTimeOffset Taken,
    IReadOnlyDictionary<byte, VcpValue> Values,
    IReadOnlyDictionary<byte, string> Errors)
{
    /// <summary>Codes listed in the monitor's capabilities string (a deep scan also reads the others).</summary>
    public IReadOnlyCollection<byte> Advertised { get; init; } = [];

    public bool IsAdvertised(byte code) => Advertised.Count == 0 || Advertised.Contains(code);
}

/// <summary>One code whose value differs between two snapshots.</summary>
public sealed record VcpChange(byte Code, uint? Before, uint? After, uint Maximum)
{
    public override string ToString() => $"0x{Code:X2}: {Show(Before)} -> {Show(After)}" + (Maximum > 0 ? $" (max {Maximum})" : "");
    private static string Show(uint? v) => v is uint x ? $"0x{x:X2} ({x})" : "unreadable";
}

/// <summary>
/// Learns what vendor controls do, for any monitor: snapshot every advertised code, let the user change one
/// setting with the monitor's own buttons, snapshot again and compare. Never writes.
/// </summary>
public static class VcpScanner
{
    /// <summary>
    /// Codes that are safe to read: the advertised ones, or with <paramref name="deep"/> every code 0x01-0xFF
    /// (monitors often answer codes they do not advertise: the LG 29WK600 answers sharpness 0x87). MCCS commands
    /// (resets, degauss ...) are write-only and never touched. Reading never changes a setting.
    /// </summary>
    public static IReadOnlyList<byte> ScannableCodes(MonitorDevice monitor, bool deep = false) =>
        (deep ? Enumerable.Range(1, 255).Select(c => (byte)c) : monitor.Capabilities.VcpCodes)
            .Where(code => !FeatureCatalog.IsCommand(code)).ToArray();

    public static VcpSnapshot Take(MonitorDevice monitor) => Take(monitor, deep: false);

    /// <param name="deep">Also read codes the monitor does not advertise (one attempt each, no retries).</param>
    public static VcpSnapshot Take(MonitorDevice monitor, bool deep)
    {
        var values = new SortedDictionary<byte, VcpValue>();
        var errors = new SortedDictionary<byte, string>();
        var advertised = monitor.Capabilities.VcpCodes.ToHashSet();
        foreach (byte code in ScannableCodes(monitor, deep))
        {
            try { values[code] = advertised.Contains(code) ? monitor.Read(code) : monitor.ReadOnce(code); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                if (advertised.Contains(code)) errors[code] = error.Message;   // unanswered extra codes are just absent
            }
        }
        return new VcpSnapshot(monitor.Key, monitor.Name, DateTimeOffset.Now, values, errors) { Advertised = advertised };
    }

    /// <summary>One row of a deep scan.</summary>
    public sealed record ScanEntry(byte Code, VcpValue Value, bool Advertised, bool Stable)
    {
        public bool NeverWritten => VcpCode.IsNeverWritten(Code);
    }

    /// <summary>
    /// Every code the monitor answers, read twice so values that move on their own (timers, frequencies) are flagged.
    /// Read-only, like ControlMyMonitor's full list but never touching write-only commands.
    /// </summary>
    public static IReadOnlyList<ScanEntry> DeepScan(MonitorDevice monitor)
    {
        var first = Take(monitor, deep: true);
        var second = Take(monitor, deep: true);
        return first.Values
            .Select(pair => new ScanEntry(pair.Key, pair.Value, first.Advertised.Contains(pair.Key),
                second.Values.TryGetValue(pair.Key, out var again) && again.Current == pair.Value.Current
                    && !FeatureCatalog.IsVolatile(pair.Key)))
            .ToArray();
    }

    /// <summary>Codes whose current value changed, ignoring timers and measured frequencies.</summary>
    public static IReadOnlyList<VcpChange> Diff(VcpSnapshot before, VcpSnapshot after)
    {
        var changes = new List<VcpChange>();
        foreach (byte code in before.Values.Keys.Union(after.Values.Keys).Order())
        {
            if (FeatureCatalog.IsVolatile(code)) continue;
            uint? a = before.Values.TryGetValue(code, out var va) ? va.Current : null;
            uint? b = after.Values.TryGetValue(code, out var vb) ? vb.Current : null;
            if (a == b) continue;
            changes.Add(new VcpChange(code, a, b, Math.Max(va.Maximum, vb.Maximum)));
        }
        return changes;
    }

    public static string ToJson(VcpSnapshot snapshot) => JsonSerializer.Serialize(new
    {
        monitorKey = snapshot.MonitorKey,
        monitorName = snapshot.MonitorName,
        taken = snapshot.Taken,
        values = snapshot.Values.ToDictionary(p => p.Key.ToString("X2"), p => new[] { p.Value.Current, p.Value.Maximum }),
        errors = snapshot.Errors.ToDictionary(p => p.Key.ToString("X2"), p => p.Value),
        advertised = snapshot.Advertised.Order().Select(c => c.ToString("X2")).ToArray()
    }, new JsonSerializerOptions { WriteIndented = true });

    public static VcpSnapshot FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var values = new SortedDictionary<byte, VcpValue>();
        foreach (var pair in root.GetProperty("values").EnumerateObject())
        {
            var array = pair.Value.EnumerateArray().Select(e => e.GetUInt32()).ToArray();
            values[byte.Parse(pair.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = new VcpValue(array[0], array[1]);
        }
        var errors = new SortedDictionary<byte, string>();
        if (root.TryGetProperty("errors", out var errorObject))
            foreach (var pair in errorObject.EnumerateObject())
                errors[byte.Parse(pair.Name, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = pair.Value.GetString() ?? "";
        var advertised = new HashSet<byte>();
        if (root.TryGetProperty("advertised", out var advertisedArray))
            foreach (var item in advertisedArray.EnumerateArray())
                advertised.Add(byte.Parse(item.GetString() ?? "", NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return new VcpSnapshot(
            root.GetProperty("monitorKey").GetString() ?? "",
            root.GetProperty("monitorName").GetString() ?? "",
            root.GetProperty("taken").GetDateTimeOffset(),
            values, errors) { Advertised = advertised };
    }
}
