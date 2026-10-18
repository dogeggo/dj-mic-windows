using MicMini.Desktop.Protocol;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
    { checks++; return; }
    throw new Exception("未拒绝：" + name);
}
void Prefix(byte[] report, params byte[] expected)
{
    Check(report.Length == 64, "固定报告长度");
    Check(report.AsSpan(0, expected.Length).SequenceEqual(expected), "静态证据控制字节");
    Check(report.AsSpan(expected.Length).IndexOfAnyExcept((byte)0) < 0, "确定的零填充尾部");
}

// Golden prefixes are instruction constants, not hardware captures.
Prefix(MicFirmwareProtocol.EnterUpgrade(0), 0x55, 0x55, 0);
Prefix(MicFirmwareProtocol.EnterUpgrade(7), 0x55, 0x55, 7);
Prefix(MicFirmwareProtocol.Reboot(1), 0x55, 0x52, 1);
Prefix(MicFirmwareProtocol.GetVersion(0), 0x55, 0x5E, 0, 3);
Prefix(MicFirmwareProtocol.GetTxConnectedCount(), 0x55, 0x59);
Prefix(MicFirmwareProtocol.GetTxConnectedIndex(2), 0x55, 0x6A, 2, 1, 0);
Reject(() => MicFirmwareProtocol.GetTxConnectedIndex(0), "TX ordinal 从1开始");

foreach (var result in new byte[] { 0, 1, 2, 255 })
{
    var enter = MicFirmwareProtocol.ParseReport([0x55, 0x02, result]);
    var done = MicFirmwareProtocol.ParseReport([0x55, 0x01, result]);
    var flow = MicFirmwareProtocol.ParseReport([0x55, 0x60, result]);
    Check(enter.Kind == MicFirmwareReportKind.EnterAck && enter.Accepted == (result != 0), "进入应答非零肯定");
    Check(done.Kind == MicFirmwareReportKind.Result && done.Accepted == (result != 0), "最终结果非零肯定");
    Check(flow.Paused == (result != 0) && flow.Accepted is null, "非零暂停、零继续");
    Check(done.Paused is null && enter.Version is null, "非适用语义保持未知");
    Check(MicFirmwareProtocol.ParseReport([0x55, 0x52, result]).Accepted is null, "重启结果不猜成功含义");
}
var version = MicFirmwareProtocol.ParseReport([0x55, 0x5E, 0, 17, 3, 2]);
Check(version.Version == "02.03.17.00" && version.Accepted is null, "小端版本与独立语义");
Check(MicFirmwareProtocol.ParseReport([0x55, 0x59, 2]).ConnectedTxCount == 2, "TX数量");
Check(MicFirmwareProtocol.ParseReport([0x55, 0x6A, 7]).Module == 7, "TX module映射");
Check(MicFirmwareProtocol.ParseReport([0x55, 0x6A, 0]).Module is null, "拒绝module0作为TX");
Check(MicFirmwareProtocol.ParseReport([0x55, 0x6A, 255]).Module is null, "拒绝moduleFF作为TX");
Check(MicFirmwareProtocol.ParseReport([0x55, 0x55]).Kind == MicFirmwareReportKind.Unknown, "请求码55不是进入ACK码02");
Check(MicFirmwareProtocol.ParseReport([0x55, 0x7F, 8]).RawValue == 8, "未知报告保留原值");
foreach (var command in new byte[] { 1, 2, 0x52, 0x59, 0x5E, 0x60, 0x6A })
{
    var required = command == 0x5E ? 6 : 3;
    for (var n = 0; n < required; n++)
    {
        var report = new byte[required]; report[0] = 0x55; report[1] = command;
        var truncated = report[..n];
        Reject(() => MicFirmwareProtocol.ParseReport(truncated), "已知响应每个截断位置");
    }
}
Reject(() => MicFirmwareProtocol.ParseReport([0, 0x55, 2, 1]), "不猜测OS报告ID");
Reject(() => MicFirmwareProtocol.ParseReport(new byte[65]), "报告过长");
Reject(() => MicFirmwareProtocol.BuildDataReport([]), "空数据包");
Reject(() => MicFirmwareProtocol.BuildDataReport(new byte[64]), "超长数据包");
Reject(() => MicFirmwareProtocol.WriteDataReport(new byte[63], [1]), "目标报告长度不对");

// Offline stream simulation: variable final blocks, failed writes, and paused
// intervals must never skip, duplicate or accidentally transmit container bytes.
foreach (var length in new[] { 1, 62, 63, 64, 126, 127, 4097 })
{
    var module = Enumerable.Range(0, length).Select(i => (byte)(i * 37)).ToArray();
    var report = new byte[64];
    using var received = new MemoryStream();
    var offset = 0;
    var paused = MicFirmwareProtocol.ParseReport([0x55, 0x60, 1]).Paused!.Value;
    Check(paused && offset == 0, "开始等待流控");
    paused = MicFirmwareProtocol.ParseReport([0x55, 0x60, 0]).Paused!.Value;
    while (offset < module.Length && !paused)
    {
        var count = Math.Min(63, module.Length - offset);
        MicFirmwareProtocol.WriteDataReport(report, module.AsSpan(offset, count));
        var failedWrite = report.ToArray();
        // Failed write is not consumed and does not increment offset.
        MicFirmwareProtocol.WriteDataReport(report, module.AsSpan(offset, count));
        Check(report.SequenceEqual(failedWrite), "失败重试保持相同数据块");
        Check(report[0] == 0x41, "数据报告ID41");
        received.Write(report.AsSpan(1, count));
        offset += count;
    }
    Check(received.ToArray().SequenceEqual(module), "成功写入重组完整模块：" + length);
}
var retained = Enumerable.Repeat((byte)0xCC, 64).ToArray();
MicFirmwareProtocol.WriteDataReport(retained, [0xAB]);
Check(retained[0] == 0x41 && retained[1] == 0xAB && retained[2] == 0xCC, "复用buffer保留末包尾部");
Check(MicFirmwareProtocol.BuildDataReport([0xAB])[2] == 0, "新buffer确定零填充");
Console.WriteLine($"固件纯协议离线检查通过：{checks} checks；未连接或写入任何硬件。");
