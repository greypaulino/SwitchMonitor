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

    public VcpValue Read(byte code)
    {
        RequireBrightness(code);
        foreach (object wmiObject in Query("SELECT Active, InstanceName, CurrentBrightness FROM WmiMonitorBrightness"))
        {
            if (!Matches(wmiObject)) continue;
            dynamic item = wmiObject;
            uint current = Convert.ToUInt32((object)item.CurrentBrightness);
            return new VcpValue(current, 100);
        }
        throw new TransportException(Id, $"WMI no longer reports brightness for {instanceName}.");
    }

    public void Write(byte code, uint value)
    {
        RequireBrightness(code);
        byte level = (byte)Math.Clamp(value, 0u, 100u);
        foreach (object wmiObject in Query("SELECT Active, InstanceName FROM WmiMonitorBrightnessMethods"))
        {
            if (!Matches(wmiObject)) continue;
            dynamic item = wmiObject;
            item.WmiSetBrightness(0, level);
            return;
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

    private static IEnumerable<object> Query(string wql)
    {
        Type locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)!;
        dynamic locator = Activator.CreateInstance(locatorType)!;
        dynamic service = locator.ConnectServer(".", @"root\WMI");
        dynamic results = service.ExecQuery(wql);
        var items = new List<object>();
        foreach (object item in results) items.Add(item);
        return items;
    }
}
