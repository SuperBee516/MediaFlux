using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueRowRemovalSafetyTests
{
    [Fact]
    public void DeleteToolbarContextAndCleanupPathsProtectActiveRowsAndCleanIdleRemoval()
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

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow active = queue.Rows[queue.Rows.Add()];
                DataGridViewRow idle = queue.Rows[queue.Rows.Add()];
                SetField(main, "_suppressRowEvents", false);

                const string activePath = @"C:\MediaFluxTests\active.mkv";
                const string idlePath = @"C:\MediaFluxTests\idle.mkv";
                object activeMeta = SetRow(main, active, activePath);
                SetRow(main, idle, idlePath);
                EncodingPlan plan = new()
                {
                    IsAvailable = true,
                    Source = new("h264", 1920, 1080, 24, 600),
                    Video = new("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
                    Hardware = new(true, "nvenc", true),
                    SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                        100, "Frozen removal-test plan.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
                };
                activeMeta.GetType().GetField("IntelligencePlan", BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(activeMeta, plan);
                var recommendation = new SmartEncodeRecommendation
                {
                    Kind = SmartEncodeRecommendationKind.Review,
                    Confidence = SmartEncodeConfidence.High,
                    PrimaryReason = "Keep the active row's recommendation metadata."
                };
                var calibration = EncodingSizePredictionCalibration.Unavailable(
                    100, "Keep the active row's calibration metadata.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch);
                activeMeta.GetType().GetField("EncodeRecommendation", BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(activeMeta, recommendation);
                activeMeta.GetType().GetField("SizePredictionCalibration", BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(activeMeta, calibration);
                activeMeta.GetType().GetField("DuplicateGroupId", BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(activeMeta, 17);
                activeMeta.GetType().GetField("DuplicateReason", BindingFlags.Instance | BindingFlags.Public)!
                    .SetValue(activeMeta, "Duplicate review metadata remains attached.");

                var runningJobs = Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs");
                runningJobs[active] = activePath;
                SetField(main, "_activeEncodeRow", active);
                SetField(main, "_encodingActive", true);
                RegisterQueuePath(main, activePath, active);
                RegisterQueuePath(main, idlePath, idle);

                queue.ClearSelection();
                active.Selected = true;
                queue.CurrentCell = active.Cells["colName"];
                Invoke(main, "RefreshQueueWorkspacePresentation");

                Button removeToolbar = Field<Button>(main, "_btnRemoveSelectedQueue");
                Assert.False(removeToolbar.Enabled);
                ContextMenuStrip context = queue.ContextMenuStrip ?? throw new InvalidOperationException("Queue context menu is missing.");
                RefreshContextMenuState(main, context);
                Assert.False(MenuItem(context, "Remove Selected").Enabled);
                Assert.False(MenuItem(context, "Clear Queue").Enabled);

                // Exercise the bound toolbar and menu callbacks even if their
                // visual state were bypassed by a keyboard/programmatic trigger.
                removeToolbar.Enabled = true;
                removeToolbar.PerformClick();
                MenuItem(context, "Remove Selected").Enabled = true;
                MenuItem(context, "Remove Selected").PerformClick();
                Invoke(main, "UpdateQueueWorkspaceActionState");
                RefreshContextMenuState(main, context);

                var delete = new KeyEventArgs(Keys.Delete);
                Invoke(main, "dgvEncodeQueue_KeyDown", queue, delete);
                Assert.True(delete.Handled);
                Invoke(main, "RemoveSelectedRows_Click", null, EventArgs.Empty);
                Assert.False((bool)Invoke(main, "RemoveRowAndCleanup", active, false, true)!);
                Invoke(main, "ClearGrid_Click", null, EventArgs.Empty);

                active.Cells["colProgress"].Value = "37%";
                active.Cells["colETA"].Value = "00:01:30";

                Assert.Same(queue, active.DataGridView);
                Assert.Same(activeMeta, active.Tag);
                Assert.Same(plan, activeMeta.GetType().GetField("IntelligencePlan")!.GetValue(activeMeta));
                Assert.Same(recommendation, activeMeta.GetType().GetField("EncodeRecommendation")!.GetValue(activeMeta));
                Assert.Same(calibration, activeMeta.GetType().GetField("SizePredictionCalibration")!.GetValue(activeMeta));
                Assert.Equal(17, activeMeta.GetType().GetField("DuplicateGroupId")!.GetValue(activeMeta));
                Assert.Equal("Duplicate review metadata remains attached.", activeMeta.GetType().GetField("DuplicateReason")!.GetValue(activeMeta));
                Assert.True(runningJobs.ContainsKey(active));
                Assert.Equal("37%", active.Cells["colProgress"].Value);
                Assert.Equal("00:01:30", active.Cells["colETA"].Value);
                Assert.True(Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath").ContainsKey(activePath));
                Assert.True(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(activePath));
                Assert.True(Field<Dictionary<string, double>>(main, "_queueSourceSizeMap").ContainsKey(activePath));
                Assert.True(Field<Dictionary<string, double>>(main, "_etaSpeedState").ContainsKey(activePath));
                Assert.Equal(2, queue.Rows.Cast<DataGridViewRow>().Count(row => !row.IsNewRow));
                Assert.Equal("2", Field<Label>(main, "_queueWorkspaceTotalValue").Text);
                Assert.Equal("1", Field<Label>(main, "_queueWorkspaceRunningValue").Text);

                // The active-job map remains authoritative even if the display flag
                // is stale; mixed selection blocks as the existing context menu does.
                SetField(main, "_encodingActive", false);
                Invoke(main, "UpdateQueueWorkspaceActionState");
                RefreshContextMenuState(main, context);
                Assert.False(removeToolbar.Enabled);
                Assert.False(MenuItem(context, "Remove Selected").Enabled);
                queue.ClearSelection();
                active.Selected = true;
                idle.Selected = true;
                queue.CurrentCell = active.Cells["colName"];
                Invoke(main, "RemoveSelectedRows_Click", null, EventArgs.Empty);
                Assert.Same(queue, active.DataGridView);
                Assert.Same(queue, idle.DataGridView);
                Assert.True(runningJobs.ContainsKey(active));
                Assert.Same(plan, activeMeta.GetType().GetField("IntelligencePlan")!.GetValue(activeMeta));
                Assert.Same(recommendation, activeMeta.GetType().GetField("EncodeRecommendation")!.GetValue(activeMeta));
                Assert.Same(calibration, activeMeta.GetType().GetField("SizePredictionCalibration")!.GetValue(activeMeta));
                Assert.Equal(17, activeMeta.GetType().GetField("DuplicateGroupId")!.GetValue(activeMeta));

                // Once the job is no longer active, an idle selected row is removed
                // through the shared cleanup helper and its path/estimate state goes too.
                runningJobs.TryRemove(active, out _);
                SetField(main, "_activeEncodeRow", null!);
                Field<List<DataGridViewRow>>(main, "_activeEncodeRows").Clear();
                queue.ClearSelection();
                idle.Selected = true;
                queue.CurrentCell = idle.Cells["colName"];
                Invoke(main, "RemoveSelectedRows_Click", null, EventArgs.Empty);

                Assert.Null(idle.DataGridView);
                Assert.False(Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath").ContainsKey(idlePath));
                Assert.False(Field<Dictionary<string, double>>(main, "_estimatedSizeMap").ContainsKey(idlePath));
                Assert.False(Field<Dictionary<string, double>>(main, "_queueSourceSizeMap").ContainsKey(idlePath));
                Assert.False(Field<Dictionary<string, double>>(main, "_etaSpeedState").ContainsKey(idlePath));
                Assert.Same(queue, active.DataGridView);
                Assert.Same(plan, activeMeta.GetType().GetField("IntelligencePlan")!.GetValue(activeMeta));
                Invoke(main, "RefreshQueueWorkspacePresentation");
                Assert.Equal("1", Field<Label>(main, "_queueWorkspaceTotalValue").Text);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue row-removal UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static object SetRow(MainForm main, DataGridViewRow row, string path)
    {
        row.Tag = path;
        row.Cells["colName"].Value = Path.GetFileName(path);
        row.Cells["colStatus"].Value = "Queued";
        return Invoke(main, "EnsureRowMeta", row)!;
    }

    private static void RegisterQueuePath(MainForm main, string path, DataGridViewRow row)
    {
        Field<ConcurrentDictionary<string, DataGridViewRow>>(main, "_rowsByPath")[path] = row;
        Field<Dictionary<string, double>>(main, "_estimatedSizeMap")[path] = 50;
        Field<Dictionary<string, double>>(main, "_queueSourceSizeMap")[path] = 100;
        Field<Dictionary<string, double>>(main, "_etaSpeedState")[path] = 1.25;
    }

    private static void RefreshContextMenuState(MainForm main, ContextMenuStrip menu)
    {
        ToolStripMenuItem Item(string text) => MenuItem(menu, text);
        Invoke(main, "UpdateEncodeQueueContextMenuState", menu,
            Item("Encode"), Item("Encode Settings"), Item("Analyze"), Item("Jobs"),
            Item("File"), Item("Duplicates"), Item("Utilities"));
    }

    private static ToolStripMenuItem MenuItem(ContextMenuStrip menu, string text) =>
        menu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == text);

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
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
