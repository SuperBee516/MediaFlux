using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class BoundedRetryProgressPresentationTests
{
    private static EncodingQualityResolution Quality(EncoderQualityMechanism mechanism, int value, EncodingQualityReasonCode reason) => new(
        EncodingQualityIntent.Automatic(QualityTarget.Balanced), value, mechanism,
        EncodingQualityAssessment.TypicalSource, false,
        [new EncodingQualityReason(reason, "test")]);

    [Fact]
    public void RetryPresentationUsesFrozenQualityAndKeepsGateOffAndSingleAttemptPlansQuiet()
    {
        var initial = new EncodingPlan { Quality = Quality(EncoderQualityMechanism.Cq, 25, EncodingQualityReasonCode.AdaptiveStorageSavingsSelection) };
        var retryCq = new EncodingPlan { Quality = Quality(EncoderQualityMechanism.Cq, 27, EncodingQualityReasonCode.AdaptiveStorageSavingsRetry) };
        var retryCrf = new EncodingPlan { Quality = Quality(EncoderQualityMechanism.Crf, 31, EncodingQualityReasonCode.AdaptiveStorageSavingsRetry) };

        Assert.False(BoundedRetryProgressPresentation.IsRetryPlan(null));
        Assert.False(BoundedRetryProgressPresentation.IsRetryPlan(initial));
        Assert.True(BoundedRetryProgressPresentation.IsRetryPlan(retryCq));
        Assert.Equal("Retrying at CQ27 — attempt 2 of 2", BoundedRetryProgressPresentation.RetryStatus(retryCq.Quality!));
        Assert.Equal("CQ27 executing (attempt 2 of 2)", BoundedRetryProgressPresentation.RetryQualityCell(retryCq.Quality!));
        Assert.Equal("Retrying at CRF31 — attempt 2 of 2", BoundedRetryProgressPresentation.RetryStatus(retryCrf.Quality!));
    }

    [Fact]
    public void RetryTransitionResetsSameQueueRowAndIgnoresDelayedAttemptOneProgress()
    {
        if (!OperatingSystem.IsWindows()) return;
        string source = Path.Combine(Path.GetTempPath(), $"retry-progress-{Guid.NewGuid():N}.mkv");
        string? configPath = null;
        MainForm? main = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                File.WriteAllBytes(source, [1, 2, 3]);
                main = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(main);
                main.Show();
                Application.DoEvents();

                Type formType = typeof(MainForm);
                DataGridView queue = Field<DataGridView>(formType, main, "dgvEncodeQueue");
                Method(formType, "AddEncodeItemIfNotPresent").Invoke(main, [source, false, false]);
                DataGridViewRow row = queue.Rows.Cast<DataGridViewRow>().Single();
                Method(formType, "BeginEncodeMetricsForRow").Invoke(main, [row]);
                row.Cells["colStatus"].Value = "Encoding";

                ApplyProgress(main, row, percent: 86, attempt: 1, frames: 860, seconds: 86);
                Assert.Equal("86%", row.Cells["colProgress"].Value?.ToString());
                Assert.NotEqual("--:--:--", row.Cells["colETA"].Value?.ToString());
                Dictionary<string, double> etaSpeedState = Field<Dictionary<string, double>>(formType, main, "_etaSpeedState");
                Assert.Contains(Path.GetFullPath(source), etaSpeedState.Keys);

                Method(formType, "ApplyBoundedRetryProgressToRow").Invoke(main,
                    [row, Quality(EncoderQualityMechanism.Cq, 27, EncodingQualityReasonCode.AdaptiveStorageSavingsRetry)]);

                Assert.Single(queue.Rows.Cast<DataGridViewRow>(), candidate => !candidate.IsNewRow);
                Assert.Same(row, Assert.Single(queue.Rows.Cast<DataGridViewRow>(), candidate => !candidate.IsNewRow));
                Assert.Equal("Retrying at CQ27 — attempt 2 of 2", row.Cells["colStatus"].Value?.ToString());
                Assert.Equal("CQ27 executing (attempt 2 of 2)", row.Cells["colEstimatedSize"].Value?.ToString());
                Assert.Equal("0%", row.Cells["colProgress"].Value?.ToString());
                Assert.Equal("--:--:--", row.Cells["colETA"].Value?.ToString());
                Assert.Equal(0, etaSpeedState[Path.GetFullPath(source)]);

                ApplyProgress(main, row, percent: 97, attempt: 1, frames: 970, seconds: 97);
                Assert.Equal("0%", row.Cells["colProgress"].Value?.ToString());
                ApplyProgress(main, row, percent: 12, attempt: 2, frames: 120, seconds: 12);
                Assert.Equal("12%", row.Cells["colProgress"].Value?.ToString());
                Assert.InRange(int.Parse(row.Cells["colProgress"].Value!.ToString()!.TrimEnd('%')), 0, 100);
                Assert.Equal("Retrying at CQ27 — attempt 2 of 2", row.Cells["colStatus"].Value?.ToString());
                Assert.NotEqual("--:--:--", row.Cells["colETA"].Value?.ToString());

                Method(formType, "SetEncodeRowState").Invoke(main, [row, "Verifying output", "99%", "00:00:00", "Final verification."]);
                ApplyProgress(main, row, percent: 30, attempt: 2, frames: 300, seconds: 30);
                Assert.Equal("Verifying output", row.Cells["colStatus"].Value?.ToString());
                Assert.Equal("99%", row.Cells["colProgress"].Value?.ToString());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
                try { File.Delete(source); } catch { }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Bounded retry progress UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void ApplyProgress(MainForm main, DataGridViewRow row, int percent, int attempt, long frames, int seconds)
    {
        var progress = new EncodingService.EncodeProgress(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(100),
            30, 2, 1000, percent, frames, EncodeProgressBasis.Timestamp, totalFrames: 1000, attempt: attempt);
        Method(typeof(MainForm), "ApplyStructuredEncodeProgress").Invoke(main, [row, progress]);
        Application.DoEvents();
    }

    private static T Field<T>(Type type, object instance, string name) where T : class =>
        (T)(type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
            ?? throw new MissingFieldException(name));

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new MissingMethodException(name);
}
