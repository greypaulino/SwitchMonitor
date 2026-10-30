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
    /// <summary>
    /// %LOCALAPPDATA%\SwitchMonitor-v2. Kept apart from the 1.x (AutoHotkey) and WPF preview profiles.
    /// SWITCHMONITOR_DATA_DIR moves it (demo mode uses a throwaway folder so it never touches the real settings).
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("SWITCHMONITOR_DATA_DIR") is { Length: > 0 } custom ? custom : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwitchMonitor-v2");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Pictures imported as profile icons ("image:name.png" in scenes.json).</summary>
    public static string IconsDirectory => Path.Combine(DataDirectory, "icons");
}

/// <summary>Discovers displays and picks the best transport for each one.</summary>
public static class MonitorEnumerator
{
    public static IReadOnlyList<MonitorDevice> Enumerate(CoreOptions? options = null, QuirkDatabase? quirks = null, ICoreLog? log = null)
    {
        options ??= new CoreOptions();
        quirks ??= QuirkDatabase.LoadDefault();
        log ??= NullLog.Instance;

        var wmiByPnp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var wmiUnmatched = new List<string>();
        foreach (string instance in WmiBrightnessTransport.DiscoverInstances())
        {
            if (QuirkDatabase.ExtractPnpId(instance) is string pnp) wmiByPnp.TryAdd(pnp, instance);
            else wmiUnmatched.Add(instance);
        }

        var hMonitors = WindowsMonitors();
        var activePnp = ActivePnpIds(hMonitors);
        string builtInName = BuiltInDisplayName(WmiBrightnessTransport.ComputerModel());

        var devices = new List<MonitorDevice>();
        var usedWmi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IntPtr hMonitor in hMonitors)
        {
            try { devices.AddRange(FromHMonitor(hMonitor, options, quirks, log, wmiByPnp, usedWmi, builtInName)); }
            catch (Exception error) when (error is COMException or ExternalException or InvalidOperationException)
            {
                log.Write(LogLevel.Warning, "Skipped a display that Windows could not describe.", error);
            }
        }

        foreach (var (pnp, instance) in wmiByPnp)
            if (!usedWmi.Contains(instance)) wmiUnmatched.Add(instance);
        foreach (string instance in wmiUnmatched)
        {
            // WMI keeps reporting a built-in panel that Windows is not using (lid closed, external display only).
            // List it as inactive, like the 1.x OFF row, instead of sending commands that fail.
            string? pnp = QuirkDatabase.ExtractPnpId(instance);
            bool active = pnp is not null && activePnp.Contains(pnp);
            var identity = new MonitorIdentity($"wmi|{instance}", builtInName, instance, "", pnp, false, true, active);
            devices.Add(new MonitorDevice(identity, new WmiBrightnessTransport(instance), null, log));
            if (!active) log.Write(LogLevel.Info, $"{builtInName}: Windows is not using this display; it is listed as inactive.");
        }

        foreach (var device in devices)
            log.Write(LogLevel.Info, $"Found {device} ({device.Identity.DeviceId}).");
        return devices;
    }

    /// <summary>"Inspiron N5050" → "N5050 Laptop Screen"; generic or missing model → "Built-in display".</summary>
    internal static string BuiltInDisplayName(string? computerModel)
    {
        string model = (computerModel ?? "").Trim();
        if (model.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(model,
                @"(?i)^(?:N/?A|Unknown|Default string|System Product Name|To Be Filled By O\.E\.M\.)$"))
            return "Built-in display";
        var shortName = System.Text.RegularExpressions.Regex.Match(model, @"(?i)\b([A-Z]+\d{3,})$");
        return (shortName.Success ? shortName.Groups[1].Value : model) + " Laptop Screen";
    }

    /// <summary>PnP IDs of every monitor attached to an active Windows display source.</summary>
    private static HashSet<string> ActivePnpIds(IEnumerable<IntPtr> hMonitors)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IntPtr hMonitor in hMonitors)
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref info)) continue;
            foreach (string id in ActiveMonitorDeviceIds(info.szDevice))
                if (QuirkDatabase.ExtractPnpId(id) is string pnp) result.Add(pnp);
        }
        return result;
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
        ICoreLog log, Dictionary<string, string> wmiByPnp, HashSet<string> usedWmi, string builtInName)
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
                // The i-th physical monitor is the i-th ACTIVE monitor of this source: with the laptop panel switched off
                // (25/10, "second screen only"), \\.\DISPLAY1 still lists the panel first, inactive, and the LG second;
                // taking child 0 gave the LG the laptop's id, so its quirk (picture modes, FreeSync, black levels) was lost.
                var active = ActiveMonitorDeviceIds(gdiName);
                string deviceId = (i < active.Count ? active[i] : null) ?? MonitorDeviceId(gdiName, (uint)i)
                    ?? MonitorDeviceId(gdiName, 0) ?? gdiName;
                string? pnp = QuirkDatabase.ExtractPnpId(deviceId);
                string description = string.IsNullOrWhiteSpace(physical.szPhysicalMonitorDescription)
                    ? $"Display {result.Count + 1}" : physical.szPhysicalMonitorDescription.Trim();
                var quirk = quirks.Match(deviceId);

                bool builtIn = pnp is not null && wmiByPnp.ContainsKey(pnp);
                var identity = new MonitorIdentity($"{deviceId}|{i}", builtIn ? builtInName : description,
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
                        "intel-cui" when IntelCuiTransport.IsAvailable => new IntelCuiTransport(identity.GdiName, identity.DeviceId),
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

    private const uint DisplayDeviceActive = 0x1;

    /// <summary>
    /// Device ids of the monitors Windows is showing on this source, in order. Monitors listed but switched off are
    /// left out; when none is flagged active (some drivers), every listed monitor is returned.
    /// </summary>
    internal static IReadOnlyList<string> ActiveMonitorDeviceIds(string gdiName)
    {
        var all = new List<(string Id, bool Active)>();
        if (string.IsNullOrEmpty(gdiName)) return [];
        for (uint index = 0; index < 16; index++)
        {
            var device = NativeMethods.NewDisplayDevice();
            if (!NativeMethods.EnumDisplayDevices(gdiName, index, ref device, 0)) break;
            if (!string.IsNullOrEmpty(device.DeviceID)) all.Add((device.DeviceID, (device.StateFlags & DisplayDeviceActive) != 0));
        }
        var active = all.Where(m => m.Active).Select(m => m.Id).ToArray();
        return active.Length > 0 ? active : all.Select(m => m.Id).ToArray();
    }

    private static string? MonitorDeviceId(string gdiName, uint index)
    {
        if (string.IsNullOrEmpty(gdiName)) return null;
        var device = NativeMethods.NewDisplayDevice();
        return NativeMethods.EnumDisplayDevices(gdiName, index, ref device, 0) && !string.IsNullOrEmpty(device.DeviceID)
            ? device.DeviceID : null;
    }
}
