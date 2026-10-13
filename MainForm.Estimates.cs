using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.EncodingV2;
using MediaFlux.Services.Encoders;

namespace MediaFlux
{
    public partial class MainForm : MediaFluxForm
    {
        private DateTime? _qualityChangeEstimateRefreshUtc;
        private string BuildQueueEstimateTooltip(
            RowMeta? meta,
            double sourceMb,
            double? estimatedOutputMb,
            EncodingQualityResolution? quality = null)
        {
            quality ??= meta?.IntelligencePlan?.Quality ?? meta?.QualityPreview;
            if (meta?.IntelligencePlan?.AutomaticTarget is { } targetPlan && targetPlan.IsAvailable)
            {
                string projected = estimatedOutputMb is > 0 ? FormatSize(estimatedOutputMb.Value) : "unavailable";
                string savings = sourceMb > 0 && estimatedOutputMb is > 0
                    ? (sourceMb - estimatedOutputMb.Value) / sourceMb * 100d >= 0
                        ? $"Approximate savings: {(sourceMb - estimatedOutputMb.Value) / sourceMb * 100d:0.#}%"
                        : $"Approximate increase: {Math.Abs((sourceMb - estimatedOutputMb.Value) / sourceMb * 100d):0.#}%"
                    : "Savings: unavailable";
                string identity = meta.IntelligencePlan.ProductionPlan?.PlanFingerprint ?? "unavailable";
                return string.Join(Environment.NewLine,
                    $"Compression Target: {targetPlan.TargetVideoBitrateKbps:0} kbps VBR · max {targetPlan.MaximumVideoBitrateKbps:0} kbps · buffer {targetPlan.VideoBufferSizeKbps:0} kbps",
                    $"Projected Output: {projected} (target-based projection; includes planned audio, mapped streams, and container overhead; actual size may vary)",
                    "Actual Output: measured after encoding and finalization.",
                    savings,
                    $"Quality segment: {EncodingQualityPresentation.QualityTargetDisplayName(targetPlan.QualityTarget)}",
                    $"Plan: {identity[..Math.Min(12, identity.Length)]}",
                    targetPlan.Reason);
            }
            string? qualitySummary = null;
            if (meta?.IntelligencePlan?.AdaptiveSelection is { Disposition: AdaptiveSelectionDisposition.Selected } adaptiveSelection)
            {
                qualitySummary = EncodingQualityPresentation.QualityTargetDisplayName(adaptiveSelection.Target);
            }
            else if (quality is { IsSupersededByTargetSize: false })
            {
                string? target = quality.Intent.Target is { } qualityTarget
                    ? EncodingQualityPresentation.QualityTargetDisplayName(qualityTarget)
                    : null;
                string? effective = quality.EffectiveQuality is { } value
                    ? $"{EncodingQualityPresentation.Mechanism(quality.Mechanism)} {value}"
                    : null;
                qualitySummary = target is null
                    ? effective
                    : target;
            }

            string? encoderSummary = null;
            try
            {
                EncodingPlanVideo? plannedVideo = meta?.IntelligencePlan?.Video;
                VideoEncoderSelection selection = plannedVideo is null
                    ? GetSelectedVideoEncoderSelection()
                    : new VideoEncoderSelection(
                        VideoEncoderCompatibility.ResolveEncoderId(
                            plannedVideo.Encoder,
                            VideoEncoderCompatibility.ParseCodecFamily(plannedVideo.Codec)),
                        VideoEncoderCompatibility.ParseCodecFamily(plannedVideo.Codec),
                        plannedVideo.Codec);
                string encoderName = selection.EncoderId switch
                {
                    VideoEncoderIds.Nvenc => "NVENC",
                    VideoEncoderIds.Qsv => "Intel QSV",
                    VideoEncoderIds.Libx264 or VideoEncoderIds.Libx265 or VideoEncoderIds.SvtAv1 or
                        VideoEncoderIds.LegacySoftware => "Software",
                    _ => "Encoder"
                };
                string codecName = selection.CodecFamily switch
                {
                    VideoCodecFamily.H264 => "H.264",
                    VideoCodecFamily.Hevc => "HEVC",
                    VideoCodecFamily.Av1 => "AV1",
                    _ => string.Empty
                };
                encoderSummary = string.IsNullOrEmpty(codecName)
                    ? encoderName
                    : $"{encoderName} {codecName}";
            }
            catch
            {
                // Tooltip details are optional and must not affect estimation.
            }

            string tooltip = QueueAnalysisPresentation.BuildEstimatedOutputTooltip(
                sourceMb > 0 ? sourceMb : null,
                estimatedOutputMb,
                qualitySummary,
                encoderSummary);
            if (meta?.BoundEstimate is { } bound)
            {
                string authority = bound.Authority == EstimateAuthorityLevel.Measured
                    ? $"Measured from {bound.SampleCount?.ToString() ?? "bounded"} samples"
                    : "Preliminary metadata estimate";
                tooltip += Environment.NewLine + authority + $" · {bound.Method} · plan {bound.Plan.PlanFingerprint[..Math.Min(12, bound.Plan.PlanFingerprint.Length)]}";
            }
            if (meta?.PreparedAdaptiveProductionPlan is { BoundEstimate: { } productionEstimate } prepared &&
                productionEstimate.Matches(prepared.Identity))
            {
                string range = productionEstimate.LowerBytes is long lower && productionEstimate.UpperBytes is long upper
                    ? $"{FormatSize(lower / (1024d * 1024d))}–{FormatSize(upper / (1024d * 1024d))}"
                    : "range unavailable";
                string confidence = string.IsNullOrWhiteSpace(productionEstimate.Confidence)
                    ? "unspecified confidence"
                    : $"{productionEstimate.Confidence} confidence";
                int selectedQuality = prepared.Selection.SelectedQuality ?? prepared.Selection.PreferredQuality;
                tooltip += Environment.NewLine +
                    $"Production-ready estimate · CQ/CRF {selectedQuality} · range {range} · {confidence} · fingerprint {prepared.Identity.PlanFingerprint[..Math.Min(12, prepared.Identity.PlanFingerprint.Length)]}.";
            }
            if (meta?.EstimateState == QueueEstimateState.Measuring)
                tooltip += Environment.NewLine + "A bounded measured refinement is running in the background.";
            else if (meta?.EstimateState == QueueEstimateState.Preliminary)
                tooltip += Environment.NewLine + "Preliminary estimate · advisory; actual validated output controls acceptance.";
            return tooltip;
        }

        private (int w, int h) ProbeResolutionPixels(string file)
        {
            return _mediaInfoService.GetResolutionPixels(file);
        }

        private double ProbeFps(string file)
        {
            return _mediaInfoService.GetFps(file);
        }

        // Background Results UI

        private void StartEstimateUiPump()
        {
            if (_estSmartUiTimer == null)
            {
                _estSmartUiTimer = new System.Windows.Forms.Timer { Interval = 100 };
                _estSmartUiTimer.Tick += EstSmartUiTimer_Tick;
                _estSmartUiTimer.Start();
            }

        }

        private void StartV2EstimateUiPump()
        {
            if (IsDisposed || !IsHandleCreated)
                return;
            if (_v2EstimateUiTimer == null)
            {
                _v2EstimateUiTimer = new System.Windows.Forms.Timer { Interval = 16 };
                _v2EstimateUiTimer.Tick += V2EstimateUiTimer_Tick;
            }
            if (!_v2EstimateUiTimer.Enabled)
                _v2EstimateUiTimer.Start();
        }

        private void EstSmartUiTimer_Tick(object? sender, EventArgs e)
        {
            ApplySmartEstimateResultsBatch();
        }

        private void V2EstimateUiTimer_Tick(object? sender, EventArgs e)
        {
            ApplyV2EstimateResultsBatch();
            if (_v2EstimateCoordinator?.PendingWorkCount == 0)
                _v2EstimateUiTimer?.Stop();
        }

        private void StopEstimateUiPump()
        {
            // Cancel workers and clear queues
            _estimateService.ResetAndCancel();
            _v2EstimateCoordinator?.Dispose();
            _v2EstimateCoordinator = null;

            if (_estimateRefreshTimer != null)
            {
                _estimateRefreshTimer.Stop();
                _estimateRefreshTimer.Tick -= EstimateRefreshTimer_Tick;
                _estimateRefreshTimer.Dispose();
                _estimateRefreshTimer = null;
            }

            if (_estSmartUiTimer != null)
            {
                _estSmartUiTimer.Stop();
                _estSmartUiTimer.Tick -= EstSmartUiTimer_Tick;
                _estSmartUiTimer.Dispose();
                _estSmartUiTimer = null;
            }

            if (_v2EstimateUiTimer != null)
            {
                _v2EstimateUiTimer.Stop();
                _v2EstimateUiTimer.Tick -= V2EstimateUiTimer_Tick;
                _v2EstimateUiTimer.Dispose();
                _v2EstimateUiTimer = null;
            }
        }

        private void ApplySmartEstimateResultsBatch()
        {
            if (IsDisposed || !IsHandleCreated) return;

            const int MaxPerTick = 64;
            int applied = 0;

            dgvEncodeQueue.SuspendLayout();
            try
            {
                while (applied < MaxPerTick && _estimateService.TryDequeueSmart(out var item))
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Dequeued {item.Path}: srcMb={item.SourceMb}, estMb={item.EstimatedMb}, dur={item.DurationSec}, res={item.Resolution}");
                    PreparedProductionPlan? preparedForRow = item.PreparedProductionPlan is { } queuedPlan
                        ? queuedPlan with { QueueItemId = item.QueueItemId, QueueGeneration = item.Generation }
                        : null;

                    if (_rowsByPath.TryGetValue(item.Path, out var row) &&
                        row?.Cells != null &&
                        row.Tag is RowMeta resultMeta &&
                        resultMeta.QueueItemId == item.QueueItemId &&
                        resultMeta.EstimateAuthorityRevision == item.EstimateAuthorityRevision &&
                        !IsQueueRowActivelyEncoding(row) &&
                        PathsIdentifySameSource(resultMeta.Path, item.Path))
                    {
                        try
                        {
                            if (item.RecommendationOnly)
                            {
                                if (!TryBindEstimateResult(resultMeta, item))
                                {
                                    applied++;
                                    continue;
                                }
                                resultMeta.CurrentEstimatePlanIdentity = item.PlanIdentity;
                                resultMeta.BoundEstimate = item.BoundEstimate;
                                resultMeta.PreparedAdaptiveProductionPlan = item.PreparedAdaptiveProductionPlan ?? resultMeta.PreparedAdaptiveProductionPlan;
                                if (resultMeta.AdaptiveQualitySelectionPending &&
                                    item.Recommendation is { Kind: not SmartEncodeRecommendationKind.Unavailable })
                                    SetSmartRecommendationPending(row);
                                else
                                    ApplySmartRecommendation(row, item.Recommendation);
                                applied++;
                                continue;
                            }
                            double srcMb = item.SourceMb;
                            if (preparedForRow is { } preparedPlan &&
                                string.Equals(preparedPlan.Identity.SourceIdentity,
                                    ProductionPlanFingerprintService.CaptureSourceIdentity([item.Path]), StringComparison.Ordinal))
                            {
                                resultMeta.CurrentEstimatePlanIdentity = preparedPlan.Identity;
                                resultMeta.PlanBindingRequired = true;
                            }
                            bool metadataOnlyProgress = (item.EstimateState is QueueEstimateState.Pending or QueueEstimateState.Measuring) &&
                                item.PlanIdentity is null && item.BoundEstimate is null &&
                                preparedForRow is null && item.EstimatedMb <= 0;
                            bool planBound = metadataOnlyProgress || TryBindEstimateResult(resultMeta, item);
                            bool resultMatchesPreparedPlan = planBound && item.PlanIdentity is not null;
                            if (planBound && item.PlanIdentity is not null)
                            {
                                resultMeta.CurrentEstimatePlanIdentity = item.PlanIdentity;
                                resultMeta.BoundEstimate = item.BoundEstimate;
                                resultMeta.PreparedAdaptiveProductionPlan = item.PreparedAdaptiveProductionPlan ?? resultMeta.PreparedAdaptiveProductionPlan;
                                if (preparedForRow is { } prepared)
                                {
                                    resultMeta.PreparedProductionPlan = prepared;
                                    resultMeta.IntelligencePlan = prepared.Plan;
                                    resultMeta.PreparedAdaptiveProductionPlan = null;
                                }
                            }
                            else if (resultMeta.PlanBindingRequired)
                            {
                                ClearBoundQueueEstimate(row, resultMeta, "Unavailable", "The estimate could not be bound to the current production plan and source.");
                            }
                            resultMeta.EstimateState = planBound
                                ? item.EstimateState
                                : resultMeta.PlanBindingRequired
                                    ? QueueEstimateState.Unavailable
                                    : item.PlanIdentity is null
                                        ? item.EstimatedMb > 0
                                            ? QueueEstimateState.Pending
                                            : item.EstimateState
                                        : QueueEstimateState.Unavailable;
                            double estMb = planBound ? item.EstimatedMb : 0;
                            bool hasEstimate = srcMb > 0 && estMb > 0;
                            row.Cells["colEstimatedSize"].Value = hasEstimate
                                ? FormatSize(estMb)
                                : resultMeta.EstimateState switch
                                {
                                    QueueEstimateState.Pending => "Pending…",
                                    QueueEstimateState.Inspecting => "Inspecting…",
                                    QueueEstimateState.Measuring => "Measuring…",
                                    QueueEstimateState.Preliminary => "Pending…",
                                    QueueEstimateState.Ready => "Ready",
                                    QueueEstimateState.Review => "Review",
                                    QueueEstimateState.Skip => "Skip",
                                    QueueEstimateState.Failed => "Error",
                                    _ => "Unavailable"
                                };
                            if (srcMb > 0)
                                row.Cells["colSize"].Value = FormatSize(srcMb);
                            row.Cells["colEstimatedSize"].Tag = hasEstimate
                                ? new Tuple<double, double>(srcMb, estMb)
                                : null;
                            row.Cells["colEstimatedSize"].ToolTipText = hasEstimate
                                ? BuildQueueEstimateTooltip(resultMeta, srcMb, estMb, item.QualityResolution)
                                : "Estimated output is not available yet.";
                            if (_queueSourceSizeMap.TryGetValue(item.Path, out var previousSrc))
                                _queueTotalSourceMb += srcMb - previousSrc;
                            else
                                _queueTotalSourceMb += srcMb;

                            _queueSourceSizeMap[item.Path] = srcMb;

                            if (hasEstimate)
                            {
                                if (_estimatedSizeMap.TryGetValue(item.Path, out var previousEst))
                                    _queueTotalEstimatedMb += estMb - previousEst;
                                else
                                    _queueTotalEstimatedMb += estMb;

                                _estimatedSizeMap[item.Path] = estMb;
                            }
                            else if (_estimatedSizeMap.Remove(item.Path, out var previousEst))
                            {
                                _queueTotalEstimatedMb -= previousEst;
                            }
                            _queueTotalsDirty = false;

                            // Update RowMeta on the UI thread so later consumers can avoid probing
                            string path = item.Path;
                            double durSec = item.DurationSec;
                            string res = item.Resolution ?? "";
                            string codec = item.VideoCodec ?? "";

                            if (row.Tag is RowMeta rm)
                            {
                                if (string.IsNullOrWhiteSpace(rm.Path))
                                    rm.Path = path;

                                if (durSec > 0)
                                    rm.DurationSec = durSec;

                                if (!string.IsNullOrEmpty(res))
                                    rm.Resolution = res;

                                if (srcMb > 0)
                                    rm.SrcMb = srcMb;

                                if (!string.IsNullOrWhiteSpace(codec))
                                    rm.VideoCodec = codec;

                                if (item.Fps > 0)
                                    rm.Fps = (int)Math.Round(item.Fps);

                                rm.EmpiricalPreview = resultMatchesPreparedPlan ? item.EmpiricalPreview : null;
                                if (item.PreparedAdaptiveProductionPlan is not null)
                                    rm.PreparedAdaptiveProductionPlan = item.PreparedAdaptiveProductionPlan;
                                if (preparedForRow is { } prepared)
                                {
                                    rm.PreparedProductionPlan = prepared;
                                    rm.IntelligencePlan = prepared.Plan;
                                    rm.PreparedAdaptiveProductionPlan = null;
                                }
                                rm.EstimateState = resultMeta.EstimateState;
                                rm.EstimateDiagnostic = item.EstimateDiagnostic;
                                rm.SizePredictionCalibration = resultMatchesPreparedPlan ? item.SizeCalibration : null;
                                rm.AdaptiveQualitySelectionPending = false;
                                rm.AdaptiveSelectionSkipped = false;
                                rm.EstimatedPlannedAudioBitrateKbps =
                                    item.PlannedAudioBitrateKbps;
                                rm.EstimatedPlannedMappedAncillaryBitrateKbps =
                                    item.PlannedMappedAncillaryBitrateKbps;
                                rm.QualityPreview = resultMatchesPreparedPlan &&
                                    item.QualityResolution is { EffectiveQuality: not null, IsSupersededByTargetSize: false }
                                        ? item.QualityResolution
                                        : null;
                            }
                            else
                            {
                                RowMeta estimateMeta = EnsureRowMeta(row);
                                estimateMeta.DurationSec = durSec;
                                estimateMeta.Resolution = res;
                                estimateMeta.SrcMb = srcMb;
                                estimateMeta.VideoCodec = codec;
                                estimateMeta.Fps = item.Fps > 0 ? (int)Math.Round(item.Fps) : 0;
                                estimateMeta.EmpiricalPreview = resultMatchesPreparedPlan ? item.EmpiricalPreview : null;
                                if (item.PreparedAdaptiveProductionPlan is not null)
                                    estimateMeta.PreparedAdaptiveProductionPlan = item.PreparedAdaptiveProductionPlan;
                                if (preparedForRow is { } prepared)
                                {
                                    estimateMeta.PreparedProductionPlan = prepared;
                                    estimateMeta.IntelligencePlan = prepared.Plan;
                                    estimateMeta.PreparedAdaptiveProductionPlan = null;
                                }
                                estimateMeta.EstimateState = resultMeta.EstimateState;
                                estimateMeta.EstimateDiagnostic = item.EstimateDiagnostic;
                                estimateMeta.SizePredictionCalibration = resultMatchesPreparedPlan ? item.SizeCalibration : null;
                                estimateMeta.AdaptiveQualitySelectionPending = false;
                                estimateMeta.AdaptiveSelectionSkipped = false;
                                estimateMeta.EstimatedPlannedAudioBitrateKbps =
                                    item.PlannedAudioBitrateKbps;
                                estimateMeta.EstimatedPlannedMappedAncillaryBitrateKbps =
                                    item.PlannedMappedAncillaryBitrateKbps;
                                estimateMeta.QualityPreview = resultMatchesPreparedPlan &&
                                    item.QualityResolution is { EffectiveQuality: not null, IsSupersededByTargetSize: false }
                                        ? item.QualityResolution
                                        : null;
                            }

                            TrackCodecFilterCount(path, codec);

                            if (planBound && item.Recommendation is not null)
                                ApplySmartRecommendation(row, item.Recommendation);
                            UpdateRowCustomFlag(row);
                            RestoreQueuedStateAfterEstimate(row);
                            CriticalPathTiming.WriteLine(
                                $"[CriticalPathTiming] stage=estimate-visible item={item.QueueItemId:N} " +
                                $"source={Path.GetFileName(item.Path)} sinceAcceptedMs={(DateTime.UtcNow - item.QueueAcceptedUtc).TotalMilliseconds:0}" +
                                (item.QualityChangeStartedUtc is { } changedUtc
                                    ? $" sinceQualityChangeMs={(DateTime.UtcNow - changedUtc).TotalMilliseconds:0}"
                                    : string.Empty));
                            applied++;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"Smart Estimate UI Update Error for {item.Path}: {ex.Message}\n{ex.StackTrace}");
                            row.Cells["colEstimatedSize"].Value = "Error";
                            SetRecommendationUnavailable(
                                row,
                                "Queue analysis failed for this file.");
                        }
                    }
                }
            }
            finally
            {
                dgvEncodeQueue.ResumeLayout();
                if (applied > 0)
                    dgvEncodeQueue.Invalidate();
                UpdateSizeTotals();
                UpdateEstimateProgressStatus();
            }
        }

        private void UpdateEstimateProgressStatus()
        {
            int pending = _estimateService.PendingEstimates;
            if (_lastEstimateQueuedCount > 0 && pending > 0)
            {
                int completed = Math.Max(0, _lastEstimateQueuedCount - pending);
                string text = $"Analyzing queue metadata... {completed:N0}/{_lastEstimateQueuedCount:N0}";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                SetQueueProgress(completed, _lastEstimateQueuedCount, visible: true);
                SetQueueWorkCancelVisible(true);
            }
            else if (_lastEstimateQueuedCount > 0 && pending <= 0)
            {
                string text = _estimateService.PendingRefinements > 0
                    ? $"Preliminary estimates ready for {_lastEstimateQueuedCount:N0} file(s); measured refinements continue in the background."
                    : _estimatesDeferredForLargeQueue
                    ? $"Large queue mode: {dgvEncodeQueue.Rows.Count:N0} files loaded. Use Analyze Queue to estimate sizes."
                    : $"Queue analysis complete for {_lastEstimateQueuedCount:N0} file(s).";
                toolStripStatusLabel1.Text = text;
                UpdateRelocatedEncodeStatus(text);
                _lastEstimateQueuedCount = 0;
                SetQueueProgress(0, 0, visible: false);
            }
            SetQueueWorkCancelVisible(pending > 0 || _estimateService.PendingRefinements > 0);
        }

        private void SafeRefreshEstimates()
        {
            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            RunEstimatePass();
        }

        private void ScheduleEstimateRefresh(bool qualityChanged = false)
        {
            if (IsDisposed || dgvEncodeQueue == null || dgvEncodeQueue.Rows.Count == 0)
                return;

            if (qualityChanged)
            {
                _qualityChangeEstimateRefreshUtc = DateTime.UtcNow;
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=quality-change-initiated utc={_qualityChangeEstimateRefreshUtc:O} queueCount={dgvEncodeQueue.Rows.Count}");
            }

            _estimateRefreshTimer ??= new System.Windows.Forms.Timer();
            _estimateRefreshTimer.Interval = qualityChanged ? 25 : 75;
            _estimateRefreshTimer.Stop();
            _estimateRefreshTimer.Tick -= EstimateRefreshTimer_Tick;
            _estimateRefreshTimer.Tick += EstimateRefreshTimer_Tick;
            _estimateRefreshTimer.Start();
        }

        private void EstimateRefreshTimer_Tick(object? sender, EventArgs e)
        {
            _estimateRefreshTimer?.Stop();
            SafeRefreshEstimates();
        }

        private Dictionary<Guid, QueueEstimateRefinementPriority> GetQueueRefinementPriorities()
        {
            DataGridViewRow[] queuedRows = dgvEncodeQueue.Rows.Cast<DataGridViewRow>()
                .Where(row => !row.IsNewRow && row.Tag is RowMeta meta &&
                    !meta.ExcludedFromEncodeAsDuplicate && !IsQueueRowActivelyEncoding(row))
                .OrderBy(row => ((RowMeta)row.Tag!).QueueSequence)
                .ToArray();
            HashSet<Guid> nearTerm = queuedRows.Take(3)
                .Select(row => ((RowMeta)row.Tag!).QueueItemId)
                .ToHashSet();
            var priorities = new Dictionary<Guid, QueueEstimateRefinementPriority>(queuedRows.Length);
            foreach (DataGridViewRow row in queuedRows)
            {
                RowMeta meta = (RowMeta)row.Tag!;
                bool selected = row.Selected || dgvEncodeQueue.CurrentRow == row;
                bool visible = false;
                try { visible = row.Displayed; }
                catch (InvalidOperationException) { }
                priorities[meta.QueueItemId] = selected
                    ? QueueEstimateRefinementPriority.Selected
                    : visible ? QueueEstimateRefinementPriority.Visible
                    : nearTerm.Contains(meta.QueueItemId) ? QueueEstimateRefinementPriority.NearTerm
                    : QueueEstimateRefinementPriority.Background;
            }
            return priorities;
        }

        private void UpdateEstimateRefinementPriorities()
        {
            if (IsDisposed || dgvEncodeQueue is null || _estimateService is null)
                return;
            foreach ((Guid itemId, QueueEstimateRefinementPriority priority) in GetQueueRefinementPriorities())
                _estimateService.SetRefinementPriority(itemId, priority);
        }

        private bool TryQueueV2Estimate(
            DataGridViewRow row,
            RowMeta meta,
            string path,
            DateTime? qualityChangeStartedUtc,
            string estimateProfile)
        {
            V2EstimateRefreshCoordinator? coordinator = _v2EstimateCoordinator;
            if (coordinator == null || row.IsNewRow || IsQueueRowActivelyEncoding(row))
                return false;

            double? manualTargetMb = EncodingTargetSizeResolver.ResolveConfiguredManualTargetMb(
                chkAutoTargetSize.Checked, txtTargetSize.Text);
            bool custom = RowHasCustomSettings(meta);
            VideoRestorationPipelinePlan restoration = VideoRestorationPipeline.BuildPlan(
                _config.VideoRestoration, GetSelectedScaleMode());
            bool restorationEnabled = restoration.UsesAi ||
                !string.IsNullOrWhiteSpace(restoration.ConventionalFilterChain) ||
                !string.IsNullOrWhiteSpace(restoration.PreAiFilterChain) ||
                !string.IsNullOrWhiteSpace(restoration.PostAiFilterChain);

            try
            {
                ValidatedEncoderSettings settings = GetValidatedEncoderSettingsFromUi(
                    includeConcurrentSessions: false);
                if (!V2QueueEstimateRequestMapper.TryCreate(
                    path,
                    settings.Resolved.Selection,
                    settings.QualityValue,
                    estimateProfile,
                    settings.Preset,
                    GetSelectedOutputContainer(),
                    GetEstimateTargetHeight(),
                    GetSelectedAudioChannels(),
                    settings.TenBit,
                    adaptiveQuality: IsAutomaticQualitySelected(),
                    targetSize: manualTargetMb is > 0 || meta.CustomTargetMb is > 0,
                    customSettings: custom,
                    restorationEnabled: restorationEnabled,
                    out V2FastEstimateCommandRequest? request,
                    out _))
                {
                    coordinator.Forget(meta.QueueItemId);
                    return false;
                }

                V2SourceFileIdentity identity = V2SourceFileIdentity.Capture(path);
                bool cachedFacts = coordinator.TryGetCachedSourceFacts(identity, out _);
                meta.V2SourceIdentity = identity;
                meta.V2EstimateResult = null;
                meta.CurrentEstimatePlanIdentity = null;
                meta.BoundEstimate = null;
                meta.PreparedAdaptiveProductionPlan = null;
                meta.EstimateState = QueueEstimateState.Pending;
                meta.PlanBindingRequired = true;
                meta.V2QualityChangeStartedUtc = qualityChangeStartedUtc;
                V2EstimateRefreshTicket ticket = coordinator.Request(
                    meta.QueueItemId, identity, request!);
                meta.V2EstimateGeneration = ticket.Generation;
                CriticalPathTiming.WriteLine(
                    $"[CriticalPathTiming] stage=v2-estimate-requested item={meta.QueueItemId:N} " +
                    $"generation={ticket.Generation} profile={estimateProfile} quality={request!.QualityValue:0.###} " +
                    $"sourceFacts={(cachedFacts ? "cached" : "pending-probe")}" +
                    (qualityChangeStartedUtc is { } changedUtc
                        ? $" sinceEstimateInputChangeMs={(DateTime.UtcNow - changedUtc).TotalMilliseconds:0.##}"
                        : string.Empty));
                row.Cells["colEstimatedSize"].Value = cachedFacts ? "Calculating…" : "Probing…";
                row.Cells["colEstimatedSize"].Tag = null;
                row.Cells["colEstimatedSize"].ToolTipText = cachedFacts
                    ? "V2 fast estimate is recalculating from cached source facts. The result is advisory."
                    : "V2 fast estimate is reading source facts once. The result is advisory.";
                StartV2EstimateUiPump();
                return true;
            }
            catch (Exception ex)
            {
                coordinator.Forget(meta.QueueItemId);
                System.Diagnostics.Debug.WriteLine($"V2 estimate request was not scheduled for {Path.GetFileName(path)}: {ex.Message}");
                return false;
            }
        }

        private void ApplyV2EstimateResultsBatch()
        {
            V2EstimateRefreshCoordinator? coordinator = _v2EstimateCoordinator;
            if (coordinator == null || IsDisposed || !IsHandleCreated)
                return;

            int applied = 0;
            dgvEncodeQueue.SuspendLayout();
            try
            {
                while (applied < 128 && coordinator.TryDequeueCompleted(out V2EstimateRefreshResult result))
                {
                    if (result.IsStale ||
                        !_rowsByPath.TryGetValue(result.SourceIdentity.FullPath, out DataGridViewRow? row) ||
                        row == null || row.DataGridView != dgvEncodeQueue ||
                        row.Tag is not RowMeta meta || meta.QueueItemId != result.QueueItemId ||
                        meta.ExcludedFromEncodeAsDuplicate ||
                        meta.V2EstimateGeneration != result.Generation ||
                        !PathsIdentifySameSource(meta.Path, result.SourceIdentity.FullPath) ||
                        IsQueueRowActivelyEncoding(row))
                        continue;

                    if (result.Estimate is not { } estimate || result.SourceFacts is not { } facts)
                    {
                        meta.V2EstimateResult = null;
                        meta.EstimateState = QueueEstimateState.Unavailable;
                        row.Cells["colEstimatedSize"].Value = "Unavailable";
                        row.Cells["colEstimatedSize"].Tag = null;
                        row.Cells["colEstimatedSize"].ToolTipText =
                            $"V2 estimate unavailable: {result.ErrorMessage ?? "source facts could not be read"}";
                        if (!meta.QueueAdmissionPending)
                            RestoreQueuedStateAfterEstimate(row);
                        applied++;
                        continue;
                    }

                    ProductionPlanIdentity? planIdentity = estimate.ProductionPlan;
                    bool planBound = planIdentity is not null &&
                        string.Equals(planIdentity.SourceIdentity,
                            ProductionPlanFingerprintService.CaptureSourceIdentity([meta.Path]), StringComparison.Ordinal);
                    if (!planBound)
                    {
                        meta.CurrentEstimatePlanIdentity = null;
                        meta.BoundEstimate = null;
                        meta.EstimateState = QueueEstimateState.Unavailable;
                        row.Cells["colEstimatedSize"].Value = "Unavailable";
                        row.Cells["colEstimatedSize"].Tag = null;
                        row.Cells["colEstimatedSize"].ToolTipText = "The V2 estimate could not be bound to the current production plan and source.";
                        _estimatedSizeMap.Remove(meta.Path);
                        MarkQueueTotalsDirty();
                        applied++;
                        continue;
                    }

                    long uiApplyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    double sourceMb = facts.SourceSizeBytes / (1024d * 1024d);
                    double estimateMb = estimate.EstimatedTotalBytes / (1024d * 1024d);
                    meta.CurrentEstimatePlanIdentity = planIdentity;
                    meta.EstimateState = QueueEstimateState.Preliminary;
                    meta.BoundEstimate = new PlanBoundEstimate(planIdentity!, result.Generation,
                        "V2FastEstimate", estimate.EstimatedTotalBytes, EstimateAuthorityLevel.Preliminary,
                        Confidence: estimate.Confidence.ToString());
                    meta.V2SourceIdentity = result.SourceIdentity;
                    meta.V2SourceFacts = facts;
                    meta.V2EstimateResult = estimate;
                    meta.SourceSizeBytes = facts.SourceSizeBytes;
                    meta.SrcMb = sourceMb;
                    meta.DurationSec = facts.DurationSeconds;
                    meta.VideoCodec = facts.VideoCodec;
                    meta.Resolution = $"{facts.Width}x{facts.Height}";
                    meta.Fps = facts.FrameRate is > 0 ? (int)Math.Round(facts.FrameRate.Value) : 0;

                    QueueV2MeasuredRefinement(meta, estimate, facts);

                    row.Cells["colSize"].Value = FormatSize(sourceMb);
                    row.Cells["colEstimatedSize"].Value = FormatSize(estimateMb);
                    row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(sourceMb, estimateMb);
                    row.Cells["colEstimatedSize"].ToolTipText = BuildV2EstimateTooltip(
                        estimate, facts, result.SourceFactsReused, result.ProbeMilliseconds);

                    _queueSourceSizeMap[result.SourceIdentity.FullPath] = sourceMb;
                    _estimatedSizeMap[result.SourceIdentity.FullPath] = estimateMb;
                    TrackCodecFilterCount(result.SourceIdentity.FullPath, facts.VideoCodec);
                    MarkQueueTotalsDirty();
                    if (!meta.QueueAdmissionPending)
                        RestoreQueuedStateAfterEstimate(row);

                    DateTime now = DateTime.UtcNow;
                    CriticalPathTiming.WriteLine(
                        $"[CriticalPathTiming] stage=v2-estimate-visible item={result.QueueItemId:N} " +
                        $"source={Path.GetFileName(meta.Path)} sourceFacts={(result.SourceFactsReused ? "reused" : "probed")}" +
                        $" probeMs={result.ProbeMilliseconds:0.##} calculateMs={estimate.CalculationMilliseconds:0.###}" +
                        $" queueWaitMs={result.QueueWaitMilliseconds:0.##} totalMs={result.TotalMilliseconds:0.##}" +
                        $" readyToUiMs={(now - result.CompletedUtc).TotalMilliseconds:0.##}" +
                        $" uiApplyMs={System.Diagnostics.Stopwatch.GetElapsedTime(uiApplyStarted).TotalMilliseconds:0.###}" +
                        (meta.V2QualityChangeStartedUtc is { } changedUtc
                            ? $" sinceQualityChangeMs={(now - changedUtc).TotalMilliseconds:0.##}"
                            : $" sinceRowVisibleMs={(now - meta.QueueRowCreatedUtc).TotalMilliseconds:0.##}"));
                    meta.V2QualityChangeStartedUtc = null;
                    applied++;
                }
            }
            finally
            {
                dgvEncodeQueue.ResumeLayout();
                if (applied > 0)
                {
                    dgvEncodeQueue.Invalidate();
                    UpdateSizeTotals(force: true);
                }
            }
        }

        private void QueueV2MeasuredRefinement(
            RowMeta meta,
            V2FastEstimateResult estimate,
            V2FastEstimateSourceFacts facts)
        {
            if (meta.BoundEstimate is not { Authority: EstimateAuthorityLevel.Preliminary } preliminary ||
                meta.CurrentEstimatePlanIdentity is not { } identity)
                return;

            try
            {
                string profile = !string.IsNullOrWhiteSpace(meta.CustomCompressionProfile)
                    ? meta.CustomCompressionProfile!
                    : IsAutomaticQualitySelected()
                        ? "Medium Quality (Default)"
                        : comboCompressionProfile.SelectedItem?.ToString() ?? "Medium";
                ProductionPlanIdentity? rebuilt = CreateQueueEstimatePlanIdentity(
                    meta, meta.Path, profile, out EncodeExecutionSnapshotSettings? previewSettings,
                    out EncodeExecutionSnapshotItem? previewItem);
                if (rebuilt is null || previewSettings is null || previewItem is null ||
                    !string.Equals(rebuilt.PlanFingerprint, identity.PlanFingerprint, StringComparison.Ordinal) ||
                    (!previewSettings.AutomaticQuality && !previewSettings.NumericQualityValue.HasValue) ||
                    profile.Equals("No Compression", StringComparison.OrdinalIgnoreCase))
                    return;

                VideoRestorationPipelinePlan restoration = VideoRestorationPipeline.BuildPlan(
                    _config.VideoRestoration, GetSelectedScaleMode());
                bool decisionRelevant = previewSettings.AutomaticQuality && _config.StorageSavings.Enabled &&
                    !restoration.UsesAi && string.IsNullOrWhiteSpace(restoration.ConventionalFilterChain) &&
                    string.IsNullOrWhiteSpace(restoration.PreAiFilterChain) &&
                    string.IsNullOrWhiteSpace(restoration.PostAiFilterChain);
                _estimateService.QueueEmpiricalRefinement(
                    meta.Path,
                    meta.QueueItemId,
                    meta.EstimateAuthorityRevision,
                    meta.QueueAcceptedUtc,
                    facts.SourceSizeBytes / (1024d * 1024d),
                    estimate.EstimatedTotalBytes / (1024d * 1024d),
                    facts.DurationSeconds,
                    $"{facts.Width}x{facts.Height}",
                    facts.VideoCodec,
                    facts.FrameRate ?? 0,
                    previewSettings,
                    previewItem with { SourceSizeBytes = facts.SourceSizeBytes, MediaDurationSeconds = facts.DurationSeconds },
                    rebuilt,
                    preliminary,
                    meta.EstimateRefinementPriority,
                    decisionRelevant);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"V2 estimate refinement was not scheduled for {Path.GetFileName(meta.Path)}: {ex.Message}");
            }
        }

        private static string BuildV2EstimateTooltip(
            V2FastEstimateResult estimate,
            V2FastEstimateSourceFacts facts,
            bool sourceFactsReused,
            double probeMilliseconds)
        {
            string saved = FormatSize(estimate.EstimatedSavingsBytes);
            string fingerprint = estimate.ProductionPlan?.PlanFingerprint ?? "unbound";
            return $"Preliminary V2 fast estimate · advisory · plan {fingerprint[..Math.Min(12, fingerprint.Length)]}" + Environment.NewLine +
                   $"Estimated savings: {saved} ({estimate.EstimatedSavingsPercent:0.0}%)" + Environment.NewLine +
                   $"Savings class: {estimate.SavingsClassification.Kind} (advisory)" + Environment.NewLine +
                   $"Confidence: {estimate.Confidence}; {estimate.EstimateBasis}" + Environment.NewLine +
                   $"Source facts: {(sourceFactsReused ? "reused" : "probed")}; probe {probeMilliseconds:0.##} ms" + Environment.NewLine +
                   $"Calculation: {estimate.CalculationMilliseconds:0.###} ms; {facts.Width}x{facts.Height}";
        }

        private bool TryBindEstimateResult(RowMeta meta, EstimateBackgroundService.SmartEstimateResult result)
        {
            if (result.PlanIdentity is not { } identity || meta.CurrentEstimatePlanIdentity is not { } expected ||
                !string.Equals(expected.SourceIdentity, identity.SourceIdentity, StringComparison.Ordinal))
                return false;
            bool estimateMatches = result.BoundEstimate is { } estimate && estimate.Matches(identity);
            bool preparedPlanMatches = result.PreparedProductionPlan is { } prepared &&
                string.Equals(prepared.Identity.PlanFingerprint, identity.PlanFingerprint, StringComparison.Ordinal) &&
                (prepared.Estimate is null || prepared.Estimate.Matches(identity));
            return (estimateMatches || preparedPlanMatches) &&
                string.Equals(expected.PlanFingerprint, identity.PlanFingerprint, StringComparison.Ordinal) &&
                string.Equals(identity.SourceIdentity,
                    ProductionPlanFingerprintService.CaptureSourceIdentity([result.Path]), StringComparison.Ordinal);
        }

        private void ClearBoundQueueEstimate(DataGridViewRow row, RowMeta meta, string display, string tooltip)
        {
            meta.BoundEstimate = null;
            _estimatedSizeMap.Remove(meta.Path);
            row.Cells["colEstimatedSize"].Value = display;
            row.Cells["colEstimatedSize"].Tag = null;
            row.Cells["colEstimatedSize"].ToolTipText = tooltip;
            MarkQueueTotalsDirty();
        }

        private void ApplyProductionPlanBoundEstimate(
            DataGridViewRow row,
            RowMeta meta,
            EncodingPlan plan)
        {
            ProductionPlanIdentity? identity = plan.ProductionPlan;
            if (meta.BoundEstimate?.Matches(identity) == true)
                return;
            ClearBoundQueueEstimate(row, meta, "Unavailable", "The estimate no longer matches the frozen production plan.");
        }

        private void RunEstimatePass()
        {
            RunEstimatePass(force: false);
        }

        private void RunEstimatePass(bool force)
        {
            DateTime? qualityChangeStartedUtc = _qualityChangeEstimateRefreshUtc;
            _qualityChangeEstimateRefreshUtc = null;
            _estimateService.ResetAndCancel();

            if (dgvEncodeQueue.Rows.Count == 0)
                return;

            _largeQueueModeActive = dgvEncodeQueue.Rows.Count >= GetLargeQueueThreshold();
            bool deferLegacyForLargeQueue = false;
            _estimatesDeferredForLargeQueue = false;

            bool autoRequested = chkAutoTargetSize.Checked;
            EncodingQualityIntent? automaticQualityIntent =
                IsAutomaticQualitySelected()
                    ? EncodingQualityIntent.AutomaticTarget(GetSelectedQualityTarget())
                    : null;
            // The old profile remains meaningful for Manual mode and explicit
            // per-row overrides. Automatic estimation must use the same resolved
            // encoder quality as execution, not this hidden legacy selection.
            string profile = automaticQualityIntent == null
                ? comboCompressionProfile.SelectedItem?.ToString() ?? "Medium"
                : "Medium Quality (Default)";
            ValidatedEncoderSettings encoderSettings =
                GetValidatedEncoderSettingsFromUi(
                    includeConcurrentSessions: true);
            VideoEncoderSelection targetEncoder =
                encoderSettings.Resolved.Selection;
            int quality = encoderSettings.QualityValue;
            int? targetHeight = GetEstimateTargetHeight();
            VideoRestorationPipelinePlan restorationPlan = VideoRestorationPipeline.BuildPlan(
                _config.VideoRestoration, GetSelectedScaleMode());
            bool sourceAdaptiveCeilingEligible = automaticQualityIntent != null &&
                !restorationPlan.UsesAi &&
                string.IsNullOrWhiteSpace(restorationPlan.ConventionalFilterChain) &&
                string.IsNullOrWhiteSpace(restorationPlan.PreAiFilterChain) &&
                string.IsNullOrWhiteSpace(restorationPlan.PostAiFilterChain);

            double manualTargetMb = 0;
            if (!autoRequested && double.TryParse(txtTargetSize.Text, out var m) && m > 0)
                manualTargetMb = m;

            // A valid manual target wins when Auto is off. If the field is blank,
            // continue showing the metadata-based estimate for the selected Quality /
            // File Size profile; the empty field is not an error state.
            bool useProfileEstimate = SizeEstimateService.ShouldUseProfileEstimate(
                autoRequested,
                manualTargetMb);

            Dictionary<Guid, QueueEstimateRefinementPriority> refinementPriorities = GetQueueRefinementPriorities();
            int queued = 0;
            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                // The active job owns both execution state and source probing for
                // this item. Leave its estimate/presentation data intact while
                // continuing to schedule unrelated idle rows.
                if (IsQueueRowActivelyEncoding(row))
                    continue;

                var meta = row.Tag as RowMeta;
                string? path = meta?.Path ?? row.Tag as string;

                if (meta?.ExcludedFromEncodeAsDuplicate == true)
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    SetRecommendationUnavailable(
                        row,
                        "Exact duplicate is excluded from encoding.");
                    SetEncodeRowState(
                        row,
                        "Excluded - exact duplicate",
                        "",
                        "",
                        "Exact duplicate soft-excluded from encoding. The source file was not changed.");
                    continue;
                }

                bool isCustom = RowHasCustomSettings(meta);
                string rowProfile = !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile)
                    ? meta!.CustomCompressionProfile!
                    : profile;
                double rowManualTargetMb = meta?.CustomTargetMb is > 0
                    ? meta.CustomTargetMb.Value
                    : manualTargetMb;
                bool rowAuto = meta?.CustomTargetMb is > 0
                    ? false
                    : !string.IsNullOrWhiteSpace(meta?.CustomCompressionProfile) || useProfileEstimate;

                if (meta?.IsDvdEncode == true)
                {
                    meta.EstimateState = QueueEstimateState.Pending;
                    meta.BoundEstimate = null;
                    meta.PreparedProductionPlan = null;
                    meta.IntelligencePlan = null;
                    _estimatedSizeMap.Remove(path!);
                    row.Cells["colSize"].Value = meta.SrcMb > 0 ? FormatSize(meta.SrcMb) : "—";
                    row.Cells["colEstimatedSize"].Value = "Pending…";
                    row.Cells["colEstimatedSize"].Tag = null;
                    row.Cells["colEstimatedSize"].ToolTipText =
                        "The exact DVD title plan is prepared before encoding. No arithmetic output-size estimate is shown.";
                    SetRecommendationUnavailable(
                        row,
                        "DVD title recommendations remain in the DVD import workflow.");
                    UpdateRowCustomFlag(row);
                    RestoreQueuedStateAfterEstimate(row);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(path) || (qualityChangeStartedUtc is null && !File.Exists(path)))
                {
                    row.Cells["colEstimatedSize"].Value = "";
                    SetRecommendationUnavailable(
                        row,
                        "The source file is unavailable.");
                    continue;
                }

                if (IsQueueRowActivelyEncoding(row))
                    continue;

                RowMeta estimateMeta = meta ?? EnsureRowMeta(row);
                if (string.IsNullOrWhiteSpace(estimateMeta.Path))
                    estimateMeta.Path = path;
                estimateMeta.EstimateAuthorityRevision = checked(estimateMeta.EstimateAuthorityRevision + 1);
                estimateMeta.EstimateRefinementPriority = refinementPriorities.GetValueOrDefault(
                    estimateMeta.QueueItemId, QueueEstimateRefinementPriority.Background);

                estimateMeta.BoundEstimate = null;
                estimateMeta.CurrentEstimatePlanIdentity = null;
                estimateMeta.PreparedAdaptiveProductionPlan = null;
                estimateMeta.PreparedProductionPlan = null;
                estimateMeta.IntelligencePlan = null;
                estimateMeta.PlanBindingRequired = false;
                estimateMeta.EstimateState = QueueEstimateState.Pending;
                estimateMeta.EstimateRefinementPriority = refinementPriorities.GetValueOrDefault(
                    estimateMeta.QueueItemId, QueueEstimateRefinementPriority.Background);
                _estimatedSizeMap.Remove(path);
                row.Cells["colEstimatedSize"].Value = "Pending…";
                row.Cells["colEstimatedSize"].Tag = null;
                row.Cells["colEstimatedSize"].ToolTipText = "The exact production plan is being prepared for measured estimation.";

                if (estimateMeta.QueueAdmissionPending)
                    continue;

                // Keep the path → row map in sync for the UI pump
                _rowsByPath[path] = row;
                _estimatedSizeMap.Remove(path);
                estimateMeta.EmpiricalPreview = null;
                estimateMeta.SizePredictionCalibration = null;
                row.Cells["colEstimatedSize"].Value = "Pending…";
                row.Cells["colEstimatedSize"].Tag = null;
                row.Cells["colEstimatedSize"].ToolTipText = "The exact production plan will be measured when this row is selected or is near-term.";
                SetRecommendationAnalyzing(row);
                // Queue estimate work; UI pump will apply results
                StorageSavingsOptions rowStorageSavings =
                    _config.StorageSavings.CloneNormalized();
                if (automaticQualityIntent is null && rowAuto)
                {
                    // Manual File-size estimate profiles own the estimate-only quality
                    // offset. Storage Savings bitrate targets are an Automatic-mode
                    // policy; leaving one enabled here makes the legacy fallback ignore
                    // the selected profile. This is a clone used only by estimation and
                    // does not change the frozen production encoding settings.
                    rowStorageSavings.Enabled = false;
                }
                if (estimateMeta.CustomTargetMb is > 0 ||
                    !string.IsNullOrWhiteSpace(estimateMeta.CustomCompressionProfile))
                {
                    rowStorageSavings.Enabled = false;
                }
                _ = CreateQueueEstimatePlanIdentity(
                    estimateMeta, path, rowProfile,
                    out EncodeExecutionSnapshotSettings? previewSettings,
                    out EncodeExecutionSnapshotItem? previewItem);
                QueueEstimate(
                    path,
                    rowAuto,
                    rowProfile,
                    rowManualTargetMb,
                    targetEncoder,
                    quality,
                    targetHeight,
                    GetSelectedAudioChannels(),
                    isCustom,
                    rowStorageSavings,
                    isCustom ? null : automaticQualityIntent,
                    sourceAdaptiveCeilingEligible,
                    estimateMeta.QueueItemId,
                    estimateMeta.QueueAcceptedUtc,
                    encoderSettings.Preset,
                    encoderSettings.TenBit ? 10 : 8,
                    encoderSettings.ConcurrentEncoderSessions,
                    previewSettings, previewItem,
                    estimateMeta.EstimateAuthorityRevision,
                    qualityChangeStartedUtc,
                    null,
                    estimateMeta.EstimateRefinementPriority);
                queued++;
            }

            _lastEstimateQueuedCount = queued;
            _estimatesDeferredForLargeQueue = deferLegacyForLargeQueue;
            MarkQueueTotalsDirty();
            UpdateSizeTotals(force: true);
            if (queued > 0)
            {
                SetQueueWorkCancelVisible(true);
                SetQueueProgress(0, queued, visible: true);
                toolStripStatusLabel1.Text = $"Analyzing queue metadata... 0/{queued:N0}";
                UpdateRelocatedEncodeStatus(toolStripStatusLabel1.Text);
            }
            UpdateAnalyzeQueueButtonState();

            // Make sure timers are alive (constructor already calls this, but this is cheap)
            StartEstimateUiPump();
        }

        private void RestoreQueuedStateAfterEstimate(DataGridViewRow row)
        {
            if (row == null || row.IsNewRow || row.DataGridView != dgvEncodeQueue ||
                IsQueueRowActivelyEncoding(row))
                return;

            string status = row.Cells["colStatus"].Value?.ToString() ?? "";
            if (status.Equals("Estimating", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(status))
            {
                SetEncodeRowState(row, "Queued", row.Cells["colProgress"].Value?.ToString(), row.Cells["colETA"].Value?.ToString(), "Ready to encode.");
            }
            else
            {
                ApplyEncodeRowVisualState(row);
            }
        }

        private void QueueEstimate(
            string path,
            bool auto,
            string profile,
            double manualTargetMb,
            VideoEncoderSelection targetEncoder,
            int quality,
            int? targetHeight,
            int? targetAudioChannels,
            bool isCustom,
            StorageSavingsOptions storageSavings,
            EncodingQualityIntent? qualityIntent,
            bool sourceAdaptiveCeilingEligible,
            Guid queueItemId,
            DateTime queueAcceptedUtc,
            string encoderPreset,
            int outputBitDepth,
            bool concurrentEncoderSessions,
            EncodeExecutionSnapshotSettings? previewSettings = null,
            EncodeExecutionSnapshotItem? previewItem = null,
            long estimateAuthorityRevision = 0,
            DateTime? qualityChangeStartedUtc = null,
            ProductionPlanIdentity? planIdentity = null,
            QueueEstimateRefinementPriority refinementPriority = QueueEstimateRefinementPriority.Background)
        {
            _estimateService.QueueSmartEstimate(
                path,
                auto,
                profile,
                manualTargetMb,
                targetEncoder,
                quality,
                targetHeight,
                targetAudioChannels,
                isCustom,
                _config.SmartRecommendationsEnabled,
                _config.MinimumExpectedSavingsPercent,
                storageSavings,
                qualityIntent,
                sourceAdaptiveCeilingEligible,
                _config.UseHistoricalSizePredictionCalibration && qualityChangeStartedUtc is null,
                queueItemId,
                encoderPreset,
                outputBitDepth,
                concurrentEncoderSessions,
                previewSettings,
                previewItem,
                estimateAuthorityRevision,
                queueAcceptedUtc: queueAcceptedUtc,
                qualityChangeStartedUtc: qualityChangeStartedUtc,
                planIdentity: planIdentity,
                refinementPriority: refinementPriority,
                empiricalPreviewEnabled: previewSettings is not null && previewItem is not null &&
                    !profile.Equals("No Compression", StringComparison.OrdinalIgnoreCase) &&
                    previewItem.CustomTargetMb is null &&
                    previewItem.LibraryPolicyIntent?.ProjectedOutputBytes is null &&
                    (qualityIntent is not null || previewSettings.AutomaticQuality ||
                     previewSettings.NumericQualityValue.HasValue));
        }

        private ProductionPlanIdentity? CreateQueueEstimatePlanIdentity(
            RowMeta meta,
            string path,
            string profile,
            out EncodeExecutionSnapshotSettings? previewSettings,
            out EncodeExecutionSnapshotItem? previewItem)
        {
            previewSettings = null;
            previewItem = null;
            try
            {
                EncodeExecutionSnapshotSettings settings = CaptureEncodeExecutionSnapshotSettings(
                    GetSelectedOutputContainer()) with { CompressionProfile = profile };
                int? sourceWidth = null;
                int? sourceHeight = null;
                string[] resolution = (meta.Resolution ?? string.Empty).Split('x', 'X');
                if (resolution.Length == 2 && int.TryParse(resolution[0], out int parsedWidth) && parsedWidth > 0 &&
                    int.TryParse(resolution[1], out int parsedHeight) && parsedHeight > 0)
                {
                    sourceWidth = parsedWidth;
                    sourceHeight = parsedHeight;
                }
                var item = new EncodeExecutionSnapshotItem
                {
                    SourceFilePath = path,
                    LogicalSourcePath = path,
                    SourceMeasurements = sourceHeight is int height && sourceWidth is int width
                        ? new EncodeExecutionSourceMeasurements { Width = width, Height = height }
                        : null,
                    CustomCompressionProfile = meta.CustomCompressionProfile,
                    CustomTargetMb = meta.CustomTargetMb,
                    PredictionShadowExperimentAssignment = meta.PredictionShadowExperimentAssignment,
                    SourceSizeBytes = meta.SourceSizeBytes,
                    MediaDurationSeconds = meta.DurationSec > 0 ? meta.DurationSec : null,
                    LibraryPolicyIntent = meta.LibraryPolicyIntent
                };
                EncodeExecutionSnapshot snapshot = _encodeExecutionSnapshotBuilder.Build(
                    new EncodeExecutionSnapshotIdentity
                    {
                        OperationId = string.IsNullOrWhiteSpace(meta.StatisticsOperationId)
                            ? meta.QueueItemId.ToString("N")
                            : meta.StatisticsOperationId,
                        StatisticsStartUtc = meta.StatisticsStartUtc,
                        MediaFluxVersion = Application.ProductVersion,
                        StatisticsPath = AppPaths.EncodingStatisticsFile,
                        ContainerCompatibilityConfirmed = false
                    },
                    settings,
                    item);
                previewSettings = settings;
                previewItem = item;
                return snapshot.ProductionPlan;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Could not bind queue estimate to a production plan for {Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private bool IsEstimateSourceOwnedByActiveEncode(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string normalizedPath = NormalizeEstimatePath(path);
            return _runningEncodeJobs.Values.Any(activePath =>
                StringComparer.OrdinalIgnoreCase.Equals(
                    NormalizeEstimatePath(activePath),
                    normalizedPath));
        }

        private static string NormalizeEstimatePath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path.Trim(); }
        }

        private static bool PathsIdentifySameSource(string left, string right) =>
            !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                NormalizeEstimatePath(left),
                NormalizeEstimatePath(right));

        private int? GetEstimateTargetHeight()
        {
            return GetSelectedScaleMode() switch
            {
                EncodingService.ScaleMode.To720p => 720,
                EncodingService.ScaleMode.To1080p => 1080,
                EncodingService.ScaleMode.To1440p => 1440,
                EncodingService.ScaleMode.To4K => 2160,
                _ => null
            };
        }

        private bool IsNoCompressionSelected()
        {
            var text = comboCompressionProfile?.SelectedItem?.ToString()
                       ?? comboCompressionProfile?.Text
                       ?? string.Empty;
            return text.Equals("No Compression", StringComparison.OrdinalIgnoreCase);
        }

        private void chkAutoTargetSize_CheckedChanged(object? sender, EventArgs e)
        {
            // Re-run the unified estimator (same as Refresh button)
            RunEstimatePass();
        }

        // totals & size formatting helpers

        private void MarkQueueTotalsDirty()
        {
            _queueTotalsDirty = true;
        }

        private void UpdateSizeTotals()
        {
            UpdateSizeTotals(force: false);
        }

        private void UpdateSizeTotals(bool force)
        {
            // Ensure we run on the UI thread
            if (InvokeRequired)
            {
                Ui(() => UpdateSizeTotals(force));
                return;
            }

            if (dgvEncodeQueue == null || dgvEncodeQueue.Rows.Count == 0)
            {
                _queueTotalSourceMb = 0;
                _queueTotalEstimatedMb = 0;
                _queueTotalEstimatedSourceMb = 0;
                _queueTotalSavingsEstimateOutputMb = 0;
                _queueEstimatedFileCount = 0;
                _queueSavingsEstimateFileCount = 0;
                _queueEstimateEligibleFileCount = 0;
                _queueFileCount = 0;
                _queueSourceSizeMap.Clear();
                _estimatedSizeMap.Clear();
                _queueTotalsDirty = false;
                if (_summaryFileCountValue != null) _summaryFileCountValue.Text = "0";
                if (_summaryTotalCurrentValue != null) _summaryTotalCurrentValue.Text = "--";
                if (_summaryNewSizeValue != null) _summaryNewSizeValue.Text = "--";
                if (_summaryEstimatedCompletionValue != null) _summaryEstimatedCompletionValue.Text = "--";
                if (_summaryTotalEstimatedSavedValue != null) _summaryTotalEstimatedSavedValue.Text = "--";
                if (_summaryDuplicateGroupsValue != null) _summaryDuplicateGroupsValue.Text = "--";
                if (_summaryDuplicateFilesValue != null) _summaryDuplicateFilesValue.Text = "--";
                if (_summaryDuplicateRecoverableValue != null) _summaryDuplicateRecoverableValue.Text = "--";
                if (_summarySelectedCountValue != null) _summarySelectedCountValue.Text = "0";
                if (_summarySelectedSavedValue != null) _summarySelectedSavedValue.Text = "--";
                UpdateQueueCommandSummary();
                RefreshQueueWorkspacePresentation();
                return;
            }

            if (!force && !_queueTotalsDirty &&
                (DateTime.UtcNow - _lastQueueTotalsRefreshUtc).TotalMilliseconds < 500)
            {
                return;
            }

            // Rebuild from the numeric queue/cache state at each accepted refresh.
            // Incremental totals remain useful between refreshes, but grid text is
            // transient presentation and must never replace a completed estimate.
            RebuildQueueTotalsFromGrid();

            _lastQueueTotalsRefreshUtc = DateTime.UtcNow;

            if (_summaryFileCountValue != null)
                _summaryFileCountValue.Text = $"{_queueFileCount} file{(_queueFileCount == 1 ? "" : "s")}";

            if (_summaryTotalCurrentValue != null)
                _summaryTotalCurrentValue.Text = _queueTotalSourceMb > 0 ? FormatSize(_queueTotalSourceMb) : "--";

            if (_summaryNewSizeValue != null)
                _summaryNewSizeValue.Text = GetQueueEstimatedOutputSummary();

            if (_summaryTotalEstimatedSavedValue != null)
                _summaryTotalEstimatedSavedValue.Text = GetQueueEstimatedSavingsSummary();

            UpdateQueueCommandSummary();
            UpdateRelocatedEncodeStatus(lblEncodeStatus?.Text);

            UpdateSelectedSpaceTotals();
            // Totals and background estimates are dynamic presentation state, not
            // encoding-configuration changes. Keep the frozen Encoding Plan cached.
            UpdateQueueSelectionPreview();
            RefreshQueueWorkspacePresentation();
        }

        private void RebuildQueueTotalsFromGrid()
        {
            var previousSourceSizes = new Dictionary<string, double>(
                _queueSourceSizeMap,
                _queueSourceSizeMap.Comparer);
            var rebuiltSourceSizes = new Dictionary<string, double>(
                _queueSourceSizeMap.Comparer);
            _queueTotalSourceMb = 0;
            _queueTotalEstimatedMb = 0;
            _queueTotalEstimatedSourceMb = 0;
            _queueTotalSavingsEstimateOutputMb = 0;
            _queueEstimatedFileCount = 0;
            _queueSavingsEstimateFileCount = 0;
            _queueEstimateEligibleFileCount = 0;
            _queueFileCount = 0;

            foreach (DataGridViewRow row in dgvEncodeQueue.Rows)
            {
                if (row.IsNewRow) continue;

                _queueFileCount++;
                string? path = GetPathFromRow(row);
                RowMeta? meta = row.Tag as RowMeta;
                bool estimateEligible = meta?.ExcludedFromEncodeAsDuplicate != true &&
                    meta?.AdaptiveSelectionSkipped != true;
                if (estimateEligible)
                    _queueEstimateEligibleFileCount++;
                double sourceMb = 0;
                if (!string.IsNullOrWhiteSpace(path) &&
                    previousSourceSizes.TryGetValue(path, out double cachedSourceMb) &&
                    cachedSourceMb > 0)
                {
                    sourceMb = cachedSourceMb;
                }
                else if (meta?.SourceSizeBytes is > 0)
                {
                    sourceMb = meta.SourceSizeBytes.Value / (1024d * 1024d);
                }
                else if (meta?.SrcMb > 0)
                {
                    sourceMb = meta.SrcMb;
                }
                else if (!string.IsNullOrWhiteSpace(path))
                {
                    sourceMb = GetMbOnDisk(path);
                }

                double estimatedMb = estimateEligible && !string.IsNullOrWhiteSpace(path) &&
                    _estimatedSizeMap.TryGetValue(path, out double mappedEstimateMb) &&
                    mappedEstimateMb > 0
                        ? mappedEstimateMb
                        : 0;

                _queueTotalSourceMb += sourceMb;

                if (!string.IsNullOrWhiteSpace(path) && sourceMb > 0)
                    rebuiltSourceSizes[path] = sourceMb;

                if (estimatedMb > 0)
                {
                    _queueTotalEstimatedMb += estimatedMb;
                    _queueEstimatedFileCount++;
                    if (sourceMb > 0)
                    {
                        _queueTotalEstimatedSourceMb += sourceMb;
                        _queueTotalSavingsEstimateOutputMb += estimatedMb;
                        _queueSavingsEstimateFileCount++;
                    }
                }
            }

            _queueSourceSizeMap.Clear();
            foreach (var (path, sourceMb) in rebuiltSourceSizes)
                _queueSourceSizeMap[path] = sourceMb;

            _queueTotalsDirty = false;
        }

        private string GetQueueEstimatedOutputSummary()
        {
            if (_queueFileCount <= 0)
                return "--";
            if (_queueEstimateEligibleFileCount <= 0)
                return "No estimates applicable";
            if (_queueEstimatedFileCount <= 0 || _queueTotalEstimatedMb <= 0)
                return "Waiting for estimates";

            string estimate = FormatSize(_queueTotalEstimatedMb);
            return _queueEstimateEligibleFileCount > 0 &&
                   _queueEstimatedFileCount == _queueEstimateEligibleFileCount
                ? estimate
                : $"{estimate} partial ({_queueEstimatedFileCount:N0}/{_queueEstimateEligibleFileCount:N0})";
        }

        private string GetQueueEstimatedSavingsSummary()
        {
            if (_queueEstimatedFileCount <= 0 || _queueSavingsEstimateFileCount <= 0 ||
                _queueTotalEstimatedSourceMb <= 0)
            {
                return "--";
            }

            double savedMb = Math.Max(
                0,
                _queueTotalEstimatedSourceMb - _queueTotalSavingsEstimateOutputMb);
            if (savedMb <= 0)
                return "--";

            double savedPercent = savedMb / _queueTotalEstimatedSourceMb * 100d;
            bool complete = _queueEstimateEligibleFileCount > 0 &&
                            _queueEstimatedFileCount == _queueEstimateEligibleFileCount &&
                            _queueSavingsEstimateFileCount == _queueEstimateEligibleFileCount;
            return complete
                ? $"{FormatSize(savedMb)} ({savedPercent:0}% saved)"
                : $"{FormatSize(savedMb)} partial ({_queueSavingsEstimateFileCount:N0}/{_queueEstimateEligibleFileCount:N0}; {savedPercent:0}% saved)";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "";
            double mb = bytes / (1024.0 * 1024.0);
            return FormatSize(mb); // existing MB-based formatter
        }

        private static string FormatSize(double mb)
        {
            if (mb <= 0) return "";
            if (mb >= 1024) return $"{mb / 1024.0:0.##} GB";
            return $"{mb:0.#} MB";
        }

        private static double GetMbOnDisk(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists) return fi.Length / (1024.0 * 1024.0);
            }
            catch { }
            return 0;
        }

        private static string PercentReduction(double sourceMb, double estMb)
        {
            if (sourceMb <= 0 || estMb <= 0 || estMb >= sourceMb) return "(N/A)";
            double reduction = 1.0 - (estMb / sourceMb);
            return $"({(reduction * 100):F0}%)"; // e.g., "(45%)"
        }

        private double ParseSizeToMb(string? sizeStr)
        {
            if (string.IsNullOrWhiteSpace(sizeStr)) return 0;

            var parts = sizeStr.Split(' ');
            if (parts.Length < 2) return 0;

            if (!double.TryParse(parts[0], out var value)) return 0;

            string unit = parts[1].ToUpperInvariant();
            return unit switch
            {
                "GB" => value * 1024,
                "MB" => value,
                "KB" => value / 1024,
                _ => value
            };
        }
    }
}
