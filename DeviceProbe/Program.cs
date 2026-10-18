using System.Text.Json;
using MicMini.Desktop.Services;
using MicMini.Desktop.Protocol;

try
{
if (args.Contains("--winusb"))
{
    Console.WriteLine(JsonSerializer.Serialize(WinUsbTransport.Enumerate(), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.Contains("--bulk-readonly"))
{
    var bulkDevices = WinUsbTransport.Enumerate();
    Console.WriteLine(JsonSerializer.Serialize(bulkDevices));
    if (bulkDevices.Count != 1) throw new IOException("需要恰好一个已安装WinUSB的MI_06接口。");
    await using var bulk = new WinUsbTransport();
    var decoder = new DumlStreamDecoder();
    bulk.Disconnected += Console.WriteLine;
    bulk.DataReceived += data =>
    {
        Console.WriteLine("BULK RX " + Convert.ToHexString(data));
        foreach (var frame in decoder.Feed(data))
        {
            Console.WriteLine("FRAME " + JsonSerializer.Serialize(frame));
            if (frame.CommandSet == 0x5b && frame.Command == 3 && frame.Payload.Length > 0 && frame.Payload[0] == 0)
            {
                try { Console.WriteLine("STATE " + JsonSerializer.Serialize(MicMiniStateCodec.DecodeLegacy(frame.Payload))); }
                catch (InvalidDataException ex) { Console.WriteLine("STATE PARSE " + ex.Message); }
            }
        }
    };
    bulk.Open(bulkDevices[0]);
    await Task.Delay(1500);
    if (args.Contains("--capability"))
    {
        var capability = DumlCodec.Encode(new(2, 0x5a, 1, 0x40, 0x5b, 0x12, new byte[6]));
        Console.WriteLine("BULK TX " + Convert.ToHexString(capability));
        await bulk.SendAsync(capability);
    }
    await Task.Delay(5000);
    return 0;
}

var devices = HidTransport.Enumerate();
Console.WriteLine(JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }));
var device = devices.FirstOrDefault(x => x.UsagePage == 0xff90);
if (device is null) return 2;
await using var hid = new HidTransport();
var reports = new List<string>();
hid.ReportReceived += bytes => { lock (reports) reports.Add(Convert.ToHexString(bytes)); Console.WriteLine("RX " + Convert.ToHexString(bytes)); };
hid.Disconnected += error => Console.WriteLine("ERROR " + error);
hid.Open(device);
if (args.Length > 0)
{
    foreach (var hex in args)
    {
        var payload = Convert.FromHexString(hex);
        var report = new byte[device.OutputLength];
        payload.CopyTo(report, 0);
        Console.WriteLine("TX " + Convert.ToHexString(report));
        await hid.SendAsync(report);
        await Task.Delay(1200);
    }
}
await Task.Delay(2500);
return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 1;
}
