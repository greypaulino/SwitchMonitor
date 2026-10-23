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
    IReadOnlyDictionary<byte, string> Errors);

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
    /// <summary>Advertised codes that are safe to read: everything except MCCS commands (resets, degauss ...).</summary>
    public static IReadOnlyList<byte> ScannableCodes(MonitorDevice monitor) =>
        monitor.Capabilities.VcpCodes.Where(code => !FeatureCatalog.IsCommand(code)).ToArray();

    public static VcpSnapshot Take(MonitorDevice monitor)
    {
        var values = new SortedDictionary<byte, VcpValue>();
        var errors = new SortedDictionary<byte, string>();
        foreach (byte code in ScannableCodes(monitor))
        {
            try { values[code] = monitor.Read(code); }
            catch (Exception error) when (error is not OutOfMemoryException) { errors[code] = error.Message; }
        }
        return new VcpSnapshot(monitor.Key, monitor.Name, DateTimeOffset.Now, values, errors);
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
        errors = snapshot.Errors.ToDictionary(p => p.Key.ToString("X2"), p => p.Value)
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
        return new VcpSnapshot(
            root.GetProperty("monitorKey").GetString() ?? "",
            root.GetProperty("monitorName").GetString() ?? "",
            root.GetProperty("taken").GetDateTimeOffset(),
            values, errors);
    }
}
