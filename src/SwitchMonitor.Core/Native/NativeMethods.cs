using System.Runtime.InteropServices;
using System.Text;

namespace SwitchMonitor.Core.Native;

/// <summary>Windows in-box APIs (user32 and dxva2). No third-party binaries.</summary>
internal static class NativeMethods
{
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    internal const uint MONITORINFOF_PRIMARY = 0x1;

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

    internal static DISPLAY_DEVICE NewDisplayDevice() =>
        new() { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

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
