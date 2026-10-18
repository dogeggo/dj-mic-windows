using System.Buffers.Binary;

namespace MicMini.Desktop.Protocol;

public sealed record MicTlv(ushort Type, byte[] Value);
/// <summary>Offline payload codec. A TLV is NOT a complete USB/BLE packet.</summary>
public static class MicTlvCodec
{
    public static IReadOnlyList<MicTlv> Decode(ReadOnlySpan<byte> bytes)
    {
        var result = new List<MicTlv>();
        while (!bytes.IsEmpty)
        {
            if (bytes.Length < 3) throw new InvalidDataException("TLV 头被截断。");
            var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            var length = bytes[2];
            if (bytes.Length < 3 + length) throw new InvalidDataException($"TLV 0x{type:X4} 内容被截断。");
            result.Add(new(type, bytes.Slice(3, length).ToArray()));
            bytes = bytes[(3 + length)..];
        }
        return result;
    }
    public static byte[] Encode(IEnumerable<MicTlv> items)
    {
        using var stream = new MemoryStream();
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item.Value);
            if (item.Value.Length > 255) throw new ArgumentOutOfRangeException(nameof(items), "TLV 值不能超过 255 字节。");
            stream.WriteByte((byte)item.Type); stream.WriteByte((byte)(item.Type >> 8));
            stream.WriteByte((byte)item.Value.Length); stream.Write(item.Value);
        }
        return stream.ToArray();
    }
    public static byte[] SettingPayload(byte mode, uint selector, int selectorBits, IEnumerable<MicTlv> items)
    {
        if (selectorBits != 8 && selectorBits != 32) throw new ArgumentOutOfRangeException(nameof(selectorBits));
        if (selectorBits == 8 && selector > 255) throw new ArgumentOutOfRangeException(nameof(selector));
        var tlv = Encode(items); var prefix = selectorBits == 8 ? 2 : 5;
        var payload = new byte[prefix + tlv.Length]; payload[0] = mode;
        if (selectorBits == 8) payload[1] = (byte)selector;
        else BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), selector);
        tlv.CopyTo(payload, prefix); return payload;
    }
}
