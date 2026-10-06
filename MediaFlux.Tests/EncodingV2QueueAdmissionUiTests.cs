using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services.EncodingV2;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class EncodingV2QueueAdmissionUiTests
{
    [Fact]
    public void ProvisionalQueueRowIsNotEligibleUntilAdmissionChecksFinish()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                row.Cells["colName"].Value = "provisional.mp4";
                row.Cells["colStatus"].Value = "Preparing";
                object meta = Invoke(main, "EnsureRowMeta", row)!;
                SetMetaField(meta, "QueueAdmissionPending", true);
                SetField(main, "_suppressRowEvents", false);

                IEnumerable<DataGridViewRow> eligible = EligibleRows(main);
                Assert.DoesNotContain(row, eligible);

                SetMetaField(meta, "QueueAdmissionPending", false);
                Assert.Contains(row, EligibleRows(main));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue admission UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void CoordinatorResultProjectsNumericAdvisoryEstimateIntoQueueGrid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxV2QueueUi-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
                Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", path, false, false)!);
                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                DataGridViewRow row = queue.Rows.Cast<DataGridViewRow>()
                    .Single(candidate => candidate.Tag?.GetType().GetField("Path")?.GetValue(candidate.Tag)?.ToString() == path);
                object meta = row.Tag!;
                Guid queueItemId = (Guid)meta.GetType().GetField("QueueItemId")!.GetValue(meta)!;
                V2SourceFileIdentity identity = V2SourceFileIdentity.Capture(path);
                var facts = new V2FastEstimateSourceFacts
                {
                    SourcePath = path,
                    SourceSizeBytes = identity.SourceSizeBytes,
                    DurationSeconds = 30,
                    VideoCodec = "h264",
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 24,
                    VideoBitrateKbps = 7000
                };
                var coordinator = new V2EstimateRefreshCoordinator(new V2SourceFactsCache((_, _) =>
                    throw new InvalidOperationException("Supplied source facts must prevent a probe.")));
                var previous = Field<V2EstimateRefreshCoordinator?>(main, "_v2EstimateCoordinator");
                previous?.Dispose();
                SetField(main, "_v2EstimateCoordinator", coordinator);
                V2EstimateRefreshTicket ticket = coordinator.Request(queueItemId, identity,
                    new V2FastEstimateCommandRequest
                    {
                        SourcePath = path,
                        SourceFacts = facts,
                        Encoder = "libx264",
                        Codec = "h264",
                        Container = "mp4",
                        QualityMode = "crf",
                        QualityValue = 24,
                        Preset = "medium",
                        AudioMode = "copy"
                    });
                V2EstimateRefreshResult result = ticket.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                SetMetaField(meta, "V2SourceIdentity", identity);
                SetMetaField(meta, "V2EstimateGeneration", ticket.Generation);
                Invoke(main, "ApplyV2EstimateResultsBatch");

                Assert.True(result.Estimate!.EstimatedTotalBytes > 0);
                Assert.NotEqual("", row.Cells["colEstimatedSize"].Value?.ToString());
                Assert.Contains("V2 fast estimate", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.Ordinal);
                Assert.Contains("advisory", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(result.Estimate.EstimatedTotalBytes / (1024d * 1024d),
                    Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path], 5);
                Assert.Equal(0, coordinator.SourceProbeCount);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue estimate projection UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void FileSizePresetChangeRefreshesV2EstimateFromCachedFactsWithoutChangingManualQuality()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Exception? failure = null;
        MainForm? main = null;
        string? configPath = null;
        string? testRoot = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                Invoke(main, "SelectEncoderById", VideoEncoderIds.Libx264);
                Invoke(main, "RefreshVideoFormatItems", VideoCodecFamily.H264);
                Field<ComboBox>(main, "comboQualityMode").SelectedItem = "Manual • Legacy Numeric";
                Field<NumericUpDown>(main, "nudAutoQuality").Value = 24;
                Field<ComboBox>(main, "comboCompressionProfile").SelectedItem = "Medium Quality (Default)";
                Field<CheckBox>(main, "chkAutoTargetSize").Checked = true;
                Field<TextBox>(main, "txtTargetSize").Text = "";

                testRoot = Path.Combine(Path.GetTempPath(), "MediaFluxV2PresetRefresh-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(testRoot);
                string path = Path.Combine(testRoot, "source.mp4");
                File.WriteAllBytes(path, Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray());
                V2SourceFileIdentity identity = V2SourceFileIdentity.Capture(path);
                V2FastEstimateSourceFacts facts = new()
                {
                    SourcePath = path,
                    SourceSizeBytes = identity.SourceSizeBytes,
                    DurationSeconds = 30,
                    VideoCodec = "h264",
                    Width = 1920,
                    Height = 1080,
                    FrameRate = 24,
                    VideoBitrateKbps = 3000
                };

                int probeCount = 0;
                var coordinator = new V2EstimateRefreshCoordinator(new V2SourceFactsCache((sourcePath, _) =>
                {
                    Interlocked.Increment(ref probeCount);
                    return Task.FromResult(facts with { SourcePath = sourcePath });
                }));
                Field<V2EstimateRefreshCoordinator?>(main, "_v2EstimateCoordinator")?.Dispose();
                SetField(main, "_v2EstimateCoordinator", coordinator);

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow row;
                object meta;
                try
                {
                    row = queue.Rows[queue.Rows.Add()];
                    row.Tag = path;
                    row.Cells["colName"].Value = Path.GetFileName(path);
                    row.Cells["colStatus"].Value = "Queued";
                    meta = Invoke(main, "EnsureRowMeta", row)!;
                    SetMetaField(meta, "Path", path);
                    Field<System.Collections.Concurrent.ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
                }
                finally
                {
                    SetField(main, "_suppressRowEvents", false);
                }

                int productionQualityBefore = (int)Invoke(main, "GetDefaultQualityForSelection")!;
                Invoke(main, "SafeRefreshEstimates");
                WaitForEstimate(coordinator);
                Invoke(main, "ApplyV2EstimateResultsBatch");
                V2FastEstimateResult? initial = (V2FastEstimateResult?)meta.GetType()
                    .GetField("V2EstimateResult")!.GetValue(meta);
                Assert.NotNull(initial);
                Assert.Equal(24d, initial.QualityValue);
                double initialOutputMb = Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path];
                long initialGeneration = (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!;
                Assert.Equal(1, Volatile.Read(ref probeCount));

                ComboBox profile = Field<ComboBox>(main, "comboCompressionProfile");
                profile.SelectedItem = "Very High Quality (Largest File)";
                profile.SelectedItem = "Low Quality (Smaller File)";
                profile.SelectedItem = "Very Low Quality (Smallest File)";
                profile.SelectedItem = "High Quality";
                System.Windows.Forms.Timer refreshTimer = Field<System.Windows.Forms.Timer>(main, "_estimateRefreshTimer");
                Assert.True(refreshTimer.Enabled);
                Invoke(main, "EstimateRefreshTimer_Tick", null, EventArgs.Empty);

                WaitForEstimate(coordinator);
                Invoke(main, "ApplyV2EstimateResultsBatch");
                V2FastEstimateResult? refreshed = (V2FastEstimateResult?)meta.GetType()
                    .GetField("V2EstimateResult")!.GetValue(meta);
                Assert.NotNull(refreshed);
                Assert.True(refreshed.QualityValue < initial.QualityValue);
                Assert.True(refreshed.EstimatedTotalBytes > initial.EstimatedTotalBytes);
                long refreshedGeneration = (long)meta.GetType().GetField("V2EstimateGeneration")!.GetValue(meta)!;
                Assert.True(refreshedGeneration > initialGeneration);
                Assert.Equal(24, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                Assert.Equal(productionQualityBefore, (int)Invoke(main, "GetDefaultQualityForSelection")!);
                Assert.Equal(1, Volatile.Read(ref probeCount));
                Assert.Contains("Source facts: reused", row.Cells["colEstimatedSize"].ToolTipText, StringComparison.Ordinal);

                double refreshedOutputMb = refreshed.EstimatedTotalBytes / (1024d * 1024d);
                Assert.NotEqual(initialOutputMb, refreshedOutputMb);
                Assert.Equal(refreshedOutputMb, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path], 5);
                Assert.Matches(@"^\d+(\.\d+)? MB$", row.Cells["colEstimatedSize"].Value?.ToString() ?? "");
                Assert.Equal(refreshedOutputMb, Field<double>(main, "_queueTotalEstimatedMb"), 5);
                Assert.Equal(row.Cells["colEstimatedSize"].Value?.ToString(), Field<Label>(main, "_summaryNewSizeValue").Text);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (main != null)
                    WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                if (testRoot != null && Directory.Exists(testRoot))
                    Directory.Delete(testRoot, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "File-size estimate preset UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void WaitForEstimate(V2EstimateRefreshCoordinator coordinator)
    {
        Assert.True(SpinWait.SpinUntil(() => coordinator.PendingWorkCount == 0, TimeSpan.FromSeconds(10)),
            "V2 estimate did not complete within the bounded test window.");
    }

    private static IEnumerable<DataGridViewRow> EligibleRows(MainForm main) =>
        (IEnumerable<DataGridViewRow>)Invoke(main, "GetEligibleEncodeRowsInExecutionOrder")!;

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(target, args);

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetMetaField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)!.SetValue(target, value);

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

}
