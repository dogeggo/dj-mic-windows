using System.Buffers.Binary;

namespace MicMini.Desktop.Protocol;

public sealed record MiniTransmitterState(bool Muted, bool LowCut, bool AutoShutdown, bool Denoise,
    byte DenoiseLevelRaw, byte BatteryLevelRaw, byte ConnectionStateRaw, byte SampleLevelRaw, byte LedRaw, byte[] Raw);
public sealed record MiniReceiverState(bool CameraPowerSync, bool AutoShutdown, byte ChannelRaw,
    byte BatteryLevelRaw, byte GainStepRaw, bool AutomaticLimiter, bool SafetyTrack, byte[] Raw);
public sealed record MiniLegacyState(MiniTransmitterState Transmitter1, MiniTransmitterState Transmitter2, MiniReceiverState Receiver, byte[] UnknownTail);
public sealed record MicStatusRecord(byte Kind, uint Selector, byte[] Data);

/// <summary>Static APK state layouts. Raw enumerations deliberately remain raw until hardware validation.</summary>
public static class MicMiniStateCodec
{
    public static MiniLegacyState DecodeLegacy(ReadOnlySpan<byte> payload)
    {
        var body = Body(payload, 0);
        var offset = 0;
        var tx1 = Transmitter(TakeRecord(body, ref offset, 9, 8));
        var tx2 = Transmitter(TakeRecord(body, ref offset, 9, 8));
        var rxBytes = TakeRecord(body, ref offset, 7, 6);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(rxBytes);
        var rx = new MiniReceiverState((flags & 1) != 0, (flags & 2) != 0, (byte)((flags >> 2) & 3),
            (byte)((flags >> 5) & 7), (byte)((flags >> 8) & 7), (flags & 0x2000) != 0, (flags & 0x8000) != 0, rxBytes.ToArray());
        return new(tx1, tx2, rx, body[offset..].ToArray());
    }
    private static MiniTransmitterState Transmitter(ReadOnlySpan<byte> data)
    {
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data);
        return new((flags & 2) != 0, (flags & 4) != 0, (flags & 0x10) != 0, (flags & 0x100) != 0,
            (byte)((flags >> 5) & 3), (byte)((flags >> 10) & 7), (byte)((flags >> 13) & 7), data[2], (byte)((data[3] >> 6) & 3), data.ToArray());
    }
    private static ReadOnlySpan<byte> TakeRecord(ReadOnlySpan<byte> body, ref int offset, int fixedLength, int extensionLengthOffset)
    {
        if (body.Length - offset < fixedLength) throw new InvalidDataException("Mic Mini 状态记录被截断。");
        var length = fixedLength + body[offset + extensionLengthOffset];
        if (body.Length - offset < length) throw new InvalidDataException("Mic Mini 状态扩展被截断。");
        var record = body.Slice(offset, length); offset += length; return record;
    }
    private static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> payload, byte expectedVersion)
    {
        if (payload.Length < 3 || payload[0] != expectedVersion) throw new InvalidDataException("状态载荷版本无效。");
        var length = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        if (length != payload.Length - 3) throw new InvalidDataException("状态载荷声明长度与内容不符。");
        return payload[3..];
    }
    public static IReadOnlyList<MicStatusRecord> DecodeVersion3(ReadOnlySpan<byte> payload)
    {
        var body = Body(payload, 3); var records = new List<MicStatusRecord>();
        while (!body.IsEmpty)
        {
            if (body.Length < 6) throw new InvalidDataException("Mic 状态记录头被截断。");
            var length = body[5];
            if (body.Length < 6 + length) throw new InvalidDataException("Mic 状态记录内容被截断。");
            records.Add(new(body[0], BinaryPrimitives.ReadUInt32LittleEndian(body[1..]), body.Slice(6, length).ToArray()));
            body = body[(6 + length)..];
        }
        return records;
    }
}

public static class MicMiniCommands
{
    public static DumlFrame CapabilityQuery(ushort sequence) => new(2, 0x5a, sequence, 0x40, 0x5b, 0x12, new byte[6]);
    /// <summary>Builds an offline packet only. Transport/session and readback must be validated before sending.</summary>
    public static DumlFrame BooleanSetting(ushort sequence, string key, bool enabled)
    {
        var (selector, type) = key switch
        {
            "low_cut" => (3u, (ushort)0x03),
            "tx_mute" => (3u, (ushort)0x09),
            "denoise_enabled" => (3u, (ushort)0x0e),
            "power_button_denoise" => (3u, (ushort)0x0f),
            "tx_auto_power_off" => (3u, (ushort)0x10),
            "rx_auto_power_off" => (0u, (ushort)0x10),
            "limiter" => (0u, (ushort)0x1e),
            "camera_power_sync" => (0u, (ushort)0x20),
            "safety_track" => (0u, (ushort)0x21),
            _ => throw new ArgumentOutOfRangeException(nameof(key), "该参数未建立经过静态核验的布尔编码。")
        };
        return new(2, 0x5a, sequence, 0x40, 0x5b, 1, MicTlvCodec.SettingPayload(0, selector, 8, [new(type, [(byte)(enabled ? 1 : 0)])]));
    }
}
