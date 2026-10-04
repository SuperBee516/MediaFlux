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
                false, null, null, "preferred estimate", 128, 0,
                qualityResolution: EncodingPlanService.Create(context).Quality, sizeCalibration: calibration);
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(result);
            Invoke(main, "ApplySmartEstimateResultsBatch");
            Invoke(main, "UpdateSizeTotals", true);
            Invoke(main, "RefreshQueueWorkspaceRow", row);
            if (pending)
            {
                Assert.Contains("6 MB", row.Cells["colEstimatedSize"].Value!.ToString());
                Assert.IsType<Tuple<double, double>>(row.Cells["colEstimatedSize"].Tag);
                Assert.Equal(6d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
                Assert.Equal(6d, Field<double>(main, "_queueTotalEstimatedMb"));
                Assert.Equal("6 MB", Field<Label>(main, "_summaryNewSizeValue").Text);
                Assert.Equal(1, Field<int>(main, "_queueEstimatedFileCount"));
                Assert.Equal(6d, Field<double>(main, "_queueTotalSavingsEstimateOutputMb"));
                Assert.Equal(1, Field<int>(main, "_queueSavingsEstimateFileCount"));
                Assert.NotEqual("--", Field<Label>(main, "_summaryTotalEstimatedSavedValue").Text);
                Assert.Contains("12 MB → 6 MB", row.Cells["colSourceEstimate"].Value!.ToString());
                Assert.DoesNotContain(AdaptiveStorageSavingsPolicy.PendingEstimate, row.Cells["colSourceEstimate"].Value!.ToString());
                Assert.Contains("Provisional output-size estimate", row.Cells["colEstimatedSize"].ToolTipText);
                Assert.True(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
                Assert.Null(Field<object?>(meta, "SizePredictionCalibration"));
                Assert.Contains("Provisional output-size estimate", Field<string>(meta, "EstimateDiagnostic"));
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
            var result = new EstimateBackgroundService.SmartEstimateResult(service.CurrentGeneration,
                Field<Guid>(meta, "QueueItemId"), path, 12, 0, 100, "1920x1080", "h264", 24,
                false, "Metadata unavailable", null, "Required media metadata could not be determined.", 128, 0,
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
            row.Cells["colEstimatedSize"].Value = "6 MB  (-50.0%)";
            row.Cells["colEstimatedSize"].Tag = new Tuple<double, double>(12, 6);
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 6d;
            SetField(main, "_queueTotalEstimatedMb", 6d);
            EncodingPlan plan = BuildAdaptivePlan(AdaptiveSelectionDisposition.Selected, 26);

            Invoke(main, "ApplyAdaptivePlanSnapshotPresentation", row, meta, plan);
            Invoke(main, "RefreshQueueWorkspaceRow", row);

            Assert.Equal("6 MB  (-50.0%)", row.Cells["colEstimatedSize"].Value);
            Assert.Equal(6d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
            Assert.Equal(6d, Field<double>(main, "_queueTotalEstimatedMb"));
            Assert.False(Field<bool>(meta, "AdaptiveQualitySelectionPending"));
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
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        return method.Invoke(main, args);
    }
}
