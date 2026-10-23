using SwitchMonitor.Core.Vcp;

namespace SwitchMonitor.Core.Transports;

/// <summary>DDC/CI wire format helpers shared by the GPU-driver transports.</summary>
internal static class DdcPacket
{
    /// <summary>Destination address of the display (0x6E = 0x37 &lt;&lt; 1).</summary>
    internal const byte DisplayAddress = 0x6E;
    internal const byte HostReadSource = 0x51;
    internal const byte HostWriteSource = 0x50;

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
