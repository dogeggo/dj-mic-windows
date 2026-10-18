using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MicMini.Desktop.Protocol;

// SYNTHETIC, NOT A REAL DJI FIRMWARE. Generated independently in Python using
// the recovered layout. This fixture must never be submitted to hardware.
// Golden header CRC = A2AB; complete fixture length = 234 bytes.
var fixture = Convert.FromBase64String("eFY0EgEAqgAAAABlREpJAAAAAAAAAAAAAAAAAGRtMzAwAAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAAAAADoAAAAEAwIBqgAAACAAAAAgAAAAWYUzG87JcbJiEiqhylrUEVmFMxvOyXGyYhIqocpa1BFaAAAABgUEA8oAAAAgAAAAIAAAACmtxMTCmlH8WzJnJCWI+y8prcTEwppR/FsyZyQliPsvq6IBAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fIFNZTlRIRVRJQyBSWCAtIE5PVCBSRUFMIEZJUk1XQVJF");
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
}
void Reject(Action action, string description)
{
    try { action(); }
    catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or ArgumentException)
    { checks++; return; }
    throw new Exception("未拒绝：" + description);
}
void Set16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value);
void Set32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

// Independent table-driven checksum in the fixture mutator. Production uses a
// bit loop. Header/mapping mutations must pass CRC before semantic rejection.
void RecheckHeader(byte[] bytes)
{
    var table = new ushort[256];
    for (var value = 0; value < 256; value++)
    {
        var crc = value;
        for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1;
        table[value] = (ushort)crc;
    }
    var end = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6, 2));
    ushort checksum = 0x3692;
    for (var index = 0; index < end - 2; index++)
        checksum = (ushort)((checksum >> 8) ^ table[(checksum ^ bytes[index]) & 255]);
    Set16(bytes, end - 2, checksum);
}
byte[] Change(Action<byte[]> mutation, bool renewCrc = true)
{
    var bytes = fixture.ToArray();
    mutation(bytes);
    if (renewCrc) RecheckHeader(bytes);
    return bytes;
}
var tx = DjiFirmwareTarget.ForProductType(0xED);
var rx = DjiFirmwareTarget.ForProductType(0xEE);
DjiFirmwareContainer Read(byte[] bytes) => DjiFirmwareContainer.Parse(bytes, tx);

Check(Convert.ToHexString(SHA256.HashData(fixture)) == "E3EC18EE309A694B6BA55C6E2D0E3BAB35527BF59199884795CDA1523B4E4020", "固定合成样本 SHA-256");
var parsed = Read(fixture);
Check(parsed.Model == "dm300" && parsed.Manufacturer == "DJI" && parsed.HeaderLength == 170, "型号与52字节描述符包头");
Check(parsed.FormatVersion == 1 && parsed.Modules.Count == 2 && parsed.FileLength == 234, "容器版本、模块数、总长");
Check(parsed.SelectedModule.ProductType == 0xED && parsed.SelectedModule.ModuleId == 0x3A && parsed.SelectedModule.ReceiverType == 26 && parsed.SelectedModule.ReceiverSequence == 1, "TX目标位域及产品ID");
Check(parsed.SelectedModule.FirmwareVersion == "01.02.03.04" && parsed.SelectedModule.FirmwareVersionRaw == 0x01020304, "模块四字节版本顺序");
Check(parsed.SelectedModule.FirmwareData.Span.SequenceEqual(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()), "只返回载荷切片、不含总包头");
Check(parsed.SelectedModule.DataOffset == 170 && parsed.SelectedModule.StoredLength == 32 && parsed.SelectedModule.OriginalLength == 32, "描述符长度字段含义");
Check(parsed.SelectedModule.StoredMd5 == "5985331BCEC971B262122AA1CA5AD411" && parsed.SelectedModule.OriginalMd5 == parsed.SelectedModule.StoredMd5, "两个明文载荷MD5");
Check(parsed.IntegrityVerified && !parsed.AuthenticityVerified, "完整性不冒充官方真实性认证");
var selectedRx = DjiFirmwareContainer.Parse(fixture, rx).SelectedModule;
Check(selectedRx.ProductType == 0xEE && selectedRx.ModuleId == 0x5A && selectedRx.FirmwareVersion == "03.04.05.06", "RX独立模块选择");

var mini2 = Change(bytes => { bytes.AsSpan(28, 16).Clear(); Encoding.ASCII.GetBytes("dm202b").CopyTo(bytes.AsSpan(28)); });
Check(DjiFirmwareContainer.Parse(mini2, DjiFirmwareTarget.ForProductType(0x140)).SelectedModule.ModuleId == 0x5A, "dm202b RX对应产品0x140");
Check(DjiFirmwareContainer.Parse(mini2, DjiFirmwareTarget.ForProductType(0x141)).SelectedModule.ModuleId == 0x3A, "dm202b TX对应产品0x141");
Reject(() => DjiFirmwareContainer.Parse(mini2, tx), "当前RX和TX不可混用型号");
Reject(() => DjiFirmwareTarget.ForProductType(0x142), "72字节cbox扩展未放行");
Reject(() => DjiFirmwareContainer.Parse(fixture, tx with { ReceiverSequence = 2 }), "伪造目标身份组合");

Reject(() => Read(fixture[..65]), "不足固定头部长度");
Reject(() => Read(fixture[..180]), "模块数据截断");
Reject(() => Read(Change(bytes => bytes[0] ^= 1)), "错误magic");
Reject(() => Read(Change(bytes => Set16(bytes, 4, 0))), "旧版描述符不猜测");
Reject(() => Read(Change(bytes => Set16(bytes, 4, 2))), "未知描述符版本");
Reject(() => Read(Change(bytes => Set16(bytes, 6, 169), false)), "错误包头长度");
Reject(() => Read(Change(bytes => bytes[12] = (byte)'X')), "制造商不符");
Reject(() => Read(Change(bytes => bytes[28] = (byte)'x')), "未知产品名");
Reject(() => Read(Change(bytes => bytes[28] = (byte)'D')), "型号必须精确匹配");
Reject(() => Read(Change(bytes => bytes.AsSpan(28, 16).Fill((byte)'a'))), "未终止型号字段");
Reject(() => Read(Change(bytes => bytes[38] = (byte)'x')), "型号零终止后的隐藏内容");
Reject(() => Read(Change(bytes => bytes[54] = 1)), "未知非零头部保留数据");
Reject(() => Read(Change(bytes => Set16(bytes, 44, 0))), "零模块");
Reject(() => Read(Change(bytes => Set16(bytes, 44, 65535))), "模块数量溢出/过大");
Reject(() => Read(Change(bytes => bytes[168] ^= 1, false)), "头部CRC错误");
Reject(() => Read(Change(bytes => bytes[65] = 0x10)), "加密模块");
Reject(() => Read(Change(bytes => bytes[65] = 1)), "未知特殊编码");
Reject(() => Read(Change(bytes => bytes[66] = 1)), "非零模块保留位");
Reject(() => Read(Change(bytes => Set32(bytes, 80, 31))), "存储长度和原始长度不同");
Reject(() => Read(Change(bytes => { Set32(bytes, 76, 0); Set32(bytes, 80, 0); })), "零长载荷");
Reject(() => Read(Change(bytes => Set32(bytes, 72, 64))), "模块覆盖描述符表");
Reject(() => Read(Change(bytes => Set32(bytes, 72, 234))), "模块终点越界");
Reject(() => Read(Change(bytes => Set32(bytes, 72, 0xFFFFFFF0))), "UInt32起点加长度回绕");
Reject(() => Read(Change(bytes => Set32(bytes, 124, 185))), "模块相互重叠");
Reject(() => Read(Change(bytes => bytes[116] = 0x3A)), "重复模块地址");
Reject(() => Read(Change(bytes => bytes[64] = 0x39)), "未知receiver type");
Reject(() => Read(Change(bytes => bytes[64] = 0x7A)), "未知receiver sequence");
var txOnly = Change(bytes => { Set16(bytes, 44, 1); Set16(bytes, 6, 118); });
Reject(() => DjiFirmwareContainer.Parse(txOnly, rx), "容器无匹配目标模块");
Reject(() => Read(Change(bytes => bytes[84] ^= 1)), "存储MD5被篡改");
Reject(() => Read(Change(bytes => bytes[100] ^= 1)), "原始MD5被篡改");
Reject(() => Read(Change(bytes => bytes[170] ^= 1, false)), "载荷损坏且包头CRC仍正确");

var mutableInput = fixture.ToArray();
var stable = Read(mutableInput);
mutableInput[170] ^= 0xFF;
Check(stable.SelectedModule.FirmwareData.Span[0] == 1 && stable.ContainerSha256 == parsed.ContainerSha256, "解析器持有与已校验摘要一致的独立快照");
Console.WriteLine($"PASS: {checks} offline checks. SYNTHETIC ONLY; no genuine package or hardware validation performed.");
