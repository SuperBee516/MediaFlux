using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueEstimateOwnershipTests
{
    [Fact]
    public void ParallelEstimatePassSkipsActiveRecoveryButSchedulesIdleRowsAndProtectsCompletion()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? main = null;
        string? isolatedConfigPath = null;
        string? testDirectory = null;
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
                testDirectory = Path.Combine(Path.GetTempPath(), $"MediaFlux.EstimateOwnership.{Guid.NewGuid():N}");
                Directory.CreateDirectory(testDirectory);
                string recoveryPath = SizedFile(testDirectory, "recovering.mkv", 4);
                string transitionPath = SizedFile(testDirectory, "transition.mkv", 8);
                string idlePath = SizedFile(testDirectory, "idle.mkv", 12);

                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow recovery = queue.Rows[queue.Rows.Add()];
                SetRow(main, recovery, recoveryPath, "Attempting source recovery…");
                SetField(main, "_suppressRowEvents", false);
                object recoveryMeta = EnsureMeta(main, recovery);

                Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", transitionPath, false, false)!);
                Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", idlePath, false, false)!);
                DataGridViewRow transition = RowForPath(main, transitionPath);
                DataGridViewRow idle = RowForPath(main, idlePath);
                object transitionMeta = EnsureMeta(main, transition);
                object idleMeta = EnsureMeta(main, idle);

                var plan = new EncodingPlan
                {
                    IsAvailable = true,
                    Source = new("h264", 1920, 1080, 24, 600),
                    Video = new("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
                    Hardware = new(true, "nvenc", true),
                    Estimates = new(null, null, null),
                    SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                        100, "Frozen plan for estimate ownership test.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
                };
                transitionMeta.GetType().GetField("IntelligencePlan")!.SetValue(transitionMeta, plan);
                transition.Selected = true;
                queue.CurrentCell = transition.Cells["colName"];
                Invoke(main, "RenderEncodingPlan", plan, null, transition);
                TableLayoutPanel planTable = Field<TableLayoutPanel>(main, "_encodingPlanTable");
                Assert.NotEmpty(planTable.Controls.Cast<Control>());
                Control planControl = planTable.Controls[0];
                DateTime frozenDecisionUtc = Assert.IsType<DateTime>(plan.SizePredictionCalibration?.DecisionUtc);

                var runningJobs = Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs");
                runningJobs[recovery] = recoveryPath;
                SetField(main, "_encodingActive", true);

                recovery.Cells["colEstimatedSize"].Value = "2 MB";
                Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[recoveryPath] = 2;
                Field<Dictionary<string, double>>(main, "_queueSourceSizeMap")[recoveryPath] = 4;
                Invoke(main, "RunEstimatePass");

                Assert.Equal("Attempting source recovery…", recovery.Cells["colStatus"].Value);
                Assert.Equal("Attempting source recovery…", recoveryMeta.GetType().GetField("CurrentProcessingStage")!.GetValue(recoveryMeta));
                Assert.Equal("2 MB", recovery.Cells["colEstimatedSize"].Value);
                Assert.Equal("Estimating", transition.Cells["colStatus"].Value);
                Assert.Equal("Estimating", idle.Cells["colStatus"].Value);
                EstimateBackgroundService estimateService = Field<EstimateBackgroundService>(main, "_estimateService");
                Assert.Equal(2, estimateService.PendingEstimates);
                Assert.Same(planControl, planTable.Controls[0]);

                // Simulate queued idle estimates whose rows become active jobs
                // before their completion callbacks are applied.
                estimateService.ResetAndCancel();
                runningJobs[transition] = transitionPath;
                Invoke(main, "SetEncodeRowState", transition, "Retrying encode with recovered source…", "", "", "Recovery retry owns this status.");

                Guid transitionId = (Guid)transitionMeta.GetType().GetField("QueueItemId")!.GetValue(transitionMeta)!;
                Guid idleId = (Guid)idleMeta.GetType().GetField("QueueItemId")!.GetValue(idleMeta)!;
                var recommendation = new SmartEncodeRecommendation
                {
                    Kind = SmartEncodeRecommendationKind.Review,
                    Confidence = SmartEncodeConfidence.High,
                    EstimatedSavingsPercent = 20,
                    EstimatedSavingsMb = 2,
                    PrimaryReason = "Synthetic estimate completion for the recovery race regression."
                };
                var calibration = EncodingSizePredictionCalibration.Unavailable(
                    6, "Synthetic estimate result.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch);
                var resultQueue = Field<ConcurrentQueue<EstimateBackgroundService.SmartEstimateResult>>(estimateService, "_smartResults");
                resultQueue.Enqueue(Result(estimateService.CurrentGeneration, transitionId, transitionPath, 8, 6, recommendation, calibration));
                resultQueue.Enqueue(Result(estimateService.CurrentGeneration, idleId, idlePath, 12, 9, recommendation, calibration));

                Invoke(main, "ApplySmartEstimateResultsBatch");

                Assert.Equal("Retrying encode with recovered source…", transition.Cells["colStatus"].Value);
                Assert.Equal("Retrying encode with recovered source…", transitionMeta.GetType().GetField("CurrentProcessingStage")!.GetValue(transitionMeta));
                Assert.Equal("Queued", idle.Cells["colStatus"].Value);
                Assert.Equal(6d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[transitionPath]);
                Assert.Equal(9d, Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[idlePath]);
                Assert.True(Field<double>(main, "_queueTotalEstimatedMb") > 0);
                Invoke(main, "UpdateSizeTotals", true);
                Assert.Equal(17d, Field<double>(main, "_queueTotalEstimatedMb"));
                Assert.Same(planControl, planTable.Controls[0]);
                Assert.Equal(frozenDecisionUtc, plan.SizePredictionCalibration?.DecisionUtc);

                // A delayed result for a removed/recreated row with the same path
                // must not be applied to the replacement queue item.
                Assert.True((bool)Invoke(main, "RemoveRowAndCleanup", idle, false, true)!);
                Assert.True((bool)Invoke(main, "AddEncodeItemIfNotPresent", idlePath, false, false)!);
                DataGridViewRow replacement = RowForPath(main, idlePath);
                Assert.Equal("", replacement.Cells["colEstimatedSize"].Value);
                resultQueue.Enqueue(Result(estimateService.CurrentGeneration, idleId, idlePath, 12, 1, recommendation, calibration));
                Invoke(main, "ApplySmartEstimateResultsBatch");
                Assert.Equal("", replacement.Cells["colEstimatedSize"].Value);
                Assert.False(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(idlePath));
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                if (!string.IsNullOrWhiteSpace(testDirectory) && Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Estimate ownership UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void BackgroundWorkersSkipSourcesClaimedAfterSchedulingAndStillEstimateIdleSources()
    {
        string activePath = Path.Combine(Path.GetTempPath(), $"MediaFlux.active.{Guid.NewGuid():N}.mkv");
        string idlePath = Path.Combine(Path.GetTempPath(), $"MediaFlux.idle.{Guid.NewGuid():N}.mkv");
        var active = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        string normalizedActivePath = Path.GetFullPath(activePath);
        using var activeChecked = new ManualResetEventSlim();
        using var activeCheckEntered = new ManualResetEventSlim();
        using var releaseActiveCheck = new ManualResetEventSlim();
        using var service = new EstimateBackgroundService(
            new MediaInfoService(persistentCacheEnabled: false),
            isSourceOwnedByActiveJob: path =>
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), normalizedActivePath))
                {
                    activeCheckEntered.Set();
                    if (!releaseActiveCheck.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Active estimate eligibility check was not released.");
                }

                bool owned = active.ContainsKey(Path.GetFullPath(path));
                if (owned) activeChecked.Set();
                return owned;
            });

        VideoEncoderSelection encoder = new("x265", VideoCodecFamily.Hevc, "libx265");
        service.QueueSmartEstimate(activePath, true, "Medium Quality (Default)", 0, encoder, 28,
            null, null, false, true, 10, new StorageSavingsOptions(),
            historicalCalibrationEnabled: false, queueItemId: Guid.NewGuid());
        service.QueueSmartEstimate(idlePath, true, "Medium Quality (Default)", 0, encoder, 28,
            null, null, false, true, 10, new StorageSavingsOptions(),
            historicalCalibrationEnabled: false, queueItemId: Guid.NewGuid());

        Assert.True(activeCheckEntered.Wait(TimeSpan.FromSeconds(10)), "The active estimate did not reach the worker eligibility gate.");
        active[normalizedActivePath] = 0;
        releaseActiveCheck.Set();

        EstimateBackgroundService.SmartEstimateResult result = default;
        bool gotResult = SpinWait.SpinUntil(() => service.TryDequeueSmart(out result), TimeSpan.FromSeconds(10));
        Assert.True(gotResult, "The unrelated idle estimate did not complete.");
        Assert.True(activeChecked.Wait(TimeSpan.FromSeconds(10)), "The active source was not checked by the worker eligibility gate.");
        Assert.Equal(Path.GetFullPath(idlePath), Path.GetFullPath(result.Path));
        Assert.False(service.TryDequeueSmart(out _));
        Assert.True(SpinWait.SpinUntil(() => service.PendingEstimates == 0, TimeSpan.FromSeconds(10)));
    }

    private static string SizedFile(string directory, string name, int sizeMb)
    {
        string path = Path.Combine(directory, name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.SetLength(sizeMb * 1024L * 1024L);
        return path;
    }

    private static void SetRow(MainForm main, DataGridViewRow row, string path, string status)
    {
        row.Tag = path;
        row.Cells["colName"].Value = Path.GetFileName(path);
        object meta = EnsureMeta(main, row);
        meta.GetType().GetField("Path")!.SetValue(meta, path);
        Invoke(main, "SetEncodeRowState", row, status, "", "", status);
        Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
    }

    private static EstimateBackgroundService.SmartEstimateResult Result(
        int generation,
        Guid queueItemId,
        string path,
        double sourceMb,
        double estimatedMb,
        SmartEncodeRecommendation recommendation,
        EncodingSizePredictionCalibration calibration) => new(
            generation,
            queueItemId,
            path,
            sourceMb,
            estimatedMb,
            60,
            "1920x1080",
            "h264",
            24,
            false,
            null,
            recommendation,
            "Synthetic estimate completion.",
            128,
            0,
            sizeCalibration: calibration);

    private static object EnsureMeta(MainForm main, DataGridViewRow row) =>
        Invoke(main, "EnsureRowMeta", row)!;

    private static DataGridViewRow RowForPath(MainForm main, string path) =>
        Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path];

    private static T Field<T>(MainForm main, string name) =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static T Field<T>(EstimateBackgroundService service, string name) =>
        (T)(typeof(EstimateBackgroundService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(service)
            ?? throw new MissingFieldException(name));

    private static void SetField(MainForm main, string name, object value) =>
        (typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(main, value);

    private static object? Invoke(MainForm main, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        return method.Invoke(main, args);
    }
}
