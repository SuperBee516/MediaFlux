using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueEstimateSummaryUiTests
{
    [Theory]
    [InlineData("Encoding", false)]
    [InlineData("AdaptiveSkip", false)]
    [InlineData("AdaptiveSkip", true)]
    [InlineData("StoragePolicyRejected", true)]
    [InlineData("Canceled", true)]
    [InlineData("Failed", true)]
    [InlineData("ActualOutput", true)]
    [InlineData("Retry Queued", true)]
    [InlineData("PolicyCRetry", false)]
    public void PreProductionPreviewCannotRegainAuthorityDuringOrAfterProduction(string state, bool released)
    {
        RunOnUiThread(main =>
        {
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.authority-handoff");
            QueuePreviewResult(service, meta, path, Field<long>(meta, "EstimateAuthorityRevision"));
            Invoke(main, "ClaimQueueRowForProduction", row, path);
            Assert.Equal(1, Field<long>(meta, "EstimateAuthorityRevision"));
            Assert.True((bool)Invoke(main, "IsQueueRowActivelyEncoding", row)!);
            EncodingPlan plan = BuildAdaptivePlan(state == "AdaptiveSkip"
                ? AdaptiveSelectionDisposition.Skipped : AdaptiveSelectionDisposition.Selected, state == "AdaptiveSkip" ? null : 26);
            Invoke(main, "ApplyAdaptivePlanSnapshotPresentation", row, meta, plan);
            SetField(meta, "IntelligenceOutcome", new EncodingExecutionOutcome(plan.PlanId, [], [],
                TerminalResult: state == "ActualOutput" ? EncodingTerminalResult.Completed : EncodingTerminalResult.NotRun));
            SetField(meta, "FailureAnalysis", new EncodeFailureAnalysis(EncodeFailureCategory.Output, "Production",
                "Production result", "Production evidence", "Retain source", "Validated production diagnostic", EncodeFailureConfidence.High, null));
            SetField(meta, "CurrentProcessingStage", state);
            SetField(meta, "AutoRetryScheduled", state == "Retry Queued");
            row.Cells["colStatus"].Value = state;
            row.Cells["colProgress"].Value = released ? "Terminal" : "42%";
            row.Cells["colETA"].Value = "Production ETA";
            if (state != "AdaptiveSkip")
            {
                row.Cells["colEstimatedSize"].Value = state == "ActualOutput" ? "8 MB actual" : "Production estimate state";
                row.Cells["colEstimatedSize"].ToolTipText = state == "ActualOutput" ? "Actual validated final output size." : "Production evidence";
                row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(12, 8);
                Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 8;
            }
            if (released) ReleaseProductionOwnership(main, row);
            Assert.Equal(!released, (bool)Invoke(main, "IsQueueRowActivelyEncoding", row)!);
            var cells = row.Cells.Cast<DataGridViewCell>().Select(c => (c.Value, c.Tag, c.ToolTipText)).ToArray();
            string[] fields = ["EmpiricalPreview", "SizePredictionCalibration", "EncodeRecommendation", "BaselineEncodeRecommendation",
                "RecommendationPending", "AdaptiveQualitySelectionPending", "AdaptiveSelectionSkipped", "IntelligencePlan",
                "IntelligenceOutcome", "FailureAnalysis", "CurrentProcessingStage", "AutoRetryScheduled", "QualityPreview", "EstimateDiagnostic"];
            object?[] metadata = fields.Select(name => Field<object?>(meta, name)).ToArray();
            var estimates = Field<Dictionary<string, double>>(main, "_estimatedSizeMap");
            var expectedEstimates = estimates.ToArray();
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Assert.Equal(cells, row.Cells.Cast<DataGridViewCell>().Select(c => (c.Value, c.Tag, c.ToolTipText)).ToArray());
            Assert.Equal(metadata, fields.Select(name => Field<object?>(meta, name)).ToArray());
            Assert.Equal(expectedEstimates, estimates.ToArray());
            Assert.Same(plan, Field<EncodingPlan>(meta, "IntelligencePlan"));
            if (state == "AdaptiveSkip")
            {
                Assert.True(Field<bool>(meta, "AdaptiveSelectionSkipped"));
                Assert.False(estimates.ContainsKey(path));
            }
            Assert.Equal(0, service.PendingEstimates);
        });
    }

    [Fact]
    public void CurrentRevisionIsStillRejectedWhileProductionOwnsRow()
    {
        RunOnUiThread(main =>
        {
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.current-owned");
            Invoke(main, "ClaimQueueRowForProduction", row, path);
            row.Cells["colEstimatedSize"].Value = "Encoding";
            QueuePreviewResult(service, meta, path, Field<long>(meta, "EstimateAuthorityRevision"));
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Assert.Equal("Encoding", row.Cells["colEstimatedSize"].Value);
            Assert.Null(Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview"));
            Assert.False(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path));
        });
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("item")]
    [InlineData("path")]
    public void CurrentAuthorityRevisionDoesNotBypassOtherEstimateIdentities(string mismatch)
    {
        RunOnUiThread(main =>
        {
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.authority-identity");
            Invoke(main, "ClaimQueueRowForProduction", row, path);
            ReleaseProductionOwnership(main, row);
            row.Cells["colEstimatedSize"].Value = "Production state";
            QueuePreviewResult(service, meta, path, Field<long>(meta, "EstimateAuthorityRevision"),
                mismatch == "generation" ? service.CurrentGeneration - 1 : null);
            if (mismatch == "item") SetField(meta, "QueueItemId", Guid.NewGuid());
            if (mismatch == "path") SetField(meta, "Path", path + ".replacement");
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Assert.Equal("Production state", row.Cells["colEstimatedSize"].Value);
            Assert.Null(Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview"));
        });
    }

    [Fact]
    public async Task FreshPostProductionWorkCapturesNewAuthorityRevisionAndMayApply()
    {
        await RunOnUiThreadAsync(async main =>
        {
            var previousService = Field<EstimateBackgroundService>(main, "_estimateService");
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = false;
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual • Legacy Numeric";
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
            var samples = new AuthoritySamples();
            var provider = new QueueEmpiricalPreviewProvider(new EncodingService(Path.GetTempPath(), _ => { }),
                new AuthorityProbe(), new EncodeExecutionSnapshotBuilder(), "test-tools",
                (_, _, _) => samples, _ => "test-video", new PreviewSamplingCoordinator());
            var service = new EstimateBackgroundService(Field<MediaInfoService>(main, "_mediaInfoService"), provider,
                _ => new() { Width = 1920, Height = 1080, Fps = 30, DurationSeconds = 100, VideoCodec = "h264", BitrateKbps = 4000 });
            previousService.Dispose();
            SetField(main, "_estimateService", service);
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.fresh-authority");
            File.WriteAllBytes(path, new byte[32]);
            try
            {
                Invoke(main, "ClaimQueueRowForProduction", row, path);
                ReleaseProductionOwnership(main, row);
                Assert.Equal(1, Field<long>(meta, "EstimateAuthorityRevision"));
                Invoke(main, "RunEstimatePass", true);
                object work = Field<object>(service, "_workItems");
                work.GetType().GetMethod("CompleteAdding")!.Invoke(work, null);
                await Task.WhenAll(Field<Task[]>(service, "_workers")).WaitAsync(TimeSpan.FromSeconds(10));
                Invoke(main, "ApplySmartEstimateResultsBatch");
                Assert.Null(Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview"));
                Assert.True(Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] > 0);
                Assert.Equal("BitrateArithmetic", Field<EncodingSizePredictionCalibration>(meta, "SizePredictionCalibration").EstimateModelId);
                Assert.Equal(0, samples.Calls);
                Assert.Equal(1, Field<long>(meta, "EstimateAuthorityRevision"));
            }
            finally { service.ResetAndCancel(); File.Delete(path); }
        });
    }

    [Fact]
    public async Task RealProductionEntryInvalidatesBeforePreviewDrainAndCleansCanceledClaim()
    {
        await RunOnUiThreadAsync(async main =>
        {
            Field<EstimateBackgroundService>(main, "_estimateService").ResetAndCancel();
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.production-entry");
            var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var preview = PreviewSamplingCoordinator.Shared.PreviewAsync(async token =>
            {
                started.SetResult();
                await drain.Task;
                token.ThrowIfCancellationRequested();
                return 1;
            }, default);
            await started.Task;
            using var cancellation = new CancellationTokenSource();
            try
            {
                var production = (Task)Invoke(main, "EncodeSingleRow", row, cancellation.Token, OutputContainerSelection.Auto)!;
                Assert.False(production.IsCompleted);
                Assert.Equal(1, Field<long>(meta, "EstimateAuthorityRevision"));
                Assert.True((bool)Invoke(main, "IsQueueRowActivelyEncoding", row)!);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => production);
                Assert.False((bool)Invoke(main, "IsQueueRowActivelyEncoding", row)!);
                Assert.Equal(1, Field<long>(meta, "EstimateAuthorityRevision"));
            }
            finally { drain.TrySetResult(); }
            await Assert.ThrowsAsync<PreviewUnavailableException>(() => preview);
            Assert.Equal(1, await PreviewSamplingCoordinator.Shared.PreviewAsync(_ => Task.FromResult(1), default));
        });
    }

    private sealed class AuthorityProbe : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaProbeResult
        {
            Success = true, DurationSeconds = 100,
            Streams = [new() { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080,
                FrameRate = 30, BitRate = 4000000, PixelFormat = "yuv420p", DurationSeconds = 100 }]
        });
    }

    private sealed class AuthoritySamples : IAdaptiveVideoSampleRunner
    {
        public int Calls { get; private set; }

        public Task<RepresentativeSampleEvidence> MeasureAsync(AdaptiveQualitySelectionRequest request, int quality,
            RepresentativeSample sample, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(
                new RepresentativeSampleEvidence(sample, 25000, 5) { Purpose = VideoSamplePurpose.PreviewEstimation });
        }
    }

    private static void ReleaseProductionOwnership(MainForm main, DataGridViewRow row) =>
        Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs").TryRemove(row, out _);

    private static EmpiricalPreviewResult QueuePreviewResult(EstimateBackgroundService service, object meta, string path,
        long revision, int? generation = null)
    {
        var preview = new EmpiricalPreviewResult(EncoderQualityMechanism.Crf, [24],
            [new(24, 5000, 500000, 400000, 6 * 1048576, 7 * 1048576, 200000, AdaptiveSampleClassification.Borderline, false)],
            24, 24, 6 * 1048576, 0, 12 * 1048576, null, null, 9, 45, 0, 1);
        Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(new(
            generation ?? service.CurrentGeneration, Field<Guid>(meta, "QueueItemId"), path, 12, 6, 100, "1920x1080", "h264", 30,
            false, null, Recommendation(SmartEncodeRecommendationKind.StrongCandidate), preview.Diagnostic, 0, 0,
            sizeCalibration: EncodingSizePredictionCalibration.Unavailable(6, "advisory") with { EstimateModelId = EmpiricalPreviewResult.ModelId },
            empiricalPreview: preview, estimateAuthorityRevision: revision));
        return preview;
    }

    [Fact]
    public void LargeQueueDefersPreviewUntilAnalyzeQueueIsExplicitlyRequested()
    {
        RunOnUiThread(main =>
        {
            var config = Field<Config>(main, "_config");
            config.LargeQueueThreshold = 1;
            config.AutoAnalyzeLargeQueues = false;
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            var (row, _, path) = AddQueueRow(main, "MediaFlux.preview-large-queue");
            File.WriteAllBytes(path, new byte[32]);
            try
            {
                Invoke(main, "RunEstimatePass", false);
                Assert.True(Field<bool>(main, "_estimatesDeferredForLargeQueue"));
                Assert.Equal(0, service.PendingEstimates);
                Invoke(main, "RunEstimatePass", true);
                Assert.False(Field<bool>(main, "_estimatesDeferredForLargeQueue"));
                Assert.Equal(1, Field<int>(main, "_lastEstimateQueuedCount"));
                Assert.Equal("Estimating…", row.Cells["colEstimatedSize"].Value);
            }
            finally { service.ResetAndCancel(); File.Delete(path); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmpiricalAndFallbackPresentationHaveDistinctAuthorityWithoutAuthoritativeSkip(bool fallback)
    {
        RunOnUiThread(main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = true;
            config.VideoRestoration = new();
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<TextBox>(main, "txtTargetSize").Text = "";
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = false;
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.empirical-presentation");
            var quality = EncodingPlanService.Create(AdaptiveStorageSavingsTests.Context() with
                { Encoder = new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265") }).Quality;
            var preview = new EmpiricalPreviewResult(EncoderQualityMechanism.Crf, [24, 26],
                [new(26, 5000, 500000, 400000, 6 * 1048576, 7 * 1048576, 200000, AdaptiveSampleClassification.Borderline, false)],
                null, 26, 6 * 1048576, 0, 12 * 1048576, 5 * 1048576,
                AdaptiveSelectionDisposition.Skipped, 9, 45, 0, 2);
            var calibration = EncodingSizePredictionCalibration.Unavailable(6, "preview") with
            {
                EstimateModelId = fallback ? "GenericBppV1" : EmpiricalPreviewResult.ModelId,
                EstimateStatus = fallback ? "LowConfidenceGenericFallback:PreviewUnavailable" : "AdvisoryPreview"
            };
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(new(
                service.CurrentGeneration, Field<Guid>(meta, "QueueItemId"), path, 12, 6, 100, "1920x1080", "h264", 30,
                false, null, Recommendation(SmartEncodeRecommendationKind.StrongCandidate),
                fallback ? "Generic BPP low confidence fallback" : preview.Diagnostic, 0, 0, quality, calibration,
                fallback ? null : preview));
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Invoke(main, "RefreshQueueWorkspaceRow", row);
            string estimate = row.Cells["colEstimatedSize"].Value!.ToString()!;
            string combined = row.Cells["colSourceEstimate"].Value!.ToString()!;
            if (fallback)
            {
                Assert.StartsWith("~", estimate);
                Assert.Contains("low confidence", estimate);
                Assert.Contains("low confidence", combined);
            }
            else
            {
                Assert.Contains("likely skip", estimate);
                Assert.Contains("CRF26", combined);
                Assert.DoesNotContain("provisional", estimate);
                Assert.Contains("Empirical projection", row.Cells["colEstimatedSize"].ToolTipText);
                Assert.Same(preview, Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview"));
            }
            Assert.False(Field<bool>(meta, "AdaptiveSelectionSkipped"));
            Assert.True(Field<bool>(meta, "RecommendationPending"));
            Assert.Equal(6d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
        });
    }

    [Fact]
    public void RapidQualityTargetChangesRefreshTheLatestEstimateGeneration()
    {
        RunOnUiThread(main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = true;
            config.VideoRestoration = new();
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Automatic • Source Adaptive";
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = false;
            Field<TextBox>(main, "txtTargetSize").Text = "";

            EstimateBackgroundService service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            int oldGeneration = service.CurrentGeneration;
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.quality-target-generation");

            TrackBar target = Field<TrackBar>(main, "trkQualityTarget");
            target.Value = 4;
            target.Value = 3;
            System.Windows.Forms.Timer refreshTimer = Field<System.Windows.Forms.Timer>(main, "_estimateRefreshTimer");
            Assert.True(refreshTimer.Enabled);

            Invoke(main, "EstimateRefreshTimer_Tick", null, EventArgs.Empty);

            Assert.Equal(oldGeneration + 1, service.CurrentGeneration);
            Assert.Equal(QualityTarget.HighQuality, Invoke(main, "GetSelectedQualityTarget"));

            MediaProbeResult source = new()
            {
                Success = true,
                Streams = [new MediaProbeStreamInfo
                {
                    CodecType = "video", CodecName = "h264", Width = 1920,
                    Height = 1080, FrameRate = 30, BitRate = 6_000_000
                }]
            };
            var encoder = new VideoEncoderSelection(
                VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc");
            QualityTarget selectedTarget = (QualityTarget)Invoke(main, "GetSelectedQualityTarget")!;
            EncodingQualityResolution quality = new EncodingQualityPolicyService().Resolve(new(
                EncodingQualityIntent.Automatic(selectedTarget), source, encoder,
                null, EncodingService.ScaleMode.None, null));
            SizeEstimateBreakdown expected =
                SizeEstimateService.EstimateAutoTargetMbSmartDetailed(
                    srcMb: 2_700, durationSec: 3_600, width: 1920, height: 1080,
                    fps: 30, sourceVideoBitrateKbps: 6_000, sourceCodec: "h264",
                    compressionProfile: "Medium Quality (Default)", targetCodec: encoder.FfmpegCodec,
                    quality: quality.EffectiveQuality!.Value, targetHeight: null,
                    storageSavings: config.StorageSavings,
                    useResolvedQualityTarget: true);

            var results = Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(
                service, "_smartResults");
            Guid queueItemId = Field<Guid>(meta, "QueueItemId");
            results.Enqueue(new EstimateBackgroundService.SmartEstimateResult(
                service.CurrentGeneration, queueItemId, path, 2_700, expected.EstimatedOutputMb,
                3_600, "1920x1080", "h264", 30, false, null, null,
                "Latest target estimate.", 0, 0, qualityResolution: quality));
            results.Enqueue(new EstimateBackgroundService.SmartEstimateResult(
                oldGeneration, queueItemId, path, 2_700, 999,
                3_600, "1920x1080", "h264", 30, false, null, null,
                "Stale prior-target estimate.", 0, 0));

            Invoke(main, "ApplySmartEstimateResultsBatch");

            Assert.Equal(expected.EstimatedOutputMb,
                Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
            Assert.Equal(QualityTarget.HighQuality,
                Field<EncodingQualityResolution>(meta, "QualityPreview").Intent.Target);
            Assert.DoesNotContain("999", Convert.ToString(row.Cells["colEstimatedSize"].Value));
        });
    }

    [Theory]
    [InlineData("h264", true, true, true)]
    [InlineData("avc", true, true, true)]
    [InlineData("h264", false, true, false)]
    [InlineData("h264", true, false, false)]
    public void AdaptivePendingEstimateRetainsValidProvisionalOutputSize(
        string sourceCodec, bool storageEnabled, bool automatic, bool pending)
    {
        RunOnUiThread(main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = storageEnabled;
            config.VideoRestoration = new();
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<TextBox>(main, "txtTargetSize").Text = "";
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = false;
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            string path = Path.Combine(Path.GetTempPath(), $"MediaFlux.adaptive-estimate.{Guid.NewGuid():N}.mkv");
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            SetField(main, "_suppressRowEvents", true);
            DataGridViewRow row;
            object meta;
            try
            {
                row = queue.Rows[queue.Rows.Add()];
                row.Tag = path;
                row.Cells["colName"].Value = Path.GetFileName(path);
                meta = Invoke(main, "EnsureRowMeta", row)!;
                SetField(meta, "Path", path);
                Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
            }
            finally { SetField(main, "_suppressRowEvents", false); }
            var calibration = EncodingSizePredictionCalibration.Unavailable(6, "preferred estimate");
            var context = AdaptiveStorageSavingsTests.Context() with
            {
                Encoder = new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265"),
                QualityIntent = automatic ? EncodingQualityIntent.Automatic(QualityTarget.Balanced) : EncodingQualityIntent.LegacyNumeric(24)
            };
            var result = new EstimateBackgroundService.SmartEstimateResult(service.CurrentGeneration,
                Field<Guid>(meta, "QueueItemId"), path, 12, 6, 100, "1920x1080", sourceCodec, 24,
                false, null, Recommendation(SmartEncodeRecommendationKind.StrongCandidate), "preferred estimate", 128, 0,
                qualityResolution: EncodingPlanService.Create(context).Quality, sizeCalibration: calibration);
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(result);
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Invoke(main, "UpdateSizeTotals", true);
            Invoke(main, "RefreshQueueWorkspaceRow", row);
            if (pending)
            {
                Assert.Contains("6 MB", row.Cells["colEstimatedSize"].Value!.ToString());
                Assert.Contains("(provisional)", row.Cells["colEstimatedSize"].Value!.ToString());
                Assert.IsType<Tuple<double, double>>(row.Cells["colEstimatedSize"].Tag);
                Assert.Equal(6d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
                Assert.Equal(6d, Field<double>(main, "_queueTotalEstimatedMb"));
                Assert.Equal("6 MB", Field<Label>(main, "_summaryNewSizeValue").Text);
                Assert.Equal(1, Field<int>(main, "_queueEstimatedFileCount"));
                Assert.Equal(6d, Field<double>(main, "_queueTotalSavingsEstimateOutputMb"));
                Assert.Equal(1, Field<int>(main, "_queueSavingsEstimateFileCount"));
                Assert.NotEqual("--", Field<Label>(main, "_summaryTotalEstimatedSavedValue").Text);
                Assert.Contains("12 MB → 6 MB", row.Cells["colSourceEstimate"].Value!.ToString());
                Assert.Contains("(provisional)", row.Cells["colSourceEstimate"].Value!.ToString());
                Assert.DoesNotContain(AdaptiveStorageSavingsPolicy.PendingEstimate, row.Cells["colSourceEstimate"].Value!.ToString());
                Assert.Contains("Provisional output-size estimate", row.Cells["colEstimatedSize"].ToolTipText);
                Assert.Contains("low confidence", row.Cells["colEstimatedSize"].ToolTipText);
                Assert.True(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
                Assert.Null(Field<object?>(meta, "SizePredictionCalibration"));
                Assert.Contains("Provisional output-size estimate", Field<string>(meta, "EstimateDiagnostic"));
                Assert.Null(Field<SmartEncodeRecommendation?>(meta, "EncodeRecommendation"));
                Assert.True(Field<bool>(meta, "RecommendationPending"));
                Assert.Equal("Adaptive pending", row.Cells["colEncodeRecommendation"].Value);
                Assert.Contains("final Source Adaptive quality decision", row.Cells["colEncodeRecommendation"].ToolTipText);
                Assert.Equal("Adaptive pending", Invoke(main, "GetQueueWorkspaceRecommendationText", row));
                Assert.False((bool)Invoke(main, "IsSmartEncodeCandidate", row)!);
                Assert.True((bool)Invoke(main, "QueueWorkspaceRecommendationNeedsAttention", meta)!);
                Assert.Equal("None", Invoke(main, "GetQueueWorkspaceRecommendationFilter", meta)?.ToString());
                Invoke(main, "RefreshQueueWorkspacePresentation");
                Assert.Equal("0", Field<Label>(main, "_queueWorkspaceReadyValue").Text);
                Assert.Equal("1", Field<Label>(main, "_queueWorkspaceAttentionValue").Text);
            }
            else
            {
                Assert.Contains("6 MB", row.Cells["colEstimatedSize"].Value!.ToString());
                Assert.Equal(6, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
                Assert.Same(calibration, Field<object?>(meta, "SizePredictionCalibration"));
                Assert.False(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
            }
        });
    }

    [Fact]
    public void AdaptivePendingEstimateWithUnavailableMetadataDoesNotFabricateOutputSize()
    {
        RunOnUiThread(main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = true;
            config.VideoRestoration = new();
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";

            EstimateBackgroundService service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.adaptive-estimate-unavailable");
            EncodingDecisionContext context = AdaptiveStorageSavingsTests.Context() with
            {
                Encoder = new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265")
            };
            SmartEncodeRecommendation unavailable = new()
            {
                Kind = SmartEncodeRecommendationKind.Unavailable,
                Confidence = SmartEncodeConfidence.Low,
                PrimaryReason = "Required media metadata is unavailable.",
                Reasons = ["Required media metadata is unavailable."]
            };
            var result = new EstimateBackgroundService.SmartEstimateResult(service.CurrentGeneration,
                Field<Guid>(meta, "QueueItemId"), path, 12, 0, 100, "1920x1080", "h264", 24,
                false, "Metadata unavailable", unavailable, "Required media metadata could not be determined.", 128, 0,
                qualityResolution: EncodingPlanService.Create(context).Quality);
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(result);

            Invoke(main, "ApplySmartEstimateResultsBatch");
            Invoke(main, "UpdateSizeTotals", true);
            Invoke(main, "RefreshQueueWorkspaceRow", row);

            Assert.Equal(AdaptiveStorageSavingsPolicy.PendingEstimate, row.Cells["colEstimatedSize"].Value);
            Assert.DoesNotContain(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
            Assert.Equal(0d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.True(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
            Assert.Contains("No numeric output-size estimate is available yet", row.Cells["colEstimatedSize"].ToolTipText);
            Assert.Contains("12 MB →", row.Cells["colSourceEstimate"].Value!.ToString());
            Assert.Contains(AdaptiveStorageSavingsPolicy.PendingEstimate, row.Cells["colSourceEstimate"].Value!.ToString());
            Assert.False(Field<bool>(meta, "RecommendationPending"));
            Assert.Equal(SmartEncodeRecommendationKind.Unavailable,
                Field<SmartEncodeRecommendation>(meta, "EncodeRecommendation").Kind);
            Assert.Equal("Unavailable", Invoke(main, "GetQueueWorkspaceRecommendationText", row));
        });
    }

    [Fact]
    public void AdaptiveEstimateReplacementUpdatesTotalsByDelta()
    {
        RunOnUiThread(main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = true;
            config.VideoRestoration = new();
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";

            EstimateBackgroundService service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.adaptive-estimate-replacement");
            Dictionary<string, double> estimates = Field<Dictionary<string, double>>(main, "_estimatedSizeMap");
            estimates[path] = 8d;
            SetField(main, "_queueTotalEstimatedMb", 8d);
            var calibration = EncodingSizePredictionCalibration.Unavailable(6, "preferred estimate");
            EncodingDecisionContext context = AdaptiveStorageSavingsTests.Context() with
            {
                Encoder = new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265")
            };
            var result = new EstimateBackgroundService.SmartEstimateResult(service.CurrentGeneration,
                Field<Guid>(meta, "QueueItemId"), path, 12, 6, 100, "1920x1080", "h264", 24,
                false, null, null, "preferred estimate", 128, 0,
                qualityResolution: EncodingPlanService.Create(context).Quality, sizeCalibration: calibration);
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(result);

            Invoke(main, "ApplySmartEstimateResultsBatch");

            Assert.Equal(6d, estimates[path]);
            Assert.Equal(6d, Field<double>(main, "_queueTotalEstimatedMb"));
            Invoke(main, "UpdateSizeTotals", true);
            Assert.Equal("6 MB", Field<Label>(main, "_summaryNewSizeValue").Text);
            Assert.Equal(1, Field<int>(main, "_queueEstimatedFileCount"));
        });
    }

    [Fact]
    public void AdaptivePlanSnapshotKeepsProvisionalSizeAndPublishesSelectedQualityInPlan()
    {
        RunOnUiThread(main =>
        {
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.adaptive-plan-selected");
            var calibration = EncodingSizePredictionCalibration.Unavailable(6, "preferred estimate");
            SetField(meta, "SizePredictionCalibration", calibration);
            SetField(meta, "AdaptiveQualitySelectionPending", true);
            SetField(meta, "RecommendationPending", true);
            row.Cells["colEstimatedSize"].Value = "6 MB  (-50.0%)";
            row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(12, 6);
            row.Cells["colEncodeRecommendation"].Value = "Adaptive pending";
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 6d;
            SetField(main, "_queueTotalEstimatedMb", 6d);
            EncodingPlan plan = BuildAdaptivePlan(AdaptiveSelectionDisposition.Selected, 26);

            Invoke(main, "ApplyAdaptivePlanSnapshotPresentation", row, meta, plan);
            Invoke(main, "RefreshQueueWorkspaceRow", row);

            Assert.Equal("6 MB  (-50.0%)", row.Cells["colEstimatedSize"].Value);
            Assert.Equal(6d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
            Assert.Equal(6d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.False(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
            Assert.False(Field<bool>(meta, "RecommendationPending"));
            Assert.Equal(SmartEncodeRecommendationKind.Unavailable,
                Field<SmartEncodeRecommendation>(meta, "EncodeRecommendation").Kind);
            Assert.Equal("Unavailable", row.Cells["colEncodeRecommendation"].Value);
            Assert.Equal("Unavailable", Invoke(main, "GetQueueWorkspaceRecommendationText", row));
            Assert.Null(Field<object?>(meta, "SizePredictionCalibration"));
            Assert.Same(plan, Field<EncodingPlan?>(meta, "IntelligencePlan"));
            Assert.Equal(26, Field<EncodingPlan?>(meta, "IntelligencePlan")!.AdaptiveSelection!.SelectedQuality);
            Assert.Contains("selected CQ 26", row.Cells["colEstimatedSize"].ToolTipText);
            Assert.Contains("12 MB → 6 MB", row.Cells["colSourceEstimate"].Value!.ToString());
        });
    }

    [Fact]
    public void AdaptiveSkipReplacesSizePresentationAndExcludesSkippedOutputFromQueueTotal()
    {
        RunOnUiThread(main =>
        {
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.adaptive-plan-skipped");
            SetField(meta, "AdaptiveQualitySelectionPending", true);
            row.Cells["colEstimatedSize"].Value = "6 MB  (-50.0%)";
            row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(12, 6);
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 6d;
            Invoke(main, "UpdateSizeTotals", true);
            EncodingPlan plan = BuildAdaptivePlan(AdaptiveSelectionDisposition.Skipped, null);

            Invoke(main, "ApplyAdaptivePlanSnapshotPresentation", row, meta, plan);
            Invoke(main, "RefreshQueueWorkspaceRow", row);

            Assert.Equal("Skipped — insufficient savings at acceptable quality", row.Cells["colEstimatedSize"].Value);
            Assert.False(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path));
            Assert.Equal(0d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.Equal(0, Field<int>(main, "_queueEstimatedFileCount"));
            Assert.Equal(0, Field<int>(main, "_queueEstimateEligibleFileCount"));
            Assert.True(Field<bool>(meta, "AdaptiveSelectionSkipped"));
            Assert.False(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
            Assert.Contains("12 MB → Skipped", row.Cells["colSourceEstimate"].Value!.ToString());
        });
    }

    [Fact]
    public void QueueSummaryDistinguishesEmptyAllExcludedAndMixedEligibility()
    {
        if (!OperatingSystem.IsWindows()) return;

        RunOnUiThread(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            Label commandSummary = Field<Label>(main, "_queueCommandSummaryLabel");
            Label outputSummary = Field<Label>(main, "_summaryNewSizeValue");
            Label workspaceOutput = Field<Label>(main, "_queueWorkspaceEstimateOutputValue");

            Invoke(main, "UpdateSizeTotals", true);
            Assert.Equal("--", outputSummary.Text);
            Assert.Equal("Est. output: --", workspaceOutput.Text);
            Assert.Contains("Estimated output: --", commandSummary.Text);

            string prefix = Path.Combine(Path.GetTempPath(), $"MediaFlux.excluded-estimates.{Guid.NewGuid():N}");
            SetField(main, "_suppressRowEvents", true);
            try
            {
                for (int index = 0; index < 2; index++)
                {
                    string path = Path.Combine(prefix, $"duplicate-{index}.mkv");
                    DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                    row.Tag = path;
                    row.Cells["colName"].Value = Path.GetFileName(path);
                    object meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetField(meta, "Path", path);
                    SetField(meta, "ExcludedFromEncodeAsDuplicate", true);
                }
            }
            finally
            {
                SetField(main, "_suppressRowEvents", false);
            }

            Invoke(main, "UpdateSizeTotals", true);
            Assert.Equal("No estimates applicable", outputSummary.Text);
            Assert.Equal("Est. output: No estimates applicable", workspaceOutput.Text);
            Assert.Contains("Estimated output: No estimates applicable", commandSummary.Text);
            Assert.DoesNotContain("Waiting for estimates", outputSummary.Text);

            SetField(main, "_suppressRowEvents", true);
            try
            {
                string path = Path.Combine(prefix, "eligible.mkv");
                DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                row.Tag = path;
                row.Cells["colName"].Value = Path.GetFileName(path);
                object meta = Invoke(main, "EnsureRowMeta", row)!;
                SetField(meta, "Path", path);
                SetField(meta, "SrcMb", 10d);
                SetField(meta, "SourceSizeBytes", 10L * 1024 * 1024);
            }
            finally
            {
                SetField(main, "_suppressRowEvents", false);
            }

            Invoke(main, "UpdateSizeTotals", true);
            Assert.Equal("Waiting for estimates", outputSummary.Text);
            Assert.Equal("Est. output: waiting for estimates", workspaceOutput.Text);
            Assert.Contains("Estimated output: Waiting for estimates", commandSummary.Text);
        });
    }

    [Fact]
    public void QueueSummaryShowsPartialCompleteAndWaitingEstimateStates()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();
                Field<System.Windows.Forms.Timer>(main, "_estSmartUiTimer").Stop();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                var estimateMap = Field<Dictionary<string, double>>(main, "_estimatedSizeMap");
                var sourceMap = Field<Dictionary<string, double>>(main, "_queueSourceSizeMap");
                var runningJobs = Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs");
                var rows = new List<DataGridViewRow>(149);
                string testPrefix = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueEstimateSummary.{Guid.NewGuid():N}");

                SetField(main, "_suppressRowEvents", true);
                try
                {
                    for (int index = 0; index < 149; index++)
                    {
                        string path = Path.Combine(testPrefix, $"item-{index:D3}.mkv");
                        DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                        row.Tag = path;
                        row.Cells["colName"].Value = Path.GetFileName(path);
                        object meta = Invoke(main, "EnsureRowMeta", row)!;
                        SetField(meta, "Path", path);
                        SetField(meta, "SrcMb", 2d);
                        SetField(meta, "SourceSizeBytes", 2L * 1024 * 1024);
                        sourceMap[path] = 2d;
                        if (index < 147)
                        {
                            estimateMap[path] = 1d;
                            row.Cells["colEstimatedSize"].Value = "1 MB";
                        }
                        else
                        {
                            row.Cells["colStatus"].Value = "Encoding";
                            row.Cells["colEstimatedSize"].Value = "Encoding";
                            runningJobs[row] = path;
                        }

                        rows.Add(row);
                    }
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }

                Invoke(main, "UpdateSizeTotals", true);
                Assert.Equal("147 MB partial (147/149)", Field<Label>(main, "_summaryNewSizeValue").Text);
                Assert.Equal("Est. output: 147 MB partial (147/149)",
                    Field<Label>(main, "_queueWorkspaceEstimateOutputValue").Text);
                Assert.Equal("147 MB partial (147/149; 50% saved)",
                    Field<Label>(main, "_summaryTotalEstimatedSavedValue").Text);

                foreach (DataGridViewRow row in rows.Skip(147))
                {
                    string path = (string)runningJobs[row];
                    estimateMap[path] = 1d;
                }
                Invoke(main, "UpdateSizeTotals", true);
                Assert.Equal("149 MB", Field<Label>(main, "_summaryNewSizeValue").Text);
                Assert.Equal("Est. output: 149 MB", Field<Label>(main, "_queueWorkspaceEstimateOutputValue").Text);
                Assert.Equal("149 MB (50% saved)", Field<Label>(main, "_summaryTotalEstimatedSavedValue").Text);

                estimateMap.Clear();
                Invoke(main, "UpdateSizeTotals", true);
                Assert.Equal("Waiting for estimates", Field<Label>(main, "_summaryNewSizeValue").Text);
                Assert.Equal("Est. output: waiting for estimates",
                    Field<Label>(main, "_queueWorkspaceEstimateOutputValue").Text);
                Assert.Equal("--", Field<Label>(main, "_summaryTotalEstimatedSavedValue").Text);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue estimate summary test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void TotalsRebuildUsesCachedEstimateWhenCellContainsPresentationText()
    {
        if (!OperatingSystem.IsWindows()) return;

        RunOnUiThread(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            string path = Path.Combine(Path.GetTempPath(), $"MediaFlux.cached-estimate.{Guid.NewGuid():N}.mkv");
            SetField(main, "_suppressRowEvents", true);
            DataGridViewRow row;
            try
            {
                row = queue.Rows[queue.Rows.Add()];
                row.Tag = path;
                row.Cells["colName"].Value = Path.GetFileName(path);
                row.Cells["colEstimatedSize"].Value = "Analyzing…";
                object meta = Invoke(main, "EnsureRowMeta", row)!;
                SetField(meta, "Path", path);
                SetField(meta, "SrcMb", 20d);
                SetField(meta, "SourceSizeBytes", 20L * 1024 * 1024);
            }
            finally
            {
                SetField(main, "_suppressRowEvents", false);
            }

            Field<Dictionary<string, double>>(main, "_queueSourceSizeMap")[path] = 20d;
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 8d;
            Invoke(main, "UpdateSizeTotals", true);

            Assert.Equal(8d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.Equal(8d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
            Assert.Equal("8 MB", Field<Label>(main, "_summaryNewSizeValue").Text);
        });
    }

    [Fact]
    public void RunningRowKeepsPreviouslyCompletedEstimateAcrossEstimatePass()
    {
        if (!OperatingSystem.IsWindows()) return;

        RunOnUiThread(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            string path = Path.Combine(Path.GetTempPath(), $"MediaFlux.active-cached-estimate.{Guid.NewGuid():N}.mkv");
            SetField(main, "_suppressRowEvents", true);
            DataGridViewRow row;
            try
            {
                row = queue.Rows[queue.Rows.Add()];
                row.Tag = path;
                row.Cells["colName"].Value = Path.GetFileName(path);
                row.Cells["colStatus"].Value = "Encoding";
                row.Cells["colEstimatedSize"].Value = "Encoding";
                object meta = Invoke(main, "EnsureRowMeta", row)!;
                SetField(meta, "Path", path);
                SetField(meta, "SrcMb", 20d);
                SetField(meta, "SourceSizeBytes", 20L * 1024 * 1024);
            }
            finally
            {
                SetField(main, "_suppressRowEvents", false);
            }

            Field<Dictionary<string, double>>(main, "_queueSourceSizeMap")[path] = 20d;
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 8d;
            Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs")[row] = path;
            SetField(main, "_encodingActive", true);

            Invoke(main, "RunEstimatePass");

            Assert.Equal("Encoding", row.Cells["colEstimatedSize"].Value);
            Assert.Equal(8d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
            Assert.Equal(8d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.Equal("8 MB", Field<Label>(main, "_summaryNewSizeValue").Text);
            Assert.Equal(0, Field<MediaFlux.Services.EstimateBackgroundService>(main, "_estimateService").PendingEstimates);
        });
    }

    private static (DataGridViewRow Row, object Meta, string Path) AddQueueRow(MainForm main, string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}.{Guid.NewGuid():N}.mkv");
        DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
        SetField(main, "_suppressRowEvents", true);
        try
        {
            DataGridViewRow row = queue.Rows[queue.Rows.Add()];
            row.Tag = path;
            row.Cells["colName"].Value = Path.GetFileName(path);
            row.Cells["colSize"].Value = "12 MB";
            object meta = Invoke(main, "EnsureRowMeta", row)!;
            SetField(meta, "Path", path);
            SetField(meta, "SrcMb", 12d);
            SetField(meta, "SourceSizeBytes", 12L * 1024 * 1024);
            Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
            return (row, meta, path);
        }
        finally
        {
            SetField(main, "_suppressRowEvents", false);
        }
    }

    private static EncodingPlan BuildAdaptivePlan(AdaptiveSelectionDisposition disposition, int? selectedQuality)
    {
        EncodingDecisionContext context = AdaptiveStorageSavingsTests.Context() with
        {
            Encoder = new(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265")
        };
        EncodingPlan initial = EncodingPlanService.Create(context);
        var evidence = new AdaptiveQualitySelectionEvidence(
            disposition,
            context.Encoder.EncoderId,
            context.Encoder.FfmpegCodec,
            QualityTarget.Balanced,
            EncoderQualityMechanism.Cq,
            24,
            selectedQuality,
            34,
            10,
            null,
            false,
            null,
            Array.Empty<AdaptiveCandidateEvidence>(),
            disposition == AdaptiveSelectionDisposition.Skipped
                ? "Skipped — insufficient savings at acceptable quality."
                : "Source Adaptive selected the highest acceptable tested quality.");
        return EncodingPlanService.FreezeAdaptiveSelection(initial, evidence);
    }

    private static SmartEncodeRecommendation Recommendation(SmartEncodeRecommendationKind kind) => new()
    {
        Kind = kind,
        Confidence = SmartEncodeConfidence.High,
        EstimatedSavingsPercent = 50,
        EstimatedSavingsMb = 6,
        PrimaryReason = "Expected savings.",
        Reasons = ["Expected savings."]
    };

    private static void RunOnUiThread(Action<MainForm> action)
    {
        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();
                Field<System.Windows.Forms.Timer>(main, "_estSmartUiTimer").Stop();
                action(main);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue estimate UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static async Task RunOnUiThreadAsync(Func<MainForm, Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            MainForm? main = null;
            string? isolatedConfigPath = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Shown += async (_, _) =>
                {
                    try
                    {
                        Field<System.Windows.Forms.Timer>(main, "_estSmartUiTimer").Stop();
                        await action(main);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { main.Close(); }
                };
                Application.Run(main);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (failure is null) completion.TrySetResult(); else completion.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static T Field<T>(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);
        return (T)field.GetValue(target)!;
    }

    private static void SetField(object target, string name, object value)
    {
        FieldInfo field = target.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name);
        field.SetValue(target, value);
    }

    private static object? Invoke(MainForm main, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        return method.Invoke(method.IsStatic ? null : main, args);
    }
}
