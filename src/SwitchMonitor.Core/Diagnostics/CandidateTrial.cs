using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Diagnostics;

/// <summary>Outcome of checking one candidate code: what was written, what the monitor reported and what the person saw.</summary>
public sealed record CandidateResult(
    string Monitor, byte Code, string Name, uint? Original, uint? Target,
    bool ReadBackMatched, bool Restored, bool? PersonSawIt, string Detail, bool? PersonSawRestore = null)
{
    /// <summary>All three proofs: the monitor reports the value, it went back, and the change was seen.</summary>
    public bool Passed => ReadBackMatched && Restored && PersonSawIt == true;

    /// <summary>
    /// The monitor applies the value but keeps reporting the old one (LG sharpness: the picture changes, the menu and
    /// DDC stay at 50). Proven only by the person seeing the change and seeing it go back.
    /// </summary>
    public bool WriteOnly => !ReadBackMatched && PersonSawIt == true && PersonSawRestore == true;

    public override string ToString() =>
        $"{(Passed ? "PASS" : WriteOnly ? "PASS (write-only)" : "FAIL")}  {Monitor}: 0x{Code:X2} {Name} - {Detail}";
}

/// <summary>
/// Guided check of a code the monitor answers but does not advertise (found by the deep scan).
/// The value is moved far enough to be visible, read back, held on screen while the person looks,
/// and always restored. Reset, input, power and OSD codes are refused.
/// </summary>
public static class CandidateTrial
{
    /// <summary>How far the value moves: large enough to see, still inside the reported range.</summary>
    public const uint Step = 40;

    public static uint TargetFor(uint current, uint maximum, uint step = Step)
    {
        if (maximum == 0) return current;
        return current + step <= maximum ? current + step : current >= step ? current - step : maximum;
    }

    /// <param name="ask">Shows a question while the test value is on screen; true = seen, false = not seen, null = skipped.</param>
    /// <param name="applied">Called with the original value just after the test value is written (for an emergency restore).</param>
    /// <param name="settled">Called once the original value is back.</param>
    /// <param name="step">Move by this much instead of <see cref="Step"/> (LG sharpness only takes steps of 10).</param>
    public static CandidateResult Run(MonitorDevice monitor, byte code, Func<string, bool?> ask,
        IDelay? delay = null, Action<uint>? applied = null, Action? settled = null, uint? step = null, uint? to = null)
    {
        delay ??= ThreadDelay.Instance;
        string name = VcpCode.Describe(code);
        if (VcpCode.IsNeverWritten(code))
            return new(monitor.Name, code, name, null, null, false, true, null, "refused: this code is never written");

        VcpValue original;
        try { original = monitor.Read(code); }
        catch (Exception error) { return new(monitor.Name, code, name, null, null, false, true, null, $"read failed: {error.Message}"); }
        if (original.Maximum == 0)
            return new(monitor.Name, code, name, original.Current, null, false, true, null, "the monitor reports a maximum of 0");

        uint target = to is uint fixedTarget && fixedTarget <= original.Maximum
            ? fixedTarget : TargetFor(original.Current, original.Maximum, step ?? Step);
        bool matched;
        uint seen;
        bool? sawIt = null;
        try
        {
            applied?.Invoke(original.Current);
            Walk(monitor, code, original.Current, target, step, delay);
            matched = WaitFor(monitor, code, target, delay, out seen);
            sawIt = ask($"0x{code:X2} {name}: {original.Current} -> {target}" +
                        (matched ? " (the monitor reports the new value)" : $" (the monitor still reports {seen})"));
        }
        catch (Exception error)
        {
            bool back = Restore(monitor, code, original.Current, delay, out _);
            settled?.Invoke();
            return new(monitor.Name, code, name, original.Current, target, false, back, null,
                $"write refused: {error.Message}; {(back ? "unchanged" : "RESTORE NOT CONFIRMED")}");
        }

        try { Walk(monitor, code, target, original.Current, step, delay, finalWrite: false); } catch (Exception) { }
        bool restored = Restore(monitor, code, original.Current, delay, out uint last);
        settled?.Invoke();
        // Write-only controls: the read-back cannot prove anything, so the person confirms the picture went back.
        bool? sawBack = !matched && sawIt == true
            ? ask($"RESTORED 0x{code:X2} {name}: back to {original.Current}")
            : null;
        string seenText = sawIt switch { true => "seen", false => "NOT seen", null => "not checked" };
        return new(monitor.Name, code, name, original.Current, target, matched, restored, sawIt,
            $"{original.Current} -> {target} {(matched ? "read back" : $"NOT read back (reads {seen})")}, {seenText}; " +
            (restored ? $"restored to {original.Current}" : $"RESTORE NOT CONFIRMED (reads {last}, was {original.Current})") +
            (sawBack switch { true => ", seen going back", false => ", NOT seen going back", null => "" }), sawBack);
    }

    /// <summary>
    /// Writes the values between <paramref name="from"/> and <paramref name="to"/> one step at a time (40 -> 30 for
    /// 50 -> 30 with step 10), for controls that only accept one menu step per write. Without a step, one write.
    /// </summary>
    private static void Walk(MonitorDevice monitor, byte code, uint from, uint to, uint? step, IDelay delay, bool finalWrite = true)
    {
        if (step is uint size && size > 0)
        {
            uint value = from;
            while (to > value ? to - value > size : value - to > size)
            {
                value = to > value ? value + size : value - size;
                monitor.Write(code, value);
                WaitFor(monitor, code, value, delay, out _);
            }
        }
        if (finalWrite) monitor.Write(code, to);
    }

    public static bool Restore(MonitorDevice monitor, byte code, uint value, IDelay? delay, out uint last)
    {
        last = 0;
        try
        {
            monitor.Write(code, value);
            return WaitFor(monitor, code, value, delay ?? ThreadDelay.Instance, out last);
        }
        catch (Exception) { return false; }
    }

    private static bool WaitFor(MonitorDevice monitor, byte code, uint expected, IDelay delay, out uint last)
    {
        // Up to ~4 s: the LG applies some writes a few seconds later.
        last = 0;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            delay.Wait(TimeSpan.FromMilliseconds(Math.Min(100 + attempt * 100, 800)));
            try { last = monitor.Read(code).Current; if (last == expected) return true; }
            catch (Exception) { }
        }
        return false;
    }
}

/// <summary>One reading of the watched codes after the person set something in the monitor's own menu.</summary>
public sealed record MenuObservation(string Setting, IReadOnlyDictionary<byte, uint?> Values);

/// <summary>
/// Read-only check of whether a code follows a menu setting (for example 0x0C against Warm / Medium / Cool):
/// the person sets each option in the monitor's menu, the codes are read after each one, and the first option is
/// repeated at the end to prove the reading is repeatable.
/// </summary>
public static class MenuWatch
{
    public static MenuObservation Observe(MonitorDevice monitor, string setting, IReadOnlyList<byte> codes)
    {
        var values = new Dictionary<byte, uint?>();
        foreach (byte code in codes)
        {
            try { values[code] = monitor.Read(code).Current; }
            catch (Exception) { values[code] = null; }
        }
        return new MenuObservation(setting, values);
    }

    /// <summary>
    /// The code tracks the menu when every distinct option gives a distinct value and the repeated option
    /// (same name, observed again) gives the same value both times.
    /// </summary>
    public static bool Tracks(IReadOnlyList<MenuObservation> observations, byte code, out string reason)
    {
        var byName = observations.GroupBy(o => o.Setting, StringComparer.OrdinalIgnoreCase).ToList();
        if (observations.Any(o => o.Values.GetValueOrDefault(code) is null))
        {
            reason = "did not answer every time";
            return false;
        }
        foreach (var group in byName)
            if (group.Select(o => o.Values[code]).Distinct().Count() > 1)
            {
                reason = $"\"{group.Key}\" gave different values";
                return false;
            }
        var perOption = byName.Select(g => g.First().Values[code]).ToList();
        if (perOption.Distinct().Count() < perOption.Count)
        {
            reason = "two different options gave the same value";
            return false;
        }
        if (byName.Count == observations.Count)
        {
            reason = "no option was repeated, so the reading is not proven repeatable";
            return false;
        }
        reason = "one value per option, repeatable";
        return true;
    }
}
