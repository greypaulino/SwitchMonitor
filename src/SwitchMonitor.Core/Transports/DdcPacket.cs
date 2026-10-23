using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>Write addressing used by the GPU-driver transports.</summary>
public static class DdcWriteSettings
{
    /// <summary>
    /// Source address for Set VCP writes other than the LG input switch (0xF4, which needs 0x50).
    /// MCCS specifies 0x51. Validated on the LG 29WK600 through intel-cui: with 0x50 only brightness took
    /// effect; with 0x51 brightness, contrast, volume, picture mode and response time all do.
    /// </summary>
    public static byte StandardWriteSource { get; set; } = 0x51;
}

/// <summary>DDC/CI wire format helpers shared by the GPU-driver transports.</summary>
internal static class DdcPacket
{
    /// <summary>Destination address of the display (0x6E = 0x37 &lt;&lt; 1).</summary>
    internal const byte DisplayAddress = 0x6E;
    internal const byte HostReadSource = 0x51;
    internal const byte HostWriteSource = 0x50;

    internal static byte WriteSourceFor(byte code) => code == 0xF4 ? HostWriteSource : DdcWriteSettings.StandardWriteSource;

    /// <summary>Get VCP Feature request payload: length 0x82, opcode 0x01, feature.</summary>
    internal static byte[] GetVcpPayload(byte feature) => [0x82, 0x01, feature];

    /// <summary>Set VCP Feature request payload: length 0x84, opcode 0x03, feature, value high, value low.</summary>
    internal static byte[] SetVcpPayload(byte feature, uint value)
    {
        if (value > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        return [0x84, 0x03, feature, (byte)(value >> 8), (byte)value];
    }

    /// <summary>Full packet with destination, source, payload and XOR checksum.</summary>
    internal static byte[] Build(byte source, ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[payload.Length + 3];
        packet[0] = DisplayAddress;
        packet[1] = source;
        payload.CopyTo(packet.AsSpan(2));
        byte checksum = 0;
        foreach (byte value in packet.AsSpan(0, packet.Length - 1)) checksum ^= value;
        packet[^1] = checksum;
        return packet;
    }

    /// <summary>Capabilities Request payload: length 0x83, opcode 0xF3, offset high, offset low.</summary>
    internal static byte[] CapabilitiesRequestPayload(ushort offset) => [0x83, 0xF3, (byte)(offset >> 8), (byte)offset];

    /// <summary>Longest Capabilities Reply fragment: address, length, E3, offset (2), 32 data bytes, checksum.</summary>
    internal const int MaxCapabilitiesReplyLength = 38;

    /// <summary>
    /// Parses one Capabilities Reply fragment:
    /// 6E (0x80|n) E3 offHi offLo data[n-3] checksum (checksum seeded with 0x50).
    /// An empty <paramref name="data"/> marks the end of the string.
    /// </summary>
    internal static bool TryParseCapabilitiesReply(ReadOnlySpan<byte> reply, ushort expectedOffset, out byte[] data)
    {
        data = [];
        if (reply.Length < 6 || reply[0] != DisplayAddress || (reply[1] & 0x80) == 0) return false;
        int length = reply[1] & 0x7F;
        if (length < 3 || length > 35 || reply.Length < length + 3) return false;
        if (reply[2] != 0xE3 || (reply[3] << 8 | reply[4]) != expectedOffset) return false;
        byte checksum = 0x50;
        foreach (byte b in reply[..(length + 3)]) checksum ^= b;
        if (checksum != 0) return false;
        data = reply.Slice(5, length - 3).ToArray();
        return true;
    }

    /// <summary>
    /// Assembles the full capabilities string from fragments fetched at increasing offsets.
    /// <paramref name="fetch"/> sends one request and returns the raw reply bytes.
    /// </summary>
    internal static string ReadCapabilities(Func<ushort, byte[]> fetch, string transportId, int maxBytes = 8192)
    {
        var text = new List<byte>();
        ushort offset = 0;
        for (int fragment = 0; fragment < 512; fragment++)
        {
            byte[]? data = null;
            for (int attempt = 1; attempt <= 3 && data is null; attempt++)
                if (TryParseCapabilitiesReply(fetch(offset), offset, out var parsed)) data = parsed;
            if (data is null)
                throw new TransportException(transportId, $"Invalid capabilities reply at offset {offset}.");
            if (data.Length == 0) break;
            text.AddRange(data);
            if (text.Count > maxBytes)
                throw new TransportException(transportId, "The capabilities string is unreasonably long.");
            offset = checked((ushort)(offset + data.Length));
        }
        return System.Text.Encoding.ASCII.GetString(text.ToArray()).Replace("\0", "").Trim();
    }

    /// <summary>
    /// Parses an 11-byte Get VCP Feature reply:
    /// 6E 88 02 result feature type maxHi maxLo curHi curLo checksum (checksum seeded with 0x50).
    /// </summary>
    internal static bool TryParseGetVcpReply(ReadOnlySpan<byte> reply, byte feature, out VcpValue value)
    {
        value = default;
        if (reply.Length < 11) return false;
        if (reply[0] != DisplayAddress || reply[1] != 0x88 || reply[2] != 0x02 || reply[3] != 0x00 || reply[4] != feature)
            return false;
        byte checksum = 0x50;
        foreach (byte b in reply[..11]) checksum ^= b;
        if (checksum != 0) return false;
        value = new VcpValue((uint)(reply[8] << 8 | reply[9]), (uint)(reply[6] << 8 | reply[7]));
        return true;
    }
}
