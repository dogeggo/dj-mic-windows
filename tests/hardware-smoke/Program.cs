using System.Text.Json;
using System.Buffers.Binary;
using MicMini.Desktop.Services;

var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("hardware-smoke");
Directory.CreateDirectory(output);
await using var service = new MicDeviceService();
service.Log += Console.WriteLine;
var receiver = await service.ConnectAndReadAsync();
var devices = AudioCapture.EnumerateDevices();
Console.WriteLine(JsonSerializer.Serialize(devices));
var match = devices.Where(x => x.Name.Contains("Wireless Mic Rx", StringComparison.OrdinalIgnoreCase)).ToArray();
if (match.Length != 1) throw new Exception("需要恰好一个真实 Wireless Mic Rx 输入。");
using var capture = new AudioCapture();
var errors = new List<string>();
capture.Error += error => { lock (errors) errors.Add(error); };
capture.StartMonitor(match[0].Id);
await Task.Delay(500);
var recording = capture.StartRecording(output);
double left = 0, right = 0;
for (var i = 0; i < 30; i++) { await Task.Delay(100); left = Math.Max(left, capture.PeakLeft); right = Math.Max(right, capture.PeakRight); }
capture.StopRecording();
var duration = capture.RecordedDuration;
var sampleRate = capture.SampleRate; var channels = capture.Channels;
capture.StopMonitor();
var bytes = File.ReadAllBytes(recording);
if (System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)) != bytes.Length - 44)
    throw new Exception("WAV 长度/头校验失败。");
if (duration.TotalSeconds < 2 || bytes.Length < 10000) throw new Exception("录音内容不足。");
var result = new { Receiver = receiver, AudioDevice = match[0], SampleRate = sampleRate, Channels = channels, Duration = duration.TotalSeconds,
    PeakLeft = left, PeakRight = right, WavFile = recording, WavBytes = bytes.Length, Errors = errors, TestedAt = DateTimeOffset.Now };
File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(result));
return errors.Count == 0 ? 0 : 1;
