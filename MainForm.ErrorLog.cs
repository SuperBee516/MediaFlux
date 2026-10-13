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
            var centralLogPath = ErrorLogService.GetDefaultLogPath(Application.StartupPath);

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

            var lblView = new Label { Text = "View:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) };
            var cmbView = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 210,
                Margin = new Padding(0, 2, 8, 0)
            };
            cmbView.Items.AddRange(new object[]
            {
                "Central Error Log",
                "Latest Failure Diagnostic",
                "Latest Raw FFmpeg Evidence"
            });
            cmbView.SelectedIndex = 0;

            var btnRefresh = new Button { Text = "Refresh", Width = 90 };
            var btnOpenFolder = new Button { Text = "Open Folder", Width = 110 };
            var btnCopyPath = new Button { Text = "Copy Path", Width = 95 };
            var btnCopyAll = new Button { Text = "Copy All", Width = 95 };
            var btnClear = new Button { Text = "Clear Log", Width = 95 };
            var btnClose = new Button { Text = "Close", Width = 90 };
            bar.Controls.AddRange(new Control[] { lblView, cmbView, btnRefresh, btnOpenFolder, btnCopyPath, btnCopyAll, btnClear, btnClose });

            var lblPath = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Text = centralLogPath
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

            string? displayedPath = null;

            ErrorLogView SelectedView() => (ErrorLogView)Math.Max(0, cmbView.SelectedIndex);

            string? ResolveCurrentPath(ErrorLogView view) => view switch
            {
                ErrorLogView.CentralErrorLog => centralLogPath,
                ErrorLogView.LatestFailureDiagnostic => ErrorLogService.FindLatestFailureDiagnosticReport(),
                ErrorLogView.LatestRawFfmpegEvidence => ErrorLogService.FindLatestRawFfmpegEvidence(),
                _ => centralLogPath
            };

            void LoadLog()
            {
                ErrorLogView view = SelectedView();
                string? currentPath = ResolveCurrentPath(view);
                bool isCentral = view == ErrorLogView.CentralErrorLog;
                bool hasPath = !string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath);
                displayedPath = hasPath ? currentPath : null;
                btnClear.Enabled = hasPath;
                btnCopyPath.Enabled = hasPath;
                lblPath.Text = hasPath ? currentPath! : "Not available";

                if (!hasPath)
                {
                    txtLog.Text = view switch
                    {
                        ErrorLogView.LatestFailureDiagnostic => "No failure diagnostic reports are available.",
                        ErrorLogView.LatestRawFfmpegEvidence => "No raw FFmpeg evidence files are available.",
                        _ => "No error log has been created yet."
                    };
                    btnCopyAll.Enabled = txtLog.TextLength > 0;
                    return;
                }

                try
                {
                    txtLog.Text = ErrorLogService.ReadTail(
                        currentPath!,
                        ErrorLogViewerMaxBytes,
                        out bool truncated);
                    lblPath.Text = truncated && isCentral
                        ? $"{currentPath}  (showing the most recent 4 MB)"
                        : currentPath;
                    txtLog.SelectionStart = txtLog.TextLength;
                    txtLog.ScrollToCaret();
                    btnCopyAll.Enabled = txtLog.TextLength > 0;
                }
                catch (Exception ex)
                {
                    txtLog.Text = $"Unable to read selected log:{Environment.NewLine}{ex}";
                    btnCopyAll.Enabled = txtLog.TextLength > 0;
                }
            }

            btnRefresh.Click += (_, __) => LoadLog();
            btnOpenFolder.Click += (_, __) =>
            {
                var dir = Path.GetDirectoryName(centralLogPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", dir);
                }
            };
            btnCopyPath.Click += (_, __) =>
            {
                if (!string.IsNullOrWhiteSpace(displayedPath) && File.Exists(displayedPath))
                    Clipboard.SetText(displayedPath);
            };
            btnCopyAll.Click += (_, __) =>
            {
                if (txtLog.TextLength == 0)
                    return;

                try { Clipboard.SetText(txtLog.Text); }
                catch { }
            };
            btnClear.Click += (_, __) =>
            {
                ErrorLogView view = SelectedView();
                bool isCentral = view == ErrorLogView.CentralErrorLog;
                var ok = MessageBox.Show(
                    frm,
                    isCentral
                        ? "Clear the error log?"
                        : "Delete this failure diagnostic and its paired raw FFmpeg evidence?",
                    isCentral ? "Confirm Clear" : "Confirm Delete",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (ok != DialogResult.Yes)
                    return;

                try
                {
                    if (isCentral)
                    {
                        var dir = Path.GetDirectoryName(centralLogPath);
                        if (!string.IsNullOrWhiteSpace(dir))
                            Directory.CreateDirectory(dir);
                        File.WriteAllText(centralLogPath, string.Empty);
                    }
                    else
                    {
                        string? deleteError = null;
                        bool deleted = !string.IsNullOrWhiteSpace(displayedPath) &&
                            ErrorLogService.TryDeleteFailureDiagnosticPair(
                                displayedPath,
                                Path.GetDirectoryName(centralLogPath),
                                out deleteError);
                        if (!deleted && !string.IsNullOrWhiteSpace(deleteError))
                            MessageBox.Show(frm, deleteError, "Unable to Delete Diagnostic", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                    LoadLog();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(frm, ex.Message, "Unable to Clear Log", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            cmbView.SelectedIndexChanged += (_, __) => LoadLog();
            btnClose.Click += (_, __) => frm.Close();

            LoadLog();
            frm.ShowDialog(this);
        }
    }
}
