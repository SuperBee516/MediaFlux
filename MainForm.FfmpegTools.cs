using MediaFlux.Services;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private const string FfmpegDownloadUrl = "https://ffmpeg.org/download.html";

        private ToolStrip? _ffmpegWarningStrip;
        private ToolStripLabel? _ffmpegWarningLabel;

        private void InitializeFfmpegAvailabilityBanner()
        {
            _ffmpegWarningLabel = new ToolStripLabel
            {
                ForeColor = Color.FromArgb(102, 60, 0),
                Margin = new Padding(6, 1, 6, 2)
            };

            var openSettingsButton = new ToolStripLabel("Open Settings")
            {
                IsLink = true,
                ToolTipText = "Choose the locations of ffmpeg.exe and ffprobe.exe."
            };
            openSettingsButton.Click += (_, __) => ShowSettingsDialog(focusMediaTools: true);

            var downloadButton = new ToolStripLabel("Download FFmpeg")
            {
                IsLink = true,
                ToolTipText = "Open the official FFmpeg download page."
            };
            downloadButton.Click += (_, __) => OpenFfmpegDownloadPage();

            _ffmpegWarningStrip = new ToolStrip
            {
                BackColor = Color.FromArgb(255, 244, 206),
                CanOverflow = true,
                Dock = DockStyle.Top,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(2),
                RenderMode = ToolStripRenderMode.System,
                Stretch = true,
                Visible = false
            };
            _ffmpegWarningStrip.Items.Add(_ffmpegWarningLabel);
            _ffmpegWarningStrip.Items.Add(openSettingsButton);
            _ffmpegWarningStrip.Items.Add(downloadButton);

            Controls.Add(_ffmpegWarningStrip);
            _ffmpegWarningStrip.BringToFront();
            menuStrip1.BringToFront();
            RefreshFfmpegToolAvailability();
        }

        private FfmpegToolPaths ResolveFfmpegTools() =>
            FfmpegToolResolver.Resolve(
                AppPaths.InstallDirectory,
                _config.FfmpegPath,
                _config.FfprobePath);

        private void RefreshFfmpegToolAvailability()
        {
            if (_ffmpegWarningStrip == null || _ffmpegWarningLabel == null)
                return;

            var tools = ResolveFfmpegTools();
            var missing = GetMissingToolNames(tools);
            _ffmpegWarningStrip.Visible = missing.Length > 0;
            if (missing.Length == 0)
                return;

            string subject = missing.Length == 1
                ? $"{missing[0]} was not found."
                : $"{string.Join(" and ", missing)} were not found.";
            _ffmpegWarningLabel.Text =
                $"{subject} Add the file{(missing.Length == 1 ? string.Empty : "s")} to the MediaFlux Programs folder or choose paths in Settings.";
            _ffmpegWarningLabel.ToolTipText =
                $"Expected FFmpeg: {tools.FfmpegPath}{Environment.NewLine}Expected FFprobe: {tools.FfprobePath}";
        }

        private bool EnsureFfmpegToolsAvailable(
            bool requireFfmpeg = true,
            bool requireFfprobe = true)
        {
            var tools = ResolveFfmpegTools();
            bool available = (!requireFfmpeg || tools.HasFfmpeg) &&
                             (!requireFfprobe || tools.HasFfprobe);
            RefreshFfmpegToolAvailability();
            if (available)
                return true;

            var missing = GetMissingToolNames(tools)
                .Where(name => (name == "ffmpeg.exe" && requireFfmpeg) ||
                               (name == "ffprobe.exe" && requireFfprobe))
                .ToArray();
            string subject = missing.Length == 1
                ? $"{missing[0]} could not be found."
                : $"{string.Join(" and ", missing)} could not be found.";

            var result = MessageBox.Show(
                this,
                subject + "\r\n\r\n" +
                "Place the required executable files in the MediaFlux Programs folder, " +
                "or select their locations under Tools > Settings.\r\n\r\n" +
                "Open Settings now?",
                "FFmpeg tools required",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result == DialogResult.Yes)
                ShowSettingsDialog(focusMediaTools: true);

            return false;
        }

        private static string[] GetMissingToolNames(FfmpegToolPaths tools)
        {
            return new[]
                {
                    tools.HasFfmpeg ? null : "ffmpeg.exe",
                    tools.HasFfprobe ? null : "ffprobe.exe"
                }
                .Where(name => name != null)
                .Cast<string>()
                .ToArray();
        }

        private void OpenFfmpegDownloadPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = FfmpegDownloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "MediaFlux could not open the FFmpeg download page.\r\n\r\n" +
                    FfmpegDownloadUrl + "\r\n\r\n" + ex.Message,
                    "Unable to open download page",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }
}
