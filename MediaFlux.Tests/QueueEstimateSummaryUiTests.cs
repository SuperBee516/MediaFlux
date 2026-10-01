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
    public void AdaptivePendingEstimateRemovesPreferredPrecisionAndCalibrationFromRenderedQueue(
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
            Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 6;
            Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(service, "_smartResults").Enqueue(result);
            Invoke(main, "ApplySmartEstimateResultsBatch");
            if (pending)
            {
                Assert.Equal(AdaptiveStorageSavingsPolicy.PendingEstimate, row.Cells["colEstimatedSize"].Value);
                Assert.Null(row.Cells["colEstimatedSize"].Tag);
                Assert.False(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(path));
                Assert.Null(Field<object?>(meta, "SizePredictionCalibration"));
            }
            else
            {
                Assert.Contains("6 MB", row.Cells["colEstimatedSize"].Value!.ToString());
                Assert.Equal(6, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path]);
                Assert.Same(calibration, Field<object?>(meta, "SizePredictionCalibration"));
            }
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
