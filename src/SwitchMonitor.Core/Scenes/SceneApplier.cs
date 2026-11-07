using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Scenes;

public enum SceneStepStatus
{
    /// <summary>Written and read back with the requested value.</summary>
    Applied,

    /// <summary>Written; the monitor did not answer the read-back.</summary>
    Unconfirmed,

    /// <summary>Written but the monitor reports another value.</summary>
    Mismatch,

    /// <summary>The monitor does not offer this control or value; nothing was sent.</summary>
    Skipped,

    /// <summary>The command failed.</summary>
    Failed,

    /// <summary>The monitor already had this value; nothing was sent.</summary>
    AlreadySet
}

public sealed record SceneStep(string Monitor, SceneSetting Setting, SceneStepStatus Status, string? Detail = null)
{
    public override string ToString() => $"{Monitor}: {Setting} -> {Status}{(Detail is null ? "" : $" ({Detail})")}";
}

/// <summary>Applies a scene to every matching active display and reports each step.</summary>
public static class SceneApplier
{
    /// <summary>
    /// Wait after a mode or preset change before setting levels: the monitor loads the mode's own brightness and
    /// contrast a moment later (the LG 29WK600 needs a few seconds), which would overwrite levels written too early.
    /// </summary>
    public static TimeSpan ModeSettle { get; } = TimeSpan.FromSeconds(3);

    /// <summary>Reason of a skip when the display has no such control at all (the laptop screen and picture mode).</summary>
    public const string NotOffered = "not offered by this monitor";

    /// <summary>Start of the reason when the picture mode locks the control (LG response time outside Custom).</summary>
    public const string LockedPrefix = "locked in";

    /// <summary>Skips that only mean "this line does not concern this display now" (not counted in summaries).</summary>
    public static bool IsQuietSkip(SceneStep step) =>
        step.Status == SceneStepStatus.Skipped &&
        (step.Detail == NotOffered || step.Detail?.StartsWith(LockedPrefix, StringComparison.Ordinal) == true);

    /// <summary>True for the picture-mode line (LG 0x15, MCCS 0xDC): it loads the mode's own settings.</summary>
    internal static bool IsPictureMode(SceneSetting setting) =>
        setting.Feature.Code == VcpCode.DisplayMode ||
        string.Equals(setting.Feature.Name, FeatureCatalog.DisplayMode.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies the scene. Each value is read first and only sent when the monitor does not have it already (going from
    /// Custom to Game with the sound muted in both does not touch mute). The mode settle wait only happens when a mode
    /// really changed. Displays are handled at the same time, each on its own connection.
    /// </summary>
    /// <param name="fade">
    /// Gradual change: levels (brightness, contrast, volume, colors) move there in small steps over this time instead of
    /// at once. Modes and presets cannot be gradual and are set first, as usual. Zero = at once.
    /// </param>
    /// <param name="hooks">
    /// When the profile changes FreeSync (which blacks the screen), the levels fade first, the app is asked to cover the
    /// screens, FreeSync is written last, the display is awaited, and the cover is lifted. Null: as always.
    /// </param>
    public static IReadOnlyList<SceneStep> Apply(SceneProfile scene, IEnumerable<MonitorDevice> monitors, ICoreLog? log = null,
        IDelay? delay = null, TimeSpan fade = default, TransitionHooks? hooks = null)
    {
        log ??= NullLog.Instance;
        bool realTime = delay is null;
        delay ??= ThreadDelay.Instance;
        var targets = monitors.Where(m => scene.AppliesTo(m) && m.IsControllable).ToArray();

        IReadOnlyList<SceneStep> Run(SceneProfile part, TimeSpan partFade)
        {
            var perMonitor = new IReadOnlyList<SceneStep>[targets.Length];
            if (realTime && targets.Length > 1)
                Parallel.For(0, targets.Length, i => perMonitor[i] = ApplyTo(part, targets[i], delay, partFade, hooks, log));
            else
                for (int i = 0; i < targets.Length; i++) perMonitor[i] = ApplyTo(part, targets[i], delay, partFade, hooks, log);
            return perMonitor.SelectMany(s => s).ToList();
        }

        var disruptive = hooks is null ? [] : scene.Settings.Where(IsDisruptive).ToArray();
        List<SceneStep> steps;
        if (hooks?.BeforeDisruptive is not null && disruptive.Length > 0 && targets.Any(m => WillChange(m, scene, disruptive)))
        {
            // The whole profile (modes, levels, FreeSync) is applied under the cover, at once and unseen, while the screen is
            // black anyway: when the cover lifts the new look is already there and nothing is noticed.
            hooks.BeforeDisruptive?.Invoke();
            try
            {
                steps = Run(scene, TimeSpan.Zero).ToList();
                WaitStable(targets, disruptive, delay, hooks.StableWithin);
            }
            finally { hooks.AfterDisruptive?.Invoke(); }
        }
        else steps = Run(scene, fade).ToList();

        foreach (var step in steps)
            log.Write(step.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch ? LogLevel.Warning : LogLevel.Info,
                $"Scene {scene.Name}: {step}");
        return steps;
    }

    /// <summary>Settings that can black the screen out while the link re-syncs (FreeSync / adaptive sync).</summary>
    public static bool IsDisruptive(SceneSetting setting) => IsDisruptive(setting.Feature.Name);

    public static bool IsDisruptive(string featureName) =>
        featureName.Contains("freesync", StringComparison.OrdinalIgnoreCase) ||
        featureName.Contains("adaptive sync", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when applying the scene would really change a disruptive setting on the monitor (nothing is sent).</summary>
    private static bool WillChange(MonitorDevice monitor, SceneProfile scene, IReadOnlyList<SceneSetting> disruptive)
    {
        var features = monitor.Features;
        // The picture mode (and other choices) the scene sets first can unlock FreeSync: judge it as it will be then.
        var planned = new Dictionary<byte, uint>();
        foreach (var other in scene.Settings.Where(s => !IsDisruptive(s) && s.Percent is null))
            if (Choose(features, other) is { Reason: null } chosen && chosen.Setting.Value is uint value)
                planned[chosen.Setting.Feature.Code] = value;
        foreach (var requested in disruptive)
        {
            var (setting, reason) = Choose(features, requested);
            if (reason is not null || Match(features, setting) is not { } feature) continue;
            if (LockedBy(monitor, features, feature, planned) is not null) continue;
            try { if (!AlreadyHas(monitor, feature, setting)) return true; }
            catch (Exception error) when (error is not OutOfMemoryException) { return true; }   // unreadable: assume it changes
        }
        return false;
    }

    /// <summary>After the change: waits (in short steps, never longer than <paramref name="limit"/>) until each display answers a read again.</summary>
    private static void WaitStable(IEnumerable<MonitorDevice> monitors, IReadOnlyList<SceneSetting> disruptive, IDelay delay, TimeSpan limit)
    {
        foreach (var monitor in monitors)
            if (disruptive.Select(s => Match(monitor.Features, s)).FirstOrDefault(f => f is not null) is { } feature)
                WaitUntilAnswering(monitor, feature.Code, delay, limit);
        delay.Wait(TimeSpan.FromMilliseconds(1200));   // the picture is back and steady: a margin before uncovering
    }

    /// <summary>Waits (in short steps, never longer than <paramref name="limit"/>) until the display answers a read of <paramref name="code"/> again.</summary>
    public static void WaitUntilAnswering(MonitorDevice monitor, byte code, IDelay delay, TimeSpan limit)
    {
        var step = TimeSpan.FromMilliseconds(300);
        int attempts = Math.Max(1, (int)(limit / step));
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                // Two answers in a row, a step apart: the first one can come while the link is still settling.
                monitor.CurrentValue(code);
                delay.Wait(step);
                monitor.CurrentValue(code);
                return;
            }
            catch (Exception error) when (error is not OutOfMemoryException) { delay.Wait(step); }
        }
    }

    /// <summary>Time between two steps of a fade (a DDC write takes about 50-100 ms).</summary>
    public static TimeSpan FadeStep { get; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Smallest pause between two rounds of writes in a real-time fade (the display gets a breather).</summary>
    public static TimeSpan MinFadeGap { get; } = TimeSpan.FromMilliseconds(30);

    /// <summary>The longest a gradual change may take when the display is slow (one write per 150-500 ms).</summary>
    public static TimeSpan MaxFade { get; } = TimeSpan.FromMilliseconds(3500);

    /// <summary>How long brightness, contrast, volume and colors take to reach the next profile (ease in-out).</summary>
    public static TimeSpan ProfileFade { get; } = TimeSpan.FromMilliseconds(1500);

    private static IReadOnlyList<SceneStep> ApplyTo(SceneProfile scene, MonitorDevice monitor, IDelay delay, TimeSpan fade,
        TransitionHooks? hooks = null, ICoreLog? log = null)
    {
        var features = monitor.Features;
        // The picture mode first, then the other choices (color preset, response time, FreeSync...), levels last.
        // A new picture mode loads its own settings a few seconds later (the LG reloads its response time too), so
        // nothing else is compared or written until it has settled; a preset change likewise waits before the levels.
        var ordered = scene.Settings.OrderBy(s => IsPictureMode(s) ? 0 : s.Percent is null ? 1 : 2).ToArray();
        var results = new Dictionary<SceneSetting, SceneStep>();
        bool pictureModeChanged = false, choiceChanged = false;
        var planned = new Dictionary<byte, uint>();   // choice values the scene has set (or found set) so far
        bool faded = false;
        foreach (var setting in ordered)
        {
            // One wait covers both: after a new picture mode before anything else, after a color preset before the levels.
            if ((pictureModeChanged && !IsPictureMode(setting)) || (choiceChanged && setting.Percent is not null))
            {
                delay.Wait(ModeSettle);   // the monitor loads the mode's own values; compare against those
                pictureModeChanged = choiceChanged = false;
            }
            if (!faded && fade > TimeSpan.Zero && setting.Percent is not null)
            {
                // All the levels move together, one small step at a time, from what the (new) mode loaded; the final
                // values are then written and checked by ApplyOne as usual.
                Fade(monitor, features, ordered.Where(s => s.Percent is not null), planned, delay, fade, hooks, log);
                faded = true;
            }
            var step = ApplyOne(monitor, features, setting, planned);
            results[setting] = step;
            if (step.Setting.Value is uint chosen && step.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed or SceneStepStatus.AlreadySet)
                planned[step.Setting.Feature.Code] = chosen;
            bool changed = step.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed or SceneStepStatus.Mismatch;
            if (changed && IsPictureMode(setting)) pictureModeChanged = true;
            // A color preset loads its own gains, so levels wait for it; response time or FreeSync load nothing.
            else if (changed && setting.Feature.Code == VcpCode.ColorPreset) choiceChanged = true;
        }
        return scene.Settings.Select(s => results[s]).ToArray();   // report in the order the scene lists them
    }

    private sealed record Ramp(byte Code, uint From, uint To);

    /// <summary>Moves every level of the scene from its current value toward the target in steps (not the last one).</summary>
    private static void Fade(MonitorDevice monitor, IReadOnlyList<MonitorFeature> features, IEnumerable<SceneSetting> levels,
        IReadOnlyDictionary<byte, uint> planned, IDelay delay, TimeSpan fade, TransitionHooks? hooks = null, ICoreLog? log = null)
    {
        var ramps = new List<Ramp>();
        foreach (var requested in levels)
        {
            var (setting, reason) = Choose(features, requested);
            if (reason is not null || setting.Percent is not double percent) continue;
            if (Match(features, setting) is not { } feature || feature.Definition.WriteOnly || feature.Definition.Step is not null) continue;
            if (LockedBy(monitor, features, feature, planned) is not null) continue;
            try
            {
                var current = monitor.CurrentValue(feature.Code);
                uint top = feature.Definition.Maximum ?? current.Maximum;
                if (top == 0) continue;
                uint target = monitor.Snap(feature.Code, VcpValue.FromPercent(percent, top));
                if (target != current.Current) ramps.Add(new Ramp(feature.Code, current.Current, target));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }   // unreadable: set directly at the end
        }
        if (ramps.Count == 0) return;
        if (delay is ThreadDelay)
        {
            // Real time: the levels follow an ease-in-out curve over the whole duration, written as fast as the display
            // takes them (a DDC write is about 50-100 ms), never twice the same value, never closer than MinFadeGap.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var current = ramps.ToDictionary(r => r.Code, r => r.From);
            int writes = 0;
            var duration = fade;
            bool tuned = false;
            while (clock.Elapsed < duration)
            {
                var round = System.Diagnostics.Stopwatch.StartNew();
                double u = clock.Elapsed / duration;
                u = u * u * (3 - 2 * u);   // ease in and out: no visible start or stop
                foreach (var ramp in ramps)
                {
                    uint value = monitor.Snap(ramp.Code, (uint)Math.Round(ramp.From + (ramp.To - (double)ramp.From) * u));
                    if (value == current[ramp.Code] || value == ramp.To) continue;   // the target is left to the final, checked write
                    try { monitor.Write(ramp.Code, value, quiet: true); current[ramp.Code] = value; writes++; hooks?.LevelMoved?.Invoke(monitor, ramp.Code, value); }
                    catch (Exception error) when (error is not OutOfMemoryException) { return; }   // the final write reports it
                }
                if (!tuned)
                {
                    // A real display takes ~150-500 ms per write, so a short fade has only a few steps and the levels visibly
                    // jump. After the first round we know the pace: give each level up to 14 steps (about 1 unit each for
                    // small changes), within MaxFade, so the movement stays smooth instead of stepping.
                    tuned = true;
                    int rounds = (int)Math.Clamp(ramps.Max(r => Math.Abs((long)r.To - r.From)), 1, 14);
                    var wanted = TimeSpan.FromMilliseconds(rounds * (round.ElapsedMilliseconds + MinFadeGap.TotalMilliseconds));
                    duration = wanted < fade ? fade : wanted > MaxFade ? MaxFade : wanted;
                }
                delay.Wait(MinFadeGap);
            }
            log?.Write(LogLevel.Info, $"Fade {monitor.Name}: {string.Join(", ", ramps.Select(r => $"0x{r.Code:X2} {r.From}->{r.To}"))} " +
                $"in {clock.ElapsedMilliseconds} ms, {writes} writes ({(writes == 0 ? 0 : clock.ElapsedMilliseconds / writes)} ms each).");
            return;
        }
        int largest = Math.Max(2, ramps.Max(r => (int)Math.Abs((long)r.To - r.From)));   // never more steps than values
        int steps = (int)Math.Clamp(fade / (FadeStep * ramps.Count), 2, largest);
        var last = ramps.ToDictionary(r => r.Code, r => r.From);
        for (int step = 1; step < steps; step++)
        {
            foreach (var ramp in ramps)
            {
                double t = step / (double)steps;
                t = t * t * (3 - 2 * t);   // ease in and out: no visible start or stop
                uint value = monitor.Snap(ramp.Code, (uint)Math.Round(ramp.From + (ramp.To - (double)ramp.From) * t));
                // The target itself is left to the final write, which checks it and reports the line as changed.
                if (value == last[ramp.Code] || value == ramp.To) continue;
                try { monitor.Write(ramp.Code, value, quiet: true); last[ramp.Code] = value; hooks?.LevelMoved?.Invoke(monitor, ramp.Code, value); }
                catch (Exception error) when (error is not OutOfMemoryException) { return; }   // the final write reports it
            }
            delay.Wait(FadeStep);
        }
    }

    /// <summary>True when the monitor already shows the value (percentages compared in the monitor's own steps).</summary>
    internal static bool AlreadyHas(MonitorDevice monitor, MonitorFeature feature, SceneSetting setting)
    {
        var current = monitor.CurrentValue(feature.Code);
        if (setting.Percent is double percent)
        {
            uint top = feature.Definition.Maximum ?? current.Maximum;
            if (top == 0) return false;
            uint raw = monitor.Snap(feature.Code, VcpValue.FromPercent(percent, top));
            if (feature.Definition.Step is uint step && step > 1) raw = Math.Min(top, (uint)Math.Round(raw / (double)step) * step);
            return current.Current == raw;
        }
        return setting.Value is uint value && current.Current == value;
    }

    /// <summary>What <see cref="Apply"/> would do, without sending anything.</summary>
    public static IReadOnlyList<SceneStep> Preview(SceneProfile scene, IEnumerable<MonitorDevice> monitors)
    {
        var steps = new List<SceneStep>();
        foreach (var monitor in monitors)
        {
            if (!scene.AppliesTo(monitor) || !monitor.IsControllable) continue;
            var features = monitor.Features;
            foreach (var setting in scene.Settings)
            {
                var (chosen, reason) = Choose(features, setting);
                steps.Add(new SceneStep(monitor.Name, chosen, reason is null ? SceneStepStatus.Applied : SceneStepStatus.Skipped,
                    reason ?? "would be sent"));
            }
        }
        return steps;
    }

    /// <summary>The first candidate (the setting or one of its alternatives) this monitor can take.</summary>
    private static (SceneSetting Setting, string? Reason) Choose(IReadOnlyList<MonitorFeature> features, SceneSetting setting)
    {
        string? firstReason = null;
        foreach (var candidate in setting.Candidates)
        {
            string? reason = WhyNot(features, candidate);
            if (reason is null) return (candidate, null);
            firstReason ??= reason;
        }
        return (setting, firstReason);
    }

    private static SceneStep ApplyOne(MonitorDevice monitor, IReadOnlyList<MonitorFeature> features, SceneSetting requested,
        IReadOnlyDictionary<byte, uint> planned)
    {
        var (setting, reason) = Choose(features, requested);
        if (reason is not null)
            return new SceneStep(monitor.Name, setting, SceneStepStatus.Skipped, reason);
        if (Match(features, setting) is { } target && LockedBy(monitor, features, target, planned) is { } lockReason)
            return new SceneStep(monitor.Name, setting, SceneStepStatus.Skipped, lockReason);
        try
        {
            if (Match(features, setting) is { } feature && AlreadyHas(monitor, feature, setting))
                return new SceneStep(monitor.Name, setting, SceneStepStatus.AlreadySet, "already set");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Could not read it: send it anyway.
        }
        try
        {
            var outcome = setting.Percent is double percent
                ? monitor.SetPercent(setting.Feature.Code, percent)
                : monitor.SetChoice(setting.Feature.Code, setting.Value!.Value);
            return new SceneStep(monitor.Name, setting, outcome switch
            {
                WriteOutcome.Verified or WriteOutcome.Applied => SceneStepStatus.Applied,
                WriteOutcome.Unverified => SceneStepStatus.Unconfirmed,
                _ => SceneStepStatus.Mismatch
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new SceneStep(monitor.Name, setting, SceneStepStatus.Failed, error.Message);
        }
    }

    /// <summary>
    /// "locked in Reader" when the mode the scene leaves the monitor in (or its current one) does not allow the control,
    /// following the quirk's enabledWhen rule; writing it anyway would only bounce back.
    /// </summary>
    private static string? LockedBy(MonitorDevice monitor, IReadOnlyList<MonitorFeature> features, MonitorFeature target,
        IReadOnlyDictionary<byte, uint> planned)
    {
        foreach (var (code, values) in target.Definition.EnabledWhen)
        {
            uint? mode = planned.TryGetValue(code, out uint set) ? set : null;
            if (mode is null)
                try { mode = monitor.CurrentValue(code).Current; }
                catch (Exception error) when (error is not OutOfMemoryException) { }
            if (mode is uint value && !values.Contains(value))
            {
                var source = features.FirstOrDefault(f => f.Code == code);
                return $"{LockedPrefix} {source?.Definition.DescribeValue(value) ?? $"0x{value:X2}"}";
            }
        }
        return null;
    }

    /// <summary>
    /// The monitor's control for a scene setting. Standard controls match by code; vendor controls also by name,
    /// because the same vendor code means different things on different models.
    /// </summary>
    internal static MonitorFeature? Match(IReadOnlyList<MonitorFeature> features, SceneSetting setting) =>
        features.FirstOrDefault(f => f.Code == setting.Feature.Code &&
            (FeatureCatalog.Find(f.Code) is not null || string.Equals(f.Name, setting.Feature.Name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The monitor control a scene line uses (the first of its candidates the monitor offers), or null.</summary>
    public static (MonitorFeature Feature, SceneSetting Setting)? FeatureFor(IReadOnlyList<MonitorFeature> features, SceneSetting setting)
    {
        foreach (var candidate in setting.Candidates)
            if (Match(features, candidate) is { } feature) return (feature, candidate);
        return null;
    }

    private static string? WhyNot(IReadOnlyList<MonitorFeature> features, SceneSetting setting)
    {
        var feature = Match(features, setting);
        if (feature is null) return NotOffered;
        if (feature.Definition.Risk is FeatureRisk.ReadOnly or FeatureRisk.Unmapped) return "read-only on this monitor";
        if (setting.Value is uint value && feature.Kind != FeatureKind.Continuous && !feature.AllowedValues.Contains(value))
            return $"{setting.Feature.DescribeValue(value)} not offered by this monitor";
        return null;
    }
}
