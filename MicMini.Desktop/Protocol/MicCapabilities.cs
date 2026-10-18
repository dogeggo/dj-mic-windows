using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace MicMini.Desktop.Protocol;

public sealed record MicGainRange(int Minimum, int Maximum, int Step);

/// <summary>
/// A 5B/12 response starts with a body length, not a result code. Capability
/// types form a separate namespace from the 5B/01 setting types.
/// </summary>
public sealed class MicCapabilitySet
{
    private const ushort GainRangeType = 0x0006;

    public IReadOnlyDictionary<uint, IReadOnlyList<MicTlv>> Devices { get; }

    private MicCapabilitySet(Dictionary<uint, List<MicTlv>> devices)
    {
        Devices = new ReadOnlyDictionary<uint, IReadOnlyList<MicTlv>>(
            devices.ToDictionary(pair => pair.Key,
                pair => (IReadOnlyList<MicTlv>)pair.Value.AsReadOnly()));
    }

    /// <summary>
    /// Decodes the complete DUML payload: bodyLength:u16, followed by records
    /// containing selector:u32, recordLength:u16, and ordinary Mic TLVs.
    /// Duplicate selectors are combined without discarding duplicate TLVs.
    /// </summary>
    public static MicCapabilitySet Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
            throw new InvalidDataException("能力响应总长度字段被截断。");

        var bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (bodyLength != payload.Length - 2)
            throw new InvalidDataException("能力响应声明长度与实际长度不一致。");

        var devices = new Dictionary<uint, List<MicTlv>>();
        var body = payload[2..];
        while (!body.IsEmpty)
        {
            if (body.Length < 6)
                throw new InvalidDataException("能力响应设备记录头被截断。");

            var selector = BinaryPrimitives.ReadUInt32LittleEndian(body);
            var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
            body = body[6..];
            if (recordLength > body.Length)
                throw new InvalidDataException($"设备 {selector} 的能力记录被截断。");

            var items = MicTlvCodec.Decode(body[..recordLength]);
            if (!devices.TryGetValue(selector, out var existing))
            {
                existing = [];
                devices.Add(selector, existing);
            }
            existing.AddRange(items);
            body = body[recordLength..];
        }

        return new MicCapabilitySet(devices);
    }

    /// <summary>Reports presence only; it does not interpret or validate a value.</summary>
    public bool Has(uint selector, ushort type) =>
        Devices.TryGetValue(selector, out var items) && items.Any(item => item.Type == type);

    /// <summary>
    /// Returns a unique 1–4 byte little-endian support mask. Mask bits are
    /// capability enum values, not necessarily the setting's enum values.
    /// Missing, duplicated, or differently sized values remain unknown.
    /// </summary>
    public uint? EnumMask(uint selector, ushort type)
    {
        var item = Unique(selector, type);
        if (item is null || item.Value.Length is < 1 or > 4)
            return null;

        uint mask = 0;
        for (var index = 0; index < item.Value.Length; index++)
            mask |= (uint)item.Value[index] << (8 * index);
        return mask;
    }

    /// <summary>
    /// Interprets only capability type 06 as three signed little-endian int32s.
    /// The supported setting uses an int8, so out-of-byte-range gain limits
    /// and steps larger than the entire int8 domain are rejected.
    /// </summary>
    public MicGainRange? GainRange(uint selector)
    {
        var item = Unique(selector, GainRangeType);
        if (item is null || item.Value.Length != 12)
            return null;

        var bytes = item.Value.AsSpan();
        var minimum = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        var maximum = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]);
        var step = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        if (minimum < sbyte.MinValue || maximum > sbyte.MaxValue || minimum > maximum ||
            step is <= 0 or > byte.MaxValue)
            return null;

        return new MicGainRange(minimum, maximum, step);
    }

    private MicTlv? Unique(uint selector, ushort type)
    {
        if (!Devices.TryGetValue(selector, out var items))
            return null;

        MicTlv? match = null;
        foreach (var item in items)
        {
            if (item.Type != type) continue;
            if (match is not null) return null;
            match = item;
        }
        return match;
    }
}
