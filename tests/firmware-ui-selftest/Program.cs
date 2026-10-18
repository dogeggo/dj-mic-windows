using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MicMini.Desktop;

// Offline WPF regression: construct views only. Never create AppController,
// initialize device services, enumerate hardware, or invoke installation events.
internal static class FirmwareUiSelfTest
{
    private static int checks;

    [STAThread]
    public static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        MainWindow? owner = null;
        FirmwareWindow? dialog = null;
        FirmwareWindow? cancelable = null;
        try
        {
            owner = new MainWindow();
            KeepOffscreen(owner);
            owner.Show();
            dialog = new FirmwareWindow(owner, ["测试接收端", "测试发射端"]);
            KeepOffscreen(dialog);
            dialog.Show();
            dialog.UpdateLayout();

            var target = Field<ComboBox>(dialog, "target");
            var assets = Field<ComboBox>(dialog, "assets");
            var install = Field<Button>(dialog, "install");
            var close = Field<Button>(dialog, "close");
            var package = Field<TextBlock>(dialog, "package");
            var status = Field<TextBlock>(dialog, "status");
            var identity = Field<TextBlock>(dialog, "identity");
            var controls = new[] { "check", "select", "download" }.Select(name => Field<Button>(dialog, name)).ToArray();

            Check(owner.Icon is BitmapSource { PixelWidth: > 0, PixelHeight: > 0 }, "主窗口图标资源必须能够解码");
            Check(ReferenceEquals(dialog.Icon, owner.Icon), "固件窗口沿用真实主窗口图标");
            Check(!install.IsEnabled, "初始状态未校验固件，禁止安装");
            Check(!dialog.IsWriting && close.IsEnabled, "初始状态允许关闭");
            Check(target.IsEnabled && target.SelectedIndex == 0, "空闲时可选择目标设备");
            Check(assets.Visibility == Visibility.Collapsed, "没有官方资产时不显示下载候选");
            Check(status.Text.Contains("尚未确认") && status.Text.Contains("无法安装"), "初始状态准确说明官方入口尚未确认");
            Check(VisualChildren(dialog).OfType<TextBlock>().Any(text => text.Text.Contains("固件文件须与官方清单")), "窗口持续展示官方文件匹配门槛");

            dialog.SetPackage("未与官方清单匹配的本地文件", false);
            Check(!install.IsEnabled, "仅提供本地文件仍不能安装");
            dialog.SetPackage("已由调用方完成全部资格校验的离线测试候选", true);
            Check(install.IsEnabled, "校验通过且空闲时安装按钮可用");
            dialog.SetBusy(true, true);
            Check(dialog.IsWriting, "写入忙状态可被窗口生命周期查询");
            Check(!install.IsEnabled, "写入期间禁止重复启动安装");
            Check(!target.IsEnabled, "写入期间不能切换目标");
            Check(!assets.IsEnabled, "写入期间不能切换官方资产");
            foreach (var button in controls) Check(!button.IsEnabled, $"写入期间禁用 {button.Content}");
            Check(!close.IsEnabled, "写入期间关闭按钮禁用");

            var closeAttempts = 0;
            var closeWasCanceled = false;
            var closed = false;
            dialog.Closing += (_, e) => { closeAttempts++; closeWasCanceled = e.Cancel; };
            dialog.Closed += (_, _) => closed = true;
            dialog.Close();
            Check(closeAttempts == 1 && closeWasCanceled, "真实 Close 调用被 Closing 事件取消");
            Check(!closed && dialog.IsVisible, "被阻止关闭后窗口仍存在");
            Check(status.Text.Contains("暂时无法关闭"), "被阻止关闭时给出原因");

            dialog.SetBusy(false);
            Check(!dialog.IsWriting && close.IsEnabled, "解除写入忙状态后可关闭");
            Check(target.IsEnabled && assets.IsEnabled && controls.All(button => button.IsEnabled), "解除忙状态后恢复交互");
            Check(install.IsEnabled, "解除忙状态后保留已校验候选的资格");
            var targetChanges = 0;
            dialog.TargetChanged += _ => targetChanges++;
            target.SelectedIndex = 1;
            Check(targetChanges == 1 && dialog.SelectedTarget == 1, "切换目标通知调用方重新核对身份");
            Check(!install.IsEnabled, "切换目标清除旧安装资格");

            dialog.SetPackage("SHA-256：" + new string('A', 64), false);
            package.Measure(new Size(220, double.PositiveInfinity));
            Check(package.TextWrapping == TextWrapping.Wrap && package.DesiredSize.Height > package.LineHeight, "连续长 SHA-256 在窄宽度下实际换行");
            dialog.SetStatus(string.Concat(Enumerable.Repeat("重连后仍在核对同一设备的序列号和实际固件版本。", 6)));
            status.Measure(new Size(220, double.PositiveInfinity));
            Check(status.TextWrapping == TextWrapping.Wrap && status.DesiredSize.Height > status.LineHeight, "长状态信息实际换行");
            Check(identity.TextWrapping == TextWrapping.Wrap, "长设备身份允许换行");
            var footer = VisualChildren(dialog).OfType<TextBlock>().Single(text => text.Text.StartsWith("匹配官方文件后才能安装。"));
            footer.Measure(new Size(220, double.PositiveInfinity));
            Check(footer.TextWrapping == TextWrapping.Wrap && footer.DesiredSize.Height > footer.FontSize * 2, "底部说明在窄宽度下实际换行");
            Check(VisualChildren(dialog).OfType<ScrollViewer>().Any(scroll => scroll.Content is StackPanel && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled), "主体宽度受窗口约束，不依赖横向滚动阅读");

            dialog.Close();
            Check(closeAttempts == 2 && !closeWasCanceled && closed, "解除写入状态后真实 Close 正常结束窗口");

            cancelable = new FirmwareWindow(owner, ["测试接收端"]);
            KeepOffscreen(cancelable);
            cancelable.Show();
            cancelable.SetBusy(true);
            Check(!cancelable.IsWriting && Field<Button>(cancelable, "close").IsEnabled, "普通下载或检查忙状态仍可取消");
            var canceledWindowClosed = false;
            cancelable.Closed += (_, _) => canceledWindowClosed = true;
            cancelable.Close();
            Check(canceledWindowClosed, "普通忙状态下真实 Close 正常关闭");

            Console.WriteLine($"PASS {checks} offline firmware UI checks; no controller or hardware initialized.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL after {checks} checks: {ex}");
            return 1;
        }
        finally
        {
            if (dialog?.IsVisible == true) { dialog.SetBusy(false); dialog.Close(); }
            if (cancelable?.IsVisible == true) { cancelable.SetBusy(false); cancelable.Close(); }
            owner?.Close();
            app.Shutdown();
        }
    }

    private static void KeepOffscreen(Window window)
    {
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
    }

    private static T Field<T>(FirmwareWindow dialog, string name) where T : class =>
        (T)(typeof(FirmwareWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog)
            ?? throw new InvalidOperationException($"缺少待验证控件：{name}"));

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in VisualChildren(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        checks++;
    }
}
