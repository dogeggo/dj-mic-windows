using System.Buffers.Binary;
using MicMini.Desktop.Protocol;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
void Reject(byte[] payload, string name)
{
    try { MicCapabilitySet.Decode(payload); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception("未拒绝 " + name);
}
byte[] Payload(params (uint Selector, MicTlv[] Items)[] records)
{
    using var stream = new MemoryStream();
    stream.Write([0, 0]);
    foreach (var record in records)
    {
        var tlvs = MicTlvCodec.Encode(record.Items);
        var header = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(header, record.Selector);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), checked((ushort)tlvs.Length));
        stream.Write(header); stream.Write(tlvs);
    }
    var result = stream.ToArray();
    BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)(result.Length - 2)));
    return result;
}
MicTlv Range(int minimum, int maximum, int step, ushort type = 6)
{
    var value = new byte[12];
    BinaryPrimitives.WriteInt32LittleEndian(value, minimum);
    BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), maximum);
    BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(8), step);
    return new(type, value);
}

// Captured 5B/12 response: the initial 58 00 is a length, not an ACK byte.
var golden = Convert.FromHexString("58000000000035000D00010106000CF4FFFFFF0C0000000600000009000CF4FFFFFF0C00000001000000110008C80000000000000005000107150001010100000017000200080000000000000000030001030400010512000100");
var capability = MicCapabilitySet.Decode(golden);
Check(capability.Devices.Count == 2, "真实RX/TX能力记录");
Check(capability.Devices[0].Count == 6 && capability.Devices[1].Count == 4, "真实TLV数量");
Check(capability.GainRange(0) == new MicGainRange(-12, 12, 6), "真实RX增益范围");
Check(capability.GainRange(1) is null && !capability.Has(1, 6), "TX未声明增益能力");
Check(capability.EnumMask(0, 5) == 7 && capability.EnumMask(1, 3) == 3 && capability.EnumMask(1, 4) == 5, "真实枚举支持位");
Check(capability.Has(1, 0x12) && capability.EnumMask(1, 0x12) == 0, "零mask与不存在不同");
Check(capability.Devices[0].Single(item => item.Type == 0x11).Value.SequenceEqual(Convert.FromHexString("C800000000000000")), "未知能力原始值保留");
Check(capability.EnumMask(0, 0x11) is null && capability.EnumMask(9, 5) is null, "非mask长度和缺失设备返回未知");
Check(MicCapabilitySet.Decode([0, 0]).Devices.Count == 0, "合法空能力集合");

for (var length = 0; length < golden.Length; length++)
    Reject(golden[..length], "真实响应每个截断位置 " + length);
Reject(golden.Concat(new byte[] { 0 }).ToArray(), "尾随数据");
Reject([1, 0, 0], "设备记录头截断");
Reject([6, 0, 0, 0, 0, 0, 1, 0], "设备记录超出body");
Reject([7, 0, 0, 0, 0, 0, 1, 0, 1], "TLV头截断");
Reject([9, 0, 0, 0, 0, 0, 3, 0, 4, 0, 1], "TLV值截断");

var repeated = MicCapabilitySet.Decode(Payload(
    (1, [new(4, [5]), Range(-12, 12, 6)]),
    (1, [new(4, [5]), Range(-12, 12, 6), new(ushort.MaxValue, [])])));
Check(repeated.Devices.Count == 1 && repeated.Devices[1].Count == 5, "重复selector与TLV保留");
Check(repeated.EnumMask(1, 4) is null && repeated.GainRange(1) is null, "相同重复值也不猜唯一语义");
Check(repeated.Has(1, 4) && repeated.Has(1, ushort.MaxValue), "Has仅反映存在");

var masks = MicCapabilitySet.Decode(Payload((uint.MaxValue, [
    new(1, [0x80]), new(2, [1, 2]), new(3, [1, 2, 3]), new(4, [1, 2, 3, 0x80]),
    new(5, []), new(6, [1, 2, 3, 4, 5])])));
Check(masks.EnumMask(uint.MaxValue, 1) == 0x80 && masks.EnumMask(uint.MaxValue, 2) == 0x0201 &&
    masks.EnumMask(uint.MaxValue, 3) == 0x030201 && masks.EnumMask(uint.MaxValue, 4) == 0x80030201,
    "1至4字节小端uint mask，包括高位");
Check(masks.EnumMask(uint.MaxValue, 5) is null && masks.EnumMask(uint.MaxValue, 6) is null, "非法mask长度");

foreach (var item in new[] { Range(12, -12, 6), Range(-12, 12, 0), Range(-12, 12, -1),
    Range(int.MinValue, 12, 1), Range(-12, int.MaxValue, 1), Range(-12, 12, int.MaxValue),
    Range(-129, 12, 1), Range(-12, 128, 1), Range(-12, 12, 256), new MicTlv(6, new byte[11]),
    new MicTlv(6, new byte[13]) })
    Check(MicCapabilitySet.Decode(Payload((0, [item]))).GainRange(0) is null, "非法增益范围不会解锁控件");
Check(MicCapabilitySet.Decode(Payload((0, [Range(0, 0, 1)]))).GainRange(0) == new MicGainRange(0, 0, 1), "合法固定增益范围");
Check(MicCapabilitySet.Decode(Payload((0, [Range(-128, 127, 255)]))).GainRange(0) == new MicGainRange(-128, 127, 255), "int8编码边界");
Check(MicCapabilitySet.Decode(Payload((0, [Range(-12, 12, 1, 9)]))).GainRange(0) is null, "监听增益类型不冒充主增益");

Console.WriteLine($"PASS: {checks} capability checks; real 5B/12 payload and malformed inputs verified.");
