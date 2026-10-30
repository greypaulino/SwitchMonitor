using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Transports;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Diagnostics;

/// <summary>
/// In-memory displays for looking at the interface without hardware (<c>SwitchMonitor.exe --demo [demo|worst|empty]</c>).
/// Nothing here reaches a real monitor: every write lands in a dictionary.
/// </summary>
public static class DemoMonitors
{
    public static readonly string[] Scenarios = ["demo", "worst", "empty"];

    public static IReadOnlyList<MonitorDevice> Create(string scenario, ICoreLog? log = null)
    {
        // The demo's own data folder: settings learned in the demo show up there as on a real monitor.
        QuirkDatabase quirks;
        try { quirks = QuirkDatabase.LoadDefault(); }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or IOException) { quirks = QuirkDatabase.LoadEmbedded(); }
        var result = new List<MonitorDevice>();
        switch (scenario)
        {
            case "empty":
                break;
            case "worst":
                result.Add(Lg(quirks, log));
                result.Add(LongGeneric(log));
                result.Add(Device(new MonitorIdentity("demo|ddc-fail", "Generic PnP Monitor", @"MONITOR\HWP3245\{demo}\0003",
                    "", "HWP3245", false, false), UnavailableTransport.Instance, null, log));
                result.Add(LaptopOff(log));
                break;
            default:
                result.Add(Lg(quirks, log));
                result.Add(LaptopOff(log));
                break;
        }
        return result;
    }

    /// <summary>The scenes.json a demo starts with: the four templates, plus long and many profiles for the worst case.</summary>
    public static string ScenesJson(string scenario) => scenario switch
    {
        "empty" => """{ "version": 4, "scenes": [] }""",
        "worst" => Scenes.SceneTemplates.Json.Replace("\"scenes\": [", """
            "scenes": [
              { "name": "Night — reading on the sofa (warm)", "settings": { "brightness": 15, "color preset": "5000 K" } },
              { "name": "Photo editing (sRGB, 120 cd/m²)", "settings": { "brightness": 45, "contrast": 70 } },
              { "name": "Presentation", "settings": { "brightness": 100 } },
              { "name": "Jo", "settings": { "brightness": 60 } },
            """),
        _ => Scenes.SceneTemplates.Json
    };

    // Mirrors the LG 29WK600 on DESKTOP-BPMFO0B (probe of 2026-10-24) so the real quirk drives the controls.
    private static MonitorDevice Lg(QuirkDatabase quirks, ICoreLog? log)
    {
        const string deviceId = @"MONITOR\GSM7714\{demo}\0001";
        var transport = new DemoTransport("intel-cui",
            "(prot(monitor)type(lcd)model(29WK600)cmds(01 02 03 0C E3 F3)vcp(02 04 05 08 10 12 14(05 08) 15 16 18 1A " +
            "60(11 12 0F) 62 8D(01 02) F4 F7(00 01 02 03) F8(00 01) F9 FE)mccs_ver(2.1))");
        transport.Set(0x10, 25); transport.Set(0x12, 47); transport.Set(0x62, 100); transport.Set(0x8D, 2, 2);
        transport.Set(0x14, 0x05, 0x0B); transport.Set(0x15, 0x0B, 0x30); transport.Set(0x60, 0x11, 0x0F);
        transport.Set(0x16, 50); transport.Set(0x18, 50); transport.Set(0x1A, 50);
        transport.Set(0x6C, 50); transport.Set(0x6E, 50); transport.Set(0x70, 50);
        transport.Set(0xF7, 0x01, 0x03); transport.Set(0xF8, 0x00, 0x01); transport.Set(0xF9, 50); transport.Set(0xFE, 0x02, 0x07);
        transport.Set(0xF4, 0x90, 0xFF); transport.Set(0x87, 50);
        // Codes nobody has mapped, for the Learn settings wizard: a level that only works in the Custom picture modes,
        // a three-value list and a standard saturation the LG does not advertise.
        transport.Set(0xE9, 40); transport.Set(0xEA, 1, 2); transport.Set(0x8A, 50);
        // As on the real LG: a code that reboots the monitor (hidden) and information that is never written.
        transport.Set(0xF6, 2, 255); transport.Set(0x20, 0); transport.Set(0x4D, 30771, 0xFFFF);
        transport.IgnoreWrite = (code, _) => code == 0xE9 && transport.Peek(0x15) is not (0x0B or 0x0E);
        // The LG switches inputs through its vendor code 0xF4; the standard 0x60 then reports the new input.
        transport.AfterWrite = (code, value) =>
        {
            if (code == 0xF4 && value switch { 0x90 => 0x11u, 0x91 => 0x12u, 0xD0 => 0x0Fu, _ => 0u } is var input and > 0)
                transport.Set(0x60, input, 0x0F);
        };
        return Device(new MonitorIdentity($"{deviceId}|0", "LG ULTRAWIDE", deviceId, "", "GSM7714", true, false),
            transport, quirks.Match(deviceId), log);
    }

    // Worst case: a long marketing name, seven inputs, vendor codes nobody mapped, a picture mode list in MCCS terms.
    private static MonitorDevice LongGeneric(ICoreLog? log)
    {
        const string deviceId = @"MONITOR\DELA1F2\{demo}\0002";
        var transport = new DemoTransport("ddc-ci",
            "(prot(monitor)type(lcd)model(U2723QE)vcp(02 04 05 08 10 12 14(04 05 06 08 0B) 16 18 1A " +
            "60(0F 10 11 12 13 1B 1C) 62 8D(01 02) 87 AC AE B6 C6 C8 C9 CC(02 03 04 05 06 07 08 09 0A 0C 0D 14 16 1E) " +
            "D6(01 04 05) DC(00 02 03 05) DF E0 E1 E2(00 1D) F0(00 08) F1 F2 FD)mccs_ver(2.1))");
        transport.Set(0x10, 100); transport.Set(0x12, 5); transport.Set(0x62, 0); transport.Set(0x8D, 1, 2);
        transport.Set(0x14, 0x05, 0x0B); transport.Set(0x60, 0x1B, 0x1C); transport.Set(0x87, 50);
        transport.Set(0x16, 100); transport.Set(0x18, 100); transport.Set(0x1A, 100);
        transport.Set(0xCC, 0x02, 0x1E); transport.Set(0xD6, 0x01, 0x05); transport.Set(0xDC, 0x00, 0x05);
        transport.Set(0xE2, 0x00, 0x1D); transport.Set(0xF0, 0x00, 0x08);
        transport.FailingCodes.Add(0xE1);
        return Device(new MonitorIdentity($"{deviceId}|0",
                "DELL U2723QE 27\" 4K USB-C Hub Monitor (DisplayPort 2 via Thunderbolt dock)", deviceId, "", "DELA1F2", false, false),
            transport, null, log);
    }

    private static MonitorDevice LaptopOff(ICoreLog? log) =>
        Device(new MonitorIdentity("demo|wmi-laptop", "N5050 Laptop Screen", @"DISPLAY\CMO15A4\{demo}", "", "CMO15A4",
            false, true, IsActive: false), new DemoTransport("wmi", "(vcp(10))"), null, log);

    private static MonitorDevice Device(MonitorIdentity identity, IMonitorTransport transport, MonitorQuirk? quirk, ICoreLog? log) =>
        new(identity, transport, quirk, log);

    private sealed class DemoTransport(string id, string capabilities) : IMonitorTransport
    {
        private readonly Dictionary<byte, VcpValue> values = [];
        // Codes not modeled answer only when advertised (as 50 of 100), so a deep scan finds what a monitor would.
        private readonly HashSet<byte> advertised = Capabilities.MccsCapabilitiesParser.Parse(capabilities).VcpCodes.ToHashSet();
        public HashSet<byte> FailingCodes { get; } = [];
        public Action<byte, uint>? AfterWrite { get; set; }
        /// <summary>Writes the monitor accepts but does not apply (a setting locked by the picture mode).</summary>
        public Func<byte, uint, bool>? IgnoreWrite { get; set; }

        public uint Peek(byte code)
        {
            lock (values) return values.TryGetValue(code, out var value) ? value.Current : 0;
        }
        public string Id => id;
        public bool SupportsWrites => true;

        public void Set(byte code, uint value, uint maximum = 100)
        {
            lock (values) values[code] = new VcpValue(value, maximum);
        }

        public string? ReadCapabilities() => capabilities;

        public VcpValue Read(byte code)
        {
            Thread.Sleep(15);   // a little of the real bus latency, so loading states are visible
            if (FailingCodes.Contains(code)) throw new TransportException(Id, $"demo: VCP 0x{code:X2} does not answer.");
            lock (values)
            {
                if (values.TryGetValue(code, out var value)) return value;
                if (advertised.Contains(code)) return new VcpValue(50, 100);
            }
            throw new TransportException(Id, $"demo: VCP 0x{code:X2} is not supported.");
        }

        public void Write(byte code, uint value)
        {
            Thread.Sleep(80);
            if (FailingCodes.Contains(code)) throw new TransportException(Id, $"demo: VCP 0x{code:X2} ignores writes.");
            if (IgnoreWrite?.Invoke(code, value) == true) return;
            lock (values) values[code] = new VcpValue(value, values.TryGetValue(code, out var old) ? old.Maximum : 100);
            AfterWrite?.Invoke(code, value);
        }

        public void Dispose() { }
    }
}
