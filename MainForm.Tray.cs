using MediaFlux.Services;

namespace MediaFlux;

public partial class MainForm
{
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private ToolStripMenuItem? _trayStatusItem;
    private ToolStripMenuItem? _trayNextRunItem;
    private bool _trayTipShown;
    private bool _movingToTray;

    private void InitializeSystemTray()
    {
        if (_trayIcon != null)
            return;

        _trayMenu = new ContextMenuStrip();
        _trayStatusItem = new ToolStripMenuItem("Status: Ready") { Enabled = false };
        _trayNextRunItem = new ToolStripMenuItem { Enabled = false, Visible = false };
        _trayMenu.Items.Add(_trayStatusItem);
        _trayMenu.Items.Add(_trayNextRunItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Open MediaFlux", null, (_, __) => RestoreFromSystemTray());
        _trayMenu.Items.Add("Job Manager...", null, (_, __) => { RestoreFromSystemTray(); ShowJobManager(); });
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Exit MediaFlux", null, (_, __) => Close());

        _trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "MediaFlux",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromSystemTray(); };
        _trayIcon.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromSystemTray(); };
        UpdateTrayStatus();
    }

    private void MinimizeToSystemTray()
    {
        if (_movingToTray || IsDisposed)
            return;

        InitializeSystemTray();
        _movingToTray = true;
        try
        {
            _trayIcon!.Visible = true;
            UpdateTrayStatus();
            ShowInTaskbar = false;
            Hide();
            if (!_trayTipShown)
            {
                _trayIcon.ShowBalloonTip(3500, "MediaFlux", "MediaFlux is still running in the background. Open it from the system tray.", ToolTipIcon.Info);
                _trayTipShown = true;
            }
        }
        finally { _movingToTray = false; }
    }

    private void RestoreFromSystemTray()
    {
        if (IsDisposed)
            return;

        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!_movingToTray && WindowState == FormWindowState.Minimized && _config?.MinimizeToSystemTrayWhenMinimized == true)
            MinimizeToSystemTray();
    }

    private void DisposeSystemTray()
    {
        if (_trayIcon == null)
            return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
        _trayStatusItem = null;
        _trayNextRunItem = null;
        _trayMenu?.Dispose();
        _trayMenu = null;
    }

    private void UpdateTrayStatus()
    {
        try
        {
            if (IsDisposed || _trayIcon == null)
                return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(UpdateTrayStatus));
                return;
            }

            TrayStatusInfo status = TrayStatusFormatter.Build(
                _runningEncodeJobs.Count,
                _encodingActive,
                _encodeJobs,
                DateTime.Now);
            _trayIcon.Text = TrayStatusFormatter.ToNotifyIconText(status.Status);
            if (_trayStatusItem != null)
                _trayStatusItem.Text = $"Status: {status.Status}";
            if (_trayNextRunItem != null)
            {
                _trayNextRunItem.Visible = !string.IsNullOrWhiteSpace(status.NextRun);
                _trayNextRunItem.Text = status.NextRun == null ? "" : $"Next: {status.NextRun}";
            }
        }
        catch
        {
            // Tray feedback must never affect encoding or scheduling.
        }
    }
}
