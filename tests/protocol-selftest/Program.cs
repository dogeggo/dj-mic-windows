using MicMini.Desktop.Protocol;

var checks = 0;
void Check(bool passed, string name) { if (!passed) throw new Exception(name); checks++; }
void Reject(Action action, string name) { try { action(); } catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) { checks++; return; } throw new Exception("未拒绝 " + name); }
Check(MicHidProtocol.ReadVersion(Convert.FromHexString("555E00110302")) == "02.03.17.00", "真实 RX 回复的版本字节序");
Check(MicHidProtocol.ReadTransmitterCount(Convert.FromHexString("555901")) == 1, "真实 TX 数量回复");
Reject(() => MicHidProtocol.ReadVersion([0x55, 0x5e, 0]), "HID 截断");
Reject(() => MicHidProtocol.ReadTransmitterIndex([0x55, 0x6a, 255]), "无效 TX 索引");
var unknown = MicTlvCodec.Decode(Convert.FromHexString("FFFF0200010E0001000E000101"));
Check(unknown.Count == 3 && unknown[0].Type == 65535 && unknown[1].Value[0] == 0 && unknown[2].Value[0] == 1, "未知与重复 TLV 保留");
Reject(() => MicTlvCodec.Decode([0x0e, 0]), "TLV 头截断");
Reject(() => MicTlvCodec.Decode([0x0e, 0, 2, 1]), "TLV 值截断");
Reject(() => MicTlvCodec.SettingPayload(0, 256, 8, []), "selector 不截断");
Check(Convert.ToHexString(MicTlvCodec.SettingPayload(2, 0x04030201, 32, [new(0x0e, [1])])) == "02010203040E000101", "静态恢复的32位设置布局");
var frame = new DumlFrame(2, 0x5a, 1, 0x40, 0x5b, 0x12, new byte[6]);
var encoded = DumlCodec.Encode(frame);
Check(Convert.ToHexString(encoded) == "55130403025A0100405B1200000000000085BB", "APK原始CRC查表计算的RX黄金帧");
Check(Convert.ToHexString(DumlCodec.Encode(frame with { Receiver = 0x3a, Sequence = 2 })) == "55130403023A0200405B12000000000000E639", "APK原始CRC查表计算的TX黄金帧");
var decoded = DumlCodec.Decode(encoded);
Check(decoded.Sender == 2 && decoded.Receiver == 0x5a && decoded.Sequence == 1 && decoded.CommandSet == 0x5b && decoded.Payload.Length == 6, "帧编码解码");
for (var split = 0; split <= encoded.Length; split++)
{
    var stream = new DumlStreamDecoder();
    var output = stream.Feed(encoded.AsSpan(0, split)).Concat(stream.Feed(encoded.AsSpan(split))).ToArray();
    Check(output.Length == 1 && stream.BufferedBytes == 0, "USB任意位置分片 " + split);
}
var noisy = new DumlStreamDecoder();
Check(noisy.Feed(new byte[] { 0, 0x55, 0x55, 0x55, 0x55 }.Concat(encoded).Concat(encoded).ToArray()).Count == 2, "噪声后重新同步与合并帧");
for (var i = 0; i < encoded.Length; i++)
{
    var corrupt = encoded.ToArray(); corrupt[i] ^= 1;
    Reject(() => DumlCodec.Decode(corrupt), "单字节损坏 " + i);
    var decoder = new DumlStreamDecoder();
    var recovered = decoder.Feed(corrupt.Concat(encoded).ToArray());
    Check(recovered.Count == 1, "损坏后恢复 " + i);
}
var random = new Random(1234); var junk = new byte[1_000_000]; random.NextBytes(junk);
var fuzz = new DumlStreamDecoder(); fuzz.Feed(junk);
Check(fuzz.BufferedBytes <= DumlCodec.MaximumFrameLength, "百万字节噪声后内存有界");
var statePayload = new byte[28]; statePayload[1] = 25;
statePayload[3] = 0x34; statePayload[4] = 0x2d; statePayload[5] = 42; statePayload[6] = 0x80;
statePayload[21] = 0x4b; statePayload[22] = 0xa3;
var state = MicMiniStateCodec.DecodeLegacy(statePayload);
Check(state.Transmitter1.LowCut && state.Transmitter1.Denoise && state.Transmitter1.AutoShutdown && state.Transmitter1.ConnectionStateRaw == 1, "TX 位字段解析");
Check(state.Transmitter1.BatteryLevelRaw == 3 && state.Transmitter1.LedRaw == 2 && state.Transmitter1.SampleLevelRaw == 42, "TX未知单位保持原始值");
Check(state.Receiver.CameraPowerSync && state.Receiver.AutoShutdown && state.Receiver.AutomaticLimiter && state.Receiver.SafetyTrack && state.Receiver.ChannelRaw == 2 && state.Receiver.GainStepRaw == 3, "RX 位字段解析");
for (var i = 0; i < statePayload.Length; i++)
{
    var truncated = statePayload[..i];
    Reject(() => MicMiniStateCodec.DecodeLegacy(truncated), "状态截断 " + i);
}
var badExtension = statePayload.ToArray(); badExtension[11] = 255;
Reject(() => MicMiniStateCodec.DecodeLegacy(badExtension), "状态扩展越界");
var version3 = MicMiniStateCodec.DecodeVersion3(Convert.FromHexString("030800FF0403020102AABB"));
Check(version3.Count == 1 && version3[0].Kind == 255 && version3[0].Selector == 0x01020304 && Convert.ToHexString(version3[0].Data) == "AABB", "新版本未知记录无损保留");
Check(Convert.ToHexString(MicMiniCommands.BooleanSetting(1, "low_cut", true).Payload) == "000303000101", "APK Mini TX低切载荷");
Check(Convert.ToHexString(MicMiniCommands.BooleanSetting(2, "safety_track", false).Payload) == "000021000100", "APK Mini RX安全轨载荷");
Console.WriteLine($"PASS: {checks} protocol checks. Candidate query (offline only): {Convert.ToHexString(encoded)}");
