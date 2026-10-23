using SwitchMonitor.Core.Diagnostics;

namespace SwitchMonitor.Core.Reliability;

/// <summary>Abstracts waiting so retry timing can be tested without real delays.</summary>
public interface IDelay
{
    void Wait(TimeSpan duration);
}

public sealed class ThreadDelay : IDelay
{
    public static ThreadDelay Instance { get; } = new();
    public void Wait(TimeSpan duration) { if (duration > TimeSpan.Zero) Thread.Sleep(duration); }
}

/// <summary>
/// Exponential backoff for DDC/CI commands. Replaces the fixed Sleep(400)/Sleep(1200) waits:
/// fast monitors answer on the first try, slow ones get progressively more time.
/// </summary>
public sealed class RetryPolicy
{
    public int MaxAttempts { get; init; } = 4;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMilliseconds(60);
    public double Multiplier { get; init; } = 2.0;
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(800);

    /// <summary>Default for reads and ordinary writes.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Input switches: the panel may be busy re-syncing for over a second.</summary>
    public static RetryPolicy InputSwitch { get; } = new()
    {
        MaxAttempts = 6,
        InitialDelay = TimeSpan.FromMilliseconds(150),
        MaxDelay = TimeSpan.FromMilliseconds(1200)
    };

    /// <summary>Delay before attempt <paramref name="attempt"/> (1-based; attempt 1 has no delay).</summary>
    public TimeSpan DelayBefore(int attempt)
    {
        if (attempt <= 1) return TimeSpan.Zero;
        double ms = InitialDelay.TotalMilliseconds * Math.Pow(Multiplier, attempt - 2);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }

    public T Execute<T>(string operation, Func<T> action, IDelay? delay = null, ICoreLog? log = null)
    {
        if (MaxAttempts < 1) throw new InvalidOperationException("MaxAttempts must be at least 1.");
        delay ??= ThreadDelay.Instance;
        log ??= NullLog.Instance;
        for (int attempt = 1; ; attempt++)
        {
            delay.Wait(DelayBefore(attempt));
            try
            {
                return action();
            }
            catch (Exception error) when (attempt < MaxAttempts && IsTransient(error))
            {
                log.Write(LogLevel.Debug, $"{operation}: attempt {attempt}/{MaxAttempts} failed ({error.Message}); retrying.");
            }
        }
    }

    public void Execute(string operation, Action action, IDelay? delay = null, ICoreLog? log = null) =>
        Execute(operation, () => { action(); return true; }, delay, log);

    /// <summary>Configuration and programming errors are not retried; I/O-style failures are.</summary>
    internal static bool IsTransient(Exception error) => error is not (
        ArgumentException or NotSupportedException or ObjectDisposedException or InvalidCastException or FormatException);
}
