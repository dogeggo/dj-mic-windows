using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MicMini.Desktop;

/// <summary>Firmware workflow view. File selection and status display never send device commands.</summary>
public sealed class FirmwareWindow : Window
{
    private readonly ComboBox target = new();
    private readonly TextBlock identity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock package = new() { Text = "尚未选择固件。", TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox assets = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBlock status = new() { Text = "当前 Mini 官方固件入口尚未确认；匹配官方清单中的文件前无法安装。", TextWrapping = TextWrapping.Wrap };
    private readonly TextBox log = new();
    private readonly ProgressBar progress = new() { Maximum = 100, Height = 10 };
    private readonly Button check = new() { Content = "检查官方更新" };
    private readonly Button select = new() { Content = "选择固件文件" };
    private readonly Button download = new() { Content = "下载官方固件", Visibility = Visibility.Collapsed };
    private readonly Button install = new() { Content = "开始升级", IsEnabled = false };
    private readonly Button close = new() { Content = "关闭" };
    private readonly Queue<string> lines = new();
    private bool busy, writing, ready;
    public event Action<int>? TargetChanged;
    public event Action? CheckRequested;
    public event Action<string>? FileSelected;
    public event Action? DownloadRequested;
    public event Action? InstallRequested;
    public event Action<int>? AssetChanged;
    public int SelectedTarget => target.SelectedIndex;
    public bool IsWriting => writing;
    public int SelectedAsset => assets.SelectedIndex;

    public FirmwareWindow(MainWindow owner, IReadOnlyList<string> targets)
    {
        Owner = owner; Icon = owner.Icon; Title = "固件升级 · Mic Mini";
        Width = 790; Height = 760; MinWidth = 660; MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#10151B"); Foreground = Brush("#EAF1F7");
        FontFamily = owner.FontFamily; FontSize = 13;
        Resources.MergedDictionaries.Add(owner.Resources);
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "固件升级", FontSize = 25, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "当前 Mini 官方固件入口尚未确认。固件文件须与官方清单、设备型号及模块匹配后才能安装。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#96A6B7"), Margin = new Thickness(0, 8, 0, 0) });
        root.Children.Add(header);
        var body = new StackPanel();
        var deviceCard = new StackPanel();
        deviceCard.Children.Add(Label("升级设备"));
        target.ItemsSource = targets; target.SelectedIndex = targets.Count == 0 ? -1 : 0;
        target.Margin = new Thickness(0, 6, 0, 8); deviceCard.Children.Add(target);
        identity.Foreground = Brush("#96A6B7"); identity.LineHeight = 21; deviceCard.Children.Add(identity);
        body.Children.Add(Card(deviceCard));
        var fileCard = new StackPanel();
        fileCard.Children.Add(Label("官方固件"));
        package.Margin = new Thickness(0, 9, 0, 14); package.LineHeight = 22; fileCard.Children.Add(package);
        fileCard.Children.Add(assets);
        var actions = new WrapPanel();
        foreach (var button in new[] { check, select, download }) { button.Margin = new Thickness(0, 0, 10, 8); actions.Children.Add(button); }
        fileCard.Children.Add(actions); body.Children.Add(Card(fileCard));
        var progressCard = new StackPanel();
        status.LineHeight = 22; status.Margin = new Thickness(0, 0, 0, 12); progressCard.Children.Add(status);
        progressCard.Children.Add(progress);
        log.IsReadOnly = true; log.Background = Brush("#10171E"); log.Foreground = Brush("#A6B7C6");
        log.BorderBrush = Brush("#2A333E"); log.Padding = new Thickness(10); log.Height = 140;
        log.FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"); log.FontSize = 11;
        log.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; log.TextWrapping = TextWrapping.Wrap;
        log.Margin = new Thickness(0, 14, 0, 0); progressCard.Children.Add(log); body.Children.Add(Card(progressCard));
        var scroller = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }; Grid.SetRow(scroller, 1); root.Children.Add(scroller);
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock { Text = "匹配官方文件后才能安装。开始升级前请停止录音，并保持设备供电和 USB 连接。", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#96A6B7"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) });
        var finalActions = new StackPanel { Orientation = Orientation.Horizontal };
        install.Style = (Style)owner.FindResource("PrimaryButton"); install.Margin = new Thickness(0, 0, 10, 0);
        finalActions.Children.Add(install); finalActions.Children.Add(close); Grid.SetColumn(finalActions, 1); footer.Children.Add(finalActions);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        target.SelectionChanged += (_, _) => { if (!busy) { SetPackage("尚未选择固件。", false); TargetChanged?.Invoke(target.SelectedIndex); } };
        check.Click += (_, _) => CheckRequested?.Invoke();
        select.Click += (_, _) =>
        {
            var picker = new OpenFileDialog { Title = "选择官方固件文件", Filter = "固件文件|*.bin;*.sig;*.fw;*.zip;*.json|所有文件|*.*", CheckFileExists = true, Multiselect = false };
            if (picker.ShowDialog(this) == true) FileSelected?.Invoke(picker.FileName);
        };
        download.Click += (_, _) => DownloadRequested?.Invoke();
        assets.SelectionChanged += (_, _) => { if (!busy) AssetChanged?.Invoke(assets.SelectedIndex); };
        install.Click += (_, _) => InstallRequested?.Invoke();
        close.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (!writing) return;
            e.Cancel = true;
            status.Text = "设备正在安装固件，请保持连接并等待结果，暂时无法关闭窗口。";
        };
    }

    public void SetIdentity(string text) => identity.Text = text;
    public void SetPackage(string text, bool canInstall)
    {
        package.Text = text; ready = canInstall; UpdateButtons();
    }
    public void SetDownloadAvailable(bool available) => download.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
    public void SetAssets(IReadOnlyList<string> labels)
    {
        assets.ItemsSource = labels; assets.SelectedIndex = labels.Count > 0 ? 0 : -1;
        assets.Visibility = labels.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SetStatus(string text, double? percent = null)
    {
        status.Text = text;
        progress.Value = percent.HasValue ? Math.Clamp(percent.Value, 0, 100) : 0;
    }
    public void SetBusy(bool value, bool deviceWriting = false)
    {
        busy = value; writing = value && deviceWriting; UpdateButtons();
    }
    public void AppendLog(string text)
    {
        lines.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}"); while (lines.Count > 150) lines.Dequeue();
        log.Text = string.Join(Environment.NewLine, lines); log.ScrollToEnd();
    }
    private void UpdateButtons()
    {
        target.IsEnabled = !busy; assets.IsEnabled = !busy; check.IsEnabled = !busy; select.IsEnabled = !busy; download.IsEnabled = !busy;
        install.IsEnabled = !busy && ready; close.IsEnabled = !writing;
    }
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 15 };
    private Border Card(UIElement content) => new() { Style = (Style)FindResource("Card"), Child = content, Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 15) };
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}
