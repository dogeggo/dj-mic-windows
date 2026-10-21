using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using MicMini.Desktop.Protocol;
using MicMini.Desktop.Services;

namespace MicMini.Desktop;

public sealed partial class AppController : IAsyncDisposable
{
    public event Action<AppSnapshot>? SnapshotUpdated;
    private sealed record Binding(uint Selector, ushort Type, byte[] Values, Func<byte, bool> Matches);
    private readonly MainWindow window;
    private readonly MicDeviceService device = new();
    private MicBulkSession? bulk;
    private readonly AudioCapture audio = new();
    private readonly DispatcherTimer timer;
    private readonly Queue<string> logs = new();
    private readonly Dictionary<string, Binding> bindings = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly string dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicMiniDesktop");
    private readonly string recordingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicMiniDesktop", "Recordings");
    private string connectionTitle = "正在检测 DJI Mic";
    private string connectionDetail = "连接接收端后，读取设备信息、参数状态和 USB 音频输入。";
    private string audioStatus = "正在检测 USB 音频输入";
    private string lastAction = "";
    private bool busy, disposed;
    private IReadOnlyList<HidDeviceInfo> hidDevices = [];
    private IReadOnlyList<AudioInputDevice> audioDevices = [];
    private static readonly string[] Toggle = ["关闭", "开启"];

    public AppController(MainWindow window)
    {
        this.window = window;
        device.Log += WriteLog;
        device.Disconnected += error => window.Dispatcher.BeginInvoke(() => { WriteLog("设备信息通道断开：" + error); Render(); });
        audio.Error += error => window.Dispatcher.BeginInvoke(() => { audioStatus = "音频错误：" + error; WriteLog(audioStatus); Render(); });
        window.ConnectRequested += async () => await RefreshAsync();
        window.RefreshRequested += async () => await RefreshAsync();
        window.DisconnectRequested += async () =>
        {
            await operations.WaitAsync();
            try
            {
                if (bulk is not null) { await bulk.DisposeAsync(); bulk = null; }
                await device.DisposeAsync(); connectionTitle = "设备调参已断开";
                connectionDetail = "USB 音频电平仍可查看。请重新连接并确认发射端在线后录音。";
                WriteLog("已断开设备调参，录音暂停使用。"); Render();
            }
            finally { operations.Release(); }
        };
        window.StartRecordingRequested += () =>
        {
            try
            {
                if (disposed) return;
                if (busy) throw new InvalidOperationException("正在与设备通信，请稍后开始录音。");
                if (bulk?.HasConnectedTransmitter != true)
                    throw new InvalidOperationException("未确认发射端在线，请连接发射端后再录音。");
                WriteLog("开始电脑录音：" + audio.StartRecording(recordingDirectory));
            }
            catch (Exception ex) { WriteLog("无法录音：" + ex.Message); }
            Render();
        };
        window.StopRecordingRequested += () =>
        {
            try { audio.StopRecording(); WriteLog("录音已保存：" + audio.RecordingPath); }
            catch (Exception ex) { WriteLog("保存录音失败：" + ex.Message); }
            Render();
        };
        window.OpenRecordingsRequested += () =>
        {
            Directory.CreateDirectory(recordingDirectory);
            Process.Start(new ProcessStartInfo(recordingDirectory) { UseShellExecute = true });
        };
        window.ExportDiagnosticsRequested += ExportDiagnostics;
        window.FirmwareRequested += async () => await OpenFirmwareAsync();
        window.SettingRequested += async (key, index) => await SetParameterAsync(key, index);
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Render(); timer.Start(); Render();
    }

    public Task InitializeAsync() => RefreshAsync();

    public async Task RefreshAsync()
    {
        if (!await operations.WaitAsync(0)) return;
        busy = true; Render();
        try
        {
            hidDevices = await Task.Run(HidTransport.Enumerate);
            audioDevices = await Task.Run(AudioCapture.EnumerateDevices);
            var candidates = audioDevices.Where(x => x.Name.Contains("Wireless Mic Rx", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("DJI Mic", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!audio.IsMonitoring)
            {
                if (candidates.Length == 1)
                {
                    try
                    {
                        await Task.Run(() => audio.StartMonitor(candidates[0].Id));
                        audioStatus = $"{candidates[0].Name} · {audio.SampleRate / 1000.0:0.#} kHz / {audio.Channels} 声道 / PCM 16-bit";
                        WriteLog("USB 音频已打开：" + audioStatus);
                    }
                    catch (Exception ex) { audioStatus = "无法打开 USB 音频：" + ex.Message; WriteLog(audioStatus); }
                }
                else audioStatus = candidates.Length == 0 ? "未检测到 DJI USB 音频输入。" : "检测到多个 DJI 音频输入，请只保留目标接收端。";
            }
            try { await device.ConnectAndReadAsync(); }
            catch (Exception ex) { WriteLog("HID 设备信息：" + ex.Message); }
            if (bulk is not null) { await bulk.DisposeAsync(); bulk = null; }
            try
            {
                var next = new MicBulkSession(); next.Log += WriteLog; bulk = next;
                await next.ConnectAndQueryCapabilitiesAsync();
                connectionTitle = "设备参数已连接";
                connectionDetail = "参数来自设备实时回读。每次修改均等待设备确认与状态更新。";
                WriteLog("WinUSB 参数通道已就绪，能力与状态均已读取。");
            }
            catch (Exception ex)
            {
                if (bulk is not null) { await bulk.DisposeAsync(); bulk = null; }
                connectionTitle = device.State is null ? "设备连接未完成" : "设备信息已连接";
                connectionDetail = ex.Message;
                WriteLog("参数通道：" + ex.Message);
            }
        }
        catch (Exception ex) { connectionDetail = ex.Message; WriteLog("检测失败：" + ex.Message); }
        finally { busy = false; operations.Release(); Render(); }
    }

    private async Task SetParameterAsync(string key, int index)
    {
        if (!bindings.TryGetValue(key, out var binding) || index < 0 || index >= binding.Values.Length || bulk is null) return;
        if (!await operations.WaitAsync(0)) return;
        busy = true; lastAction = "正在设置，等待设备确认…"; Render();
        var session = bulk;
        try
        {
            var value = binding.Values[index];
            await session.SetValueAsync(binding.Selector, binding.Type, value, () => binding.Matches(value));
            lastAction = "设置已生效，已通过设备回读确认。";
            WriteLog(key + "：设置成功并已回读。");
        }
        catch (Exception ex) { lastAction = "设置未确认：" + ex.Message; WriteLog(key + "：" + lastAction); }
        finally { busy = false; operations.Release(); Render(); }
    }

    private IReadOnlyList<SettingItem> Settings()
    {
        bindings.Clear(); var items = new List<SettingItem>(); var session = bulk;
        var rx = session?.Receiver;
        if (session?.IsOpen != true || rx is null)
        {
            foreach (var (key, title) in new[] { ("noise", "降噪"), ("channel", "声道模式"), ("safety", "安全音轨"), ("lowcut", "低切"), ("limiter", "自动防爆音"), ("led", "发射端指示灯"), ("power", "电源与按键") })
                items.Add(new(key, title, "连接设备后读取当前参数。", Toggle, null, false, "等待参数通道"));
            return items;
        }
        void Add(string key, string title, string description, uint selector, ushort type, string[] labels, byte[] values, int current, Func<byte, bool> matches, bool enabled = true, string? availability = null)
        {
            var index = Array.FindIndex(values, v => v == unchecked((byte)current));
            items.Add(new(key, title, description, labels, index >= 0 ? index : null, enabled, availability ?? "设备状态已读取 · 修改后等待回读确认"));
            if (enabled) bindings[key] = new(selector, type, values, matches);
        }
        void RxBool(string key, string title, string description, ushort type, Func<MicV3ReceiverState, bool?> get)
        {
            var current = get(rx);
            Add(key, title, description, 0, type, Toggle, [0, 1], current == true ? 1 : current == false ? 0 : -1,
                v => session.Receiver is { } rx && get(rx) == (v != 0), current.HasValue);
        }
        Add("rx:channel", "声道模式", "安全音轨开启时设备使用单声道；所有联动变化均会重新显示。", 0, 8,
            ["单声道", "立体声"], [0, 2], rx.ChannelRaw, v => session.Receiver?.ChannelRaw == v);
        RxBool("rx:safety", "安全音轨", "启用会自动切换为单声道，保留较低电平的备份音轨。", 0x21, r => r.SafetyTrack);
        RxBool("rx:limiter", "自动防爆音", "抑制突发大音量引起的失真。", 0x1e, r => r.AutomaticLimiter);
        var gain = session.Capabilities?.GainRange(0);
        if (gain is not null)
        {
            var values = Enumerable.Range(0, (gain.Maximum - gain.Minimum) / gain.Step + 1).Select(i => gain.Minimum + i * gain.Step).ToArray();
            Add("rx:gain", "接收端增益", "范围与步长由设备能力查询返回。", 0, 0x39,
                values.Select(v => $"{v:+0;-0;0} dB").ToArray(), values.Select(v => unchecked((byte)v)).ToArray(), rx.GainRaw,
                v => session.Receiver?.GainRaw == unchecked((sbyte)v));
        }
        RxBool("rx:camera", "跟随相机开关机", "接收端跟随相机电源状态。", 0x20, r => r.CameraPowerSync);
        RxBool("rx:autooff", "接收端超时关机", "按设备规则在未连接时自动关机。", 0x10, r => r.AutoShutdown);
        foreach (var (selector, tx) in session.Transmitters.OrderBy(x => x.Key))
        {
            var prefix = $"tx:{selector}:"; var label = $"TX {selector}";
            bool Match(Func<MicV3TransmitterState, bool> test) => session.Transmitters.TryGetValue(selector, out var now) && test(now);
            void TxBool(string key, string title, string description, ushort type, bool current, Func<MicV3TransmitterState, bool> get)
                => Add(prefix + key, label + " · " + title, description, selector, type, Toggle, [0, 1], current ? 1 : 0, v => Match(t => get(t) == (v != 0)));
            TxBool("denoise", "降噪", "降低环境噪声。降噪强度在下方单独选择。", 0x0e, tx.Denoise, t => t.Denoise);
            var noiseMask = session.Capabilities?.EnumMask(selector, 3);
            if (noiseMask is not null)
                Add(prefix + "noiselevel", label + " · 降噪等级", "两个等级均已实机验证；与安卓版强/弱名称的对应尚待核对。", selector, 0x1d,
                    ["等级 1", "等级 2"], [0, 1], tx.DenoiseLevelRaw, v => Match(t => t.DenoiseLevelRaw == v), tx.Denoise);
            TxBool("lowcut", "低切", "减少低频风噪与环境震动。", 3, tx.LowCut, t => t.LowCut);
            var ledMask = session.Capabilities?.EnumMask(selector, 4);
            if (ledMask is not null)
                Add(prefix + "led", label + " · 指示灯", "开启或关闭发射端指示灯，亮灭状态已通过实机确认。", selector, 0x0a,
                    ["开启", "关闭"], [0, 2], tx.LedRaw, v => Match(t => t.LedRaw == v));
            TxBool("button", "电源键开关降噪", "允许用发射端电源键切换降噪。", 0x0f, tx.DenoiseButton, t => t.DenoiseButton);
            TxBool("autooff", "超时自动关机", "按设备规则在未连接时自动关机。", 0x10, tx.AutoShutdown, t => t.AutoShutdown);
        }
        return items;
    }

    private AppSnapshot Snapshot()
    {
        var info = device.State; var session = bulk; var ready = session?.IsOpen == true && session.Receiver is not null;
        var ids = session?.Identities;
        var identity = ids?.GetValueOrDefault(0u);
        List<DeviceItem> devices = [];
        if (ready)
        {
            devices.Add(new(identity?.ProductName ?? "DJI 接收端", "RX", "参数在线", $"固件 {identity?.FirmwareVersion ?? info?.Firmware} · SN {identity?.SerialNumber ?? "未读取"} · 名称 {identity?.DeviceName ?? "未读取"}"));
            foreach (var (selector, tx) in session!.Transmitters.OrderBy(x => x.Key))
            {
                var txInfo = ids?.GetValueOrDefault(selector);
                devices.Add(new(txInfo?.ProductName ?? $"发射端 {selector}", $"TX {selector}", "已连接",
                    $"固件 {txInfo?.FirmwareVersion ?? "未读取"} · SN {txInfo?.SerialNumber ?? "未读取"} · 名称 {txInfo?.DeviceName ?? "未读取"}",
                    new BatteryItem(tx.BatteryLevelRaw, tx.Charging)));
            }
        }
        else if (info is not null)
        {
            devices.Add(new("DJI 接收端", "RX", "信息已响应", "固件 " + info.Firmware + " · USB HID"));
            devices.AddRange(info.Transmitters.Select((tx, i) => new DeviceItem($"发射端 {i + 1}", "TX", "接收端确认在线", "固件 " + tx.Firmware, new BatteryItem(null, false))));
        }
        var title = ready ? (identity?.ProductName ?? "DJI Mic") + " 已连接" : connectionTitle;
        var detail = ready ? "USB 参数控制在线 · " + (string.IsNullOrEmpty(lastAction) ? "所有参数均来自设备回读。" : lastAction) : connectionDetail;
        return new(title, detail, ready || info is not null, audioStatus, audio.IsMonitoring, audio.IsRecording,
            audio.PeakLeft, audio.PeakRight, audio.RecordedDuration.ToString(@"hh\:mm\:ss"), audio.RecordingPath ?? recordingDirectory,
            identity?.FirmwareVersion ?? info?.Firmware ?? "未读取", identity?.SerialNumber ?? (info is null ? "未读取" : info.Device.UsbSerial + "（USB 描述符）"),
            devices, Settings(), logs.ToArray(), busy, ready, session?.HasConnectedTransmitter == true);
    }
    private void Render()
    {
        if (disposed) return;
        var snapshot = Snapshot();
        if (snapshot.IsRecording && !snapshot.TransmitterConnected)
        {
            try
            {
                audio.StopRecording();
                WriteLog("发射端已断开或在线状态失效，录音已自动停止并保存：" + audio.RecordingPath);
            }
            catch (Exception ex) { WriteLog("发射端离线，停止并保存录音失败：" + ex.Message); }
            snapshot = Snapshot();
        }
        window.Render(snapshot);
        SnapshotUpdated?.Invoke(snapshot);
    }
    private void WriteLog(string text)
    {
        if (!window.Dispatcher.CheckAccess()) { window.Dispatcher.BeginInvoke(() => WriteLog(text)); return; }
        logs.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}"); while (logs.Count > 180) logs.Dequeue();
    }
    private void ExportDiagnostics()
    {
        try
        {
            var folder = Path.Combine(dataDirectory, "Diagnostics"); Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"mic-mini-{DateTime.Now:yyyyMMdd-HHmmss-fff}.json");
            var data = new { CreatedAt = DateTimeOffset.Now, ApplicationVersion = typeof(AppController).Assembly.GetName().Version?.ToString(), Os = Environment.OSVersion.ToString(), Receiver = device.State,
                HidDevices = hidDevices, AudioDevices = audioDevices, Identities = bulk?.Identities, Parameters = bulk?.Receiver, Transmitters = bulk?.Transmitters,
                Capabilities = bulk?.Capabilities?.Devices, Snapshot = Snapshot() };
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            WriteLog("诊断已导出：" + path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { WriteLog("导出诊断失败：" + ex.Message); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; timer.Stop();
        await operations.WaitAsync();
        try { if (bulk is not null) await bulk.DisposeAsync(); await device.DisposeAsync(); await Task.Run(audio.Dispose); }
        finally { operations.Release(); }
    }
}
