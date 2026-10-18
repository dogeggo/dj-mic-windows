using MicMini.Desktop.Protocol;

namespace MicMini.Desktop.Services;

public sealed record TransmitterInfo(byte Module, string Firmware);
public sealed record ReceiverState(HidDeviceInfo Device, string Firmware, IReadOnlyList<TransmitterInfo> Transmitters, DateTimeOffset ReadAt);

public sealed class MicDeviceService : IAsyncDisposable
{
    private HidTransport? transport;
    private readonly SemaphoreSlim session = new(1, 1);
    private readonly object pendingGate = new();
    private TaskCompletionSource<byte[]>? pending;
    private byte expected;
    public event Action<string>? Log;
    public event Action<string>? Disconnected;
    public bool IsConnected => transport?.IsOpen == true;
    public ReceiverState? State { get; private set; }

    public async Task<ReceiverState> ConnectAndReadAsync()
    {
        await session.WaitAsync();
        try
        {
            await CloseInternal();
            var all = await Task.Run(HidTransport.Enumerate);
            var matches = all.Where(x => x.UsagePage == 0xff90 && x.InputLength == 64 && x.OutputLength == 64).ToArray();
            if (matches.Length == 0) throw new IOException("未找到 DJI Mic USB 控制接口，请检查接收端连接。");
            if (matches.Length > 1) throw new IOException("检测到多个 DJI 接收端，请仅连接需要控制的一台后重试。");
            var device = matches[0];
            transport = new HidTransport();
            transport.ReportReceived += HandleReport;
            transport.Disconnected += HandleDisconnect;
            transport.Open(device);
            Log?.Invoke("已打开 DJI USB HID 信息通道。");
            var firmware = MicHidProtocol.ReadVersion(await Query(MicHidProtocol.VersionRequest(0)));
            var count = MicHidProtocol.ReadTransmitterCount(await Query(MicHidProtocol.TransmitterCountRequest()));
            // Mic Mini has two TX slots. Do not infer a new device profile from a corrupt/unknown reply.
            if (count > 2) throw new InvalidDataException($"设备返回 {count} 个 TX，超出已验证 Mic Mini 配置。");
            var transmitters = new List<TransmitterInfo>();
            for (byte ordinal = 1; ordinal <= count; ordinal++)
            {
                var module = count == 1 ? (byte)1 : MicHidProtocol.ReadTransmitterIndex(await Query(MicHidProtocol.TransmitterIndexRequest(ordinal)));
                var version = MicHidProtocol.ReadVersion(await Query(MicHidProtocol.VersionRequest(module)));
                transmitters.Add(new(module, version));
            }
            State = new(device, firmware, transmitters, DateTimeOffset.Now);
            Log?.Invoke($"设备确认：RX {firmware}，已连接 {count} 个 TX。");
            return State;
        }
        catch { await CloseInternal(); throw; }
        finally { session.Release(); }
    }

    private void HandleReport(byte[] bytes)
    {
        Log?.Invoke("HID ← " + Convert.ToHexString(bytes));
        lock (pendingGate)
        {
            if (bytes.Length >= 2 && bytes[0] == 0x55 && bytes[1] == expected)
                pending?.TrySetResult(bytes);
        }
    }
    private void HandleDisconnect(string error)
    {
        lock (pendingGate) pending?.TrySetException(new IOException(error));
        State = null;
        Disconnected?.Invoke(error);
    }
    private async Task<byte[]> Query(byte[] report)
    {
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (pendingGate) { expected = report[1]; pending = completion; }
        try
        {
            Log?.Invoke("HID → " + Convert.ToHexString(report.AsSpan(0, 6)));
            await transport!.SendAsync(report);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { throw new IOException("设备信息查询超时；已关闭本次会话，请重新连接。"); }
        finally { lock (pendingGate) pending = null; }
    }
    private async Task CloseInternal()
    {
        State = null;
        if (transport is not null)
        {
            transport.ReportReceived -= HandleReport;
            transport.Disconnected -= HandleDisconnect;
            await transport.DisposeAsync(); transport = null;
        }
    }
    public async ValueTask DisposeAsync()
    {
        await session.WaitAsync();
        try { await CloseInternal(); }
        finally { session.Release(); }
    }
}
