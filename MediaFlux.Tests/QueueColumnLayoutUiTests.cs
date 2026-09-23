using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueColumnLayoutUiTests
{
    [Fact]
    public void ResizeLockAndPersistenceKeepUserWidthsAcrossRestart()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        MainForm? first = null;
        MainForm? restored = null;
        string? configPath = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                first = new MainForm();
                configPath = QueueWorkspaceTestSupport.UseIsolatedConfig(first);
                first.Show();
                Application.DoEvents();

                DataGridView grid = Field<DataGridView>(first, "dgvEncodeQueue");
                Config config = Field<Config>(first, "_config");
                Assert.True(grid.AllowUserToResizeColumns);
                Assert.Equal(DataGridViewTriState.True, grid.Columns["colStatus"].Resizable);

                DataGridViewColumn statusColumn = grid.Columns["colStatus"];
                Rectangle statusBounds = grid.GetColumnDisplayRectangle(statusColumn.Index, false);
                Invoke(first, "DgvEncodeQueue_ColumnResizeMouseDown", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, statusBounds.Right - 1, grid.ColumnHeadersHeight / 2, 0));
                Assert.True((bool)FieldValue(first, "_queueColumnResizeGesture"));
                int startingWidth = statusColumn.Width;
                statusColumn.Width = startingWidth + 27;
                Assert.True(config.EncodeGridColumnWidthsCustomized);
                Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column =>
                    Assert.Equal(DataGridViewAutoSizeColumnMode.None, column.AutoSizeMode));
                Invoke(first, "DgvEncodeQueue_ColumnResizeMouseUp", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                int chosenWidth = grid.Columns["colStatus"].Width;
                Assert.Equal(chosenWidth, Config.Load(configPath!).EncodeGridColumnWidths["colStatus"]);

                int[] beforeLock = grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Width).ToArray();
                config.EncodeGridColumnWidthsLocked = true;
                Invoke(first, "ApplyQueueColumnResizeLock");
                Assert.False(grid.AllowUserToResizeColumns);
                Assert.All(grid.Columns.Cast<DataGridViewColumn>(), column =>
                    Assert.Equal(DataGridViewTriState.False, column.Resizable));
                Assert.Equal(beforeLock, grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Width));
                Invoke(first, "DgvEncodeQueue_ColumnResizeMouseDown", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, statusBounds.Right - 1, grid.ColumnHeadersHeight / 2, 0));
                Assert.False((bool)FieldValue(first, "_queueColumnResizeGesture"));

                config.EncodeGridColumnWidthsLocked = false;
                Invoke(first, "ApplyQueueColumnResizeLock");
                Assert.True(grid.AllowUserToResizeColumns);
                Assert.Equal(chosenWidth, grid.Columns["colStatus"].Width);
                config.Save(configPath!);

                restored = new MainForm();
                QueueWorkspaceTestSupport.UseIsolatedConfig(restored);
                SetField(restored, "_config", Config.Load(configPath!));
                restored.Show();
                Application.DoEvents();
                DataGridView restoredGrid = Field<DataGridView>(restored, "dgvEncodeQueue");
                Assert.Equal(chosenWidth, restoredGrid.Columns["colStatus"].Width);
                Assert.True(restoredGrid.AllowUserToResizeColumns);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(restored);
                WinFormsTestLifecycle.CloseAndDispose(first);
                QueueWorkspaceTestSupport.DeleteIsolatedConfig(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue column persistence test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void ResetRestoresResponsiveDefaultsWithoutChangingOtherQueueState()
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
                DataGridViewRow row = AddQueueRow(main, grid, "reset.mkv");
                row.Selected = true;
                grid.CurrentCell = row.Cells["colName"];
                Invoke(main, "RefreshQueueInspectorFromSelection");
                var tabs = Field<TabControl>(main, "_encodeInfoTabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Plan & Analysis");

                grid.Columns["colStatus"].DisplayIndex = grid.Columns.Count - 1;
                grid.Columns["colCreated"].Visible = true;
                row.Selected = true;
                grid.Columns["colStatus"].Width += 35;
                SetField(main, "_queueColumnResizeGesture", true);
                Invoke(main, "HandleQueueColumnWidthChanged", grid.Columns["colStatus"]);
                Invoke(main, "DgvEncodeQueue_ColumnResizeMouseUp", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                string[] orderBeforeReset = grid.Columns.Cast<DataGridViewColumn>()
                    .OrderBy(column => column.DisplayIndex).Select(column => column.Name).ToArray();
                bool[] visibilityBeforeReset = grid.Columns.Cast<DataGridViewColumn>()
                    .Select(column => column.Visible).ToArray();

                Config config = Field<Config>(main, "_config");
                config.EncodeGridColumnWidthsLocked = true;
                Invoke(main, "ResetQueueColumnWidths");
                Assert.False(config.EncodeGridColumnWidthsCustomized);
                Assert.True(config.EncodeGridColumnWidthsLocked);
                Assert.False(grid.AllowUserToResizeColumns);
                Assert.Equal(visibilityBeforeReset, grid.Columns.Cast<DataGridViewColumn>().Select(column => column.Visible));
                Assert.Equal(orderBeforeReset, grid.Columns.Cast<DataGridViewColumn>().OrderBy(column => column.DisplayIndex).Select(column => column.Name));
                Assert.True(row.Selected);
                Assert.Equal("Plan & Analysis", tabs.SelectedTab?.Text);
                Assert.Equal(DataGridViewAutoSizeColumnMode.Fill, grid.Columns["colName"].AutoSizeMode);
                Assert.Equal(ScaleUi(main, 86), grid.Columns["colStatus"].Width);

                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    main.ClientSize = size;
                    main.PerformLayout();
                    grid.PerformLayout();
                    Application.DoEvents();
                    Assert.Equal(size, main.ClientSize);
                    Assert.True(grid.ClientSize.Width > 0 && grid.ClientSize.Height >= 24);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue column reset test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void InvalidWidthsClampOrIgnoreAndRefreshDoesNotRegressPlanOrCustomWidths()
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
                Config config = Field<Config>(main, "_config");
                config.EncodeGridColumnWidthsCustomized = true;
                config.EncodeGridColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["colStatus"] = 10,
                    ["colProgress"] = -1,
                    ["colETA"] = 90_000,
                    ["removedLegacyColumn"] = 700
                };
                Invoke(main, "ApplyQueueColumnLayoutPreferences");
                Assert.Equal(ScaleUi(main, 64), grid.Columns["colStatus"].Width);
                Assert.Equal(ScaleUi(main, 1600), grid.Columns["colETA"].Width);
                Assert.Equal(ScaleUi(main, 72), grid.Columns["colProgress"].Width);

                DataGridViewRow row = AddQueueRow(main, grid, "plan.mkv");
                EncodingPlan plan = new()
                {
                    IsAvailable = true,
                    Source = new EncodingPlanSource("h264", 1920, 1080, 24, 1200),
                    Video = new EncodingPlanVideo("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
                    Hardware = new EncodingPlanHardware(true, "nvenc", true),
                    Estimates = new EncodingPlanEstimates(null, null, null),
                    SizePredictionCalibration = EncodingSizePredictionCalibration.Unavailable(
                        8, "Frozen width test plan.", "PredictionCalibrationPolicyV1", DateTime.UnixEpoch)
                };
                object meta = Invoke(main, "EnsureRowMeta", row)!;
                meta.GetType().GetField("IntelligencePlan")!.SetValue(meta, plan);
                row.Selected = true;
                grid.CurrentCell = row.Cells["colName"];
                Invoke(main, "ScheduleEncodingPlanRefresh");
                TableLayoutPanel planTable = Field<TableLayoutPanel>(main, "_encodingPlanTable");
                Control retainedPlanControl = planTable.Controls[0];
                DateTime decisionUtc = plan.SizePredictionCalibration!.DecisionUtc!.Value;

                SetField(main, "_queueColumnResizeGesture", true);
                grid.Columns["colPlannedOutput"].Width += 24;
                Invoke(main, "DgvEncodeQueue_ColumnResizeMouseUp", grid,
                    new MouseEventArgs(MouseButtons.Left, 1, 0, 0, 0));
                int customizedWidth = grid.Columns["colPlannedOutput"].Width;
                Invoke(main, "RefreshQueueWorkspaceRow", row);
                row.Cells["colProgress"].Value = "51%";
                Invoke(main, "RefreshQueueInspectorForRow", row, true);
                Assert.Equal(customizedWidth, grid.Columns["colPlannedOutput"].Width);
                Assert.Same(retainedPlanControl, planTable.Controls[0]);
                Assert.Equal(decisionUtc, plan.SizePredictionCalibration.DecisionUtc);

                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    main.ClientSize = size;
                    main.PerformLayout();
                    grid.PerformLayout();
                    Application.DoEvents();
                    Assert.Equal(size, main.ClientSize);
                    Assert.Equal(customizedWidth, grid.Columns["colPlannedOutput"].Width);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Queue column refresh test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static DataGridViewRow AddQueueRow(MainForm main, DataGridView grid, string filename)
    {
        SetField(main, "_suppressRowEvents", true);
        try
        {
            DataGridViewRow row = grid.Rows[grid.Rows.Add()];
            row.Cells["colName"].Value = filename;
            row.Cells["colStatus"].Value = "Queued";
            Invoke(main, "EnsureRowMeta", row);
            return row;
        }
        finally { SetField(main, "_suppressRowEvents", false); }
    }

    private static int ScaleUi(MainForm main, int value) =>
        (int)Math.Round(value * Math.Max(1d, main.DeviceDpi / 96d));

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static void SetField(MainForm main, string name, object value) =>
        (typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(name)).SetValue(main, value);

    private static object FieldValue(MainForm main, string name) =>
        typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
        ?? throw new MissingFieldException(name);

    private static object? Invoke(MainForm main, string name, params object?[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == args.Length)
            ?? throw new MissingMethodException(name);
        return method.Invoke(main, args);
    }
}
