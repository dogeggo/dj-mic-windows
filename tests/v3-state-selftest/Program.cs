using System.Buffers.Binary;
using System.Text.Json;
using MicMini.Desktop.Protocol;

// Offline replay only. No transport, USB, audio, or device calls are referenced.
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}
byte[] Payload(byte kind, uint selector, byte[] body)
{
    var result = new byte[9 + body.Length];
    result[0] = 3;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(1), (ushort)(6 + body.Length));
    result[3] = kind;
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), selector);
    result[8] = (byte)body.Length;
    body.CopyTo(result, 9);
    return result;
}

var path = args.Length > 0 ? args[0] : "analysis/evidence/mic-mini-bulk-readonly.log";
var all = new List<MicV3Record>();
var frames = 0;
foreach (var line in File.ReadLines(path))
{
    if (!line.StartsWith("FRAME ")) continue;
    using var doc = JsonDocument.Parse(line[6..]);
    var frame = doc.RootElement;
    if (frame.GetProperty("CommandSet").GetInt32() != 0x5b || frame.GetProperty("Command").GetInt32() != 3) continue;
    var payload = frame.GetProperty("Payload").GetBytesFromBase64();
    if (payload.Length == 0 || payload[0] != 3) continue;
    all.AddRange(MicV3State.Decode(payload));
    frames++;
}
Check(frames > 0, "saved live capture contains v3 frames");
Check(Enumerable.Range(1, 6).All(kind => all.Any(record => record.Kind == kind)), "live kinds 1 through 6 decoded");
Check(all.Any(record => record.Selector == 0 && record.Identity?.SerialNumber == "B3NXP8D025HKVH"), "RX serial");
Check(all.Any(record => record.Selector == 1 && record.Identity?.SerialNumber == "8PDWP8K01515XA"), "TX serial");
Check(all.Where(record => record.Kind == 1).All(record => record.Identity?.FirmwareVersion == "02.03.17.00"), "firmware byte order and decimal formatting");
Check(all.Any(record => record.Selector == 0 && record.Identity?.DeviceName == "f26f26"), "kind 4 device name");
Check(all.Any(record => record.Selector == 0 && record.Identity?.ProductName == "DJI Mic Mini 2"), "kind 6 product name");
Check(all.Any(record => record.Selector == 1 && record.Identity?.ProductName == "DJI Mic Mini"), "TX product name");
var receivers = all.Where(record => record.Rx is not null).Select(record => record.Rx!).ToArray();
Check(receivers.All(rx => rx.AutoShutdown && rx.Denoise && rx.ChannelRaw == 2 && rx.GainRaw == 0), "captured RX flags");
Check(receivers.All(rx => rx.AutomaticLimiter == true && rx.SafetyTrack == false && rx.UsbPlayback == true), "RX data28 flags");
Check(receivers.Any(rx => rx.ConnectedTransmittersMask == 0) && receivers.Any(rx => rx.ConnectedTransmittersMask == 1), "actual connected-mask transition");
var tx = all.First(record => record.Tx is not null).Tx!;
Check(tx.AutoShutdown && tx.Denoise && !tx.Muted && !tx.LowCut && !tx.Charging, "captured TX flags");
Check(tx.BatteryLevelRaw == 4 && tx.DenoiseLevelRaw == 0 && tx.LedRaw == 0 && tx.GainRaw == 0, "TX raw enumerations");
Check(all.Where(record => record.Kind == 5).Select(record => record.LevelMeterRaw).Distinct().Count() == 25, "25 distinct captured meter levels");

var txBytes = Convert.FromHexString("1831040100780000000000000000000000000000000000000000");
txBytes[3] |= 0x20;
txBytes[7] = 0xf4;
var changed = MicV3State.Decode(Payload(2, 1, txBytes)).Single();
Check(changed.Tx!.LowCut && changed.Tx.GainRaw == -12, "TX bit29 and signed gain");
Check(MicV3State.Decode(Payload(3, 0, [0, 0, 0])).Single().Rx!.AutomaticLimiter is null, "absent RX extensions remain unknown");
Check(MicV3State.Decode(Payload(5, 1, [0x78, 0x56, 0x34, 0x12, 0xff])).Single().LevelMeterRaw == 0x12345678, "meter copies at most four little-endian bytes");
Check(MicV3State.Decode(Payload(5, 1, [])).Single().LevelMeterRaw == 0, "zero-length meter matches native zero-fill behavior");
var unknown = MicV3State.Decode(Payload(99, 0x10203040, [1, 2, 3])).Single();
Check(unknown.Kind == 99 && unknown.Selector == 0x10203040 && unknown.RawData.SequenceEqual(new byte[] { 1, 2, 3 }) && unknown.Rx is null, "unknown record preserved");
foreach (var invalid in new[] { new byte[] { 3, 1, 0 }, Payload(1, 0, new byte[17]), Payload(2, 1, new byte[7]), Payload(3, 0, new byte[2]) })
{
    try { MicV3State.Decode(invalid); throw new Exception("Invalid record accepted"); }
    catch (InvalidDataException) { checks++; }
}
Console.WriteLine($"PASS: {checks} checks; {frames} saved v3 frames / {all.Count} records replayed; no device access.");
