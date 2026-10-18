using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MicMini.Desktop.Protocol;

public sealed record MicV3TransmitterState(
    uint RawFlags, bool Muted, bool LowCut, bool AutoShutdown, bool Denoise,
    byte DenoiseLevelRaw, bool DenoiseButton, bool Charging, byte BatteryLevelRaw,
    byte LedRaw, sbyte GainRaw, byte RssiLevelRaw);

public sealed record MicV3ReceiverState(
    ushort RawFlags, bool CameraPowerSync, bool AutoShutdown, byte ChannelRaw,
    bool Denoise, byte DenoiseLevelRaw, bool Charging, byte BatteryLevelRaw, sbyte GainRaw,
    uint? PairingTransmittersMask, uint? ConnectedTransmittersMask,
    bool? AutomaticLimiter, bool? SafetyTrack, bool? UsbPlayback);

/// <summary>
/// Identity records arrive independently: kind 1 supplies firmware/SN, kind 4 the
/// device name, and kind 6 the product name. Merge non-null fields per selector.
/// </summary>
public sealed record MicV3IdentityState(
    string? FirmwareVersion = null, string? SerialNumber = null,
    string? DeviceName = null, string? ProductName = null);

public sealed record MicV3Record(
    byte Kind, uint Selector, byte[] RawData,
    MicV3ReceiverState? Rx = null, MicV3TransmitterState? Tx = null,
    MicV3IdentityState? Identity = null, int? LevelMeterRaw = null);

/// <summary>
/// 5B/03 version-3 state decoder. Bit positions are recovered from APK ARM64
/// parsers and corroborated by the saved Mic Mini bulk capture. All offsets below
/// are relative to record DATA, excluding its 6-byte kind/selector/length header.
/// Battery, channel, denoise, LED and RSSI remain raw enum values; meter/gain units
/// are not inferred. Unknown kinds and unparsed bytes are preserved in RawData.
/// Evidence: analysis/evidence/native/protocol-runtime-v3-fields.json,
/// protocol-runtime-mic-status.json, protocol-runtime-v3-identity.json.
/// </summary>
public static class MicV3State
{
    public static IReadOnlyList<MicV3Record> Decode(ReadOnlySpan<byte> payload)
    {
        var records = MicMiniStateCodec.DecodeVersion3(payload);
        var result = new List<MicV3Record>(records.Count);
        foreach (var record in records)
        {
            var data = record.Data.AsSpan();
            result.Add(record.Kind switch
            {
                1 => new(record.Kind, record.Selector, record.Data, Identity: DecodeIdentity(data)),
                2 => new(record.Kind, record.Selector, record.Data, Tx: DecodeTransmitter(data)),
                3 => new(record.Kind, record.Selector, record.Data, Rx: DecodeReceiver(data)),
                4 => new(record.Kind, record.Selector, record.Data,
                    Identity: new(DeviceName: DecodeText(data))),
                5 => new(record.Kind, record.Selector, record.Data, LevelMeterRaw: DecodeLevelMeter(data)),
                6 => new(record.Kind, record.Selector, record.Data,
                    Identity: new(ProductName: DecodeText(data))),
                _ => new(record.Kind, record.Selector, record.Data)
            });
        }
        return result;
    }

    private static MicV3TransmitterState DecodeTransmitter(ReadOnlySpan<byte> data)
    {
        RequireLength(data, 8, 2);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
        return new(flags,
            Muted: Bit(flags, 2), LowCut: Bit(flags, 29), AutoShutdown: Bit(flags, 4),
            Denoise: Bit(flags, 8), DenoiseLevelRaw: (byte)((flags >> 5) & 3),
            DenoiseButton: Bit(flags, 7), Charging: Bit(flags, 9), BatteryLevelRaw: (byte)((flags >> 10) & 7),
            LedRaw: (byte)(flags & 3), GainRaw: unchecked((sbyte)data[7]), RssiLevelRaw: (byte)(data[6] >> 4));
    }

    private static MicV3ReceiverState DecodeReceiver(ReadOnlySpan<byte> data)
    {
        RequireLength(data, 3, 3);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data);
        return new(flags,
            CameraPowerSync: Bit(flags, 7), AutoShutdown: Bit(flags, 8), ChannelRaw: (byte)((flags >> 9) & 7),
            Denoise: Bit(flags, 5), DenoiseLevelRaw: (byte)((flags >> 3) & 3),
            Charging: Bit(flags, 12), BatteryLevelRaw: (byte)((flags >> 13) & 7), GainRaw: unchecked((sbyte)data[2]),
            PairingTransmittersMask: data.Length >= 24 ? BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) : null,
            ConnectedTransmittersMask: data.Length >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(data[24..]) : null,
            AutomaticLimiter: data.Length >= 29 ? Bit(data[28], 4) : null,
            SafetyTrack: data.Length >= 29 ? Bit(data[28], 6) : null,
            UsbPlayback: data.Length >= 29 ? Bit(data[28], 1) : null);
    }

    private static MicV3IdentityState DecodeIdentity(ReadOnlySpan<byte> data)
    {
        RequireLength(data, 18, 1);
        return new(FirmwareVersion: $"{data[3]:D2}.{data[2]:D2}.{data[1]:D2}.{data[0]:D2}",
            SerialNumber: Encoding.ASCII.GetString(data.Slice(4, 14)).TrimEnd('\0'));
    }

    private static int DecodeLevelMeter(ReadOnlySpan<byte> data)
    {
        // KeyMicLevelMeterPush zeroes an int then copies min(length, 4) bytes.
        Span<byte> value = stackalloc byte[4];
        value.Clear();
        data[..Math.Min(4, data.Length)].CopyTo(value);
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private static string DecodeText(ReadOnlySpan<byte> data) => Encoding.UTF8.GetString(data).TrimEnd('\0');
    private static bool Bit(uint flags, int bit) => (flags & (1u << bit)) != 0;
    private static void RequireLength(ReadOnlySpan<byte> data, int minimum, byte kind)
    {
        if (data.Length < minimum)
            throw new InvalidDataException($"Mic v3 kind {kind} 状态数据被截断：需要至少 {minimum} 字节，实际 {data.Length} 字节。");
    }
}
