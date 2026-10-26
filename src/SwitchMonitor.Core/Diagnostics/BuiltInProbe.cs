using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SwitchMonitor.Core.Transports;

namespace SwitchMonitor.Core.Diagnostics;

/// <summary>
/// Read-only report of what Windows and the graphics driver offer for each active display, with built-in panels
/// (laptop screens) flagged: connection type and EDID, real brightness levels, AC/battery brightness, power-plan
/// display timers, light sensors, display modes, HDR, gamma ramp and the driver. Nothing is changed.
/// </summary>
public static class BuiltInProbe
{
    /// <summary>An active display as Windows sees it, with a short panel description from its EDID.</summary>
    public sealed record DisplaySummary(string GdiName, string Name, bool IsBuiltIn, string? Panel);

    /// <summary>Active displays (connection type and EDID); empty when Windows cannot say.</summary>
    public static IReadOnlyList<DisplaySummary> Displays()
    {
        try
        {
            return DisplayConfig.ActivePaths().Select(d =>
            {
                string? panel = null;
                try { if (Edid.Read(d.DevicePath) is { } edid) panel = Edid.Summary(edid); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
                return new DisplaySummary(d.GdiName, d.FriendlyName, d.IsBuiltIn, panel);
            }).ToArray();
        }
        catch (Exception error) when (error is Win32Exception or EntryPointNotFoundException or DllNotFoundException) { return []; }
    }

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>();
        void Line(string text) => lines.Add(text);
        void Section(string title, Action body)
        {
            Line("");
            Line(title);
            try { body(); }
            catch (Exception error) when (error is not OutOfMemoryException) { Line($"  failed: {error.Message}"); }
        }

        var displays = new List<DisplayPath>();
        Section("Displays (Windows display configuration)", () =>
        {
            displays.AddRange(DisplayConfig.ActivePaths());
            if (displays.Count == 0) Line("  none reported");
            foreach (var d in displays)
            {
                Line($"  {d.GdiName}  {d.FriendlyName}{(d.IsBuiltIn ? "  [BUILT-IN]" : "")}");
                Line($"    connection: {d.Technology}   EDID id: {d.EdidVendor}{d.EdidProduct:X4}   refresh: {d.RefreshHz:0.##} Hz");
                Line($"    HDR: {d.Hdr}" + (d.SdrWhiteNits is double nits ? $"   SDR white level: {nits:0} nits" : ""));
                Line($"    adapter: {Adapter(d.GdiName) ?? "?"}");
            }
        });

        foreach (var d in displays)
        {
            Section($"EDID of {d.GdiName} ({d.FriendlyName})", () =>
            {
                var edid = Edid.Read(d.DevicePath);
                if (edid is null) { Line("  not found in the registry"); return; }
                foreach (string item in Edid.Describe(edid)) Line("  " + item);
            });
            Section($"Display modes of {d.GdiName}", () => { foreach (string item in DisplayModes.Describe(d.GdiName)) Line("  " + item); });
            Section($"Gamma ramp of {d.GdiName} (software color, red/green/blue, night tint)", () =>
            {
                foreach (string item in Gamma.Describe(d.GdiName)) Line("  " + item);
            });
        }

        if (displays.Any(d => d.IsBuiltIn))
        {
            Section("Built-in backlight (\\\\.\\LCD)", () =>
            {
                try
                {
                    var levels = LcdBrightness.SupportedLevels();
                    Line($"  supported levels ({levels.Count}): {string.Join(", ", levels)}");
                }
                catch (Win32Exception error) { Line($"  supported levels: not reported ({error.Message})"); }
                try
                {
                    var (ac, dc) = LcdBrightness.ReadBoth();
                    Line($"  current: plugged in {ac}%, on battery {dc}%");
                }
                catch (Win32Exception error) { Line($"  current: not readable ({error.Message})"); }
            });
        }
        else Line("\nNo built-in display is active (lid closed or external only): backlight checks skipped.");

        Section("Power (current plan)", () => { foreach (string item in PowerPlan.Describe()) Line("  " + item); });
        Section("Sensors (ambient light for adaptive brightness)", () => { foreach (string item in Sensors.Describe()) Line("  " + item); });
        Section("Graphics driver", () => { foreach (string item in Driver.Describe()) Line("  " + item); });
        return lines;
    }

    // ---- Display configuration (connection type, HDR) ----

    internal sealed record DisplayPath(string GdiName, string FriendlyName, string Technology, bool IsBuiltIn, string EdidVendor,
        ushort EdidProduct, double RefreshHz, string Hdr, double? SdrWhiteNits, string DevicePath);

    private static class DisplayConfig
    {
        private const uint QdcOnlyActivePaths = 0x2;

        [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] private struct Rational { public uint Numerator; public uint Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SourceInfo { public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint StatusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TargetInfo
        {
            public Luid AdapterId; public uint Id; public uint ModeInfoIdx; public uint OutputTechnology; public uint Rotation;
            public uint Scaling; public Rational RefreshRate; public uint ScanLineOrdering; public int TargetAvailable; public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public SourceInfo Source; public TargetInfo Target; public uint Flags; }

        [StructLayout(LayoutKind.Sequential, Size = 64)] private struct ModeInfo { public uint InfoType; public uint Id; public Luid AdapterId; }

        [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type; public uint Size; public Luid AdapterId; public uint Id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SourceName { public Header Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string GdiName; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TargetName
        {
            public Header Header; public uint Flags; public uint OutputTechnology; public ushort EdidManufactureId; public ushort EdidProductCodeId;
            public uint ConnectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AdvancedColor { public Header Header; public uint Value; public uint ColorEncoding; public uint BitsPerColorChannel; }

        [StructLayout(LayoutKind.Sequential)] private struct SdrWhite { public Header Header; public uint Level; }

        [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths, ref uint modeCount,
            [Out] ModeInfo[] modes, IntPtr topology);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName info);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName info);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColor info);
        [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SdrWhite info);

        public static IReadOnlyList<DisplayPath> ActivePaths()
        {
            int status = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount);
            if (status != 0) throw new Win32Exception(status);
            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            status = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (status != 0) throw new Win32Exception(status);

            var result = new List<DisplayPath>();
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new SourceName { Header = Head(1, Marshal.SizeOf<SourceName>(), path.Source.AdapterId, path.Source.Id) };
                DisplayConfigGetDeviceInfo(ref source);
                var target = new TargetName { Header = Head(2, Marshal.SizeOf<TargetName>(), path.Target.AdapterId, path.Target.Id) };
                DisplayConfigGetDeviceInfo(ref target);
                var color = new AdvancedColor { Header = Head(9, Marshal.SizeOf<AdvancedColor>(), path.Target.AdapterId, path.Target.Id) };
                string hdr = DisplayConfigGetDeviceInfo(ref color) != 0 ? "not reported (Windows too old?)"
                    : (color.Value & 1) == 0 ? "not supported"
                    : (color.Value & 2) != 0 ? $"supported, ON ({color.BitsPerColorChannel} bits)" : "supported, off";
                var white = new SdrWhite { Header = Head(11, Marshal.SizeOf<SdrWhite>(), path.Target.AdapterId, path.Target.Id) };
                double? nits = DisplayConfigGetDeviceInfo(ref white) == 0 && (color.Value & 2) != 0 ? white.Level / 1000.0 * 80 : null;
                double hz = path.Target.RefreshRate.Denominator == 0 ? 0
                    : path.Target.RefreshRate.Numerator / (double)path.Target.RefreshRate.Denominator;
                uint tech = target.OutputTechnology;
                result.Add(new DisplayPath(source.GdiName ?? "?", string.IsNullOrEmpty(target.FriendlyName) ? "(no name)" : target.FriendlyName,
                    Technology(tech), IsBuiltIn(tech), Vendor(target.EdidManufactureId), target.EdidProductCodeId, hz, hdr, nits,
                    target.DevicePath ?? ""));
            }
            return result;
        }

        private static Header Head(uint type, int size, Luid adapter, uint id) => new() { Type = type, Size = (uint)size, AdapterId = adapter, Id = id };

        private static bool IsBuiltIn(uint tech) => tech is 6 or 11 or 13 or 0x80000000;

        private static string Technology(uint tech) => tech switch
        {
            0 => "VGA", 4 => "DVI", 5 => "HDMI", 6 => "LVDS (built-in)", 10 => "DisplayPort", 11 => "embedded DisplayPort (built-in)",
            12 => "UDI", 13 => "embedded UDI (built-in)", 15 => "Miracast (wireless)", 16 => "indirect (USB/dock)",
            17 => "indirect (virtual)", 0x80000000 => "internal (built-in)", _ => $"other ({tech})"
        };

        /// <summary>EDID manufacturer id: three letters packed in 5-bit groups, big-endian.</summary>
        internal static string Vendor(ushort id)
        {
            int v = (id >> 8) | ((id & 0xFF) << 8);
            return new string([(char)('A' - 1 + ((v >> 10) & 31)), (char)('A' - 1 + ((v >> 5) & 31)), (char)('A' - 1 + (v & 31))]);
        }
    }

    private static string? Adapter(string gdiName)
    {
        var device = Native.NativeMethods.NewDisplayDevice();
        for (uint i = 0; Native.NativeMethods.EnumDisplayDevices(null, i, ref device, 0); i++, device = Native.NativeMethods.NewDisplayDevice())
            if (string.Equals(device.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase)) return device.DeviceString;
        return null;
    }

    // ---- EDID (manufacturer, panel name and part number, size, year) ----

    private static class Edid
    {
        /// <summary>\\?\DISPLAY#AUO213E#5&amp;...#{guid} → HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\AUO213E\5&amp;...\Device Parameters\EDID.</summary>
        public static byte[]? Read(string devicePath)
        {
            var parts = devicePath.Split('#');
            if (parts.Length < 3) return null;
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\DISPLAY\{parts[1]}\{parts[2]}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }

        /// <summary>"CMO XM5XGN156B6 · 15.6″ · 1366×768": maker, model or panel part number, diagonal and native size.</summary>
        public static string? Summary(byte[] edid)
        {
            if (edid.Length < 128) return null;
            string vendor = DisplayConfig.Vendor((ushort)(edid[8] | (edid[9] << 8)));
            string? model = null;
            string? native = null;
            for (int offset = 54; offset <= 108; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0)
                {
                    native ??= $"{edid[offset + 2] | ((edid[offset + 4] & 0xF0) << 4)}×{edid[offset + 5] | ((edid[offset + 7] & 0xF0) << 4)}";
                    continue;
                }
                string text = Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n')[0].Trim();
                if (edid[offset + 3] == 0xFC && text.Length > 0) model = text;
                else if (edid[offset + 3] == 0xFE && text.Length > 0) model ??= text;
            }
            double inches = Math.Round(Math.Sqrt(edid[21] * edid[21] + edid[22] * edid[22]) / 2.54, 1);
            var parts = new List<string> { model is null ? vendor : $"{vendor} {model}" };
            if (inches > 0) parts.Add($"{inches}″");
            if (native is not null) parts.Add(native);
            return string.Join(" · ", parts);
        }

        public static IEnumerable<string> Describe(byte[] edid)
        {
            if (edid.Length < 128) { yield return $"too short ({edid.Length} bytes)"; yield break; }
            ushort vendor = (ushort)(edid[8] | (edid[9] << 8));
            yield return $"manufacturer {DisplayConfig.Vendor(vendor)}, product 0x{edid[10] | (edid[11] << 8):X4}, made {1990 + edid[17]} (week {edid[16]})";
            yield return $"size {edid[21]} x {edid[22]} cm (~{Math.Round(Math.Sqrt(edid[21] * edid[21] + edid[22] * edid[22]) / 2.54, 1)}\"), " +
                         ((edid[20] & 0x80) != 0 ? $"digital input{BitDepth(edid[20])}" : "analog input");
            for (int offset = 54; offset <= 108; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0)
                {
                    int width = edid[offset + 2] | ((edid[offset + 4] & 0xF0) << 4);
                    int height = edid[offset + 5] | ((edid[offset + 7] & 0xF0) << 4);
                    yield return $"native timing {width} x {height}";
                    continue;
                }
                string text = Encoding.ASCII.GetString(edid, offset + 5, 13).Split('\n')[0].Trim();
                switch (edid[offset + 3])
                {
                    case 0xFC: yield return $"name \"{text}\""; break;
                    case 0xFE: yield return $"text \"{text}\" (often the panel part number)"; break;
                    case 0xFD: yield return "range limits present"; break;
                }
            }
            if (edid.Length > 128) yield return $"{edid[126]} extension block(s)";
        }

        private static string BitDepth(byte input) => ((input >> 4) & 7) switch
        {
            1 => ", 6 bits per color",
            2 => ", 8 bits per color",
            3 => ", 10 bits per color",
            _ => ""
        };
    }

    // ---- Display modes (resolution, refresh, rotation) ----

    private static class DisplayModes
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public ushort SpecVersion, DriverVersion, Size, DriverExtra;
            public uint Fields;
            public int PositionX, PositionY;
            public uint DisplayOrientation, DisplayFixedOutput;
            public short Color, Duplex, YResolution, TTOption, Collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
            public ushort LogPixels;
            public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
            public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsEx(string device, int mode, ref DevMode devMode, uint flags);

        public static IEnumerable<string> Describe(string gdiName)
        {
            var current = New();
            if (EnumDisplaySettingsEx(gdiName, -1 /* ENUM_CURRENT_SETTINGS */, ref current, 0))
                yield return $"current {current.PelsWidth} x {current.PelsHeight} @ {current.DisplayFrequency} Hz, {current.BitsPerPel} bit, " +
                             $"rotation {current.DisplayOrientation * 90}°";
            var modes = new List<(uint W, uint H, uint Hz)>();
            var mode = New();
            for (int i = 0; EnumDisplaySettingsEx(gdiName, i, ref mode, 0); i++, mode = New())
                if (mode.BitsPerPel >= 24) modes.Add((mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency));
            var rates = modes.Select(m => m.Hz).Distinct().Order().ToArray();
            var sizes = modes.Select(m => (m.W, m.H)).Distinct().OrderByDescending(s => s.W * s.H).ToArray();
            yield return $"refresh rates: {string.Join(", ", rates.Select(r => $"{r} Hz"))}";
            yield return $"resolutions ({sizes.Length}): {string.Join(", ", sizes.Take(8).Select(s => $"{s.W}x{s.H}"))}{(sizes.Length > 8 ? ", …" : "")}";
        }

        private static DevMode New() => new() { Size = (ushort)Marshal.SizeOf<DevMode>() };
    }

    // ---- Gamma ramp (software color for any display) ----

    private static class Gamma
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr data);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);

        public static IEnumerable<string> Describe(string gdiName)
        {
            IntPtr hdc = CreateDC("DISPLAY", gdiName, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero) { yield return "no device context: the gamma ramp cannot be used"; yield break; }
            var ramp = new ushort[768];
            bool ok;
            try { ok = GetDeviceGammaRamp(hdc, ramp); }
            finally { DeleteDC(hdc); }
            if (!ok) { yield return "GetDeviceGammaRamp failed: the driver does not allow software color here"; yield break; }
            int worst = 0;
            for (int channel = 0; channel < 3; channel++)
                for (int i = 0; i < 256; i++) worst = Math.Max(worst, Math.Abs(ramp[channel * 256 + i] - i * 257));
            yield return worst <= 512
                ? "readable and neutral: software color temperature / red-green-blue is possible"
                : $"readable but already changed (largest difference {worst}): Night light, a calibration profile or another app is using it";
            yield return $"white point now: red {ramp[255] * 100 / 65535}%, green {ramp[511] * 100 / 65535}%, blue {ramp[767] * 100 / 65535}%";
            using var icm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ICM");
            yield return icm?.GetValue("GdiIcmGammaRange") is int range
                ? $"GdiIcmGammaRange = {range} (Windows allows stronger ramps)"
                : "GdiIcmGammaRange not set: Windows limits how far the ramp can go (warm tints fine, extreme ones refused)";
        }
    }

    // ---- Power plan (brightness and timers for plugged in / battery) ----

    private static class PowerPlan
    {
        private static readonly Guid Video = new("7516b95f-f776-4464-8c53-06167f40cc99");
        private static readonly (string Name, Guid Id, string Unit)[] Settings =
        [
            ("Brightness", new("aded5e82-b909-4619-9949-f5d71dac0bcb"), "%"),
            ("Dimmed brightness", new("f1fbfde2-a960-4165-9f88-50667911ce96"), "%"),
            ("Dim display after", new("17aaa29b-8b43-4b94-aafe-35f64daaf1ee"), "s"),
            ("Turn off display after", new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"), "s"),
            ("Adaptive brightness", new("fbd9aa66-9553-4097-ba44-ed6e9d65eab8"), "")
        ];

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerStatus
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);

        public static IEnumerable<string> Describe()
        {
            if (GetSystemPowerStatus(out var power))
                yield return (power.BatteryFlag & 128) != 0
                    ? "no battery (desktop)"
                    : $"battery {power.BatteryLifePercent}%, {(power.AcLineStatus == 1 ? "plugged in" : "on battery")}";
            if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr pointer) != 0) { yield return "active plan not readable"; yield break; }
            Guid scheme = Marshal.PtrToStructure<Guid>(pointer);
            LocalFree(pointer);
            yield return $"plan {scheme}";
            var video = Video;
            foreach (var (name, id, unit) in Settings)
            {
                var setting = id;
                bool hasAc = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref video, ref setting, out uint ac) == 0;
                bool hasDc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref video, ref setting, out uint dc) == 0;
                yield return !hasAc && !hasDc
                    ? $"{name}: not in this plan"
                    : $"{name}: plugged in {Show(ac, unit, hasAc)}, battery {Show(dc, unit, hasDc)}";
            }
        }

        private static string Show(uint value, string unit, bool present) => !present ? "-"
            : unit == "s" ? (value == 0 ? "never" : $"{value / 60.0:0.#} min")
            : unit == "" ? (value == 0 ? "off" : "on")
            : $"{value}{unit}";
    }

    // ---- Sensors (ambient light) ----

    private static class Sensors
    {
        private static readonly Guid SensorClass = new("5175d334-c371-4806-b3ba-71fd53c9258d");

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll")] private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfoData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfoData data, uint property, out uint type,
            byte[] buffer, int size, out int required);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        public static IEnumerable<string> Describe()
        {
            var guid = SensorClass;
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x2 /* DIGCF_PRESENT */);
            if (set == new IntPtr(-1)) { yield return "sensor list not available"; yield break; }
            var names = new List<string>();
            try
            {
                var data = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    var buffer = new byte[512];
                    if (SetupDiGetDeviceRegistryProperty(set, ref data, 0x0C /* friendly name */, out _, buffer, buffer.Length, out _) ||
                        SetupDiGetDeviceRegistryProperty(set, ref data, 0x00 /* description */, out _, buffer, buffer.Length, out _))
                        names.Add(Encoding.Unicode.GetString(buffer).TrimEnd('\0'));
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            if (names.Count == 0) { yield return "no sensors: adaptive brightness from ambient light is not possible"; yield break; }
            foreach (string name in names)
                yield return name + (name.Contains("light", StringComparison.OrdinalIgnoreCase) || name.Contains("ALS") ? "  ← ambient light" : "");
        }
    }

    // ---- Graphics driver (version, Intel panel power saving) ----

    private static class Driver
    {
        public static IEnumerable<string> Describe()
        {
            using var display = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (display is null) { yield return "display class not readable"; yield break; }
            foreach (string sub in display.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                RegistryKey? key = null;
                try { key = display.OpenSubKey(sub); }
                catch (System.Security.SecurityException) { }
                if (key is null) continue;
                using (key)
                {
                    if (key.GetValue("DriverDesc") is not string name) continue;
                    yield return $"{name}: driver {key.GetValue("DriverVersion")} ({key.GetValue("DriverDate")})";
                    if (key.GetValue("FeatureTestControl") is int ftc)
                        yield return $"  Intel FeatureTestControl 0x{ftc:X}: panel power saving (DPST) {((ftc & 0x10) != 0 ? "disabled" : "enabled")}";
                    if (key.GetValue("PP_VariBrightFeatureEnable") is int vari)
                        yield return $"  AMD Vari-Bright {(vari != 0 ? "enabled" : "disabled")}";
                }
            }
        }
    }
}
