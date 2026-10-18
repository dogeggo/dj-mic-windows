using MicMini.Desktop.Services;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
async Task Reject(Func<Task> operation, string name)
{
    try { await operation(); throw new Exception("Expected rejection: " + name); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { checks++; }
}
MicFirmwareTransfer Session(FakeChannel wire) => new(wire, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(80), TimeSpan.Zero);
var firmware = Enumerable.Range(0, 130).Select(i => (byte)i).ToArray();
var success = new FakeChannel();
success.OnSend = (wire, data) =>
{
    if (data[0] == 0x55) { wire.Receive(0x55, 0x02, 1); wire.Receive(0x55, 0x60, 0); }
    else if (wire.DataWrites == 3) wire.Receive(0x55, 0x01, 1);
    return Task.CompletedTask;
};
var receipt = await Session(success).TransferModuleAsync(0, firmware);
Check(receipt is { DeviceAccepted: true, BytesSent: 130 }, "receipt reports acceptance only");
Check(success.Writes.Count == 4 && success.Writes[0][1] == 0x55, "one enter and three data reports");
Check(success.Writes.All(x => x.Length == 64), "all writes have HID report length");
Check(success.Writes.Skip(1).SelectMany(x => x.Skip(1)).Take(130).SequenceEqual(firmware), "ordered bytes across blocks");
Check(success.Writes[3].AsSpan(5).SequenceEqual(success.Writes[2].AsSpan(5)), "short tail preserves last report bytes");
Check(!success.Writes.Any(x => x[0] == 0x55 && x[1] == 0x52), "no unsolicited reboot");
Check(success.SubscriberCount == 0, "handlers detached after accepted result");

foreach (var writeReportsDisconnect in new[] { false, true })
{
    var restart = new FakeChannel();
    restart.OnSend = (wire, data) =>
    {
        if (data[0] == 0x55) { wire.Receive(0x55, 0x02, 1); wire.Receive(0x55, 0x60, 0); }
        else
        {
            wire.Receive(0x55, 0x01, 1); wire.Disconnect("expected restart after final acceptance");
            if (writeReportsDisconnect) throw new IOException("USB reset completing final write");
        }
        return Task.CompletedTask;
    };
    var accepted = await Session(restart).TransferModuleAsync(0, new byte[] { 1 });
    Check(accepted.DeviceAccepted && accepted.BytesSent == 1, "post-result restart preserves intermediate receipt");
    Check(restart.SubscriberCount == 0, "post-result restart detaches handlers");
}

var paused = new FakeChannel(); var enterSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
paused.OnSend = (wire, data) =>
{
    if (data[0] == 0x55) { wire.Receive(0x55, 0x02, 1); enterSeen.SetResult(); }
    else wire.Receive(0x55, 0x01, 1);
    return Task.CompletedTask;
};
var pending = Session(paused).TransferModuleAsync(0, new byte[] { 7 });
await enterSeen.Task;
Check(paused.DataWrites == 0 && !pending.IsCompleted, "enter acknowledgement alone cannot send data");
paused.Receive(0x55, 0x60, 0);
await pending;
Check(paused.DataWrites == 1, "explicit resume permits data");

var midPause = new FakeChannel(); var firstData = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
midPause.OnSend = (wire, data) =>
{
    if (data[0] == 0x55) { wire.Receive(0x55, 0x02, 1); wire.Receive(0x55, 0x60, 0); }
    else if (wire.DataWrites == 1) { wire.Receive(0x55, 0x60, 1); firstData.SetResult(); }
    else if (wire.DataWrites == 3) wire.Receive(0x55, 0x01, 1);
    return Task.CompletedTask;
};
var pausingTransfer = Session(midPause).TransferModuleAsync(0, firmware);
await firstData.Task;
Check(midPause.DataWrites == 1 && !pausingTransfer.IsCompleted, "device flow pause stops the next data report");
midPause.Receive(0x55, 0x60, 0);
await pausingTransfer;
Check(midPause.DataWrites == 3, "resuming continues the same offset without duplication");

var earlyFlow = new FakeChannel();
earlyFlow.OnSend = (wire, data) =>
{
    if (data[0] == 0x55) { wire.Receive(0x55, 0x60, 0); wire.Receive(0x55, 0x02, 1); }
    else wire.Receive(0x55, 0x01, 1);
    return Task.CompletedTask;
};
var earlyPending = Session(earlyFlow).TransferModuleAsync(0, new byte[] { 1 });
Check(earlyFlow.DataWrites == 0 && !earlyPending.IsCompleted, "flow before enter acknowledgement cannot unlock data");
earlyFlow.Receive(0x55, 0x60, 0);
await earlyPending;
Check(earlyFlow.DataWrites == 1, "fresh flow after enter is required");

var slowWrite = new FakeChannel { WaitForCancellation = true };
await Reject(() => Session(slowWrite).TransferModuleAsync(0, firmware), "per-write timeout cancels pending I/O");
Check(slowWrite.Writes.Count == 1 && slowWrite.SubscriberCount == 0, "write timeout has no retry and clears handlers");
var lostDuringWrite = new FakeChannel { WaitForCancellation = true };
lostDuringWrite.OnSend = (wire, _) => { wire.Disconnect("during write"); return Task.CompletedTask; };
await Reject(() => Session(lostDuringWrite).TransferModuleAsync(0, firmware), "disconnect cancels pending I/O");
Check(lostDuringWrite.Writes.Count == 1 && lostDuringWrite.SubscriberCount == 0, "disconnect during write is bounded");

foreach (var scenario in new[] { "reject-enter", "no-enter", "no-flow", "premature-result", "reject-result", "disconnect", "write-failure", "missing-result" })
{
    var wire = new FakeChannel();
    wire.OnSend = (fake, data) =>
    {
        if (data[0] == 0x55)
        {
            if (scenario == "no-enter") { fake.Receive(0x55, 0x59, 1); return Task.CompletedTask; }
            if (scenario == "reject-enter") { fake.Receive(0x55, 0x02, 0); return Task.CompletedTask; }
            if (scenario == "premature-result") { fake.Receive(0x55, 0x01, 1); return Task.CompletedTask; }
            fake.Receive(0x55, 0x02, 1);
            if (scenario != "no-flow") fake.Receive(0x55, 0x60, 0);
        }
        else
        {
            if (scenario == "reject-result") fake.Receive(0x55, 0x01, 0);
            if (scenario == "disconnect") fake.Disconnect("test disconnect");
            if (scenario == "write-failure") throw new IOException("test write failure");
            if (scenario == "missing-result") { fake.Receive(0x55, 0x01); fake.Receive(0x55, 0x5e, 0, 17, 3, 2); }
        }
        return Task.CompletedTask;
    };
    await Reject(() => Session(wire).TransferModuleAsync(0, new byte[] { 5 }), scenario);
    Check(wire.Writes.Count(x => x[0] == 0x55) == 1, scenario + " does not resend control or reboot");
    Check(wire.DataWrites <= 1, scenario + " does not repeat data");
    Check(wire.SubscriberCount == 0, scenario + " detaches callbacks");
}
var cancelled = new FakeChannel();
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    await Reject(() => Session(cancelled).TransferModuleAsync(0, firmware, token: cancellation.Token), "pre-cancelled");
    Check(cancelled.Writes.Count == 0, "pre-cancellation sends nothing");
}
var empty = new FakeChannel();
await Reject(() => Session(empty).TransferModuleAsync(0, Array.Empty<byte>()), "empty module");
Check(empty.Writes.Count == 0, "empty image sends nothing");

var snapshotInput = firmware.ToArray(); var stable = new FakeChannel();
stable.OnSend = (wire, data) =>
{
    if (data[0] == 0x55) { Array.Fill(snapshotInput, (byte)255); wire.Receive(0x55, 0x02, 1); wire.Receive(0x55, 0x60, 0); }
    else if (wire.DataWrites == 3) wire.Receive(0x55, 0x01, 1);
    return Task.CompletedTask;
};
await Session(stable).TransferModuleAsync(1, snapshotInput);
Check(stable.Writes[0][2] == 1, "explicit TX module routed without changing target");
Check(stable.Writes.Skip(1).SelectMany(x => x.Skip(1)).Take(130).SequenceEqual(firmware), "caller mutations cannot change transfer bytes");
Console.WriteLine($"PASS: {checks} firmware session checks; simulated HID only; no hardware opened.");

sealed class FakeChannel : IFirmwareHidChannel
{
    private Action<byte[]>? receive;
    private Action<string>? disconnected;
    public event Action<byte[]>? ReportReceived { add => receive += value; remove => receive -= value; }
    public event Action<string>? Disconnected { add => disconnected += value; remove => disconnected -= value; }
    public int SubscriberCount => (receive?.GetInvocationList().Length ?? 0) + (disconnected?.GetInvocationList().Length ?? 0);
    public List<byte[]> Writes { get; } = [];
    public int DataWrites => Writes.Count(x => x[0] == 0x41);
    public Func<FakeChannel, byte[], Task>? OnSend { get; set; }
    public bool WaitForCancellation { get; init; }
    public Task SendAsync(ReadOnlyMemory<byte> report, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var copy = report.ToArray(); Writes.Add(copy);
        var completed = OnSend?.Invoke(this, copy) ?? Task.CompletedTask;
        return WaitForCancellation ? Task.Delay(Timeout.Infinite, token) : completed;
    }
    public void Receive(params byte[] report) => receive?.Invoke(report);
    public void Disconnect(string error) => disconnected?.Invoke(error);
}
