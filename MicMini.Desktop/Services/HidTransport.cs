using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MicMini.Desktop.Services;

public sealed record HidDeviceInfo(string Path, string Product, string UsbSerial, ushort UsagePage, ushort Usage,
    ushort InputLength, ushort OutputLength, ushort FeatureLength, Guid ContainerId = default);

/// <summary>Windows inbox HID driver. Never replaces or installs an audio/USB driver.</summary>
public sealed class HidTransport : IAsyncDisposable
{
    private FileStream? stream;
    private CancellationTokenSource? stop;
    private Task? reader;
    private readonly SemaphoreSlim writes = new(1, 1);
    public event Action<byte[]>? ReportReceived;
    public event Action<string>? Disconnected;
    public bool IsOpen => stream is not null;
    public HidDeviceInfo? Device { get; private set; }

    public static IReadOnlyList<HidDeviceInfo> Enumerate()
    {
        Native.HidD_GetHidGuid(out var guid);
        var set = Native.SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var result = new List<HidDeviceInfo>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var iface = new InterfaceData { Size = Marshal.SizeOf<InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref iface))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception();
                    break;
                }
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref iface, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                var detail = Marshal.AllocHGlobal((int)size);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    var deviceInfo = new DeviceInfoData { Size = Marshal.SizeOf<DeviceInfoData>() };
                    if (!Native.SetupDiGetDeviceInterfaceDetailWithInfoW(set, ref iface, detail, size, out _, ref deviceInfo)) continue;
                    var path = Marshal.PtrToStringUni(detail + 4)!;
                    if (!path.Contains("vid_2ca3&pid_4011", StringComparison.OrdinalIgnoreCase)) continue;
                    using var handle = Native.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (handle.IsInvalid || !Native.HidD_GetPreparsedData(handle, out var preparsed)) continue;
                    try
                    {
                        if (Native.HidP_GetCaps(preparsed, out var caps) < 0) continue;
                        result.Add(new(path, GetString(handle, false), GetString(handle, true), caps.UsagePage, caps.Usage,
                            caps.InputLength, caps.OutputLength, caps.FeatureLength, ReadContainerId(set, ref deviceInfo)));
                    }
                    finally { Native.HidD_FreePreparsedData(preparsed); }
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    private static Guid ReadContainerId(IntPtr set, ref DeviceInfoData device)
    {
        var key = new PropertyKey { Format = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), Id = 2 };
        var bytes = new byte[16];
        return Native.SetupDiGetDevicePropertyW(set, ref device, ref key, out var type, bytes, 16, out var size, 0)
            && type == 0x0D && size == 16 ? new Guid(bytes) : Guid.Empty;
    }

    /// <summary>Checks a report descriptor in memory. Does not send a report or request write access.</summary>
    public static bool DeclaresOutputReport(HidDeviceInfo device, byte reportId)
    {
        using var handle = Native.CreateFileW(device.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !Native.HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            if (device.OutputLength == 0) return false;
            var report = new byte[device.OutputLength];
            return Native.HidP_InitializeReportForID(1, reportId, data, report, (uint)report.Length) == 0x00110000;
        }
        finally { Native.HidD_FreePreparsedData(data); }
    }

    private static string GetString(SafeFileHandle handle, bool serial)
    {
        var buffer = new byte[256];
        var ok = serial ? Native.HidD_GetSerialNumberString(handle, buffer, buffer.Length) : Native.HidD_GetProductString(handle, buffer, buffer.Length);
        return ok ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0') : "";
    }

    public void Open(HidDeviceInfo device)
    {
        if (stream is not null) throw new InvalidOperationException("HID 通道已打开。");
        var handle = Native.CreateFileW(device.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = new Win32Exception(); handle.Dispose(); throw error; }
        // Disable FileStream's shared buffer: an outstanding HID read must not block writes.
        try { stream = new FileStream(handle, FileAccess.ReadWrite, 1, true); }
        catch { handle.Dispose(); throw; }
        Device = device;
        stop = new CancellationTokenSource();
        reader = ReadLoop(stream, device.InputLength, stop.Token);
    }

    private async Task ReadLoop(FileStream input, int length, CancellationToken token)
    {
        var buffer = new byte[length];
        try
        {
            while (!token.IsCancellationRequested)
            {
                var n = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (n == 0) throw new IOException("HID 设备已断开。");
                ReportReceived?.Invoke(buffer[..n]);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) Disconnected?.Invoke(ex.Message); }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> report, CancellationToken token = default)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var output = stream ?? throw new IOException("HID 未连接。");
            if (report.Length != Device!.OutputLength) throw new ArgumentException("HID 报告长度不匹配。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await output.WriteAsync(report, timeout.Token).ConfigureAwait(false);
            await output.FlushAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { writes.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        stop?.Cancel();
        if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
        if (reader is not null) await reader.ConfigureAwait(false);
        stream = null; reader = null; Device = null;
        stop?.Dispose(); stop = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InterfaceData { public int Size; public Guid Guid; public uint Flags; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData { public int Size; public Guid ClassGuid; public uint DeviceInstance; public UIntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
    }
    private static class Native
    {
        [DllImport("hid.dll")] public static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern IntPtr SetupDiGetClassDevsW(ref Guid guid, string? enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetupDiGetDeviceInterfaceDetailWithInfoW(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, ref DeviceInfoData info);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref DeviceInfoData device, ref PropertyKey key, out uint type, byte[] buffer, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
        [DllImport("hid.dll")] public static extern int HidP_InitializeReportForID(int type, byte reportId, IntPtr data, byte[] report, uint length);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_GetProductString(SafeFileHandle device, byte[] data, int length);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] public static extern bool HidD_GetSerialNumberString(SafeFileHandle device, byte[] data, int length);
    }
}
