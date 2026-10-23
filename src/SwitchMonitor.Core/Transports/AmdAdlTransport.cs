using System.Runtime.InteropServices;
using System.Text;
using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>
/// DDC through AMD ADL2 (atiadlxx.dll, part of the installed AMD driver).
/// Each command targets one display mapped to the requested Windows source and EDID product code.
/// Reads are validated; writes stay disabled unless explicitly enabled (still unvalidated on hardware).
/// </summary>
internal sealed class AmdAdlTransport : IMonitorTransport
{
    private readonly string gdiName;
    private readonly int? productCode;

    public AmdAdlTransport(string gdiName, int? productCode, bool allowWrites)
    {
        if (!IsAvailable) throw new NotSupportedException("The AMD ADL2 driver library is unavailable.");
        this.gdiName = gdiName;
        this.productCode = productCode;
        SupportsWrites = allowWrites;
    }

    public string Id => "amd-adl";
    public bool SupportsWrites { get; }

    internal static bool IsAvailable => Environment.Is64BitProcess &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"));

    /// <summary>Capabilities over ADL2 DDC (DDC/CI 0xF3) with split send/receive, like VCP reads. Read-only.</summary>
    public string? ReadCapabilities()
    {
        using var adl = new AdlSession();
        var target = adl.Select(gdiName, productCode);
        return DdcPacket.ReadCapabilities(offset =>
        {
            adl.Send(target, DdcPacket.Build(DdcPacket.HostReadSource, DdcPacket.CapabilitiesRequestPayload(offset)));
            Thread.Sleep(50);
            byte[] reply = adl.Receive(target, DdcPacket.MaxCapabilitiesReplyLength, out int received);
            return reply.AsSpan(0, Math.Clamp(received, 0, reply.Length)).ToArray();
        }, Id);
    }

    public VcpValue Read(byte code)
    {
        using var adl = new AdlSession();
        var target = adl.Select(gdiName, productCode);
        // AMD requires separate send and receive transactions; the combined form returned inconsistent bytes.
        adl.Send(target, DdcPacket.Build(DdcPacket.HostReadSource, DdcPacket.GetVcpPayload(code)));
        Thread.Sleep(50);
        byte[] reply = adl.Receive(target, 16, out int received);
        if (!DdcPacket.TryParseGetVcpReply(reply.AsSpan(0, Math.Clamp(received, 0, reply.Length)), code, out var value))
            throw new TransportException(Id, $"AMD returned an invalid VCP 0x{code:X2} reply: " +
                Convert.ToHexString(reply.AsSpan(0, Math.Clamp(received, 0, reply.Length))));
        return value;
    }

    public void Write(byte code, uint value)
    {
        if (!SupportsWrites)
            throw new TransportException(Id, "AMD DDC writes are disabled until they are validated on hardware.");
        using var adl = new AdlSession();
        var target = adl.Select(gdiName, productCode);
        adl.Send(target, DdcPacket.Build(DdcPacket.WriteSourceFor(code), DdcPacket.SetVcpPayload(code, value)));
    }

    public void Dispose() { }

    internal sealed record AmdDisplay(int AdapterIndex, int DisplayIndex, string GdiName, string Name, int? ProductCode);

    private sealed class AdlSession : IDisposable
    {
        private const int AdapterInfoSize = 1572;
        private const int DisplayInfoSize = 552;
        private const int EdidDataSize = 288;
        private readonly IntPtr module;
        private readonly AllocCallback allocator;
        private readonly DestroyControl destroy;
        private readonly DdcBlockAccess ddc;
        private readonly List<AmdDisplay> displays = [];
        private IntPtr context;

        public AdlSession()
        {
            module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"));
            allocator = Malloc;
            try
            {
                var create = Export<CreateControl>("ADL2_Main_Control_Create");
                destroy = Export<DestroyControl>("ADL2_Main_Control_Destroy");
                ddc = Export<DdcBlockAccess>("ADL2_Display_DDCBlockAccess_Get");
                Check(create(allocator, 1, out context), "create ADL2 context");
                if (context == IntPtr.Zero) throw new TransportException("amd-adl", "AMD returned a null ADL2 context.");
                Enumerate();
            }
            catch
            {
                if (context != IntPtr.Zero) destroy?.Invoke(context);
                NativeLibrary.Free(module);
                throw;
            }
        }

        public AmdDisplay Select(string gdiName, int? product)
        {
            var onSource = displays.Where(d => string.Equals(d.GdiName, gdiName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (onSource.Length == 0) throw new TransportException("amd-adl", $"AMD has no active display mapped to {gdiName}.");
            if (product is int code)
            {
                var matching = onSource.Where(d => d.ProductCode == code).ToArray();
                if (matching.Length == 1) return matching[0];
                if (matching.Length > 1) throw new TransportException("amd-adl", "AMD reported several displays with the same EDID product.");
                if (onSource.Length == 1 && onSource[0].ProductCode is null) return onSource[0];
                throw new TransportException("amd-adl", "AMD could not identify a unique display with that EDID on this source.");
            }
            if (onSource.Length == 1) return onSource[0];
            throw new TransportException("amd-adl", $"AMD reported {onSource.Length} displays on {gdiName}; an EDID product code is required.");
        }

        public void Send(AmdDisplay target, byte[] packet)
        {
            int noReceive = 0;
            Check(ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0, packet.Length, packet, ref noReceive, null), "send DDC");
        }

        public byte[] Receive(AmdDisplay target, int length, out int received)
        {
            byte[] buffer = new byte[length];
            received = buffer.Length;
            Check(ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0, 1, [0x6F], ref received, buffer), "receive DDC");
            return buffer;
        }

        private void Enumerate()
        {
            var countAdapters = Export<AdapterCount>("ADL2_Adapter_NumberOfAdapters_Get");
            var getAdapters = Export<AdapterInfoGet>("ADL2_Adapter_AdapterInfo_Get");
            var getDisplays = Export<DisplayInfoGet>("ADL2_Display_DisplayInfo_Get");
            NativeLibrary.TryGetExport(module, "ADL2_Display_EdidData_Get", out IntPtr edidAddress);
            var getEdid = edidAddress == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<EdidDataGet>(edidAddress);
            Check(countAdapters(context, out int count), "count AMD adapters");
            if (count is < 1 or > 250) throw new TransportException("amd-adl", $"Invalid AMD adapter count: {count}.");
            byte[] adapters = new byte[checked(count * AdapterInfoSize)];
            for (int i = 0; i < count; i++) BitConverter.GetBytes(AdapterInfoSize).CopyTo(adapters, i * AdapterInfoSize);
            var pinned = GCHandle.Alloc(adapters, GCHandleType.Pinned);
            try { Check(getAdapters(context, pinned.AddrOfPinnedObject(), adapters.Length), "read AMD adapters"); }
            finally { pinned.Free(); }
            for (int i = 0; i < count; i++)
            {
                int offset = i * AdapterInfoSize;
                int adapterIndex = BitConverter.ToInt32(adapters, offset + 4);
                string gdiName = Ansi(adapters, offset + 536, 256);
                if (BitConverter.ToInt32(adapters, offset + 792) == 0 || string.IsNullOrWhiteSpace(gdiName)) continue;
                IntPtr records = IntPtr.Zero;
                try
                {
                    Check(getDisplays(context, adapterIndex, out int displayCount, out records, 0), "enumerate AMD displays");
                    if (displayCount is < 0 or > 150 || displayCount > 0 && records == IntPtr.Zero)
                        throw new TransportException("amd-adl", "AMD returned an invalid display list.");
                    for (int j = 0; j < displayCount; j++)
                    {
                        IntPtr item = records + j * DisplayInfoSize;
                        int displayIndex = Marshal.ReadInt32(item);
                        int mappedAdapter = Marshal.ReadInt32(item, 8);
                        uint mask = unchecked((uint)Marshal.ReadInt32(item, 544));
                        uint value = unchecked((uint)Marshal.ReadInt32(item, 548));
                        if (mappedAdapter != adapterIndex || (mask & value & 3) != 3) continue;
                        string name = Marshal.PtrToStringAnsi(item + 20, 256)?.Split('\0')[0].Trim() ?? "";
                        int? product = getEdid is null ? null : ReadProductCode(getEdid, adapterIndex, displayIndex);
                        displays.Add(new AmdDisplay(adapterIndex, displayIndex, gdiName, name, product));
                    }
                }
                finally { if (records != IntPtr.Zero) Marshal.FreeHGlobal(records); }
            }
        }

        private int? ReadProductCode(EdidDataGet getEdid, int adapter, int display)
        {
            IntPtr buffer = Marshal.AllocHGlobal(EdidDataSize);
            try
            {
                for (int i = 0; i < EdidDataSize; i++) Marshal.WriteByte(buffer, i, 0);
                Marshal.WriteInt32(buffer, EdidDataSize);
                if (getEdid(context, adapter, display, buffer) != 0 || Marshal.ReadInt32(buffer, 8) < 128)
                    return null;
                int low = Marshal.ReadByte(buffer, 26), high = Marshal.ReadByte(buffer, 27);
                return low | high << 8;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

        private static string Ansi(byte[] bytes, int offset, int length) =>
            Encoding.Latin1.GetString(bytes, offset, length).Split('\0')[0].Trim();

        private static void Check(int result, string operation)
        {
            if (result != 0) throw new TransportException("amd-adl", $"AMD ADL2 could not {operation}: code {result}.");
        }

        public void Dispose()
        {
            if (context != IntPtr.Zero) destroy(context);
            context = IntPtr.Zero;
            NativeLibrary.Free(module);
        }

        private static IntPtr Malloc(int bytes) => Marshal.AllocHGlobal(bytes);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AllocCallback(int bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreateControl(AllocCallback alloc, int enumerateConnected, out IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DestroyControl(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AdapterCount(IntPtr context, out int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AdapterInfoGet(IntPtr context, IntPtr buffer, int bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DisplayInfoGet(IntPtr context, int adapter, out int count, out IntPtr records, int forceDetect);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int EdidDataGet(IntPtr context, int adapter, int display, IntPtr edid);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DdcBlockAccess(IntPtr context, int adapter, int display, int option, int command,
            int sendBytes, [In] byte[] send, ref int receiveBytes, [Out] byte[]? receive);
    }
}
