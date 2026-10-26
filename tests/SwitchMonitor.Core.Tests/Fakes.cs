using SwitchMonitor.Core.Reliability;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Tests;

/// <summary>In-memory monitor. Write hooks let tests model vendor codes and flaky panels.</summary>
internal sealed class FakeTransport : IMonitorTransport
{
    public Dictionary<byte, VcpValue> Values { get; } = [];
    public List<(byte Code, uint Value)> Writes { get; } = [];
    public string? Capabilities { get; set; }
    public Action<FakeTransport, byte, uint>? OnWrite { get; set; }
    public Func<byte, Exception?>? FailRead { get; set; }
    public int FailWritesRemaining { get; set; }
    public bool SupportsWrites { get; set; } = true;
    public bool Disposed { get; private set; }
    public string Id { get; set; } = "fake";
    public bool SnapsToLevels { get; set; }
    public IReadOnlyList<uint>? BrightnessLevels { get; set; }

    public string? ReadCapabilities() => Capabilities;

    public VcpValue Read(byte code)
    {
        if (FailRead?.Invoke(code) is Exception error) throw error;
        return Values.TryGetValue(code, out var value)
            ? value
            : throw new TransportException(Id, $"VCP 0x{code:X2} not modeled.");
    }

    public void Write(byte code, uint value)
    {
        if (FailWritesRemaining > 0)
        {
            FailWritesRemaining--;
            throw new TransportException(Id, "simulated busy panel");
        }
        Writes.Add((code, value));
        if (OnWrite is not null) OnWrite(this, code, value);
        else Values[code] = new VcpValue(value, Values.TryGetValue(code, out var old) ? old.Maximum : 0);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class RecordingDelay : IDelay
{
    public List<TimeSpan> Waits { get; } = [];
    public void Wait(TimeSpan duration) => Waits.Add(duration);
}

internal static class TestMonitors
{
    public static MonitorIdentity Identity(string deviceId = @"MONITOR\DEL4321\{4d36e96e-e325-11ce-bfc1-08002be10318}\0001") =>
        new($"{deviceId}|0", "Test display", deviceId, @"\\.\DISPLAY1", Quirks.QuirkDatabase.ExtractPnpId(deviceId), true, false);
}
