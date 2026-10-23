using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>
/// DDC through the Intel HD Graphics 3000 CUI COM interface (part of the installed Intel driver).
/// Port of the validated transport from intelLegacyDdc.ahk; restricted to igfxsrvc.exe 8.15.10.4459.
/// </summary>
internal sealed class IntelCuiTransport : IMonitorTransport
{
    internal const string ValidatedDriverVersion = "8.15.10.4459";
    private readonly string gdiName;

    public IntelCuiTransport(string gdiName)
    {
        if (!IsAvailable) throw new NotSupportedException("The validated Intel HD Graphics 3000 driver is unavailable.");
        this.gdiName = gdiName;
    }

    public string Id => "intel-cui";
    public bool SupportsWrites => true;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                string service = Path.Combine(Environment.SystemDirectory, "igfxsrvc.exe");
                return Environment.Is64BitProcess && File.Exists(service)
                    && FileVersionInfo.GetVersionInfo(service).FileVersion == ValidatedDriverVersion;
            }
            catch (IOException) { return false; }
        }
    }

    public string? ReadCapabilities() => null;

    public VcpValue Read(byte code) => Sta.Run(() =>
    {
        using var session = new Session(gdiName);
        byte[] request = session.BuildRequest(DdcPacket.HostReadSource, DdcPacket.GetVcpPayload(code), 11);
        session.Exchange(request, write: false);
        if (!DdcPacket.TryParseGetVcpReply(request.AsSpan(52, 11), code, out var value))
            throw new TransportException(Id, $"Intel CUI returned an invalid reply for VCP 0x{code:X2}.");
        return value;
    });

    public void Write(byte code, uint value) => Sta.Run(() =>
    {
        using var session = new Session(gdiName);
        byte[] request = session.BuildRequest(DdcPacket.HostWriteSource, DdcPacket.SetVcpPayload(code, value), 0);
        session.Exchange(request, write: true);
        return true;
    });

    public void Dispose() { }

    /// <summary>One COM session; the driver requires STA and fresh objects per command.</summary>
    private sealed class Session : IDisposable
    {
        private static readonly Guid DriverClsid = new("9CEE304E-DC6C-11D2-B561-00A0C92E6848");
        private static readonly Guid DriverIid = new("BB74AF4F-DC70-11D2-B561-00A0C92E6848");
        private static readonly Guid ApiClsid = new("7160A13D-73DA-4CEA-95B9-37356478588A");
        private static readonly Guid ApiIid = new("27E7234F-429F-4787-AC8F-8AADDED01355");
        private static readonly Guid I2cGuid = new("1B9FD916-DA16-4A51-AFBE-8FA712FBB5CE");
        private const uint ClsctxAll = 23;
        private readonly IntPtr driver;
        private readonly uint uid;

        public Session(string gdiName)
        {
            int init = CoInitializeEx(IntPtr.Zero, 2);
            if (init is not (0 or 1)) Marshal.ThrowExceptionForHR(init);
            IntPtr api = IntPtr.Zero;
            try
            {
                Guid apiClsid = ApiClsid, apiIid = ApiIid;
                int result = CoCreateInstance(ref apiClsid, IntPtr.Zero, ClsctxAll, ref apiIid, out api);
                if (result != 0) Marshal.ThrowExceptionForHR(result);
                IntPtr name = Marshal.StringToBSTR(gdiName);
                try
                {
                    var find = Method<FindDisplay>(api, 3);
                    var found = new List<uint>();
                    for (uint index = 0; index < 16; index++)
                    {
                        if (find(api, name, index, out uint candidate, out _) != 0) break;
                        if (candidate != 0) found.Add(candidate);
                    }
                    if (found.Count != 1)
                        throw new TransportException("intel-cui", $"Intel returned {found.Count} active displays for {gdiName}.");
                    uid = found[0];
                }
                finally { Marshal.FreeBSTR(name); }

                Guid driverClsid = DriverClsid, driverIid = DriverIid;
                result = CoCreateInstance(ref driverClsid, IntPtr.Zero, ClsctxAll, ref driverIid, out driver);
                if (result != 0) Marshal.ThrowExceptionForHR(result);
            }
            catch
            {
                if (driver != IntPtr.Zero) Marshal.Release(driver);
                CoUninitialize();
                throw;
            }
            finally { if (api != IntPtr.Zero) Marshal.Release(api); }
        }

        public byte[] BuildRequest(uint source, byte[] payload, uint receiveBytes)
        {
            byte[] request = new byte[184];
            var span = request.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(span[24..], uid);
            BinaryPrimitives.WriteUInt32LittleEndian(span[32..], DdcPacket.DisplayAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(span[36..], source);
            BinaryPrimitives.WriteUInt32LittleEndian(span[40..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(span[44..], (uint)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span[48..], receiveBytes);
            payload.AsSpan().CopyTo(span[52..]);
            return request;
        }

        public void Exchange(byte[] request, bool write)
        {
            var call = Method<ExchangeCall>(driver, write ? 4 : 3);
            var pinned = GCHandle.Alloc(request, GCHandleType.Pinned);
            try
            {
                Guid guid = I2cGuid;
                int hr = call(driver, ref guid, request.Length, pinned.AddrOfPinnedObject());
                if (hr != 0 || request[0] != 0)
                    throw new TransportException("intel-cui", $"Intel CUI rejected DDC: HRESULT=0x{hr:X8}, status={request[0]}.");
            }
            finally { pinned.Free(); }
        }

        public void Dispose()
        {
            if (driver != IntPtr.Zero) Marshal.Release(driver);
            CoUninitialize();
        }

        private static T Method<T>(IntPtr instance, int index) where T : Delegate
        {
            IntPtr table = Marshal.ReadIntPtr(instance);
            return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, index * IntPtr.Size));
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
}

/// <summary>Runs work on a short-lived STA thread, as COM driver interfaces require.</summary>
internal static class Sta
{
    public static T Run<T>(Func<T> action)
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        }) { IsBackground = true, Name = "SwitchMonitor STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result!;
    }
}
