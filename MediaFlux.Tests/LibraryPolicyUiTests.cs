using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Windows.Forms;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryPolicyUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-PolicyUiTests", Guid.NewGuid().ToString("N"));
    public LibraryPolicyUiTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PolicyTabHasNoSilentDefaultAndQueuesOnlySelectedCandidateIntent()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "ui.db"), Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
                catalog.Initialize();
                string media = SeedCatalog(catalog);
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new EmptyProbe(), new EmptyVisual());
                var queued = new TaskCompletionSource<IReadOnlyList<LibraryPolicyQueueItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var capabilities = new LibraryPolicyCapabilitySnapshot
                {
                    AvailableEncoderCodecs = new Dictionary<string, string>
                    {
                        [LibraryPolicyCapabilitySnapshot.Key(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc)] = "libx265"
                    },
                    TenBitEncoderIds = new HashSet<string> { VideoEncoderIds.Libx265 }
                };
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(
                    PolicyStore: new LibraryPolicyStore(Path.Combine(_root, "policies.json")), PolicyCapabilities: capabilities,
                    AddPolicyCandidatesToEncodeQueue: items => { queued.SetResult(items); return Task.CompletedTask; }));
                form.Show();
                PumpUntil(() => catalog.QueryPolicyFileFacts(0, 10).SingleOrDefault()?.ProbeStatus == LibraryProbeStatus.Succeeded);
                TabControl tabs = Field<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(tab => tab.Text == "Library Policies");
                ComboBox selector = Field<ComboBox>(form, "_policySelection");
                DataGridView grid = Field<DataGridView>(form, "_optimizationGrid");
                Assert.Equal(-1, selector.SelectedIndex);
                Assert.DoesNotContain(grid.Rows.Cast<DataGridViewRow>(), row => !row.IsNewRow);

                selector.SelectedIndex = 0;
                PumpUntil(() => grid.Rows.Cast<DataGridViewRow>().Any(row => !row.IsNewRow));
                Assert.Single(grid.Rows.Cast<DataGridViewRow>(), row => !row.IsNewRow);
                DataGridViewRow candidate = grid.Rows[0];
                LibraryPolicyEvaluationResult evaluation = Assert.IsType<LibraryPolicyEvaluationResult>(candidate.Tag);
                Assert.True(evaluation.State == LibraryPolicyComplianceState.OptimizationCandidate,
                    $"Expected candidate, got {evaluation.State}: {string.Join(" | ", evaluation.Reasons.Concat(evaluation.ReviewReasons))}");
                Assert.Equal(LibraryPolicySuggestedAction.Reencode, evaluation.SuggestedAction);
                grid.ClearSelection();
                candidate.Selected = true;
                grid.CurrentCell = candidate.Cells.Cast<DataGridViewCell>().First(cell => cell.Visible);
                Invoke(form, "AddOptimizationSelectionToQueue_Click", null, EventArgs.Empty);
                PumpUntil(() => queued.Task.IsCompleted);
                LibraryPolicyQueueItem item = Assert.Single(queued.Task.Result);
                Assert.Equal(media, item.FullPath);
                Assert.Equal(VideoCodecFamily.Hevc, item.ProposedCodec);
                Assert.Equal(VideoEncoderIds.Libx265, item.EncoderId);
                Assert.Equal(OutputContainerSelection.Auto, item.TargetContainer);
                form.Close();
                Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Policy UI smoke test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private string SeedCatalog(SqliteLibraryCatalog catalog)
    {
        string folder = Path.Combine(_root, "media"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "large-h264.mkv"); File.WriteAllBytes(path, new byte[32]);
        LibraryLocationRecord location = catalog.UpsertLocation(new LibraryLocationUpsert(folder));
        LibraryScanHandle scan = catalog.BeginScan(location.Id);
        LibraryInventoryMutation mutation = Assert.Single(catalog.UpsertInventoryBatchDetailed(scan,
            new[] { new LibraryInventoryEntry(path, Path.GetFileName(path), 12L * 1024 * 1024 * 1024, DateTime.UtcNow, DateTime.UtcNow) }, 1).Mutations);
        var probe = new MediaProbeResult
        {
            Success = true, FormatName = "matroska", DurationSeconds = 3600, BitRate = 30_000_000,
            Streams = new[]
            {
                new MediaProbeStreamInfo { CodecType = "video", CodecName = "h264", Profile = "High", Width = 1920, Height = 1080, FrameRate = 24, BitsPerRawSample = 8, FieldOrder = "progressive" },
                new MediaProbeStreamInfo { CodecType = "audio", CodecName = "aac", Channels = 2 }
            }
        };
        catalog.SaveMediaMetadata(LibraryMetadataMapper.Map(new LibraryEnrichmentRequest(mutation.FileId, path, "", 12L * 1024 * 1024 * 1024, DateTime.UtcNow),
            probe, 1, "policy-ui", DateTime.UtcNow, null));
        catalog.CompleteScan(scan, new LibraryScanCompletion(LibraryScanStatus.Completed, 1, 0, 1, 0, 0, 0));
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class EmptyProbe : ILibraryMetadataProbe
    {
        public string ToolVersion => "policy-ui";
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) => Task.FromResult(new MediaProbeResult
        {
            Success = true, FormatName = "matroska", DurationSeconds = 3600, BitRate = 30_000_000,
            Streams = new[]
            {
                new MediaProbeStreamInfo { CodecType = "video", CodecName = "h264", Profile = "High", Width = 1920, Height = 1080, FrameRate = 24, BitsPerRawSample = 8, FieldOrder = "progressive" },
                new MediaProbeStreamInfo { CodecType = "audio", CodecName = "aac", Channels = 2 }
            }
        });
    }
    private sealed class EmptyVisual : ILibraryVisualFingerprintExtractor
    {
        public string ToolVersion => "unused";
        public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate candidate, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());
    }
    private static T Field<T>(object instance, string name) => (T)(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) ?? throw new MissingFieldException(name));
    private static void Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, args);
    private static void PumpUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(); Application.DoEvents(); Thread.Sleep(10); }
    }
}
