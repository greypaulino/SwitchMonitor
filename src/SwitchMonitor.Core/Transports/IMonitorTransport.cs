using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>
/// A way to send DDC/CI-style feature commands to one physical display.
/// Implementations are not required to be thread-safe; <see cref="Monitor"/> serializes calls.
/// </summary>
public interface IMonitorTransport : IDisposable
{
    /// <summary>Stable identifier: "ddc-ci", "wmi", "intel-cui" or "amd-adl".</summary>
    string Id { get; }

    /// <summary>False when writes are disabled, for example unvalidated experimental paths.</summary>
    bool SupportsWrites { get; }

    /// <summary>Raw MCCS capabilities string, or null when this transport cannot provide one.</summary>
    string? ReadCapabilities();

    VcpValue Read(byte code);

    void Write(byte code, uint value);

    /// <summary>
    /// The panel only has some brightness levels (laptop screens over WMI: 15 % becomes 18 %), so a read-back near the
    /// requested value is the closest level, not a failure.
    /// </summary>
    bool SnapsToLevels => false;
}

/// <summary>Thrown when a transport cannot perform the requested operation on this display.</summary>
public sealed class TransportException : Exception
{
    public string TransportId { get; }

    public TransportException(string transportId, string message, Exception? inner = null)
        : base(message, inner) => TransportId = transportId;
}
