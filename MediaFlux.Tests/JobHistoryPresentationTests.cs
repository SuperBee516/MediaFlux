using MediaFlux.Services;
using Xunit;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using MediaFlux;
using MediaFlux.Models;

namespace MediaFlux.Tests;

public sealed class JobHistoryPresentationTests
{
    private static JobHistoryRecord Record(JobStatus status, string source = "C:\\media\\clip.mp4", DateTime? end = null, string notes = "") => new() { Status = status, Type = JobType.Encode, SourcePath = source, OutputPath = "C:\\out\\clip.mkv", EndUtc = end ?? new DateTime(2026, 9, 12, 16, 37, 0, DateTimeKind.Utc), Notes = notes };

    [Fact]
    public void CountsAndStatusFilterUseDisplayedSet()
    {
        var records = new[] { Record(JobStatus.Success), Record(JobStatus.Failed), Record(JobStatus.Canceled) };
        var filtered = JobHistoryPresentation.Filter(records, "", "Failed", "All", JobHistoryDateFilter.All, new DateTime(2026, 9, 12, 14, 0, 0));
        Assert.Single(filtered); Assert.Equal(JobStatus.Failed, filtered[0].Status); Assert.Equal((1, 0, 1, 0), JobHistoryPresentation.Counts(filtered));
    }

    [Fact]
    public void SearchMatchesFilenameAndResult()
    {
        var record = Record(JobStatus.Failed, source: "C:\\media\\holiday.mp4", notes: "FFmpeg timeout");
        Assert.Single(JobHistoryPresentation.Filter(new[] { record }, "holiday", "All", "All", JobHistoryDateFilter.All)); Assert.Single(JobHistoryPresentation.Filter(new[] { record }, "timeout", "All", "All", JobHistoryDateFilter.All));
    }

    [Fact]
    public void DateFilterAndPresentationKeepTimestampSortable()
    {
        TimeZoneInfo context = TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-4), "Test Eastern", "Test Eastern");
        DateTime now = new(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc); DateTime end = new(2026, 9, 11, 23, 41, 0, DateTimeKind.Utc); var record = Record(JobStatus.Success, end: end);
        Assert.Single(JobHistoryPresentation.Filter(new[] { record }, "", "All", "All", JobHistoryDateFilter.Last7Days, now, context)); Assert.Equal("Yesterday 7:41 PM", JobHistoryPresentation.FormatFinished(end, now, context)); Assert.Equal("clip.mp4", JobHistoryPresentation.FileNameOrUnavailable(record.SourcePath)); Assert.Equal(end, record.EndUtc);
    }

    [Fact]
    public void MissingOutputIsExplicit() => Assert.Equal("Unavailable", JobHistoryPresentation.FileNameOrUnavailable(""));

    [Fact]
    public void RecoveryOutcomeLabelsAndSummariesAreDistinctFromOrdinaryStates()
    {
        JobHistoryRecord recovered = Record(JobStatus.Success);
        recovered.TerminalResult = EncodingTerminalResult.CompletedAfterRecovery;
        JobHistoryRecord damaged = Record(JobStatus.Failed);
        damaged.TerminalResult = EncodingTerminalResult.SourceUnrecoverable;
        JobHistoryRecord salvaged = Record(JobStatus.Success);
        salvaged.TerminalResult = EncodingTerminalResult.CompletedAfterDegradedSalvage;

        Assert.Equal("Completed — Source recovered", JobHistoryPresentation.OutcomeLabel(recovered));
        Assert.Equal("Failed — Source damaged", JobHistoryPresentation.OutcomeLabel(damaged));
        Assert.Equal("Completed — Source salvaged with media loss", JobHistoryPresentation.OutcomeLabel(salvaged));
        Assert.Contains("Source recovered", JobHistoryPresentation.OutcomeSummary(recovered));
        Assert.Contains("Source media is damaged", JobHistoryPresentation.OutcomeSummary(damaged));
        Assert.Contains("media loss", JobHistoryPresentation.OutcomeSummary(salvaged), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Completed", JobHistoryPresentation.OutcomeLabel(Record(JobStatus.Success)));
        Assert.Equal("Failed", JobHistoryPresentation.OutcomeLabel(Record(JobStatus.Failed)));
    }

    [Theory]
    [InlineData(EncodingTerminalResult.Completed, JobStatus.Success, "Completed", "accepted")]
    [InlineData(EncodingTerminalResult.StoragePolicyRejected, JobStatus.Skipped, "Skipped — insufficient savings", "storage savings rejected")]
    [InlineData(EncodingTerminalResult.EncodeFailed, JobStatus.Failed, "Failed", "encode failed")]
    [InlineData(EncodingTerminalResult.ValidationFailed, JobStatus.Failed, "Failed", "validation failed")]
    [InlineData(EncodingTerminalResult.Canceled, JobStatus.Canceled, "Canceled", "canceled")]
    public void BoundedRetryHistoryShowsOneLogicalOutcomeAndBothAttemptDetails(
        EncodingTerminalResult terminal, JobStatus status, string label, string attemptTwoOutcome)
    {
        JobHistoryRecord record = Record(status);
        record.TerminalResult = terminal;
        record.ProductionEncodeCount = 2;
        record.AdaptiveStorageSavingsRetry = BoundedRetryEvidence.Trace(terminal);

        Assert.Equal(label, JobHistoryPresentation.OutcomeLabel(record));
        Assert.Contains("2 production encodes", JobHistoryPresentation.OutcomeSummary(record));
        string details = JobHistoryPresentation.RetryDetails(record);
        Assert.Contains("Production encode count: 2 of 2 maximum", details);
        Assert.Contains("Attempt 1: CQ25 — storage savings rejected", details);
        Assert.Contains($"Attempt 2: CQ27 — {attemptTwoOutcome}", details);
        Assert.Contains($"Final logical outcome: {label}", details);
        Assert.Single(new[] { record });
    }

    [Fact]
    public void LegacySingleAttemptAndGenericRecoveryHistoryHaveNoRetryNoise()
    {
        JobHistoryRecord legacy = Record(JobStatus.Success);
        JobHistoryRecord recovered = Record(JobStatus.Success);
        recovered.TerminalResult = EncodingTerminalResult.CompletedAfterRecovery;

        Assert.Equal("Completed", JobHistoryPresentation.OutcomeLabel(legacy));
        Assert.Equal("Completed successfully", JobHistoryPresentation.OutcomeSummary(legacy));
        Assert.Equal("", JobHistoryPresentation.RetryDetails(legacy));
        Assert.Contains("Source recovered", JobHistoryPresentation.OutcomeSummary(recovered));
        Assert.DoesNotContain("retry", JobHistoryPresentation.OutcomeSummary(recovered), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", JobHistoryPresentation.RetryDetails(recovered));
    }

    [Fact]
    public void RetryQualityHistoryUsesCrfTerminologyWhenTraceUsesCrf()
    {
        var crfTrace = BoundedRetryEvidence.Trace(qualityMechanism: EncoderQualityMechanism.Crf);
        JobHistoryRecord record = Record(JobStatus.Success);
        record.AdaptiveStorageSavingsRetry = crfTrace;
        record.ProductionEncodeCount = 2;

        string details = JobHistoryPresentation.RetryDetails(record);
        Assert.Contains("Initial adaptive quality: CRF25", details);
        Assert.Contains("Attempt 2: CRF27", details);
    }

    [Fact]
    public void ActiveRecoveryStatusUsesHumanReadableLabels()
    {
        Assert.Equal("Source corruption detected — analyzing…", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.SourceCorruptionDetected)));
        Assert.Equal("Attempting source recovery…", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.AttemptingSourceRecovery)));
        Assert.Equal("Validating recovered source…", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.ValidatingRecoveredSource)));
        Assert.Equal("Retrying encode with recovered source…", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.RetryingWithRecoveredSource)));
        Assert.Equal("Attempting degraded source salvage…", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.AttemptingDegradedSourceSalvage)));
        Assert.Equal("Validating salvaged media…", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.ValidatingSalvagedMedia)));
        Assert.DoesNotContain("SourceContainerRemux", JobHistoryPresentation.ActiveRecoveryStatus(new(EncodingRecoveryStatusKind.AttemptingSourceRecovery)));
    }

    [Fact]
    public void HistoryPersistsRecoveryTerminalResultAndLegacyRecordStillLoads()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxJobHistoryPresentation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "history.json");
            var history = new HistoryService(path);
            JobHistoryRecord damaged = Record(JobStatus.Failed);
            damaged.TerminalResult = EncodingTerminalResult.SourceUnrecoverable;
            history.Append(damaged);
            history.Append(Record(JobStatus.Success));

            JobHistoryRecord[] loaded = new HistoryService(path).LoadAll().ToArray();
            Assert.Contains(loaded, record => record.TerminalResult == EncodingTerminalResult.SourceUnrecoverable);
            Assert.Contains(loaded, record => record.TerminalResult == null && JobHistoryPresentation.OutcomeLabel(record) == "Completed");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void WindowBoundsRestoreClampsInvalidAndOffScreenValues()
    {
        Size minimum = new(980, 680); Rectangle area = new(0, 0, 1920, 1080);
        Rectangle invalid = JobHistoryPresentation.RestoreWindowBounds(-100000, -100000, 1, 1, minimum, new Size(1280, 820), new[] { area });
        Assert.Equal(new Size(980, 680), invalid.Size); Assert.True(area.Contains(invalid));
        Rectangle valid = JobHistoryPresentation.RestoreWindowBounds(200, 150, 1400, 800, minimum, new Size(1280, 820), new[] { area });
        Assert.Equal(new Rectangle(200, 150, 1400, 800), valid);
    }

    [Fact]
    public void JobHistoryFormConstructsAndShowsWithLegacyInvalidBounds()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null; bool visible = false; Rectangle bounds = Rectangle.Empty;
        var thread = new Thread(() =>
        {
            JobHistoryForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string root = Path.Combine(Path.GetTempPath(), "MediaFluxJobHistoryTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
                var config = new Config { JobHistoryWindowWidth = 1, JobHistoryWindowHeight = 1, JobHistoryWindowX = -100000, JobHistoryWindowY = -100000 };
                form = new JobHistoryForm(new HistoryService(Path.Combine(root, "history.json")), config, Path.Combine(root, "config.json")); form.Show(); Application.DoEvents(); visible = form.Visible; bounds = form.Bounds; form.Close();
            }
            catch (Exception ex) { failure = ex; }
            finally { if (form != null && !form.IsDisposed) form.Dispose(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Job History UI test timed out."); if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString()); Assert.True(visible); Assert.True(bounds.Width >= 980); Assert.True(bounds.Height >= 680);
    }
}
