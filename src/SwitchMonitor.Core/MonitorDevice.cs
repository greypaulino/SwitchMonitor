using SwitchMonitor.Core.Capabilities;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Reliability;
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
    Mismatch
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
    public bool IsControllable => transport is not UnavailableTransport;
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

    public void Write(byte code, uint value)
    {
        lock (gate)
        {
            EnsureWritable();
            WritePolicy.Execute($"{Name}: write {VcpCode.Describe(code)}={value}", () => transport.Write(code, value), delay, log);
            log.Write(LogLevel.Info, $"{Name}: {VcpCode.Describe(code)} set to {value} via {transport.Id}.");
        }
    }

    /// <summary>Sets a continuous feature (brightness, contrast, volume...) as 0-100 percent of its reported maximum.</summary>
    public WriteOutcome SetPercent(byte code, double percent)
    {
        lock (gate)
        {
            EnsureWritable();
            if (!maximums.TryGetValue(code, out uint maximum)) maximum = Read(code).Maximum;
            uint raw = VcpValue.FromPercent(percent, maximum);
            Write(code, raw);
            return Verify(code, raw, ReadPolicy);
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
        log.Write(LogLevel.Warning, $"{Name}: {VcpCode.Describe(code)} reads {last}, expected {expected}.");
        return WriteOutcome.Mismatch;
    }

    private void EnsureUsable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
public sealed record MonitorIdentity(
    string Key, string FriendlyName, string DeviceId, string GdiName, string? PnpId, bool IsPrimary, bool IsBuiltIn);

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
