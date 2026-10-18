using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using MicMini.Desktop.Services;

// Uses synthetic PCM and private writer seams only. Never opens a physical input.
const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
var output = Path.Combine(Path.GetTempPath(), "MicMini-audio-selftest-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var recordingType = typeof(AudioCapture).GetNestedType("RecordingSession", BindingFlags.NonPublic)!;
var captureType = typeof(AudioCapture).GetNestedType("CaptureSession", BindingFlags.NonPublic)!;
var checks = 0;

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}
object Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, Hidden)!.Invoke(target, args)!;
object Writer(int rate, int channels, Action<string> error) => Activator.CreateInstance(recordingType,
    Hidden, null, new object[] { output, rate, channels, error }, null)!;
string WriterPath(object writer) => (string)recordingType.GetProperty("Path")!.GetValue(writer)!;
bool Enqueue(object writer, byte[] data)
{
    var pooled = ArrayPool<byte>.Shared.Rent(data.Length);
    data.CopyTo(pooled, 0);
    var accepted = (bool)Invoke(writer, "TryEnqueue", pooled, data.Length);
    if (!accepted) ArrayPool<byte>.Shared.Return(pooled);
    return accepted;
}

foreach (var (rate, channels) in new[] { (48000, 2), (48000, 1), (44100, 2) })
{
    var errors = new List<string>();
    var writer = Writer(rate, channels, errors.Add);
    var pcm = new byte[rate / 20 * channels * 2];
    for (var i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i % 251);
    for (var i = 0; i < 4; i++) Check(Enqueue(writer, pcm), "accept PCM packet");
    Invoke(writer, "Complete");
    Invoke(writer, "Wait");
    var bytes = File.ReadAllBytes(WriterPath(writer));
    Check(bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8), "RIFF signature");
    Check(BitConverter.ToUInt32(bytes, 4) == bytes.Length - 8, "RIFF size");
    Check(bytes.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8), "WAVE PCM signature");
    Check(BitConverter.ToUInt16(bytes, 22) == channels, "channel count");
    Check(BitConverter.ToUInt32(bytes, 24) == rate, "sample rate");
    Check(BitConverter.ToUInt32(bytes, 40) == pcm.Length * 4, "data size");
    Check(bytes.AsSpan(44, pcm.Length).SequenceEqual(pcm), "PCM bytes preserved");
    Check((TimeSpan)recordingType.GetProperty("Duration")!.GetValue(writer)! == TimeSpan.FromSeconds(0.2), "duration");
    Check(errors.Count == 0, "successful recording has no errors");
    Check(!Enqueue(writer, pcm), "stopped writer rejects further PCM");
}

using (var capture = new AudioCapture())
{
    var session = Activator.CreateInstance(captureType, Hidden, null, new object[] { capture, 0 }, null)!;
    captureType.GetProperty("Channels")!.SetValue(session, 2);
    captureType.GetProperty("SampleRate")!.SetValue(session, 48000);
    typeof(AudioCapture).GetField("_capture", Hidden)!.SetValue(capture, session);
    var data = new byte[] { 0x00, 0x80, 0x00, 0x40, 0xFF, 0x7F, 0x00, 0x00 };
    var ptr = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, ptr, data.Length);
        Invoke(capture, "ReceiveSamples", session, ptr, data.Length);
        Check(capture.PeakLeft == 1 && capture.PeakRight == 0.5, "PCM16 stereo peak including -32768");
        captureType.GetProperty("Channels")!.SetValue(session, 1);
        Invoke(capture, "ReceiveSamples", session, ptr, data.Length);
        Check(capture.PeakLeft == 1 && capture.PeakRight == 1, "mono level mirrors to right");
    }
    finally { Marshal.FreeHGlobal(ptr); }
    capture.StopMonitor();
    Check(!capture.IsMonitoring && !capture.IsRecording && capture.PeakLeft == 0, "idempotent stopped state");
    capture.StopMonitor();
    try { capture.StartRecording(output); throw new Exception("Unexpected recording without active input"); }
    catch (InvalidOperationException) { checks++; }
}

var faultErrors = new List<string>();
var faultWriter = Writer(48000, 2, faultErrors.Add);
((FileStream)recordingType.GetField("_stream", Hidden)!.GetValue(faultWriter)!).Dispose();
Enqueue(faultWriter, new byte[16]);
Invoke(faultWriter, "Complete");
try { Invoke(faultWriter, "Wait"); throw new Exception("A disk failure was silently accepted"); }
catch (TargetInvocationException exception) when (exception.InnerException is IOException) { checks++; }
Check(faultErrors.Count == 1, "disk fault is reported once");
Invoke(faultWriter, "Wait"); // An acknowledged failure cannot permanently block starting another recording.

var stressErrors = new List<string>();
var stress = Writer(48000, 2, stressErrors.Add);
var acceptedPackets = 0;
var producer = Task.Run(() =>
{
    for (var i = 0; i < 500; i++)
        if (Enqueue(stress, new byte[9600])) Interlocked.Increment(ref acceptedPackets);
});
producer.GetAwaiter().GetResult();
Invoke(stress, "Complete");
try { Invoke(stress, "Wait"); }
catch (TargetInvocationException exception) when (exception.InnerException is IOException && stressErrors.Count == 1) { }
var stressBytes = File.ReadAllBytes(WriterPath(stress));
Check(stressBytes.Length == 44 + acceptedPackets * 9600, "bounded producer preserves all accepted audio");
Check(BitConverter.ToUInt32(stressBytes, 40) == stressBytes.Length - 44, "stress WAV finalized");

var raceErrors = new List<string>();
var raceWriter = Writer(48000, 2, raceErrors.Add);
using var producerStarted = new ManualResetEventSlim();
var raceAccepted = 0;
var racingProducer = Task.Run(() =>
{
    for (var i = 0; i < 500; i++)
    {
        if (Enqueue(raceWriter, new byte[9600])) Interlocked.Increment(ref raceAccepted);
        if (i == 4) producerStarted.Set();
    }
});
producerStarted.Wait();
Invoke(raceWriter, "Complete");
racingProducer.GetAwaiter().GetResult();
try { Invoke(raceWriter, "Wait"); }
catch (TargetInvocationException exception) when (exception.InnerException is IOException && raceErrors.Count == 1) { }
Check(new FileInfo(WriterPath(raceWriter)).Length == 44 + raceAccepted * 9600, "concurrent stop preserves accepted buffers");

Console.WriteLine($"PASS: {checks} checks; synthetic PCM only; no microphone was opened.");
Console.WriteLine("Generated WAV fixtures: " + output);
