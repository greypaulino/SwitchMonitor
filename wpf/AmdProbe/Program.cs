using System.Text;
using SwitchMonitor.Wpf;

string reportPath = Path.Combine(AppContext.BaseDirectory, "amd-probe.txt");
var report = new StringBuilder("SwitchMonitor AMD ADL2 read-only probe\n");
report.AppendLine("No brightness or input setting commands are sent.");
File.WriteAllText(reportPath, report.ToString());

if (args is ["--packet-test"])
{
    var expected = new Dictionary<int, string>
    {
        [17] = "6E508403F40090DD", [18] = "6E508403F40091DC", [15] = "6E508403F400D09D"
    };
    foreach (var pair in expected)
    {
        byte value = pair.Key switch { 17 => 0x90, 18 => 0x91, _ => 0xD0 };
        string actual = Convert.ToHexString(AmdLegacyDdc.Packet(0x50, [0x84, 0x03, 0xF4, 0, value]));
        if (actual != pair.Value) throw new InvalidOperationException($"Packet {pair.Key}: {actual}");
    }
    var brightness = AmdLegacyDdc.ParseVcpReply(Convert.FromHexString("6E880200100000640034F42000002E00"), 16, 0x10);
    var input = AmdLegacyDdc.ParseVcpReply(Convert.FromHexString("6E88020060000012000FC90003000200"), 16, 0x60);
    if (brightness != (52u, 100u) || input != (15u, 18u))
        throw new InvalidOperationException("AMD VCP reply parsing failed.");
    try
    {
        AmdLegacyDdc.ParseVcpReply(Convert.FromHexString("6E880200100000640034002000002E00"), 16, 0x10);
        throw new InvalidOperationException("A bad checksum was accepted.");
    }
    catch (InvalidDataException) { }
    Console.WriteLine("AMD LG packet checksum: PASS (no driver call)");
    Console.WriteLine("AMD VCP reply parsing: PASS (no driver call)");
    return;
}

try
{
    report.AppendLine("ADL2 DLL present: " + AmdLegacyDdc.IsAvailable);
    var monitors = MonitorService.Enumerate();
    try
    {
        foreach (var monitor in monitors)
            report.AppendLine($"Windows monitor: {monitor.Name} | {monitor.DeviceId} | {monitor.DisplayName}");
        using var amd = new AmdLegacyDdc();
        foreach (var display in amd.Displays)
            report.AppendLine($"ADL display: GDI={display.GdiName} adapter={display.AdapterIndex} " +
                $"display={display.DisplayIndex} name={display.Name} EDID product=" +
                (display.ProductCode is null ? "unavailable" : $"0x{display.ProductCode:X4}"));
        foreach (var monitor in monitors)
        {
            if (!monitor.IsLegacyLg) continue;
            try
            {
                var target = amd.Select(monitor.DisplayName, monitor.DeviceId);
                report.AppendLine($"Selected AMD target: adapter={target.AdapterIndex} display={target.DisplayIndex}");
                foreach (byte feature in new byte[] { 0x10, 0x60 })
                {
                    try
                    {
                        var value = amd.ReadVcp(target, feature);
                        report.AppendLine($"VCP {feature:X2}: {value.Current}/{value.Maximum}");
                    }
                    catch (Exception error) { report.AppendLine($"VCP {feature:X2} error: {error.Message}"); }
                    try { report.AppendLine($"VCP {feature:X2} split: {amd.ProbeSplitVcp(target, feature)}"); }
                    catch (Exception error) { report.AppendLine($"VCP {feature:X2} split error: {error.Message}"); }
                }
            }
            catch (Exception error) { report.AppendLine("AMD target error: " + error.Message); }
        }
    }
    finally { foreach (var monitor in monitors) monitor.Dispose(); }
}
catch (Exception error)
{
    report.AppendLine("AMD probe error: " + error);
    Environment.ExitCode = 1;
}
finally
{
    File.WriteAllText(reportPath, report.ToString());
    Console.WriteLine(report);
    Console.WriteLine("Report: " + reportPath);
}
