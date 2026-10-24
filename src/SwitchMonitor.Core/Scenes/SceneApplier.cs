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
    Failed
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

    public static IReadOnlyList<SceneStep> Apply(SceneProfile scene, IEnumerable<MonitorDevice> monitors, ICoreLog? log = null,
        IDelay? delay = null)
    {
        log ??= NullLog.Instance;
        delay ??= ThreadDelay.Instance;
        var steps = new List<SceneStep>();
        foreach (var monitor in monitors)
        {
            if (!scene.AppliesTo(monitor) || !monitor.IsControllable) continue;
            var features = monitor.Features;
            // Modes and presets first, levels after, so a mode change cannot undo the scene's brightness.
            var ordered = scene.Settings.OrderBy(s => s.Percent is null ? 0 : 1).ToArray();
            var results = new Dictionary<SceneSetting, SceneStep>();
            bool modeChanged = false;
            foreach (var setting in ordered)
            {
                if (setting.Percent is not null && modeChanged)
                {
                    delay.Wait(ModeSettle);
                    modeChanged = false;
                }
                var step = ApplyOne(monitor, features, setting);
                results[setting] = step;
                if (setting.Percent is null && step.Status is SceneStepStatus.Applied or SceneStepStatus.Unconfirmed or SceneStepStatus.Mismatch)
                    modeChanged = true;
            }
            steps.AddRange(scene.Settings.Select(s => results[s]));   // report in the order the scene lists them
        }
        foreach (var step in steps)
            log.Write(step.Status is SceneStepStatus.Failed or SceneStepStatus.Mismatch ? LogLevel.Warning : LogLevel.Info,
                $"Scene {scene.Name}: {step}");
        return steps;
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
            var outcome = setting.Percent is double percent
                ? monitor.SetPercent(setting.Feature.Code, percent)
                : monitor.SetChoice(setting.Feature.Code, setting.Value!.Value);
            return new SceneStep(monitor.Name, setting, outcome switch
            {
                WriteOutcome.Verified => SceneStepStatus.Applied,
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
