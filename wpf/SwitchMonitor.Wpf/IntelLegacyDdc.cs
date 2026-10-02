using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;

namespace SwitchMonitor.Wpf;

// Port of the verified Intel HD Graphics 3000 CUI transport in intelLegacyDdc.ahk.
// The driver ABI is restricted to igfxsrvc.exe 8.15.10.4459.
internal sealed class IntelLegacyDdc : IDisposable
{
    private static readonly Guid DriverClsid = new("9CEE304E-DC6C-11D2-B561-00A0C92E6848");
    private static readonly Guid DriverIid = new("BB74AF4F-DC70-11D2-B561-00A0C92E6848");
    private static readonly Guid ApiClsid = new("7160A13D-73DA-4CEA-95B9-37356478588A");
    private static readonly Guid ApiIid = new("27E7234F-429F-4787-AC8F-8AADDED01355");
    private static readonly Guid I2cGuid = new("1B9FD916-DA16-4A51-AFBE-8FA712FBB5CE");
    private readonly IntPtr driver;
    private readonly uint uid;
    private readonly bool uninitializeCom;

    public static bool IsAvailable
    {
        get
        {
            string service = Path.Combine(Environment.SystemDirectory, "igfxsrvc.exe");
            return Environment.Is64BitProcess && File.Exists(service)
                && FileVersionInfo.GetVersionInfo(service).FileVersion == "8.15.10.4459";
        }
    }

    public IntelLegacyDdc(string displayName)
    {
        if (!IsAvailable)
            throw new NotSupportedException("The validated Intel HD Graphics 3000 driver is unavailable.");
        int init = CoInitializeEx(IntPtr.Zero, 2);
        if (init is not (0 or 1)) Marshal.ThrowExceptionForHR(init);
        uninitializeCom = true;
        IntPtr api = IntPtr.Zero;
        try
        {
            Guid apiClsid = ApiClsid, apiIid = ApiIid;
            int result = CoCreateInstance(ref apiClsid, IntPtr.Zero, 23,
                ref apiIid, out api);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            IntPtr name = Marshal.StringToBSTR(displayName);
            try
            {
                var find = Method<FindDisplay>(api, 3);
                var found = new List<uint>();
                for (uint index = 0; index < 16; index++)
                {
                    int hr = find(api, name, index, out uint candidate, out _);
                    if (hr != 0) break;
                    if (candidate != 0) found.Add(candidate);
                }
                if (found.Count != 1)
                    throw new InvalidOperationException($"Intel returned {found.Count} active displays for {displayName}.");
                uid = found[0];
            }
            finally { Marshal.FreeBSTR(name); }

            Guid driverClsid = DriverClsid, driverIid = DriverIid;
            result = CoCreateInstance(ref driverClsid, IntPtr.Zero, 23,
                ref driverIid, out driver);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
        }
        catch
        {
            if (driver != IntPtr.Zero) Marshal.Release(driver);
            if (uninitializeCom) CoUninitialize();
            throw;
        }
        finally { if (api != IntPtr.Zero) Marshal.Release(api); }
    }

    public (uint Current, uint Maximum) GetVcp(byte feature)
    {
        byte[] request = BuildRequest(0x51, [0x82, 0x01, feature], 11);
        Exchange(request, false);
        int checksum = 0x50;
        for (int i = 52; i < 63; i++) checksum ^= request[i];
        if (checksum != 0 || request[52] != 0x6E || request[53] != 0x88
            || request[54] != 2 || request[55] != 0 || request[56] != feature)
            throw new InvalidOperationException($"Invalid Intel DDC reply for VCP {feature:X2}.");
        uint maximum = (uint)(request[58] << 8 | request[59]);
        uint current = (uint)(request[60] << 8 | request[61]);
        return (current, maximum);
    }

    public void SetVcp(byte feature, uint value)
    {
        if (value > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        byte[] request = BuildRequest(0x50,
            [0x84, 0x03, feature, (byte)(value >> 8), (byte)value], 0);
        Exchange(request, true);
    }

    public void SetLgInput(int code)
    {
        byte value = code switch { 17 => 0x90, 18 => 0x91, 15 => 0xD0,
            _ => throw new ArgumentOutOfRangeException(nameof(code)) };
        byte[] request = BuildRequest(0x50, [0x84, 0x03, 0xF4, 0, value], 0);
        Exchange(request, true);
    }

    private byte[] BuildRequest(uint source, byte[] payload, uint receiveBytes)
    {
        if (payload.Length > 128 || receiveBytes > 128) throw new ArgumentOutOfRangeException(nameof(payload));
        byte[] request = new byte[184];
        var span = request.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], uid);
        BinaryPrimitives.WriteUInt32LittleEndian(span[32..], 0x6E);
        BinaryPrimitives.WriteUInt32LittleEndian(span[36..], source);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[48..], receiveBytes);
        payload.AsSpan().CopyTo(span[52..]);
        return request;
    }

    private void Exchange(byte[] request, bool write)
    {
        var call = Method<ExchangeCall>(driver, write ? 4 : 3);
        var pinned = GCHandle.Alloc(request, GCHandleType.Pinned);
        try
        {
            Guid guid = I2cGuid;
            int hr = call(driver, ref guid, request.Length, pinned.AddrOfPinnedObject());
            if (hr != 0 || request[0] != 0)
                throw new InvalidOperationException($"Intel CUI rejected DDC: HRESULT={hr:X8}, status={request[0]}.");
        }
        finally { pinned.Free(); }
    }

    private static T Method<T>(IntPtr instance, int index) where T : Delegate
    {
        IntPtr table = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
    }

    public void Dispose()
    {
        if (driver != IntPtr.Zero) Marshal.Release(driver);
        if (uninitializeCom) CoUninitialize();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FindDisplay(IntPtr self, IntPtr name, uint index, out uint uid, out uint kind);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ExchangeCall(IntPtr self, ref Guid guid, int size, IntPtr request);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr result);
}

internal static class StaRunner
{
    public static T Run<T>(Func<T> action)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result!;
    }
}
