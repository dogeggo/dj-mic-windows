using System.Text.Json;
using MicMini.Desktop.Services;

await using var session = new MicBulkSession();
session.Log += Console.WriteLine;
await session.ConnectAndQueryCapabilitiesAsync();
Console.WriteLine("IDENTITIES " + JsonSerializer.Serialize(session.Identities));
Console.WriteLine("RX " + JsonSerializer.Serialize(session.Receiver));
Console.WriteLine("TX " + JsonSerializer.Serialize(session.Transmitters));
if (session.Receiver is null || !session.Transmitters.TryGetValue(1, out var tx)) throw new Exception("缺少实机状态。");
var original = tx.LowCut;
try
{
    await session.SetValueAsync(1, 3, (byte)(original ? 0 : 1), () => session.Transmitters.TryGetValue(1, out var now) && now.LowCut != original);
    Console.WriteLine("SESSION CHANGE VERIFIED");
}
finally
{
    await session.SetValueAsync(1, 3, (byte)(original ? 1 : 0), () => session.Transmitters.TryGetValue(1, out var now) && now.LowCut == original);
    Console.WriteLine("SESSION RESTORE VERIFIED");
}
Console.WriteLine("PASS: production bulk session connects, reads capabilities, validates ACK and readback, restores original setting.");
