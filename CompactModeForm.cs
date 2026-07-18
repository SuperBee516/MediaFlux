using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MediaFlux
{
    internal sealed record CompactQueueSnapshot(
        string FileName,
        int Progress,
        int ActiveCount,
        int RemainingCount,
        string Eta,
        string State,
        bool IsEncoding,
        bool IsPaused);

    internal sealed class CompactModeForm : Form
    {
        private readonly MainForm _mainForm;
        private readonly Label _fileLabel;
        private readonly Label _summaryLabel;
        private readonly ProgressBar _progressBar;
        private readonly Button _pauseButton;
        private readonly Button _restoreButton;
        private readonly System.Windows.Forms.Timer _refreshTimer;
        private bool _applicationExiting;
        private Point _dragOrigin;

        internal CompactModeForm(MainForm mainForm, bool alwaysOnTop)
        {
            _mainForm = mainForm;
            TopMost = alwaysOnTop;
            Text = "Encode - Compact Mode";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            ClientSize = new Size(360, 108);
            MinimumSize = MaximumSize = Size;
            BackColor = Color.FromArgb(42, 43, 48);
            Padding = new Padding(10, 8, 10, 8);
            KeyPreview = true;

            _fileLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Location = new Point(11, 8),
                Size = new Size(338, 20),
                Text = "Encode queue"
            };
            _progressBar = new ProgressBar
            {
                Location = new Point(11, 31),
                Size = new Size(338, 12),
                Style = ProgressBarStyle.Continuous
            };
            _summaryLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = Color.FromArgb(210, 214, 220),
                Location = new Point(11, 51),
                Size = new Size(226, 42),
                TextAlign = ContentAlignment.MiddleLeft
            };
            _pauseButton = CreateButton("Pause", new Point(240, 55), new Size(52, 32));
            _pauseButton.Click += (_, __) => _mainForm.ToggleCompactQueuePause();
            _restoreButton = CreateButton("Open", new Point(297, 55), new Size(52, 32));
            _restoreButton.Click += (_, __) => RestoreMainWindow();

            Controls.AddRange(new Control[] { _fileLabel, _progressBar, _summaryLabel, _pauseButton, _restoreButton });

            var menu = new ContextMenuStrip();
            var topMostItem = new ToolStripMenuItem("Always on Top") { Checked = TopMost, CheckOnClick = true };
            topMostItem.CheckedChanged += (_, __) =>
            {
                TopMost = topMostItem.Checked;
                SavePreferences();
            };
            var stopItem = new ToolStripMenuItem("Stop Encoding");
            stopItem.Click += (_, __) => _mainForm.StopEncodingFromCompactMode();
            var restoreItem = new ToolStripMenuItem("Exit Compact Mode");
            restoreItem.Click += (_, __) => RestoreMainWindow();
            menu.Items.AddRange(new ToolStripItem[] { topMostItem, stopItem, new ToolStripSeparator(), restoreItem });
            ContextMenuStrip = menu;
            foreach (Control control in Controls)
                control.ContextMenuStrip = menu;

            WireDragSurface(this);
            WireDragSurface(_fileLabel);
            WireDragSurface(_summaryLabel);
            DoubleClick += (_, __) => RestoreMainWindow();
            _fileLabel.DoubleClick += (_, __) => RestoreMainWindow();
            _summaryLabel.DoubleClick += (_, __) => RestoreMainWindow();
            KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                    RestoreMainWindow();
            };

            _refreshTimer = new System.Windows.Forms.Timer { Interval = 250 };
            _refreshTimer.Tick += (_, __) => RefreshSnapshot();
            Shown += (_, __) =>
            {
                if (Location == Point.Empty)
                {
                    var area = Screen.FromControl(_mainForm).WorkingArea;
                    Location = new Point(area.Right - Width - 16, area.Top + 16);
                }
            };
            VisibleChanged += (_, __) =>
            {
                if (Visible)
                {
                    // Shown only fires once for a Form. Compact mode reuses this
                    // instance, so every Show() must restart live updates and
                    // replace any values left from the previous session.
                    RefreshSnapshot();
                    _refreshTimer.Start();
                }
                else
                {
                    _refreshTimer.Stop();
                }
            };
            FormClosing += CompactModeForm_FormClosing;
            Move += (_, __) =>
            {
                if (Visible && WindowState == FormWindowState.Normal)
                    SavePreferences();
            };
        }

        private static Button CreateButton(string text, Point location, Size size) => new()
        {
            Text = text,
            Location = location,
            Size = size,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(66, 68, 75),
            ForeColor = Color.White,
            TabStop = false
        };

        private void RefreshSnapshot()
        {
            if (_mainForm.IsDisposed)
            {
                CloseForApplicationExit();
                return;
            }

            try
            {
                var snapshot = _mainForm.GetCompactQueueSnapshot();
                _fileLabel.Text = snapshot.FileName;
                _fileLabel.ForeColor = snapshot.IsPaused ? Color.FromArgb(255, 205, 95) : Color.White;
                _progressBar.Value = snapshot.Progress;
                string active = snapshot.ActiveCount == 1 ? "1 active" : $"{snapshot.ActiveCount} active";
                string remaining = snapshot.RemainingCount == 1 ? "1 left" : $"{snapshot.RemainingCount} left";
                string eta = snapshot.Eta is "" or "--" ? "" : $" · ETA {snapshot.Eta}";
                _summaryLabel.Text = $"{snapshot.State} · {active} · {remaining}{eta}";
                _pauseButton.Text = snapshot.IsPaused ? "Resume" : "Pause";
                _pauseButton.Enabled = snapshot.IsEncoding;
            }
            catch
            {
                // Keep the compact window informative if queue rows are changing
                // during the same UI tick. The next 250 ms refresh retries.
                _summaryLabel.Text = "Refreshing queue status…";
            }
        }

        private void WireDragSurface(Control control)
        {
            control.MouseDown += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    _dragOrigin = new Point(e.X, e.Y);
            };
            control.MouseMove += (_, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    Point screen = control.PointToScreen(e.Location);
                    Location = new Point(screen.X - _dragOrigin.X, screen.Y - _dragOrigin.Y);
                }
            };
        }

        private void RestoreMainWindow()
        {
            SavePreferences();
            _refreshTimer.Stop();
            Hide();
            _mainForm.RestoreFromCompactMode();
        }

        private void SavePreferences() => _mainForm.SaveCompactWindowPreferences(Location, TopMost);

        private void CompactModeForm_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _refreshTimer.Stop();
            if (_applicationExiting)
                return;

            e.Cancel = true;
            RestoreMainWindow();
        }

        internal void CloseForApplicationExit()
        {
            _applicationExiting = true;
            _refreshTimer.Stop();
            Close();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var pen = new Pen(Color.FromArgb(90, 92, 100));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }
}
