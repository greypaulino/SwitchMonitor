using System.Globalization;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwitchMonitor.Wpf;

internal sealed class PhysicalMonitor : IDisposable
{
    private readonly object gate = new();
    private IntPtr handle;
    private bool disposed;

    public string Name { get; }
    public string DeviceId { get; }
    public string DisplayName { get; }
    public string Key { get; }
    public bool IsLegacyLg => DeviceId.Contains("GSM7714", StringComparison.OrdinalIgnoreCase) ||
                              DeviceId.Contains("GSM7715", StringComparison.OrdinalIgnoreCase);
    public bool HasStandardDdc => handle != IntPtr.Zero && !disposed;
    public bool HasIntelLegacyBackend => IsLegacyLg && IntelLegacyDdc.IsAvailable;
    public bool HasAmdLegacyBackend => IsLegacyLg && !HasIntelLegacyBackend && AmdLegacyDdc.IsAvailable &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, "amd-experimental.flag"));
    public IReadOnlyList<int> InputCodes { get; private set; } = [];

    internal PhysicalMonitor(IntPtr handle, string name, string deviceId, string displayName, int index)
    {
        this.handle = handle;
        DeviceId = deviceId;
        DisplayName = displayName;
        Name = IsLegacyLg ? "LG 29WK600" : string.IsNullOrWhiteSpace(name) ? $"Display {index + 1}" : name.Trim();
        Key = $"{deviceId}|{index}";
    }

    public void ReadInputs()
    {
        lock (gate)
        {
            EnsureNotDisposed();
            // The LG 29WK600 uses the existing GPU-specific transport, not VCP 0x60.
            if (IsLegacyLg)
            {
                InputCodes = [17, 18, 15];
                return;
            }
            EnsureOpen();

            if (!Native.GetCapabilitiesStringLength(handle, out uint length) || length is < 4 or > 65536)
                return;
            var text = new StringBuilder((int)length);
            if (!Native.CapabilitiesRequestAndCapabilitiesReply(handle, text, length))
                return;
            var match = Regex.Match(text.ToString(), @"(?i)(?:^|\s)60\s*\(([^)]*)\)");
            if (!match.Success)
                return;
            var detected = new HashSet<int>();
            foreach (Match token in Regex.Matches(match.Groups[1].Value, @"(?i)\b[0-9a-f]{1,4}\b"))
                if (int.TryParse(token.Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                    detected.Add(code);
            int[] preferred = [17, 18, 15, 16, 3, 4, 1, 2];
            InputCodes = preferred.Where(detected.Contains).Concat(detected.Except(preferred).Order()).ToArray();
        }
    }

    public (uint Current, uint Maximum) ReadBrightness() => ReadVcp(0x10);
    public (uint Current, uint Maximum) ReadInput() => ReadVcp(0x60);

    public string ReadCapabilitiesForDiagnostics()
    {
        lock (gate)
        {
            EnsureOpen();
            if (!Native.GetCapabilitiesStringLength(handle, out uint length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read DDC/CI capabilities length");
            if (length is < 4 or > 65536)
                throw new InvalidDataException($"DDC/CI capabilities length is invalid: {length}.");
            var text = new StringBuilder((int)length);
            if (!Native.CapabilitiesRequestAndCapabilitiesReply(handle, text, length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read DDC/CI capabilities");
            return text.ToString();
        }
    }

    private (uint Current, uint Maximum) ReadVcp(byte code)
    {
        lock (gate)
        {
            EnsureNotDisposed();
            if (HasIntelLegacyBackend)
                return StaRunner.Run(() =>
                {
                using var intel = new IntelLegacyDdc(DisplayName);
                return intel.GetVcp(code);
                });
            if (HasAmdLegacyBackend)
            {
                using var amd = new AmdLegacyDdc();
                return amd.ReadVcp(amd.Select(DisplayName, DeviceId), code);
            }
            EnsureOpen();
            if (!Native.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out uint current, out uint maximum))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"The monitor did not answer VCP {code:X2}");
            return (current, maximum);
        }
    }

    public void SetBrightnessPercent(double percent, uint maximum)
    {
        if (maximum == 0) throw new InvalidOperationException("Brightness range is unavailable.");
        uint raw = (uint)Math.Clamp(Math.Round(percent * maximum / 100), 0, maximum);
        SetVcp(0x10, raw);
    }

    public void SetInput(int code)
    {
        if (IsLegacyLg)
        {
            if (!HasIntelLegacyBackend && !HasAmdLegacyBackend)
                throw new NotSupportedException("LG 29WK600 input switching needs a supported GPU DDC backend.");
            lock (gate)
            {
                EnsureNotDisposed();
                if (HasIntelLegacyBackend)
                    StaRunner.Run(() =>
                    {
                        using var intel = new IntelLegacyDdc(DisplayName);
                        intel.SetLgInput(code);
                        return true;
                    });
                else
                {
                    using var amd = new AmdLegacyDdc();
                    amd.SetLgInput(amd.Select(DisplayName, DeviceId), code);
                }
            }
            return;
        }
        if (!InputCodes.Contains(code))
            throw new InvalidOperationException("This input was not advertised by the monitor.");
        SetVcp(0x60, (uint)code);
    }

    private void SetVcp(byte code, uint value)
    {
        lock (gate)
        {
            if (HasIntelLegacyBackend)
            {
                EnsureNotDisposed();
                StaRunner.Run(() =>
                {
                    using var intel = new IntelLegacyDdc(DisplayName);
                    intel.SetVcp(code, value);
                    return true;
                });
                return;
            }
            if (HasAmdLegacyBackend)
            {
                EnsureNotDisposed();
                using var amd = new AmdLegacyDdc();
                amd.SetVcp(amd.Select(DisplayName, DeviceId), code, value);
                return;
            }
            EnsureOpen();
            if (!Native.SetVCPFeature(handle, code, value))
                throw new InvalidOperationException($"The monitor rejected VCP {code:X2} value {value}.");
        }
    }

    private void EnsureOpen()
    {
        EnsureNotDisposed();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Windows did not provide a standard DDC/CI handle for this monitor.");
    }

    private void EnsureNotDisposed()
    {
        if (disposed) throw new ObjectDisposedException(nameof(PhysicalMonitor));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (handle != IntPtr.Zero)
            {
                Native.DestroyPhysicalMonitor(handle);
                handle = IntPtr.Zero;
            }
        }
    }

    public override string ToString() => Name;
}

internal static class MonitorService
{
    public static IReadOnlyList<string> DescribeWindowsDevices(string displayName)
    {
        var result = new List<string>();
        for (uint adapterIndex = 0; adapterIndex < 32; adapterIndex++)
        {
            var adapter = new Native.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(null, adapterIndex, ref adapter, 0)) break;
            if (!string.Equals(adapter.DeviceName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add($"Adapter: {adapter.DeviceName} | {adapter.DeviceString} | {adapter.DeviceID}");
            break;
        }
        for (uint monitorIndex = 0; monitorIndex < 32; monitorIndex++)
        {
            var device = new Native.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            if (!Native.EnumDisplayDevices(displayName, monitorIndex, ref device, 0)) break;
            result.Add($"Windows monitor {monitorIndex}: {device.DeviceString} | {device.DeviceID} | flags=0x{device.StateFlags:X}");
        }
        return result;
    }

    public static IReadOnlyList<PhysicalMonitor> Enumerate()
    {
        var monitors = new List<PhysicalMonitor>();
        Native.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Native.MONITORINFOEX>() };
            string displayName = Native.GetMonitorInfo(hMonitor, ref info) ? info.szDevice : "";
            var display = new Native.DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
            string deviceId = Native.EnumDisplayDevices(displayName, 0, ref display, 0)
                ? display.DeviceID : displayName;
            if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count is 0 or > 16)
                return true;
            int size = Marshal.SizeOf<Native.PHYSICAL_MONITOR>();
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)count * size));
            try
            {
                if (!Native.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
                    return true;
                for (int i = 0; i < count; i++)
                {
                    var physical = Marshal.PtrToStructure<Native.PHYSICAL_MONITOR>(buffer + i * size);
                    monitors.Add(new PhysicalMonitor(physical.hPhysicalMonitor,
                        physical.szPhysicalMonitorDescription, deviceId, displayName, i));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors;
    }

    public static string PortName(int code) => code switch
    {
        17 => "HDMI 1", 18 => "HDMI 2", 15 => "DisplayPort 1", 16 => "DisplayPort 2",
        3 => "DVI 1", 4 => "DVI 2", 1 => "VGA 1", 2 => "VGA 2", _ => $"Input {code}"
    };
}

internal static class Native
{
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PHYSICAL_MONITOR
    {
        internal IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        internal uint cbSize;
        internal int left, top, right, bottom;
        internal int workLeft, workTop, workRight, workBottom;
        internal uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        internal uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceString;
        internal uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string DeviceKey;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW", SetLastError = true)]
    internal static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE display, uint flags);
    [DllImport("Dxva2.dll", SetLastError = true)]
    internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("Dxva2.dll", SetLastError = true)]
    internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, IntPtr physical);
    [DllImport("Dxva2.dll", SetLastError = true)]
    internal static extern bool DestroyPhysicalMonitor(IntPtr physical);
    [DllImport("Dxva2.dll", SetLastError = true)]
    internal static extern bool GetCapabilitiesStringLength(IntPtr physical, out uint length);
    [DllImport("Dxva2.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr physical, StringBuilder capabilities, uint length);
    [DllImport("Dxva2.dll", SetLastError = true)]
    internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr physical, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("Dxva2.dll", SetLastError = true)]
    internal static extern bool SetVCPFeature(IntPtr physical, byte code, uint value);
}
