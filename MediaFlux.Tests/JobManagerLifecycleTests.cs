using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class JobManagerLifecycleTests
{
    [Fact]
    public void ReviewCancellationKeepsSavedJobReadyAndDoesNotReportCompleted()
    {
        (EncodeJobStatus status, string result) = MainForm.ResolveSavedJobRunResult(
            MainForm.EncodeQueueStartOutcome.ReviewCancelled,
            failedCount: 0,
            skippedCount: 0);

        Assert.Equal(EncodeJobStatus.Ready, status);
        Assert.Equal("Canceled during pre-encode review; no encode run started.", result);
        Assert.NotEqual("Completed.", result);
    }

    [Fact]
    public void CompletedQueueWithSkippedFilesRemainsCompleted()
    {
        (EncodeJobStatus status, string result) = MainForm.ResolveSavedJobRunResult(
            MainForm.EncodeQueueStartOutcome.Completed,
            failedCount: 0,
            skippedCount: 1);

        Assert.Equal(EncodeJobStatus.Completed, status);
        Assert.Equal("Completed with 1 file(s) skipped — insufficient savings.", result);
    }

    [Fact]
    public void ActiveCancellationAndEncodeFailureKeepDistinctSavedJobResults()
    {
        Assert.Equal(
            (EncodeJobStatus.Failed, "Stopped or failed."),
            MainForm.ResolveSavedJobRunResult(MainForm.EncodeQueueStartOutcome.Canceled, 0, 0));
        Assert.Equal(
            (EncodeJobStatus.CompletedWithErrors, "Completed with 1 failed file(s)."),
            MainForm.ResolveSavedJobRunResult(MainForm.EncodeQueueStartOutcome.Completed, 1, 0));
    }

    [Fact]
    public void AdaptivePreProductionSkipUsesNotRunAndFfmpegFailureKeepsFfmpegFailed()
    {
        Assert.Equal("NotRun", MainForm.ResolveFailureHistoryFinalizationOutcome(
            null, assignmentValidationFailed: false, researchEvidenceFailed: false,
            adaptivePolicySkipped: true, isCanceled: false));
        Assert.Equal("FfmpegFailed", MainForm.ResolveFailureHistoryFinalizationOutcome(
            null, assignmentValidationFailed: false, researchEvidenceFailed: false,
            adaptivePolicySkipped: false, isCanceled: false));
    }

    [Fact]
    public void JobManagerRefreshesPersistedStatusAfterAsyncActionCompletes()
    {
        if (!OperatingSystem.IsWindows()) return;

        string path = Path.Combine(Path.GetTempPath(), $"MediaFlux.JobManager.{Guid.NewGuid():N}.json");
        var service = new EncodeJobService(path);
        var job = new EncodeJob { Name = "Lifecycle test", Status = EncodeJobStatus.Running, LastResult = "Running." };
        service.Save([job]);
        var actionGate = new TaskCompletionSource<bool>();
        Exception? failure = null;
        JobManagerForm? form = null;

        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                form = new JobManagerForm(
                    () => service.LoadStrict(),
                    async (jobId, action) =>
                    {
                        Assert.Equal(job.Id, jobId);
                        Assert.Equal("Run Now", action);
                        await actionGate.Task;
                        EncodeJob persisted = Assert.Single(service.LoadStrict());
                        persisted.Status = EncodeJobStatus.Completed;
                        persisted.LastResult = "Completed.";
                        service.Save([persisted]);
                    });
                form.Shown += async (_, __) =>
                {
                    try
                    {
                        var grid = (DataGridView)(typeof(JobManagerForm)
                            .GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(form)!);
                        grid.CurrentCell = grid.Rows[0].Cells[0];

                        Task action = form.InvokeActionAsync("Run Now");
                        Assert.False(action.IsCompleted);
                        Assert.Equal("Running", grid.Rows[0].Cells[5].Value?.ToString());
                        Assert.Equal(EncodeJobStatus.Running, Assert.Single(service.LoadStrict()).Status);

                        actionGate.SetResult(true);
                        await action;

                        EncodeJob persisted = Assert.Single(service.LoadStrict());
                        Assert.Equal(EncodeJobStatus.Completed, persisted.Status);
                        Assert.Equal("Completed", grid.Rows[0].Cells[5].Value?.ToString());
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        form.Close();
                    }
                };

                Application.Run(form);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(form);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Job Manager async refresh test timed out.");
        try
        {
            if (failure != null)
                throw new Xunit.Sdk.XunitException(failure.ToString());
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }
}
