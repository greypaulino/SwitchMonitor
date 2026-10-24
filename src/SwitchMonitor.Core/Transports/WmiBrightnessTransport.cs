using System.ComponentModel;
using System.Runtime.InteropServices;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>
/// Built-in laptop panel brightness through Windows WMI (root\WMI WmiMonitorBrightness*).
/// Uses the in-box WbemScripting COM automation object, so no System.Management package is needed.
/// Only VCP 0x10 (brightness) is supported, on a 0-100 scale.
/// </summary>
internal sealed class WmiBrightnessTransport : IMonitorTransport
{
    private readonly string instanceName;

    public WmiBrightnessTransport(string instanceName) => this.instanceName = instanceName;

    public string Id => "wmi";
    public bool SupportsWrites => true;
    public string InstanceName => instanceName;

    public string? ReadCapabilities() => "(vcp(10))";

    // Ways to read WmiMonitorBrightness, tried in order until one works on this computer. On the Dell N5050
    // (Intel HD 3000) the first one fails with E_FAIL "Unspecified error" while 1.x, which used the winmgmts:
    // moniker from an STA thread, could read it.
    private static readonly string[] ReadStrategies =
        ["locator, listed properties", "locator, all properties", "winmgmts moniker", "winmgmts moniker on an STA thread",
         "LCD brightness IOCTL"];
    private const int LcdStrategy = 4;
    private int readStrategy = -1;

    /// <summary>Name of the read strategy that worked, for diagnostics (null until a read succeeds).</summary>
    public string? ReadStrategy => readStrategy < 0 ? null : ReadStrategies[readStrategy];

    public VcpValue Read(byte code)
    {
        RequireBrightness(code);
        int[] order = readStrategy >= 0 ? [readStrategy] : [0, 1, 2, 3, LcdStrategy];
        var failures = new List<string>();
        Exception? last = null;
        foreach (int strategy in order)
        {
            try
            {
                uint current = strategy switch
                {
                    LcdStrategy => LcdBrightness.Read(),
                    3 => RunSta(() => ReadWith(strategy)),
                    _ => ReadWith(strategy)
                };
                readStrategy = strategy;
                return new VcpValue(current, 100);
            }
            catch (Exception error) when (error is COMException or InvalidCastException or TransportException
                or NotSupportedException or Win32Exception or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                last = error;
                failures.Add($"{ReadStrategies[strategy]}: {Describe(error)}");
            }
        }
        if (readStrategy >= 0) readStrategy = -1;   // the remembered way stopped working: try them all next time
        throw new TransportException(Id, $"WMI brightness read failed for {instanceName} ({string.Join("; ", failures)}).", last);
    }

    private uint ReadWith(int strategy)
    {
        IEnumerable<object> rows = strategy switch
        {
            0 => Query("SELECT Active, InstanceName, CurrentBrightness FROM WmiMonitorBrightness"),
            1 => Query("SELECT * FROM WmiMonitorBrightness"),
            _ => MonikerQuery("SELECT * FROM WmiMonitorBrightness")
        };
        foreach (object wmiObject in rows)
        {
            if (!Matches(wmiObject)) continue;
            dynamic item = wmiObject;
            return Convert.ToUInt32((object)item.CurrentBrightness);
        }
        throw new TransportException(Id, $"WMI no longer reports brightness for {instanceName}.");
    }

    private static string Describe(Exception error) => error is COMException com
        ? $"{com.Message.Trim()} (0x{com.HResult:X8})"
        : error.Message.Trim();

    /// <summary>Same query through the winmgmts: moniker with impersonation, as 1.x (AutoHotkey ComObjGet) did.</summary>
    private static IEnumerable<object> MonikerQuery(string wql)
    {
        var options = new BindOptions { Size = Marshal.SizeOf<BindOptions>(), Mode = 2 /* STGM_READWRITE */ };
        var dispatch = new Guid("00020400-0000-0000-C000-000000000046");   // IDispatch
        CoGetObject(@"winmgmts:{impersonationLevel=impersonate}!\\.\root\wmi", ref options, ref dispatch, out object moniker);
        dynamic service = moniker;
        dynamic results = service.ExecQuery(wql);
        var items = new List<object>();
        foreach (object item in results) items.Add(item);
        return items;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BindOptions
    {
        public int Size;
        public int Flags;
        public int Mode;
        public int TickCountDeadline;
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void CoGetObject(string name, ref BindOptions options, ref Guid iid,
        [MarshalAs(UnmanagedType.IDispatch)] out object result);

    private static T RunSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "WMI brightness (STA)" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TransportException("wmi", "WMI brightness read timed out on the STA thread.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public void Write(byte code, uint value)
    {
        RequireBrightness(code);
        byte level = (byte)Math.Clamp(value, 0u, 100u);
        // When only the LCD device could read the level, WMI is broken here: write the same way.
        if (readStrategy == LcdStrategy)
        {
            LcdBrightness.Write(level);
            return;
        }
        try
        {
            foreach (object wmiObject in Query("SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods"))
            {
                if (!Matches(wmiObject)) continue;
                dynamic item = wmiObject;
                item.WmiSetBrightness(0, level);
                return;
            }
        }
        catch (COMException error)
        {
            try { LcdBrightness.Write(level); return; }
            catch (Win32Exception lcd) { throw new TransportException(Id, $"WMI brightness write failed ({Describe(error)}); LCD IOCTL: {lcd.Message}", error); }
        }
        throw new TransportException(Id, $"WMI no longer exposes brightness methods for {instanceName}.");
    }

    public void Dispose() { }

    /// <summary>Instance names of active built-in panels with WMI brightness control.</summary>
    internal static IReadOnlyList<string> DiscoverInstances()
    {
        var names = new List<string>();
        try
        {
            foreach (object wmiObject in Query("SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods"))
            {
                dynamic item = wmiObject;
                object name = item.InstanceName;
                if ((bool)item.Active && name is string text && text.Length > 0)
                    names.Add(text);
            }
        }
        catch (COMException) { }   // No WMI brightness provider: desktops usually have none.
        return names;
    }

    /// <summary>Computer model from root\cimv2 Win32_ComputerSystem (e.g. "Inspiron N5050"), or null.</summary>
    internal static string? ComputerModel()
    {
        try
        {
            foreach (object wmiObject in Query("SELECT Model FROM Win32_ComputerSystem", @"root\cimv2"))
            {
                dynamic item = wmiObject;
                object model = item.Model;
                return model as string;
            }
        }
        catch (COMException) { }
        return null;
    }

    private bool Matches(object wmiObject)
    {
        dynamic item = wmiObject;
        return (bool)item.Active &&
            string.Equals((string)item.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase);
    }

    private void RequireBrightness(byte code)
    {
        if (code != VcpCode.Brightness)
            throw new TransportException(Id, $"WMI supports only brightness, not {VcpCode.Describe(code)}.");
    }

    private static IEnumerable<object> Query(string wql, string wmiNamespace = @"root\WMI")
    {
        Type locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
        dynamic locator = Activator.CreateInstance(locatorType)!;
        dynamic service = locator.ConnectServer(".", wmiNamespace);
        dynamic results = service.ExecQuery(wql);
        var items = new List<object>();
        foreach (object item in results) items.Add(item);
        return items;
    }
}

/// <summary>
/// Laptop backlight through the Windows LCD device (\\.\LCD, IOCTL_VIDEO_*_DISPLAY_BRIGHTNESS). Independent of WMI,
/// which fails on some drivers. Levels are percentages; the value for the current power source (AC or battery) is used.
/// </summary>
internal static class LcdBrightness
{
    private const uint QueryDisplayBrightness = 0x00230498;   // CTL_CODE(FILE_DEVICE_VIDEO, 0x126, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint SetDisplayBrightness = 0x0023049C;     // 0x127
    private const byte PolicyBoth = 3;                        // DISPLAYPOLICY_BOTH: AC and DC

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct DisplayBrightness
    {
        public byte Policy;
        public byte AcLevel;
        public byte DcLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    public static uint Read()
    {
        using var device = Open();
        var brightness = new DisplayBrightness();
        if (!DeviceIoControl(device, QueryDisplayBrightness, IntPtr.Zero, 0, ref brightness, 3, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        bool onBattery = GetSystemPowerStatus(out var power) && power.AcLineStatus == 0;
        return Math.Min(100u, onBattery ? brightness.DcLevel : brightness.AcLevel);
    }

    public static void Write(byte level)
    {
        using var device = Open();
        var brightness = new DisplayBrightness { Policy = PolicyBoth, AcLevel = level, DcLevel = level };
        if (!DeviceIoControl(device, SetDisplayBrightness, ref brightness, 3, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle Open()
    {
        var handle = CreateFile(@"\\.\LCD", 0xC0000000 /* GENERIC_READ | GENERIC_WRITE */, 3 /* share read+write */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, IntPtr input,
        int inputSize, ref DisplayBrightness output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, ref DisplayBrightness input,
        int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
