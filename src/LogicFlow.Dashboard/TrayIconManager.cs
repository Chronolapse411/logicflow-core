// LogicFlow.Dashboard — System Tray Integration
// Proprietary implementation by DelgadoLogic.Tech
// NotifyIcon, context menu, minimize-to-tray, balloon notifications

using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace LogicFlow.Dashboard;

/// <summary>
/// Manages the system tray icon for LogicFlow.
/// Provides context menu with quick actions, balloon notifications, and minimize-to-tray.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _trayIcon;
    private readonly MainWindow _mainWindow;
    private bool _disposed;

    public TrayIconManager(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;

        _trayIcon = new NotifyIcon
        {
            Text = "LogicFlow — AI-Powered Windows Optimization",
            Visible = true,
            Icon = LoadTrayIcon(),
            ContextMenuStrip = BuildContextMenu(),
        };

        _trayIcon.DoubleClick += (_, _) => RestoreWindow();
        _trayIcon.BalloonTipClicked += (_, _) => RestoreWindow();
    }

    private static Icon LoadTrayIcon()
    {
        // Try to load from app resources, fallback to system default
        try
        {
            var iconPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Assets", "Icons", "LogicFlow.ico");
            if (System.IO.File.Exists(iconPath))
                return new Icon(iconPath);
        }
        catch { }

        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.BackColor = System.Drawing.Color.FromArgb(13, 17, 23);
        menu.ForeColor = System.Drawing.Color.FromArgb(200, 200, 200);
        menu.Font = new Font("Segoe UI", 9F);

        menu.Items.Add(new ToolStripLabel("⚡ LogicFlow v1.0.0") { ForeColor = System.Drawing.Color.FromArgb(0, 229, 255) });
        menu.Items.Add(new ToolStripSeparator());

        var openItem = new ToolStripMenuItem("🏠 Open Dashboard");
        openItem.Click += (_, _) => RestoreWindow();
        menu.Items.Add(openItem);

        var quickScan = new ToolStripMenuItem("📊 Quick Full Scan");
        quickScan.Click += (_, _) => OnQuickScanFromTray();
        menu.Items.Add(quickScan);

        var secScan = new ToolStripMenuItem("🛡 Security Scan");
        secScan.Click += (_, _) => OnScanFromTray("security");
        menu.Items.Add(secScan);

        var perfScan = new ToolStripMenuItem("⚡ Performance Scan");
        perfScan.Click += (_, _) => OnScanFromTray("performance");
        menu.Items.Add(perfScan);

        var regScan = new ToolStripMenuItem("🔧 Registry Scan");
        regScan.Click += (_, _) => OnScanFromTray("registry");
        menu.Items.Add(regScan);

        menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("⚙ Settings");
        settings.Click += (_, _) => { RestoreWindow(); /* Navigate to settings */ };
        menu.Items.Add(settings);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("✕ Exit LogicFlow");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        return menu;
    }

    /// <summary>
    /// Minimizes the main window to the system tray.
    /// </summary>
    public void MinimizeToTray()
    {
        _mainWindow.Hide();
        ShowBalloon("LogicFlow", "Running in background. Double-click to restore.", ToolTipIcon.Info);
    }

    /// <summary>
    /// Restores the main window from the system tray.
    /// </summary>
    public void RestoreWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Focus();
    }

    /// <summary>
    /// Shows a balloon notification from the system tray.
    /// </summary>
    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, int timeout = 3000)
    {
        _trayIcon.ShowBalloonTip(timeout, title, text, icon);
    }

    /// <summary>
    /// Shows a scan result notification.
    /// </summary>
    public void NotifyScanComplete(string scanType, int issuesFound)
    {
        var icon = issuesFound > 5 ? ToolTipIcon.Warning :
                   issuesFound > 0 ? ToolTipIcon.Info : ToolTipIcon.None;
        var msg = issuesFound > 0
            ? $"{scanType} scan complete: {issuesFound} issues found. Click to review."
            : $"{scanType} scan complete: No issues found. Your system is healthy!";
        ShowBalloon("LogicFlow Scan Results", msg, icon);
    }

    private void OnQuickScanFromTray()
    {
        RestoreWindow();
        ShowBalloon("LogicFlow", "Starting full system scan...", ToolTipIcon.Info, 2000);
    }

    private void OnScanFromTray(string scanType)
    {
        RestoreWindow();
        ShowBalloon("LogicFlow", $"Starting {scanType} scan...", ToolTipIcon.Info, 2000);
    }

    private void ExitApplication()
    {
        _trayIcon.Visible = false;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
    }
}
