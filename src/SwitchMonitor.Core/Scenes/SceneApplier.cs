using SwitchMonitor.Core.Diagnostics;
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
    public static IReadOnlyList<SceneStep> Apply(SceneProfile scene, IEnumerable<MonitorDevice> monitors, ICoreLog? log = null)
    {
        log ??= NullLog.Instance;
        var steps = new List<SceneStep>();
        foreach (var monitor in monitors)
        {
            if (!scene.AppliesTo(monitor) || !monitor.IsControllable) continue;
            var features = monitor.Features;
            foreach (var setting in scene.Settings)
                steps.Add(ApplyOne(monitor, features, setting));
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
                string? reason = WhyNot(features, setting);
                steps.Add(new SceneStep(monitor.Name, setting, reason is null ? SceneStepStatus.Applied : SceneStepStatus.Skipped,
                    reason ?? "would be sent"));
            }
        }
        return steps;
    }

    private static SceneStep ApplyOne(MonitorDevice monitor, IReadOnlyList<MonitorFeature> features, SceneSetting setting)
    {
        if (WhyNot(features, setting) is string reason)
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

    private static string? WhyNot(IReadOnlyList<MonitorFeature> features, SceneSetting setting)
    {
        var feature = features.FirstOrDefault(f => f.Code == setting.Feature.Code);
        if (feature is null) return "not offered by this monitor";
        if (setting.Value is uint value && feature.Kind != FeatureKind.Continuous && !feature.AllowedValues.Contains(value))
            return $"{setting.Feature.DescribeValue(value)} not offered by this monitor";
        return null;
    }
}
