using MicMini.Desktop.Protocol;
using System.Diagnostics;

namespace MicMini.Desktop.Services;

/// <summary>Injectable transport for one exclusive firmware session. SendAsync must honor cancellation and retain its buffer until completion.</summary>
public interface IFirmwareHidChannel
{
    event Action<byte[]>? ReportReceived;
    event Action<string>? Disconnected;
    Task SendAsync(ReadOnlyMemory<byte> report, CancellationToken token);
}

public sealed record FirmwareTransferProgress(long BytesSent, long TotalBytes, string Stage);
public sealed record FirmwareTransferReceipt(long BytesSent, bool DeviceAccepted);

/// <summary>
/// Transfer of an already selected module image. Does not parse packages, establish compatibility,
/// automatically retry commands, reboot a device, or call a positive result an installed update.
/// The caller must verify package/model/module first and re-read the expected version after reconnect.
/// </summary>
public sealed class MicFirmwareTransfer
{
    private readonly IFirmwareHidChannel channel;
    private readonly TimeSpan enterTimeout, flowTimeout, resultTimeout, packetDelay;
    private readonly SemaphoreSlim session = new(1, 1);

    public MicFirmwareTransfer(IFirmwareHidChannel channel, TimeSpan? enterTimeout = null,
        TimeSpan? flowTimeout = null, TimeSpan? resultTimeout = null, TimeSpan? packetDelay = null)
    {
        this.channel = channel;
        this.enterTimeout = enterTimeout ?? TimeSpan.FromSeconds(16);
        this.flowTimeout = flowTimeout ?? TimeSpan.FromSeconds(30);
        this.resultTimeout = resultTimeout ?? TimeSpan.FromMinutes(2);
        this.packetDelay = packetDelay ?? TimeSpan.FromMilliseconds(2);
    }

    public async Task<FirmwareTransferReceipt> TransferModuleAsync(byte module, ReadOnlyMemory<byte> moduleImage,
        IProgress<FirmwareTransferProgress>? progress = null, CancellationToken token = default)
    {
        if (moduleImage.IsEmpty) throw new InvalidDataException("升级模块为空，未发送任何命令。");
        if (moduleImage.Length > FirmwarePackageInspector.MaximumFileBytes) throw new InvalidDataException("升级模块超过大小限制。");
        // Stable content for this entire session, even if the caller owns a mutable byte array.
        var image = moduleImage.ToArray();
        await session.WaitAsync(token).ConfigureAwait(false);
        using var sessionStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var sessionToken = sessionStop.Token;
        var gate = new object();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool paused = true, commandSent = false, enterAccepted = false, resultAccepted = false;
        long submitted = 0;
        Exception? failure = null;
        void Fail(Exception error)
        {
            lock (gate)
            {
                failure ??= error;
                entered.TrySetException(error); result.TrySetException(error); resume.TrySetException(error);
            }
        }
        void Receive(byte[] bytes)
        {
            try
            {
                var report = MicFirmwareProtocol.ParseReport(bytes);
                lock (gate)
                {
                    if (!commandSent || failure is not null) return;
                    switch (report.Kind)
                    {
                        case MicFirmwareReportKind.EnterAck:
                            if (enterAccepted) break;
                            if (report.Accepted == true)
                            {
                                // The SDK starts its data worker paused after entering upgrade.
                                enterAccepted = true; paused = true;
                                resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                                entered.TrySetResult(true);
                            }
                            else Fail(new IOException("设备拒绝进入升级模式。"));
                            break;
                        case MicFirmwareReportKind.FlowControl:
                            if (!enterAccepted) break;
                            if (report.Paused == true)
                            {
                                if (!paused) resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
                                paused = true;
                            }
                            else if (report.Paused == false) { paused = false; resume.TrySetResult(true); }
                            break;
                        case MicFirmwareReportKind.Result:
                            if (report.Accepted != true) Fail(new IOException("设备报告固件升级失败。"));
                            else if (submitted < image.LongLength) Fail(new IOException("固件尚未完整发送，收到提前完成响应，已停止本次升级。"));
                            else { resultAccepted = true; result.TrySetResult(true); }
                            break;
                    }
                }
            }
            catch (InvalidDataException) { /* Unrelated/malformed reports cannot complete a pending operation. */ }
        }
        void Disconnected(string error)
        {
            lock (gate)
            {
                // A successful final result is followed by a link update/reboot in the SDK.
                // Keep its receipt; only the caller's subsequent identity/version check can finish the update.
                if (resultAccepted) return;
            }
            Fail(new IOException("升级通信中断：" + error));
            sessionStop.Cancel();
        }
        bool HasFinalReceipt() { lock (gate) return failure is null && resultAccepted && submitted == image.LongLength; }
        async Task Send(ReadOnlyMemory<byte> report)
        {
            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
            writeTimeout.CancelAfter(MicFirmwareProtocol.NativeWriteTimeoutMilliseconds);
            try { await channel.SendAsync(report, writeTimeout.Token).ConfigureAwait(false); }
            catch (IOException) when (HasFinalReceipt()) { /* Device acknowledged the entire image before its expected link reset. */ }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && HasFinalReceipt()) { }
            catch (OperationCanceledException ex) when (!sessionToken.IsCancellationRequested)
            {
                throw new IOException("固件 HID 写入超时，已停止发送且没有自动重试。", ex);
            }
        }
        channel.ReportReceived += Receive; channel.Disconnected += Disconnected;
        try
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new(0, image.Length, "正在请求进入升级模式"));
            lock (gate) commandSent = true;
            await Send(MicFirmwareProtocol.EnterUpgrade(module)).ConfigureAwait(false);
            await entered.Task.WaitAsync(enterTimeout, sessionToken).ConfigureAwait(false);
            var reportBuffer = new byte[64];
            var offset = 0;
            var progressClock = Stopwatch.StartNew();
            while (offset < image.Length)
            {
                while (true)
                {
                    Task? wait;
                    lock (gate)
                    {
                        if (failure is not null) throw failure;
                        wait = paused ? resume.Task : null;
                    }
                    if (wait is null) break;
                    progress?.Report(new(offset, image.Length, "等待设备接收固件"));
                    await wait.WaitAsync(flowTimeout, sessionToken).ConfigureAwait(false);
                }
                sessionToken.ThrowIfCancellationRequested();
                var count = Math.Min(63, image.Length - offset);
                MicFirmwareProtocol.WriteDataReport(reportBuffer, image.AsSpan(offset, count));
                lock (gate)
                {
                    if (failure is not null) throw failure;
                    if (paused) continue;
                    // The result may be delivered within the final SendAsync callback.
                    submitted = offset + count;
                }
                await Send(reportBuffer).ConfigureAwait(false);
                offset += count;
                if (offset == image.Length || progressClock.ElapsedMilliseconds >= 100)
                {
                    progress?.Report(new(offset, image.Length, "正在传输固件"));
                    progressClock.Restart();
                }
                if (packetDelay > TimeSpan.Zero) await Task.Delay(packetDelay, sessionToken).ConfigureAwait(false);
            }
            progress?.Report(new(image.Length, image.Length, "传输完成，等待设备校验与安装"));
            await result.Task.WaitAsync(resultTimeout, sessionToken).ConfigureAwait(false);
            progress?.Report(new(image.Length, image.Length, "设备已接受固件，仍需重连并核对版本"));
            return new(image.Length, true);
        }
        catch (TimeoutException ex)
        {
            throw new IOException("升级等待超时。没有自动重发、重启或重新开始刷写；需要核对设备状态后再处理。", ex);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && failure is not null)
        {
            throw failure;
        }
        finally
        {
            channel.ReportReceived -= Receive; channel.Disconnected -= Disconnected;
            // Observe secondary failed promises when another wait produced the visible failure.
            if (entered.Task.IsFaulted) _ = entered.Task.Exception;
            if (result.Task.IsFaulted) _ = result.Task.Exception;
            if (resume.Task.IsFaulted) _ = resume.Task.Exception;
            session.Release();
        }
    }
}
