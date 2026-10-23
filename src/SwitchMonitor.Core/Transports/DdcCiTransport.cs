using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SwitchMonitor.Core.Native;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>Standard DDC/CI through the Windows Monitor Configuration API (dxva2.dll).</summary>
internal sealed class DdcCiTransport : IMonitorTransport
{
    private IntPtr handle;

    public DdcCiTransport(IntPtr physicalMonitorHandle)
    {
        if (physicalMonitorHandle == IntPtr.Zero)
            throw new ArgumentException("A physical monitor handle is required.", nameof(physicalMonitorHandle));
        handle = physicalMonitorHandle;
    }

    public string Id => "ddc-ci";
    public bool SupportsWrites => true;

    public string? ReadCapabilities()
    {
        EnsureOpen();
        if (!NativeMethods.GetCapabilitiesStringLength(handle, out uint length))
            throw Failure("could not read the capabilities length");
        if (length is < 4 or > 65536)
            throw new TransportException(Id, $"The capabilities length is invalid: {length}.");
        var text = new StringBuilder((int)length);
        if (!NativeMethods.CapabilitiesRequestAndCapabilitiesReply(handle, text, length))
            throw Failure("could not read the capabilities string");
        return text.ToString();
    }

    public VcpValue Read(byte code)
    {
        EnsureOpen();
        if (!NativeMethods.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out uint current, out uint maximum))
            throw Failure($"the monitor did not answer VCP 0x{code:X2}");
        return new VcpValue(current, maximum);
    }

    public void Write(byte code, uint value)
    {
        EnsureOpen();
        if (!NativeMethods.SetVCPFeature(handle, code, value))
            throw Failure($"the monitor rejected VCP 0x{code:X2} = {value}");
    }

    private TransportException Failure(string what) =>
        new(Id, $"DDC/CI: {what}.", new Win32Exception(Marshal.GetLastWin32Error()));

    private void EnsureOpen()
    {
        if (handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(DdcCiTransport));
    }

    public void Dispose()
    {
        if (handle == IntPtr.Zero) return;
        NativeMethods.DestroyPhysicalMonitor(handle);
        handle = IntPtr.Zero;
    }
}
