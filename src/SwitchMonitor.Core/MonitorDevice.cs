using SwitchMonitor.Core.Capabilities;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Settings;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core;

/// <summary>Result of a write followed by a read-back check.</summary>
public enum WriteOutcome
{
    /// <summary>The monitor reported the requested value afterwards.</summary>
    Verified,

    /// <summary>The write was accepted but the monitor could not be read back (common right after an input switch).</summary>
    Unverified,

    /// <summary>The monitor kept answering with a different value.</summary>
    Mismatch,

    /// <summary>Sent to a write-only control (it applies writes but never reports them); nothing to read back.</summary>
    Applied
}

/// <summary>MCCS factory-default commands the app may send (never 0x04, which resets everything).</summary>
public enum DefaultsGroup : byte
{
    /// <summary>VCP 0x05: brightness and contrast of the current mode.</summary>
    LuminanceContrast = 0x05,

    /// <summary>VCP 0x08: color preset and red/green/blue gains of the current mode.</summary>
    Color = 0x08
}

/// <summary>
/// One controllable display: identity, quirks, capabilities and a transport.
/// All commands are serialized per display and use <see cref="RetryPolicy"/> instead of fixed sleeps.
/// </summary>
public sealed class MonitorDevice : IDisposable
{
    private readonly object gate = new();
    private readonly IMonitorTransport transport;
    private readonly IDelay delay;
    private readonly ICoreLog log;
    private readonly Dictionary<byte, uint> maximums = [];
    private MonitorCapabilities? capabilities;
    private bool disposed;

    internal MonitorDevice(MonitorIdentity identity, IMonitorTransport transport, MonitorQuirk? quirk,
        ICoreLog? log = null, IDelay? delay = null)
    {
        Identity = identity;
        this.transport = transport;
        Quirk = quirk;
        this.log = log ?? NullLog.Instance;
        this.delay = delay ?? ThreadDelay.Instance;
    }

    public MonitorIdentity Identity { get; }
    public MonitorQuirk? Quirk { get; }
    public string Key => Identity.Key;
    public string Name => Quirk?.Name ?? Identity.FriendlyName;
    public string TransportId => transport.Id;
    /// <summary>False when Windows is not currently using this display (for example a closed laptop lid).</summary>
    public bool IsActive => Identity.IsActive;

    /// <summary>True when commands can be sent now: a working transport and an active display.</summary>
    public bool IsControllable => IsActive && transport is not UnavailableTransport;
    public RetryPolicy ReadPolicy { get; init; } = RetryPolicy.Default;
    public RetryPolicy WritePolicy { get; init; } = RetryPolicy.Default;
    public RetryPolicy InputPolicy { get; init; } = RetryPolicy.InputSwitch;

    /// <summary>Parsed capabilities. Empty when the transport cannot report them; see <see cref="Supports"/>.</summary>
    public MonitorCapabilities Capabilities
    {
        get
        {
            lock (gate)
            {
                if (capabilities is not null) return capabilities;
                if (!IsControllable) return MonitorCapabilities.Empty;   // Not cached: the display may become active.
                try
                {
                    string? raw = ReadPolicy.Execute($"{Name}: capabilities", () => transport.ReadCapabilities(), delay, log);
                    capabilities = MccsCapabilitiesParser.Parse(raw);
                }
                catch (Exception error) when (error is TransportException or NotSupportedException)
                {
                    log.Write(LogLevel.Warning, $"{Name}: capabilities unavailable via {transport.Id}.", error);
                    capabilities = MonitorCapabilities.Empty;
                }
                return capabilities;
            }
        }
    }

    /// <summary>Inputs the user can select. Quirks override capabilities that list ports the panel lacks.</summary>
    public IReadOnlyList<int> Inputs =>
        Quirk is { Inputs.Count: > 0 } quirk ? quirk.Inputs.Select(i => i.Code).ToArray() : Capabilities.InputSources;

    public string DescribeInput(int code)
    {
        if (Quirk is not null)
            foreach (var input in Quirk.Inputs)
                if (input.Code == code) return input.Name;
        return InputSource.Describe(code);
    }

    /// <summary>Whether a VCP feature can be used on this display.</summary>
    public bool Supports(byte code)
    {
        if (!IsControllable) return false;
        if (Quirk?.Controls.Any(c => c.Code == code && c.NotAdvertised) == true) return true;
        if (Capabilities.VcpCodes.Count > 0) return Capabilities.Supports(code) || (code == VcpCode.InputSource && Inputs.Count > 0);
        // No capabilities string (GPU-driver transports): rely on the quirk's validated features.
        return Quirk is not null && code is VcpCode.Brightness or VcpCode.InputSource;
    }

    public VcpValue Read(byte code)
    {
        lock (gate)
        {
            EnsureUsable();
            var value = ReadPolicy.Execute($"{Name}: read {VcpCode.Describe(code)}", () => transport.Read(code), delay, log);
            if (value.Maximum > 0) maximums[code] = value.Maximum;
            return value;
        }
    }

    /// <summary>One read without retries, for scans of codes the monitor may not support.</summary>
    public VcpValue ReadOnce(byte code)
    {
        lock (gate)
        {
            EnsureUsable();
            var value = transport.Read(code);
            if (value.Maximum > 0) maximums[code] = value.Maximum;
            return value;
        }
    }

    public void Write(byte code, uint value) => Write(code, value, quiet: false);

    /// <param name="quiet">Not logged: the in-between steps of a gradual change (a fade writes dozens of them).</param>
    public void Write(byte code, uint value, bool quiet)
    {
        lock (gate)
        {
            EnsureWritable();
            WritePolicy.Execute($"{Name}: write {VcpCode.Describe(code)}={value}", () => transport.Write(code, value), delay, log);
            if (!quiet) log.Write(LogLevel.Info, $"{Name}: {VcpCode.Describe(code)} set to {value} via {transport.Id}.");
        }
    }

    /// <summary>Sets a continuous feature (brightness, contrast, volume...) as 0-100 percent of its reported maximum.</summary>
    public WriteOutcome SetPercent(byte code, double percent)
    {
        lock (gate)
        {
            EnsureWritable();
            if (FeatureCatalog.Find(code) is { Kind: not FeatureKind.Continuous } definition)
                throw new ArgumentException($"{definition.Name} is not a percentage; use SetChoice.", nameof(code));
            if (FeatureCatalog.Find(code) is null)
            {
                var feature = Features.FirstOrDefault(f => f.Code == code)
                    ?? throw new NotSupportedException($"{Name} does not offer {VcpCode.Describe(code)}.");
                if (feature.Definition.Risk is FeatureRisk.Unmapped or FeatureRisk.ReadOnly)
                    throw new NotSupportedException(ReadOnlyMessage(feature));
                if (feature.Kind != FeatureKind.Continuous)
                    throw new ArgumentException($"{feature.Name} is not a percentage; use SetChoice.", nameof(code));
            }
            uint maximum = Features.FirstOrDefault(f => f.Code == code)?.Definition.Maximum ?? 0;
            if (maximum == 0 && !maximums.TryGetValue(code, out maximum)) maximum = Read(code).Maximum;
            uint raw = Snap(code, VcpValue.FromPercent(percent, maximum));
            if (Features.FirstOrDefault(f => f.Code == code)?.Definition is { WriteOnly: true } writeOnly)
                return WriteRemembered(writeOnly, raw);
            Write(code, raw);
            return Verify(code, raw, ReadPolicy);
        }
    }

    /// <summary>
    /// Controls this display offers, in interface order: standard controls first, then vendor controls.
    /// Input is excluded (see <see cref="Inputs"/>). Choice and toggle controls carry the advertised values.
    /// Advertised vendor codes without a known meaning appear as read-only <see cref="FeatureRisk.Unmapped"/> controls.
    /// </summary>
    public IReadOnlyList<MonitorFeature> Features
    {
        get
        {
            var quirkControls = Quirk?.Controls.ToDictionary(c => c.Code) ?? new Dictionary<byte, FeatureDefinition>();
            var result = new List<MonitorFeature>();
            var seen = new HashSet<byte>();
            foreach (var standard in FeatureCatalog.All)
            {
                if (standard.Code == VcpCode.InputSource || !Supports(standard.Code)) continue;
                var definition = quirkControls.GetValueOrDefault(standard.Code) ?? standard;
                result.Add(new MonitorFeature(definition, AllowedValues(definition)));
                seen.Add(standard.Code);
            }
            foreach (byte code in Capabilities.VcpCodes)
            {
                if (seen.Contains(code) || code == VcpCode.InputSource || code == Quirk?.InputWriteVcp
                    || FeatureCatalog.IsCommand(code) || FeatureCatalog.IsInformational(code)) continue;
                var definition = quirkControls.GetValueOrDefault(code)
                    ?? FeatureCatalog.Unmapped(code, Capabilities.ValuesFor(code).Count > 0);
                result.Add(new MonitorFeature(definition, AllowedValues(definition)));
                seen.Add(code);
            }
            // Verified codes the monitor does not advertise, placed where the quirk says (black levels after blue).
            foreach (var hidden in quirkControls.Values.Where(c => c.NotAdvertised && !seen.Contains(c.Code)))
            {
                var feature = new MonitorFeature(hidden, AllowedValues(hidden));
                int anchor = hidden.PlaceAfter is byte after ? result.FindLastIndex(f => f.Code == after) : -1;
                if (anchor >= 0) result.Insert(anchor + 1, feature);
                else result.Add(feature);
                seen.Add(hidden.Code);
            }
            return result;
        }
    }

    private string ReadOnlyMessage(MonitorFeature feature) => feature.Definition.Risk == FeatureRisk.ReadOnly
        ? $"{Name} ignores DDC writes to {feature.Name}; it is read-only."
        : $"{feature.Name} has not been mapped yet; it is read-only.";

    private static IReadOnlyList<uint> AllowedValues(FeatureDefinition definition, IReadOnlyList<uint> values)
    {
        if (definition.DisplayOrder is not { Count: > 0 } order) return values;
        int Position(uint value)
        {
            for (int i = 0; i < order.Count; i++)
                if (order[i] == value) return i;
            return int.MaxValue;
        }
        return values.OrderBy(Position).ThenBy(v => v).ToArray();
    }

    private IReadOnlyList<uint> AllowedValues(FeatureDefinition definition) => AllowedValues(definition, AdvertisedValues(definition));

    private IReadOnlyList<uint> AdvertisedValues(FeatureDefinition definition)
    {
        if (definition.Kind == FeatureKind.Continuous) return Array.Empty<uint>();
        if (definition.IgnoreCapabilityValues) return definition.ValueNames.Keys.Order().ToArray();
        var advertised = Capabilities.ValuesFor(definition.Code)
            .Select(v => definition.CapabilityValuesAreDecimal ? DecimalDigits(v) : (uint?)v)
            .OfType<uint>().ToArray();
        // Quirk-named values are trusted even when the monitor leaves them out (LG omits HDR Effect).
        if (advertised.Length > 0)
        {
            bool quirkControl = definition.Risk is FeatureRisk.Safe or FeatureRisk.ReadOnly && FeatureCatalog.Find(definition.Code) is null;
            IEnumerable<uint> extra = quirkControl ? definition.ValueNames.Keys : Array.Empty<uint>();
            return advertised.Union(extra).Order().ToArray();
        }
        // Toggles and quirk-named choices without a value list in the capabilities string.
        return definition.ValueNames.Keys.Order().ToArray();
    }

    /// <summary>0x48 (hex digits "48") → 48. Returns null when the hex digits are not a decimal number (e.g. 0x2A).</summary>
    internal static uint? DecimalDigits(int value) =>
        uint.TryParse(value.ToString("X", System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
            ? parsed : null;

    /// <summary>
    /// Sets a choice or toggle control to a raw MCCS value (e.g. color preset 0x05 = 6500 K).
    /// The value must be one the monitor advertised; this never guesses unadvertised codes.
    /// </summary>
    public WriteOutcome SetChoice(byte code, uint value)
    {
        lock (gate)
        {
            EnsureWritable();
            if (code == VcpCode.InputSource)
                throw new ArgumentException("Use SetInput to change the input.", nameof(code));
            var feature = Features.FirstOrDefault(f => f.Code == code)
                ?? throw new NotSupportedException($"{Name} does not offer {VcpCode.Describe(code)}.");
            if (feature.Definition.Risk is FeatureRisk.Unmapped or FeatureRisk.ReadOnly)
                throw new NotSupportedException(ReadOnlyMessage(feature));
            if (feature.Kind == FeatureKind.Continuous)
                throw new ArgumentException($"{feature.Name} is continuous; use SetPercent.", nameof(code));
            if (!feature.AllowedValues.Contains(value))
                throw new ArgumentOutOfRangeException(nameof(value),
                    $"{Name} does not advertise {feature.Name} = {feature.Definition.DescribeValue(value)}.");
            Write(code, value);
            return Verify(code, value, ReadPolicy);
        }
    }

    /// <summary>Selects an input by its standard MCCS code (0x11 = HDMI 1, 0x0F = DisplayPort 1 ...).</summary>
    public WriteOutcome SetInput(int inputCode)
    {
        lock (gate)
        {
            EnsureWritable();
            if (!Inputs.Contains(inputCode))
                throw new ArgumentOutOfRangeException(nameof(inputCode), $"{Name} does not advertise {InputSource.Describe(inputCode)}.");
            var (vcp, value) = Quirk?.MapInputWrite(inputCode) ?? (VcpCode.InputSource, (uint)inputCode);
            InputPolicy.Execute($"{Name}: switch to {DescribeInput(inputCode)}", () => transport.Write(vcp, value), delay, log);
            log.Write(LogLevel.Info, $"{Name}: input switch to {DescribeInput(inputCode)} sent via {transport.Id}.");
            return Verify(VcpCode.InputSource, (uint)inputCode, InputPolicy);
        }
    }

    /// <summary>Wait after a factory-default command before reading the restored values.</summary>
    public static TimeSpan RestoreSettle { get; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Controls each factory-default command restores.</summary>
    public static IReadOnlyList<byte> CodesRestoredBy(DefaultsGroup group) => group switch
    {
        DefaultsGroup.LuminanceContrast => [VcpCode.Brightness, VcpCode.Contrast],
        _ => [VcpCode.ColorPreset, VcpCode.RedGain, VcpCode.GreenGain, VcpCode.BlueGain]
    };

    /// <summary>The group whose factory-default command restores <paramref name="code"/>, if any.</summary>
    public static DefaultsGroup? GroupOf(byte code) =>
        CodesRestoredBy(DefaultsGroup.LuminanceContrast).Contains(code) ? DefaultsGroup.LuminanceContrast
        : CodesRestoredBy(DefaultsGroup.Color).Contains(code) ? DefaultsGroup.Color
        : null;

    /// <summary>Whether the monitor advertises this factory-default command.</summary>
    public bool CanRestore(DefaultsGroup group) => IsControllable && Capabilities.VcpCodes.Contains((byte)group);

    /// <summary>
    /// Restores factory defaults for a group in the current mode. With <paramref name="only"/>, just that control is reset:
    /// the others in the group are written back to their previous values. Returns the factory values read afterwards.
    /// </summary>
    public IReadOnlyDictionary<byte, uint> RestoreDefaults(DefaultsGroup group, byte? only = null)
    {
        lock (gate)
        {
            EnsureWritable();
            if (!CanRestore(group))
                throw new NotSupportedException($"{Name} does not offer {VcpCode.Describe((byte)group)} (factory defaults).");
            var codes = CodesRestoredBy(group).Where(Supports).ToArray();
            var before = new Dictionary<byte, uint>();
            foreach (byte code in codes)
                try { before[code] = Read(code).Current; }
                catch (Exception error) when (RetryPolicy.IsTransient(error)) { }

            WritePolicy.Execute($"{Name}: restore factory {group}", () => transport.Write((byte)group, 1), delay, log);
            log.Write(LogLevel.Info, $"{Name}: factory {group} defaults restored via {transport.Id}" +
                (only is byte o ? $" (keeping everything except {VcpCode.Describe(o)})." : "."));
            delay.Wait(RestoreSettle);

            var after = new Dictionary<byte, uint>();
            foreach (byte code in codes)
                try { after[code] = Read(code).Current; }
                catch (Exception error) when (RetryPolicy.IsTransient(error)) { }

            if (only is byte keep)
                foreach (var (code, value) in before)
                    if (code != keep && after.TryGetValue(code, out uint now) && now != value)
                        try { Write(code, value); after[code] = value; }
                        catch (Exception error) when (RetryPolicy.IsTransient(error))
                        {
                            log.Write(LogLevel.Warning, $"{Name}: could not put {VcpCode.Describe(code)} back to {value}.", error);
                        }
            return after;
        }
    }

    /// <summary>Brightness levels of a built-in panel (6, 12, 18 … 100), or null when every value works.</summary>
    public IReadOnlyList<uint>? BrightnessLevels => transport.BrightnessLevels;

    /// <summary>The value the monitor will really take: the nearest panel level for laptop brightness, else unchanged.</summary>
    public uint Snap(byte code, uint raw)
    {
        if (code != VcpCode.Brightness || BrightnessLevels is not { Count: > 0 } levels) return raw;
        uint best = levels[0];
        foreach (uint level in levels)
        {
            long diff = Math.Abs((long)level - raw), bestDiff = Math.Abs((long)best - raw);
            if (diff < bestDiff || (diff == bestDiff && level > best)) best = level;   // ties go up, as the driver does (15 → 18)
        }
        return best;
    }

    /// <summary>Writes a control's known default (see <see cref="FeatureDefinition.DefaultValue"/>) and confirms it.</summary>
    public WriteOutcome RestoreDefaultValue(byte code)
    {
        lock (gate)
        {
            var definition = Features.FirstOrDefault(f => f.Code == code)?.Definition;
            if (definition?.DefaultValue is not uint value)
                throw new NotSupportedException($"{Name} has no known default for {VcpCode.Describe(code)}.");
            if (definition.WriteOnly) return WriteRemembered(definition, value);
            Write(code, value);
            return Verify(code, value, ReadPolicy);
        }
    }

    /// <summary>
    /// The value to show for a control: what the monitor reports, or for write-only controls the last value this app
    /// wrote (or the known default when nothing was written yet).
    /// </summary>
    public VcpValue CurrentValue(byte code)
    {
        var definition = Features.FirstOrDefault(f => f.Code == code)?.Definition;
        if (definition is not { WriteOnly: true }) return Read(code);
        uint top = definition.Maximum ?? 100;
        return new VcpValue(Remembered.Get(Key, code) ?? definition.DefaultValue ?? top / 2, top);
    }

    /// <summary>Where write-only values are kept (default: the app's remembered.json).</summary>
    public RememberedValues Remembered { get; init; } = RememberedValues.Current;

    /// <summary>
    /// Writes a write-only control one step at a time (the LG takes sharpness in steps of 10) from the last value written,
    /// then remembers the new value. There is no read-back: the monitor keeps reporting its old value.
    /// </summary>
    private WriteOutcome WriteRemembered(FeatureDefinition definition, uint raw)
    {
        lock (gate)
        {
            byte code = definition.Code;
            uint top = definition.Maximum ?? 100;
            uint step = definition.Step ?? 1;
            uint target = Math.Min(top, (uint)Math.Round(raw / (double)step) * step);
            uint from = Remembered.Get(Key, code) ?? definition.DefaultValue ?? target;
            uint value = from;
            while (value != target)
            {
                value = target > value ? Math.Min(target, value + step) : (value - target > step ? value - step : target);
                Write(code, value);
                if (value != target) delay.Wait(TimeSpan.FromMilliseconds(120));
            }
            if (from == target) Write(code, target);   // same value again: resend, the monitor may have been reset
            Remembered.Set(Key, code, target);
            return WriteOutcome.Applied;
        }
    }

    /// <summary>True when the input must be sent again after the picture returns from another computer (see quirks).</summary>
    public bool ResendsInputOnReturn => Quirk?.ResendInputOnReturn.Contains(transport.Id, StringComparer.OrdinalIgnoreCase) == true;

    /// <summary>
    /// One quiet read of the current input: no retries and no log lines, null when the display does not answer.
    /// Used for polling while another computer may own the monitor.
    /// </summary>
    public int? TryReadInputOnce()
    {
        lock (gate)
        {
            if (disposed || !IsControllable) return null;
            try { return (int)transport.Read(VcpCode.InputSource).Current; }
            catch (Exception error) when (error is not OutOfMemoryException) { return null; }
        }
    }

    /// <summary>How far the read-back may be from the request on panels with fixed levels (see <see cref="IMonitorTransport.SnapsToLevels"/>).</summary>
    public const uint LevelTolerance = 10;

    /// <summary>Reads back after a write until the monitor reports the expected value or the policy gives up.</summary>
    private WriteOutcome Verify(byte code, uint expected, RetryPolicy policy)
    {
        bool answered = false;
        uint last = 0;
        for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            delay.Wait(policy.DelayBefore(attempt + 1));
            try
            {
                last = transport.Read(code).Current;
                answered = true;
                if (last == expected) return WriteOutcome.Verified;
            }
            catch (Exception error) when (RetryPolicy.IsTransient(error))
            {
                log.Write(LogLevel.Debug, $"{Name}: read-back of {VcpCode.Describe(code)} failed ({error.Message}).");
            }
        }
        if (!answered)
        {
            log.Write(LogLevel.Info, $"{Name}: {VcpCode.Describe(code)} write accepted but not confirmed.");
            return WriteOutcome.Unverified;
        }
        // Laptop panels over WMI have a few fixed levels: the nearest one is the answer, not a failure.
        if (transport.SnapsToLevels && Math.Abs((long)last - expected) <= LevelTolerance)
        {
            log.Write(LogLevel.Info, $"{Name}: {VcpCode.Describe(code)} set to {last}, the panel's nearest level to {expected}.");
            return WriteOutcome.Verified;
        }
        log.Write(LogLevel.Warning, $"{Name}: {VcpCode.Describe(code)} reads {last}, expected {expected}.");
        return WriteOutcome.Mismatch;
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!IsActive) throw new NotSupportedException($"Windows is not using {Name} right now.");
        if (!IsControllable) throw new NotSupportedException($"{Name} has no working control path on this computer.");
    }

    private void EnsureWritable()
    {
        EnsureUsable();
        if (!transport.SupportsWrites)
            throw new NotSupportedException($"Writes through {transport.Id} are disabled for {Name}.");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            transport.Dispose();
        }
    }

    public override string ToString() => $"{Name} [{transport.Id}]";
}

/// <summary>How Windows identifies a display.</summary>
/// <param name="Key">Stable key for settings: device ID plus physical index.</param>
/// <param name="FriendlyName">Description reported by Windows, or a generic label.</param>
/// <param name="DeviceId">Windows monitor device ID, e.g. MONITOR\GSM7714\{...}\0001.</param>
/// <param name="GdiName">Windows display source, e.g. \\.\DISPLAY1 (empty for WMI-only panels).</param>
/// <param name="PnpId">EDID manufacturer + product, e.g. GSM7714, when known.</param>
/// <param name="IsPrimary">Whether this source is the Windows main display.</param>
/// <param name="IsBuiltIn">Built-in laptop panel controlled through WMI.</param>
/// <param name="IsActive">Whether Windows currently outputs to this display; inactive ones are listed but not controlled.</param>
public sealed record MonitorIdentity(
    string Key, string FriendlyName, string DeviceId, string GdiName, string? PnpId, bool IsPrimary, bool IsBuiltIn,
    bool IsActive = true);

/// <summary>Placeholder for displays that Windows lists but that no transport can control.</summary>
internal sealed class UnavailableTransport : IMonitorTransport
{
    public static UnavailableTransport Instance { get; } = new();
    public string Id => "none";
    public bool SupportsWrites => false;
    public string? ReadCapabilities() => null;
    public VcpValue Read(byte code) => throw new NotSupportedException("This display has no working control path.");
    public void Write(byte code, uint value) => throw new NotSupportedException("This display has no working control path.");
    public void Dispose() { }
}
