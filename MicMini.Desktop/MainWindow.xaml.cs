using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MicMini.Desktop;

public partial class MainWindow : Window
{
    public event Action? RefreshRequested;
    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? StartRecordingRequested;
    public event Action? StopRecordingRequested;
    public event Action? ExportDiagnosticsRequested;
    public event Action? OpenRecordingsRequested;
    public event Action? FirmwareRequested;
    public event Action<string, int>? SettingRequested;

    private sealed record SettingControls(Border Row, TextBlock Title, TextBlock Description,
        TextBlock Availability, ComboBox Selector, string[] Options);

    private readonly Dictionary<string, SettingControls> settingControls = new();
    private readonly Dictionary<string, TextBlock> summaryValues = new();
    private string settingsStructure = "";
    private string deviceStructure = "\0";
    private string logsText = "";
    private bool rendering;
    private static readonly Brush AccentBrush = ColorBrush("#7AE2C5");
    private static readonly Brush MutedBrush = ColorBrush("#96A6B7");
    private static readonly Brush WarningBrush = ColorBrush("#EDC58B");

    public MainWindow()
    {
        InitializeComponent();
        SelectPage(0);
        ShowNoDevices();
    }

    public void Render(AppSnapshot state)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Render(state));
            return;
        }

        rendering = true;
        try
        {
            SetText(ConnectionTitleText, state.ConnectionTitle);
            SetText(ConnectionDetailText, state.ConnectionDetail);
            SetText(ConnectionBadge, state.ParametersReady ? "调参已连接" : state.ControlConnected ? "信息已连接" : "信息未连接");
            ConnectionBadge.Foreground = state.ControlConnected ? AccentBrush : WarningBrush;
            SetText(SidebarControl, state.ParametersReady ? "设备参数通道 · 已连接" : state.ControlConnected ? "设备信息通道 · 已连接" : "设备信息通道 · 未连接");
            SidebarControl.Foreground = state.ControlConnected ? AccentBrush : MutedBrush;
            SetText(SidebarAudio, state.AudioAvailable ? "USB 音频 · 可用" : "USB 音频 · 不可用");
            SidebarAudio.Foreground = state.AudioAvailable ? AccentBrush : MutedBrush;
            SetText(AudioBadge, state.AudioAvailable ? "音频可用" : "音频不可用");
            AudioBadge.Foreground = state.AudioAvailable ? AccentBrush : MutedBrush;
            SetText(AudioStatusText, state.AudioStatus);
            SetText(ControlDiagnosticText, state.ParametersReady ? "USB 调参在线；设置结果以设备回读为准" : state.ControlConnected ? "可读取固件与 TX 信息；参数通道未连接" : "未连接，设备信息尚待读取");
            SetText(AudioDiagnosticText, state.AudioStatus);
            SetText(FirmwareText, Readable(state.Firmware));
            SetText(SerialText, Readable(state.Serial));
            SetText(RecordingTimeText, string.IsNullOrWhiteSpace(state.RecordingTime) ? "00:00:00" : state.RecordingTime);
            SetText(RecordingStateText, state.IsRecording ? "正在录制 · 保存为 WAV"
                : !state.TransmitterConnected ? "请连接发射端后录音"
                : !state.AudioAvailable ? "等待可用的 USB 音频输入"
                : state.Busy ? "正在与设备通信，请稍候…" : "已就绪，等待开始录音");
            RecordingDot.Fill = state.IsRecording ? ColorBrush("#F27D8C") : MutedBrush;
            SetText(RecordingPathText, string.IsNullOrWhiteSpace(state.RecordingPath) ? "文件保存位置将在录音时显示。" : state.RecordingPath);
            RecordingPathText.ToolTip = state.RecordingPath;
            StartRecordingButton.Visibility = state.IsRecording ? Visibility.Collapsed : Visibility.Visible;
            StopRecordingButton.Visibility = state.IsRecording ? Visibility.Visible : Visibility.Collapsed;
            StartRecordingButton.IsEnabled = state.CanStartRecording;
            StopRecordingButton.IsEnabled = state.IsRecording;
            ConnectButton.Visibility = state.ControlConnected ? Visibility.Collapsed : Visibility.Visible;
            DisconnectButton.Visibility = state.ControlConnected ? Visibility.Visible : Visibility.Collapsed;
            ConnectButton.IsEnabled = !state.Busy;
            DisconnectButton.IsEnabled = !state.Busy;
            RefreshButton.IsEnabled = !state.Busy;
            FirmwareButton.IsEnabled = state.ParametersReady && !state.Busy && !state.IsRecording;
            SetText(FooterText, state.Busy ? "正在与设备通信…" : state.IsRecording ? "录音进行中 · 关闭窗口会自动结束并保存" : "设备调参与 USB 音频分别管理 · 每次设置均核对回读");

            UpdateMeter(LeftMeter, LeftLevelText, state.PeakLeft, state.AudioAvailable);
            UpdateMeter(RightMeter, RightLevelText, state.PeakRight, state.AudioAvailable);
            UpdateDevices(state.Devices);
            UpdateSettings(state.Settings, state.Busy);
            UpdateLogs(state.Logs);
        }
        finally { rendering = false; }
    }

    private static string Readable(string? value) => string.IsNullOrWhiteSpace(value) ? "未读取" : value;
    private static Brush ColorBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
    private static void SetText(TextBlock block, string? value)
    {
        var text = value ?? "";
        if (block.Text != text) block.Text = text;
    }

    private static void UpdateMeter(ProgressBar meter, TextBlock label, double peak, bool available)
    {
        peak = double.IsFinite(peak) ? Math.Clamp(peak, 0, 1) : 0;
        var db = peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        meter.Value = available ? Math.Clamp((db + 60) / 60 * 100, 0, 100) : 0;
        meter.Foreground = db > -1 ? ColorBrush("#F27D8C") : db > -6 ? WarningBrush : AccentBrush;
        SetText(label, !available ? "— dBFS" : db < -60 ? "−∞ dBFS" : $"{db:0.0} dBFS");
    }

    private void UpdateDevices(IReadOnlyList<DeviceItem> devices)
    {
        var signature = string.Join("\u001e", devices.Select(d => $"{d.Name}\u001f{d.Role}\u001f{d.Status}\u001f{d.Detail}\u001f{d.Battery}"));
        if (signature == deviceStructure) return;
        deviceStructure = signature;
        DeviceCards.Children.Clear();
        SetText(DeviceCountText, devices.Count == 0 ? "未发现设备" : $"{devices.Count} 个设备条目");
        if (devices.Count == 0) { ShowNoDevices(); return; }
        DeviceCards.Columns = devices.Count >= 3 ? 3 : devices.Count;
        foreach (var device in devices)
        {
            var body = new StackPanel();
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition());
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { Text = device.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
            heading.Children.Add(name);
            var role = new Border { Background = ColorBrush("#2A3641"), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4), Child = new TextBlock { Text = device.Role, FontSize = 10, Foreground = ColorBrush("#A4B6C5") } };
            Grid.SetColumn(role, 1);
            heading.Children.Add(role);
            body.Children.Add(heading);
            var statusColor = ContainsAny(device.Status, "未", "未知", "离线", "不支持", "不可", "等待", "失败")
                ? MutedBrush : ContainsAny(device.Status, "已连接", "在线", "可用", "已发现") ? AccentBrush : MutedBrush;
            body.Children.Add(new TextBlock { Text = device.Status, Foreground = statusColor, FontSize = 12, Margin = new Thickness(0, 15, 0, 8) });
            body.Children.Add(new TextBlock { Text = device.Detail, Foreground = MutedBrush, FontSize = 11, LineHeight = 19, MinHeight = 38 });
            if (device.Battery is { } battery) body.Children.Add(CreateBatteryIndicator(battery));
            DeviceCards.Children.Add(new Border { Style = (Style)FindResource("Card"), Padding = new Thickness(18), Margin = new Thickness(0, 0, 12, 0), Child = body });
        }
    }

    private static FrameworkElement CreateBatteryIndicator(BatteryItem battery)
    {
        // The protocol exposes a three-bit raw level, not a percentage or a confirmed full-charge threshold.
        const int rawMaximum = 7;
        var known = battery.LevelRaw is <= rawMaximum;
        var label = known ? $"等级 {battery.LevelRaw}" : "未读取";
        if (battery.Charging) label += " · 充电中";
        var hint = known ? "按设备原始等级 0–7 显示条形长度，不代表电量百分比；满电对应的等级尚未确认。" : "尚未读取到有效电量，灰色进度条不表示电量为零。";
        var panel = new StackPanel { Margin = new Thickness(0, 15, 0, 0), ToolTip = hint };
        var heading = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = "电量", FontSize = 11, Foreground = MutedBrush });
        var value = new TextBlock { Text = label, FontSize = 11, Foreground = known ? AccentBrush : MutedBrush };
        Grid.SetColumn(value, 1); heading.Children.Add(value); panel.Children.Add(heading);
        var bar = new ProgressBar
        {
            Minimum = 0, Maximum = rawMaximum, Value = known ? battery.LevelRaw!.Value : 0,
            Height = 8, Foreground = known ? AccentBrush : MutedBrush, Opacity = known ? 1 : 0.45,
            IsIndeterminate = false, ToolTip = hint
        };
        System.Windows.Automation.AutomationProperties.SetName(bar, "电量：" + label);
        System.Windows.Automation.AutomationProperties.SetHelpText(bar, hint);
        panel.Children.Add(bar);
        return panel;
    }

    private void ShowNoDevices()
    {
        DeviceCards.Children.Clear();
        DeviceCards.Columns = 1;
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "等待发现接收端与发射端", FontSize = 15, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = "请保持接收端连接电脑，并让发射端处于连接状态。", Foreground = MutedBrush, Margin = new Thickness(0, 9, 0, 0) });
        DeviceCards.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 12, 0), Child = body });
    }

    private static string SettingGroup(SettingItem setting)
    {
        var text = setting.Key + " " + setting.Title;
        if (ContainsAny(text, "关机", "电源", "超时", "power", "shutdown", "sleep", "timeout")) return "电源与连接";
        if (ContainsAny(text, "指示灯", "灯光", "按键", "按钮", "LED", "button", "light", "link", "配对")) return "指示灯与操作";
        return "声音与录制";
    }

    private static bool ContainsAny(string text, params string[] keywords) => keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private void UpdateSettings(IReadOnlyList<SettingItem> settings, bool busy)
    {
        var structure = string.Join("\u001e", settings.Select(s => $"{s.Key}\u001f{s.Title}\u001f{string.Join('\u001d', s.Options)}"));
        if (structure != settingsStructure || (settings.Count == 0 && SettingsPanel.Children.Count == 0))
        {
            settingsStructure = structure;
            BuildSettings(settings);
        }
        foreach (var setting in settings)
        {
            if (!settingControls.TryGetValue(setting.Key, out var controls)) continue;
            SetText(controls.Title, setting.Title);
            SetText(controls.Description, setting.Description);
            SetText(controls.Availability, setting.Availability);
            controls.Availability.Foreground = setting.Enabled ? MutedBrush : WarningBrush;
            controls.Selector.IsEnabled = setting.Enabled && !busy;
            controls.Selector.ToolTip = setting.Availability;
            var valid = setting.Value.HasValue && setting.Value.Value >= 0 && setting.Value.Value < setting.Options.Length;
            var selected = valid ? setting.Value!.Value + 1 : 0;
            // A background poll must not dismiss the menu or move its highlighted option.
            if (!controls.Selector.IsDropDownOpen && controls.Selector.SelectedIndex != selected)
                controls.Selector.SelectedIndex = selected;
            if (summaryValues.TryGetValue(setting.Key, out var summary))
            {
                SetText(summary, valid ? setting.Options[setting.Value!.Value] : "未读取");
                summary.Foreground = valid ? AccentBrush : MutedBrush;
            }
        }
    }

    private void BuildSettings(IReadOnlyList<SettingItem> settings)
    {
        SettingsPanel.Children.Clear();
        SummarySettings.Children.Clear();
        settingControls.Clear();
        summaryValues.Clear();
        if (settings.Count == 0)
        {
            SettingsPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Child = new TextBlock { Text = "尚未取得设备设置列表。请刷新设备并查看连接详情。", Foreground = MutedBrush } });
            SummarySettings.Children.Add(new TextBlock { Text = "等待设备参数", Foreground = MutedBrush, Margin = new Thickness(0, 5, 0, 5) });
            return;
        }
        foreach (var group in settings.GroupBy(SettingGroup))
        {
            var rows = new StackPanel();
            rows.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 0, 0, 7) });
            foreach (var setting in group)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(215) });
                var information = new StackPanel { Margin = new Thickness(0, 0, 30, 0), VerticalAlignment = VerticalAlignment.Center };
                var title = new TextBlock { Text = setting.Title, FontWeight = FontWeights.SemiBold, FontSize = 14 };
                var description = new TextBlock { Text = setting.Description, Foreground = MutedBrush, FontSize = 12, Margin = new Thickness(0, 5, 0, 0), LineHeight = 20 };
                var availability = new TextBlock { Text = setting.Availability, Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(0, 6, 0, 0), LineHeight = 18 };
                information.Children.Add(title);
                information.Children.Add(description);
                information.Children.Add(availability);
                grid.Children.Add(information);
                var selector = new ComboBox { VerticalAlignment = VerticalAlignment.Center, Tag = setting.Key };
                selector.Items.Add(new ComboBoxItem { Content = "未读取", IsEnabled = false });
                foreach (var option in setting.Options) selector.Items.Add(new ComboBoxItem { Content = option });
                selector.SelectedIndex = 0;
                selector.SelectionChanged += Setting_SelectionChanged;
                Grid.SetColumn(selector, 1);
                grid.Children.Add(selector);
                var row = new Border { BorderBrush = ColorBrush("#2A333E"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 20, 0, 20), Child = grid };
                rows.Children.Add(row);
                settingControls[setting.Key] = new SettingControls(row, title, description, availability, selector, setting.Options);

                var summary = new StackPanel();
                summary.Children.Add(new TextBlock { Text = setting.Title, Foreground = MutedBrush, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
                var value = new TextBlock { Text = "未读取", Foreground = MutedBrush, FontSize = 14, FontWeight = FontWeights.SemiBold };
                summary.Children.Add(value);
                SummarySettings.Children.Add(new Border { Background = ColorBrush("#121A21"), CornerRadius = new CornerRadius(8), Padding = new Thickness(15, 12, 15, 12), MinWidth = 148, Margin = new Thickness(0, 0, 10, 10), Child = summary });
                summaryValues[setting.Key] = value;
            }
            SettingsPanel.Children.Add(new Border { Style = (Style)FindResource("Card"), Margin = new Thickness(0, 0, 0, 18), Padding = new Thickness(22, 22, 22, 2), Child = rows });
        }
    }

    private void UpdateLogs(IReadOnlyList<string> logs)
    {
        var text = logs.Count == 0 ? "尚无诊断日志。" : string.Join(Environment.NewLine, logs);
        if (logsText == text) return;
        logsText = text;
        var atBottom = LogsTextBox.VerticalOffset >= LogsTextBox.ExtentHeight - LogsTextBox.ViewportHeight - 4;
        var selectionStart = LogsTextBox.SelectionStart;
        var selectionLength = LogsTextBox.SelectionLength;
        var offset = LogsTextBox.VerticalOffset;
        LogsTextBox.Text = text;
        if (atBottom && !LogsTextBox.IsKeyboardFocused) LogsTextBox.ScrollToEnd();
        else
        {
            LogsTextBox.Select(Math.Min(selectionStart, text.Length), Math.Min(selectionLength, Math.Max(0, text.Length - selectionStart)));
            LogsTextBox.ScrollToVerticalOffset(offset);
        }
    }

    private void SelectPage(int page)
    {
        OverviewPage.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        SoundPage.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        var buttons = new[] { OverviewNav, SoundNav, DiagnosticsNav };
        for (var i = 0; i < buttons.Length; i++)
        {
            buttons[i].Background = i == page ? ColorBrush("#263C36") : Brushes.Transparent;
            buttons[i].Foreground = i == page ? AccentBrush : MutedBrush;
        }
        PageTitle.Text = page switch { 1 => "声音与控制", 2 => "设备与诊断", _ => "设备概览" };
        PageSubtitle.Text = page switch { 1 => "让每一次收音，都符合你的习惯。", 2 => "查看真实设备状态，追踪每一次通信。", _ => "连接、电平与录音，尽在眼前。" };
    }

    private void Setting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || sender is not ComboBox selector || !selector.IsEnabled || selector.SelectedIndex < 1) return;
        if (selector.Tag is string key) SettingRequested?.Invoke(key, selector.SelectedIndex - 1);
    }
    private void Overview_Click(object sender, RoutedEventArgs e) => SelectPage(0);
    private void Sound_Click(object sender, RoutedEventArgs e) => SelectPage(1);
    private void Diagnostics_Click(object sender, RoutedEventArgs e) => SelectPage(2);
    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();
    private void Connect_Click(object sender, RoutedEventArgs e) => ConnectRequested?.Invoke();
    private void Disconnect_Click(object sender, RoutedEventArgs e) => DisconnectRequested?.Invoke();
    private void StartRecording_Click(object sender, RoutedEventArgs e) => StartRecordingRequested?.Invoke();
    private void StopRecording_Click(object sender, RoutedEventArgs e) => StopRecordingRequested?.Invoke();
    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e) => ExportDiagnosticsRequested?.Invoke();
    private void OpenRecordings_Click(object sender, RoutedEventArgs e) => OpenRecordingsRequested?.Invoke();
    private void Firmware_Click(object sender, RoutedEventArgs e) => FirmwareRequested?.Invoke();
}
