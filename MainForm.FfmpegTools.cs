using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
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
        private ToolStrip? _ffmpegWarningStrip;
        private ToolStripLabel? _ffmpegWarningLabel;
        private FfmpegEncoderCapabilities? _ffmpegEncoderCapabilities;
        private string? _encoderCapabilityWarning;

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

            var installButton = new ToolStripLabel("Install FFmpeg")
            {
                IsLink = true,
                ToolTipText = "Install the compatible MediaFlux FFmpeg package."
            };
            installButton.Click += (_, __) => InstallManagedFfmpeg();
            var locateButton = new ToolStripLabel("Locate Existing Installation") { IsLink = true, ToolTipText = "Choose an existing FFmpeg installation." };
            locateButton.Click += async (_, __) => await LocateExistingFfmpegAsync();

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
            _ffmpegWarningStrip.Items.Add(installButton);
            _ffmpegWarningStrip.Items.Add(locateButton);

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
            if (missing.Length == 0)
            {
                _ffmpegEncoderCapabilities =
                    FfmpegEncoderCapabilityService.GetCapabilities(
                        tools.FfmpegPath);
                if (!_ffmpegEncoderCapabilities.InspectionSucceeded)
                {
                    _ffmpegWarningStrip.Visible = true;
                    _ffmpegWarningLabel.Text =
                        "MediaFlux could not inspect the FFmpeg encoder list. " +
                        (_ffmpegEncoderCapabilities.ErrorMessage ??
                         "Encoder availability is unknown.");
                    _ffmpegWarningLabel.ToolTipText =
                        $"FFmpeg: {tools.FfmpegPath}";
                    return;
                }

                _ffmpegWarningStrip.Visible =
                    !string.IsNullOrWhiteSpace(_encoderCapabilityWarning);
                _ffmpegWarningLabel.Text =
                    _encoderCapabilityWarning ?? string.Empty;
                _ffmpegWarningLabel.ToolTipText =
                    $"FFmpeg: {tools.FfmpegPath}";
                return;
            }

            _ffmpegEncoderCapabilities = null;
            _ffmpegWarningStrip.Visible = true;
            string subject = missing.Length == 1
                ? $"{missing[0]} was not found."
                : $"{string.Join(" and ", missing)} were not found.";
            _ffmpegWarningLabel.Text =
                $"{subject} FFmpeg is required for media operations. Install a compatible package or locate an existing installation.";
            _ffmpegWarningLabel.ToolTipText =
                $"Expected FFmpeg: {tools.FfmpegPath}{Environment.NewLine}Expected FFprobe: {tools.FfprobePath}";
        }

        private FfmpegEncoderCapabilities GetFfmpegEncoderCapabilities()
        {
            FfmpegToolPaths tools = ResolveFfmpegTools();
            _ffmpegEncoderCapabilities =
                FfmpegEncoderCapabilityService.GetCapabilities(
                    tools.FfmpegPath);
            return _ffmpegEncoderCapabilities;
        }

        private bool IsEncoderCodecAvailable(
            string encoderId,
            VideoCodecFamily codecFamily)
        {
            FfmpegEncoderCapabilities capabilities =
                GetFfmpegEncoderCapabilities();
            if (!capabilities.InspectionSucceeded)
                return true;

            try
            {
                VideoEncoderSelection selection =
                    EncoderRegistry.Default.Resolve(
                        encoderId,
                        codecFamily).Selection;
                return capabilities.Contains(selection.FfmpegCodec);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private bool EnsureSelectedVideoEncoderAvailable(
            bool showMessage = true)
        {
            ResolvedVideoEncoder selected = EncoderRegistry.Default.Resolve(
                GetSelectedEncoderId(),
                GetSelectedVideoCodecFamily());
            FfmpegEncoderCapabilities capabilities =
                GetFfmpegEncoderCapabilities();
            if (!capabilities.InspectionSucceeded ||
                capabilities.Contains(
                    selected.Selection.FfmpegCodec))
            {
                return true;
            }

            if (showMessage)
            {
                MessageBox.Show(
                    this,
                    $"The configured FFmpeg build does not provide " +
                    $"'{selected.Selection.FfmpegCodec}', which is required by " +
                    $"{selected.Provider.Capabilities.DisplayName}.\r\n\r\n" +
                    "Choose another encoder or configure a different FFmpeg build " +
                    "under Tools > Settings.",
                    "Encoder unavailable",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            return false;
        }

        private void SetEncoderCapabilityWarning(string? message)
        {
            _encoderCapabilityWarning =
                string.IsNullOrWhiteSpace(message) ? null : message.Trim();
            RefreshFfmpegToolAvailability();
        }

        private void RefreshSelectedEncoderCapabilityWarning()
        {
            FfmpegEncoderCapabilities capabilities =
                GetFfmpegEncoderCapabilities();
            if (!capabilities.InspectionSucceeded)
                return;

            ResolvedVideoEncoder selected = EncoderRegistry.Default.Resolve(
                GetSelectedEncoderId(),
                GetSelectedVideoCodecFamily());
            SetEncoderCapabilityWarning(
                capabilities.Contains(selected.Selection.FfmpegCodec)
                    ? null
                    : $"{selected.Provider.Capabilities.DisplayName} requires " +
                      $"'{selected.Selection.FfmpegCodec}', which is not " +
                      "available in the configured FFmpeg build.");
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
                "Use the Install FFmpeg or Locate Existing Installation action in the warning banner, " +
                "or open Tools > Settings for advanced/manual configuration.\r\n\r\n" +
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

        private void InstallManagedFfmpeg()
        {
            using var dialog = new FfmpegInstallationProgressForm((progress, token) => new FfmpegManagedComponentInstaller(AppPaths.InstallDirectory, log: message => ErrorLogService.Append(AppPaths.UserDataDirectory, "FFmpeg provisioning", details: message)).InstallAsync(progress, token));
            DialogResult result = dialog.ShowDialog(this);
            if (dialog.WasCanceled) return;
            if (result == DialogResult.OK && dialog.Result?.Succeeded == true)
            {
                FfmpegSetupService.InvalidateCaches();
                RecreateMediaServices();
                RefreshFfmpegToolAvailability();
                MessageBox.Show(this, "FFmpeg was installed and is ready to use. No restart is required.", "FFmpeg installation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (dialog.Result is { } failed)
            {
                MessageBox.Show(this, FfmpegUserFacingFailure(failed.Detail), "FFmpeg installation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task LocateExistingFfmpegAsync()
        {
            using var dialog = new OpenFileDialog { Title = "Locate FFmpeg installation", Filter = "FFmpeg executable (ffmpeg.exe)|ffmpeg.exe|Executable files (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            FfmpegLocateResult located = await FfmpegSetupService.LocateAndValidateAsync(dialog.FileName);
            if (!located.Succeeded)
            {
                MessageBox.Show(this, located.Detail, "FFmpeg installation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _config.FfmpegPath = located.FfmpegPath!;
            _config.FfprobePath = located.FfprobePath!;
            _config.Save(AppPaths.ConfigFile);
            FfmpegSetupService.InvalidateCaches();
            RecreateMediaServices();
            RefreshFfmpegToolAvailability();
        }

        private static string FfmpegUserFacingFailure(string detail)
        {
            if (detail.Contains("SHA-256", StringComparison.OrdinalIgnoreCase)) return "Package verification failed. The compatible FFmpeg package was not installed.";
            if (detail.Contains("traversal", StringComparison.OrdinalIgnoreCase)) return "The package could not be safely extracted.";
            if (detail.Contains("incomplete", StringComparison.OrdinalIgnoreCase)) return "The downloaded FFmpeg package is incomplete and was not installed.";
            if (detail.Contains("download", StringComparison.OrdinalIgnoreCase) || detail.Contains("HTTP", StringComparison.OrdinalIgnoreCase)) return "The compatible FFmpeg package could not be downloaded.";
            return "FFmpeg could not be installed. Review MediaFlux diagnostics for details.";
        }
    }
}
