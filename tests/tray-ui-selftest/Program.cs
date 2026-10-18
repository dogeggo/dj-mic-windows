using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MicMini.Desktop;
using MicMini.Desktop.Services;
using Forms = System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var checks = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new MainWindow { ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        using var service = new TrayIconService(window);
        var icon = (Forms.NotifyIcon)typeof(TrayIconService).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        AppSnapshot State(params DeviceItem[] devices) => new("离线界面验证", "合成状态", true, "未初始化音频", false, false,
            0, 0, "00:00:00", "", "", "", devices, [], [], ParametersReady: true);
        var tx = new DeviceItem("测试发射端", "TX 1", "已连接", "合成状态", new(4, true));
        try
        {
            window.Show();
            var snapshot = State(new("测试接收端", "RX", "在线", ""), tx);
            window.Render(snapshot); window.UpdateLayout(); service.Update(snapshot);
            var cards = (Panel)window.FindName("DeviceCards");
            var bars = Children(cards).OfType<ProgressBar>().ToArray();
            Check(bars.Length == 1 && bars[0].Value == 4 && bars[0].Maximum == 7, "TX raw battery level renders in a dedicated bar");
            Check(icon.Visible && icon.Icon is not null, "real tray icon and resource initialized");
            Check(icon.Text.Contains("TX 1：电量等级 4 · 充电中") && !icon.Text.Contains('%'), "hover text displays raw battery and charging without a fabricated percentage");
            window.WindowState = WindowState.Minimized;
            Check(!window.IsVisible && icon.Visible, "minimize hides the window and preserves the tray icon");
            service.Update(State(tx with { Battery = new(2, false) }));
            Check(icon.Text.Contains("电量等级 2") && !icon.Text.Contains("充电中"), "battery and charging still update while minimized");
            icon.ContextMenuStrip!.Items[0].PerformClick();
            Check(window.IsVisible && window.WindowState == WindowState.Normal, "tray open action restores the window");
            snapshot = State(tx with { Battery = new(null, false) });
            service.Update(snapshot); window.Render(snapshot); window.UpdateLayout();
            bars = Children(cards).OfType<ProgressBar>().ToArray();
            Check(bars.Length == 1 && bars[0].Value == 0 && bars[0].Opacity < 1, "missing battery renders a dim empty bar");
            Check(Children(cards).OfType<TextBlock>().Any(t => t.Text == "未读取") && icon.Text.Contains("电量未读取"), "unknown is distinct from zero in both surfaces");
            service.Update(State(tx with { Battery = new(0, false) }));
            Check(icon.Text.Contains("电量等级 0") && !icon.Text.Contains("未读取"), "zero remains a known raw level");
            service.Update(State(tx with { Battery = new(255, false) }));
            Check(icon.Text.Contains("未读取"), "out-of-range data cannot claim a full battery");
            service.Update(State());
            Check(icon.Text.Contains("未连接") && !icon.Text.Contains("等级 4"), "disconnection removes stale hover battery");
            service.Update(State(tx) with { IsRecording = true, RecordingTime = "00:01:23" });
            Check(icon.Text.Contains("正在录音 · 00:01:23"), "recording remains visible in hover text");
            service.Update(State(Enumerable.Range(0, 20).Select(i => tx with { Role = $"TX {i}" }).ToArray()));
            Check(icon.Text.Length <= 127, "multiple targets respect native tooltip length limit");
            var closed = false; var preventClose = true;
            window.Closing += (_, e) => e.Cancel = preventClose;
            window.Closed += (_, _) => closed = true;
            icon.ContextMenuStrip.Items[2].PerformClick();
            Check(!closed && icon.Visible, "tray exit respects the normal window close guard");
            preventClose = false;
            icon.ContextMenuStrip.Items[2].PerformClick();
            Check(closed && !icon.Visible, "normal tray exit closes the window and removes the icon");
            Console.WriteLine($"PASS {checks} battery/tray UI checks; synthetic state, no controller, hardware, or recording.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { window.Close(); app.Shutdown(); }
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Children(child)) yield return descendant;
        }
    }
}
