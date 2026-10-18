using System.Windows;
using Forms = System.Windows.Forms;

namespace MicMini.Desktop.Services;

/// <summary>Keeps the controller running when the window is minimized; never opens device handles.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window window;
    private readonly Forms.NotifyIcon tray;
    private readonly Forms.ContextMenuStrip menu;
    private readonly System.Drawing.Icon icon;
    private WindowState restoreState = WindowState.Normal;
    private bool disposed;

    public TrayIconService(Window window)
    {
        this.window = window;
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/MicMini.Desktop;component/Assets/dji-mimo.ico"))
            ?? throw new IOException("无法读取托盘图标资源。");
        using (resource.Stream)
        using (var original = new System.Drawing.Icon(resource.Stream)) icon = (System.Drawing.Icon)original.Clone();
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开控制台", null, (_, _) => Restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出程序", null, (_, _) =>
        {
            // Reuse normal close handling: save recording and honor firmware installation protection.
            if (disposed) return;
            Restore();
            window.Close();
        });
        tray = new Forms.NotifyIcon
        {
            Icon = icon, ContextMenuStrip = menu, Text = "Mic Mini Desktop\n正在读取设备电量…", Visible = true
        };
        tray.DoubleClick += (_, _) => Restore();
        window.StateChanged += OnStateChanged;
        window.Closed += OnClosed;
    }

    public void Update(AppSnapshot snapshot)
    {
        if (disposed) return;
        var batteries = snapshot.Devices.Where(device => device.Battery is not null).Select(device =>
        {
            var battery = device.Battery!;
            var level = battery.LevelRaw is <= 7 ? $"电量等级 {battery.LevelRaw}" : "电量未读取";
            return $"{device.Role}：{level}{(battery.Charging ? " · 充电中" : "")}";
        }).ToArray();
        var lines = new List<string> { "Mic Mini Desktop" };
        lines.AddRange(batteries.Length == 0 ? ["发射端未连接，电量不可用"] : batteries);
        if (snapshot.IsRecording) lines.Add("正在录音 · " + snapshot.RecordingTime);
        lines.Add("双击打开控制台");
        var text = string.Join('\n', lines);
        // NotifyIcon.Text supports at most 127 characters on this Windows/.NET target.
        if (text.Length > 127) text = text[..126] + "…";
        if (tray.Text != text) tray.Text = text;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (window.WindowState == WindowState.Minimized) window.Hide();
        else restoreState = window.WindowState;
    }

    private void Restore()
    {
        if (disposed) return;
        window.Show();
        window.WindowState = restoreState;
        window.Activate();
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.StateChanged -= OnStateChanged;
        window.Closed -= OnClosed;
        tray.Visible = false;
        tray.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
