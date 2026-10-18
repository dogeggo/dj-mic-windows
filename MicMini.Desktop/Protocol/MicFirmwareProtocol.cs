namespace MicMini.Desktop.Protocol;

public enum MicFirmwareReportKind
{
    Unknown,
    EnterAck,
    Result,
    FlowControl,
    Version,
    RebootAck,
    ConnectedTxCount,
    ConnectedTxIndex
}

/// <summary>
/// A decoded device report. Accepted applies only to EnterAck and Result.
/// A positive Result still requires reconnection and version verification.
/// </summary>
public sealed record MicFirmwareReport(
    MicFirmwareReportKind Kind,
    byte Command,
    byte? RawValue = null,
    bool? Accepted = null,
    bool? Paused = null,
    string? Version = null,
    byte? Module = null,
    byte? ConnectedTxCount = null);

/// <summary>
/// Pure HID byte codec recovered from UpgradeUsbHidService in the Android SDK.
/// This class has no transport, package validation, automatic retry, or hardware
/// side effects. Callers must validate the official module before sending data.
/// See docs/firmware-protocol-findings.md for evidence and remaining boundaries.
/// </summary>
public static class MicFirmwareProtocol
{
    public const int ReportLength = 64;
    public const int DataBytesPerReport = 63;
    public const int NativeWriteTimeoutMilliseconds = 200;
    public const int NativeDefaultWriteDelayMilliseconds = 2;

    public static byte[] EnterUpgrade(byte module) => Control(0x55, module);

    public static byte[] Reboot(byte module) => Control(0x52, module);

    public static byte[] GetVersion(byte module)
    {
        var report = Control(0x5E, module);
        report[3] = 0x03;
        return report;
    }

    public static byte[] GetTxConnectedCount() => Control(0x59);

    /// <summary>The native ordinal starts at one; the reply supplies the module.</summary>
    public static byte[] GetTxConnectedIndex(byte ordinal)
    {
        if (ordinal == 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        var report = Control(0x6A, ordinal);
        report[3] = 0x01;
        report[4] = 0x00;
        return report;
    }

    /// <summary>
    /// Creates a 64-byte report with an explicitly zero-filled unused tail.
    /// The native SDK reuses its report buffer; use WriteDataReport to retain
    /// that behavior. There is no per-block index, size or CRC in this report.
    /// </summary>
    public static byte[] BuildDataReport(ReadOnlySpan<byte> chunk)
    {
        var report = new byte[ReportLength];
        WriteDataReport(report, chunk);
        return report;
    }

    /// <summary>
    /// Writes 0x41 followed by 1–63 raw module bytes into a reusable report.
    /// Bytes beyond the supplied chunk remain untouched, as in the SDK loop.
    /// Advance the firmware offset only after a successful transport write.
    /// </summary>
    public static void WriteDataReport(Span<byte> report, ReadOnlySpan<byte> chunk)
    {
        if (report.Length != ReportLength)
            throw new ArgumentException("HID 升级报告必须为 64 字节。", nameof(report));
        if (chunk.IsEmpty || chunk.Length > DataBytesPerReport)
            throw new ArgumentOutOfRangeException(nameof(chunk), "每个数据报告必须包含 1–63 字节模块数据。");
        // Copy before writing the prefix so an overlapping caller span remains valid.
        chunk.CopyTo(report[1..]);
        report[0] = 0x41;
    }

    /// <summary>
    /// Parses the application report beginning with 0x55. An OS-level report-ID
    /// prefix, if present, must be handled by the verified transport. It is never
    /// guessed or stripped here. Known truncated responses are rejected.
    /// </summary>
    public static MicFirmwareReport ParseReport(ReadOnlySpan<byte> report)
    {
        if (report.Length < 2)
            throw new InvalidDataException("HID 升级报告头被截断。");
        if (report.Length > ReportLength)
            throw new InvalidDataException("HID 升级报告超过 64 字节；请核对传输层报告 ID。");
        if (report[0] != 0x55)
            throw new InvalidDataException("HID 升级报告前缀不是 0x55。");

        var command = report[1];
        var kind = command switch
        {
            0x02 => MicFirmwareReportKind.EnterAck,
            0x01 => MicFirmwareReportKind.Result,
            0x60 => MicFirmwareReportKind.FlowControl,
            0x5E => MicFirmwareReportKind.Version,
            0x52 => MicFirmwareReportKind.RebootAck,
            0x59 => MicFirmwareReportKind.ConnectedTxCount,
            0x6A => MicFirmwareReportKind.ConnectedTxIndex,
            _ => MicFirmwareReportKind.Unknown
        };
        if (kind == MicFirmwareReportKind.Unknown)
            return new(kind, command, report.Length > 2 ? report[2] : null);

        var requiredLength = kind == MicFirmwareReportKind.Version ? 6 : 3;
        if (report.Length < requiredLength)
            throw new InvalidDataException($"HID 升级响应 0x{command:X2} 被截断，至少需要 {requiredLength} 字节。");

        var value = report[2];
        return kind switch
        {
            MicFirmwareReportKind.EnterAck or MicFirmwareReportKind.Result =>
                new(kind, command, value, Accepted: value != 0),
            MicFirmwareReportKind.FlowControl =>
                new(kind, command, value, Paused: value != 0),
            MicFirmwareReportKind.Version =>
                new(kind, command, value,
                    Version: $"{report[5]:D2}.{report[4]:D2}.{report[3]:D2}.{report[2]:D2}"),
            MicFirmwareReportKind.ConnectedTxCount =>
                new(kind, command, value, ConnectedTxCount: value),
            MicFirmwareReportKind.ConnectedTxIndex =>
                new(kind, command, value, Module: value is 0 or 0xFF ? null : value),
            // The native reboot parser does not branch on this byte, so its
            // success semantics must remain unknown rather than being invented.
            _ => new(kind, command, value)
        };
    }

    private static byte[] Control(byte command, byte argument = 0)
    {
        var report = new byte[ReportLength];
        report[0] = 0x55;
        report[1] = command;
        report[2] = argument;
        return report;
    }
}
