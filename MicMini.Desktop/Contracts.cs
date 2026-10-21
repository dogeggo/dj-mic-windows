namespace MicMini.Desktop;

public sealed record SettingItem(string Key, string Title, string Description, string[] Options, int? Value, bool Enabled, string Availability);
public sealed record BatteryItem(byte? LevelRaw, bool Charging);
public sealed record DeviceItem(string Name, string Role, string Status, string Detail, BatteryItem? Battery = null);
public sealed record AppSnapshot(
    string ConnectionTitle, string ConnectionDetail, bool ControlConnected,
    string AudioStatus, bool AudioAvailable, bool IsRecording, double PeakLeft, double PeakRight,
    string RecordingTime, string RecordingPath, string Firmware, string Serial,
    IReadOnlyList<DeviceItem> Devices, IReadOnlyList<SettingItem> Settings,
    IReadOnlyList<string> Logs, bool Busy = false, bool ParametersReady = false, bool TransmitterConnected = false)
{
    public bool CanStartRecording => AudioAvailable && TransmitterConnected && !IsRecording && !Busy;
}
