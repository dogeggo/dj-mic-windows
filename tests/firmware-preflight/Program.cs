using MicMini.Desktop.Services;

// Metadata/descriptor-only test. No HidTransport.Open, SendAsync, or firmware mode commands.
var devices = HidTransport.Enumerate();
foreach (var device in devices)
    Console.WriteLine($"HID usage={device.UsagePage:X4} input={device.InputLength} output={device.OutputLength} container={device.ContainerId}");
var control = devices.Single(x => x.UsagePage == 0xff90);
var pair = FirmwareHidChannel.Preflight(control.ContainerId);
if (pair.Control.ContainerId == Guid.Empty || pair.Control.ContainerId != pair.Data.ContainerId)
    throw new Exception("USB collection identity mismatch");
Console.WriteLine($"PASS: control FF90 / data FF82 share {pair.Control.ContainerId}; output report 41 is declared; no device reports sent.");
