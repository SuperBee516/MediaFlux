using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;

namespace MediaFlux
{
    public partial class MainForm
    {
        private CancellationTokenSource? _sampleComparisonCts;

        private async void btnSampleComparison_Click(object? sender, EventArgs e)
        {
            if (_sampleComparisonCts != null)
            {
                _sampleComparisonCts.Cancel();
                return;
            }

            if (_encodingActive)
            {
                ShowStatusInfo("Finish or stop the active encode before generating comparison samples.");
                return;
            }
            if (_mediaRemuxCts != null)
            {
                ShowStatusInfo("Finish or cancel the active remux before generating comparison samples.");
                return;
            }
            if (!EnsureFfmpegToolsAvailable() ||
                !EnsureSelectedVideoEncoderAvailable())
            {
                return;
            }

            DataGridViewRow? row = dgvEncodeQueue.CurrentRow;
            if (row == null || row.IsNewRow)
                row = dgvEncodeQueue.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => !r.IsNewRow && r.Visible);

            string sourcePath = row?.Tag is RowMeta meta ? meta.Path : row?.Tag as string ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                ShowStatusInfo("Select a video in the encode queue before generating samples.");
                return;
            }

            double durationSeconds = (row?.Tag as RowMeta)?.DurationSec ?? 0;
            if (durationSeconds <= 0)
                durationSeconds = await Task.Run(() => _mediaInfoService.GetDurationSeconds(sourcePath));
            if (durationSeconds <= 0)
            {
                ShowStatusInfo("MediaFlux could not determine the selected video's duration.");
                return;
            }

            _sampleComparisonCts = new CancellationTokenSource();
            CancellationToken token = _sampleComparisonCts.Token;
            btnStartEncode.Enabled = false;
            btnSampleComparison.Text = "Cancel Sample";
            lblEncodeStatus.Text = $"Preparing samples for {Path.GetFileName(sourcePath)}…";

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var settings = BuildSampleComparisonSettings(row!, sourcePath, durationSeconds);
                    var service = new SampleComparisonService(
                        AppPaths.InstallDirectory,
                        _config.FfmpegPath,
                        _config.FfprobePath);
                    var progress = new Progress<string>(message =>
                    {
                        lblEncodeStatus.Text = message;
                        toolStripStatusLabel1.Text = message;
                    });

                    using var result = await service.GenerateAsync(
                        sourcePath,
                        TimeSpan.FromSeconds(durationSeconds),
                        settings,
                        progress,
                        token);

                    using var dialog = new SampleComparisonForm(
                        sourcePath,
                        BuildSampleSettingsSummary(settings),
                        result,
                        _config.ExternalPlayerPath);
                    dialog.ShowDialog(this);

                    switch (dialog.ResultAction)
                    {
                        case SampleComparisonAction.Accept:
                            ShowStatusInfo("Sample settings accepted. Start Encoding when you are ready.");
                            return;

                        case SampleComparisonAction.IncreaseQuality:
                            AdjustSampleQuality(increaseQuality: true);
                            break;

                        case SampleComparisonAction.IncreaseCompression:
                            AdjustSampleQuality(increaseQuality: false);
                            break;

                        case SampleComparisonAction.TryAnotherCodec:
                            SelectNextSampleCodec();
                            break;

                        default:
                            ShowStatusInfo("Sample comparison canceled.");
                            return;
                    }

                    lblEncodeStatus.Text = "Regenerating samples with the adjusted settings…";
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
                ShowStatusInfo("Sample generation canceled.");
            }
            catch (Exception ex)
            {
                string logPath = ErrorLogService.Append(
                    AppPaths.InstallDirectory,
                    "Pre-encode sample comparison failed",
                    sourcePath,
                    exception: ex);
                MessageBox.Show(
                    this,
                    "MediaFlux could not generate the comparison clips.\r\n\r\n" +
                    "The details were written to the central error log:\r\n" + logPath,
                    "Sample Comparison",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                ShowStatusInfo("Sample comparison failed. See the central error log.");
            }
            finally
            {
                _sampleComparisonCts.Dispose();
                _sampleComparisonCts = null;
                btnSampleComparison.Text = "Compare Samples";
                btnStartEncode.Enabled = !_encodingActive;
                if (!_encodingActive)
                    lblEncodeStatus.Text = string.Empty;
            }
        }

        private SampleComparisonSettings BuildSampleComparisonSettings(
            DataGridViewRow row,
            string sourcePath,
            double durationSeconds)
        {
            int? audioChannels = GetSelectedAudioChannels();
            ValidatedEncoderSettings validated =
                GetValidatedEncoderSettingsFromUi(
                    includeConcurrentSessions: false);
            string videoCodec =
                validated.Resolved.Selection.FfmpegCodec;
            string profileText = (row.Tag as RowMeta)?.CustomCompressionProfile
                ?? comboCompressionProfile.SelectedItem?.ToString()
                ?? comboCompressionProfile.Text;

            double? targetMb = null;
            var meta = row.Tag as RowMeta;
            if (meta?.CustomTargetMb.HasValue == true)
            {
                targetMb = meta.CustomTargetMb;
            }
            else if (profileText.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
            {
                int? sourceKbps = ProbeSourceVideoBitrateKbps(sourcePath);
                if (sourceKbps.HasValue)
                    targetMb = sourceKbps.Value * 1.15 * durationSeconds / 8192d;
            }
            else if (!chkAutoTargetSize.Checked &&
                     double.TryParse(txtTargetSize.Text, out double manualMb) &&
                     manualMb > 0)
            {
                targetMb = manualMb;
            }
            else
            {
                double estimated = _sizeEstimateService.EstimateAutoTargetMbSmart(
                    sourcePath,
                    profileText,
                    validated.Resolved.Selection,
                    GetDefaultQualityForSelection(),
                    GetEstimateTargetHeight());
                if (estimated > 0)
                    targetMb = estimated;
                else if (_estimatedSizeMap.TryGetValue(sourcePath, out double cachedEstimate) &&
                         cachedEstimate > 0)
                    targetMb = cachedEstimate;
            }

            return new SampleComparisonSettings
            {
                Encoder = validated.Resolved.Selection,
                VideoCodec = videoCodec,
                UseGpu = validated.UseGpu,
                ProjectedTargetMb = targetMb,
                ScaleMode = GetSelectedScaleMode(),
                EncoderPreset = validated.Preset,
                QualityValue = validated.QualityValue,
                TenBit = validated.TenBit,
                AudioChannels = audioChannels,
                ClipSeconds = 25
            };
        }

        private string BuildSampleSettingsSummary(SampleComparisonSettings settings)
        {
            string scale = comboResolution?.Text ?? "Original resolution";
            string profile = comboCompressionProfile?.Text ?? "Current profile";
            string depth = settings.TenBit ? "10-bit" : "8-bit";
            return $"{comboVideoFormat.Text} • {comboEncoderMode.Text} • {profile} • {scale} • {depth}";
        }

        private void AdjustSampleQuality(bool increaseQuality)
        {
            int current = comboCompressionProfile.SelectedIndex;
            int desired = increaseQuality
                ? Math.Max(0, current - 1)
                : Math.Min(comboCompressionProfile.Items.Count - 1, current + 1);

            if (desired != current)
            {
                comboCompressionProfile.SelectedIndex = desired;
            }
            else if (nudAutoQuality != null)
            {
                decimal delta = increaseQuality ? -2 : 2;
                nudAutoQuality.Value = Math.Clamp(
                    nudAutoQuality.Value + delta,
                    nudAutoQuality.Minimum,
                    nudAutoQuality.Maximum);
            }

            UpdateEncodePreview();
            ScheduleEstimateRefresh();
        }

        private void SelectNextSampleCodec()
        {
            if (comboVideoFormat.Items.Count == 0)
                return;

            comboVideoFormat.SelectedIndex =
                (Math.Max(0, comboVideoFormat.SelectedIndex) + 1) % comboVideoFormat.Items.Count;
            UpdateEncodePreview();
            ScheduleEstimateRefresh();
        }
    }
}
