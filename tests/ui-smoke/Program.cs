using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MicMini.Desktop;
using MicMini.Desktop.Services;

internal static class UiSmoke
{
    [STAThread]
    public static int Main()
    {
        var application = new Application();
        var window = new MainWindow();
        var controller = new AppController(window);
        var exit = 0;
        window.Loaded += async (_, _) =>
        {
            MicBulkSession? session = null; bool? original = null;
            try
            {
                await controller.InitializeAsync();
                session = (MicBulkSession?)typeof(AppController).GetField("bulk", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller);
                if (session is null || !session.Transmitters.TryGetValue(1, out var tx)) throw new Exception("UI未连接到真实TX。");
                original = tx.LowCut;
                ((Button)window.FindName("SoundNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var combo = Descendants(window).OfType<ComboBox>().Single(c => Equals(c.Tag, "tx:1:lowcut"));
                if (!combo.IsEnabled) throw new Exception("已验证参数控件未启用。");
                combo.SelectedIndex = original.Value ? 1 : 2;
                await Until(() => session.Transmitters.TryGetValue(1, out var now) && now.LowCut != original);
                await Until(() => !IsBusy(controller));
                Console.WriteLine("PASS UI selection -> ACK -> live readback");
                combo.SelectedIndex = original.Value ? 2 : 1;
                await Until(() => session.Transmitters.TryGetValue(1, out var now) && now.LowCut == original);
                await Until(() => !IsBusy(controller));
                Console.WriteLine("PASS UI restores original lowcut");
                ((Button)window.FindName("OverviewNav")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var audio = (AudioCapture)typeof(AppController).GetField("audio", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
                ((Button)window.FindName("StartRecordingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => audio.IsRecording); await Task.Delay(1100);
                ((Button)window.FindName("StopRecordingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !audio.IsRecording);
                if (audio.RecordingPath is null || new FileInfo(audio.RecordingPath).Length <= 44) throw new Exception("UI录音未生成PCM数据。");
                Console.WriteLine("PASS UI recording: " + audio.RecordingPath);
                ((Button)window.FindName("DisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !session.IsOpen);
                if (!audio.IsMonitoring) throw new Exception("断开调参不应停止USB音频。");
                Console.WriteLine("PASS disconnect parameters preserves audio capture");
            }
            catch (Exception ex)
            {
                exit = 1; Console.Error.WriteLine(ex);
                var logs = (Queue<string>)typeof(AppController).GetField("logs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
                foreach (var line in logs.TakeLast(15)) Console.WriteLine(line);
            }
            finally
            {
                if (session?.IsOpen == true && original.HasValue && session.Transmitters.TryGetValue(1, out var current) && current.LowCut != original)
                    await session.SetValueAsync(1, 3, (byte)(original.Value ? 1 : 0), () => session.Transmitters.TryGetValue(1, out var now) && now.LowCut == original);
                await controller.DisposeAsync(); window.Close();
            }
        };
        application.Run(window); return exit;
    }
    private static bool IsBusy(AppController controller) => (bool)typeof(AppController).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < end) { if (predicate()) return; await Task.Delay(30); }
        throw new TimeoutException("UI流程等待超时。");
    }
}
