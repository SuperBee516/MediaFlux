using MediaFlux.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private const int ErrorLogViewerMaxBytes = 4 * 1024 * 1024;

        private void ViewErrorLogToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            var logPath = ErrorLogService.GetDefaultLogPath(Application.StartupPath);

            var frm = new MediaFluxForm
            {
                Text = "Error Log",
                StartPosition = FormStartPosition.CenterParent,
                Width = 1000,
                Height = 650
            };

            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(8)
            };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false
            };

            var btnRefresh = new Button { Text = "Refresh", Width = 90 };
            var btnOpenFolder = new Button { Text = "Open Folder", Width = 110 };
            var btnCopyPath = new Button { Text = "Copy Path", Width = 95 };
            var btnClear = new Button { Text = "Clear Log", Width = 95 };
            var btnClose = new Button { Text = "Close", Width = 90 };
            bar.Controls.AddRange(new Control[] { btnRefresh, btnOpenFolder, btnCopyPath, btnClear, btnClose });

            var lblPath = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = logPath
            };

            var txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Font = new System.Drawing.Font("Consolas", 9F)
            };

            panel.Controls.Add(bar, 0, 0);
            panel.Controls.Add(lblPath, 0, 1);
            panel.Controls.Add(txtLog, 0, 2);
            frm.Controls.Add(panel);

            void LoadLog()
            {
                lblPath.Text = logPath;
                try
                {
                    txtLog.Text = ErrorLogService.ReadTail(
                        logPath,
                        ErrorLogViewerMaxBytes,
                        out bool truncated);
                    lblPath.Text = truncated
                        ? $"{logPath}  (showing the most recent 4 MB)"
                        : logPath;
                    txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.ScrollToCaret();
                }
                catch (Exception ex)
                {
                    txtLog.Text = $"Unable to read error log:{Environment.NewLine}{ex}";
                }
            }

            btnRefresh.Click += (_, __) => LoadLog();
            btnOpenFolder.Click += (_, __) =>
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", dir);
                }
            };
            btnCopyPath.Click += (_, __) => Clipboard.SetText(logPath);
            btnClear.Click += (_, __) =>
            {
                var ok = MessageBox.Show(
                    frm,
                    "Clear the error log?",
                    "Confirm Clear",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (ok != DialogResult.Yes)
                    return;

                try
                {
                    var dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrWhiteSpace(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(logPath, string.Empty);
                    LoadLog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(frm, ex.Message, "Unable to Clear Log", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            btnClose.Click += (_, __) => frm.Close();

            LoadLog();
            frm.ShowDialog(this);
        }
    }
}
