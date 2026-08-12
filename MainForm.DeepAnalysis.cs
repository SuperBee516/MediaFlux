using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux
{
    public partial class MainForm
    {
        private CancellationTokenSource? _deepAnalysisCts;

        private async void DeepAnalyzeSelected_Click(object? sender, EventArgs e)
        {
            if (_deepAnalysisCts != null)
            {
                _deepAnalysisCts.Cancel();
                return;
            }

            if (_encodingActive)
            {
                ShowStatusInfo("Finish or stop the active encode before running deep analysis.");
                return;
            }
            if (_mediaRemuxCts != null)
            {
                ShowStatusInfo("Finish or cancel the active remux before running deep analysis.");
                return;
            }

            if (!_config.SmartRecommendationsEnabled)
            {
                ShowStatusInfo("Enable Smart Encode Recommendations in Settings before running deep analysis.");
                return;
            }

            if (!EnsureFfmpegToolsAvailable() ||
                !EnsureSelectedVideoEncoderAvailable())
            {
                return;
            }

            var rows = dgvEncodeQueue.SelectedRows
                .Cast<DataGridViewRow>()
                .Where(row =>
                    !row.IsNewRow &&
                    row.Tag is RowMeta { IsDvdEncode: false } meta &&
                    File.Exists(meta.Path))
                .OrderBy(row => row.Index)
                .ToList();
            if (rows.Count == 0)
            {
                ShowStatusInfo("Select one or more normal video files before running deep analysis.");
                return;
            }

            _deepAnalysisCts = new CancellationTokenSource();
            CancellationToken token = _deepAnalysisCts.Token;
            SetQueueWorkCancelVisible(true);
            SetQueueProgress(0, rows.Count, visible: true);
            if (_analyzeQueueButton != null)
                _analyzeQueueButton.Enabled = false;

            int completed = 0;
            int failed = 0;
            try
            {
                var service = new DeepMediaAnalysisService(
                    AppPaths.InstallDirectory,
                    _config.FfmpegPath,
                    _config.FfprobePath);

                for (int index = 0; index < rows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    DataGridViewRow row = rows[index];
                    RowMeta meta = EnsureRowMeta(row);
                    string fileName = Path.GetFileName(meta.Path);
                    double durationSeconds = meta.DurationSec > 0
                        ? meta.DurationSec
                        : await Task.Run(
                            () => _mediaInfoService.GetDurationSeconds(meta.Path),
                            token);
                    if (durationSeconds <= 0)
                    {
                        failed++;
                        continue;
                    }

                    lblEncodeStatus.Text =
                        $"Deep analyzing {fileName} ({index + 1} of {rows.Count})…";
                    toolStripStatusLabel1.Text = lblEncodeStatus.Text;

                    try
                    {
                        SampleComparisonSettings currentSettings =
                            BuildSampleComparisonSettings(row, meta.Path, durationSeconds);
                        string profileText = meta.CustomCompressionProfile ??
                            comboCompressionProfile.SelectedItem?.ToString() ??
                            comboCompressionProfile.Text;
                        SampleComparisonSettings projectionSettings =
                            BuildDeepProjectionSettings(
                                currentSettings,
                                profileText,
                                applyProfileScale: !_config.StorageSavings.Enabled);
                        var progress = new Progress<string>(message =>
                        {
                            string status =
                                $"{fileName}: {message} ({index + 1} of {rows.Count})";
                            lblEncodeStatus.Text = status;
                            toolStripStatusLabel1.Text = status;
                        });

                        DeepMediaAnalysisResult result = await service.AnalyzeAsync(
                            meta.Path,
                            TimeSpan.FromSeconds(durationSeconds),
                            projectionSettings,
                            progress,
                            token);

                        double metadataEstimateMb =
                            _estimatedSizeMap.TryGetValue(meta.Path, out double currentEstimate)
                                ? currentEstimate
                                : 0;
                        bool appliedCalibratedEstimate =
                            chkAutoTargetSize.Checked &&
                            meta.CustomTargetMb is not > 0 &&
                            !_config.StorageSavings.Enabled &&
                            result.ProjectedOutputMb is > 0;
                        if (appliedCalibratedEstimate)
                        {
                            ApplyCalibratedEstimate(
                                row,
                                meta,
                                result,
                                metadataEstimateMb);
                        }

                        SmartEncodeRecommendation? baseline =
                            appliedCalibratedEstimate
                                ? BuildBaselineRecommendation(row, meta, projectionSettings)
                                : meta.BaselineEncodeRecommendation ??
                                  meta.EncodeRecommendation ??
                                  BuildBaselineRecommendation(row, meta, projectionSettings);
                        if (baseline == null)
                        {
                            failed++;
                            continue;
                        }

                        double intendedOutputMb =
                            _estimatedSizeMap.TryGetValue(meta.Path, out double estimate)
                                ? estimate
                                : 0;
                        var refined = new SmartEncodeDecisionService()
                            .RefineWithDeepAnalysis(
                                baseline,
                                result,
                                meta.ContentHint,
                                intendedOutputMb);

                        meta.BaselineEncodeRecommendation = baseline;
                        meta.DeepAnalysis = result;
                        ApplySmartRecommendation(row, refined, updateBaseline: false);
                        completed++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        ErrorLogService.Append(
                            AppPaths.InstallDirectory,
                            "Smart Encode deep analysis failed",
                            meta.Path,
                            ex);
                    }

                    SetQueueProgress(index + 1, rows.Count, visible: true);
                }

                string summary = failed == 0
                    ? $"Deep analysis completed for {completed:N0} file(s)."
                    : $"Deep analysis completed for {completed:N0} file(s); {failed:N0} could not be analyzed.";
                ShowStatusInfo(summary);
            }
            catch (OperationCanceledException)
            {
                ShowStatusInfo(
                    $"Deep analysis canceled after {completed:N0} file(s). Completed recommendations were kept.");
            }
            finally
            {
                _deepAnalysisCts.Dispose();
                _deepAnalysisCts = null;
                SetQueueProgress(0, 0, visible: false);
                SetQueueWorkCancelVisible(false);
                UpdateAnalyzeQueueButtonState();
                if (!_encodingActive)
                    lblEncodeStatus.Text = string.Empty;
            }
        }

        private SmartEncodeRecommendation? BuildBaselineRecommendation(
            DataGridViewRow row,
            RowMeta meta,
            SampleComparisonSettings settings)
        {
            if (!_estimatedSizeMap.TryGetValue(meta.Path, out double estimatedMb) ||
                estimatedMb <= 0)
            {
                return null;
            }

            MediaInfoService.MediaInfo info = _mediaInfoService.GetInfo(meta.Path);
            double sourceMb = meta.SrcMb > 0
                ? meta.SrcMb
                : new FileInfo(meta.Path).Length / (1024d * 1024d);
            double duration = meta.DurationSec > 0
                ? meta.DurationSec
                : info.DurationSeconds ?? 0;
            int totalBitrate = info.TotalBitrateKbps ??
                (duration > 0 ? (int)Math.Round(sourceMb * 8192d / duration) : 0);
            int videoBitrate = info.BitrateKbps ?? 0;
            int audioBitrate = info.AudioBitrateKbps ??
                (videoBitrate > 0 ? Math.Max(0, totalBitrate - videoBitrate) : 0);
            string extension = Path.GetExtension(meta.Path);

            return new SmartEncodeDecisionService().Evaluate(
                new SmartEncodeSourceInfo
                {
                    Path = meta.Path,
                    SourceMb = sourceMb,
                    DurationSeconds = duration,
                    Width = info.Width ?? 0,
                    Height = info.Height ?? 0,
                    FramesPerSecond = info.Fps ?? meta.Fps,
                    VideoBitrateKbps = videoBitrate,
                    TotalBitrateKbps = totalBitrate,
                    AudioBitrateKbps = audioBitrate,
                    VideoStreamCount = info.VideoStreamCount,
                    AudioStreamCount = info.AudioStreamCount,
                    SubtitleStreamCount = info.SubtitleStreamCount,
                    VideoCodec = info.VideoCodec ?? meta.VideoCodec,
                    FormatName = info.FormatName ?? "",
                    FieldOrder = info.FieldOrder ?? "",
                    IsLikelyAnimation =
                        extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".apng", StringComparison.OrdinalIgnoreCase)
                },
                new SmartEncodeIntent
                {
                    TargetCodec = settings.Encoder?.FfmpegCodec ?? settings.VideoCodec,
                    TargetHeight = GetEstimateTargetHeight(),
                    EstimatedOutputMb = estimatedMb,
                    MinimumSavingsPercent = _config.MinimumExpectedSavingsPercent
                });
        }

        internal static SampleComparisonSettings BuildDeepProjectionSettings(
            SampleComparisonSettings current,
            string compressionProfile,
            bool applyProfileScale)
        {
            double profileScale = applyProfileScale
                ? SizeEstimateService.GetCompressionMultiplier(compressionProfile)
                : 1;
            int calibratedQuality = (int)Math.Round(
                current.QualityValue - 12 * Math.Log(profileScale, 2));
            return new SampleComparisonSettings
            {
                Encoder = current.Encoder,
                VideoCodec = current.VideoCodec,
                UseGpu = current.UseGpu,
                // Quality-based samples are independent evidence. Reusing the target
                // size would make the projection agree with the estimate by design.
                ProjectedTargetMb = null,
                ScaleMode = current.ScaleMode,
                EncoderPreset = current.EncoderPreset,
                // The metadata estimator expresses the selected file-size profile
                // as a bitrate scale. Apply the mathematically equivalent quality
                // offset so the quality-mode samples preserve that user control.
                QualityValue = calibratedQuality,
                TenBit = current.TenBit,
                AudioChannels = current.AudioChannels,
                AdditionalMappedBitrateKbps = current.AdditionalMappedBitrateKbps,
                ClipSeconds = 8
            };
        }

        private void ApplyCalibratedEstimate(
            DataGridViewRow row,
            RowMeta meta,
            DeepMediaAnalysisResult result,
            double metadataEstimateMb)
        {
            double calibratedMb = result.ProjectedOutputMb!.Value;
            if (_estimatedSizeMap.TryGetValue(meta.Path, out double previousEstimate))
                _queueTotalEstimatedMb += calibratedMb - previousEstimate;
            else
                _queueTotalEstimatedMb += calibratedMb;

            _estimatedSizeMap[meta.Path] = calibratedMb;
            _queueTotalsDirty = false;
            double sourceMb = meta.SrcMb > 0
                ? meta.SrcMb
                : new FileInfo(meta.Path).Length / (1024d * 1024d);
            row.Cells["colEstimatedSize"].Value =
                $"{FormatSize(calibratedMb)}  {PercentReduction(sourceMb, calibratedMb)} (calibrated)";
            row.Cells["colEstimatedSize"].Tag =
                new Tuple<double, double>(sourceMb, calibratedMb);

            string range = result.ProjectedOutputLowerMb is > 0 &&
                           result.ProjectedOutputUpperMb is > 0
                ? $" Expected range: {FormatSize(result.ProjectedOutputLowerMb.Value)}–" +
                  $"{FormatSize(result.ProjectedOutputUpperMb.Value)}."
                : string.Empty;
            string comparison = metadataEstimateMb > 0
                ? $" Fast metadata estimate: {FormatSize(metadataEstimateMb)}."
                : string.Empty;
            row.Cells["colEstimatedSize"].ToolTipText =
                $"Calibrated from {result.ProjectionSampleCount} representative quality-mode " +
                $"sample(s) using the selected encoder settings ({result.ProjectionConfidence} confidence)." +
                range + comparison;
            meta.EstimateDiagnostic =
                $"Calibrated sample estimate: output={calibratedMb:0.##} MB, " +
                $"range={result.ProjectedOutputLowerMb:0.##}–{result.ProjectedOutputUpperMb:0.##} MB, " +
                $"confidence={result.ProjectionConfidence}, samples={result.ProjectionSampleCount}, " +
                $"sampled={result.SampledMediaSeconds:0.##} sec, metadata={metadataEstimateMb:0.##} MB.";
            UpdateSizeTotals();
        }

        private void ApplyContentHintToSelectedRows(SmartEncodeContentHint hint)
        {
            if (dgvEncodeQueue.SelectedRows.Count == 0)
            {
                ShowStatusInfo("Select one or more files before setting a content hint.");
                return;
            }

            int changed = 0;
            foreach (DataGridViewRow row in dgvEncodeQueue.SelectedRows)
            {
                if (row.IsNewRow || row.DataGridView == null || _activeEncodeRow == row)
                    continue;

                RowMeta meta = EnsureRowMeta(row);
                meta.ContentHint = hint;
                UpdateRowCustomFlag(row);
                if (meta.BaselineEncodeRecommendation != null)
                {
                    double intendedMb = _estimatedSizeMap.TryGetValue(
                        meta.Path,
                        out double estimate)
                            ? estimate
                            : 0;
                    SmartEncodeRecommendation refined =
                        new SmartEncodeDecisionService().RefineWithDeepAnalysis(
                            meta.BaselineEncodeRecommendation,
                            meta.DeepAnalysis ??
                            new DeepMediaAnalysisResult
                            {
                                InterlaceStatus = SampledInterlaceStatus.Unavailable
                            },
                            hint,
                            intendedMb);
                    ApplySmartRecommendation(row, refined, updateBaseline: false);
                }

                changed++;
            }

            ShowStatusInfo(
                changed > 0
                    ? $"Applied the {GetContentHintDisplayName(hint)} content hint to {changed:N0} file(s)."
                    : "Content hints cannot be changed on the active encode row.");
        }

        private void ContentHintMenuItem_Click(object? sender, EventArgs e)
        {
            if (sender is ToolStripMenuItem { Tag: SmartEncodeContentHint hint })
                ApplyContentHintToSelectedRows(hint);
        }

        private static string GetContentHintDisplayName(SmartEncodeContentHint hint)
        {
            return hint switch
            {
                SmartEncodeContentHint.LiveAction => "Live action",
                SmartEncodeContentHint.Animation => "Animation",
                SmartEncodeContentHint.ScreenContent => "Screen content",
                _ => "Auto"
            };
        }
    }
}
