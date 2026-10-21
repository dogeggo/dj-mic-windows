using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using MicMini.Desktop;
using MicMini.Desktop.Services;

internal static class Program
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    [STAThread]
    private static int Main()
    {
        var checks = 0;
        var exit = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            var window = new MainWindow();
            AppController? controller = null;
            try
            {
                var start = (Button)window.FindName("StartRecordingButton");
                var stop = (Button)window.FindName("StopRecordingButton");
                var status = (TextBlock)window.FindName("RecordingStateText");
                var state = new AppSnapshot("合成状态", "离线录音测试", true, "合成 USB 音频", true, false,
                    0, 0, "00:00:00", "", "", "", [], [], [], ParametersReady: true);
                window.Render(state);
                Check(!start.IsEnabled && status.Text == "请连接发射端后录音", "USB audio alone cannot enable recording");
                window.Render(state with { Devices = [new("历史发射端", "TX", "接收端确认在线", "")] });
                Check(!start.IsEnabled, "cached HID device card cannot enable recording");
                state = state with { TransmitterConnected = true };
                window.Render(state);
                Check(start.IsEnabled && status.Text == "已就绪，等待开始录音", "online TX and available audio enable recording");
                window.Render(state with { AudioAvailable = false });
                Check(!start.IsEnabled && status.Text.Contains("USB 音频"), "online TX still requires audio input");
                window.Render(state with { Busy = true });
                Check(!start.IsEnabled && status.Text.Contains("正在与设备通信"), "busy operations disable recording");
                window.Render(state with { IsRecording = true, TransmitterConnected = false, Busy = true });
                Check(start.Visibility == Visibility.Collapsed && stop.Visibility == Visibility.Visible && stop.IsEnabled,
                    "stop and save remains available during disconnection");
                window.Render(state with { TransmitterConnected = false });
                Check(!start.IsEnabled, "TX loss updates the start button");
                window.Render(state);
                Check(start.IsEnabled && stop.Visibility == Visibility.Collapsed, "reconnection permits a manual start");

                // Use the real controller and WAV writer with synthetic PCM; never initialize hardware.
                controller = new AppController(window);
                var audio = (AudioCapture)typeof(AppController).GetField("audio", Hidden)!.GetValue(controller)!;
                var captureType = typeof(AudioCapture).GetNestedType("CaptureSession", BindingFlags.NonPublic)!;
                var capture = Activator.CreateInstance(captureType, Hidden, null, [audio, 0], null)!;
                captureType.GetProperty("Channels")!.SetValue(capture, 2);
                captureType.GetProperty("SampleRate")!.SetValue(capture, 48000);
                captureType.GetField("_running", Hidden)!.SetValue(capture, 1);
                typeof(AudioCapture).GetField("_capture", Hidden)!.SetValue(audio, capture);
                Check(audio.IsMonitoring, "synthetic audio input is active without opening a microphone");
                AppSnapshot? actual = null;
                controller.SnapshotUpdated += snapshot => actual = snapshot;
                void Render() => typeof(AppController).GetMethod("Render", Hidden)!.Invoke(controller, null);
                Render();
                Check(actual is { AudioAvailable: true, TransmitterConnected: false, CanStartRecording: false },
                    "controller independently reports recording unavailable without TX");
                window.Render(state); // Deliberately leave the button showing an outdated online state.
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!audio.IsRecording && audio.RecordingPath is null, "controller rejects a stale enabled button before creating a WAV");
                Check(actual!.Logs.Any(line => line.Contains("未确认发射端在线")) && !start.IsEnabled,
                    "rejected start explains the missing TX and refreshes the UI");

                var directory = Path.Combine(Path.GetTempPath(), "MicMini-recording-ui-" + Guid.NewGuid().ToString("N"));
                var path = audio.StartRecording(directory);
                var pcm = Enumerable.Range(0, 9600).Select(i => (byte)(i % 251)).ToArray();
                var buffer = Marshal.AllocHGlobal(pcm.Length);
                try
                {
                    Marshal.Copy(pcm, 0, buffer, pcm.Length);
                    typeof(AudioCapture).GetMethod("ReceiveSamples", Hidden)!.Invoke(audio, [capture, buffer, pcm.Length]);
                }
                finally { Marshal.FreeHGlobal(buffer); }
                Check(audio.IsRecording, "synthetic active recording models a transmitter disconnect");
                typeof(AppController).GetField("busy", Hidden)!.SetValue(controller, true);
                Render();
                Check(!audio.IsRecording && audio.IsMonitoring, "TX loss stops recording even while busy and preserves level monitoring");
                var wav = File.ReadAllBytes(path);
                Check(wav.Length == pcm.Length + 44 && BitConverter.ToUInt32(wav, 4) == wav.Length - 8 &&
                    BitConverter.ToUInt32(wav, 40) == pcm.Length && wav.AsSpan(44).SequenceEqual(pcm),
                    "automatic stop finalizes WAV headers and preserves all accepted PCM");
                Check(actual is { IsRecording: false, CanStartRecording: false } && actual.RecordingPath == path &&
                    actual.Logs.Count(line => line.Contains("录音已自动停止并保存")) == 1,
                    "automatic save reports the saved path once");
                Render();
                Check(actual!.Logs.Count(line => line.Contains("录音已自动停止并保存")) == 1,
                    "subsequent UI ticks do not repeat stop or save");
                window.Render(state);
                Check(!audio.IsRecording, "displaying reconnected state does not automatically start a new recording");
                Console.WriteLine($"PASS {checks} recording UI/controller checks; synthetic PCM only, no hardware access.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally
            {
                if (controller is not null) await controller.DisposeAsync();
                window.Close();
                app.Shutdown();
            }
        };
        app.Run();
        return exit;
    }
}
