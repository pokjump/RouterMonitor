using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace RouterMonitor.Wpf.Services;

/// <summary>Wraps a Windows Forms NotifyIcon so the WPF app can live in the system tray and show balloon alerts.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Window _mainWindow;

    public event Action? ExitRequested;

    public TrayIconService(Window mainWindow)
    {
        _mainWindow = mainWindow;

        // Assembly.Location is empty in a single-file publish - use the actual running exe path instead.
        var exePath = Environment.ProcessPath;
        var icon = (exePath is not null ? Drawing.Icon.ExtractAssociatedIcon(exePath) : null)
            ?? Drawing.SystemIcons.Application;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Pokaż", null, (_, _) => ShowWindow());
        menu.Items.Add("Zakończ", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Router Monitor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();
    }

    public void ShowWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ShowDeviceAlert(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(6000);
    }

    public void ShowError(string message)
    {
        _notifyIcon.BalloonTipTitle = "Router Monitor";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(6000, "Router Monitor", message, Forms.ToolTipIcon.Warning);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
