using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MicMini.Desktop.Services;

public sealed record AudioInputDevice(int Id, string Name, int Channels);

/// <summary>
/// Captures only the explicitly selected Windows input. Monitoring means capture and
/// level metering; audio is deliberately not played through the speakers.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private readonly object _lifecycle = new();
    private readonly object _recordingGate = new();
    private CaptureSession? _capture;
    private RecordingSession? _recording;
    private bool _disposed;
    private double _peakLeft;
    private double _peakRight;

    public bool IsMonitoring => Volatile.Read(ref _capture)?.IsRunning == true;
    public bool IsRecording => Volatile.Read(ref _recording)?.IsAccepting == true;
    public double PeakLeft => Volatile.Read(ref _peakLeft);
    public double PeakRight => Volatile.Read(ref _peakRight);
    public TimeSpan RecordedDuration => Volatile.Read(ref _recording)?.Duration ?? TimeSpan.Zero;
    public string? RecordingPath => Volatile.Read(ref _recording)?.Path;
    public int SampleRate => Volatile.Read(ref _capture)?.SampleRate ?? 0;
    public int Channels => Volatile.Read(ref _capture)?.Channels ?? 0;
    public event Action<string>? Error;

    public static IReadOnlyList<AudioInputDevice> EnumerateDevices()
    {
        var devices = new List<AudioInputDevice>();
        var count = Native.waveInGetNumDevs();
        for (uint id = 0; id < count; id++)
        {
            if (Native.waveInGetDevCapsW((UIntPtr)id, out var caps,
                    (uint)Marshal.SizeOf<Native.WaveInCaps>()) == 0)
                devices.Add(new AudioInputDevice((int)id, caps.Name, caps.Channels));
        }
        return devices;
    }

    public void StartMonitor(int deviceId)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (deviceId < 0 || !EnumerateDevices().Any(device => device.Id == deviceId))
                throw new ArgumentOutOfRangeException(nameof(deviceId), "指定的音频输入设备不存在，请重新扫描设备。");
            if (_capture is { IsRunning: true } current && current.DeviceId == deviceId)
                return;
            StopMonitorCore();
            var session = new CaptureSession(this, deviceId);
            Volatile.Write(ref _capture, session);
            try { session.Start(); }
            catch
            {
                session.Stop();
                Volatile.Write(ref _capture, null);
                throw;
            }
        }
    }

    public void StopMonitor()
    {
        lock (_lifecycle) StopMonitorCore();
    }

    private void StopMonitorCore()
    {
        _capture?.Stop();
        StopRecordingCore();
        Volatile.Write(ref _peakLeft, 0);
        Volatile.Write(ref _peakRight, 0);
    }

    public string StartRecording(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_capture is not { IsRunning: true } capture)
                throw new InvalidOperationException("请先启动所选麦克风的音频采集。");
            if (IsRecording)
                throw new InvalidOperationException("已有录音正在进行，请先停止录音。");
            StopRecordingCore();
            var recording = new RecordingSession(directory, capture.SampleRate, capture.Channels, ReportError);
            lock (_recordingGate)
            {
                // Disconnect can race file creation. Do not report success for a dead stream.
                if (!capture.IsRunning)
                {
                    recording.Complete();
                    recording.Wait();
                    throw new InvalidOperationException("音频输入已断开，无法开始录音。");
                }
                Volatile.Write(ref _recording, recording);
            }
            return recording.Path;
        }
    }

    public void StopRecording()
    {
        lock (_lifecycle) StopRecordingCore();
    }

    private void StopRecordingCore()
    {
        RecordingSession? recording;
        lock (_recordingGate)
        {
            recording = _recording;
            recording?.Complete();
        }
        // Waiting never holds the gate used by capture, so disk latency cannot block it.
        recording?.Wait();
    }

    private void ReceiveSamples(CaptureSession capture, IntPtr data, int length)
    {
        if (!ReferenceEquals(capture, Volatile.Read(ref _capture))) return;
        var samples = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Marshal.Copy(data, samples, 0, length);
            var left = 0;
            var right = 0;
            for (var offset = 0; offset < length; offset += capture.Channels * 2)
            {
                var value = Math.Abs((int)(short)(samples[offset] | samples[offset + 1] << 8));
                left = Math.Max(left, value);
                if (capture.Channels == 2)
                {
                    value = Math.Abs((int)(short)(samples[offset + 2] | samples[offset + 3] << 8));
                    right = Math.Max(right, value);
                }
            }
            Volatile.Write(ref _peakLeft, left / 32768d);
            Volatile.Write(ref _peakRight, capture.Channels == 1 ? left / 32768d : right / 32768d);
            lock (_recordingGate)
            {
                if (_recording?.TryEnqueue(samples, length) == true)
                    samples = null!; // Ownership has moved to the disk writer.
            }
        }
        finally
        {
            if (samples is not null) ArrayPool<byte>.Shared.Return(samples);
        }
    }

    private void CaptureStopped(CaptureSession capture, string? error)
    {
        if (!ReferenceEquals(capture, Volatile.Read(ref _capture))) return;
        lock (_recordingGate) _recording?.Complete();
        Volatile.Write(ref _peakLeft, 0);
        Volatile.Write(ref _peakRight, 0);
        if (error is not null) ReportError(error);
    }

    private void ReportError(string message)
    {
        // UI clients marshal this event onto their dispatcher. A subscriber cannot
        // block native-buffer handling or make the writer wait on the UI thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var handlers = Error;
            if (handlers is null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(message); }
                catch (Exception exception) { Trace.TraceError($"Audio error subscriber: {exception}"); }
            }
        });
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            StopMonitorCore();
        }
        GC.SuppressFinalize(this);
    }

    private sealed class CaptureSession
    {
        private const int BufferCount = 6;
        private const uint HeaderDone = 1;
        private const uint HeaderPrepared = 2;
        private readonly AudioCapture _owner;
        private readonly AutoResetEvent _ready = new(false);
        private readonly List<NativeBuffer> _buffers = [];
        private IntPtr _handle;
        private Thread? _thread;
        private int _running;
        private int _stopRequested;
        private bool _cleaned;
        // A broken driver must not cause us to free memory it still owns. Such a
        // session stays rooted until the process exits; the error is surfaced.
        private static readonly ConcurrentBag<CaptureSession> UnreleasedDriverSessions = [];

        public CaptureSession(AudioCapture owner, int deviceId) { _owner = owner; DeviceId = deviceId; }
        public int DeviceId { get; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }
        public bool IsRunning => Volatile.Read(ref _running) != 0;

        public void Start()
        {
            if (!UnreleasedDriverSessions.IsEmpty)
                throw new IOException("音频驱动仍占用上次采集的缓冲区，请重新插拔接收器并重启应用后再试。");
            var formats = new[] { (48000, 2), (48000, 1), (44100, 2), (44100, 1) };
            uint result = 32;
            foreach (var (sampleRate, channels) in formats)
            {
                var format = Native.WaveFormat.Pcm(sampleRate, channels);
                result = Native.waveInOpen(out _handle, (uint)DeviceId, ref format,
                    _ready.SafeWaitHandle.DangerousGetHandle(), UIntPtr.Zero, 0x00050000);
                if (result == 0)
                {
                    SampleRate = sampleRate;
                    Channels = channels;
                    break;
                }
                // Only unsupported-format errors justify trying another format.
                if (result != 32) break;
            }
            Native.Check(result, "打开所选音频输入");
            try
            {
                var bufferSize = SampleRate / 20 * Channels * 2; // 50 ms, six native buffers.
                for (var index = 0; index < BufferCount; index++)
                {
                    var buffer = new NativeBuffer(bufferSize);
                    _buffers.Add(buffer);
                    Native.Check(Native.waveInPrepareHeader(_handle, buffer.Header, Native.HeaderSize), "准备音频缓冲区");
                    Native.Check(Native.waveInAddBuffer(_handle, buffer.Header, Native.HeaderSize), "提交音频缓冲区");
                }
                Native.Check(Native.waveInStart(_handle), "启动麦克风采集");
                Volatile.Write(ref _running, 1);
                _thread = new Thread(Run) { IsBackground = true, Name = "MicMini audio capture" };
                _thread.Start();
            }
            catch
            {
                Volatile.Write(ref _running, 0);
                Cleanup();
                throw;
            }
        }

        public void Stop()
        {
            Volatile.Write(ref _stopRequested, 1);
            try { _ready.Set(); }
            catch (ObjectDisposedException) { /* The capture thread has already finished. */ }
            if (_thread is { IsAlive: true } && Thread.CurrentThread != _thread) _thread.Join();
            if (_thread is null) Cleanup();
        }

        private void Run()
        {
            string? error = null;
            var lastData = Stopwatch.GetTimestamp();
            var nextBuffer = 0;
            try
            {
                while (Volatile.Read(ref _stopRequested) == 0)
                {
                    _ready.WaitOne(250);
                    // Process in submission order, including when one event covers
                    // several completed buffers and the ring wraps around.
                    for (var index = 0; index < _buffers.Count && Volatile.Read(ref _stopRequested) == 0; index++)
                    {
                        var buffer = _buffers[nextBuffer];
                        var header = Marshal.PtrToStructure<Native.WaveHeader>(buffer.Header);
                        if ((header.Flags & HeaderDone) == 0) break;
                        if (header.BytesRecorded > 0)
                        {
                            Deliver(buffer, header.BytesRecorded);
                            lastData = Stopwatch.GetTimestamp();
                        }
                        Native.Check(Native.waveInAddBuffer(_handle, buffer.Header, Native.HeaderSize), "重新提交音频缓冲区（设备可能已断开）");
                        nextBuffer = (nextBuffer + 1) % _buffers.Count;
                    }
                    if (Stopwatch.GetElapsedTime(lastData) > TimeSpan.FromSeconds(5))
                        throw new IOException("麦克风连续 5 秒没有返回音频数据，设备可能已断开；录音已停止。");
                }
                // Reset returns partially filled buffers. Preserve them in stream
                // order so the final fraction of a second is included in the WAV.
                Native.Check(Native.waveInReset(_handle), "停止音频采集");
                for (var index = 0; index < _buffers.Count; index++)
                {
                    var buffer = _buffers[(nextBuffer + index) % _buffers.Count];
                    var header = Marshal.PtrToStructure<Native.WaveHeader>(buffer.Header);
                    if ((header.Flags & HeaderDone) != 0 && header.BytesRecorded > 0)
                        Deliver(buffer, header.BytesRecorded);
                }
            }
            catch (Exception exception) { error = exception.Message; }
            finally
            {
                Volatile.Write(ref _running, 0);
                var cleanupError = Cleanup();
                _owner.CaptureStopped(this, error is null ? cleanupError : cleanupError is null ? error : $"{error} {cleanupError}");
            }
        }

        private void Deliver(NativeBuffer buffer, uint count)
        {
            if (count > buffer.Capacity) throw new IOException("音频驱动返回了无效的缓冲区长度。");
            var frameSize = Channels * 2;
            var length = (int)count / frameSize * frameSize;
            if (length > 0) _owner.ReceiveSamples(this, buffer.Data, length);
        }

        private string? Cleanup()
        {
            if (_cleaned) return null;
            _cleaned = true;
            uint closeResult = 0;
            if (_handle != IntPtr.Zero)
            {
                // A reset returns all queued buffers before unprepare/close.
                Native.waveInReset(_handle);
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    foreach (var buffer in _buffers)
                    {
                        var header = Marshal.PtrToStructure<Native.WaveHeader>(buffer.Header);
                        if ((header.Flags & HeaderPrepared) != 0)
                            Native.waveInUnprepareHeader(_handle, buffer.Header, Native.HeaderSize);
                    }
                    closeResult = Native.waveInClose(_handle);
                    if (closeResult == 0) break;
                    Thread.Sleep(20);
                }
            }
            if (closeResult != 0)
            {
                UnreleasedDriverSessions.Add(this);
                return $"音频驱动未能释放设备：{Native.Describe(closeResult)}。请重新插拔接收器并重启应用。";
            }
            _handle = IntPtr.Zero;
            foreach (var buffer in _buffers) buffer.Dispose();
            _buffers.Clear();
            _ready.Dispose();
            return null;
        }

        private sealed class NativeBuffer : IDisposable
        {
            public IntPtr Data { get; }
            public IntPtr Header { get; }
            public int Capacity { get; }
            public NativeBuffer(int capacity)
            {
                Capacity = capacity;
                Data = Marshal.AllocHGlobal(capacity);
                try
                {
                    Header = Marshal.AllocHGlobal((int)Native.HeaderSize);
                    Marshal.StructureToPtr(new Native.WaveHeader { Data = Data, BufferLength = (uint)capacity }, Header, false);
                }
                catch { Marshal.FreeHGlobal(Data); throw; }
            }
            public void Dispose() { Marshal.FreeHGlobal(Header); Marshal.FreeHGlobal(Data); }
        }
    }

    private sealed class RecordingSession
    {
        private const long MaximumDataLength = uint.MaxValue - 36L;
        private readonly BlockingCollection<Packet> _queue = new(40); // At most two seconds of PCM.
        private readonly FileStream _stream;
        private readonly Task _writer;
        private readonly Action<string> _reportError;
        private readonly int _sampleRate;
        private readonly int _channels;
        private int _accepting = 1;
        private long _written;
        private Exception? _failure;
        private int _failureAcknowledged;
        public string Path { get; }
        public bool IsAccepting => Volatile.Read(ref _accepting) != 0;
        public TimeSpan Duration => TimeSpan.FromSeconds(Interlocked.Read(ref _written) / (double)(_sampleRate * _channels * 2));

        public RecordingSession(string directory, int sampleRate, int channels, Action<string> reportError)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _reportError = reportError;
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory),
                $"DJI_Mic_Mini_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.wav");
            _stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                65536, FileOptions.SequentialScan);
            try { WriteHeader(_stream, sampleRate, channels, 0); }
            catch { _stream.Dispose(); throw; }
            _writer = Task.Factory.StartNew(WriteLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public bool TryEnqueue(byte[] bytes, int count)
        {
            if (!IsAccepting) return false;
            try
            {
                if (_queue.TryAdd(new Packet(bytes, count))) return true;
                Fail(new IOException("磁盘写入速度不足，录音缓冲区已满；录音已停止，已写入内容会保留。"));
            }
            catch (InvalidOperationException) { /* Writer faulted while the packet was being offered. */ }
            return false;
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _accepting, 0) != 0) _queue.CompleteAdding();
        }

        public void Wait()
        {
            _writer.GetAwaiter().GetResult();
            if (_failure is not null && Interlocked.Exchange(ref _failureAcknowledged, 1) == 0)
                throw new IOException($"录音未能完整保存：{_failure.Message} 文件：{Path}", _failure);
        }

        private void Fail(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _failure, exception, null) is null)
                _reportError($"{exception.Message} 文件：{Path}");
            Complete();
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var packet in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (_written + packet.Count > MaximumDataLength)
                            throw new IOException("录音已达到标准 WAV 的 4 GiB 大小上限，请开始新的录音。");
                        _stream.Write(packet.Bytes, 0, packet.Count);
                        Interlocked.Add(ref _written, packet.Count);
                    }
                    finally { ArrayPool<byte>.Shared.Return(packet.Bytes); }
                }
            }
            catch (Exception exception) { Fail(exception); }
            finally
            {
                Complete();
                while (_queue.TryTake(out var packet)) ArrayPool<byte>.Shared.Return(packet.Bytes);
                try
                {
                    // A failed write can leave a partial PCM frame at the end.
                    _stream.SetLength(44 + _written);
                    _stream.Position = 0;
                    WriteHeader(_stream, _sampleRate, _channels, (uint)_written);
                    _stream.Flush(true);
                }
                catch (Exception exception) { Fail(exception); }
                try { _stream.Dispose(); }
                catch (Exception exception) { Fail(exception); }
                // CompleteAdding may race a final producer. Keep the tiny queue
                // object alive with this session; no wait handle is ever allocated.
            }
        }

        private static void WriteHeader(Stream stream, int sampleRate, int channels, uint bytes)
        {
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write("RIFF"u8);
            writer.Write(bytes + 36u);
            writer.Write("WAVEfmt "u8);
            writer.Write(16u);
            writer.Write((ushort)1);
            writer.Write((ushort)channels);
            writer.Write((uint)sampleRate);
            writer.Write((uint)(sampleRate * channels * 2));
            writer.Write((ushort)(channels * 2));
            writer.Write((ushort)16);
            writer.Write("data"u8);
            writer.Write(bytes);
        }

        private readonly record struct Packet(byte[] Bytes, int Count);
    }

    private static class Native
    {
        internal static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        internal struct WaveFormat
        {
            public ushort FormatTag, Channels;
            public uint SamplesPerSecond, AverageBytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
            public static WaveFormat Pcm(int sampleRate, int channels) => new()
            {
                FormatTag = 1, Channels = (ushort)channels, SamplesPerSecond = (uint)sampleRate,
                AverageBytesPerSecond = (uint)(sampleRate * channels * 2),
                BlockAlign = (ushort)(channels * 2), BitsPerSample = 16
            };
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength, BytesRecorded;
            public UIntPtr User;
            public uint Flags, Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WaveInCaps
        {
            public ushort Manufacturer, Product;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels, Reserved;
        }
        internal static void Check(uint result, string operation)
        {
            if (result != 0) throw new IOException($"{operation}失败：{Describe(result)}（{result}）。");
        }
        internal static string Describe(uint result)
        {
            var text = new StringBuilder(256);
            return waveInGetErrorTextW(result, text, (uint)text.Capacity) == 0 ? text.ToString() : $"WinMM {result}";
        }
        [DllImport("winmm.dll")] internal static extern uint waveInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint waveInGetDevCapsW(UIntPtr deviceId, out WaveInCaps caps, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInOpen(out IntPtr handle, uint deviceId,
            ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
        [DllImport("winmm.dll")] internal static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
        [DllImport("winmm.dll")] internal static extern uint waveInStart(IntPtr handle);
        [DllImport("winmm.dll")] internal static extern uint waveInReset(IntPtr handle);
        [DllImport("winmm.dll")] internal static extern uint waveInClose(IntPtr handle);
        [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        internal static extern uint waveInGetErrorTextW(uint error, StringBuilder text, uint length);
    }
}
