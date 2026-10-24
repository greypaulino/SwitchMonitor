namespace SwitchMonitor.Core.Inputs;

/// <summary>
/// Keyboard brightness rules from 1.x: an isolated tap moves one point; a third quick tap (under 167 ms apart) jumps to
/// the next five-point mark; holding the key reaches the next five-point mark after 300 ms and then moves between
/// ten-point marks.
/// </summary>
public sealed class BrightnessSteps(int tapStep = 1)
{
    public static TimeSpan QuickTap { get; } = TimeSpan.FromMilliseconds(167);
    public static TimeSpan HoldFirst { get; } = TimeSpan.FromMilliseconds(300);
    public static TimeSpan HoldFast { get; } = TimeSpan.FromMilliseconds(500);
    /// <summary>1.x timing: a step every 200 ms while holding, every 230 ms once moving by ten.</summary>
    public static TimeSpan HoldRepeat(TimeSpan held) => TimeSpan.FromMilliseconds(held >= HoldFast ? 230 : 200);

    private DateTime lastTap = DateTime.MinValue;
    private int direction;
    private int quickTaps;

    /// <summary>Target for a key press at <paramref name="now"/>.</summary>
    public int Tap(int current, int sign, DateTime now)
    {
        sign = Math.Sign(sign);
        quickTaps = sign == direction && now - lastTap < QuickTap ? quickTaps + 1 : 1;
        direction = sign;
        lastTap = now;
        return quickTaps >= 3 ? NextMark(current, sign, 5) : Math.Clamp(current + sign * tapStep, 0, 100);
    }

    /// <summary>Target while the key stays down for <paramref name="held"/>.</summary>
    public static int Hold(int current, int sign, TimeSpan held) =>
        NextMark(current, Math.Sign(sign), held >= HoldFast ? 10 : 5);

    /// <summary>Next multiple of <paramref name="mark"/> in the given direction: 43 up → 45 (5) or 50 (10); 45 up → 50.</summary>
    public static int NextMark(int current, int sign, int mark)
    {
        int next = sign > 0 ? (current / mark + 1) * mark : (current % mark == 0 ? current - mark : current / mark * mark);
        return Math.Clamp(next, 0, 100);
    }
}
