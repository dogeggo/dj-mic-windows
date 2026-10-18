using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MicMini.Desktop.Protocol;

/// <summary>
/// A device identity, not a filename hint. Values are recovered from the SDK's
/// VersionListFirmwareParser; only the two verified 52-byte descriptor layouts
/// are supported here. RX and TX product identities must be selected separately.
/// </summary>
public sealed record DjiFirmwareTarget(string Model, byte ReceiverType, byte ReceiverSequence, int ProductType)
{
    public static DjiFirmwareTarget ForProductType(int productType) => productType switch
    {
        0xED => new("dm300", 0x1A, 1, 0xED),
        0xEE => new("dm300", 0x1A, 2, 0xEE),
        0x140 => new("dm202b", 0x1A, 2, 0x140),
        0x141 => new("dm202b", 0x1A, 1, 0x141),
        _ => throw new NotSupportedException($"尚未恢复产品 0x{productType:X} 的固件容器布局，拒绝导入。")
    };
}

/// <summary>
/// The stored and original MD5 fields describe module bytes, not a trusted
/// signature. This parser accepts only unencoded modules with equal lengths;
/// therefore both digests must match the same payload. No decryption is inferred.
/// </summary>
public sealed record DjiFirmwareModule(
    int Index, int ProductType, byte ReceiverType, byte ReceiverSequence,
    byte ModuleId, string FirmwareVersion, uint FirmwareVersionRaw,
    uint DataOffset, uint StoredLength, uint OriginalLength,
    string StoredMd5, string OriginalMd5, ReadOnlyMemory<byte> FirmwareData);

/// <summary>
/// Read-only import of the narrow DJI container format recovered from
/// libdjisdk_jni.so, GetSplitedFirmwareModules @0x22ccdc0. There is no transport
/// or hardware access. CRC/MD5 checks establish integrity only; an independent
/// trusted official metadata digest is still needed to establish provenance.
/// See analysis/evidence/firmware-container-findings.md for evidence boundaries.
/// </summary>
public sealed class DjiFirmwareContainer
{
    public const int FixedHeaderLength = 64;
    public const int DescriptorLength = 52;
    public const int MaximumFileLength = 64 * 1024 * 1024;
    public const int MaximumModuleCount = 256;
    public const ushort SupportedFormatVersion = 1;

    public string Manufacturer { get; }
    public string Model { get; }
    public ushort FormatVersion { get; }
    public ushort HeaderLength { get; }
    public uint Timestamp { get; }
    // Kept raw: this pass has not validated the header's version encoding on a
    // genuine Mic package. Display module FirmwareVersion for a concrete version.
    public uint EncodedLatestVersionRaw { get; }
    public uint EncodedRollbackVersionRaw { get; }
    public int FileLength { get; }
    public string ContainerSha256 { get; }
    public ReadOnlyCollection<DjiFirmwareModule> Modules { get; }
    public DjiFirmwareModule SelectedModule { get; }
    public bool IntegrityVerified => true;
    public bool AuthenticityVerified => false;

    private DjiFirmwareContainer(string manufacturer, string model, ushort headerLength,
        uint timestamp, uint latestRaw, uint rollbackRaw, int fileLength, string containerSha256,
        DjiFirmwareModule[] modules, DjiFirmwareModule selected)
    {
        Manufacturer = manufacturer;
        Model = model;
        FormatVersion = SupportedFormatVersion;
        HeaderLength = headerLength;
        Timestamp = timestamp;
        EncodedLatestVersionRaw = latestRaw;
        EncodedRollbackVersionRaw = rollbackRaw;
        FileLength = fileLength;
        ContainerSha256 = containerSha256;
        Modules = Array.AsReadOnly(modules);
        SelectedModule = selected;
    }

    public static DjiFirmwareContainer Parse(ReadOnlySpan<byte> file, DjiFirmwareTarget expectedTarget)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        if (expectedTarget != DjiFirmwareTarget.ForProductType(expectedTarget.ProductType))
            throw new InvalidDataException("目标型号、模块地址与产品 ID 不一致，拒绝按猜测匹配固件。");
        if (file.Length < FixedHeaderLength + 2 || file.Length > MaximumFileLength)
            throw new InvalidDataException($"固件包长度须在 {FixedHeaderLength + 2} 字节至 {MaximumFileLength} 字节之间。");
        // Own the bytes before inspecting any metadata: the caller must not be
        // able to change a validated header between parsing and payload hashing.
        var snapshot = file.ToArray();
        file = snapshot;
        if (U32(file, 0) != 0x12345678)
            throw new InvalidDataException("文件不是已恢复的 DJI 固件容器；不能导入裸模块或其他格式。");
        if (U16(file, 4) != SupportedFormatVersion)
            throw new NotSupportedException("固件容器描述符版本未知，仅支持已恢复的版本 1。");

        var manufacturer = ReadIdentifier(file.Slice(0x0C, 16), "制造商");
        if (manufacturer != "DJI") throw new InvalidDataException("固件包制造商字段不匹配 DJI。");
        var model = ReadIdentifier(file.Slice(0x1C, 16), "型号");
        if (model is not ("dm300" or "dm202b"))
            throw new NotSupportedException($"尚未恢复型号 {model} 的描述符布局或模块校验，拒绝导入。");
        if (model != expectedTarget.Model)
            throw new InvalidDataException($"固件型号 {model} 与目标型号 {expectedTarget.Model} 不匹配。");
        if (file.Slice(0x36, 10).IndexOfAnyExcept((byte)0) >= 0)
            throw new NotSupportedException("固件包包含尚未解释的非零头部保留字段，拒绝导入。");

        var count = U16(file, 0x2C);
        if (count == 0 || count > MaximumModuleCount)
            throw new InvalidDataException("固件模块数量为零或超出解析上限。");
        var expectedHeaderLength = FixedHeaderLength + DescriptorLength * count + 2;
        var headerLength = U16(file, 6);
        if (headerLength != expectedHeaderLength || headerLength > file.Length)
            throw new InvalidDataException("包头长度与 52 字节描述符数量不一致，或描述符表已截断。");
        if (HeaderCrc16(file[..(headerLength - 2)]) != U16(file, headerLength - 2))
            throw new InvalidDataException("固件包头 CRC16 校验失败。");

        var parsed = new List<ParsedModule>(count);
        var identities = new HashSet<byte>();
        for (var index = 0; index < count; index++)
        {
            var descriptor = file.Slice(FixedHeaderLength + index * DescriptorLength, DescriptorLength);
            var moduleId = descriptor[0];
            var receiverType = (byte)(moduleId & 0x1F);
            var receiverSequence = (byte)(moduleId >> 5);
            var productType = ResolveProductType(model, receiverType, receiverSequence);
            if (!identities.Add(moduleId))
                throw new InvalidDataException($"固件含有重复的模块地址 0x{moduleId:X2}，无法唯一选择升级载荷。");
            if (descriptor[1] != 0 || U16(descriptor, 2) != 0)
                throw new NotSupportedException($"模块 {index} 使用未知编码、加密或非零保留字段，拒绝直接提取。");
            var offset = U32(descriptor, 8);
            var storedLength = U32(descriptor, 0x0C);
            var originalLength = U32(descriptor, 0x10);
            if (storedLength == 0 || originalLength == 0)
                throw new InvalidDataException($"模块 {index} 的载荷长度为零。");
            if (storedLength != originalLength)
                throw new NotSupportedException($"模块 {index} 的存储长度与原始长度不同，尚未恢复其转换规则。");
            // Use widened arithmetic before casting so UInt32 overflow cannot
            // turn an out-of-file or wraparound range into a valid slice.
            var end = (ulong)offset + storedLength;
            if (offset < headerLength || end > (ulong)file.Length)
                throw new InvalidDataException($"模块 {index} 的载荷范围越界或覆盖包头。");
            parsed.Add(new(index, productType, receiverType, receiverSequence, moduleId,
                U32(descriptor, 4), offset, storedLength, originalLength,
                descriptor.Slice(0x14, 16).ToArray(), descriptor.Slice(0x24, 16).ToArray()));
        }

        ulong previousEnd = headerLength;
        foreach (var module in parsed.OrderBy(module => module.Offset))
        {
            if ((ulong)module.Offset < previousEnd)
                throw new InvalidDataException("固件模块载荷范围相互重叠。");
            previousEnd = (ulong)module.Offset + module.StoredLength;
        }
        if (!parsed.Any(module => module.ProductType == expectedTarget.ProductType &&
                module.ReceiverType == expectedTarget.ReceiverType && module.ReceiverSequence == expectedTarget.ReceiverSequence))
            throw new InvalidDataException("固件包未包含与目标型号及模块地址同时匹配的载荷。");

        var modules = new DjiFirmwareModule[parsed.Count];
        foreach (var module in parsed)
        {
            var payload = snapshot.AsMemory(checked((int)module.Offset), checked((int)module.OriginalLength));
            var digest = MD5.HashData(payload.Span);
            if (!CryptographicOperations.FixedTimeEquals(digest, module.StoredMd5))
                throw new InvalidDataException($"模块 {module.Index} 的存储载荷 MD5 校验失败。");
            if (!CryptographicOperations.FixedTimeEquals(digest, module.OriginalMd5))
                throw new InvalidDataException($"模块 {module.Index} 的原始载荷 MD5 校验失败。");
            modules[module.Index] = new(module.Index, module.ProductType, module.ReceiverType,
                module.ReceiverSequence, module.ModuleId, FormatVersionBytes(module.Version), module.Version,
                module.Offset, module.StoredLength, module.OriginalLength,
                Convert.ToHexString(module.StoredMd5), Convert.ToHexString(module.OriginalMd5), payload);
        }
        var selected = modules.Single(module => module.ProductType == expectedTarget.ProductType &&
            module.ReceiverType == expectedTarget.ReceiverType && module.ReceiverSequence == expectedTarget.ReceiverSequence);
        return new(manufacturer, model, headerLength, U32(snapshot, 8), U32(snapshot, 0x2E),
            U32(snapshot, 0x32), snapshot.Length, Convert.ToHexString(SHA256.HashData(snapshot)), modules, selected);
    }

    private sealed record ParsedModule(int Index, int ProductType, byte ReceiverType, byte ReceiverSequence,
        byte ModuleId, uint Version, uint Offset, uint StoredLength, uint OriginalLength,
        byte[] StoredMd5, byte[] OriginalMd5);

    private static int ResolveProductType(string model, byte receiverType, byte receiverSequence)
    {
        if (receiverType != 0x1A || receiverSequence is not (1 or 2))
            throw new NotSupportedException($"尚未恢复型号 {model} 的模块 type={receiverType}/seq={receiverSequence}，拒绝导入。");
        return (model, receiverSequence) switch
        {
            ("dm300", 1) => 0xED,
            ("dm300", 2) => 0xEE,
            ("dm202b", 1) => 0x141,
            ("dm202b", 2) => 0x140,
            _ => throw new NotSupportedException("未知型号与模块组合。")
        };
    }

    private static string ReadIdentifier(ReadOnlySpan<byte> field, string name)
    {
        var end = field.IndexOf((byte)0);
        if (end <= 0 || field[(end + 1)..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException($"固件{name}字段未正确终止或含有未知尾部数据。");
        foreach (var value in field[..end])
            if (!(value is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'))
                throw new InvalidDataException($"固件{name}字段包含非预期字符。");
        return Encoding.ASCII.GetString(field[..end]);
    }

    private static string FormatVersionBytes(uint version) =>
        $"{version >> 24:D2}.{(version >> 16) & 0xFF:D2}.{(version >> 8) & 0xFF:D2}.{version & 0xFF:D2}";

    private static ushort HeaderCrc16(ReadOnlySpan<byte> bytes)
    {
        // libdjibase verify_crc16_ex_checksum @0xaf6388, table @0x80c4c8;
        // all 256 entries matched reflected polynomial 0x8408. SDK seed: 0x3692.
        ushort crc = 0x3692;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc >> 1) ^ ((crc & 1) != 0 ? 0x8408 : 0));
        }
        return crc;
    }

    private static ushort U16(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
}
