using System.Text.Json;
using System.Text.Json.Nodes;

namespace SwitchMonitor.Core.Inputs;

/// <summary>The last input change on one display: where it came from and where it went.</summary>
public sealed record InputSwitch(int Origin, int Target, DateTime AtUtc);

/// <summary>
/// Last input change per display, kept in state.json so "Return to previous input" survives a restart (1.x behavior).
/// </summary>
public sealed class InputHistory
{
    private readonly Dictionary<string, InputSwitch> last = [];
    private readonly string? path;

    public InputHistory(string? path = null) => this.path = path;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "state.json");

    public InputSwitch? Last(string monitorKey) => last.GetValueOrDefault(monitorKey);

    public void Record(string monitorKey, InputSwitch change)
    {
        last[monitorKey] = change;
        Save();
    }

    public static InputHistory Load(string? path = null)
    {
        path ??= DefaultPath;
        var history = new InputHistory(path);
        if (!File.Exists(path)) return history;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("lastInput", out var entries))
                foreach (var entry in entries.EnumerateObject())
                    history.last[entry.Name] = new InputSwitch(
                        entry.Value.GetProperty("origin").GetInt32(),
                        entry.Value.GetProperty("target").GetInt32(),
                        entry.Value.GetProperty("at").GetDateTime().ToUniversalTime());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            // A damaged state file only loses the "previous input"; settings live elsewhere.
        }
        return history;
    }

    private void Save()
    {
        if (path is null) return;
        var entries = new JsonObject();
        foreach (var (key, change) in last)
            entries[key] = new JsonObject { ["origin"] = change.Origin, ["target"] = change.Target, ["at"] = change.AtUtc };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JsonObject { ["version"] = 1, ["lastInput"] = entries }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum CycleReason
{
    /// <summary>Next marked input after the current one.</summary>
    Next,

    /// <summary>Pressed again within <see cref="InputCycle.ReturnWindow"/>: back to where the last switch came from.</summary>
    ReturnWithinWindow,

    /// <summary>The display no longer answers (another computer has it): one attempt to go back to the saved origin.</summary>
    ReturnToSavedOrigin,

    /// <summary>Nothing sensible to do; <see cref="CycleDecision.Message"/> says why.</summary>
    None
}

public sealed record CycleDecision(int? Target, CycleReason Reason, string? Message = null);

/// <summary>
/// The "Next input" rules from 1.x:
/// pressing again within ten seconds returns to the previous input; otherwise the current input is read and the next
/// marked input is chosen. When the current input cannot be read, the saved origin is tried once instead of guessing.
/// </summary>
public static class InputCycle
{
    public static TimeSpan ReturnWindow { get; } = TimeSpan.FromSeconds(10);

    public static CycleDecision Decide(IReadOnlyList<int> cycle, int? current, InputSwitch? last, DateTime nowUtc)
    {
        if (last is not null && nowUtc - last.AtUtc <= ReturnWindow && last.Origin != last.Target)
            return new CycleDecision(last.Origin, CycleReason.ReturnWithinWindow);
        if (current is not int now)
            return last is not null && last.Origin != last.Target
                ? new CycleDecision(last.Origin, CycleReason.ReturnToSavedOrigin)
                : new CycleDecision(null, CycleReason.None, "The current input could not be read, so the next one is unknown.");
        if (cycle.Count == 0) return new CycleDecision(null, CycleReason.None, "This display has no selectable inputs.");
        int index = IndexOf(cycle, now);
        int next = index < 0 ? cycle[0] : cycle[(index + 1) % cycle.Count];
        return next == now
            ? new CycleDecision(null, CycleReason.None, "Mark at least two inputs for Next input.")
            : new CycleDecision(next, CycleReason.Next);
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}

public enum ReturnWatchStep
{
    /// <summary>Keep polling.</summary>
    Wait,

    /// <summary>Send the original input once more so the monitor's own menu catches up.</summary>
    Resend,

    /// <summary>Nothing to do any more (returned cleanly, came back on another port, or timed out).</summary>
    Stop
}

/// <summary>
/// 1.x "automatic menu correction" for the LG 29WK600 on Intel: after this computer switches away, the monitor
/// stops answering. When it answers again with the original input twice in a row, that input is sent once more,
/// because the LG otherwise keeps showing the other port in its menu. Poll every two seconds.
/// </summary>
public sealed class InputReturnWatch(int origin, int target, DateTime startedUtc)
{
    public static TimeSpan Timeout { get; } = TimeSpan.FromMinutes(30);

    private bool lostContact;
    private int originReads;

    public int Origin { get; } = origin;
    public int Target { get; } = target;
    public DateTime StartedUtc { get; } = startedUtc;
    public bool Finished { get; private set; }

    /// <param name="input">The input read now, or null when the display did not answer.</param>
    public ReturnWatchStep Step(int? input, DateTime nowUtc)
    {
        if (Finished) return ReturnWatchStep.Stop;
        if (nowUtc - StartedUtc > Timeout) return Finish();
        if (input is not int read)
        {
            lostContact = true;
            originReads = 0;
            return ReturnWatchStep.Wait;
        }
        if (!lostContact)
            // Still in contact: either the switch has not taken effect yet, or it came back without losing contact.
            return read == Origin ? Finish() : ReturnWatchStep.Wait;
        if (read == Origin)
        {
            if (++originReads < 2) return ReturnWatchStep.Wait;
            Finished = true;
            return ReturnWatchStep.Resend;
        }
        if (read == Target)
        {
            originReads = 0;
            return ReturnWatchStep.Wait;
        }
        return Finish();   // came back on a different port: not ours to correct
    }

    private ReturnWatchStep Finish()
    {
        Finished = true;
        return ReturnWatchStep.Stop;
    }
}
