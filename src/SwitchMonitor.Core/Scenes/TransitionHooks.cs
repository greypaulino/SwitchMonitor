namespace SwitchMonitor.Core.Scenes;

/// <summary>
/// What a profile change does around a setting that blacks the screen out (FreeSync: the link re-syncs). The Core only
/// says WHEN; the app decides how to hide it (a black cover that fades in and out).
/// The order is always: <see cref="BeforeDisruptive"/> returns once the screen is covered, the whole profile is applied at once
/// (the disruptive setting included), the display answers again, <see cref="AfterDisruptive"/> (even after a failure).
/// </summary>
public sealed class TransitionHooks
{
    /// <summary>Called on the worker thread; must return only when the screens are covered (or the app gave up covering).</summary>
    public Action? BeforeDisruptive { get; init; }

    /// <summary>Called on the worker thread once the display is stable again, or after a failure; always called when Before was.</summary>
    public Action? AfterDisruptive { get; init; }

    /// <summary>
    /// Called on the worker thread after each step of a gradual change with the raw value just written (display, VCP code,
    /// value), so an interface can show the real movement. Optional: a hooks object may carry only this.
    /// </summary>
    public Action<MonitorDevice, byte, uint>? LevelMoved { get; init; }

    /// <summary>The longest wait for the display to answer again after the change (the cover never stays longer).</summary>
    public TimeSpan StableWithin { get; init; } = TimeSpan.FromSeconds(8);
}
