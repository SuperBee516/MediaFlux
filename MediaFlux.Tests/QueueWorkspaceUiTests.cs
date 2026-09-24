using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using System.Collections.Concurrent;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class QueueWorkspaceUiTests
{
    [Fact]
    public void SummaryProjectionCountsReadyRunningAndAttentionWithoutDoubleCounting()
    {
        MainForm.QueueWorkspaceCounts counts = MainForm.CountQueueWorkspaceItems(
        [
            ("Queued", false, false),
            ("Estimating", false, false),
            ("Encoding", true, true),
            ("Failed — Source damaged", false, false),
            ("Queued", false, true),
            ("Excluded - exact duplicate", false, false),
            ("Reading metadata", false, false),
            ("Retry Queued", false, false),
            ("Checking codec", false, false)
        ]);

        Assert.Equal(new MainForm.QueueWorkspaceCounts(Total: 9, Ready: 2, Running: 1, Attention: 3), counts);
    }

    [Fact]
    public void QueueControlsAndStatusCardsStayCompactAcrossRestoredMaximizedAndResizedLayouts()
    {
        if (!OperatingSystem.IsWindows()) return;

        string configPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueLayout.{Guid.NewGuid():N}.json");
        Rectangle workingArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        var savedBounds = new Rectangle(workingArea.Left + 20, workingArea.Top + 20, 1100, 760);
        new Config
        {
            MainWindowX = savedBounds.X,
            MainWindowY = savedBounds.Y,
            MainWindowWidth = savedBounds.Width,
            MainWindowHeight = savedBounds.Height
        }.Save(configPath);

        Exception? failure = null;
        MainForm? main = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                main = new MainForm(configPath) { StartPosition = FormStartPosition.Manual };
                main.Show();
                Application.DoEvents();

                Assert.Equal(savedBounds.Size, main.Bounds.Size);
                AssertCompactWorkspaceLayout(main);

                main.WindowState = FormWindowState.Maximized;
                Application.DoEvents();
                AssertCompactWorkspaceLayout(main);

                main.WindowState = FormWindowState.Normal;
                Application.DoEvents();
                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    main.ClientSize = size;
                    Application.DoEvents();
                    main.PerformLayout();
                    AssertCompactWorkspaceLayout(main);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                WinFormsTestLifecycle.CloseAndDispose(main);
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue workspace layout test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void QueueWorkspaceKeepsGlobalCountsAndActionsStableAcrossSupportedSizes()
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
                main.StartPosition = FormStartPosition.Manual;
                main.Show();
                Application.DoEvents();

                DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
                FieldInfo suppressEvents = typeof(MainForm).GetField("_suppressRowEvents", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException("_suppressRowEvents");
                suppressEvents.SetValue(main, true);
                DataGridViewRow ready = queue.Rows[queue.Rows.Add()];
                DataGridViewRow failed = queue.Rows[queue.Rows.Add()];
                DataGridViewRow running = queue.Rows[queue.Rows.Add()];
                DataGridViewRow hiddenReview = queue.Rows[queue.Rows.Add()];
                suppressEvents.SetValue(main, false);

                SetRow(ready, "ready.mkv", "Queued");
                SetRecommendation(main, ready, SmartEncodeRecommendationKind.StrongCandidate);
                SetRow(failed, "failed.mkv", "Failed");
                SetRow(running, "running.mkv", "Encoding");
                SetRow(hiddenReview, "review.mkv", "Queued");
                SetRecommendation(main, hiddenReview, SmartEncodeRecommendationKind.Review);
                hiddenReview.Visible = false;
                queue.ClearSelection();

                var runningJobs = Field<ConcurrentDictionary<DataGridViewRow, string>>(main, "_runningEncodeJobs");
                runningJobs[running] = "running.mkv";
                Invoke(main, "RefreshQueueWorkspacePresentation");

                Assert.Equal("4", Field<Label>(main, "_queueWorkspaceTotalValue").Text);
                Assert.Equal("1", Field<Label>(main, "_queueWorkspaceReadyValue").Text);
                Assert.Equal("1", Field<Label>(main, "_queueWorkspaceRunningValue").Text);
                Assert.Equal("2", Field<Label>(main, "_queueWorkspaceAttentionValue").Text);
                Assert.False(hiddenReview.Visible);

                runningJobs.TryRemove(running, out _);
                Invoke(main, "RefreshQueueWorkspacePresentation");
                Button startQueue = Field<Button>(main, "btnStartEncode");
                Button startSelected = Field<Button>(main, "_btnStartSelectedQueue");
                Button removeSelected = Field<Button>(main, "_btnRemoveSelectedQueue");
                Button pause = Field<Button>(main, "btnPauseQueue");
                Button stop = Field<Button>(main, "btnStopEncode");
                ToolStripButton analyze = Field<ToolStripButton>(main, "_analyzeQueueButton");

                Assert.True(startQueue.Enabled);
                Assert.False(startSelected.Enabled);
                Assert.False(removeSelected.Enabled);
                Assert.False(pause.Enabled);
                Assert.False(stop.Enabled);
                Assert.True(analyze.Enabled);

                ready.Selected = true;
                queue.CurrentCell = ready.Cells["colName"];
                Invoke(main, "UpdateQueueWorkspaceActionState");
                Assert.True(startSelected.Enabled);
                Assert.True(removeSelected.Enabled);

                SetField(main, "_encodingActive", true);
                Invoke(main, "UpdateQueueWorkspaceActionState");
                Assert.False(startQueue.Enabled);
                Assert.False(startSelected.Enabled);
                Assert.False(removeSelected.Enabled);
                Assert.True(pause.Enabled);
                Assert.True(stop.Enabled);
                Assert.False(analyze.Enabled);

                SetField(main, "_encodeQueuePaused", true);
                Invoke(main, "UpdateQueueWorkspaceActionState");
                Assert.Equal("Resume Queue", pause.Text);
                Assert.Contains("FFmpeg processes already running continue", pause.AccessibleDescription, StringComparison.OrdinalIgnoreCase);
                SetField(main, "_encodeQueuePaused", false);
                SetField(main, "_encodingActive", false);
                Invoke(main, "UpdateQueueWorkspaceActionState");

                string[] expectedNames =
                [
                    "colOrder", "colName", "colStatus", "colEncodeRecommendation", "colPlannedOutput",
                    "colSourceEstimate", "colProgress", "colETA"
                ];
                string[] actualNames = queue.Columns.Cast<DataGridViewColumn>()
                    .Where(column => column.Visible)
                    .OrderBy(column => column.DisplayIndex)
                    .Select(column => column.Name)
                    .ToArray();
                Assert.Equal(expectedNames, actualNames);
                Assert.Equal("File", queue.Columns["colName"].HeaderText);
                Assert.Equal("Recommendation", queue.Columns["colEncodeRecommendation"].HeaderText);
                Assert.Equal("Planned Output", queue.Columns["colPlannedOutput"].HeaderText);
                Assert.Equal("Source Size → Estimate", queue.Columns["colSourceEstimate"].HeaderText);
                Assert.True(queue.Columns["colProgress"].Visible);
                Assert.True(queue.Columns["colETA"].Visible);
                Assert.False(queue.Columns["colSize"].Visible);
                Assert.False(queue.Columns["colEstimatedSize"].Visible);
                Assert.False(queue.Columns["colCustom"].Visible);
                Assert.True(Field<Config>(main, "_config").QueueWorkspaceLayoutInitialized);

                FlowLayoutPanel actionPanel = Field<FlowLayoutPanel>(main, "pnlQueueActionButtons");
                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    main.ClientSize = size;
                    Application.DoEvents();
                    main.PerformLayout();
                    queue.PerformLayout();
                    Assert.Equal(size, main.ClientSize);

                    var visibleCoreColumns = expectedNames.Select(name => queue.Columns[name]).ToArray();
                    int fixedWidth = visibleCoreColumns.Where(column => column.Name != "colName").Sum(column => column.Width);
                    Assert.True(
                        queue.ClientSize.Width >= fixedWidth + queue.Columns["colName"].MinimumWidth,
                        $"Core queue columns should fit at {size}; grid={queue.ClientSize.Width}, required={fixedWidth + queue.Columns["colName"].MinimumWidth}.");
                    Assert.True(queue.Columns["colName"].Width >= queue.Columns["colName"].MinimumWidth);
                    Assert.True(queue.ClientSize.Height >= 24, $"Queue grid should retain usable height at {size}.");
                    Assert.All(new[] { startQueue, startSelected, removeSelected, pause, stop }, button => Assert.True(button.Visible));
                    Assert.True(analyze.Visible);
                    Assert.True(actionPanel.ClientSize.Width > 0);
                    Assert.Null(queue.SortedColumn);
                }

                Assert.True(Field<Config>(main, "_config").QueueWorkspaceLayoutInitialized);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "Queue workspace UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void SetRow(DataGridViewRow row, string path, string status)
    {
        row.Tag = path;
        row.Cells["colName"].Value = Path.GetFileName(path);
        row.Cells["colStatus"].Value = status;
    }

    private static void SetRecommendation(MainForm main, DataGridViewRow row, SmartEncodeRecommendationKind kind)
    {
        object meta = Invoke(main, "EnsureRowMeta", row)!;
        var recommendation = new SmartEncodeRecommendation
        {
            Kind = kind,
            Confidence = SmartEncodeConfidence.High,
            PrimaryReason = "Queue workspace test recommendation."
        };
        meta.GetType().GetField("EncodeRecommendation", BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(meta, recommendation);
        row.Cells["colEncodeRecommendation"].Value = recommendation.DisplayName;
    }

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static void AssertCompactWorkspaceLayout(MainForm main)
    {
        TableLayoutPanel controls = Field<TableLayoutPanel>(main, "pnlQueueControlsCard");
        int maxCardHeight = (int)Math.Ceiling(190d * main.DeviceDpi / 96d);
        Assert.True(controls.Height <= maxCardHeight,
            $"Queue Controls should remain compact; card height={controls.Height}, maximum={maxCardHeight}, client={main.ClientSize}.");

        TableLayoutPanel summary = main.Controls.Find("queueWorkspaceSummaryCards", searchAllChildren: true)
            .OfType<TableLayoutPanel>()
            .Single();
        TableLayoutPanel workspace = Field<TableLayoutPanel>(main, "_queueWorkspaceHost");
        TableLayoutPanel[] cards = summary.Controls.OfType<TableLayoutPanel>().ToArray();
        Assert.Equal(4, cards.Length);
        Assert.All(cards, card => Assert.Equal(0, card.Padding.Vertical));
        Assert.All(cards, card => Assert.Equal(1, card.RowCount));
        Assert.Equal(DockStyle.Fill, summary.Dock);
        Assert.False(summary.AutoSize);
        int compactStripHeight = (int)Math.Round(64d * main.DeviceDpi / 96d);
        int compactStripMinimum = (int)Math.Round(60d * main.DeviceDpi / 96d);
        int compactStripMaximum = (int)Math.Round(70d * main.DeviceDpi / 96d);
        RowStyle statusRow = workspace.RowStyles[0];
        int[] hostRows = workspace.GetRowHeights();
        Assert.Equal(SizeType.Absolute, statusRow.SizeType);
        Assert.Equal((float)compactStripHeight, statusRow.Height);
        Assert.InRange(hostRows[0], compactStripMinimum, compactStripMaximum);
        Assert.True(summary.Height <= compactStripHeight,
            $"Status card strip should stay compact; height={summary.Height}, allocated row={hostRows[0]}, maximum={compactStripHeight}, dpi={main.DeviceDpi}, client={main.ClientSize}.");
        Assert.Equal(hostRows[0] - summary.Margin.Vertical, summary.Height);
        Assert.True(hostRows[0] <= compactStripHeight + (int)Math.Round(4d * main.DeviceDpi / 96d),
            $"Status card row should stay compact; height={hostRows[0]}, strip={summary.Height}, maximum={compactStripHeight}, dpi={main.DeviceDpi}, client={main.ClientSize}.");
        Assert.Equal(Enumerable.Range(0, 4), cards.Select(card => summary.GetColumn(card)));
        Assert.All(cards, card => Assert.Equal(0, summary.GetRow(card)));
        Assert.All(cards, card => Assert.Equal(summary.ClientSize.Height, card.Height));
        Assert.All(cards, card =>
        {
            Label[] contents = card.Controls.OfType<Label>().ToArray();
            Assert.Equal(2, contents.Length);
            int cardCenter = card.ClientSize.Height / 2;
            Assert.All(contents, label =>
                Assert.InRange(Math.Abs(label.Top + (label.Height / 2) - cardCenter), 0, 1));
        });

        DataGridView queue = Field<DataGridView>(main, "dgvEncodeQueue");
        Assert.Equal(DockStyle.Fill, queue.Dock);
        Assert.Equal(Math.Max(hostRows[^1], queue.MinimumSize.Height), queue.Height);
        Assert.Equal(hostRows.Take(3).Sum(), queue.Top);
        Assert.True(queue.Bottom >= workspace.ClientSize.Height - 1,
            $"Queue grid should fill the remaining workspace; gridBottom={queue.Bottom}, workspaceHeight={workspace.ClientSize.Height}, client={main.ClientSize}.");
    }

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

internal static class QueueWorkspaceTestSupport
{
    public static string UseIsolatedConfig(MainForm main)
    {
        string path = Path.Combine(Path.GetTempPath(), $"MediaFlux.QueueWorkspace.{Guid.NewGuid():N}.json");
        Type formType = typeof(MainForm);
        (formType.GetField("_configPath", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("_configPath")).SetValue(main, path);
        (formType.GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("_config")).SetValue(main, new Config());
        return path;
    }

    public static void DeleteIsolatedConfig(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            File.Delete(path);
    }
}
