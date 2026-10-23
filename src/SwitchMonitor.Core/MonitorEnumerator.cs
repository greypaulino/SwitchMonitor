using System.Runtime.InteropServices;
using SwitchMonitor.Core.Diagnostics;
using SwitchMonitor.Core.Native;
using SwitchMonitor.Core.Quirks;
using SwitchMonitor.Core.Transports;

namespace SwitchMonitor.Core;

/// <summary>Settings that change which transports the enumerator may use.</summary>
public sealed record CoreOptions
{
    /// <summary>Allows AMD ADL2 DDC writes, which are not yet validated on hardware.</summary>
    public bool AllowAmdWrites { get; init; }

    /// <summary>Reads options from the v2 data directory: amd-experimental.flag enables AMD writes.</summary>
    public static CoreOptions FromDataDirectory(string? directory = null) => new()
    {
        AllowAmdWrites = File.Exists(Path.Combine(directory ?? AppPaths.DataDirectory, "amd-experimental.flag"))
    };
}

public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\SwitchMonitor-v2. Kept apart from the 1.x (AutoHotkey) and WPF preview profiles.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchMonitor-v2");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
}

/// <summary>Discovers displays and picks the best transport for each one.</summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorDevice> Enumerate(CoreOptions? options = null, QuirkDatabase? quirks = null, ICoreLog? log = null)
    {
        options ??= new CoreOptions();
        quirks ??= QuirkDatabase.LoadEmbedded();
        log ??= NullLog.Instance;

        var wmiByPnp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wmiUnmatched = new List<string>();
        foreach (string instance in WmiBrightnessTransport.DiscoverInstances())
        {
            if (QuirkDatabase.ExtractPnpId(instance) is string pnp) wmiByPnp.TryAdd(pnp, instance);
            else wmiUnmatched.Add(instance);
        }

        var devices = new List<MonitorDevice>();
        var usedWmi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IntPtr hMonitor in WindowsMonitors())
        {
            try { devices.AddRange(FromHMonitor(hMonitor, options, quirks, log, wmiByPnp, usedWmi)); }
            catch (Exception error) when (error is COMException or ExternalException or InvalidOperationException)
            {
                log.Write(LogLevel.Warning, "Skipped a display that Windows could not describe.", error);
            }
        }

        foreach (var (pnp, instance) in wmiByPnp)
            if (!usedWmi.Contains(instance)) wmiUnmatched.Add(instance);
        foreach (string instance in wmiUnmatched)
        {
            string? pnp = QuirkDatabase.ExtractPnpId(instance);
            var identity = new MonitorIdentity($"wmi|{instance}", "Built-in display", instance, "", pnp, false, true);
            devices.Add(new MonitorDevice(identity, new WmiBrightnessTransport(instance), null, log));
        }

        foreach (var device in devices)
            log.Write(LogLevel.Info, $"Found {device} ({device.Identity.DeviceId}).");
        return devices;
    }

    private static List<IntPtr> WindowsMonitors()
    {
        var handles = new List<IntPtr>();
        NativeMethods.MonitorEnumProc callback = (hMonitor, _, _, _) => { handles.Add(hMonitor); return true; };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return handles;
    }

    private static IEnumerable<MonitorDevice> FromHMonitor(IntPtr hMonitor, CoreOptions options, QuirkDatabase quirks,
        ICoreLog log, Dictionary<string, string> wmiByPnp, HashSet<string> usedWmi)
    {
        var info = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        string gdiName = NativeMethods.GetMonitorInfo(hMonitor, ref info) ? info.szDevice : "";
        bool primary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;

        var result = new List<MonitorDevice>();
        if (!NativeMethods.GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, out uint count) || count is 0 or > 16)
            return result;
        int size = Marshal.SizeOf<NativeMethods.PHYSICAL_MONITOR>();
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)count * size));
        try
        {
            if (!NativeMethods.GetPhysicalMonitorsFromHMONITOR(hMonitor, count, buffer))
                return result;
            for (int i = 0; i < count; i++)
            {
                var physical = Marshal.PtrToStructure<NativeMethods.PHYSICAL_MONITOR>(buffer + i * size);
                string deviceId = MonitorDeviceId(gdiName, (uint)i) ?? MonitorDeviceId(gdiName, 0) ?? gdiName;
                string? pnp = QuirkDatabase.ExtractPnpId(deviceId);
                string description = string.IsNullOrWhiteSpace(physical.szPhysicalMonitorDescription)
                    ? $"Display {result.Count + 1}" : physical.szPhysicalMonitorDescription.Trim();
                var quirk = quirks.Match(deviceId);

                bool builtIn = pnp is not null && wmiByPnp.ContainsKey(pnp);
                var identity = new MonitorIdentity($"{deviceId}|{i}", builtIn ? "Built-in display" : description,
                    deviceId, gdiName, pnp, primary, builtIn);
                var transport = ChooseTransport(physical.hPhysicalMonitor, identity, quirk, options, log, wmiByPnp, usedWmi);
                result.Add(new MonitorDevice(identity, transport, quirk, log));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }

    private static IMonitorTransport ChooseTransport(IntPtr handle, MonitorIdentity identity, MonitorQuirk? quirk,
        CoreOptions options, ICoreLog log, Dictionary<string, string> wmiByPnp, HashSet<string> usedWmi)
    {
        IMonitorTransport? chosen = null;
        if (identity.PnpId is string pnp && wmiByPnp.TryGetValue(pnp, out string? instance))
        {
            chosen = new WmiBrightnessTransport(instance);
            usedWmi.Add(instance);
        }

        if (chosen is null && quirk is not null)
            foreach (string id in quirk.Transports)
            {
                try
                {
                    chosen = id switch
                    {
                        "intel-cui" when IntelCuiTransport.IsAvailable => new IntelCuiTransport(identity.GdiName),
                        "amd-adl" when AmdAdlTransport.IsAvailable =>
                            new AmdAdlTransport(identity.GdiName, QuirkDatabase.ProductCode(identity.PnpId), options.AllowAmdWrites),
                        _ => null
                    };
                }
                catch (Exception error) when (error is TransportException or NotSupportedException)
                {
                    log.Write(LogLevel.Warning, $"{quirk.Name}: transport {id} unavailable.", error);
                }
                if (chosen is not null) break;
            }

        if (chosen is null && handle != IntPtr.Zero)
            return new DdcCiTransport(handle);

        // A GPU or WMI transport was chosen (or nothing works): release the unused dxva2 handle.
        if (handle != IntPtr.Zero) NativeMethods.DestroyPhysicalMonitor(handle);
        if (chosen is null)
            log.Write(LogLevel.Warning, $"{identity.FriendlyName}: no DDC/CI handle and no matching transport.");
        return chosen ?? UnavailableTransport.Instance;
    }

    private static string? MonitorDeviceId(string gdiName, uint index)
    {
        if (string.IsNullOrEmpty(gdiName)) return null;
        var device = NativeMethods.NewDisplayDevice();
        return NativeMethods.EnumDisplayDevices(gdiName, index, ref device, 0) && !string.IsNullOrEmpty(device.DeviceID)
            ? device.DeviceID : null;
    }
}
