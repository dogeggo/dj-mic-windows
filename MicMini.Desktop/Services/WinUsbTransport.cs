using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MicMini.Desktop.Services;

public sealed record WinUsbDeviceInfo(string Path, string InstanceId, string Service);

/// <summary>
/// Raw WinUSB bulk transport restricted to the Mic Mini receiver's MI_06 interface.
/// Installs no driver and adds no DJI framing. DataReceived runs on the read thread;
/// its subscribers must return promptly and marshal UI changes asynchronously.
/// </summary>
public sealed class WinUsbTransport : IAsyncDisposable
{
    private const string HardwareId = "VID_2CA3&PID_4011&MI_06";
    private const string ExpectedService = "WinUSB";
    private const int MaximumTransfer = 64 * 1024;
    private readonly object _lifecycle = new();
    private Session? _session;
    private bool _disposed;

    public bool IsOpen => Volatile.Read(ref _session)?.IsOpen == true;
    public event Action<byte[]>? DataReceived;
    public event Action<string>? Disconnected;

    /// <summary>
    /// Returns present MI_06 interfaces registered for WinUSB. An empty result means
    /// no matching receiver is present. A present but unconfigured MI_06 produces a
    /// diagnostic exception, so a missing driver cannot masquerade as a connection.
    /// </summary>
    public static IReadOnlyList<WinUsbDeviceInfo> Enumerate()
    {
        var targets = FindPresentTargets();
        if (targets.Count == 0) return Array.Empty<WinUsbDeviceInfo>();
        var devices = new List<WinUsbDeviceInfo>();
        var diagnostics = new List<string>();
        foreach (var target in targets)
        {
            if (!string.Equals(target.Service, ExpectedService, StringComparison.OrdinalIgnoreCase))
            {
                var service = string.IsNullOrWhiteSpace(target.Service) ? "未安装" : target.Service;
                diagnostics.Add($"{target.InstanceId}：MI_06 尚未绑定 WinUSB 驱动（当前服务：{service}；设备问题代码：{target.ProblemCode}）。");
                continue;
            }
            var guids = ReadInterfaceGuids(target.InstanceId);
            if (guids.Count == 0)
            {
                diagnostics.Add($"{target.InstanceId}：已绑定 WinUSB，但 Device Parameters/DeviceInterfaceGUIDs 未登记有效 GUID。");
                continue;
            }
            var before = devices.Count;
            foreach (var guid in guids) AddInterfaces(guid, target.InstanceId, devices);
            if (before == devices.Count)
                diagnostics.Add($"{target.InstanceId}：WinUSB 驱动已登记，但目标 MI_06 的设备接口尚未就绪（问题代码：{target.ProblemCode}）。");
        }
        if (devices.Count == 0)
            throw new IOException("Mic Mini 控制通道不可用。" + string.Join(" ", diagnostics) +
                " 仅应为 MI_06 配置 WinUSB；音频、HID 与 USB 复合设备接口不属于此适配器。");
        return devices.DistinctBy(device => device.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Open(WinUsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null)
                throw new InvalidOperationException("WinUSB 适配器已有会话；重新连接时请释放旧适配器并创建新实例。");
            if (!IsTargetInstance(device.InstanceId) || !IsTargetPath(device.Path) ||
                !string.Equals(device.Service, ExpectedService, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("只允许打开 VID_2CA3/PID_4011 的 MI_06 WinUSB 控制接口。", nameof(device));

            // Treat the caller's record as a selection, not as authority to open a
            // path. Re-check its present PnP identity and driver immediately before use.
            var current = Enumerate().FirstOrDefault(candidate =>
                string.Equals(candidate.Path, device.Path, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.InstanceId, device.InstanceId, StringComparison.OrdinalIgnoreCase));
            if (current is null) throw new IOException("所选 Mic Mini MI_06 WinUSB 接口已断开或驱动状态发生变化，请重新扫描。");

            var session = Session.Open(current.Path, PublishData, PublishDisconnected);
            Volatile.Write(ref _session, session);
            session.Start();
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (data.Length == 0 || data.Length > MaximumTransfer)
            throw new ArgumentOutOfRangeException(nameof(data), $"单次 WinUSB 写入须为 1–{MaximumTransfer} 字节。");
        var session = Volatile.Read(ref _session);
        if (session is null || !session.IsOpen) throw new IOException("Mic Mini WinUSB 控制通道未连接。");
        return session.SendAsync(data, cancellationToken);
    }

    private void PublishData(byte[] bytes)
    {
        var handlers = DataReceived;
        if (handlers is null) return;
        foreach (Action<byte[]> handler in handlers.GetInvocationList())
        {
            try { handler(bytes); }
            catch (Exception exception) { Trace.TraceError($"WinUSB DataReceived subscriber: {exception}"); }
        }
    }

    private void PublishDisconnected(string error)
    {
        // Never invoke UI code while a native operation or shutdown lock is held.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var handlers = Disconnected;
            if (handlers is null) return;
            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(error); }
                catch (Exception exception) { Trace.TraceError($"WinUSB Disconnected subscriber: {exception}"); }
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            _disposed = true;
            return new ValueTask(_session?.StopAsync() ?? Task.CompletedTask);
        }
    }

    private static bool IsTargetInstance(string? instance)
    {
        if (string.IsNullOrWhiteSpace(instance)) return false;
        var parts = instance.Split('\\');
        return parts.Length == 3 && parts[2].Length > 0 &&
            parts[0].Equals("USB", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals(HardwareId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTargetPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        path.StartsWith(@"\\?\usb#" + HardwareId + "#", StringComparison.OrdinalIgnoreCase);

    private static List<TargetDevice> FindPresentTargets()
    {
        var set = Native.SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, 0x06);
        if (set == new IntPtr(-1)) throw LastError("枚举 USB 设备");
        var devices = new List<TargetDevice>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var info = DeviceInfo.Create();
                if (!Native.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw Error("枚举 USB 设备", error);
                }
                var instance = GetInstanceId(set, ref info);
                if (!IsTargetInstance(instance)) continue;
                var service = GetService(set, ref info);
                var result = Native.CM_Get_DevNode_Status(out _, out var problem, info.DevInst, 0);
                devices.Add(new TargetDevice(instance, service, result == 0 ? problem.ToString() : "未知"));
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return devices;
    }

    private static IReadOnlyList<Guid> ReadInterfaceGuids(string instance)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + instance + @"\Device Parameters", false);
        var value = key?.GetValue("DeviceInterfaceGUIDs", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var entries = value switch
        {
            string[] strings => strings.AsEnumerable(),
            string text => text.Split('\0', StringSplitOptions.RemoveEmptyEntries),
            _ => Array.Empty<string>()
        };
        return entries.Take(128).Where(entry => entry.Length <= 64)
            .Select(entry => Guid.TryParse(entry, out var guid) ? guid : Guid.Empty)
            .Where(guid => guid != Guid.Empty).Distinct().ToArray();
    }

    private static void AddInterfaces(Guid guid, string expectedInstance, List<WinUsbDeviceInfo> devices)
    {
        var set = Native.SetupDiGetClassDevsByGuid(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw LastError("枚举 MI_06 WinUSB 接口");
        try
        {
            for (uint index = 0; ; index++)
            {
                var iface = new InterfaceData { Size = (uint)Marshal.SizeOf<InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref iface))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw Error("枚举 MI_06 WinUSB 接口", error);
                }
                if ((iface.Flags & 1) == 0) continue; // SPINT_ACTIVE
                var info = DeviceInfo.Create();
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out var size, ref info);
                if (size < 8 || size > 65536) continue;
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    info = DeviceInfo.Create();
                    if (!Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, detail, size, out _, ref info))
                        continue; // A disconnect during enumeration is not a selectable interface.
                    var path = Marshal.PtrToStringUni(detail + 4, ((int)size - 4) / 2)?.TrimEnd('\0') ?? "";
                    var instance = GetInstanceId(set, ref info);
                    if (!IsTargetPath(path) || !IsTargetInstance(instance) ||
                        !instance.Equals(expectedInstance, StringComparison.OrdinalIgnoreCase)) continue;
                    var service = GetService(set, ref info);
                    if (!service.Equals(ExpectedService, StringComparison.OrdinalIgnoreCase)) continue;
                    devices.Add(new WinUsbDeviceInfo(path, instance, service));
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
    }

    private static string GetInstanceId(IntPtr set, ref DeviceInfo info)
    {
        var text = new StringBuilder(512);
        if (Native.SetupDiGetDeviceInstanceIdW(set, ref info, text, (uint)text.Capacity, out var required))
            return text.ToString();
        var error = Marshal.GetLastWin32Error();
        if (error != 122 || required > 4096) throw Error("读取 USB 设备实例 ID", error);
        text = new StringBuilder((int)required);
        if (!Native.SetupDiGetDeviceInstanceIdW(set, ref info, text, required, out _))
            throw LastError("读取 USB 设备实例 ID");
        return text.ToString();
    }

    private static string GetService(IntPtr set, ref DeviceInfo info)
    {
        var bytes = new byte[1024];
        if (!Native.SetupDiGetDeviceRegistryPropertyW(set, ref info, 4, out _, bytes, (uint)bytes.Length, out var length))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 13) return ""; // SPDRP_SERVICE is absent for Code 28 devices.
            throw Error("读取 MI_06 驱动服务", error);
        }
        if (length > bytes.Length) throw new IOException("设备返回了无效的驱动服务名称。");
        return Encoding.Unicode.GetString(bytes, 0, (int)length).TrimEnd('\0');
    }

    private static IOException LastError(string operation) => Error(operation, Marshal.GetLastWin32Error());
    private static IOException Error(string operation, int code) =>
        new($"{operation}失败：{new Win32Exception(code).Message}（Windows {code}）。", new Win32Exception(code));
    private sealed record TargetDevice(string InstanceId, string Service, string ProblemCode);

    private sealed class Session
    {
        private readonly SafeFileHandle _file;
        private readonly SafeWinUsbHandle _usb;
        private readonly byte _input;
        private readonly byte _output;
        private readonly Action<byte[]> _receive;
        private readonly Action<string> _disconnected;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _writes = new(1, 1);
        private readonly object _shutdownGate = new();
        private readonly Task _reader;
        private Task? _shutdown;
        private int _running;
        private int _errorReported;
        public bool IsOpen => Volatile.Read(ref _running) != 0;

        private Session(SafeFileHandle file, SafeWinUsbHandle usb, byte input, byte output,
            Action<byte[]> receive, Action<string> disconnected)
        {
            _file = file; _usb = usb; _input = input; _output = output;
            _receive = receive; _disconnected = disconnected;
            // Store the task before it can run: a very early unplug must never
            // make shutdown mistake an unassigned reader for a completed task.
            _reader = new Task(ReadLoop, CancellationToken.None, TaskCreationOptions.LongRunning);
        }

        public static Session Open(string path, Action<byte[]> receive, Action<string> disconnected)
        {
            var file = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (file.IsInvalid)
            {
                var error = LastError("打开 MI_06 WinUSB 设备");
                file.Dispose();
                throw error;
            }
            SafeWinUsbHandle? usb = null;
            try
            {
                if (!Native.WinUsb_Initialize(file, out var raw)) throw LastError("初始化 MI_06 WinUSB（请检查该接口的驱动绑定）");
                usb = new SafeWinUsbHandle(raw);
                if (!Native.WinUsb_GetCurrentAlternateSetting(usb, out var alternate)) throw LastError("查询 MI_06 当前接口设置");
                if (!Native.WinUsb_QueryInterfaceSettings(usb, alternate, out var descriptor)) throw LastError("查询 MI_06 USB 接口描述符");
                if (descriptor.InterfaceNumber != 6)
                    throw new IOException($"拒绝打开 USB 接口 {descriptor.InterfaceNumber}：此适配器仅允许 MI_06。");
                if (descriptor.Type != 4 || descriptor.Length < 9 || descriptor.EndpointCount is 0 or > 30)
                    throw new IOException("MI_06 返回了无效的 USB 接口描述符。");
                var inputs = new List<byte>();
                var outputs = new List<byte>();
                for (byte index = 0; index < descriptor.EndpointCount; index++)
                {
                    if (!Native.WinUsb_QueryPipe(usb, alternate, index, out var pipe)) throw LastError("查询 MI_06 bulk 管道");
                    if (pipe.Type != 2) continue;
                    if (pipe.MaximumPacketSize == 0 || pipe.MaximumPacketSize > 1024)
                        throw new IOException("MI_06 bulk 管道返回了无效的最大数据包大小。");
                    ((pipe.Id & 0x80) != 0 ? inputs : outputs).Add(pipe.Id);
                }
                if (inputs.Count != 1 || outputs.Count != 1)
                    throw new IOException($"MI_06 需要唯一的 bulk IN/OUT 管道，实际为 {inputs.Count}/{outputs.Count}；未猜测或选择其他接口。");
                uint readTimeout = 1000;
                uint writeTimeout = 3000;
                if (!Native.WinUsb_SetPipePolicy(usb, inputs[0], 3, 4, ref readTimeout)) throw LastError("设置 WinUSB 读取超时");
                if (!Native.WinUsb_SetPipePolicy(usb, outputs[0], 3, 4, ref writeTimeout)) throw LastError("设置 WinUSB 写入超时");
                return new Session(file, usb, inputs[0], outputs[0], receive, disconnected);
            }
            catch
            {
                usb?.Dispose();
                file.Dispose();
                throw;
            }
        }

        public void Start()
        {
            lock (_shutdownGate)
            {
                Volatile.Write(ref _running, 1);
                try { _reader.Start(TaskScheduler.Default); }
                catch
                {
                    Volatile.Write(ref _running, 0);
                    _usb.Dispose();
                    _file.Dispose();
                    _shutdown = Task.CompletedTask;
                    throw;
                }
            }
        }

        private void ReadLoop()
        {
            var buffer = new byte[MaximumTransfer];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    uint count;
                    try { count = Transfer(buffer, _input, false, _stop.Token); }
                    catch (Win32Exception exception) when (exception.NativeErrorCode is 121 or 1460 && !_stop.IsCancellationRequested)
                    { continue; } // A one-second idle interval is normal for a control interface.
                    if (count == 0) continue; // USB zero-length packets are not disconnects.
                    _receive(buffer.AsSpan(0, checked((int)count)).ToArray());
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (!_stop.IsCancellationRequested) Fail("Mic Mini WinUSB 读取失败或设备已断开：" + exception.Message);
            }
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
            await _writes.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!IsOpen) throw new IOException("Mic Mini WinUSB 控制通道已关闭。");
                linked.Token.ThrowIfCancellationRequested();
                // Copy only after acquiring the gate: queued callers do not each
                // allocate a native-write buffer. There is at most one bulk OUT.
                var bytes = data.ToArray();
                var written = await Task.Run(() => Transfer(bytes, _output, true, linked.Token), CancellationToken.None).ConfigureAwait(false);
                if (written != bytes.Length)
                    throw new IOException($"WinUSB 仅写入 {written}/{bytes.Length} 字节；未自动重发可能具有副作用的命令。");
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (!_stop.IsCancellationRequested) Fail("Mic Mini WinUSB 写入失败：" + exception.Message);
                throw;
            }
            finally { _writes.Release(); }
        }

        private uint Transfer(byte[] bytes, byte pipe, bool write, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var signal = new ManualResetEvent(false);
            var overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
            GCHandle pinned = default;
            var operationGate = new object();
            var pending = false;
            try
            {
                pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                Marshal.StructureToPtr(new NativeOverlappedData { Event = signal.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);
                using var cancellation = token.Register(() =>
                {
                    lock (operationGate)
                    {
                        if (pending) Native.CancelIoEx(_file, overlapped);
                    }
                });
                bool success;
                int error;
                uint transferred;
                lock (operationGate)
                {
                    // Submission and cancellation share a gate. A cancel cannot
                    // occur just before submission and miss the native operation.
                    token.ThrowIfCancellationRequested();
                    success = write
                        ? Native.WinUsb_WritePipe(_usb, pipe, pinned.AddrOfPinnedObject(), (uint)bytes.Length, out transferred, overlapped)
                        : Native.WinUsb_ReadPipe(_usb, pipe, pinned.AddrOfPinnedObject(), (uint)bytes.Length, out transferred, overlapped);
                    error = success ? 0 : Marshal.GetLastWin32Error();
                    // With OVERLAPPED, obtain the authoritative byte count from
                    // GetOverlappedResult even when submission completes immediately.
                    pending = success || error == 997;
                }
                if (pending)
                {
                    // Always wait for the completion acknowledgement, including
                    // after cancellation, before releasing OVERLAPPED or PCM memory.
                    success = Native.WinUsb_GetOverlappedResult(_usb, overlapped, out transferred, true);
                    error = success ? 0 : Marshal.GetLastWin32Error();
                    lock (operationGate) pending = false;
                }
                token.ThrowIfCancellationRequested();
                if (!success) throw new Win32Exception(error);
                if (transferred > bytes.Length) throw new IOException("WinUSB 返回的传输长度超出缓冲区。");
                return transferred;
            }
            finally
            {
                if (pinned.IsAllocated) pinned.Free();
                Marshal.FreeHGlobal(overlapped);
            }
        }

        private void Fail(string message)
        {
            _ = StopAsync();
            if (Interlocked.Exchange(ref _errorReported, 1) == 0) _disconnected(message);
        }

        public Task StopAsync()
        {
            lock (_shutdownGate)
            {
                if (_shutdown is not null) return _shutdown;
                Volatile.Write(ref _running, 0);
                _stop.Cancel();
                // CancelIoEx targets each outstanding OVERLAPPED. AbortPipe also
                // releases a stalled endpoint; handles stay alive until both loops finish.
                Native.WinUsb_AbortPipe(_usb, _input);
                Native.WinUsb_AbortPipe(_usb, _output);
                _shutdown = Task.Run(async () =>
                {
                    await _reader.ConfigureAwait(false);
                    await _writes.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        _usb.Dispose();
                        _file.Dispose();
                    }
                    finally { _writes.Release(); }
                    // Keep the cancelled source and semaphore usable for late
                    // SendAsync callers. They own no native USB resources.
                });
                return _shutdown;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
        public static DeviceInfo Create() => new() { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public uint Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct UsbInterfaceDescriptor
    {
        public byte Length, Type, InterfaceNumber, AlternateSetting, EndpointCount, InterfaceClass, InterfaceSubClass, InterfaceProtocol, StringIndex;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PipeInfo { public int Type; public byte Id; public ushort MaximumPacketSize; public byte Interval; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData
    {
        public UIntPtr Internal, InternalHigh;
        public uint Offset, OffsetHigh;
        public IntPtr Event;
    }
    private sealed class SafeWinUsbHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeWinUsbHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => Native.WinUsb_Free(handle);
    }
    private static class Native
    {
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr SetupDiGetClassDevsW(IntPtr guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern IntPtr SetupDiGetClassDevsByGuid(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref DeviceInfo info, StringBuilder text, uint size, out uint required);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint size, out uint required);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceInfo info);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("cfgmgr32.dll")] public static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        public static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle file, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_Free(IntPtr usb);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_GetCurrentAlternateSetting(SafeWinUsbHandle usb, out byte setting);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_QueryInterfaceSettings(SafeWinUsbHandle usb, byte setting, out UsbInterfaceDescriptor descriptor);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_QueryPipe(SafeWinUsbHandle usb, byte setting, byte index, out PipeInfo pipe);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_SetPipePolicy(SafeWinUsbHandle usb, byte pipe, uint policy, uint length, ref uint value);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_AbortPipe(SafeWinUsbHandle usb, byte pipe);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_ReadPipe(SafeWinUsbHandle usb, byte pipe, IntPtr buffer, uint length, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_WritePipe(SafeWinUsbHandle usb, byte pipe, IntPtr buffer, uint length, out uint transferred, IntPtr overlapped);
        [DllImport("winusb.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WinUsb_GetOverlappedResult(SafeWinUsbHandle usb, IntPtr overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);
    }
}
