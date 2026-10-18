using System.Buffers.Binary;

namespace MicMini.Desktop.Protocol;

public sealed record DumlFrame(byte Sender, byte Receiver, ushort Sequence, byte Flags, byte CommandSet, byte Command, byte[] Payload);

/// <summary>DUML v1 framing recovered from DjiProtocolEncoder; only for a validated bulk transport.</summary>
public static class DumlCodec
{
    public const int MaximumFrameLength = 1023;
    public static byte[] Encode(DumlFrame frame)
    {
        if (frame.Payload.Length > MaximumFrameLength - 13) throw new ArgumentOutOfRangeException(nameof(frame));
        var bytes = new byte[13 + frame.Payload.Length]; bytes[0] = 0x55;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(1), (ushort)(0x400 | bytes.Length));
        bytes[3] = Crc8(bytes.AsSpan(0, 3)); bytes[4] = frame.Sender; bytes[5] = frame.Receiver;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), frame.Sequence);
        bytes[8] = frame.Flags; bytes[9] = frame.CommandSet; bytes[10] = frame.Command;
        frame.Payload.CopyTo(bytes, 11);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2), Crc16(bytes.AsSpan(0, bytes.Length - 2)));
        return bytes;
    }

    public static DumlFrame Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 13 || bytes.Length > MaximumFrameLength || bytes[0] != 0x55) throw new InvalidDataException("DUML 帧长度或起始标记无效。");
        var lengthVersion = BinaryPrimitives.ReadUInt16LittleEndian(bytes[1..]);
        if ((lengthVersion >> 10) != 1 || (lengthVersion & 0x3ff) != bytes.Length) throw new InvalidDataException("DUML 长度或版本无效。");
        if (bytes[3] != Crc8(bytes[..3])) throw new InvalidDataException("DUML 帧头 CRC8 不匹配。");
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[^2..]) != Crc16(bytes[..^2])) throw new InvalidDataException("DUML CRC16 不匹配。");
        return new(bytes[4], bytes[5], BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), bytes[8], bytes[9], bytes[10], bytes[11..^2].ToArray());
    }

    public static byte Crc8(ReadOnlySpan<byte> bytes)
    {
        byte crc = 0x77;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (byte)((crc >> 1) ^ ((crc & 1) != 0 ? 0x8c : 0));
        }
        return crc;
    }
    public static ushort Crc16(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0x3692;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++) crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0x8408 : 0));
        }
        return crc;
    }
}

/// <summary>Handles USB fragments/coalesced packets, rejecting CRC failures and resynchronizing.</summary>
public sealed class DumlStreamDecoder
{
    private readonly List<byte> pending = new(DumlCodec.MaximumFrameLength);
    public int RejectedBytes { get; private set; }
    public int BufferedBytes => pending.Count;
    public IReadOnlyList<DumlFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<DumlFrame>();
        foreach (var b in bytes)
        {
            pending.Add(b);
            while (pending.Count > 0)
            {
                if (pending[0] != 0x55) { Reject(); continue; }
                if (pending.Count < 4) break;
                var header = pending.Take(4).ToArray();
                var lengthVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
                var length = lengthVersion & 0x3ff;
                if ((lengthVersion >> 10) != 1 || length < 13 || header[3] != DumlCodec.Crc8(header.AsSpan(0, 3))) { Reject(); continue; }
                if (pending.Count < length) break;
                try
                {
                    frames.Add(DumlCodec.Decode(pending.Take(length).ToArray()));
                    pending.RemoveRange(0, length);
                }
                catch (InvalidDataException) { Reject(); }
            }
        }
        return frames;
    }
    private void Reject() { pending.RemoveAt(0); RejectedBytes++; }
    public void Reset() { pending.Clear(); RejectedBytes = 0; }
}
