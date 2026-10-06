using System.Reflection;
using System.Windows.Forms;
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
