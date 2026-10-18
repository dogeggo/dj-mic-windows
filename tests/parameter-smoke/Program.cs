using System.Buffers.Binary;
using System.Text.Json;
using MicMini.Desktop.Services;
using MicMini.Desktop.Protocol;

var definitions = new Dictionary<string, (byte Kind, uint Selector, ushort Type, Func<byte[], int> Read, Func<int, int> Next)>
{
    ["lowcut"] = (2, 1, 3, d => (int)((BinaryPrimitives.ReadUInt32LittleEndian(d) >> 29) & 1), v => 1-v),
    ["denoise"] = (2, 1, 0x0e, d => (d[1] & 1), v => 1-v),
    ["denoise-button"] = (2, 1, 0x0f, d => (d[0] >> 7) & 1, v => 1-v),
    ["tx-auto-off"] = (2, 1, 0x10, d => (d[0] >> 4) & 1, v => 1-v),
    ["rx-auto-off"] = (3, 0, 0x10, d => d[1] & 1, v => 1-v),
    ["limiter"] = (3, 0, 0x1e, d => (d[28] >> 4) & 1, v => 1-v),
    ["camera-sync"] = (3, 0, 0x20, d => (d[0] >> 7) & 1, v => 1-v),
    ["tx-gain"] = (2, 1, 0x39, d => (sbyte)d[7], v => v == 0 ? 1 : 0),
    ["rx-gain"] = (3, 0, 0x39, d => (sbyte)d[2], v => v == 0 ? 6 : 0),
    ["channel"] = (3, 0, 8, d => (d[1] >> 1) & 7, v => v == 1 ? 2 : 1),
    ["noise-level"] = (2, 1, 0x1d, d => (d[0] >> 5) & 3, v => v == 0 ? 1 : 0),
    ["led"] = (2, 1, 0x0a, d => d[0] & 3, v => v == 0 ? 2 : 0),
    ["led-observe"] = (2, 1, 0x0a, d => d[0] & 3, v => v == 0 ? 2 : 0),
    ["channel-zero"] = (3, 0, 8, d => (d[1] >> 1) & 7, v => v == 0 ? 2 : 0),
    ["restore-channel-2"] = (3, 0, 8, d => (d[1] >> 1) & 7, v => 2),
    ["safety"] = (3, 0, 0x21, d => (d[28] >> 6) & 1, v => 1-v)
};
if (args.Length != 1 || !definitions.TryGetValue(args[0], out var definition)) { Console.WriteLine("Choose: " + string.Join(", ", definitions.Keys) + ". Temporarily changes then restores the exact original value."); return 2; }
var devices = WinUsbTransport.Enumerate();
if (devices.Count != 1) throw new IOException("需要唯一的MI06接收端。");
await using var transport = new WinUsbTransport();
var decoder = new DumlStreamDecoder();
var gate = new object();
var states = new Dictionary<(byte, uint), (byte[] Data, long Revision)>();
TaskCompletionSource<DumlFrame>? ack = null;
ushort expectedSequence = 0;
long revision = 0;
transport.DataReceived += data =>
{
    foreach (var frame in decoder.Feed(data))
    {
        lock (gate)
        {
            if (frame.Sender == 0x5a && frame.Receiver == 2 && frame.CommandSet == 0x5b && frame.Command == 1 && frame.Sequence == expectedSequence && (frame.Flags & 0x80) != 0)
                ack?.TrySetResult(frame);
            if (frame.Sender == 0x5a && frame.CommandSet == 0x5b && frame.Command == 3 && frame.Payload.Length > 0 && frame.Payload[0] == 3)
                foreach (var record in MicMiniStateCodec.DecodeVersion3(frame.Payload))
                    states[(record.Kind, record.Selector)] = (record.Data, ++revision);
        }
    }
};
transport.Disconnected += Console.WriteLine;
transport.Open(devices[0]);
async Task Wait(Func<bool> predicate)
{
    var until = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < until) { lock (gate) if (predicate()) return; await Task.Delay(30); }
    throw new TimeoutException("等待实机状态回读超时。");
}
var key = (definition.Kind, definition.Selector);
await Wait(() => states.ContainsKey(key) && states.ContainsKey((3, 0)));
int savedChannel, savedSafety;
lock (gate) { savedChannel = (states[(3, 0)].Data[1] >> 1) & 7; savedSafety = (states[(3, 0)].Data[28] >> 6) & 1; }
int original;
lock (gate) { original = definition.Read(states[key].Data); Console.WriteLine("ORIGINAL " + args[0] + " " + Convert.ToHexString(states[key].Data) + " VALUE=" + original); }
using var audio = new AudioCapture();
if (args[0] == "channel-zero") audio.StartMonitor(AudioCapture.EnumerateDevices().Single(d => d.Name.Contains("Wireless Mic Rx")).Id);
async Task Measure()
{
    if (!audio.IsMonitoring) return;
    await Task.Delay(300); double left = 0, right = 0;
    for (var i = 0; i < 20; i++) { await Task.Delay(50); left += audio.PeakLeft; right += audio.PeakRight; }
    Console.WriteLine($"AUDIO PEAKSUM L={left} R={right} R/L={(left > 0 ? right / left : 0)}");
}
await Measure();
async Task Set(int value, ushort sequence)
{
    TaskCompletionSource<DumlFrame> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long before;
    lock (gate) { before = revision; expectedSequence = sequence; ack = completion; }
    var payload = MicTlvCodec.SettingPayload(2, definition.Selector, 32, [new(definition.Type, [unchecked((byte)value)])]);
    var packet = DumlCodec.Encode(new(2, 0x5a, sequence, 0x40, 0x5b, 1, payload));
    Console.WriteLine("SET " + value + " TX " + Convert.ToHexString(packet));
    await transport.SendAsync(packet);
    var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Console.WriteLine("ACK " + JsonSerializer.Serialize(response));
    if (response.Payload.Length == 0 || response.Payload[0] != 0) throw new IOException("设备拒绝设置: " + Convert.ToHexString(response.Payload));
    await Wait(() => states.TryGetValue(key, out var result) && result.Revision > before && definition.Read(result.Data) == value);
    lock (gate) Console.WriteLine("READBACK " + Convert.ToHexString(states[key].Data));
    await Measure();
}
if (args[0] == "restore-channel-2") { await Set(2, 103); Console.WriteLine("RESTORED channel=2, the original value recorded before the safety-track test."); return 0; }
try { await Set(definition.Next(original), 101); if (args[0] == "led-observe") { Console.WriteLine("OBSERVE: alternative LED state active for 45 seconds, then automatic restore."); await Task.Delay(45000); } }
finally
{
    await Set(original, 102);
    // Safety-track can implicitly change channel mode. Restore collateral RX
    // settings as well as the selected field, using the captured original state.
    int currentSafety, currentChannel;
    lock (gate) { currentSafety = (states[(3, 0)].Data[28] >> 6) & 1; currentChannel = (states[(3, 0)].Data[1] >> 1) & 7; }
    if (currentSafety != savedSafety)
    {
        definition = (3, 0, 0x21, d => (d[28] >> 6) & 1, v => v); key = (3, 0);
        await Set(savedSafety, 103);
    }
    lock (gate) currentChannel = (states[(3, 0)].Data[1] >> 1) & 7;
    if (currentChannel != savedChannel)
    {
        definition = (3, 0, 8, d => (d[1] >> 1) & 7, v => v); key = (3, 0);
        await Set(savedChannel, 104);
    }
}
Console.WriteLine("PASS: " + args[0] + " changed, acknowledged, read back, and restored.");
return 0;
