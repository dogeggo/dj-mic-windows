using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using MicMini.Desktop.Protocol;

namespace MicMini.Desktop.Services;

/// <summary>One receiver connection. Commands require matching ACKs and newer live state.
/// Reconnect with a new session. Events never execute on the USB reader or under state locks.</summary>
public sealed class MicBulkSession : IAsyncDisposable
{
    private static readonly TimeSpan StateLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private readonly WinUsbTransport transport = new();
    private readonly DumlStreamDecoder decoder = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new(), disposeGate = new(), logGate = new();
    private readonly Dictionary<uint, MicV3IdentityState> identities = [];
    private readonly Dictionary<uint, MicV3TransmitterState> transmitters = [];
    private readonly Dictionary<uint, long> transmitterTimes = [], targetRevisions = [];
    private readonly Queue<string> logQueue = new();
    private readonly Timer expiryTimer;
    private MicV3ReceiverState? receiver;
    private MicCapabilitySet? capabilities;
    private TaskCompletionSource<bool> statePulse = NewPulse();
    private PendingRequest? pending;
    private Task? disposal;
    private long receiverTime, revision, disconnectedGeneration, stateNoticeVersion, lastMalformedLog;
    private int noticeQueued, logQueued, sequence;
    private bool connected, started, disposed;

    public event Action<string>? Log;
    public event Action? StateChanged;
    public bool IsOpen { get { lock (gate) return !disposed && connected && transport.IsOpen; } }
    public IReadOnlyDictionary<uint, MicV3IdentityState> Identities
    {
        get
        {
            lock (gate)
            {
                var snapshot = new Dictionary<uint, MicV3IdentityState>();
                if (ReceiverIsFreshLocked())
                    foreach (var (selector, identity) in identities)
                        if (selector == 0 || ((receiver!.ConnectedTransmittersMask.GetValueOrDefault() & selector) == selector &&
                            IsFresh(transmitterTimes.GetValueOrDefault(selector)))) snapshot.Add(selector, identity);
                return new ReadOnlyDictionary<uint, MicV3IdentityState>(snapshot);
            }
        }
    }
    public IReadOnlyDictionary<uint, MicV3TransmitterState> Transmitters
    {
        get
        {
            lock (gate)
            {
                var snapshot = new Dictionary<uint, MicV3TransmitterState>();
                if (ReceiverIsFreshLocked() && receiver!.ConnectedTransmittersMask is uint mask)
                    foreach (var (selector, value) in transmitters)
                        if ((selector & mask) == selector && IsFresh(transmitterTimes.GetValueOrDefault(selector))) snapshot.Add(selector, value);
                return new ReadOnlyDictionary<uint, MicV3TransmitterState>(snapshot);
            }
        }
    }
    public MicV3ReceiverState? Receiver { get { lock (gate) return ReceiverIsFreshLocked() ? receiver : null; } }
    public MicCapabilitySet? Capabilities { get { lock (gate) return connected && !disposed ? capabilities : null; } }

    public MicBulkSession()
    {
        transport.DataReceived += Received;
        transport.Disconnected += Disconnected;
        expiryTimer = new Timer(ExpireState, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
    }

    public async Task ConnectAndQueryCapabilitiesAsync()
    {
        await operations.WaitAsync(lifetime.Token).ConfigureAwait(false);
        var ownsAttempt = false;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (started) throw new InvalidOperationException("该会话已尝试连接；请创建新会话后重试。");
                started = true;
                ownsAttempt = true;
            }
            var interfaces = WinUsbTransport.Enumerate();
            if (interfaces.Count != 1) throw new IOException("需要唯一的 Mic Mini MI_06 WinUSB 接口。");
            // Ready the receive path before Open starts its native read thread.
            lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); connected = true; }
            transport.Open(interfaces[0]);
            WriteLog("已打开 Mic Mini MI_06 控制通道，正在查询设备能力。");
            using (var queryTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
            {
                queryTimeout.CancelAfter(OperationTimeout);
                DumlFrame reply;
                try
                {
                    ushort next;
                    lock (gate) next = NextSequenceLocked();
                    reply = await ExchangeAsync(MicMiniCommands.CapabilityQuery(next), queryTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
                { throw new TimeoutException("Mic Mini 能力查询在 3 秒内没有收到匹配响应。"); }
                // 5B/12 begins with a uint16 body length, NOT an ACK status byte.
                var parsed = MicCapabilitySet.Decode(reply.Payload);
                lock (gate) { EnsureConnectedLocked(); capabilities = parsed; }
                WriteLog($"设备能力查询完成，响应包含 {parsed.Devices.Count} 个设备选择器。");
            }
            using var stateTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            stateTimeout.CancelAfter(OperationTimeout);
            try { await WaitForInitialStateAsync(stateTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            { throw new TimeoutException("已连接控制通道，但 3 秒内未获得接收器及已连接发射器的完整新鲜状态。"); }
            WriteLog("设备能力与在线状态已就绪，可以回读确认参数修改。");
            QueueStateChanged();
        }
        catch
        {
            if (ownsAttempt)
            {
                Invalidate("Mic Mini 控制会话未完成初始化。");
                await transport.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally { operations.Release(); }
    }

    public async Task SetValueAsync(uint selector, ushort type, byte value, Func<bool> readbackMatches, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(readbackMatches);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Dictionary<uint, long> baseline;
            long generation;
            ushort next;
            lock (gate)
            {
                EnsureConnectedLocked();
                if (capabilities is null) throw new IOException("尚未完成设备能力查询，不能写入参数。");
                baseline = TargetRevisionsLocked(selector);
                generation = disconnectedGeneration;
                next = NextSequenceLocked();
            }
            var payload = MicTlvCodec.SettingPayload(2, selector, 32, [new MicTlv(type, [value])]);
            var request = new DumlFrame(2, 0x5a, next, 0x40, 0x5b, 1, payload);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(OperationTimeout);
            try
            {
                WriteLog($"设置 selector={selector} type=0x{type:X4} value={value}，等待 ACK 和新状态回读。");
                var ack = await ExchangeAsync(request, timeout.Token).ConfigureAwait(false);
                if (ack.Payload.Length == 0) throw new InvalidDataException("Mic Mini 参数 ACK 没有返回状态码，不能确认执行结果。");
                if (ack.Payload[0] != 0) throw new IOException($"Mic Mini 拒绝参数 type=0x{type:X4}，ACK 状态码 0x{ack.Payload[0]:X2}。");
                await WaitForReadbackAsync(selector, baseline, generation, readbackMatches, timeout.Token).ConfigureAwait(false);
                WriteLog($"参数 type=0x{type:X4} 已由设备新状态回读确认（selector={selector}）。");
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            { throw new TimeoutException($"参数 type=0x{type:X4} 在 3 秒内未完成 ACK 与新状态回读确认；未自动重试，请检查设备实际状态。"); }
        }
        finally { operations.Release(); }
    }

    // Caller holds operations. Exact correlation excludes unsolicited status/ACKs.
    private async Task<DumlFrame> ExchangeAsync(DumlFrame request, CancellationToken token)
    {
        var exchange = new PendingRequest(request);
        lock (gate)
        {
            EnsureConnectedLocked();
            if (pending is not null) throw new InvalidOperationException("已有控制命令等待响应。");
            pending = exchange;
        }
        try
        {
            token.ThrowIfCancellationRequested();
            await transport.SendAsync(DumlCodec.Encode(request), token).ConfigureAwait(false);
            return await exchange.Completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally { lock (gate) if (ReferenceEquals(pending, exchange)) pending = null; }
    }

    private async Task WaitForInitialStateAsync(CancellationToken token)
    {
        while (true)
        {
            Task pulse;
            lock (gate)
            {
                EnsureConnectedLocked();
                if (ReceiverIsFreshLocked() && receiver!.ConnectedTransmittersMask is uint mask &&
                    Selectors(mask).All(selector => transmitters.ContainsKey(selector) && IsFresh(transmitterTimes.GetValueOrDefault(selector)))) return;
                pulse = statePulse.Task;
            }
            await pulse.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private async Task WaitForReadbackAsync(uint selector, Dictionary<uint, long> baseline, long generation, Func<bool> matches, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Dictionary<uint, long> observed;
            Task pulse;
            lock (gate)
            {
                EnsureConnectedLocked();
                if (disconnectedGeneration != generation) throw new IOException("参数确认期间麦克风在线状态已变化，请重新检查连接和设备参数。");
                observed = TargetRevisionsLocked(selector);
                pulse = statePulse.Task;
            }
            if (observed.All(item => item.Value > baseline[item.Key]))
            {
                // A predicate may read our snapshots: never hold gate or invoke it
                // on the USB reader. Require stable revisions across its evaluation.
                var matched = matches();
                lock (gate)
                {
                    EnsureConnectedLocked();
                    if (disconnectedGeneration != generation) throw new IOException("参数确认期间麦克风连接发生变化。");
                    var latest = TargetRevisionsLocked(selector);
                    if (!latest.All(item => item.Value == observed[item.Key])) continue;
                    if (matched) return;
                    pulse = statePulse.Task;
                }
            }
            await pulse.WaitAsync(token).ConfigureAwait(false);
        }
    }

    private Dictionary<uint, long> TargetRevisionsLocked(uint selector)
    {
        if (!ReceiverIsFreshLocked()) throw new IOException("接收器状态已过期或尚未收到实时状态，不能确认参数。");
        if (selector == 0) return new() { [0] = targetRevisions.GetValueOrDefault(0u) };
        if (receiver!.ConnectedTransmittersMask is not uint mask || (selector & mask) != selector)
            throw new IOException($"发射器 selector={selector} 当前不在线，不能写入或确认参数。");
        var selected = new Dictionary<uint, long>();
        foreach (var item in Selectors(selector))
        {
            if (!transmitters.ContainsKey(item) || !IsFresh(transmitterTimes.GetValueOrDefault(item))) throw new IOException($"发射器 selector={item} 的实时状态缺失或过期。");
            selected[item] = targetRevisions.GetValueOrDefault(item);
        }
        return selected;
    }

    private void Received(byte[] bytes)
    {
        IReadOnlyList<DumlFrame> frames;
        lock (gate)
        {
            if (disposed || !connected) return;
            frames = decoder.Feed(bytes);
        }
        foreach (var frame in frames)
        {
            bool matched;
            lock (gate)
            {
                if (disposed || !connected) return;
                matched = pending is not null && MatchesResponse(pending.Request, frame);
                if (matched) pending!.Completion.TrySetResult(frame);
            }
            if (matched) WriteLog($"收到匹配响应 {frame.CommandSet:X2}/{frame.Command:X2} seq={frame.Sequence}，载荷 {frame.Payload.Length} 字节。");
            if (frame.Sender != 0x5a || frame.Receiver != 2 || frame.CommandSet != 0x5b || frame.Command != 3 ||
                (frame.Flags & 0x80) != 0 || frame.Payload.Length == 0 || frame.Payload[0] != 3) continue;
            try
            {
                if (ApplyStatus(MicV3State.Decode(frame.Payload))) QueueStateChanged();
            }
            catch (InvalidDataException exception)
            {
                var now = Stopwatch.GetTimestamp();
                if (lastMalformedLog == 0 || Stopwatch.GetElapsedTime(lastMalformedLog, now) > TimeSpan.FromSeconds(3))
                { lastMalformedLog = now; WriteLog("忽略无效状态载荷：" + exception.Message); }
            }
        }
    }

    private bool ApplyStatus(IReadOnlyList<MicV3Record> records)
    {
        var changed = false;
        var liveStatus = false;
        var summary = new List<string>();
        lock (gate)
        {
            if (disposed || !connected) return false;
            var now = Stopwatch.GetTimestamp();
            // A new status packet must not revive identities from an expired connection.
            if (receiver is not null && !IsFresh(receiverTime))
            {
                receiver = null; identities.Clear(); transmitters.Clear(); transmitterTimes.Clear(); targetRevisions.Clear();
                disconnectedGeneration++; changed = true;
            }
            foreach (var selector in transmitterTimes.Where(item => !IsFresh(item.Value)).Select(item => item.Key).ToArray())
            { RemoveTransmitterLocked(selector); changed = true; }
            // Apply membership before TX even when wire records arrive TX-first.
            foreach (var record in records.Where(record => record.Rx is not null && record.Selector == 0))
            {
                if (receiver != record.Rx)
                {
                    changed = true;
                    summary.Add($"RX 状态更新：声道原始值 {record.Rx!.ChannelRaw}，ALC={record.Rx.AutomaticLimiter}，安全轨={record.Rx.SafetyTrack}，在线掩码=0x{record.Rx.ConnectedTransmittersMask.GetValueOrDefault():X8}");
                }
                receiver = record.Rx;
                receiverTime = now;
                targetRevisions[0] = ++revision;
                liveStatus = true;
                var mask = receiver!.ConnectedTransmittersMask.GetValueOrDefault();
                foreach (var selector in transmitters.Keys.Concat(identities.Keys).Distinct().Where(selector => selector != 0 && (selector & mask) != selector).ToArray())
                { RemoveTransmitterLocked(selector); changed = true; summary.Add($"发射器 selector={selector} 已离线。"); }
            }
            foreach (var record in records)
            {
                if (record.Identity is { } identity)
                {
                    if (record.Selector != 0 && receiver?.ConnectedTransmittersMask is uint membership && (membership & record.Selector) != record.Selector) continue;
                    identities.TryGetValue(record.Selector, out var previous);
                    if (identity.SerialNumber is not null && previous?.SerialNumber is not null && identity.SerialNumber != previous.SerialNumber)
                        previous = null; // Never mix fields from two physical transmitters.
                    var merged = new MicV3IdentityState(identity.FirmwareVersion ?? previous?.FirmwareVersion,
                        identity.SerialNumber ?? previous?.SerialNumber, identity.DeviceName ?? previous?.DeviceName,
                        identity.ProductName ?? previous?.ProductName);
                    if (previous != merged) { identities[record.Selector] = merged; changed = true; }
                }
                if (record.Tx is not { } tx || record.Selector == 0 || (record.Selector & (record.Selector - 1)) != 0) continue;
                // Cache TX arriving before first RX, but never expose it as online
                // without the RX's fresh connected mask.
                if (receiver?.ConnectedTransmittersMask is uint mask && (mask & record.Selector) == 0) continue;
                if (!transmitters.TryGetValue(record.Selector, out var previousTx) || previousTx != tx)
                {
                    changed = true;
                    summary.Add($"TX selector={record.Selector} 状态更新：降噪={tx.Denoise}，低切={tx.LowCut}，静音={tx.Muted}，电量等级={tx.BatteryLevelRaw}");
                }
                transmitters[record.Selector] = tx;
                transmitterTimes[record.Selector] = now;
                targetRevisions[record.Selector] = ++revision;
                liveStatus = true;
            }
            // Identity and high-frequency kind5 packets cannot satisfy readback.
            if (liveStatus) PulseLocked();
        }
        foreach (var message in summary) WriteLog(message);
        return changed;
    }

    private static bool MatchesResponse(DumlFrame request, DumlFrame reply) =>
        (reply.Flags & 0x80) != 0 && reply.Sequence == request.Sequence && reply.CommandSet == request.CommandSet &&
        reply.Command == request.Command && reply.Sender == request.Receiver && reply.Receiver == request.Sender;

    private void Disconnected(string message) => Invalidate("Mic Mini 控制通道断开：" + message);
    private void Invalidate(string message)
    {
        lock (gate)
        {
            connected = false; receiver = null; capabilities = null;
            transmitters.Clear(); transmitterTimes.Clear(); targetRevisions.Clear(); identities.Clear();
            disconnectedGeneration++;
            pending?.Completion.TrySetException(new IOException(message));
            PulseLocked();
        }
        WriteLog(message);
        QueueStateChanged();
    }
    private void ExpireState(object? _)
    {
        var changed = false;
        lock (gate)
        {
            if (disposed || !connected) return;
            if (receiver is not null && !IsFresh(receiverTime))
            {
                receiver = null; targetRevisions.Clear(); transmitters.Clear(); transmitterTimes.Clear(); identities.Clear();
                disconnectedGeneration++; changed = true;
            }
            foreach (var selector in transmitterTimes.Where(item => !IsFresh(item.Value)).Select(item => item.Key).ToArray())
            { RemoveTransmitterLocked(selector); changed = true; }
            if (changed) PulseLocked();
        }
        if (changed) { WriteLog("部分麦克风实时状态超过 3 秒未更新，相关参数控制已暂停。"); QueueStateChanged(); }
    }
    private void RemoveTransmitterLocked(uint selector)
    {
        transmitters.Remove(selector); transmitterTimes.Remove(selector); targetRevisions.Remove(selector); identities.Remove(selector); disconnectedGeneration++;
    }
    private bool ReceiverIsFreshLocked() => !disposed && connected && receiver is not null && IsFresh(receiverTime);
    private static bool IsFresh(long timestamp) => timestamp != 0 && Stopwatch.GetElapsedTime(timestamp) <= StateLifetime;
    private void EnsureConnectedLocked()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!connected || !transport.IsOpen) throw new IOException("Mic Mini 控制通道未连接。");
    }
    private ushort NextSequenceLocked()
    {
        if (sequence >= ushort.MaxValue) throw new IOException("本会话命令序列号已用尽，请重新连接后继续。");
        return (ushort)++sequence;
    }
    private static IEnumerable<uint> Selectors(uint mask)
    {
        for (var bit = 0; bit < 32; bit++) { var value = 1u << bit; if ((mask & value) != 0) yield return value; }
    }
    private static TaskCompletionSource<bool> NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void PulseLocked() { var old = statePulse; statePulse = NewPulse(); old.TrySetResult(true); }

    private void QueueStateChanged()
    {
        Interlocked.Increment(ref stateNoticeVersion);
        if (Interlocked.CompareExchange(ref noticeQueued, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                var observed = Volatile.Read(ref stateNoticeVersion);
                var handlers = StateChanged;
                if (handlers is not null)
                    foreach (Action handler in handlers.GetInvocationList())
                        try { handler(); } catch (Exception exception) { Trace.TraceError($"Mic state subscriber: {exception}"); }
                Interlocked.Exchange(ref noticeQueued, 0);
                if (observed == Volatile.Read(ref stateNoticeVersion) || Interlocked.CompareExchange(ref noticeQueued, 1, 0) != 0) return;
            }
        });
    }
    private void WriteLog(string message)
    {
        lock (logGate)
        {
            while (logQueue.Count >= 64) logQueue.Dequeue();
            logQueue.Enqueue(message);
            if (logQueued != 0) return;
            logQueued = 1;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            while (true)
            {
                string entry;
                lock (logGate)
                {
                    if (logQueue.Count == 0) { logQueued = 0; return; }
                    entry = logQueue.Dequeue();
                }
                var handlers = Log;
                if (handlers is not null)
                    foreach (Action<string> handler in handlers.GetInvocationList())
                        try { handler(entry); } catch (Exception exception) { Trace.TraceError($"Mic log subscriber: {exception}"); }
            }
        });
    }

    public ValueTask DisposeAsync() { lock (disposeGate) return new ValueTask(disposal ??= DisposeCoreAsync()); }
    private async Task DisposeCoreAsync()
    {
        lock (gate)
        {
            disposed = true; connected = false; receiver = null; capabilities = null;
            transmitters.Clear(); transmitterTimes.Clear(); targetRevisions.Clear(); identities.Clear();
            disconnectedGeneration++;
            pending?.Completion.TrySetCanceled();
            PulseLocked();
        }
        expiryTimer.Dispose();
        lifetime.Cancel();
        QueueStateChanged();
        // Abort native I/O before waiting for the command owner. USB shutdown
        // never waits for user event callbacks, avoiding UI/reader lock inversion.
        await transport.DisposeAsync().ConfigureAwait(false);
        await operations.WaitAsync().ConfigureAwait(false);
        operations.Release();
        transport.DataReceived -= Received;
        transport.Disconnected -= Disconnected;
    }
    private sealed record PendingRequest(DumlFrame Request)
    {
        public TaskCompletionSource<DumlFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
