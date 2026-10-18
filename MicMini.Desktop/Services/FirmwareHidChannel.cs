namespace MicMini.Desktop.Services;

/// <summary>Routes control 0x55 via FF90/COL03 and firmware 0x41 via FF82/COL04 of one physical receiver.</summary>
public sealed class FirmwareHidChannel : IFirmwareHidChannel, IAsyncDisposable
{
    private readonly HidTransport control = new();
    private readonly HidTransport data = new();
    public event Action<byte[]>? ReportReceived;
    public event Action<string>? Disconnected;
    public Guid ContainerId { get; }
    private FirmwareHidChannel(HidDeviceInfo controlInfo, HidDeviceInfo dataInfo)
    {
        ContainerId = controlInfo.ContainerId;
        control.ReportReceived += ForwardReport; data.ReportReceived += ForwardReport;
        control.Disconnected += ForwardDisconnect; data.Disconnected += ForwardDisconnect;
        try { control.Open(controlInfo); data.Open(dataInfo); }
        catch
        {
            // Construction happens off the UI thread; both handles must be released on partial failure.
            control.DisposeAsync().AsTask().GetAwaiter().GetResult();
            data.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    public static (HidDeviceInfo Control, HidDeviceInfo Data) Preflight(Guid expectedContainer)
    {
        if (expectedContainer == Guid.Empty) throw new IOException("未取得设备容器标识，不能确认升级接口属于同一接收端。");
        var devices = HidTransport.Enumerate();
        var control = devices.Where(x => x.UsagePage == 0xff90 && x.InputLength == 64 && x.OutputLength == 64).ToArray();
        var data = devices.Where(x => x.UsagePage == 0xff82 && x.InputLength == 64 && x.OutputLength == 64).ToArray();
        if (control.Length != 1 || data.Length != 1) throw new IOException("升级需要唯一的一台 DJI 接收端及其两个 HID 接口。");
        if (control[0].ContainerId != expectedContainer || data[0].ContainerId != expectedContainer)
            throw new IOException("设备已更换，或控制与固件数据接口不属于同一台接收端。");
        if (!HidTransport.DeclaresOutputReport(data[0], 0x41))
            throw new IOException("当前设备没有声明固件数据报告 0x41，不能升级。");
        return (control[0], data[0]);
    }

    public static FirmwareHidChannel Open(Guid expectedContainer)
    {
        var pair = Preflight(expectedContainer);
        return new(pair.Control, pair.Data);
    }
    private void ForwardReport(byte[] report) => ReportReceived?.Invoke(report);
    private void ForwardDisconnect(string error) => Disconnected?.Invoke(error);
    public Task SendAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        if (report.Length != 64) throw new ArgumentException("升级 HID 报告必须为64字节。");
        return report.Span[0] switch
        {
            0x55 => control.SendAsync(report, token),
            0x41 => data.SendAsync(report, token),
            _ => throw new InvalidDataException("未支持的升级报告类型。")
        };
    }
    public async ValueTask DisposeAsync()
    {
        control.ReportReceived -= ForwardReport; data.ReportReceived -= ForwardReport;
        control.Disconnected -= ForwardDisconnect; data.Disconnected -= ForwardDisconnect;
        await control.DisposeAsync().ConfigureAwait(false);
        await data.DisposeAsync().ConfigureAwait(false);
    }
}
