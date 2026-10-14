using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueEstimateSummaryUiTests
{
    private readonly ITestOutputHelper _output;

    public QueueEstimateSummaryUiTests(ITestOutputHelper output) => _output = output;

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
            ClaimProductionOwnership(main, row, path);
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
            ClaimProductionOwnership(main, row, path);
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
            ClaimProductionOwnership(main, row, path);
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
    public async Task FreshPostProductionWorkCapturesNewAuthorityRevisionAndPublishesThreeSamplePreview()
    {
        await RunOnUiThreadAsync(async main =>
        {
            var previousService = Field<EstimateBackgroundService>(main, "_estimateService");
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = false;
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual";
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
            Field<System.Windows.Forms.Timer>(main, "_estSmartUiTimer").Stop();
            var (row, meta, path) = AddQueueRow(main, "MediaFlux.fresh-authority");
            using (var source = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.SetLength(50_000_000);
            try
            {
                SetField(meta, "EstimateAuthorityRevision", 1L);
                Assert.Equal(1, Field<long>(meta, "EstimateAuthorityRevision"));
                Invoke(main, "RunEstimatePass", true);
                object work = Field<object>(service, "_workItems");
                work.GetType().GetMethod("CompleteAdding")!.Invoke(work, null);
                await Task.WhenAll(Field<Task[]>(service, "_workers")).WaitAsync(TimeSpan.FromSeconds(10));
                Task refinementDrain = (Task)typeof(EstimateBackgroundService)
                    .GetMethod("WaitForRefinementsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, [CancellationToken.None])!;
                await refinementDrain.WaitAsync(TimeSpan.FromSeconds(10));
                EstimateBackgroundService.SmartEstimateResult[] queuedResults =
                    Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").ToArray();
                Assert.Contains(queuedResults, result => result.EstimateState is QueueEstimateState.Measured or QueueEstimateState.Unavailable);
                Invoke(main, "ApplySmartEstimateResultsBatch");
                EmpiricalPreviewResult? appliedPreview = Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview");
                Assert.True(appliedPreview is not null,
                    $"Samples={samples.Calls}; pendingRefinements={service.PendingRefinements}; jobs={service.EmpiricalJobsCompleted}/{service.EmpiricalJobsLaunched}; generation={service.CurrentGeneration}; " +
                    $"revision={Field<long>(meta, "EstimateAuthorityRevision")}; state={Field<QueueEstimateState>(meta, "EstimateState")}; " +
                    $"diagnostic={Field<string>(meta, "EstimateDiagnostic")}; cell={row.Cells["colEstimatedSize"].Value}; results=" +
                    string.Join(" | ", queuedResults.Select(value => $"{value.EstimateState}:g{value.Generation}:r{value.EstimateAuthorityRevision}:id={value.PlanIdentity?.PlanFingerprint}:bound={value.BoundEstimate?.Plan.PlanFingerprint}:prepared={value.PreparedProductionPlan?.Identity.PlanFingerprint}")) + "; remaining=" +
                    string.Join(" | ", Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults")
                        .ToArray().Select(value => $"{value.EstimateState}:{value.EstimateDiagnostic}")));
                EmpiricalPreviewResult preview = appliedPreview!;
                Assert.Equal(3, preview.RegionsPerAnchor);
                Assert.True(Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] > 0);
                Assert.Null(Field<object?>(meta, "SizePredictionCalibration"));
                Assert.Equal(3, samples.Calls);
                PlanBoundEstimate bound = Assert.IsType<PlanBoundEstimate>(Field<PlanBoundEstimate?>(meta, "BoundEstimate"));
                Assert.Equal(EstimateAuthorityLevel.Measured, bound.Authority);
                Assert.Equal(EmpiricalPreviewResult.ModelId, bound.Method);
                Assert.Equal(3, bound.SampleCount);
                Assert.NotEmpty(bound.SampleEvidence!);
                PreparedProductionPlan prepared = Assert.IsType<PreparedProductionPlan>(
                    Field<PreparedProductionPlan?>(meta, "PreparedProductionPlan"));
                Assert.Equal(prepared.Identity.PlanFingerprint, bound.Plan.PlanFingerprint);
                Assert.Equal(prepared.Identity.PlanFingerprint, prepared.Snapshot.ProductionPlan!.PlanFingerprint);
                Assert.Equal(2, Field<long>(meta, "EstimateAuthorityRevision"));
            }
            finally { service.ResetAndCancel(); File.Delete(path); }
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualQueueDispatchValidatesThePublishedPlanAfterOneAtomicClaim(bool automatic)
    {
        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(
            AppPaths.InstallDirectory,
            Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFPROBE_PATH"));
        if (!tools.AreAllAvailable)
        {
            _output.WriteLine("Queue dispatch validation requires a discoverable FFmpeg/FFprobe pair; no managed, configured, or PATH pair was found.");
            return;
        }

        await RunOnUiThreadAsync(async main =>
        {
            Config config = Field<Config>(main, "_config");
            config.FfmpegPath = tools.FfmpegPath;
            config.FfprobePath = tools.FfprobePath;
            config.StorageSavings.Enabled = false;
            config.SmartRecommendationsEnabled = false;
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = automatic
                ? "Automatic"
                : "Manual";
            Field<NumericUpDown>(main, "nudAutoQuality").Value = 24;
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
            Field<TextBox>(main, "txtTargetSize").Text = "";
            if (automatic)
                Field<QualityTargetSegmentSelector>(main, "qualityTargetSelector").SetSelectedTarget(QualityTarget.Balanced);

            EstimateBackgroundService previousService = Field<EstimateBackgroundService>(main, "_estimateService");
            var samples = new AuthoritySamples();
            var provider = new QueueEmpiricalPreviewProvider(
                new EncodingService(Path.GetTempPath(), _ => { }),
                new AuthorityProbe(),
                new EncodeExecutionSnapshotBuilder(),
                "test-tools",
                (_, _, _) => samples,
                _ => "test-video",
                new PreviewSamplingCoordinator());
            var service = new EstimateBackgroundService(
                Field<MediaInfoService>(main, "_mediaInfoService"),
                provider,
                _ => new MediaInfoService.MediaInfo
                {
                    Width = 1920,
                    Height = 1080,
                    Fps = 30,
                    DurationSeconds = 100,
                    VideoCodec = "h264",
                    BitrateKbps = 4_000,
                    TotalBitrateKbps = 4_000
                });
            previousService.Dispose();
            SetField(main, "_estimateService", service);

            EncodeExecutionOrchestrator originalOrchestrator = Field<EncodeExecutionOrchestrator>(main, "_encodeExecutionOrchestrator");
            var executor = new StopBeforeEncodingExecutor();
            var testOrchestrator = new EncodeExecutionOrchestrator(
                executor,
                Field<NvencQualityModePredictionShadowService>(originalOrchestrator, "_predictionShadowService"),
                Field<EncodingStatisticsService>(originalOrchestrator, "_encodingStatisticsService"));
            SetField(main, "_encodeExecutionOrchestrator", testOrchestrator);
            SetField(main, "_encodeExecutionLifecycleService", new EncodeExecutionLifecycleService(
                testOrchestrator,
                () => Field<HistoryService>(main, "_historyService")));

            Field<System.Windows.Forms.Timer>(main, "_estSmartUiTimer").Stop();
            Field<ComboBox>(main, "cmbEncodeOutput").Text = Path.GetTempPath();
            string path = Path.Combine(Path.GetTempPath(), $"MediaFlux.{(automatic ? "automatic" : "manual")}-plan-handoff.{Guid.NewGuid():N}.mkv");
            using (var source = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.SetLength(50_000_000);
            try
            {
                bool admitted = (bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, true)!;
                Assert.True(admitted, "MainForm must admit the fixture as one queue row.");
                DataGridViewRow row = Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path];
                object meta = row.Tag ?? throw new InvalidOperationException("The admitted row has no queue metadata.");
                SetField(meta, "DurationSec", 100d);
                long originalQueueRevision = Field<long>(meta, "EstimateAuthorityRevision");
                DataGridViewRow[] eligibleRows = ((IEnumerable<DataGridViewRow>)Invoke(main,
                    "GetEligibleEncodeRowsInExecutionOrder")!).ToArray();
                Assert.Same(row, Assert.Single(eligibleRows));

                var queueRun = (Task<MainForm.EncodeQueueStartOutcome>)Invoke(main, "StartEncodeAsync", (bool?)true)!;
                MainForm.EncodeQueueStartOutcome queueOutcome = await queueRun.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(MainForm.EncodeQueueStartOutcome.Completed, queueOutcome);

                PreparedProductionPlan? maybePrepared = Field<PreparedProductionPlan?>(meta, "PreparedProductionPlan");
                Assert.True(maybePrepared is not null,
                    $"EstimateState={Field<QueueEstimateState>(meta, "EstimateState")}; " +
                    $"EstimateDiagnostic={Field<string>(meta, "EstimateDiagnostic")}; " +
                    $"ProcessingStage={Field<string>(meta, "CurrentProcessingStage")}; " +
                    $"InspectorLog={(string)meta.GetType().GetMethod("GetInspectorLogTail")!.Invoke(meta, null)!}");
                PreparedProductionPlan prepared = maybePrepared!;
                Assert.Equal(Field<Guid>(meta, "QueueItemId"), prepared.QueueItemId);
                long claimedQueueRevision = Field<long>(meta, "EstimateAuthorityRevision");
                Assert.Equal(originalQueueRevision + 1, prepared.EstimateAuthorityRevision);
                Assert.Equal(prepared.EstimateAuthorityRevision + 1, claimedQueueRevision);
                Assert.Equal(automatic ? EstimateAuthorityLevel.Preliminary : EstimateAuthorityLevel.Measured,
                    prepared.Estimate?.Authority);
                Assert.Equal(prepared.Identity.PlanFingerprint, prepared.Estimate?.Plan.PlanFingerprint);
                Assert.Equal(prepared.Identity.PlanFingerprint, prepared.Snapshot.ProductionPlan?.PlanFingerprint);
                ProductionPlanIdentity rebuilt = ProductionPlanFingerprintService.Create(
                    ProductionPlanFingerprintService.FromSnapshot(prepared.Snapshot), [path]);
                Assert.Equal(prepared.Identity.Inputs, rebuilt.Inputs);
                Assert.Equal(prepared.Identity.PlanFingerprint, rebuilt.PlanFingerprint);
                Assert.Equal(1, executor.Calls);
                Assert.Equal(prepared.Identity.PlanFingerprint, executor.Request?.ProductionPlan?.PlanFingerprint);
                string inspectorLog = (string)meta.GetType().GetMethod("GetInspectorLogTail")!.Invoke(meta, null)!;
                string validationLine = Assert.Single(inspectorLog.Split(Environment.NewLine), line =>
                    line.StartsWith("[PreparedPlanValidation] ", StringComparison.Ordinal));
                Assert.Contains("FailedPredicate=none", validationLine);
                Assert.Contains($"ExpectedQueueAuthorityRevision={claimedQueueRevision}", validationLine);
                Assert.Contains($"ActualQueueAuthorityRevision={claimedQueueRevision}", validationLine);
                Assert.Equal(prepared.Identity.PlanFingerprint, executor.Request!.ProductionPlan!.PlanFingerprint);
                EncodingRequest dispatchedRequest = executor.Request!;
                string dispatchedFingerprint = dispatchedRequest.ProductionPlan?.PlanFingerprint
                    ?? throw new Xunit.Sdk.XunitException("The queue runner did not dispatch a production plan.");
                Assert.Equal(prepared.Identity.PlanFingerprint, dispatchedFingerprint);
                Assert.Equal(prepared.Identity.PlanFingerprint, dispatchedRequest.PreparedPlan?.ProductionPlanFingerprint);
                if (automatic)
                {
                    AutomaticTargetPlan target = Assert.IsType<AutomaticTargetPlan>(prepared.Plan.AutomaticTarget);
                    Assert.Equal(target, dispatchedRequest.PreparedPlan?.AutomaticTarget);
                    Assert.Equal(prepared.Snapshot.Encoder.FfmpegCodec, dispatchedRequest.Encoder.FfmpegCodec);
                    Assert.Equal(prepared.Snapshot.EncoderPreset, dispatchedRequest.EncoderPreset);
                }
                _output.WriteLine(
                    $"QUEUE_HANDOFF_CAPTURE item={prepared.QueueItemId} originalRevision={originalQueueRevision} " +
                    $"preparedRevision={prepared.EstimateAuthorityRevision} claimedRevision={claimedQueueRevision} " +
                    $"claimCount={claimedQueueRevision - prepared.EstimateAuthorityRevision} preparedPlanId={prepared.Plan.PlanId} " +
                    $"estimateFingerprint={prepared.Estimate?.Plan.PlanFingerprint} preparedFingerprint={prepared.Identity.PlanFingerprint} " +
                    $"dispatchedFingerprint={dispatchedFingerprint} failedPredicate=none");
                AutomaticTargetPlan? automaticTarget = prepared.Plan.AutomaticTarget;
                _output.WriteLine($"DISPATCH_REQUEST_CAPTURE mode={(automatic ? "Automatic target-bitrate VBR" : "Manual Numeric CQ")} " +
                    $"encoder={dispatchedRequest.Encoder.FfmpegCodec} preset={dispatchedRequest.EncoderPreset} " +
                    $"targetKbps={automaticTarget?.TargetVideoBitrateKbps} maxrateKbps={automaticTarget?.MaximumVideoBitrateKbps} " +
                    $"bufferKbps={automaticTarget?.VideoBufferSizeKbps} mapMode={dispatchedRequest.MapMode} " +
                    $"videoStreams={string.Join(',', prepared.Snapshot.Input.VideoStreamIndexes)} " +
                    $"audioStreams={string.Join(',', prepared.Snapshot.Input.AudioStreamIndexes)}");
                if (automatic)
                {
                    Assert.Equal("AutomaticTargetProjection", prepared.Estimate?.Method);
                    Assert.Equal(AutomaticTargetDisposition.Ready, prepared.Plan.AutomaticTarget?.Disposition);
                    Assert.Equal(0, samples.Calls);
                    Assert.Equal(0, service.EmpiricalJobsLaunched);
                    Assert.Equal(0, service.EmpiricalSamplesLaunched);
                }
                else
                {
                    Assert.True(samples.Calls >= 3);
                }

                PreparedProductionPlanValidationDiagnostic extraClaim =
                    PreparedProductionPlanValidator.ValidateQueueHandoff(
                        prepared,
                        Field<Guid>(meta, "QueueItemId"),
                        Field<long>(meta, "EstimateAuthorityRevision") + 1,
                        [path],
                        service.CurrentGeneration,
                        invalidationAttempted: false,
                        repreparationAttempted: false);
                Assert.Equal(prepared.EstimateAuthorityRevision + 1, extraClaim.ExpectedQueueAuthorityRevision);
                Assert.Equal(prepared.EstimateAuthorityRevision + 2, extraClaim.ActualQueueAuthorityRevision);
                Assert.Contains("queue-authority-revision", extraClaim.FailedPredicate);
                Assert.False(extraClaim.InvalidationAttempted);
                Assert.False(extraClaim.RepreparationAttempted);
                Assert.False(extraClaim.FfmpegLaunched);
                Assert.Contains("PreparedPlanId=", extraClaim.ToLogLine());
                EncodeFailureAnalysis? failureAnalysis = EncodeFailureAnalysisService.Analyze(
                    new EncodeFailureAnalysisContext(
                        new PreparedProductionPlanValidationException(extraClaim),
                        "",
                        "Prepared plan validation",
                        false,
                        path,
                        "",
                        VideoEncoderIds.Nvenc,
                        "NVENC HEVC",
                        "hevc_nvenc",
                        "p5",
                        true,
                        "auto",
                        "Off"));
                Assert.Equal(EncodeFailureCategory.ApplicationLifecycle, failureAnalysis?.Category);
                Assert.Equal("Prepared production plan validation", failureAnalysis?.FailureStage);

                PreparedProductionPlanValidationDiagnostic generationChange =
                    PreparedProductionPlanValidator.ValidateQueueHandoff(
                        prepared,
                        Field<Guid>(meta, "QueueItemId"),
                        Field<long>(meta, "EstimateAuthorityRevision"),
                        [path],
                        service.CurrentGeneration + 1,
                        invalidationAttempted: false,
                        repreparationAttempted: false);
                Assert.Equal("none", generationChange.FailedPredicate);
                Assert.NotEqual(generationChange.ExpectedQueueGeneration, generationChange.ActualQueueGeneration);

                PreparedProductionPlan divergentSnapshot = prepared with
                {
                    BuildResult = prepared.BuildResult with
                    {
                        Snapshot = prepared.Snapshot with { OutputHeight = prepared.Snapshot.OutputHeight + 1 }
                    }
                };
                PreparedProductionPlanValidationDiagnostic fingerprintChange =
                    PreparedProductionPlanValidator.ValidateQueueHandoff(
                        divergentSnapshot,
                        Field<Guid>(meta, "QueueItemId"),
                        Field<long>(meta, "EstimateAuthorityRevision"),
                        [path],
                        service.CurrentGeneration,
                        invalidationAttempted: false,
                        repreparationAttempted: false);
                Assert.Contains("execution-fingerprint", fingerprintChange.FailedPredicate);

                PreparedProductionPlanValidationDiagnostic queueChange =
                    PreparedProductionPlanValidator.ValidateQueueHandoff(
                        prepared,
                        Guid.NewGuid(),
                        Field<long>(meta, "EstimateAuthorityRevision"),
                        [path],
                        service.CurrentGeneration,
                        invalidationAttempted: false,
                        repreparationAttempted: false);
                Assert.Contains("queue-item-identity", queueChange.FailedPredicate);

                File.AppendAllText(path, "source identity changed");
                PreparedProductionPlanValidationDiagnostic sourceChange =
                    PreparedProductionPlanValidator.ValidateQueueHandoff(
                        prepared,
                        Field<Guid>(meta, "QueueItemId"),
                        Field<long>(meta, "EstimateAuthorityRevision"),
                        [path],
                        service.CurrentGeneration,
                        invalidationAttempted: false,
                        repreparationAttempted: false);
                Assert.Contains("source-identity", sourceChange.FailedPredicate);
            }
            finally
            {
                service.ResetAndCancel();
                if (File.Exists(path)) File.Delete(path);
            }
        });
    }

    [Fact]
    public async Task OptInRealMediaQueueDispatchEncodesTheValidatedPreparedPlan()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? configuredSource = Environment.GetEnvironmentVariable("MEDIAFLUX_QUEUE_HANDOFF_SOURCE");
        string? configuredOutput = Environment.GetEnvironmentVariable("MEDIAFLUX_QUEUE_HANDOFF_OUTPUT");
        if (string.IsNullOrWhiteSpace(configuredSource) || string.IsNullOrWhiteSpace(configuredOutput))
        {
            _output.WriteLine("Opt-in real-media run not requested; set MEDIAFLUX_QUEUE_HANDOFF_SOURCE and MEDIAFLUX_QUEUE_HANDOFF_OUTPUT to execute it.");
            return;
        }

        string sourcePath = Path.GetFullPath(configuredSource);
        string outputDirectory = Path.GetFullPath(configuredOutput);
        Assert.True(File.Exists(sourcePath), "The configured real-media source must exist.");
        Assert.True(Directory.Exists(outputDirectory), "Prepare the isolated real-media output directory before running this test.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(outputDirectory));
        Assert.False(MediaFluxStoragePathService.PathsOverlap(sourcePath, outputDirectory),
            "The real-media output directory must not overlap its source.");
        string evidenceRoot = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new InvalidOperationException("The real-media output directory must have an evidence-root parent.");
        Assert.False(MediaFluxStoragePathService.PathsOverlap(sourcePath, evidenceRoot),
            "The evidence and isolated UserData roots must not overlap the source.");
        string userDataRoot = Path.Combine(evidenceRoot, "UserData");
        Assert.False(Directory.Exists(userDataRoot), "Use a fresh evidence root for each real-media queue run.");
        Directory.CreateDirectory(userDataRoot);
        MediaFluxStoragePathService storage = MediaFluxStoragePathService.ForIsolatedHeadlessRoot(userDataRoot);
        storage.InitializeDirectories();
        using IDisposable isolatedAppStorage = AppPaths.UseIsolatedHeadlessStorage(storage);
        string sourceHashBefore = HashFile(sourcePath);

        FfmpegToolPaths tools = FfmpegToolResolver.Resolve(
            AppPaths.InstallDirectory,
            Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFPROBE_PATH"));
        if (!tools.AreAllAvailable)
        {
            _output.WriteLine("Opt-in real-media run requires a discoverable FFmpeg/FFprobe pair; no managed, configured, or PATH pair was found.");
            return;
        }
        string ffmpeg = tools.FfmpegPath;
        string ffprobe = tools.FfprobePath;

        await RunOnUiThreadAsync(async main =>
        {
            Config config = Field<Config>(main, "_config");
            config.FfmpegPath = ffmpeg;
            config.FfprobePath = ffprobe;
            config.StorageSavings.Enabled = false;
            config.DeleteSourceAfterCompression = false;
            config.SmartRecommendationsEnabled = false;
            config.UseHistoricalSizePredictionCalibration = false;
            config.VideoRestoration = new();

            Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Automatic";
            Field<NumericUpDown>(main, "nudAutoQuality").Value = 24;
            Field<QualityTargetSegmentSelector>(main, "qualityTargetSelector").SetSelectedTarget(QualityTarget.Balanced);
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
            Field<TextBox>(main, "txtTargetSize").Text = string.Empty;
            Field<CheckBox>(main, "chkDeleteSource").Checked = false;
            Field<ComboBox>(main, "cmbEncodeOutput").Text = outputDirectory;
            Field<System.Windows.Forms.Timer>(main, "_estSmartUiTimer").Stop();

            EstimateBackgroundService previousEstimates = Field<EstimateBackgroundService>(main, "_estimateService");
            previousEstimates.Dispose();
            var media = new MediaInfoService(AppPaths.InstallDirectory, ffprobe, persistentCacheEnabled: false);
            var encoding = new EncodingService(AppPaths.InstallDirectory, _ => { },
                message => _output.WriteLine(message), ffmpeg, ffprobe);
            var snapshots = new EncodeExecutionSnapshotBuilder(new SizeEstimateService(media), media.GetBitrateKbps);
            var estimateProvider = new QueueEmpiricalPreviewProvider(
                encoding,
                new FfprobeService(AppPaths.InstallDirectory, ffprobe),
                snapshots,
                ffmpeg + "|" + ffprobe);
            // Install the app's real preview provider through the same constructor seam
            // used by the deterministic queue-handoff tests.
            var estimates = new EstimateBackgroundService(media, estimateProvider,
                path => media.GetInfo(path));
            SetField(main, "_estimateService", estimates);
            SetField(main, "_mediaInfoService", media);
            SetField(main, "_encodingService", encoding);
            SetField(main, "_encodeExecutionSnapshotBuilder", snapshots);

            EncodeExecutionOrchestrator originalOrchestrator = Field<EncodeExecutionOrchestrator>(main, "_encodeExecutionOrchestrator");
            var orchestrator = new EncodeExecutionOrchestrator(
                encoding,
                Field<NvencQualityModePredictionShadowService>(originalOrchestrator, "_predictionShadowService"),
                Field<EncodingStatisticsService>(main, "_encodingStatisticsService"));
            SetField(main, "_encodeExecutionOrchestrator", orchestrator);
            SetField(main, "_encodeExecutionLifecycleService", new EncodeExecutionLifecycleService(
                orchestrator,
                () => Field<HistoryService>(main, "_historyService")));

            bool added = (bool)Invoke(main, "AddEncodeItemIfNotPresent", sourcePath, false, true)!;
            Assert.True(added, "MainForm must admit the approved source as one queue item.");
            ConcurrentDictionary<string, DataGridViewRow> rowsByPath = Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath");
            DataGridViewRow row = rowsByPath[sourcePath];
            object meta = row.Tag ?? throw new InvalidOperationException("MainForm did not attach queue metadata to the admitted row.");
            SetField(meta, "DurationSec", 0d);
            long originalQueueRevision = Field<long>(meta, "EstimateAuthorityRevision");
            DateTime queueAcceptedUtc = Field<DateTime>(meta, "QueueAcceptedUtc");
            DataGridViewRow[] initiallyEligible = ((IEnumerable<DataGridViewRow>)Invoke(main,
                "GetEligibleEncodeRowsInExecutionOrder")!).ToArray();
            Assert.Same(row, Assert.Single(initiallyEligible));

            DateTime startedUtc = DateTime.UtcNow;
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            var queueRun = (Task<MainForm.EncodeQueueStartOutcome>)Invoke(main, "StartEncodeAsync", (bool?)true)!;
            MainForm.EncodeQueueStartOutcome queueOutcome = await queueRun.WaitAsync(TimeSpan.FromMinutes(40));
            elapsed.Stop();
            Assert.Equal(MainForm.EncodeQueueStartOutcome.Completed, queueOutcome);

            PreparedProductionPlan prepared = Assert.IsType<PreparedProductionPlan>(
                Field<PreparedProductionPlan?>(meta, "PreparedProductionPlan"));
            AutomaticTargetPlan automaticTarget = Assert.IsType<AutomaticTargetPlan>(prepared.Plan.AutomaticTarget);
            Assert.NotNull(prepared.Estimate);
            Assert.Equal(AutomaticTargetDisposition.Ready, automaticTarget.Disposition);
            Assert.Equal(QualityTarget.Balanced, automaticTarget.QualityTarget);
            Assert.Equal(prepared.Identity.PlanFingerprint, prepared.Estimate!.Plan.PlanFingerprint);
            Assert.Equal(prepared.Identity.PlanFingerprint, prepared.Snapshot.ProductionPlan?.PlanFingerprint);
            Assert.Equal(prepared.QueueItemId, Field<Guid>(meta, "QueueItemId"));
            long claimedQueueRevision = Field<long>(meta, "EstimateAuthorityRevision");
            Assert.Equal(originalQueueRevision + 1, prepared.EstimateAuthorityRevision);
            Assert.Equal(prepared.EstimateAuthorityRevision + 1, claimedQueueRevision);
            Assert.False(config.DeleteSourceAfterCompression);
            Assert.False(config.StorageSavings.Enabled);
            Assert.True(Field<double>(meta, "DurationSec") > 0, "MainForm production preparation must acquire source duration metadata.");

            string inspectorLog = (string)meta.GetType().GetMethod("GetInspectorLogTail")!.Invoke(meta, null)!;
            string validationLine = Assert.Single(inspectorLog.Split(Environment.NewLine), line =>
                line.StartsWith("[PreparedPlanValidation] ", StringComparison.Ordinal));
            Assert.Contains("FailedPredicate=none", validationLine);
            Assert.Contains("FfmpegLaunched=False", validationLine);
            string productionPlanLine = Assert.Single(inspectorLog.Split(Environment.NewLine), line =>
                line.StartsWith("[ProductionPlan] ", StringComparison.Ordinal));
            string dispatchedFingerprint = productionPlanLine.Split("Fingerprint=", 2)[1].Split(';', 2)[0];
            Assert.Equal(prepared.Identity.PlanFingerprint, dispatchedFingerprint);
            Assert.Equal(dispatchedFingerprint, Field<EncodingPlan?>(meta, "IntelligencePlan")?.ProductionPlan?.PlanFingerprint);
            Assert.Equal(EncodingTerminalResult.Completed, Field<EncodingExecutionOutcome?>(meta, "IntelligenceOutcome")?.TerminalResult);
            string ffmpegArgumentsLine = Assert.Single(inspectorLog.Split(Environment.NewLine), line =>
                line.StartsWith("[MediaFlux] FFmpeg arguments: ", StringComparison.Ordinal));
            string ffmpegArguments = ffmpegArgumentsLine["[MediaFlux] FFmpeg arguments: ".Length..];
            Assert.Contains($"-c:v {prepared.Snapshot.Encoder.FfmpegCodec}", ffmpegArguments, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"-b:v {automaticTarget.TargetVideoBitrateKbps.GetValueOrDefault().ToString("F0", CultureInfo.InvariantCulture)}k", ffmpegArguments);
            Assert.Contains($"-maxrate {automaticTarget.MaximumVideoBitrateKbps.GetValueOrDefault().ToString("F0", CultureInfo.InvariantCulture)}k", ffmpegArguments);
            Assert.Contains($"-bufsize {automaticTarget.VideoBufferSizeKbps.GetValueOrDefault().ToString("F0", CultureInfo.InvariantCulture)}k", ffmpegArguments);
            Assert.Contains("-rc vbr", ffmpegArguments);
            Assert.Contains($"-preset {prepared.Snapshot.EncoderPreset}", ffmpegArguments);
            Assert.DoesNotContain("-cq ", ffmpegArguments);
            Assert.DoesNotContain("-crf ", ffmpegArguments);
            string expectedVideoMap = prepared.Snapshot.Input.HasExplicitStreamSelection
                ? $"-map 0:{prepared.Snapshot.Input.VideoStreamIndexes.First()}"
                : "-map 0:v:0";
            Assert.Contains(expectedVideoMap, ffmpegArguments);
            foreach (int audioStreamIndex in prepared.Snapshot.Input.AudioStreamIndexes)
                Assert.Contains($"-map 0:{audioStreamIndex}", ffmpegArguments);

            string[] outputs = Directory.EnumerateFiles(outputDirectory).ToArray();
            string outputPath = Assert.Single(outputs);
            long sourceBytes = new FileInfo(sourcePath).Length;
            long actualBytes = new FileInfo(outputPath).Length;
            Assert.True(actualBytes > 0);
            string sourceHashAfter = HashFile(sourcePath);
            Assert.Equal(sourceHashBefore, sourceHashAfter);
            string evidencePath = Path.Combine(outputDirectory, "prepared-plan-queue-handoff-evidence.json");
            var evidence = new
            {
                QueueScheduler = "MainForm.StartEncodeAsync -> EncodeQueueRunner.RunAsync",
                QueueRowsAdmitted = 1,
                QueueRowsDispatched = 1,
                QueueItemId = prepared.QueueItemId,
                OriginalQueueRevision = originalQueueRevision,
                PreparedPlanRevision = prepared.EstimateAuthorityRevision,
                ClaimedQueueRevision = claimedQueueRevision,
                ClaimCount = claimedQueueRevision - prepared.EstimateAuthorityRevision,
                QueueAcceptedUtc = queueAcceptedUtc,
                StartedUtc = startedUtc,
                QueueRunOutcome = queueOutcome.ToString(),
                SourceBytes = sourceBytes,
                SourceSha256Before = sourceHashBefore,
                SourceSha256After = sourceHashAfter,
                EstimatedBytes = prepared.Estimate.ProjectedBytes,
                EstimatedSavingsPercent = 100d * (sourceBytes - prepared.Estimate.ProjectedBytes) / sourceBytes,
                ActualBytes = actualBytes,
                ActualSavingsPercent = 100d * (sourceBytes - actualBytes) / sourceBytes,
                EstimationErrorBytes = prepared.Estimate.ProjectedBytes - actualBytes,
                EstimationErrorPercentOfActual = 100d * (prepared.Estimate.ProjectedBytes - actualBytes) / actualBytes,
                Encoder = prepared.Snapshot.Encoder.EncoderId + " / " + prepared.Snapshot.Codec,
                SelectedProfile = automaticTarget.QualityTarget.ToString(),
                RateControl = "target-bitrate VBR",
                TargetVideoBitrateKbps = automaticTarget.TargetVideoBitrateKbps,
                MaximumVideoBitrateKbps = automaticTarget.MaximumVideoBitrateKbps,
                VideoBufferSizeKbps = automaticTarget.VideoBufferSizeKbps,
                StreamMapMode = prepared.Identity.Inputs.StreamMapMode,
                SelectedVideoStreams = prepared.Snapshot.Input.VideoStreamIndexes,
                SelectedAudioStreams = prepared.Snapshot.Input.AudioStreamIndexes,
                PreparedPlanId = prepared.Plan.PlanId,
                Preset = prepared.Snapshot.EncoderPreset,
                BitDepth = prepared.Snapshot.TenBit ? 10 : 8,
                EstimateFingerprint = prepared.Estimate.Plan.PlanFingerprint,
                PreparedPlanFingerprint = prepared.Identity.PlanFingerprint,
                DispatchedFingerprint = dispatchedFingerprint,
                ExecutedFingerprint = dispatchedFingerprint,
                ActualFfmpegArguments = ffmpegArguments,
                EncodingDurationSeconds = elapsed.Elapsed.TotalSeconds,
                FinalStatus = Field<EncodingExecutionOutcome?>(meta, "IntelligenceOutcome")?.TerminalResult.ToString(),
                StorageSavingsPolicy = "Disabled for this isolated acceptance run; no policy rejection or retention decision.",
                SourceDeletion = "Disabled; original source remains in place.",
                PlanValidation = validationLine,
                TimingLogPath = Environment.GetEnvironmentVariable("MEDIAFLUX_TIMING_LOG"),
                RuntimeUserDataRoot = AppPaths.UserDataDirectory,
                OutputPath = outputPath
            };
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            _output.WriteLine("REAL_MEDIA_EVIDENCE=" + evidencePath);
            _output.WriteLine(JsonSerializer.Serialize(evidence));
        }, timeout: TimeSpan.FromMinutes(42));
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
                Assert.Equal(0, Field<long>(meta, "EstimateAuthorityRevision"));
                Assert.False((bool)Invoke(main, "IsQueueRowActivelyEncoding", row)!);
                cancellation.Cancel();
                await production;
                Assert.False((bool)Invoke(main, "IsQueueRowActivelyEncoding", row)!);
                Assert.Equal(0, Field<long>(meta, "EstimateAuthorityRevision"));
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

    private sealed class ProfileEstimateProbe(double durationSeconds, long videoBitrate) : IMediaProbeService
    {
        public int Calls;

        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new MediaProbeResult
            {
                Success = true,
                DurationSeconds = durationSeconds,
                Streams = [new() { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920,
                    Height = 1080, FrameRate = 30, BitRate = videoBitrate,
                    PixelFormat = "yuv420p", DurationSeconds = durationSeconds }]
            });
        }
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

    private sealed class StopBeforeEncodingExecutor : IEncodeRequestExecutor
    {
        public int Calls { get; private set; }
        public EncodingRequest? Request { get; private set; }

        public Task<EncodingService.EncodeResult> EncodeWithResultAsync(EncodingRequest request)
        {
            Calls++;
            Request = request;
            return Task.FromException<EncodingService.EncodeResult>(
                new OperationCanceledException("Test stopped after prepared-plan dispatch validation."));
        }
    }

    private static void ReleaseProductionOwnership(MainForm main, DataGridViewRow row) =>
        Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs").TryRemove(row, out _);

    private static void ClaimProductionOwnership(MainForm main, DataGridViewRow row, string sourcePath)
    {
        object meta = row.Tag ?? throw new InvalidOperationException("The queue row has no metadata.");
        SetField(meta, "EstimateAuthorityRevision", checked(Field<long>(meta, "EstimateAuthorityRevision") + 1));
        Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs")[row] = sourcePath;
    }

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
    public void LargeQueueStartsPreliminaryEstimatesWithoutExplicitAnalyzeQueue()
    {
        RunOnUiThread(main =>
        {
            var config = Field<Config>(main, "_config");
            config.LargeQueueThreshold = 1;
            config.AutoAnalyzeLargeQueues = false;
            var service = Field<EstimateBackgroundService>(main, "_estimateService");
            service.ResetAndCancel();
            var (row, _, path) = AddQueueRow(main, "MediaFlux.preview-large-queue");
            Invoke(main, "SetEncodeRowState", row, "Queued", "", "", "Queued for encoding.");
            File.WriteAllBytes(path, new byte[32]);
            try
            {
                Invoke(main, "RunEstimatePass", false);
                Assert.False(Field<bool>(main, "_estimatesDeferredForLargeQueue"));
                Assert.Equal(1, Field<int>(main, "_lastEstimateQueuedCount"));
                Assert.Equal("Pending…", row.Cells["colEstimatedSize"].Value);
                Assert.Equal("Queued", row.Cells["colStatus"].Value);
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
            Assert.Equal("Pending…", row.Cells["colEstimatedSize"].Value);
            Assert.DoesNotContain(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
            Assert.Null(Field<PlanBoundEstimate?>(meta, "BoundEstimate"));
            Assert.Null(Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview"));
            Assert.False(Field<bool>(meta, "AdaptiveSelectionSkipped"));
            Assert.False(Field<bool>(meta, "RecommendationPending"));
        });
    }

    [Fact]
    public async Task RapidQualityTargetChangesRefreshTheLatestEstimateGeneration()
    {
        await RunOnUiThreadAsync(async main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = true;
            config.VideoRestoration = new();
            config.SmartRecommendationsEnabled = false;
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Nvenc);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Automatic";
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
            Field<TextBox>(main, "txtTargetSize").Text = "";

            EstimateBackgroundService previousService = Field<EstimateBackgroundService>(main, "_estimateService");
            var probe = new ProfileEstimateProbe(100, 800_000);
            var samples = new AuthoritySamples();
            var provider = new QueueEmpiricalPreviewProvider(
                new EncodingService(Path.GetTempPath(), _ => { }), probe,
                new EncodeExecutionSnapshotBuilder(), "quality-target-generation-test",
                (_, _, _) => samples, _ => "profile-video", new PreviewSamplingCoordinator());
            var service = new EstimateBackgroundService(
                Field<MediaInfoService>(main, "_mediaInfoService"),
                provider,
                _ => new MediaInfoService.MediaInfo
                {
                    Width = 1920,
                    Height = 1080,
                    Fps = 30,
                    DurationSeconds = 3_600,
                    VideoCodec = "h264",
                    BitrateKbps = 6_000,
                    TotalBitrateKbps = 6_000
                });
            previousService.Dispose();
            SetField(main, "_estimateService", service);

            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.quality-target-generation");
            try
            {
                const long sourceBytes = 12L * 1024 * 1024;
                using (var source = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.SetLength(sourceBytes);

                QualityTargetSegmentSelector target = Field<QualityTargetSegmentSelector>(main, "qualityTargetSelector");
                int oldGeneration = service.CurrentGeneration;
                target.GetSegment(QualityTarget.MaximumQuality).PerformClick();
                target.GetSegment(QualityTarget.HighQuality).PerformClick();
                System.Windows.Forms.Timer refreshTimer = Field<System.Windows.Forms.Timer>(main, "_estimateRefreshTimer");
                Assert.True(refreshTimer.Enabled);

                Invoke(main, "EstimateRefreshTimer_Tick", null, EventArgs.Empty);

                Assert.Equal(oldGeneration + 1, service.CurrentGeneration);
                Assert.Equal(QualityTarget.HighQuality, Invoke(main, "GetSelectedQualityTarget"));

                object workItems = Field<object>(service, "_workItems");
                workItems.GetType().GetMethod("CompleteAdding")!.Invoke(workItems, null);
                await Task.WhenAll(Field<Task[]>(service, "_workers")).WaitAsync(TimeSpan.FromSeconds(10));

                ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult> results =
                    Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults");
                EstimateBackgroundService.SmartEstimateResult latest = Assert.Single(
                    results.ToArray(), item => !item.RecommendationOnly &&
                        item.EstimateState is QueueEstimateState.Ready or QueueEstimateState.Review or
                            QueueEstimateState.Unavailable or QueueEstimateState.Failed);
                Assert.Equal(service.CurrentGeneration, latest.Generation);
                Assert.Equal(Field<Guid>(meta, "QueueItemId"), latest.QueueItemId);
                Assert.Equal(QualityTarget.HighQuality, latest.PreparedProductionPlan?.Plan.AutomaticTarget?.QualityTarget);
                Assert.True(latest.EstimatedMb > 0);
                Assert.Equal(EstimateAuthorityLevel.Preliminary, latest.BoundEstimate?.Authority);
                Assert.Equal(0, samples.Calls);
                Assert.Equal(0, service.EmpiricalJobsLaunched);
                Assert.Equal(0, service.EmpiricalSamplesLaunched);
                Assert.Equal(1, probe.Calls);
                Assert.Equal(0L, Field<long>(meta, "V2EstimateGeneration"));

                results.Enqueue(new EstimateBackgroundService.SmartEstimateResult(
                    oldGeneration, latest.QueueItemId, path, 12, 999,
                    3_600, "1920x1080", "h264", 30, false, null, null,
                    "Stale prior-target estimate.", 0, 0));

                Invoke(main, "ApplySmartEstimateResultsBatch");

                Assert.Contains(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
                Assert.DoesNotContain("999", Convert.ToString(row.Cells["colEstimatedSize"].Value));
                Assert.Null(Field<EncodingQualityResolution?>(meta, "QualityPreview"));
                Assert.DoesNotContain("999", Convert.ToString(row.Cells["colEstimatedSize"].Value));
                Assert.False(service.TryDequeueSmart(out _));
            }
            finally
            {
                service.Dispose();
                if (File.Exists(path))
                    File.Delete(path);
            }
        });
    }

    [Fact]
    public async Task AutomaticQualityTargetsReachTheActualEstimateAndManualTargetOverridesThem()
    {
        var automaticRows = new List<string>();
        var automaticPresentations = new List<MainForm.QueueEstimateCellPresentation>();
        var manualRows = new List<string>();
        await RunOnUiThreadAsync(async main =>
        {
            Config config = Field<Config>(main, "_config");
            config.StorageSavings.Enabled = false;
            config.VideoRestoration = new();
            config.SmartRecommendationsEnabled = false;
            Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx265);
            Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.Hevc);
            ComboBox encoderPreset = Field<ComboBox>(main, "comboEncoderPreset");
            encoderPreset.SelectedItem = "slow";
            Assert.Equal("slow", encoderPreset.Text);
            Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Automatic";
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
            Field<TextBox>(main, "txtTargetSize").Text = "";

            EstimateBackgroundService previousService = Field<EstimateBackgroundService>(main, "_estimateService");
            var probe = new ProfileEstimateProbe(100, 800_000);
            var samples = new AuthoritySamples();
            var provider = new QueueEmpiricalPreviewProvider(
                new EncodingService(Path.GetTempPath(), _ => { }), probe,
                new EncodeExecutionSnapshotBuilder(), "quality-target-estimate-test",
                (_, _, _) => samples, _ => "profile-video", new PreviewSamplingCoordinator());
            var service = new EstimateBackgroundService(
                Field<MediaInfoService>(main, "_mediaInfoService"),
                provider,
                _ => new MediaInfoService.MediaInfo
                {
                    Width = 1920,
                    Height = 1080,
                    Fps = 30,
                    DurationSeconds = 16.777216,
                    VideoCodec = "h264",
                    BitrateKbps = 6_000,
                    TotalBitrateKbps = 6_000
                });
            previousService.Dispose();
            SetField(main, "_estimateService", service);

            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.automatic-quality-estimate");
            try
            {
            const long sourceBytes = 12L * 1024 * 1024;
            using (var source = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                source.SetLength(sourceBytes);

            QualityTargetSegmentSelector selector = Field<QualityTargetSegmentSelector>(main, "qualityTargetSelector");
            selector.SetSelectedTarget(QualityTarget.Balanced);
            int mainHandlerCount = QualityTargetChangedHandlerCount(selector);
            Assert.Equal(1, mainHandlerCount);
            var targets = new[]
            {
                QualityTarget.SmallerFile,
                QualityTarget.Efficient,
                QualityTarget.Balanced,
                QualityTarget.HighQuality,
                QualityTarget.MaximumQuality
            };
            var targetBitrates = new List<double>();
            string configPath = Field<string>(main, "_configPath");
            System.Windows.Forms.Timer? refreshTimer = null;

            foreach (QualityTarget target in targets)
            {
                int previousGeneration = service.CurrentGeneration;
                selector.GetSegment(target).PerformClick();
                refreshTimer ??= Field<System.Windows.Forms.Timer>(main, "_estimateRefreshTimer");
                Assert.True(refreshTimer.Enabled);
                Assert.Equal(target, selector.SelectedTarget);
                Assert.Equal(target.ToString(), Config.Load(configPath).LastQualityTarget);
                Invoke(main, "EstimateRefreshTimer_Tick", null, EventArgs.Empty);
                Assert.Equal(previousGeneration + 1, service.CurrentGeneration);

                object workItems = Field<object>(service, "_workItems");
                workItems.GetType().GetMethod("CompleteAdding")!.Invoke(workItems, null);
                await Task.WhenAll(Field<Task[]>(service, "_workers")).WaitAsync(TimeSpan.FromSeconds(10));

                ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult> results =
                    Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults");
                EstimateBackgroundService.SmartEstimateResult result = Assert.Single(
                    results.ToArray(), item => !item.RecommendationOnly &&
                        item.EstimateState is QueueEstimateState.Ready or QueueEstimateState.Review or
                            QueueEstimateState.Unavailable or QueueEstimateState.Failed);
                Assert.Equal(service.CurrentGeneration, result.Generation);
                AutomaticTargetPlan targetPlan = Assert.IsType<AutomaticTargetPlan>(
                    result.PreparedProductionPlan?.Plan.AutomaticTarget);
                Assert.Equal(target, targetPlan.QualityTarget);
                Assert.True(targetPlan.IsAvailable, targetPlan.Reason);
                Assert.True(result.EstimatedMb > 0);
                Assert.Equal("AutomaticTargetProjection", result.BoundEstimate?.Method);
                Assert.Equal(EstimateAuthorityLevel.Preliminary, result.BoundEstimate?.Authority);
                Assert.Equal(0, service.EmpiricalJobsLaunched);
                Assert.Equal(0, service.EmpiricalSamplesLaunched);
                Assert.Equal(0, samples.Calls);
                Assert.Equal(0L, Field<long>(meta, "V2EstimateGeneration"));
                targetBitrates.Add(targetPlan.TargetVideoBitrateKbps!.Value);

                Invoke(main, "ApplySmartEstimateResultsBatch");
                Assert.Contains(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
                Assert.DoesNotContain("~", Convert.ToString(row.Cells["colEstimatedSize"].Value));
                Assert.Null(Field<EncodingQualityResolution?>(meta, "QualityPreview"));
                Assert.Equal(mainHandlerCount, QualityTargetChangedHandlerCount(selector));
                MainForm.QueueEstimateCellPresentation presentation = MainForm.FormatQueueEstimateCellPresentation(
                    12d,
                    result.EstimatedMb,
                    Convert.ToString(row.Cells["colEstimatedSize"].Value),
                    row.Cells["colEstimatedSize"].ToolTipText);
                Assert.True(presentation.HasSavingsPercentage);
                Assert.Equal(presentation.SavingsPercentage,
                    (12d - result.EstimatedMb) / 12d * 100d, 10);
                Assert.Equal(
                    MainForm.GetQueueSavingsColorCategory(presentation.SavingsPercentage),
                    presentation.SavingsColorCategory);
                Assert.Contains("Projected Output:", presentation.ToolTipText);
                Assert.Contains("Actual Output:", presentation.ToolTipText);
                automaticPresentations.Add(presentation);
                automaticRows.Add(
                    $"{target}|VBR {targetPlan.TargetVideoBitrateKbps:0} kbps|{result.EstimatedMb * 1024d * 1024d:0.##} bytes|" +
                    $"{presentation.FullText}|{((12d - result.EstimatedMb) / 12d * 100d):0.###}%|{result.EstimateDiagnostic}");
            }

            Assert.Equal(5, targetBitrates.Distinct().Count());
            Assert.True(targetBitrates.Zip(targetBitrates.Skip(1)).All(pair => pair.First < pair.Second),
                "Smaller-to-Maximum quality targets should resolve to progressively higher target bitrates.");
            Assert.Equal(5, automaticPresentations.Count);
            Assert.True(automaticPresentations.Select(item => item.FullText).Distinct().Count() > 1,
                "Each selected profile must publish a corresponding output-size and savings presentation.");
            Assert.True(automaticPresentations.Select(item => item.ToolTipText).Distinct().Count() > 1,
                "Each selected profile must update the target-plan tooltip.");
            Assert.All(automaticPresentations, item =>
            {
                Assert.True(item.HasNumericOutput);
                Assert.True(item.HasSavingsPercentage);
                Assert.Equal(Math.Clamp(item.SavingsPercentage / 100d, 0d, 1d), item.SavingsFraction, 10);
                Assert.DoesNotContain("~", item.FullText);
            });
            Assert.Equal(1, probe.Calls);

            // The selected preference survives a Manual round trip without adding
            // selector subscriptions, then applies again when Automatic is restored.
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual";
            Assert.Equal(QualityTarget.MaximumQuality, selector.SelectedTarget);
            Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Automatic";
            Assert.Equal(QualityTarget.MaximumQuality, selector.SelectedTarget);
            Assert.Equal(mainHandlerCount, QualityTargetChangedHandlerCount(selector));
            refreshTimer!.Stop();

            // With Auto target off and an explicit target size, the size target
            // supersedes the retained quality preference in the estimate request.
            Field<CheckBox>(main, "chkAutoTargetSize").Checked = false;
            Field<TextBox>(main, "txtTargetSize").Text = "25";
            Assert.False(selector.Enabled);
            foreach (QualityTarget target in targets)
            {
                int previousGeneration = service.CurrentGeneration;
                selector.SetSelectedTarget(target);
                Invoke(main, "EstimateRefreshTimer_Tick", null, EventArgs.Empty);
                Assert.Equal(previousGeneration + 1, service.CurrentGeneration);

                object workItems = Field<object>(service, "_workItems");
                workItems.GetType().GetMethod("CompleteAdding")!.Invoke(workItems, null);
                await Task.WhenAll(Field<Task[]>(service, "_workers")).WaitAsync(TimeSpan.FromSeconds(10));
                ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult> results =
                    Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults");
                EstimateBackgroundService.SmartEstimateResult result = Assert.Single(
                    results.ToArray(), item => !item.RecommendationOnly &&
                        item.EstimateState == QueueEstimateState.Pending);
                Assert.True(result.QualityResolution is null || result.QualityResolution.IsSupersededByTargetSize);
                Assert.Equal(0, result.EstimatedMb);
                Assert.Equal(QueueEstimateState.Pending, result.EstimateState);
                Invoke(main, "ApplySmartEstimateResultsBatch");
                Assert.DoesNotContain(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
                Assert.Equal("Pending…", row.Cells["colEstimatedSize"].Value);
                Assert.Equal(mainHandlerCount, QualityTargetChangedHandlerCount(selector));
                manualRows.Add($"{target}|25 MiB target selected; measured plan estimate remains pending");
            }
            Assert.Equal(5, manualRows.Count);
            Assert.Equal(1, QualityTargetChangedHandlerCount(selector));
            }
            finally
            {
                service.Dispose();
                if (File.Exists(path))
                    File.Delete(path);
            }
        });

        _output.WriteLine("Automatic, Auto target ON (one real EstimateBackgroundService work item per target):");
        foreach (string line in automaticRows)
            _output.WriteLine(line);
        _output.WriteLine("Automatic, Auto target OFF with 25 MiB manual target:");
        foreach (string line in manualRows)
            _output.WriteLine(line);
    }

    [Theory]
    [InlineData("h264", true, true)]
    [InlineData("avc", true, true)]
    [InlineData("h264", false, true)]
    [InlineData("h264", true, false)]
    public void UnboundLegacyEstimateCannotReplacePendingPlanEstimate(
        string sourceCodec, bool storageEnabled, bool automatic)
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
                QualityIntent = automatic ? EncodingQualityIntent.Automatic(QualityTarget.Balanced) : EncodingQualityIntent.ManualNumeric(24)
            };
            var result = new EstimateBackgroundService.SmartEstimateResult(service.CurrentGeneration,
                Field<Guid>(meta, "QueueItemId"), path, 12, 6, 100, "1920x1080", sourceCodec, 24,
                false, null, Recommendation(SmartEncodeRecommendationKind.StrongCandidate), "preferred estimate", 128, 0,
                qualityResolution: EncodingPlanService.Create(context).Quality, sizeCalibration: calibration);
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(result);
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Invoke(main, "UpdateSizeTotals", true);
            Invoke(main, "RefreshQueueWorkspaceRow", row);
            Assert.Equal("Pending…", row.Cells["colEstimatedSize"].Value);
            Assert.Null(row.Cells["colEstimatedSize"].Tag);
            Assert.DoesNotContain(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
            Assert.Equal(0d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.Equal(QueueEstimateState.Pending, Field<QueueEstimateState>(meta, "EstimateState"));
            Assert.Null(Field<PlanBoundEstimate?>(meta, "BoundEstimate"));
            Assert.Null(Field<EmpiricalPreviewResult?>(meta, "EmpiricalPreview"));
            Assert.Null(Field<object?>(meta, "SizePredictionCalibration"));
            Assert.False(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
            Assert.False(Field<bool>(meta, "RecommendationPending"));
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

            Assert.Equal("Unavailable", row.Cells["colEstimatedSize"].Value);
            Assert.DoesNotContain(path, Field<Dictionary<string, double>>(main, "_estimatedSizeMap").Keys);
            Assert.Equal(0d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.False(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
            Assert.Equal("Estimated output is not available yet.", row.Cells["colEstimatedSize"].ToolTipText);
            Assert.False(Field<bool>(meta, "RecommendationPending"));
            Assert.Null(Field<SmartEncodeRecommendation?>(meta, "EncodeRecommendation"));
            Assert.Equal("—", Invoke(main, "GetQueueWorkspaceRecommendationText", row));
        });
    }

    [Fact]
    public void UnboundEstimateCannotReplaceQueueTotal()
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

            Assert.DoesNotContain(path, estimates.Keys);
            Assert.Equal(0d, Field<double>(main, "_queueTotalEstimatedMb"));
            Invoke(main, "UpdateSizeTotals", true);
            Assert.Equal("Waiting for estimates", Field<Label>(main, "_summaryNewSizeValue").Text);
            Assert.Equal(0, Field<int>(main, "_queueEstimatedFileCount"));
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
            row.Cells["colEstimatedSize"].Value = "6 MB";
            row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(12, 6);
            row.Cells["colEncodeRecommendation"].Value = "—";
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 6d;
            SetField(main, "_queueTotalEstimatedMb", 6d);
            EncodingPlan plan = BuildAdaptivePlan(AdaptiveSelectionDisposition.Selected, 26);

            Invoke(main, "ApplyAdaptivePlanSnapshotPresentation", row, meta, plan);
            Invoke(main, "RefreshQueueWorkspaceRow", row);

            Assert.Equal("6 MB", row.Cells["colEstimatedSize"].Value);
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
            Assert.Contains("Quality: Balanced", row.Cells["colEstimatedSize"].ToolTipText);
            Assert.DoesNotContain("CQ 26", row.Cells["colEstimatedSize"].ToolTipText);
        });
    }

    [Fact]
    public void AdaptiveSkipReplacesSizePresentationAndExcludesSkippedOutputFromQueueTotal()
    {
        RunOnUiThread(main =>
        {
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.adaptive-plan-skipped");
            SetField(meta, "AdaptiveQualitySelectionPending", true);
            row.Cells["colEstimatedSize"].Value = "6 MB";
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
        });
    }

    [Fact]
    public void StalePlanFingerprintImmediatelyClearsNumericEstimate()
    {
        RunOnUiThread(main =>
        {
            (DataGridViewRow row, object meta, string path) = AddQueueRow(main, "MediaFlux.stale-plan-estimate");
            EncodingPlan currentPlan = BuildAdaptivePlan(AdaptiveSelectionDisposition.Selected, 26);
            ProductionPlanIdentity identity = Assert.IsType<ProductionPlanIdentity>(currentPlan.ProductionPlan);
            var estimate = new PlanBoundEstimate(identity, 12, "metadata-arithmetic", 8_000_000,
                EstimateAuthorityLevel.Preliminary, Confidence: "Low");
            SetField(meta, "CurrentEstimatePlanIdentity", identity);
            SetField(meta, "BoundEstimate", estimate);
            SetField(meta, "PlanBindingRequired", true);
            row.Cells["colEstimatedSize"].Value = "8 MB";
            row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(12, 8);
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 8d;

            EncodingPlan changedPlan = new()
            {
                ProductionPlan = ProductionPlanFingerprintService.WithQuality(identity, 27),
                AdaptiveSelection = currentPlan.AdaptiveSelection
            };
            Invoke(main, "ApplyProductionPlanBoundEstimate", row, meta, changedPlan);

            Assert.Equal("Unavailable", row.Cells["colEstimatedSize"].Value);
            Assert.Null(row.Cells["colEstimatedSize"].Tag);
            Assert.Null(Field<PlanBoundEstimate?>(meta, "BoundEstimate"));
            Assert.False(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path));
        });
    }

    [Fact]
    public void ManualEstimateResultWithChangedQualityCannotRebindToTheOldPlan()
    {
        RunOnUiThread(main =>
        {
            var (_, meta, path) = AddQueueRow(main, "MediaFlux.manual-quality-binding");
            ProductionPlanFingerprintInput inputs = new(
                "hevc_nvenc", "hevc", "cq", "manual-numeric", 24, "p5", 8, "none", null,
                "preserve-source-timing", "{}", "copy-compatible", null, null, "copy-compatible",
                false, false, "mp4", "intelligent", "hardware", false, "keep-all", null);
            ProductionPlanIdentity expected = ProductionPlanFingerprintService.Create(inputs, [path]);
            ProductionPlanIdentity changedQuality = ProductionPlanFingerprintService.Create(
                inputs with { QualityValue = 25 }, [path]);
            SetField(meta, "CurrentEstimatePlanIdentity", expected);
            SetField(meta, "PlanBindingRequired", true);
            var bound = new PlanBoundEstimate(changedQuality, 1, "stale-manual-estimate", 500_000,
                EstimateAuthorityLevel.Preliminary);
            var result = new EstimateBackgroundService.SmartEstimateResult(
                1, Guid.NewGuid(), path, 1, 1, 60, null, "h264", 30, false, null, null,
                "stale manual result", 0, 0, planIdentity: changedQuality, boundEstimate: bound);

            Assert.False((bool)Invoke(main, "TryBindEstimateResult", meta, result)!);
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

    private static async Task RunOnUiThreadAsync(Func<MainForm, Task> action, TimeSpan? timeout = null)
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
        await completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60));
    }

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static int QualityTargetChangedHandlerCount(QualityTargetSegmentSelector selector)
    {
        FieldInfo field = typeof(QualityTargetSegmentSelector).GetField(
            "SelectedTargetChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(QualityTargetSegmentSelector).FullName, "SelectedTargetChanged");
        return ((Delegate?)field.GetValue(selector))?.GetInvocationList().Length ?? 0;
    }

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
