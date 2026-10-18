namespace MicMini.Desktop.Protocol;

/// <summary>APK UpgradeUsbHidService read-only requests, confirmed on the attached receiver.</summary>
public static class MicHidProtocol
{
    public static byte[] VersionRequest(byte module) => Report(0x5e, module, 0x03);
    public static byte[] TransmitterCountRequest() => Report(0x59);
    public static byte[] TransmitterIndexRequest(byte ordinal)
    {
        if (ordinal == 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return Report(0x6a, ordinal, 1, 0);
    }
    private static byte[] Report(params byte[] content)
    {
        var result = new byte[64]; result[0] = 0x55; content.CopyTo(result, 1); return result;
    }
    public static string ReadVersion(ReadOnlySpan<byte> reply)
    {
        Validate(reply, 0x5e, 6);
        return $"{reply[5]:D2}.{reply[4]:D2}.{reply[3]:D2}.{reply[2]:D2}";
    }
    public static byte ReadTransmitterCount(ReadOnlySpan<byte> reply) { Validate(reply, 0x59, 3); return reply[2]; }
    public static byte ReadTransmitterIndex(ReadOnlySpan<byte> reply)
    {
        Validate(reply, 0x6a, 3);
        if (reply[2] is 0 or 255) throw new InvalidDataException("设备返回无效发射端索引。");
        return reply[2];
    }
    private static void Validate(ReadOnlySpan<byte> reply, byte command, int minimum)
    {
        if (reply.Length < minimum || reply[0] != 0x55 || reply[1] != command)
            throw new InvalidDataException("设备返回的 HID 报告无效。");
    }
}
