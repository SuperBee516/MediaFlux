using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueColumnOrderingUiTests
{
    private static readonly string[] PhaseOneOrder =
    [
        "colName", "colStatus", "colEncodeRecommendation", "colPlannedOutput",
        "colSourceEstimate", "colProgress", "colETA", "colSize", "colEstimatedSize",
        "colCreated", "colCustom", "colDuplicate", "colDuplicateConfidence", "colDuplicateAction"
    ];

    [Fact]
    public void NativeAndSettingsReorderPersistIndependentlyOfWidthLockAndReset()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? first = null;
        MainForm? restored = null;
        string? isolatedConfigPath = null;
        string? restoredConfigPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                first = new MainForm();
                isolatedConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(first);
                first.Show();
                Application.DoEvents();
                DataGridView grid = Field<DataGridView>(first, "dgvEncodeQueue");
                Config config = Field<Config>(first, "_config");
                Assert.True(grid.AllowUserToOrderColumns);
                Assert.Equal(PhaseOneOrder, Order(grid));
                using var picker = new ComboBox();
                using var moveLeft = new Button();
                using var moveRight = new Button();
                Invoke(first, "RefreshQueueColumnOrderChoices", picker, "colName");
                Invoke(first, "UpdateQueueColumnMoveButtons", picker, moveLeft, moveRight);
                Assert.False(moveLeft.Enabled);
                Assert.True(moveRight.Enabled);
                picker.SelectedIndex = picker.Items.Count - 1;
                Invoke(first, "UpdateQueueColumnMoveButtons", picker, moveLeft, moveRight);
                Assert.True(moveLeft.Enabled);
                Assert.False(moveRight.Enabled);

                int statusWidth = grid.Columns["colStatus"].Width + 31;
                SetField(first, "_queueColumnResizeGesture", true);
                grid.Columns["colStatus"].Width = statusWidth;
                Invoke(first, "DgvEncodeQueue_ColumnResizeMouseUp", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));

                // Native DisplayIndex changes remain available with widths unlocked.
                int plannedWidth = grid.Columns["colPlannedOutput"].Width;
                grid.Columns["colSourceEstimate"].DisplayIndex = 3;
                Invoke(first, "DgvEncodeQueue_ColumnResizeMouseUp", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                Assert.Equal("colSourceEstimate", Order(grid)[3]);
                Assert.Equal(plannedWidth, grid.Columns["colPlannedOutput"].Width);

                // Locking widths does not lock ordering.
                int[] widthsBeforeLockAndReorder = grid.Columns.Cast<DataGridViewColumn>()
                    .Select(column => column.Width).ToArray();
                config.EncodeGridColumnWidthsLocked = true;
                Invoke(first, "ApplyQueueColumnResizeLock");
                Assert.False(grid.AllowUserToResizeColumns);
                string[] orderBeforeLockedMove = Order(grid);
                grid.Columns["colETA"].DisplayIndex = 2;
                Invoke(first, "DgvEncodeQueue_ColumnResizeMouseUp", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                Assert.False(orderBeforeLockedMove.SequenceEqual(Order(grid), StringComparer.OrdinalIgnoreCase));
                Assert.Equal(widthsBeforeLockAndReorder, grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Width));
                Assert.Equal(Order(grid), config.EncodeGridColumnOrder);
                Assert.True(config.EncodeGridColumnWidthsLocked);

                int etaBeforeMove = grid.Columns["colETA"].DisplayIndex;
                Invoke(first, "MoveQueueColumn", "colETA", -1);
                Assert.Equal(etaBeforeMove - 1, grid.Columns["colETA"].DisplayIndex);
                Invoke(first, "MoveQueueColumn", "colETA", 1);
                Assert.Equal(etaBeforeMove, grid.Columns["colETA"].DisplayIndex);

                DataGridViewColumn hiddenColumn = grid.Columns["colCreated"];
                hiddenColumn.Visible = false;
                Invoke(first, "MoveQueueColumn", "colCreated", -1);
                Invoke(first, "MoveQueueColumn", "colCreated", -1);
                int hiddenDisplayIndex = hiddenColumn.DisplayIndex;
                string[] persistedOrder = Order(grid);
                config.Save(isolatedConfigPath!);

                restored = new MainForm();
                restoredConfigPath = QueueWorkspaceTestSupport.UseIsolatedConfig(restored);
                SetField(restored, "_config", Config.Load(isolatedConfigPath!));
                restored.Show();
                Application.DoEvents();

                DataGridView restoredGrid = Field<DataGridView>(restored, "dgvEncodeQueue");
                Config restoredConfig = Field<Config>(restored, "_config");
                DataGridViewColumn restoredHidden = restoredGrid.Columns["colCreated"];
                Assert.Equal(persistedOrder, Order(restoredGrid));
                Assert.Equal(statusWidth, restoredGrid.Columns["colStatus"].Width);
                Assert.Equal(plannedWidth, restoredGrid.Columns["colPlannedOutput"].Width);
                Assert.True(restoredConfig.EncodeGridColumnWidthsLocked);
                Assert.False(restoredGrid.AllowUserToResizeColumns);
                Assert.False(restoredHidden.Visible);
                Assert.Equal(hiddenDisplayIndex, restoredHidden.DisplayIndex);
                restoredHidden.Visible = true;
                Assert.Equal(hiddenDisplayIndex, restoredHidden.DisplayIndex);

                int[] widthsBeforeOrderReset = restoredGrid.Columns.Cast<DataGridViewColumn>()
                    .Select(column => column.Width).ToArray();
                Invoke(restored, "ResetQueueColumnOrder");
                Assert.Equal(PhaseOneOrder, Order(restoredGrid));
                Assert.Equal(widthsBeforeOrderReset, restoredGrid.Columns.Cast<DataGridViewColumn>().Select(column => column.Width));

                string[] orderBeforeWidthReset = Order(restoredGrid);
                bool[] visibilityBeforeWidthReset = restoredGrid.Columns.Cast<DataGridViewColumn>()
                    .Select(column => column.Visible).ToArray();
                Invoke(restored, "ResetQueueColumnWidths");
                Assert.Equal(orderBeforeWidthReset, Order(restoredGrid));
                Assert.Equal(visibilityBeforeWidthReset, restoredGrid.Columns.Cast<DataGridViewColumn>().Select(column => column.Visible));
                Assert.True(restoredConfig.EncodeGridColumnWidthsLocked);
                Assert.False(restoredGrid.AllowUserToResizeColumns);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(restored);
                WinFormsTestLifecycle.CloseAndDispose(first);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(isolatedConfigPath);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(restoredConfigPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue column ordering persistence test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void OrderingSurvivesRefreshSelectionInspectorAndResponsiveLayoutWithoutRebuildingPlan()
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
                DataGridView grid = Field<DataGridView>(main, "dgvEncodeQueue");
                SetField(main, "_suppressRowEvents", true);
                DataGridViewRow first = grid.Rows[grid.Rows.Add()];
                DataGridViewRow second = grid.Rows[grid.Rows.Add()];
                SetField(main, "_suppressRowEvents", false);
                first.Cells["colName"].Value = "plan.mkv";
                first.Cells["colStatus"].Value = "Queued";
                second.Cells["colName"].Value = "other.mp4";
                second.Cells["colStatus"].Value = "Queued";
                object meta = Invoke(main, "EnsureRowMeta", first)!;
                EncodingPlan plan = new()
                {
                    IsAvailable = true,
                    Source = new EncodingPlanSource("h264", 1920, 1080, 24, 1200),
                    Video = new EncodingPlanVideo("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
                    Hardware = new EncodingPlanHardware(true, "nvenc", true),
                    Estimates = new EncodingPlanEstimates(null, null, null),
                    SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                        8, "Frozen queue-order test plan.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
                };
                meta.GetType().GetField("IntelligencePlan")!.SetValue(meta, plan);
                first.Selected = true;
                grid.CurrentCell = first.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                Invoke(main, "ScheduleEncodingPlanRefresh");

                Invoke(main, "MoveQueueColumn", "colPlannedOutput", 1);
                string[] customOrder = Order(grid);
                TableLayoutPanel planTable = Field<TableLayoutPanel>(main, "_encodingPlanTable");
                Control retainedControl = planTable.Controls[0];
                DateTime decisionUtc = plan.SizePredictionCalibration!.DecisionUtc!.Value;
                void AssertRetained()
                {
                    Assert.Equal(customOrder, Order(grid));
                    Assert.Same(retainedControl, planTable.Controls[0]);
                    Assert.Equal(decisionUtc, plan.SizePredictionCalibration.DecisionUtc);
                }

                Invoke(main, "RefreshQueueWorkspaceRow", first);
                first.Cells["colProgress"].Value = "35%";
                first.Cells["colETA"].Value = "00:02:00";
                first.Cells["colEstimatedSize"].Value = "5 MB";
                Invoke(main, "RefreshQueueWorkspacePresentation");
                Invoke(main, "RefreshQueueInspectorForRow", first, true);
                AssertRetained();

                first.Selected = false;
                second.Selected = true;
                grid.CurrentCell = second.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                second.Selected = false;
                first.Selected = true;
                grid.CurrentCell = first.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                TabControl tabs = Field<TabControl>(main, "_encodeInfoTabs");
                Invoke(main, "ScheduleEncodingPlanRefresh");
                retainedControl = planTable.Controls[0];
                foreach (TabPage tab in tabs.TabPages)
                {
                    tabs.SelectedTab = tab;
                    Application.DoEvents();
                    AssertRetained();
                }

                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    main.ClientSize = size;
                    main.PerformLayout();
                    grid.PerformLayout();
                    Application.DoEvents();
                    Assert.Equal(size, main.ClientSize);
                    Assert.True(grid.ClientSize.Width > 0 && grid.ClientSize.Height >= 24);
                    AssertRetained();
                }
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue column refresh/plan retention test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void LegacyAndMalformedSavedOrdersNormalizeByStableColumnName()
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
                Config config = Field<Config>(main, "_config");
                config.QueueWorkspaceLayoutInitialized = true;
                config.EncodeGridColumnOrder = ["colETA", "colETA", "removedLegacy", "colName", ""];
                main.Show();
                Application.DoEvents();
                DataGridView grid = Field<DataGridView>(main, "dgvEncodeQueue");
                string[] order = Order(grid);
                Assert.Equal(grid.Columns.Count, order.Length);
                Assert.Equal(order.Length, order.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Equal("colETA", order[0]);
                Assert.Equal("colName", order[1]);
                Assert.DoesNotContain("removedLegacy", order);
                Assert.Equal(order, Field<Config>(main, "_config").EncodeGridColumnOrder);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue column order normalization test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static string[] Order(DataGridView grid) => grid.Columns.Cast<DataGridViewColumn>()
        .OrderBy(column => column.DisplayIndex)
        .Select(column => column.Name)
        .ToArray();

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
