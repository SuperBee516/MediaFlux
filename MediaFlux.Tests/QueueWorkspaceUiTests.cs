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
                    "colEstimatedSize", "colProgress", "colETA"
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
                Assert.Equal("Estimated Output", queue.Columns["colEstimatedSize"].HeaderText);
                Assert.False(queue.Columns.Contains("colSourceEstimate"));
                Assert.Equal(DataGridViewColumnSortMode.Automatic, queue.Columns["colEstimatedSize"].SortMode);
                Assert.True(queue.Columns["colProgress"].Visible);
                Assert.True(queue.Columns["colETA"].Visible);
                Assert.False(queue.Columns["colSize"].Visible);
                Assert.True(queue.Columns["colEstimatedSize"].Visible);
                Assert.False(queue.Columns["colCustom"].Visible);
                Assert.True(Field<Config>(main, "_config").QueueWorkspaceLayoutInitialized);

                FlowLayoutPanel actionPanel = Field<FlowLayoutPanel>(main, "pnlQueueActionButtons");
                foreach (Size size in new[]
                         {
                             new Size(1100, 700), new Size(1000, 720),
                             new Size(1360, 840), new Size(1800, 1100)
                         })
                {
                    main.ClientSize = size;
                    Application.DoEvents();
                    main.PerformLayout();
                    queue.PerformLayout();
                    Assert.Equal(size, main.ClientSize);

                    var visibleCoreColumns = expectedNames.Select(name => queue.Columns[name]).ToArray();
                    int fixedWidth = visibleCoreColumns.Where(column => column.Name != "colName").Sum(column => column.Width);
                    string layoutPath = DescribeControlAncestors(queue);
                    int requiredWidth = fixedWidth + queue.Columns["colName"].MinimumWidth;
                    Assert.True(
                        queue.ClientSize.Width >= requiredWidth,
                        $"Core queue columns should fit at {size}; grid={queue.ClientSize.Width}, required={requiredWidth}; {layoutPath}.");
                    int expectedGridWidth = main.ClientSize.Width - Field<TableLayoutPanel>(main, "tlEncode").Padding.Horizontal;
                    Assert.Equal(expectedGridWidth, queue.ClientSize.Width);
                    Assert.True(queue.Columns["colName"].Width >= queue.Columns["colName"].MinimumWidth);
                    Assert.True(
                        queue.Parent is Control workspace &&
                        queue.ClientSize.Height >= queue.MinimumSize.Height &&
                        queue.Bottom <= workspace.ClientSize.Height,
                        $"Queue grid should remain visible inside its workspace at {size}; grid={queue.Bounds}, workspace={queue.Parent?.ClientSize}.");
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

    [Fact]
    public void LegacyDuplicateEstimateLayoutMigratesToVisibleCanonicalColumnAndPreservesLaterHide()
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
                var config = new Config
                {
                    QueueWorkspaceLayoutInitialized = true,
                    ShowEstimatedOutputColumn = false,
                    EncodeQueueSortColumn = "colSourceEstimate",
                    EncodeQueueSortDescending = true,
                    EncodeGridColumnOrder = ["colPlannedOutput", "colSourceEstimate", "colProgress", "colEstimatedSize"],
                    EncodeGridColumnWidths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["colEstimatedSize"] = 140,
                        ["colSourceEstimate"] = 225
                    }
                };
                SetField(main, "_config", config);
                main.Show();
                Application.DoEvents();

                DataGridView grid = Field<DataGridView>(main, "dgvEncodeQueue");
                Assert.True(config.EstimatedOutputColumnLayoutVersion >= 1);
                Assert.True(config.ShowEstimatedOutputColumn);
                Assert.Equal("colEstimatedSize", config.EncodeQueueSortColumn);
                Assert.True(config.EncodeQueueSortDescending);
                Assert.True(grid.Columns["colEstimatedSize"].Visible);
                Assert.False(grid.Columns.Contains("colSourceEstimate"));
                Assert.Equal(225, grid.Columns["colEstimatedSize"].Width);
                Assert.Single(config.EncodeGridColumnOrder, name => name == "colEstimatedSize");
                Assert.DoesNotContain(config.EncodeGridColumnOrder, name => name == "colSourceEstimate");
                Assert.Equal("colEstimatedSize", config.EncodeGridColumnOrder[
                    config.EncodeGridColumnOrder.IndexOf("colPlannedOutput") + 1]);
                Assert.DoesNotContain(config.EncodeGridColumnWidths.Keys, name => name == "colSourceEstimate");

                config.ShowEstimatedOutputColumn = false;
                Invoke(main, "ApplyQueueWorkspaceColumnPreferences");
                Assert.False(grid.Columns["colEstimatedSize"].Visible);
                Assert.False(config.ShowEstimatedOutputColumn);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Estimated Output layout migration test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void EstimatedOutputPresentationHandlesSavingsStatesAndExistingSizeFormatting()
    {
        MainForm.QueueEstimateCellPresentation smallMb = MainForm.FormatQueueEstimateCellPresentation(
            0, 0.25, "0.3 MB", "estimate details");
        Assert.Equal("0.3 MB", smallMb.FullText);
        Assert.Equal(0, smallMb.SavingsFraction);
        Assert.Contains("Source size is unavailable", smallMb.ToolTipText);

        MainForm.QueueEstimateCellPresentation hundredsMb = MainForm.FormatQueueEstimateCellPresentation(
            1024, 607.25, "607.3 MB", "estimate details");
        Assert.Equal("607.3 MB • 41% saved", hundredsMb.FullText);
        Assert.Equal("607.3 MB • 41%", hundredsMb.MediumText);
        Assert.Equal("41%", hundredsMb.NarrowText);
        Assert.InRange(hundredsMb.SavingsFraction, 0.4069, 0.4071);
        Assert.Contains("Source: 1 GB. Output: 607.3 MB. 41% saved.", hundredsMb.ToolTipText);

        MainForm.QueueEstimateCellPresentation oneGb = MainForm.FormatQueueEstimateCellPresentation(
            2 * 1024, 1.02 * 1024, "1.02 GB", "estimate details");
        Assert.Equal("1.02 GB • 49% saved", oneGb.FullText);

        MainForm.QueueEstimateCellPresentation veryLarge = MainForm.FormatQueueEstimateCellPresentation(
            1_000_000, 750_000, "732.42 GB", "estimate details");
        Assert.Equal("732.42 GB • 25% saved", veryLarge.FullText);

        MainForm.QueueEstimateCellPresentation smallSaving = MainForm.FormatQueueEstimateCellPresentation(
            100, 98, "98 MB", "");
        Assert.Equal("98 MB • 2% saved", smallSaving.FullText);

        MainForm.QueueEstimateCellPresentation subPercentSaving = MainForm.FormatQueueEstimateCellPresentation(
            1000, 999.5, "999.5 MB", "");
        Assert.Equal("999.5 MB • <1% saved", subPercentSaving.FullText);

        MainForm.QueueEstimateCellPresentation largeSaving = MainForm.FormatQueueEstimateCellPresentation(
            100, 20, "20 MB", "");
        Assert.Equal("20 MB • 80% saved", largeSaving.FullText);

        MainForm.QueueEstimateCellPresentation noSavings = MainForm.FormatQueueEstimateCellPresentation(
            842, 842, "842 MB", "");
        Assert.Equal("842 MB • No savings", noSavings.FullText);
        Assert.Equal(0, noSavings.SavingsFraction);

        MainForm.QueueEstimateCellPresentation largerOutput = MainForm.FormatQueueEstimateCellPresentation(
            1000, 1080, "1.05 GB", "");
        Assert.Equal("1.05 GB • 8% larger", largerOutput.FullText);
        Assert.Equal("1.05 GB • +8%", largerOutput.MediumText);
        Assert.Equal("+8%", largerOutput.NarrowText);
        Assert.Equal(0, largerOutput.SavingsFraction);

        MainForm.QueueEstimateCellPresentation pending = MainForm.FormatQueueEstimateCellPresentation(
            0, 0, "Estimating…", "");
        Assert.Equal("Estimating…", pending.FullText);
        Assert.False(pending.HasNumericOutput);

        MainForm.QueueEstimateCellPresentation unavailable = MainForm.FormatQueueEstimateCellPresentation(
            0, 0, "Metadata unavailable", "");
        Assert.Equal("Unavailable", unavailable.FullText);
        Assert.Equal("Estimated output is not available yet.", unavailable.ToolTipText);

        MainForm.QueueEstimateCellPresentation noSource = MainForm.FormatQueueEstimateCellPresentation(
            0, 6, "6 MB", "");
        Assert.Equal("6 MB", noSource.FullText);
        Assert.Contains("Source size is unavailable", noSource.ToolTipText);
    }

    [Fact]
    public void EstimatedOutputSavingsUseByteBackedValuesBeforeFormattingPainterText()
    {
        if (!OperatingSystem.IsWindows())
            return;

        (long SourceBytes, long EstimateBytes, string ExpectedRelation)[] cases =
        [
            (541_100_000, 464_100_000, "14% saved"),
            (586_800_000, 224_000_000, "62% saved"),
            (200_000_000, 15_000_000, "93% saved"),
            (1_000_000_000, 500_000_000, "50% saved"),
            (100_000_000, 100_000_000, "No savings"),
            (100_000_000, 120_000_000, "20% larger")
        ];
        MethodInfo formatSize = typeof(MainForm).GetMethod(
            "FormatSize", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(double)], null)
            ?? throw new MissingMethodException(typeof(MainForm).FullName, "FormatSize(double)");
        MethodInfo paintTextSelector = typeof(MainForm).GetMethod(
            "SelectQueueEstimateCellText", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MainForm).FullName, "SelectQueueEstimateCellText");

        using var bitmap = new Bitmap(1200, 80);
        using Graphics graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 9f);
        foreach ((long sourceBytes, long estimateBytes, string expectedRelation) in cases)
        {
            double sourceMb = sourceBytes / (1024d * 1024d);
            double estimateMb = estimateBytes / (1024d * 1024d);
            double expectedFraction = (sourceBytes - estimateBytes) / (double)sourceBytes;
            MainForm.QueueEstimateCellPresentation presentation = MainForm.FormatQueueEstimateCellPresentation(
                sourceMb,
                estimateMb,
                (string)formatSize.Invoke(null, [estimateMb])!,
                "test estimate basis");

            Assert.Equal(expectedFraction > 0 ? expectedFraction : 0, presentation.SavingsFraction, 12);
            Assert.Contains(expectedRelation, presentation.FullText, StringComparison.Ordinal);

            string sourceDisplay = (string)formatSize.Invoke(null, [sourceMb])!;
            string estimateDisplay = (string)formatSize.Invoke(null, [estimateMb])!;
            Assert.Contains($"Source: {sourceDisplay}. Output: {estimateDisplay}.", presentation.ToolTipText, StringComparison.Ordinal);

            string painterText = (string)paintTextSelector.Invoke(
                null,
                [presentation, graphics, font, 1200])!;
            Assert.Equal(presentation.FullText, painterText);
            Assert.Contains(expectedRelation, painterText, StringComparison.Ordinal);
        }

        long displayThresholdSourceBytes = (long)Math.Round(999.9501d * 1024d * 1024d);
        long displayThresholdEstimateBytes = (long)Math.Round(495.0499d * 1024d * 1024d);
        double preciseSourceMb = displayThresholdSourceBytes / (1024d * 1024d);
        double preciseEstimateMb = displayThresholdEstimateBytes / (1024d * 1024d);
        string roundedSource = (string)formatSize.Invoke(null, [preciseSourceMb])!;
        string roundedEstimate = (string)formatSize.Invoke(null, [preciseEstimateMb])!;
        MainForm.QueueEstimateCellPresentation precisePresentation = MainForm.FormatQueueEstimateCellPresentation(
            preciseSourceMb,
            preciseEstimateMb,
            roundedEstimate,
            "test estimate basis");
        double precisePercent = (displayThresholdSourceBytes - displayThresholdEstimateBytes) * 100d /
                               displayThresholdSourceBytes;
        double displayTextPercent = (1000d - 495d) * 100d / 1000d;

        Assert.Equal("1000 MB", roundedSource);
        Assert.Equal("495 MB", roundedEstimate);
        Assert.InRange(precisePercent, 50.49d, 50.5d);
        Assert.Equal(50, Math.Round(precisePercent, MidpointRounding.AwayFromZero));
        Assert.Equal(51, Math.Round(displayTextPercent, MidpointRounding.AwayFromZero));
        Assert.Contains("50% saved", precisePresentation.FullText, StringComparison.Ordinal);
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

    private static string DescribeControlAncestors(Control control)
    {
        var parts = new List<string>();
        for (Control? current = control; current != null; current = current.Parent)
        {
            string details = current is SplitContainer split
                ? $" orientation={split.Orientation} distance={split.SplitterDistance} splitter={split.SplitterWidth} " +
                  $"panel1={split.Panel1.Bounds} panel2={split.Panel2.Bounds} panelMins={split.Panel1MinSize}/{split.Panel2MinSize}"
                : string.Empty;
            parts.Add($"{current.Name}:{current.GetType().Name} bounds={current.Bounds} client={current.ClientSize} " +
                      $"dock={current.Dock} margin={current.Margin} padding={current.Padding} min={current.MinimumSize}{details}");
        }

        return string.Join(" <- ", parts);
    }

    private static void AssertCompactWorkspaceLayout(MainForm main)
    {
        TableLayoutPanel controls = Field<TableLayoutPanel>(main, "pnlQueueControlsCard");
        TableLayoutPanel encodingControls = Field<TableLayoutPanel>(main, "pnlEncodingProfileCard");
        int maxCardHeight = (int)Math.Ceiling(190d * main.DeviceDpi / 96d);
        int[] queueRows = controls.GetRowHeights();
        int contentHeight = controls.RowCount == 7
            ? queueRows.Skip(1).Take(5).Sum() + controls.Padding.Vertical
            : queueRows.Sum() + controls.Padding.Vertical;
        Assert.True(contentHeight <= maxCardHeight,
            $"Queue Controls content should remain compact; content height={contentHeight}, maximum={maxCardHeight}, card={controls.Bounds}, rows={string.Join(",", queueRows)}, client={main.ClientSize}.");
        if (controls.RowCount == 7)
        {
            Assert.False(controls.AutoSize);
            Assert.Equal(DockStyle.Fill, controls.Dock);
            Assert.InRange(Math.Abs(controls.Height - encodingControls.Height), 0, 2);
        }
        else
        {
            Assert.True(controls.AutoSize);
            Assert.True(controls.Height <= maxCardHeight,
                $"Stacked Queue Controls should retain its natural height; card height={controls.Height}, maximum={maxCardHeight}, client={main.ClientSize}.");
        }

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
