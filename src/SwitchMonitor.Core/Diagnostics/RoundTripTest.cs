using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Diagnostics;

public sealed record RoundTripResult(string Monitor, string Feature, bool Passed, string Detail)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")}  {Monitor}: {Feature} - {Detail}";
}

/// <summary>
/// Hardware write check that leaves the monitor as it was: read a continuous control, move it by one step,
/// confirm the monitor reports the new value, then restore and confirm the original.
/// Only safe controls (brightness, contrast, volume) are exercised; power and input never are.
/// </summary>
public static class RoundTripTest
{
    public static readonly byte[] SafeCodes = [VcpCode.Brightness, VcpCode.Contrast, VcpCode.AudioVolume];

    public static IReadOnlyList<RoundTripResult> Run(IEnumerable<MonitorDevice> monitors, IDelay? delay = null) =>
        Run(monitors, SafeCodes, delay);

    /// <summary>Round-trips the given continuous codes; mapped vendor sliders (e.g. LG black stabilizer) are allowed.</summary>
    public static IReadOnlyList<RoundTripResult> Run(IEnumerable<MonitorDevice> monitors, IReadOnlyList<byte> codes, IDelay? delay = null)
    {
        delay ??= ThreadDelay.Instance;
        var results = new List<RoundTripResult>();
        foreach (var monitor in monitors)
        {
            if (!monitor.IsControllable) continue;
            var features = monitor.Features;
            foreach (byte code in codes)
            {
                var feature = features.FirstOrDefault(f => f.Code == code);
                if (feature is null || feature.Kind != FeatureKind.Continuous || feature.Definition.Risk != FeatureRisk.Safe) continue;
                results.Add(RunOne(monitor, code, delay, feature.Name, feature.Definition.Maximum));
            }
        }
        return results;
    }

    internal static RoundTripResult RunOne(MonitorDevice monitor, byte code, IDelay delay, string? label = null, uint? realMaximum = null)
    {
        string name = label ?? VcpCode.Describe(code);
        VcpValue original;
        try { original = monitor.Read(code); }
        catch (Exception error) { return new(monitor.Name, name, false, $"read failed: {error.Message}"); }
        if (original.Maximum == 0)
            return new(monitor.Name, name, false, "the monitor reports a maximum of 0");

        uint top = realMaximum ?? original.Maximum;
        uint target = original.Current < top ? original.Current + 1 : original.Current - 1;
        bool confirmed;
        try
        {
            monitor.Write(code, target);
            confirmed = WaitFor(monitor, code, target, delay);
        }
        catch (Exception error)
        {
            Restore(monitor, code, original.Current, delay);
            return new(monitor.Name, name, false, $"write failed: {error.Message}");
        }

        bool restored = Restore(monitor, code, original.Current, delay);
        string changed = $"{original.Current} -> {target} {(confirmed ? "confirmed" : "not confirmed")}";
        return new(monitor.Name, name, confirmed && restored,
            $"{changed}; {(restored ? $"restored to {original.Current}" : $"RESTORE NOT CONFIRMED (was {original.Current})")}");
    }

    private static bool Restore(MonitorDevice monitor, byte code, uint value, IDelay delay)
    {
        try
        {
            monitor.Write(code, value);
            return WaitFor(monitor, code, value, delay);
        }
        catch (Exception) { return false; }
    }

    private static bool WaitFor(MonitorDevice monitor, byte code, uint expected, IDelay delay)
    {
        // Up to ~4 s: the LG applies some writes a few seconds later.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            delay.Wait(TimeSpan.FromMilliseconds(Math.Min(100 + attempt * 100, 800)));
            try { if (monitor.Read(code).Current == expected) return true; }
            catch (Exception) { }
        }
        return false;
    }
}

/// <summary>
/// Visible write check for a choice control (picture mode, response time): select a value, wait for the
/// monitor to report it, then restore the original. Some monitors (LG) apply menu changes seconds later,
/// so confirmation polls for up to <see cref="Patience"/>.
/// </summary>
public static class ChoiceTrial
{
    public static TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(8);

    public static RoundTripResult Run(MonitorDevice monitor, byte code, uint target, IDelay? delay = null)
    {
        delay ??= ThreadDelay.Instance;
        var feature = monitor.Features.FirstOrDefault(f => f.Code == code);
        string name = feature?.Name ?? VcpCode.Describe(code);
        string Show(uint v) => feature?.Definition.DescribeValue(v) ?? $"0x{v:X2}";

        uint original;
        try { original = monitor.Read(code).Current; }
        catch (Exception error) { return new(monitor.Name, name, false, $"read failed: {error.Message}"); }
        if (original == target)
            return new(monitor.Name, name, false, $"already {Show(target)}; pick another value");

        bool reached;
        try
        {
            monitor.SetChoice(code, target);
            reached = WaitFor(monitor, code, target, delay, out _);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(monitor.Name, name, false, $"write refused: {error.Message}");
        }

        delay.Wait(TimeSpan.FromSeconds(3));   // leave the change on screen briefly
        bool restored;
        uint last = 0;
        try
        {
            monitor.SetChoice(code, original);
            restored = WaitFor(monitor, code, original, delay, out last);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new(monitor.Name, name, false, $"{Show(original)} -> {Show(target)} {(reached ? "confirmed" : "NOT confirmed")}; RESTORE FAILED: {error.Message}");
        }
        return new(monitor.Name, name, reached && restored,
            $"{Show(original)} -> {Show(target)} {(reached ? "confirmed" : "NOT confirmed")}; " +
            (restored ? $"restored to {Show(original)}" : $"RESTORE NOT CONFIRMED (reads {Show(last)})"));
    }

    private static bool WaitFor(MonitorDevice monitor, byte code, uint expected, IDelay delay, out uint last)
    {
        last = 0;
        var step = TimeSpan.FromMilliseconds(500);
        for (var waited = TimeSpan.Zero; waited <= Patience; waited += step)
        {
            try { last = monitor.Read(code).Current; if (last == expected) return true; }
            catch (Exception) { }
            delay.Wait(step);
        }
        return false;
    }
}
