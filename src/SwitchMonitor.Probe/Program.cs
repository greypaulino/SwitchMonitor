using SwitchMonitor.Core;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Vcp;

// Read-only diagnostic: lists displays, chosen transport, capabilities and current values.
// It never writes to a monitor, so it is safe to run on any computer.

var log = new MemoryLog();
var options = CoreOptions.FromDataDirectory();
var monitors = MonitorEnumerator.Enumerate(options, log: log);
try
{
    Console.WriteLine($"SwitchMonitor v2 probe — {monitors.Count} display(s), AMD writes {(options.AllowAmdWrites ? "enabled" : "disabled")}");
    foreach (var monitor in monitors)
    {
        var id = monitor.Identity;
        Console.WriteLine();
        Console.WriteLine($"{monitor.Name}{(id.IsPrimary ? " (main display)" : "")}{(id.IsBuiltIn ? " (built-in)" : "")}");
        Console.WriteLine($"  Device:    {id.DeviceId}");
        Console.WriteLine($"  Source:    {(id.GdiName.Length > 0 ? id.GdiName : "-")}   PnP: {id.PnpId ?? "-"}");
        Console.WriteLine($"  Transport: {monitor.TransportId}{(monitor.Quirk is { } q ? $"   Quirk: {q.Id}" : "")}");
        if (!monitor.IsControllable) continue;

        var caps = monitor.Capabilities;
        Console.WriteLine($"  Capabilities: {(caps.VcpCodes.Count > 0 ? caps.ToString() : "(not reported by this transport)")}");
        if (monitor.Inputs.Count > 0)
            Console.WriteLine($"  Inputs:    {string.Join(", ", monitor.Inputs.Select(monitor.DescribeInput))}");

        foreach (byte code in new[] { VcpCode.Brightness, VcpCode.Contrast, VcpCode.AudioVolume, VcpCode.InputSource })
        {
            if (!monitor.Supports(code)) continue;
            try
            {
                var value = monitor.Read(code);
                string shown = code == VcpCode.InputSource
                    ? monitor.DescribeInput((int)value.Current)
                    : $"{value.Current}/{value.Maximum} ({value.Percent}%)";
                Console.WriteLine($"  {VcpCode.Describe(code),-12} {shown}");
            }
            catch (Exception error)
            {
                Console.WriteLine($"  {VcpCode.Describe(code),-12} read failed: {error.Message}");
            }
        }
    }

    if (args.Contains("--log"))
    {
        Console.WriteLine();
        foreach (string entry in log.Entries) Console.WriteLine(entry);
    }
}
finally
{
    foreach (var monitor in monitors) monitor.Dispose();
}
