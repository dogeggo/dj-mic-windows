using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MicMini.Desktop.Services;

namespace MicMini.Desktop;

public sealed class App : Application
{
    [STAThread]
    public static void Main(string[] args)
    {
        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Mic Mini Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var window = new MainWindow();
        var controller = new AppController(window);
        using var tray = new TrayIconService(window);
        controller.SnapshotUpdated += tray.Update;
        var closing = false;
        window.Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            if (controller.FirmwareInstalling)
            {
                MessageBox.Show(window, "固件升级正在进行，请保持设备连接并等待版本核验完成。", "固件升级", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            window.IsEnabled = false;
            try { await controller.DisposeAsync(); }
            finally { closing = true; window.Close(); }
        };
        window.Loaded += async (_, _) =>
        {
            await controller.InitializeAsync();
            var screenshot = Array.IndexOf(args, "--screenshot");
            if (screenshot >= 0 && screenshot + 1 < args.Length)
            {
                var page = Array.IndexOf(args, "--page");
                if (page >= 0 && page + 1 < args.Length)
                {
                    var name = args[page + 1] == "settings" ? "SoundNav" : "DiagnosticsNav";
                    if (window.FindName(name) is System.Windows.Controls.Button navigation)
                        navigation.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                }
                await Task.Delay(800);
                window.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.GetFullPath(args[screenshot + 1])); encoder.Save(file);
                if (args.Contains("--exit-after-screenshot")) window.Close();
            }
        };
        app.Run(window);
        controller.SnapshotUpdated -= tray.Update;
    }
}
