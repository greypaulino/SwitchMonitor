using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SwitchMonitor.Wpf;

// AMD ADL2 transport. The public ADL structs and DDCBlockAccess ABI are used here;
// every command is restricted to one display mapped to the requested Windows source.
internal sealed class AmdLegacyDdc : IDisposable
{
    private const int AdapterInfoSize = 1572;
    private const int DisplayInfoSize = 552;
    private const int EdidDataSize = 288;
    private readonly IntPtr module;
    private readonly AllocCallback allocator;
    private readonly DestroyControl? destroy;
    private readonly DdcBlockAccess ddc;
    private readonly List<AmdDisplay> displays = [];
    private IntPtr context;
    private bool disposed;

    internal static bool IsAvailable => Environment.Is64BitProcess &&
        File.Exists(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"));

    internal AmdLegacyDdc()
    {
        if (!IsAvailable) throw new NotSupportedException("The AMD ADL2 driver DLL is unavailable.");
        module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "atiadlxx.dll"));
        allocator = Malloc;
        try
        {
            var create = Export<CreateControl>("ADL2_Main_Control_Create");
            destroy = Export<DestroyControl>("ADL2_Main_Control_Destroy");
            ddc = Export<DdcBlockAccess>("ADL2_Display_DDCBlockAccess_Get");
            Check(create(allocator, 1, out context), "create ADL2 context");
            if (context == IntPtr.Zero) throw new InvalidOperationException("AMD returned a null ADL2 context.");
            Enumerate();
        }
        catch
        {
            if (context != IntPtr.Zero) destroy?.Invoke(context);
            NativeLibrary.Free(module);
            throw;
        }
    }

    internal IReadOnlyList<AmdDisplay> Displays => displays;

    private void Enumerate()
    {
        var countAdapters = Export<AdapterCount>("ADL2_Adapter_NumberOfAdapters_Get");
        var getAdapters = Export<AdapterInfoGet>("ADL2_Adapter_AdapterInfo_Get");
        var getDisplays = Export<DisplayInfoGet>("ADL2_Display_DisplayInfo_Get");
        NativeLibrary.TryGetExport(module, "ADL2_Display_EdidData_Get", out IntPtr edidAddress);
        var getEdid = edidAddress == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<EdidDataGet>(edidAddress);
        Check(countAdapters(context, out int count), "count AMD adapters");
        if (count is < 1 or > 250) throw new InvalidDataException($"Invalid AMD adapter count: {count}.");
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
                Check(getDisplays(context, adapterIndex, out int displayCount, out records, 0),
                    "enumerate AMD displays");
                if (displayCount is < 0 or > 150 || displayCount > 0 && records == IntPtr.Zero)
                    throw new InvalidDataException("AMD returned an invalid display list.");
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

    internal AmdDisplay Select(string gdiName, string deviceId)
    {
        var onSource = displays.Where(display =>
            string.Equals(display.GdiName, gdiName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (onSource.Length == 0) throw new InvalidOperationException($"AMD has no active display mapped to {gdiName}.");
        var match = Regex.Match(deviceId, @"(?i)\\GSM(7714|7715)\\");
        if (!match.Success) throw new NotSupportedException("This AMD path is restricted to the identified LG 29WK600.");
        int product = Convert.ToInt32(match.Groups[1].Value, 16);
        var matchingEdid = onSource.Where(display => display.ProductCode == product).ToArray();
        if (matchingEdid.Length == 1) return matchingEdid[0];
        if (matchingEdid.Length > 1) throw new InvalidOperationException("AMD reported multiple displays with the LG EDID.");
        if (onSource.Length == 1 && (onSource[0].ProductCode is null || onSource[0].ProductCode == product))
            return onSource[0];
        throw new InvalidOperationException("AMD could not identify a unique LG display on this Windows source.");
    }

    internal (uint Current, uint Maximum) ReadVcp(AmdDisplay target, byte feature)
    {
        byte[] send = Packet(0x51, [0x82, 0x01, feature]);
        int noReceive = 0;
        Check(ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0,
            send.Length, send, ref noReceive, null), $"query VCP {feature:X2}");
        Thread.Sleep(50);
        byte[] receive = new byte[16];
        int received = receive.Length;
        Check(ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0,
            1, [0x6F], ref received, receive), $"read VCP {feature:X2}");
        return ParseVcpReply(receive, received, feature);
    }

    internal static (uint Current, uint Maximum) ParseVcpReply(byte[] receive, int received, byte feature)
    {
        if (received >= 11 && receive[0] == 0x6E && receive[1] == 0x88 &&
            receive[2] == 2 && receive[3] == 0 && receive[4] == feature &&
            ValidReplyChecksum(receive.AsSpan(0, 11)))
            return ((uint)(receive[8] << 8 | receive[9]), (uint)(receive[6] << 8 | receive[7]));
        throw new InvalidDataException($"AMD returned an invalid VCP {feature:X2} reply: " +
            Convert.ToHexString(receive.AsSpan(0, Math.Clamp(received, 0, receive.Length))));
    }

    internal string ProbeRawVcp(AmdDisplay target, byte feature, int receiveLength)
    {
        byte[] send = Packet(0x51, [0x82, 0x01, feature]);
        byte[] receive = new byte[receiveLength];
        int received = receive.Length;
        int result = ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0,
            send.Length, send, ref received, receive);
        return $"ADL={result} reportedLength={received} bytes={Convert.ToHexString(receive)}";
    }

    internal string ProbeSplitVcp(AmdDisplay target, byte feature)
    {
        // ADL's documented send-only and receive forms correspond to separate
        // I2C write/read transactions. The request is a Get VCP command only.
        byte[] send = Packet(0x51, [0x82, 0x01, feature]);
        int noReceive = 0;
        int writeResult = ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0,
            send.Length, send, ref noReceive, null);
        if (writeResult != 0) return $"query ADL={writeResult}";
        Thread.Sleep(50);
        byte[] receive = new byte[16];
        int received = receive.Length;
        int readResult = ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0,
            1, [0x6F], ref received, receive);
        return $"query ADL={writeResult} read ADL={readResult} reportedLength={received} bytes={Convert.ToHexString(receive)}";
    }

    private static bool ValidReplyChecksum(ReadOnlySpan<byte> reply)
    {
        byte checksum = 0x50;
        foreach (byte value in reply) checksum ^= value;
        return checksum == 0;
    }

    internal void SetVcp(AmdDisplay target, byte feature, uint value)
    {
        if (value > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        byte[] send = Packet(0x50, [0x84, 0x03, feature, (byte)(value >> 8), (byte)value]);
        int received = 0;
        Check(ddc(context, target.AdapterIndex, target.DisplayIndex, 0, 0,
            send.Length, send, ref received, null), $"set VCP {feature:X2}");
    }

    internal void SetLgInput(AmdDisplay target, int code)
    {
        byte value = code switch { 17 => 0x90, 18 => 0x91, 15 => 0xD0,
            _ => throw new ArgumentOutOfRangeException(nameof(code)) };
        SetVcp(target, 0xF4, value);
    }

    internal static byte[] Packet(byte source, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[payload.Length + 3];
        packet[0] = 0x6E;
        packet[1] = source;
        payload.CopyTo(packet.AsSpan(2));
        byte checksum = 0;
        foreach (byte value in packet.AsSpan(0, packet.Length - 1)) checksum ^= value;
        packet[^1] = checksum;
        return packet;
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    private static string Ansi(byte[] bytes, int offset, int length) =>
        Encoding.Default.GetString(bytes, offset, length).Split('\0')[0].Trim();

    private static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"AMD ADL2 could not {operation}: code {result}.");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (context != IntPtr.Zero) destroy?.Invoke(context);
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

internal sealed record AmdDisplay(int AdapterIndex, int DisplayIndex, string GdiName, string Name, int? ProductCode);
