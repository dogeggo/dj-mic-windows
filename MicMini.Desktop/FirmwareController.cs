using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using MicMini.Desktop.Protocol;
using MicMini.Desktop.Services;

namespace MicMini.Desktop;

public sealed partial class AppController
{
    private sealed record FirmwareDevice(int ProductType, uint Selector, byte HidModule, string Product,
        string Serial, string Version, string ReceiverSerial, Guid Container)
    {
        public DjiFirmwareTarget Target => DjiFirmwareTarget.ForProductType(ProductType);
        public string Label => $"{Product} · {(Selector == 0 ? "接收端" : "发射端")} · {Version}";
    }
    public bool FirmwareInstalling { get; private set; }

    private List<FirmwareDevice> FirmwareDevices()
    {
        var result = new List<FirmwareDevice>();
        var session = bulk; var info = device.State;
        if (session?.IsOpen != true || session.Receiver is null || info is null) return result;
        var ids = session.Identities;
        if (!ids.TryGetValue(0, out var rx) || string.IsNullOrEmpty(rx.SerialNumber)) return result;
        void Add(uint selector, byte module, bool isRx)
        {
            if (!ids.TryGetValue(selector, out var id) || string.IsNullOrEmpty(id.SerialNumber) || string.IsNullOrEmpty(id.FirmwareVersion)) return;
            var product = (id.ProductName, isRx) switch
            {
                ("DJI Mic Mini", true) => 0xEE, ("DJI Mic Mini", false) => 0xED,
                ("DJI Mic Mini 2", true) => 0x140, ("DJI Mic Mini 2", false) => 0x141,
                _ => 0
            };
            if (product != 0) result.Add(new(product, selector, module, id.ProductName!, id.SerialNumber,
                id.FirmwareVersion, rx.SerialNumber, info.Device.ContainerId));
        }
        Add(0, 0, true);
        // This attached configuration has one TX. Do not guess a HID ordinal from a multi-TX selector mask.
        if (info.Transmitters.Count == 1 && session.Transmitters.Count == 1 && session.Transmitters.ContainsKey(1))
            Add(1, info.Transmitters[0].Module, false);
        return result;
    }

    private async Task OpenFirmwareAsync()
    {
        if (audio.IsRecording) { WriteLog("请先停止并保存录音，再打开固件升级。"); return; }
        if (!await operations.WaitAsync(0)) return;
        var reconnect = false;
        try
        {
            var targets = FirmwareDevices();
            if (targets.Count == 0) { WriteLog("尚未取得受支持设备的完整身份，暂不能升级。请重新连接设备。"); return; }
            var dialog = new FirmwareWindow(window, targets.Select(x => x.Label).ToArray());
            using var lifetime = new CancellationTokenSource();
            using var client = new DjiFirmwareCatalogClient();
            var assets = new List<DjiFirmwareAsset>();
            DjiFirmwareContainer? imported = null;
            FirmwareFileInspection? inspection = null;
            var working = false;
            FirmwareDevice Selected() => targets[Math.Clamp(dialog.SelectedTarget, 0, targets.Count - 1)];
            void Log(string message) { dialog.AppendLog(message); WriteLog("固件：" + message); }
            void Identity()
            {
                var selected = Selected();
                dialog.SetIdentity($"当前固件：{selected.Version}\n序列号：{selected.Serial}");
                imported = null; inspection = null; assets.Clear();
                dialog.SetAssets([]); dialog.SetDownloadAvailable(false); dialog.SetPackage("尚未选择固件。", false);
                dialog.SetStatus("可以检查官方服务或校验本地文件；当前尚无此设备的已验证官方固件候选，暂不能安装。");
            }
            string Eligibility()
            {
                if (imported is null || inspection is null) return "请先下载或选择固件。";
                var selected = Selected();
                if (imported.SelectedModule.ProductType != selected.ProductType) return "固件模块与目标设备不匹配。";
                if (imported.ContainerSha256 != inspection.Sha256) return "文件内容发生变化，请重新选择。";
                var official = assets.Any(x => x.ProductId == selected.Target.Model && x.ExpectedLength == inspection.Length
                    && string.Equals(x.Md5, inspection.Md5, StringComparison.OrdinalIgnoreCase));
                if (!official) return "文件结构与完整性已校验，但未与本次官方清单的文件摘要匹配，暂不能安装。";
                if (!Version.TryParse(selected.Version, out var current) || !Version.TryParse(imported.SelectedModule.FirmwareVersion, out var next))
                    return "无法准确比较设备版本与固件模块版本。";
                if (current.Major > 2) return "当前主版本超出已恢复的 USB 升级范围。";
                if (next == current) return "设备已是所选模块版本，无需重复安装。";
                if (next < current) return "所选模块版本低于当前设备版本，已禁止降级。";
                if (selected.Container == Guid.Empty) return "无法识别物理接收端，暂不能安装。";
                return "";
            }
            void UpdatePackage()
            {
                if (imported is null || inspection is null) return;
                var reason = Eligibility();
                dialog.SetPackage($"{Path.GetFileName(inspection.Path)}\n目标模块版本：{imported.SelectedModule.FirmwareVersion}\n文件大小：{inspection.Length:N0} 字节\nSHA-256：{inspection.Sha256}", reason.Length == 0);
                dialog.SetStatus(reason.Length == 0 ? "官方文件摘要、设备型号和模块完整性检查通过，可以开始升级。" : reason);
            }
            async Task Work(Func<Task> action)
            {
                if (working || lifetime.IsCancellationRequested) return;
                working = true; dialog.SetBusy(true);
                try { await action(); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    if (!lifetime.IsCancellationRequested) { dialog.SetStatus("未完成：" + ex.Message); Log(ex.Message); }
                }
                finally { working = false; if (!lifetime.IsCancellationRequested) dialog.SetBusy(false); }
            }
            async Task Inspect(string path)
            {
                imported = null; inspection = null; dialog.SetPackage("正在校验所选文件…", false);
                var selected = Selected();
                var examined = await FirmwarePackageInspector.InspectAsync(path, lifetime.Token);
                if (examined.Length > DjiFirmwareContainer.MaximumFileLength) throw new InvalidDataException("该文件超过已恢复固件容器的大小上限。");
                var bytes = await File.ReadAllBytesAsync(path, lifetime.Token);
                var container = await Task.Run(() => DjiFirmwareContainer.Parse(bytes, selected.Target), lifetime.Token);
                if (container.ContainerSha256 != examined.Sha256) throw new InvalidDataException("文件在校验期间发生变化，未接受该固件。");
                imported = container; inspection = examined;
                Log($"文件完整性检查完成：{Path.GetFileName(path)}；型号 {container.Model}；模块版本 {container.SelectedModule.FirmwareVersion}。");
                UpdatePackage();
            }
            dialog.TargetChanged += _ => Identity();
            dialog.AssetChanged += _ => { imported = null; inspection = null; dialog.SetPackage("请选择下载官方固件，下载后将校验型号与模块。", false); };
            dialog.CheckRequested += async () => await Work(async () =>
            {
                assets.Clear(); dialog.SetAssets([]); dialog.SetDownloadAvailable(false);
                if (imported is not null) UpdatePackage();
                dialog.SetStatus("正在请求官方固件清单…");
                var catalog = await client.GetCatalogAsync(Selected().Target.Model, lifetime.Token);
                assets.AddRange(catalog.Releases.SelectMany(r => r.Assets).Where(a => a.ProductId == Selected().Target.Model));
                dialog.SetAssets(assets.Select(x => $"{x.Version} · {x.FileName}").ToArray());
                dialog.SetDownloadAvailable(assets.Count > 0);
                Log($"官方清单查询完成，返回 {catalog.Releases.Count} 个版本、{assets.Count} 个文件。");
                dialog.SetStatus(assets.Count == 0 ? "官方接口未返回可下载文件，无法据此确认当前设备是否为最新版本。" : "请选择官方文件并下载；下载后还会核对设备型号与模块版本。");
                UpdatePackage();
            });
            dialog.FileSelected += async path => await Work(() => Inspect(path));
            dialog.DownloadRequested += async () => await Work(async () =>
            {
                if (dialog.SelectedAsset < 0 || dialog.SelectedAsset >= assets.Count) return;
                var asset = assets[dialog.SelectedAsset];
                var folder = Path.Combine(dataDirectory, "Firmware", "Downloads"); Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(asset.FileName));
                dialog.SetPackage("正在下载并核验官方固件…", false);
                await client.DownloadAsync(asset, path, new Progress<long>(bytes =>
                {
                    if (!lifetime.IsCancellationRequested) dialog.SetStatus($"下载官方固件：{bytes:N0} / {asset.ExpectedLength:N0} 字节", asset.ExpectedLength > 0 ? 100d * bytes / asset.ExpectedLength : 0);
                }), lifetime.Token);
                await Inspect(path);
            });
            dialog.InstallRequested += async () => await Work(async () =>
            {
                var reason = Eligibility();
                if (reason.Length > 0) { dialog.SetStatus(reason); return; }
                var selected = Selected(); var image = imported!; var file = inspection!;
                var confirmation = $"目标：{selected.Label}\n序列号：{selected.Serial}\n版本：{selected.Version} → {image.SelectedModule.FirmwareVersion}\n\n升级期间将暂停售用音频输入。请保持供电与USB连接，开始后等待设备重连和版本确认。\n\n是否开始升级？";
                if (MessageBox.Show(dialog, confirmation, "确认固件升级", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                // This is the only workflow path which may create the physical writing transport.
                reconnect = true;
                // Protect the entire confirmed operation, including asynchronous preflight.
                FirmwareInstalling = true; busy = true;
                try
                {
                    dialog.SetBusy(true, true); Render();
                    await InstallFirmwareAsync(selected, image, file, dialog, Log);
                    imported = null; inspection = null;
                    dialog.SetPackage("本次升级操作已结束。重新打开此窗口可读取最新设备信息。", false);
                }
                finally { FirmwareInstalling = false; busy = false; dialog.SetBusy(false); Render(); }
            });
            dialog.Closed += (_, _) => lifetime.Cancel();
            Identity(); dialog.ShowDialog();
        }
        catch (Exception ex) { WriteLog("固件升级窗口：" + ex.Message); }
        finally
        {
            operations.Release();
            if (reconnect && !disposed) await RefreshAsync();
        }
    }

    private async Task InstallFirmwareAsync(FirmwareDevice target, DjiFirmwareContainer container,
        FirmwareFileInspection file, FirmwareWindow dialog, Action<string> log)
    {
        if (audio.IsRecording) throw new InvalidOperationException("请先停止录音。");
        var fresh = FirmwareDevices().SingleOrDefault(x => x.Selector == target.Selector);
        if (fresh != target) throw new IOException("设备身份或版本已变化，请重新打开升级窗口。");
        await Task.Run(() => FirmwareHidChannel.Preflight(target.Container));
        var module = container.SelectedModule;
        var bytes = module.FirmwareData.ToArray();
        if (!string.Equals(Convert.ToHexString(MD5.HashData(bytes)), module.OriginalMd5, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("模块数据在检查后发生变化，未发送升级命令。");
        var transactionDirectory = Path.Combine(dataDirectory, "Firmware", "Transactions");
        Directory.CreateDirectory(transactionDirectory);
        var transactionPath = Path.Combine(transactionDirectory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".json");
        void Save(string stage, string? error = null, bool required = false)
        {
            try
            {
                var text = JsonSerializer.Serialize(new { UpdatedAtUtc = DateTimeOffset.UtcNow, Target = target,
                    ExpectedVersion = module.FirmwareVersion, PackageSha256 = file.Sha256, File = file.Path, Stage = stage, Error = error }, new JsonSerializerOptions { WriteIndented = true });
                var temp = transactionPath + ".tmp"; File.WriteAllText(temp, text); File.Move(temp, transactionPath, true);
            }
            catch (Exception ex) { if (required) throw; log("无法保存升级记录：" + ex.Message); }
        }
        Save("prepared", required: true);
        try
        {
            await Task.Run(audio.StopMonitor); audioStatus = "固件升级期间暂停音频输入";
            if (bulk is not null) { await bulk.DisposeAsync(); bulk = null; }
            // Read complete identities in a new connection after confirmation. A status-only
            // replacement TX must never inherit a serial number from the previous session.
            await using var identitySession = new MicBulkSession();
            await identitySession.ConnectAndQueryCapabilitiesAsync();
            var identityWait = Stopwatch.StartNew();
            while (!FirmwareIdentityMatches(identitySession, target))
            {
                if (identityWait.Elapsed > TimeSpan.FromSeconds(3))
                    throw new IOException("升级前未重新读取到同一设备的完整身份，未发送升级命令。请重新打开升级窗口。");
                await Task.Delay(100);
            }
            await device.DisposeAsync();
            await using (var channel = await Task.Run(() => FirmwareHidChannel.Open(target.Container)))
            {
                // Read the selected module again on the exclusive control handle before the first state-changing command.
                var version = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnReport(byte[] report)
                {
                    try { var decoded = MicFirmwareProtocol.ParseReport(report); if (decoded.Kind == MicFirmwareReportKind.Version && decoded.Version is not null) version.TrySetResult(decoded.Version); }
                    catch (InvalidDataException) { }
                }
                channel.ReportReceived += OnReport;
                try
                {
                    await channel.SendAsync(MicFirmwareProtocol.GetVersion(target.HidModule), CancellationToken.None);
                    var current = await version.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    if (current != target.Version) throw new IOException("设备版本在开始前发生变化，已中止升级。");
                }
                finally { channel.ReportReceived -= OnReport; }
                if (!FirmwareIdentityMatches(identitySession, target))
                    throw new IOException("设备在升级前断开或身份发生变化，未发送升级命令。");
                await identitySession.DisposeAsync();
                Save("entering"); log($"开始升级 {target.Product}，目标模块版本 {module.FirmwareVersion}。");
                var lastStage = ""; var transferring = true;
                var reporter = new Progress<FirmwareTransferProgress>(p =>
                {
                    if (!transferring) return;
                    dialog.SetStatus(p.Stage, 5 + 80d * p.BytesSent / p.TotalBytes);
                    if (p.Stage != lastStage) { lastStage = p.Stage; log(p.Stage); Save(p.Stage); }
                });
                try { await new MicFirmwareTransfer(channel).TransferModuleAsync(target.HidModule, bytes, reporter); }
                finally { transferring = false; }
            }
            Save("awaiting_reconnect"); dialog.SetStatus("设备已接受固件，正在等待重连并核对版本…", 90);
            log("开始核验同一设备的身份和安装后的实际版本。");
            await VerifyFirmwareAfterRestartAsync(target, module.FirmwareVersion);
            Save("verified"); dialog.SetStatus($"升级完成，已确认 {target.Product} 的实际版本为 {module.FirmwareVersion}。", 100);
            log("升级完成，设备身份与目标版本均已重新读取确认。");
        }
        catch (Exception ex)
        {
            Save("unconfirmed", ex.Message);
            dialog.SetStatus("升级未确认：" + ex.Message);
            log("升级未确认。记录已保存至 " + transactionPath);
        }
    }

    private static bool FirmwareIdentityMatches(MicBulkSession session, FirmwareDevice target)
    {
        // Identities is one atomic snapshot containing only devices with fresh live membership.
        var identities = session.Identities;
        return identities.TryGetValue(0, out var receiver) && receiver.SerialNumber == target.ReceiverSerial &&
            identities.TryGetValue(target.Selector, out var identity) && identity.SerialNumber == target.Serial &&
            identity.ProductName == target.Product && identity.FirmwareVersion == target.Version &&
            (target.Selector == 0 || (session.Transmitters.Count == 1 && session.Transmitters.ContainsKey(target.Selector)));
    }

    private static async Task VerifyFirmwareAfterRestartAsync(FirmwareDevice target, string expectedVersion)
    {
        var timer = Stopwatch.StartNew();
        string last = "设备尚未重新出现";
        while (timer.Elapsed < TimeSpan.FromMinutes(2))
        {
            await Task.Delay(target.HidModule == 0 ? 2000 : 3000);
            await using var info = new MicDeviceService();
            await using var parameters = new MicBulkSession();
            try
            {
                var state = await info.ConnectAndReadAsync();
                if (state.Device.ContainerId != target.Container) { last = "连接的接收端身份不同"; continue; }
                var version = target.HidModule == 0 ? state.Firmware : state.Transmitters.SingleOrDefault(x => x.Module == target.HidModule)?.Firmware;
                if (version != expectedVersion) { last = "当前读取版本：" + (version ?? "发射端未连接"); continue; }
                await parameters.ConnectAndQueryCapabilitiesAsync();
                if (!parameters.Identities.TryGetValue(0, out var rx) || rx.SerialNumber != target.ReceiverSerial)
                { last = "接收端序列号尚未核实"; continue; }
                if (!parameters.Identities.TryGetValue(target.Selector, out var identity) || identity.SerialNumber != target.Serial
                    || identity.ProductName != target.Product || identity.FirmwareVersion != expectedVersion)
                { last = "目标设备身份或版本尚未核实"; continue; }
                return;
            }
            catch (Exception ex) { last = ex.Message; }
        }
        throw new IOException("重连后未能确认目标版本；" + last + "。请保持设备供电并检查设备状态。");
    }
}
