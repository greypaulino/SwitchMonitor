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

    /// <summary>
    /// Applies the scene. Each value is read first and only sent when the monitor does not have it already (going from
    /// Custom to Game with the sound muted in both does not touch mute). The mode settle wait only happens when a mode
    /// really changed. Displays are handled at the same time, each on its own connection.
    /// </summary>
    public static IReadOnlyList<SceneStep> Apply(SceneProfile scene, IEnumerable<MonitorDevice> monitors, ICoreLog? log = null,
        IDelay? delay = null)
    {
        log ??= NullLog.Instance;
        bool realTime = delay is null;
        delay ??= ThreadDelay.Instance;
        var targets = monitors.Where(m => scene.AppliesTo(m) && m.IsControllable).ToArray();
        var perMonitor = new IReadOnlyList<SceneStep>[targets.Length];
        if (realTime && targets.Length > 1)
            Parallel.For(0, targets.Length, i => perMonitor[i] = ApplyTo(scene, targets[i], delay));
        else
            for (int i = 0; i < targets.Length; i++) perMonitor[i] = ApplyTo(scene, targets[i], delay);
        var steps = perMonitor.SelectMany(s => s).ToList();
        foreach (var step in steps)
            log.Write(step.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch ? LogLevel.Warning : LogLevel.Info,
                $"Scene {scene.Name}: {step}");
        return steps;
    }

    private static IReadOnlyList<SceneStep> ApplyTo(SceneProfile scene, MonitorDevice monitor, IDelay delay)
    {
        var features = monitor.Features;
        // Modes and presets first, levels after, so a mode change cannot undo the scene's brightness.
        var ordered = scene.Settings.OrderBy(s => s.Percent is null ? 0 : 1).ToArray();
        var results = new Dictionary<SceneSetting, SceneStep>();
        bool modeChanged = false;
        foreach (var setting in ordered)
        {
            if (setting.Percent is not null && modeChanged)
            {
                delay.Wait(ModeSettle);   // the new mode loads its own levels; compare against those
                modeChanged = false;
            }
            var step = ApplyOne(monitor, features, setting);
            results[setting] = step;
            if (setting.Percent is null && step.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed or SceneStepStatus.Mismatch)
                modeChanged = true;
        }
        return scene.Settings.Select(s => results[s]).ToArray();   // report in the order the scene lists them
    }

    /// <summary>True when the monitor already shows the value (percentages compared in the monitor's own steps).</summary>
    internal static bool AlreadyHas(MonitorDevice monitor, MonitorFeature feature, SceneSetting setting)
    {
        var current = monitor.CurrentValue(feature.Code);
        if (setting.Percent is double percent)
        {
            uint top = feature.Definition.Maximum ?? current.Maximum;
            if (top == 0) return false;
            uint raw = VcpValue.FromPercent(percent, top);
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

    private static SceneStep ApplyOne(MonitorDevice monitor, IReadOnlyList<MonitorFeature> features, SceneSetting requested)
    {
        var (setting, reason) = Choose(features, requested);
        if (reason is not null)
            return new SceneStep(monitor.Name, setting, SceneStepStatus.Skipped, reason);
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
        if (feature is null) return "not offered by this monitor";
        if (feature.Definition.Risk is FeatureRisk.ReadOnly or FeatureRisk.Unmapped) return "read-only on this monitor";
        if (setting.Value is uint value && feature.Kind != FeatureKind.Continuous && !feature.AllowedValues.Contains(value))
            return $"{setting.Feature.DescribeValue(value)} not offered by this monitor";
        return null;
    }
}
