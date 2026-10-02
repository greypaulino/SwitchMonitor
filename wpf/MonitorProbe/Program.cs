using SwitchMonitor.Wpf;

if (args is ["--selftest-return-sync"])
{
    Check([17, 17], [ReturnSyncDecision.Wait, ReturnSyncDecision.Wait]);
    Check([0, 17], [ReturnSyncDecision.Wait, ReturnSyncDecision.Synchronize]);
    Check([0, 0, 17], [ReturnSyncDecision.Wait, ReturnSyncDecision.Wait, ReturnSyncDecision.Synchronize]);
    Check([0, 18], [ReturnSyncDecision.Wait, ReturnSyncDecision.Cancel]);
    Console.WriteLine("Return synchronization state: PASS (no monitor command sent)");
    return;
}

if (args is ["--verify-current-input-write"])
{
    using var currentScope = new MonitorScope(MonitorService.Enumerate());
    var lg = currentScope.Monitors.Where(m => m.IsLegacyLg && m.HasIntelLegacyBackend).ToArray();
    if (lg.Length != 1) throw new InvalidOperationException("Expected exactly one LG with the validated Intel backend.");
    var current = lg[0].ReadInput().Current;
    if (current is not (17 or 18 or 15))
        throw new InvalidOperationException($"Cannot safely repeat unknown input {current}.");
    lg[0].SetInput((int)current);
    await Task.Delay(350);
    var after = lg[0].ReadInput().Current;
    if (after != current)
        throw new InvalidOperationException($"Input changed unexpectedly from {current} to {after}.");
    Console.WriteLine($"Current-input write: PASS ({MonitorService.PortName((int)current)} remained active)");
    return;
}

using var scope = new MonitorScope(MonitorService.Enumerate());
Console.WriteLine($"Physical monitors: {scope.Monitors.Count}");
foreach (var monitor in scope.Monitors)
{
    Console.WriteLine($"{monitor.Name} | {monitor.DeviceId}");
    if (args is ["--detailed"])
    {
        foreach (string device in MonitorService.DescribeWindowsDevices(monitor.DisplayName))
            Console.WriteLine(device);
        try { Console.WriteLine("Capabilities: " + monitor.ReadCapabilitiesForDiagnostics()); }
        catch (Exception error) { Console.WriteLine("Capabilities error: " + error.Message); }
    }
    try
    {
        monitor.ReadInputs();
        Console.WriteLine("Inputs: " + string.Join(", ", monitor.InputCodes.Select(MonitorService.PortName)));
    }
    catch (Exception error) { Console.WriteLine("Inputs: " + error.Message); }
    try
    {
        var (current, maximum) = monitor.ReadBrightness();
        Console.WriteLine($"Brightness: {current}/{maximum}");
    }
    catch (Exception error) { Console.WriteLine("Brightness: " + error); }
    try
    {
        var (current, _) = monitor.ReadInput();
        Console.WriteLine("Input: " + current);
    }
    catch (Exception error) { Console.WriteLine("Input: " + error.Message); }
}

static void Check(int[] readings, ReturnSyncDecision[] expected)
{
    var watch = new ReturnSyncState(17);
    for (int i = 0; i < readings.Length; i++)
        if (watch.Observe(readings[i]) != expected[i])
            throw new InvalidOperationException($"Return sync failed at reading {i}.");
}

internal sealed class MonitorScope(IReadOnlyList<PhysicalMonitor> monitors) : IDisposable
{
    public IReadOnlyList<PhysicalMonitor> Monitors { get; } = monitors;
    public void Dispose()
    {
        foreach (var monitor in Monitors) monitor.Dispose();
    }
}
