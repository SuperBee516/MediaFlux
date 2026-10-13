using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueExecutionPriorityUiTests
{
    [Fact]
    public void CompactProgressUsesGlobalStatusStripAndHidesWhenIdle()
    {
        RunOnSta(main =>
        {
            StatusStrip status = Field<StatusStrip>(main, "statusStrip1");
            ToolStripProgressBar progress = Field<ToolStripProgressBar>(main, "_queueProgressBar");
            ToolStripStatusLabel operation = Field<ToolStripStatusLabel>(main, "_operationProgressLabel");
            ToolStripStatusLabel details = Field<ToolStripStatusLabel>(main, "_operationProgressDetailsLabel");

            Assert.True(status.Items.Contains(progress));
            Assert.Equal(DockStyle.Bottom, status.Dock);
            Assert.True(status.Visible);
            Assert.False(progress.Visible);
            Assert.False(operation.Visible);
            Assert.False(details.Visible);
            Assert.DoesNotContain(Descendants(main), control => control.Name == "progressBarEncode");

            Invoke(main, "SetQueueProgress", 1, 0, true);
            Assert.True(progress.Visible);
            Assert.Equal(ProgressBarStyle.Marquee, progress.Style);
            Assert.False(details.Visible);

            Invoke(main, "SetQueueProgress", 1, 4, true);
            Assert.True(progress.Visible);
            Assert.Equal(ProgressBarStyle.Continuous, progress.Style);
            Assert.Equal(25, progress.Value);
            Assert.False(operation.Visible);
            Assert.Equal("25%", details.Text);

            List<DataGridViewRow> rows = AddRows(main,
                "1.mkv", "2.mkv", "3.mkv", "4.mkv", "5.mkv", "6.mkv",
                "7.mkv", "8.mkv", "9.mkv", "10.mkv", "11.mkv");
            DataGridViewRow current = rows[2];
            current.Cells["colStatus"].Value = "Encoding";
            current.Cells["colProgress"].Value = "42%";
            current.Cells["colETA"].Value = "00:12:34";
            Field<List<DataGridViewRow>>(main, "_activeEncodeRows").Add(current);
            SetField(main, "_activeEncodeRow", current);
            SetField(main, "_encodeProcessedCount", 3);
            SetField(main, "_encodingActive", true);

            Invoke(main, "UpdateOperationProgressPresentation");

            Assert.True(progress.Visible);
            Assert.Equal(ProgressBarStyle.Continuous, progress.Style);
            Assert.Equal(42, progress.Value);
            Assert.True(operation.Visible);
            Assert.Equal("Encoding 3 of 11", operation.Text);
            Assert.True(details.Visible);
            Assert.Equal("42% · ETA 00:12:34", details.Text);

            Field<List<DataGridViewRow>>(main, "_activeEncodeRows").Add(rows[3]);
            Invoke(main, "UpdateOperationProgressPresentation");
            Assert.Equal(ProgressBarStyle.Marquee, progress.Style);

            Field<List<DataGridViewRow>>(main, "_activeEncodeRows").Clear();
            SetField(main, "_activeEncodeRow", null);
            SetField(main, "_encodingActive", false);
            Invoke(main, "UpdateOperationProgressPresentation");

            Assert.True(progress.Visible);
            Assert.Equal(25, progress.Value);
            Assert.Equal("25%", details.Text);

            Invoke(main, "SetQueueProgress", 0, 0, false);
            Assert.False(progress.Visible);
            Assert.Equal(ProgressBarStyle.Continuous, progress.Style);
            Assert.Equal(0, progress.Value);
            Assert.False(operation.Visible);
            Assert.False(details.Visible);
        });
    }

    [Fact]
    public void OrderColumnShowsLogicalPositionsAcrossSortAndUsesColumnPersistence()
    {
        RunOnSta(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            List<DataGridViewRow> rows = AddRows(main, "A.mkv", "B.mkv", "C.mkv");
            SetQueueSequence(main, rows[0], 90);
            SetQueueSequence(main, rows[1], 10);
            SetQueueSequence(main, rows[2], 50);

            Invoke(main, "RefreshQueueExecutionOrderPresentation");
            Assert.Equal(3, rows[0].Cells["colOrder"].Value);
            Assert.Equal(1, rows[1].Cells["colOrder"].Value);
            Assert.Equal(2, rows[2].Cells["colOrder"].Value);
            Assert.Equal(10L, QueueSequence(main, rows[1]));
            Assert.Equal("1", FormattedOrder(main, rows[1]));

            queue.Sort(queue.Columns["colName"], ListSortDirection.Descending);
            Invoke(main, "RefreshQueueExecutionOrderPresentation");
            Assert.Equal("colName", queue.SortedColumn?.Name);
            Assert.Equal(SortOrder.Descending, queue.SortOrder);
            Assert.Equal(1, rows[1].Cells["colOrder"].Value);
            Assert.Equal(2, rows[2].Cells["colOrder"].Value);
            Assert.Equal(3, rows[0].Cells["colOrder"].Value);

            EncodingPlan plan = MakePlan();
            SetMetaField(main, rows[1], "IntelligencePlan", plan);
            SelectRows(queue, rows[1]);
            Invoke(main, "RefreshQueueInspectorFromSelection");
            Invoke(main, "ScheduleEncodingPlanRefresh");
            TableLayoutPanel planTable = Field<TableLayoutPanel>(main, "_encodingPlanTable");
            Control retainedPlanControl = planTable.Controls[0];
            ClickPriority(main, QueueExecutionOrderOperation.MoveDown);
            Assert.Equal("2 of 3", Field<Label>(main, "_queueInspectorExecution").Text);
            Assert.Same(plan, MetaField(main, rows[1], "IntelligencePlan"));
            Assert.Same(retainedPlanControl, planTable.Controls[0]);

            DataGridViewColumn orderColumn = queue.Columns["colOrder"];
            Config config = Field<Config>(main, "_config");
            string configPath = Field<string>(main, "_configPath");
            Assert.True(orderColumn.Visible);
            Assert.True(config.ShowExecutionOrderColumn);
            Assert.Contains("colOrder", config.EncodeGridColumnOrder);
            orderColumn.Width = Math.Max(orderColumn.MinimumWidth, 73);
            Invoke(main, "PersistQueueColumnWidths");
            Invoke(main, "MoveQueueColumn", "colOrder", 1);
            int savedDisplayIndex = orderColumn.DisplayIndex;
            config.ShowExecutionOrderColumn = false;
            Invoke(main, "ApplyQueueWorkspaceColumnPreferences");
            config.Save(configPath);

            Config restored = Config.Load(configPath);
            Assert.False(restored.ShowExecutionOrderColumn);
            Assert.Equal(orderColumn.Width, restored.EncodeGridColumnWidths["colOrder"]);
            Assert.Equal(savedDisplayIndex, restored.EncodeGridColumnOrder.IndexOf("colOrder"));
            SetField(main, "_config", restored);
            Invoke(main, "ApplyQueueWorkspaceColumnPreferences");
            Invoke(main, "ApplyQueueColumnLayoutPreferences");
            Invoke(main, "ApplyRememberedQueueColumnOrder");
            Assert.False(queue.Columns["colOrder"].Visible);
            Assert.Equal(orderColumn.Width, queue.Columns["colOrder"].Width);
            Assert.Equal(savedDisplayIndex, queue.Columns["colOrder"].DisplayIndex);
        });
    }

    [Fact]
    public void ContextMenuCommandsUseLogicalOrderAndPreserveSelectionAndPresentationSort()
    {
        RunOnSta(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            List<DataGridViewRow> rows = AddRows(main, "A.mkv", "B.mkv", "C.mkv", "D.mkv", "E.mkv", "F.mkv");
            queue.Sort(queue.Columns["colName"], ListSortDirection.Descending);
            ToolStripMenuItem priority = GetPriorityMenu(queue);
            Assert.NotNull(queue.ContextMenuStrip);
            Assert.Equal("queuePriorityMenu", priority.Name);

            (QueueExecutionOrderOperation Operation, string[] Expected)[] cases =
            [
                (QueueExecutionOrderOperation.EncodeNext, ["C.mkv", "E.mkv", "A.mkv", "B.mkv", "D.mkv", "F.mkv"]),
                (QueueExecutionOrderOperation.MoveToTop, ["C.mkv", "E.mkv", "A.mkv", "B.mkv", "D.mkv", "F.mkv"]),
                (QueueExecutionOrderOperation.MoveUp, ["A.mkv", "C.mkv", "B.mkv", "E.mkv", "D.mkv", "F.mkv"]),
                (QueueExecutionOrderOperation.MoveDown, ["A.mkv", "B.mkv", "D.mkv", "C.mkv", "F.mkv", "E.mkv"]),
                (QueueExecutionOrderOperation.MoveToBottom, ["A.mkv", "B.mkv", "D.mkv", "F.mkv", "C.mkv", "E.mkv"])
            ];

            foreach ((QueueExecutionOrderOperation operation, string[] expected) in cases)
            {
                for (int index = 0; index < rows.Count; index++)
                    SetQueueSequence(main, rows[index], index + 1);
                Invoke(main, "RefreshQueueExecutionOrderPresentation");
                SelectRows(queue, rows[2], rows[4]);
                Invoke(main, "UpdateQueuePriorityCommandState", priority);
                ToolStripMenuItem command = PriorityItem(priority, operation);
                Assert.True(command.Enabled);
                command.PerformClick();

                Assert.Equal(expected, LogicalNames(main));
                Assert.Equal(new[] { rows[2], rows[4] }, queue.SelectedRows.Cast<DataGridViewRow>()
                    .OrderBy(row => Array.IndexOf(rows.ToArray(), row)));
                Assert.Same(rows[2], queue.CurrentRow);
                Assert.Equal("colName", queue.SortedColumn?.Name);
                Assert.Equal(SortOrder.Descending, queue.SortOrder);
            }

            SelectRows(queue, rows[0]);
            Invoke(main, "UpdateQueuePriorityCommandState", priority);
            Assert.False(PriorityItem(priority, QueueExecutionOrderOperation.EncodeNext).Enabled);
            Assert.False(PriorityItem(priority, QueueExecutionOrderOperation.MoveToTop).Enabled);
            Assert.False(PriorityItem(priority, QueueExecutionOrderOperation.MoveUp).Enabled);
            Assert.True(PriorityItem(priority, QueueExecutionOrderOperation.MoveDown).Enabled);
            Assert.True(PriorityItem(priority, QueueExecutionOrderOperation.MoveToBottom).Enabled);

            SelectRows(queue, rows[4]);
            Invoke(main, "UpdateQueuePriorityCommandState", priority);
            Assert.False(PriorityItem(priority, QueueExecutionOrderOperation.MoveDown).Enabled);
            Assert.False(PriorityItem(priority, QueueExecutionOrderOperation.MoveToBottom).Enabled);
            Assert.True(PriorityItem(priority, QueueExecutionOrderOperation.EncodeNext).Enabled);

            SetMetaField(main, rows[2], "ExcludedFromEncodeAsDuplicate", true);
            SelectRows(queue, rows[2]);
            Invoke(main, "UpdateQueuePriorityCommandState", priority);
            Assert.All(priority.DropDownItems.OfType<ToolStripMenuItem>(), item => Assert.False(item.Enabled));
        });
    }

    [Fact]
    public void SearchAndOperationalFiltersDoNotChangeMoveUpLogicalNeighbors()
    {
        RunOnSta(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            List<DataGridViewRow> rows = AddRows(main, "A.mkv", "B.mkv", "C.mkv", "D.mkv", "E.mkv");
            foreach (DataGridViewRow row in rows)
            {
                SetMetaField(main, row, "EncodeRecommendation", new SmartEncodeRecommendation
                {
                    Kind = row == rows[2] || row == rows[4]
                        ? SmartEncodeRecommendationKind.Review
                        : SmartEncodeRecommendationKind.Skip,
                    Confidence = SmartEncodeConfidence.Medium,
                    PrimaryReason = "Queue execution order is independent of recommendation filtering."
                });
            }

            TextBox search = Field<TextBox>(main, "_queueWorkspaceSearchBox");
            search.Text = "E.mkv";
            Assert.False(rows[3].Visible);
            SelectRows(queue, rows[4]);
            ClickPriority(main, QueueExecutionOrderOperation.MoveUp);
            Assert.Equal(new[] { "A.mkv", "B.mkv", "C.mkv", "E.mkv", "D.mkv" }, LogicalNames(main));
            Assert.True(rows[4].Selected);

            search.Clear();
            ComboBox view = Field<ComboBox>(main, "_queueWorkspaceViewSelector");
            view.SelectedItem = "Review";
            Invoke(main, "ApplyEncodeQueueViewFilter");
            Assert.True(rows[2].Visible);
            Assert.True(rows[4].Visible);
            Assert.False(rows[3].Visible);
            SelectRows(queue, rows[4]);
            ClickPriority(main, QueueExecutionOrderOperation.MoveUp);
            Assert.Equal(new[] { "A.mkv", "B.mkv", "E.mkv", "C.mkv", "D.mkv" }, LogicalNames(main));
            Assert.True(rows[4].Selected);
            Assert.Equal("colOrder", queue.Columns["colOrder"].Name);
            Assert.Same(queue.ContextMenuStrip, Field<DataGridView>(main, "dgvEncodeQueue").ContextMenuStrip);
        });
    }

    [Fact]
    public void ActiveRunShowsRunningAndPendingTailPositionsAndProtectsExcludedRows()
    {
        RunOnSta(main =>
        {
            DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
            List<DataGridViewRow> rows = AddRows(main, "A.mkv", "B.mkv", "C.mkv", "D.mkv", "E.mkv");
            var active = new List<DataGridViewRow> { rows[0], rows[1], rows[2] };
            SetField(main, "_encodingActive", true);
            SetField(main, "_activeEncodeQueue", active);
            SetField(main, "_activeEncodeQueueDispatchedCount", 1);
            SetField(main, "_activeEncodeQueueAccepting", true);
            var running = Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs");
            running[rows[0]] = "A.mkv";
            SetMetaField(main, rows[4], "ExcludedFromEncodeAsDuplicate", true);
            Invoke(main, "RefreshQueueExecutionOrderPresentation");

            Assert.Equal("Running", FormattedOrder(main, rows[0]));
            Assert.Equal(1, rows[1].Cells["colOrder"].Value);
            Assert.Equal(2, rows[2].Cells["colOrder"].Value);
            Assert.Null(rows[3].Cells["colOrder"].Value);
            Assert.Null(rows[4].Cells["colOrder"].Value);

            ToolStripMenuItem priority = GetPriorityMenu(queue);
            SelectRows(queue, rows[0]);
            Invoke(main, "UpdateQueuePriorityCommandState", priority);
            Assert.All(priority.DropDownItems.OfType<ToolStripMenuItem>(), item => Assert.False(item.Enabled));

            Assert.True((bool)Invoke(main, "TryAppendActiveEncodeQueueRow", rows[3])!);
            Application.DoEvents();
            Assert.Equal(3, rows[3].Cells["colOrder"].Value);
            SelectRows(queue, rows[0], rows[3]);
            Invoke(main, "UpdateQueuePriorityCommandState", priority);
            Assert.True(PriorityItem(priority, QueueExecutionOrderOperation.EncodeNext).Enabled);
            PriorityItem(priority, QueueExecutionOrderOperation.EncodeNext).PerformClick();

            Assert.Equal(new[] { rows[0], rows[3], rows[1], rows[2] }, active);
            Assert.Equal(new[] { "A.mkv", "D.mkv", "B.mkv", "E.mkv", "C.mkv" }, LogicalNames(main));
            Assert.Contains("already dispatched", (Field<ToolStripStatusLabel>(main, "toolStripStatusLabel1").Text ?? string.Empty).ToLowerInvariant());
            Assert.Equal("Running", FormattedOrder(main, rows[0]));
            Assert.Equal(1, rows[3].Cells["colOrder"].Value);
            Assert.Equal(2, rows[1].Cells["colOrder"].Value);
            Assert.Equal(3, rows[2].Cells["colOrder"].Value);
            Assert.True(rows[3].Selected);
            SelectRows(queue, rows[3]);
            Invoke(main, "RefreshQueueInspectorFromSelection");
            Assert.Equal("Next", Field<Label>(main, "_queueInspectorExecution").Text);

            // The retry reuses a row object but is still pending tail work with a
            // fresh logical position; the existing retry policy remains unchanged.
            running.TryRemove(rows[0], out _);
            SetField(main, "_activeEncodeQueue", new List<DataGridViewRow> { rows[0] });
            SetField(main, "_activeEncodeQueueDispatchedCount", 1);
            Field<CheckBox>(main, "chkRetryFailedJobs").Checked = true;
            Assert.True((bool)Invoke(main, "TryQueueFailedRowForAutoRetry", rows[0])!);
            Application.DoEvents();
            Assert.Equal(1, rows[0].Cells["colOrder"].Value);

            // A pending row marked as authoritatively duplicate-excluded is removed
            // through the existing Phase 4A path and never gets a displayed position.
            SetField(main, "_activeEncodeQueue", new List<DataGridViewRow> { rows[1], rows[4] });
            SetField(main, "_activeEncodeQueueDispatchedCount", 1);
            SetMetaField(main, rows[4], "ExcludedFromEncodeAsDuplicate", true);
            Invoke(main, "RemoveSoftExcludedRowsFromActiveEncodeQueue");
            Application.DoEvents();
            Assert.Null(rows[4].Cells["colOrder"].Value);
            Assert.DoesNotContain(rows[4], (IEnumerable<DataGridViewRow>)Field<List<DataGridViewRow>>(main, "_activeEncodeQueue"));

            running.Clear();
            SetField(main, "_encodingActive", false);
            SetField(main, "_activeEncodeQueue", null!);
            SetField(main, "_activeEncodeQueueDispatchedCount", 0);
            Invoke(main, "RefreshQueueExecutionOrderPresentation");
            Assert.Null(rows[4].Cells["colOrder"].Value);
            Assert.Equal(4, rows.Where(row => row != rows[4]).Select(row => row.Cells["colOrder"].Value).Distinct().Count());

            queue.Rows.Remove(rows[1]);
            Application.DoEvents();
            DataGridViewRow[] remainingEligible = ((IEnumerable)Invoke(main, "GetEligibleEncodeRowsInExecutionOrder")!)
                .Cast<DataGridViewRow>().ToArray();
            Assert.Equal(Enumerable.Range(1, remainingEligible.Length),
                remainingEligible.Select(row => (int)row.Cells["colOrder"].Value!));
        });
    }

    private static void RunOnSta(Action<MainForm> action)
    {
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
                action(main);
                Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (main != null)
                {
                    SetFieldIfPresent(main, "_encodingActive", false);
                    SetFieldIfPresent(main, "_activeEncodeQueue", null);
                    WinFormsTestLifecycle.CloseAndDispose(main);
                }
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue execution priority UI test timed out.");
        if (failure != null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static List<DataGridViewRow> AddRows(MainForm main, params string[] names)
    {
        DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
        SetField(main, "_suppressRowEvents", true);
        var rows = new List<DataGridViewRow>();
        try
        {
            foreach (string name in names)
            {
                DataGridViewRow row = queue.Rows[queue.Rows.Add()];
                row.Cells["colName"].Value = name;
                row.Cells["colStatus"].Value = "Queued";
                Invoke(main, "EnsureRowMeta", row);
                rows.Add(row);
            }
        }
        finally { SetField(main, "_suppressRowEvents", false); }
        Invoke(main, "RefreshQueueExecutionOrderPresentation");
        return rows;
    }

    private static void SetQueueSequence(MainForm main, DataGridViewRow row, long sequence) =>
        SetMetaField(main, row, "QueueSequence", sequence);

    private static long QueueSequence(MainForm main, DataGridViewRow row) =>
        Convert.ToInt64(MetaField(main, row, "QueueSequence"));

    private static void SetMetaField(MainForm main, DataGridViewRow row, string name, object value) =>
        (Meta(main, row).GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(name)).SetValue(Meta(main, row), value);

    private static object MetaField(MainForm main, DataGridViewRow row, string name) =>
        (Meta(main, row).GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingFieldException(name)).GetValue(Meta(main, row))!;

    private static object Meta(MainForm main, DataGridViewRow row) => Invoke(main, "EnsureRowMeta", row)!;

    private static string FormattedOrder(MainForm main, DataGridViewRow row)
    {
        DataGridView grid = Field<DataGridView>(main, "dgvEncodeQueue");
        DataGridViewCell cell = row.Cells["colOrder"];
        var formatting = new DataGridViewCellFormattingEventArgs(
            cell.ColumnIndex,
            row.Index,
            cell.Value,
            cell.ValueType ?? typeof(object),
            cell.Style);
        Invoke(main, "QueueWorkspace_CellFormatting", grid, formatting);
        return Convert.ToString(formatting.Value) ?? string.Empty;
    }

    private static string[] LogicalNames(MainForm main) =>
        ((IEnumerable)Invoke(main, "GetEncodeRowsInExecutionOrder")!)
        .Cast<DataGridViewRow>()
        .Select(row => row.Cells["colName"].Value?.ToString() ?? string.Empty)
        .ToArray();

    private static EncodingPlan MakePlan() => new()
    {
        IsAvailable = true,
        Source = new EncodingPlanSource("h264", 1920, 1080, 24, 1200),
        Video = new EncodingPlanVideo("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
        Hardware = new EncodingPlanHardware(true, "nvenc", true),
        Estimates = new EncodingPlanEstimates(null, null, null),
        SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
            8, "Frozen execution-position UI test plan.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
    };

    private static void SelectRows(DataGridView queue, params DataGridViewRow[] rows)
    {
        queue.ClearSelection();
        if (rows.Length > 0 && rows[0].Visible)
            queue.CurrentCell = rows[0].Cells["colName"];
        foreach (DataGridViewRow row in rows)
            if (row.Visible)
                row.Selected = true;
    }

    private static ToolStripMenuItem GetPriorityMenu(DataGridView queue) =>
        (queue.ContextMenuStrip?.Items.Cast<ToolStripItem>().OfType<ToolStripMenuItem>()
            .SingleOrDefault(item => item.Name == "queuePriorityMenu"))
        ?? throw new InvalidOperationException("Queue priority context menu is missing.");

    private static ToolStripMenuItem PriorityItem(
        ToolStripMenuItem priority,
        QueueExecutionOrderOperation operation) =>
        priority.DropDownItems.OfType<ToolStripMenuItem>()
            .Single(item => item.Tag is QueueExecutionOrderOperation itemOperation && itemOperation == operation);

    private static void ClickPriority(MainForm main, QueueExecutionOrderOperation operation)
    {
        DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
        ToolStripMenuItem priority = GetPriorityMenu(queue);
        Invoke(main, "UpdateQueuePriorityCommandState", priority);
        ToolStripMenuItem item = PriorityItem(priority, operation);
        Assert.True(item.Enabled);
        item.PerformClick();
    }

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static void SetField(MainForm main, string name, object? value) =>
        (typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(main, value);

    private static void SetFieldIfPresent(MainForm main, string name, object? value) =>
        typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(main, value);

    private static object? Invoke(MainForm main, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        return method.Invoke(main, args);
    }
}
