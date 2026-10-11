using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;
using Xunit.Abstractions;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class UpperControlLayoutUiTests
{
    private readonly ITestOutputHelper _output;

    public UpperControlLayoutUiTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void QueueAndEncodingControlsRehostExistingEditorsAndRemainResponsive()
    {
        if (!OperatingSystem.IsWindows()) return;

        string configPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.UpperControls.{Guid.NewGuid():N}.json");
        new Config
        {
            RememberCheckboxStates = false,
            LastQualityMode = "Automatic",
            LastQualityTarget = nameof(QualityTarget.Balanced),
            LastQualityValue = 26,
            LastChkProcessAll = true
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

                TableLayoutPanel queueCard = Field<TableLayoutPanel>(main, "pnlQueueControlsCard");
                TableLayoutPanel encodingCard = Field<TableLayoutPanel>(main, "pnlEncodingProfileCard");
                Panel upper = Field<Panel>(main, "_queueEncodingControlsViewport");
                TableLayoutPanel cardsLayout = Field<TableLayoutPanel>(main, "_queueEncodingControlsLayout");
                TableLayoutPanel fields = Field<TableLayoutPanel>(main, "tlEncodingProfileFields");
                FlowLayoutPanel queueBehavior = Field<FlowLayoutPanel>(main, "pnlQueueBehavior");
                FlowLayoutPanel primaryActions = Field<FlowLayoutPanel>(main, "pnlQueueActionButtons");
                FlowLayoutPanel secondaryActions = Field<FlowLayoutPanel>(main, "pnlQueueSecondaryActions");
                FlowLayoutPanel comparisons = Field<FlowLayoutPanel>(main, "pnlQueueComparisonActions");

                Assert.Single(main.Controls.Find("pnlQueueControlsCard", true));
                Assert.Single(main.Controls.Find("pnlEncodingControlsCard", true));
                Assert.Same(cardsLayout, queueCard.Parent);
                Assert.Same(cardsLayout, encodingCard.Parent);
                Assert.Same(upper, cardsLayout.Parent);
                Assert.Same(encodingCard, fields.Parent);
                Assert.Equal("Queue Controls", queueCard.Controls.OfType<Label>().First().Text);
                Assert.Equal("Encoding Controls", encodingCard.Controls.OfType<Label>().First().Text);

                CheckBox processAll = Field<CheckBox>(main, "chkProcessAll");
                CheckBox includeSubfolders = Field<CheckBox>(main, "chkIncludeSubfolders");
                CheckBox deleteSource = Field<CheckBox>(main, "chkDeleteSource");
                Assert.Same(queueBehavior, processAll.Parent);
                Assert.DoesNotContain(processAll, Descendants(Field<TableLayoutPanel>(main, "grpOptions")));
                Assert.Equal(new[] { deleteSource, includeSubfolders, processAll }, queueBehavior.Controls.OfType<CheckBox>().ToArray());
                Assert.True(processAll.Checked);

                ComboBox mode = Field<ComboBox>(main, "comboQualityMode");
                ComboBox encoder = Field<ComboBox>(main, "comboEncoderMode");
                ComboBox format = Field<ComboBox>(main, "comboVideoFormat");
                ComboBox estimateProfile = Field<ComboBox>(main, "comboCompressionProfile");
                ComboBox speed = Field<ComboBox>(main, "comboEncoderPreset");
                QualityTargetSegmentSelector automaticQuality = Field<QualityTargetSegmentSelector>(main, "qualityTargetSelector");
                NumericUpDown manualQuality = Field<NumericUpDown>(main, "nudAutoQuality");
                TextBox targetSize = Field<TextBox>(main, "txtTargetSize");
                CheckBox autoTarget = Field<CheckBox>(main, "chkAutoTargetSize");
                Label qualityPreference = Field<Label>(main, "lblQualityTargetValue");
                Label qualityDescription = Field<Label>(main, "lblQualityTargetDescription");
                ToolTip qualityToolTip = Field<ToolTip>(main, "_uiToolTip");
                Label activeProfile = Field<Label>(main, "_activeConfigurationSummaryLabel");
                ToolTip activeProfileToolTip = Field<ToolTip>(main, "_activeConfigurationSummaryToolTip");
                DataGridView queueGrid = Field<DataGridView>(main, "dgvEncodeQueue");
                SplitContainer queueSplit = Field<SplitContainer>(main, "_encodeQueueSplit");

                Assert.Same(fields, mode.Parent);
                Assert.Same(fields, encoder.Parent);
                Assert.Same(fields, format.Parent);
                Assert.Same(fields, estimateProfile.Parent);
                Assert.Same(fields, speed.Parent);
                Assert.Same(fields, targetSize.Parent);
                Assert.Same(fields, autoTarget.Parent);
                Assert.Contains(activeProfile, Descendants(encodingCard));
                Assert.True(autoTarget.Checked);
                Assert.False(targetSize.Enabled);
                Assert.Equal("Automatic • Source Adaptive", mode.Text);
                Assert.True(automaticQuality.Visible);
                Assert.False(manualQuality.Visible);
                Assert.False(estimateProfile.Visible);
                Assert.Equal(QualityTarget.Balanced, automaticQuality.SelectedTarget);
                Assert.Equal(new[] { "Smaller", "Efficient", "Balanced", "High", "Maximum" },
                    automaticQuality.Segments.Select(segment => segment.Text).ToArray());
                Assert.Equal(new[] { "Smaller File", "Efficient", "Balanced", "High Quality", "Maximum Quality" },
                    automaticQuality.Segments.Select(segment => segment.AccessibleName).ToArray());
                Assert.All(automaticQuality.Segments, segment =>
                {
                    Assert.Equal(AccessibleRole.RadioButton, segment.AccessibleRole);
                    Assert.False(string.IsNullOrWhiteSpace(segment.AccessibleDescription));
                    Assert.False(string.IsNullOrWhiteSpace(qualityToolTip.GetToolTip(segment)));
                });
                Assert.Equal(26m, manualQuality.Value);
                Assert.Equal(12m, manualQuality.Minimum);
                Assert.Equal(35m, manualQuality.Maximum);
                Assert.Equal(1m, manualQuality.Increment);
                DataGridViewColumn estimatedOutputColumn = queueGrid.Columns["colEstimatedSize"]!;
                Assert.Equal("Estimated Output", estimatedOutputColumn.HeaderText);
                Assert.True(estimatedOutputColumn.Visible);
                Assert.Equal(DataGridViewColumnSortMode.Automatic, estimatedOutputColumn.SortMode);

                autoTarget.Checked = false;
                Assert.True(targetSize.Enabled);
                autoTarget.Checked = true;
                Assert.False(targetSize.Enabled);

                Assert.Single(main.Controls.Find("comboQualityMode", true));
                Assert.Single(Descendants(main), control => ReferenceEquals(control, encoder));
                Assert.Single(Descendants(main), control => ReferenceEquals(control, format));
                Assert.Single(Descendants(main), control => ReferenceEquals(control, estimateProfile));
                Assert.Single(main.Controls.Find("comboEncoderPreset", true));
                Assert.Single(Descendants(main), control => ReferenceEquals(control, automaticQuality));
                Assert.Empty(main.Controls.Find("trkQualityTarget", true));
                Assert.Null(typeof(MainForm).GetField("trkQualityTarget", BindingFlags.Instance | BindingFlags.NonPublic));
                Assert.Single(main.Controls.Find("nudAutoQuality", true));
                Assert.Single(Descendants(main), control => ReferenceEquals(control, targetSize));
                Assert.Single(Descendants(main), control => ReferenceEquals(control, autoTarget));

                TabPage plan = Field<TabControl>(main, "_encodeInfoTabs").TabPages
                    .Cast<TabPage>().Single(page => page.Text == "Plan & Analysis");
                Assert.Empty(plan.Controls.Find("comboQualityMode", true));
                Assert.DoesNotContain(Descendants(plan), control => ReferenceEquals(control, encoder));
                Assert.DoesNotContain(Descendants(plan), control => ReferenceEquals(control, format));
                Assert.DoesNotContain(Descendants(plan), control => ReferenceEquals(control, estimateProfile));
                Assert.Empty(plan.Controls.Find("comboEncoderPreset", true));

                Button startQueue = Field<Button>(main, "btnStartEncode");
                Button startSelected = Field<Button>(main, "_btnStartSelectedQueue");
                Button pause = Field<Button>(main, "btnPauseQueue");
                Button stop = Field<Button>(main, "btnStopEncode");
                Button remove = Field<Button>(main, "_btnRemoveSelectedQueue");
                Button refresh = Field<Button>(main, "btnRefreshEncode");
                Button compareSamples = Field<Button>(main, "btnSampleComparison");
                Button compareCq = Field<Button>(main, "btnCqSampleComparison");
                ToolStripButton analyze = Field<ToolStripButton>(main, "_analyzeQueueButton");
                Assert.All(new[] { startQueue, startSelected, pause, stop }, button => Assert.Same(primaryActions, button.Parent));
                Assert.All(new[] { remove, refresh }, button => Assert.Same(secondaryActions, button.Parent));
                Assert.All(new[] { compareSamples, compareCq }, button => Assert.Same(comparisons, button.Parent));
                Assert.Same(secondaryActions, analyze.GetCurrentParent()?.Parent);
                Assert.Equal("Start Queue", startQueue.Text);
                Assert.Equal("Start Selected", startSelected.Text);
                Assert.Equal("Remove Selected", remove.Text);
                Assert.Equal("Refresh", refresh.Text);
                Assert.Equal("Compare Samples", compareSamples.Text);
                Assert.Equal("Compare CQ 22–25", compareCq.Text);
                Assert.Equal(new[] { startQueue, startSelected, pause, stop }, primaryActions.Controls.OfType<Button>().ToArray());
                Assert.Equal(new[] { remove, refresh }, secondaryActions.Controls.OfType<Button>().ToArray());
                Assert.Equal(new[] { compareSamples, compareCq }, comparisons.Controls.OfType<Button>().ToArray());
                Assert.Equal(new[] { 0, 1, 2, 3 }, new[] { startQueue, startSelected, pause, stop }.Select(button => button.TabIndex));
                Assert.Equal(1, ClickHandlerCount(startQueue));
                Assert.Equal(1, ClickHandlerCount(startSelected));
                Assert.Equal(2, ClickHandlerCount(pause));
                Assert.Equal(1, ClickHandlerCount(stop));
                Assert.Equal(1, ClickHandlerCount(remove));
                Assert.Equal(1, ClickHandlerCount(refresh));
                Assert.Equal(1, ClickHandlerCount(compareSamples));
                Assert.Equal(1, ClickHandlerCount(compareCq));
                Assert.True(analyze.Visible);
                Assert.False(string.IsNullOrWhiteSpace(analyze.ToolTipText));

                // The new split host is omitted from remembered-checkbox keys so rehosting
                // the existing Queue Controls does not change their saved identity.
                string includeSubfoldersKey = CheckboxKey(includeSubfolders);
                Assert.DoesNotContain("queueEncodingControlsLayout", includeSubfoldersKey, StringComparison.Ordinal);

                automaticQuality.SetSelectedTarget(QualityTarget.MaximumQuality);
                manualQuality.Value = 27;
                int modeChangedHandlers = EventHandlerCount(mode, "SelectedIndexChanged");
                for (int index = 0; index < 3; index++)
                {
                    mode.SelectedItem = "Manual • Legacy Numeric";
                    Application.DoEvents();
                    Assert.False(automaticQuality.Visible);
                    Assert.True(manualQuality.Visible);
                    Assert.False(qualityPreference.Visible);
                    Assert.False(qualityDescription.Visible);
                    Assert.True(estimateProfile.Visible);
                    Assert.Equal(27m, manualQuality.Value);
                    Assert.Equal("Manual", Config.Load(configPath).LastQualityMode);
                    Assert.Contains("Manual quality 27", activeProfile.Text);
                    Assert.Equal(activeProfile.Text, activeProfileToolTip.GetToolTip(activeProfile));

                    mode.SelectedItem = "Automatic • Source Adaptive";
                    Application.DoEvents();
                    Assert.True(automaticQuality.Visible);
                    Assert.False(manualQuality.Visible);
                    Assert.True(qualityPreference.Visible);
                    Assert.True(qualityDescription.Visible);
                    Assert.False(estimateProfile.Visible);
                    Assert.Equal(QualityTarget.MaximumQuality, automaticQuality.SelectedTarget);
                    Assert.Contains("Maximum Quality", qualityPreference.Text);
                    Assert.False(string.IsNullOrWhiteSpace(qualityDescription.Text));
                    Assert.Equal("Automatic", Config.Load(configPath).LastQualityMode);
                    Assert.Contains("Maximum Quality", activeProfile.Text);
                    Assert.DoesNotContain("CQ27", activeProfile.Text);
                    Assert.Equal(activeProfile.Text, activeProfileToolTip.GetToolTip(activeProfile));
                }
                Assert.Equal(modeChangedHandlers, EventHandlerCount(mode, "SelectedIndexChanged"));
                Assert.Equal(1, ClickHandlerCount(startQueue));
                Assert.Equal(1, ClickHandlerCount(startSelected));
                Assert.Equal(2, ClickHandlerCount(pause));
                Assert.Equal(1, ClickHandlerCount(stop));
                Assert.Equal(1, ClickHandlerCount(remove));
                Assert.Equal(1, ClickHandlerCount(refresh));
                Assert.Equal(1, ClickHandlerCount(compareSamples));
                Assert.Equal(1, ClickHandlerCount(compareCq));

                int minimumSideBySideWidth = (int)Invoke(main, "GetMinimumQueueEncodingControlsWidth")!;
                int stackedUpperContentHeight = 0;
                int normalWidthUpperContentHeight = 0;
                int normalWidthUpperViewportHeight = 0;
                int normalWidthWorkspaceSummaryTop = 0;
                int normalWidthUpperToWorkspaceGap = 0;
                int normalWidthProfileTextWidth = 0;

                foreach (Size size in new[]
                         {
                             new Size(1000, 720), new Size(1100, 700),
                             new Size(1300, 840), new Size(1360, 840), new Size(1800, 1100)
                         })
                {
                    main.ClientSize = size;
                    Application.DoEvents();
                    main.PerformLayout();
                    upper.PerformLayout();
                    cardsLayout.PerformLayout();
                    queueCard.PerformLayout();
                    encodingCard.PerformLayout();
                    Assert.Equal(size, main.ClientSize);
                    Assert.True(upper.ClientSize.Width > 0);
                    Assert.True(queueCard.Width > 0 && encodingCard.Width > 0);
                    Assert.False(queueCard.Bounds.IntersectsWith(encodingCard.Bounds));
                    upper.AutoScrollPosition = Point.Empty;
                    bool shouldStack = cardsLayout.ClientSize.Width < minimumSideBySideWidth;
                    if (upper.AutoScroll && upper.DisplayRectangle.Height > upper.ClientSize.Height)
                    {
                        int expectedScrollHeight = shouldStack
                            ? queueCard.Height + queueCard.Margin.Vertical + encodingCard.Height + encodingCard.Margin.Vertical
                            : Math.Max(queueCard.Height + queueCard.Margin.Vertical,
                                encodingCard.Height + encodingCard.Margin.Vertical);
                        Assert.True(upper.VerticalScroll.Visible,
                            $"Upper controls should expose vertical scrolling when height is constrained at {size}; upper={upper.ClientSize}, extent={upper.DisplayRectangle}.");
                        Assert.InRange(upper.DisplayRectangle.Height, expectedScrollHeight,
                            expectedScrollHeight + (int)Math.Round(4d * main.DeviceDpi / 96d));
                        upper.ScrollControlIntoView(queueCard);
                        Rectangle queueInViewport = upper.RectangleToClient(queueCard.RectangleToScreen(queueCard.ClientRectangle));
                        Assert.True(upper.ClientRectangle.IntersectsWith(queueInViewport));
                        upper.ScrollControlIntoView(encodingCard);
                        Rectangle encodingInViewport = upper.RectangleToClient(encodingCard.RectangleToScreen(encodingCard.ClientRectangle));
                        Assert.True(upper.ClientRectangle.IntersectsWith(encodingInViewport));
                        upper.AutoScrollPosition = Point.Empty;
                    }
                    else
                    {
                        Assert.True(upper.ClientRectangle.Contains(cardsLayout.Bounds),
                            $"Upper cards must fit or scroll at {size}; autoScroll={upper.AutoScroll}, display={upper.DisplayRectangle}, client={upper.ClientRectangle}, content={cardsLayout.Bounds}.");
                    }
                    if (shouldStack)
                    {
                        Assert.True(encodingCard.Top > queueCard.Top, $"Cards should stack at {size}; queue={queueCard.Bounds}, encoding={encodingCard.Bounds}.");
                        Assert.True(queueCard.AutoSize, $"Stacked Queue Controls should retain its natural height at {size}.");
                    }
                    else
                    {
                        Assert.Equal(queueCard.Top, encodingCard.Top);
                        Assert.False(queueCard.AutoSize, $"Side-by-side Queue Controls should fill the shared row at {size}.");
                        Assert.InRange(Math.Abs(queueCard.Bottom - encodingCard.Bottom), 0, 2);
                        Assert.Equal(queueCard.Margin.Bottom, encodingCard.Margin.Bottom);
                    }
                    if (size.Width == 1300)
                    {
                        Assert.False(shouldStack, $"Cards should fit side-by-side at the normal desktop width; minimum={minimumSideBySideWidth}, cards={cardsLayout.ClientSize.Width}.");
                        Assert.False(upper.AutoScroll, $"The 1300x840 normal-width layout should clear its scroll range when the compact cards fit; display={upper.DisplayRectangle}, client={upper.ClientRectangle}.");
                        Assert.True(upper.DisplayRectangle.Height <= upper.ClientSize.Height + 1,
                            $"The normal-width scroll extent should match the compact card height; display={upper.DisplayRectangle}, client={upper.ClientRectangle}.");
                    }

                    Assert.True(startQueue.Visible && startSelected.Visible && pause.Visible && stop.Visible);
                    Assert.True(remove.Visible && refresh.Visible && compareSamples.Visible && compareCq.Visible && analyze.Visible);
                    Assert.True(automaticQuality.Width > 0);
                    Assert.All(automaticQuality.Segments, segment =>
                    {
                        Assert.True(segment.Visible);
                        Assert.True(segment.Width > 0);
                    });
                    Assert.Single(automaticQuality.Segments, segment => segment.Checked);
                    Assert.Single(automaticQuality.Segments, segment => segment.TabStop);
                    Assert.True(mode.Width >= (int)Math.Round(120d * main.DeviceDpi / 96d), $"Mode is too narrow at {size}: {mode.Bounds}.");
                    Assert.True(encoder.Width >= (int)Math.Round(100d * main.DeviceDpi / 96d), $"Encoder is too narrow at {size}: {encoder.Bounds}.");
                    Assert.True(format.Width >= (int)Math.Round(100d * main.DeviceDpi / 96d), $"Video Format is too narrow at {size}: {format.Bounds}.");
                    Assert.True(automaticQuality.Width >= (int)Math.Round(180d * main.DeviceDpi / 96d), $"Quality selector is too narrow at {size}: {automaticQuality.Bounds}.");
                    Assert.True(queueGrid.Visible && queueGrid.ClientSize.Width >= size.Width - (int)Math.Round(28d * main.DeviceDpi / 96d),
                        $"Queue grid should retain lower-workspace width at {size}; grid={queueGrid.Bounds}, form={main.ClientSize}.");
                    Assert.True(queueGrid.ClientSize.Height > (int)Math.Round(80d * main.DeviceDpi / 96d),
                        $"Queue grid should retain useful lower-workspace height at {size}; grid={queueGrid.Bounds}.");
                    Assert.True(queueSplit.Visible, $"Lower queue/inspector splitter should stay visible at {size}.");
                    Assert.All(new[] { startQueue, startSelected, pause, stop }, button =>
                        Assert.True(button.Parent!.ClientRectangle.Contains(button.Bounds), $"Primary action is clipped at {size}: {button.Name} {button.Bounds} in {button.Parent.ClientRectangle}."));
                    _output.WriteLine(
                        $"Layout {size.Width}x{size.Height} at {main.DeviceDpi} DPI: {(shouldStack ? "stacked" : "side-by-side")}, " +
                        $"upper viewport={upper.ClientSize.Height}px, queue card={queueCard.Bounds}, encoding card={encodingCard.Bounds}, " +
                        $"queue grid={queueGrid.Bounds}, queue split visible={queueSplit.Visible}.");
                    WriteLayoutSnapshot(main, "Automatic", size, upper, queueCard, encodingCard, queueSplit, queueGrid);
                    int upperContentHeight = shouldStack
                        ? queueCard.Height + queueCard.Margin.Vertical + encodingCard.Height + encodingCard.Margin.Vertical
                        : Math.Max(queueCard.Height + queueCard.Margin.Vertical, encodingCard.Height + encodingCard.Margin.Vertical);
                    if (size.Width == 1000)
                        stackedUpperContentHeight = upperContentHeight;
                    if (size.Width == 1300)
                    {
                        normalWidthUpperContentHeight = upperContentHeight;
                        normalWidthUpperViewportHeight = upper.ClientSize.Height;
                        TableLayoutPanel encodeLayout = Field<TableLayoutPanel>(main, "tlEncode");
                        TableLayoutPanel workspace = Field<TableLayoutPanel>(main, "_queueWorkspaceHost");
                        Control summary = main.Controls.Find("queueWorkspaceSummaryCards", true).Single();
                        int upperTop = encodeLayout.PointToClient(upper.PointToScreen(Point.Empty)).Y;
                        int upperBottom = upperTop + upper.Height;
                        int splitTop = encodeLayout.PointToClient(queueSplit.PointToScreen(Point.Empty)).Y;
                        int summaryTop = encodeLayout.PointToClient(summary.PointToScreen(Point.Empty)).Y;
                        int cardSpan = Math.Max(
                            queueCard.Height + queueCard.Margin.Vertical,
                            encodingCard.Height + encodingCard.Margin.Vertical);
                        int unusedUpperHeight = upper.ClientSize.Height - cardSpan;
                        int gapAfterEncodingCard = summaryTop - (upperTop + encodingCard.Bottom);
                        int[] cardColumns = cardsLayout.GetColumnWidths();
                        float queueColumnRatio = cardColumns[0] / (float)cardColumns.Sum();
                        int[] queueRows = queueCard.GetRowHeights();
                        normalWidthWorkspaceSummaryTop = summaryTop;
                        normalWidthUpperToWorkspaceGap = splitTop - (upperTop + upper.Height);
                        normalWidthProfileTextWidth = TextRenderer.MeasureText(
                            activeProfile.Text,
                            activeProfile.Font,
                            Size.Empty,
                            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                        Assert.InRange(unusedUpperHeight, 0, (int)Math.Round(4d * main.DeviceDpi / 96d));
                        Assert.InRange(Math.Abs(normalWidthUpperToWorkspaceGap), 0, (int)Math.Round(2d * main.DeviceDpi / 96d));
                        Assert.InRange(gapAfterEncodingCard, 0, (int)Math.Round(8d * main.DeviceDpi / 96d));
                        Assert.Equal(46F, cardsLayout.ColumnStyles[0].Width);
                        Assert.Equal(54F, cardsLayout.ColumnStyles[1].Width);
                        Assert.InRange(queueColumnRatio, 0.455F, 0.465F);
                        Assert.True(queueRows[0] > 0, $"Queue content should have balanced top and bottom breathing room; rows={string.Join(",", queueRows)}.");
                        Assert.InRange(Math.Abs(queueRows[0] - queueRows[^1]), 0, 2);
                        Assert.True(activeProfile.Width >= (int)Math.Round(400d * main.DeviceDpi / 96d),
                            $"Active profile should receive useful width at normal desktop size; label={activeProfile.Bounds}, host={activeProfile.Parent?.Bounds}.");
                        Assert.True(normalWidthProfileTextWidth <= activeProfile.ClientSize.Width,
                            $"The full active profile should fit without ellipsis at normal desktop size; measured={normalWidthProfileTextWidth}, label={activeProfile.ClientSize.Width}, text='{activeProfile.Text}'.");
                        Assert.Contains("H.265", activeProfile.Text, StringComparison.Ordinal);
                        Assert.Contains("Maximum Quality", activeProfile.Text, StringComparison.Ordinal);
                        Assert.Equal(activeProfile.Text, activeProfileToolTip.GetToolTip(activeProfile));
                        string rows = string.Join(", ", encodeLayout.GetRowHeights().Select((height, index) =>
                            $"{index}:{encodeLayout.RowStyles[index].SizeType}/{encodeLayout.RowStyles[index].Height:0.#}={height}"));
                        string encodingChildren = string.Join(", ", encodingCard.Controls.Cast<Control>()
                            .Select(control => $"{control.Name}:{control.Bounds}/pref={control.PreferredSize}/dock={control.Dock}/auto={control.AutoSize}"));
                        _output.WriteLine(
                            $"1300px parent={encodeLayout.ClientSize}, upper={upper.Bounds}, layout={cardsLayout.Bounds}, " +
                            $"split={queueSplit.Bounds}, upperBottom={upperBottom}, splitTop={splitTop}, gap={splitTop - upperBottom}, " +
                            $"summary={summary.Bounds}, summaryTop={summaryTop}, workspace={workspace.Bounds}, rows=[{rows}], " +
                            $"activeProfile={activeProfile.Bounds}, activeWidth={activeProfile.Width}, host={activeProfile.Parent?.Bounds}/" +
                            $"{activeProfile.Parent?.Dock}/auto={activeProfile.Parent?.AutoSize}, fields={fields.Bounds}, " +
                            $"encodingChildren=[{encodingChildren}], textWidth={normalWidthProfileTextWidth}, " +
                            $"queueRows=[{string.Join(",", queueRows)}], cardColumnRatio={queueColumnRatio:P1}, " +
                            $"upperBlank={unusedUpperHeight}, cardToSummaryGap={gapAfterEncodingCard}, text='{activeProfile.Text}'.");
                        _output.WriteLine(
                            $"1300px normal-width card span={upperContentHeight}px, allocated upper viewport={normalWidthUpperViewportHeight}px, " +
                            $"stacked 1000px upper content={stackedUpperContentHeight}px; " +
                            $"viewport space returned={stackedUpperContentHeight - normalWidthUpperViewportHeight}px; " +
                            $"minimum side-by-side width={minimumSideBySideWidth}px at {main.DeviceDpi} DPI.");
                    }

                    mode.SelectedItem = "Manual • Legacy Numeric";
                    Application.DoEvents();
                    main.PerformLayout();
                    upper.PerformLayout();
                    cardsLayout.PerformLayout();
                    queueCard.PerformLayout();
                    encodingCard.PerformLayout();
                    fields.PerformLayout();
                    Assert.False(automaticQuality.Visible);
                    Assert.True(manualQuality.Visible);
                    Assert.True(estimateProfile.Visible);
                    Assert.Equal(27m, manualQuality.Value);
                    Assert.True(queueGrid.Visible && queueSplit.Visible);
                    Assert.Equal(shouldStack, cardsLayout.ClientSize.Width < minimumSideBySideWidth);
                    if (size.Width == 1300)
                    {
                        int manualNaturalHeight = (int)Invoke(main, "GetPreferredQueueEncodingControlsHeight")!;
                        int upperRow = Field<TableLayoutPanel>(main, "tlEncode").GetRow(upper);
                        int realizedRowHeight = Field<TableLayoutPanel>(main, "tlEncode").GetRowHeights()[upperRow];
                        Assert.InRange(Math.Abs(upper.Height - manualNaturalHeight), 0, 2);
                        Assert.InRange(Math.Abs(realizedRowHeight - manualNaturalHeight), 0, 2);
                    }
                    WriteLayoutSnapshot(main, "Manual", size, upper, queueCard, encodingCard, queueSplit, queueGrid);

                    mode.SelectedItem = "Automatic • Source Adaptive";
                    Application.DoEvents();
                    main.PerformLayout();
                    upper.PerformLayout();
                    cardsLayout.PerformLayout();
                    Assert.True(automaticQuality.Visible);
                    Assert.False(manualQuality.Visible);
                    Assert.Equal(QualityTarget.MaximumQuality, automaticQuality.SelectedTarget);
                }
                Assert.True(stackedUpperContentHeight - normalWidthUpperViewportHeight >=
                            (int)Math.Round(40d * main.DeviceDpi / 96d),
                    $"The normal-width layout should return meaningful vertical space; stacked={stackedUpperContentHeight}, side-by-side viewport={normalWidthUpperViewportHeight}, natural cards={normalWidthUpperContentHeight}.");
                Assert.InRange(normalWidthUpperToWorkspaceGap, 0, (int)Math.Round(2d * main.DeviceDpi / 96d));
                Assert.True(normalWidthWorkspaceSummaryTop > 0);
                Assert.True(normalWidthProfileTextWidth > 0);

                main.ClientSize = new Size(1000, 720);
                Application.DoEvents();
                main.PerformLayout();
                upper.PerformLayout();
                cardsLayout.PerformLayout();
                Assert.True(cardsLayout.ClientSize.Width < minimumSideBySideWidth);
                Assert.True(encodingCard.Top > queueCard.Top,
                    $"Side-by-side to stacked resize should reflow both cards; queue={queueCard.Bounds}, encoding={encodingCard.Bounds}.");
                Assert.True(upper.AutoScroll && upper.VerticalScroll.Visible,
                    $"The narrow layout should restore its vertical scroll range; viewport={upper.ClientSize}, extent={upper.DisplayRectangle}.");
                Assert.True(queueGrid.Visible && queueGrid.ClientSize.Width >= 972 && queueSplit.Visible);
                _output.WriteLine($"Resize transition 1800→1000: stacked cards, upper={upper.ClientSize}, queue grid width={queueGrid.ClientSize.Width}.");

                main.WindowState = FormWindowState.Maximized;
                Application.DoEvents();
                main.PerformLayout();
                Assert.True(main.WindowState == FormWindowState.Maximized);
                Assert.True(queueGrid.Visible && queueGrid.ClientSize.Width > 0);
                if (cardsLayout.ClientSize.Width >= minimumSideBySideWidth)
                    Assert.Equal(queueCard.Top, encodingCard.Top);
                WriteLayoutSnapshot(main, "Automatic maximized", main.ClientSize, upper, queueCard, encodingCard, queueSplit, queueGrid);

                main.WindowState = FormWindowState.Normal;
                main.ClientSize = new Size(1300, 840);
                Application.DoEvents();
                main.PerformLayout();
                upper.PerformLayout();
                Assert.True(cardsLayout.ClientSize.Width >= minimumSideBySideWidth);
                Assert.Equal(queueCard.Top, encodingCard.Top);
                Assert.True(queueGrid.Visible && queueGrid.ClientSize.Width >= 1270);
                int restoredNaturalHeight = (int)Invoke(main, "GetPreferredQueueEncodingControlsHeight")!;
                Assert.InRange(Math.Abs(upper.Height - restoredNaturalHeight), 0, 2);
                WriteLayoutSnapshot(main, "Automatic restored", main.ClientSize, upper, queueCard, encodingCard, queueSplit, queueGrid);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Upper control layout UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void AutomaticTargetToggleReflowsDescriptionWithoutOverlappingOrAccumulatingHeight()
    {
        if (!OperatingSystem.IsWindows()) return;

        string configPath = Path.Combine(Path.GetTempPath(), $"MediaFlux.AutoTargetToggle.{Guid.NewGuid():N}.json");
        new Config
        {
            RememberCheckboxStates = false,
            LastQualityMode = "Automatic",
            LastQualityTarget = nameof(QualityTarget.MaximumQuality)
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

                CheckBox autoTarget = Field<CheckBox>(main, "chkAutoTargetSize");
                TextBox targetSize = Field<TextBox>(main, "txtTargetSize");
                QualityTargetSegmentSelector selector = Field<QualityTargetSegmentSelector>(main, "qualityTargetSelector");
                Label description = Field<Label>(main, "lblQualityTargetDescription");
                TableLayoutPanel qualityLayout = (TableLayoutPanel)description.Parent!;
                TableLayoutPanel encodingFields = Field<TableLayoutPanel>(main, "tlEncodingProfileFields");
                TableLayoutPanel encodingCard = Field<TableLayoutPanel>(main, "pnlEncodingProfileCard");
                Panel upper = Field<Panel>(main, "_queueEncodingControlsViewport");
                ComboBox speed = Field<ComboBox>(main, "comboEncoderPreset");

                int autoTargetHandlers = EventHandlerCount(autoTarget, "CheckedChanged");
                int speedHandlers = EventHandlerCount(speed, "SelectedIndexChanged");
                int selectorHandlers = QualityTargetChangedHandlerCount(selector);
                Assert.Equal(5, autoTargetHandlers);
                Assert.Equal(1, speedHandlers);
                Assert.Equal(1, selectorHandlers);
                Assert.True(autoTarget.Checked);

                foreach (Size size in new[]
                         {
                             new Size(1100, 820), new Size(1300, 840),
                             new Size(1360, 840), new Size(1800, 900)
                         })
                {
                    main.ClientSize = size;
                    Application.DoEvents();
                    int? autoOnCardHeight = null;
                    int? autoOffCardHeight = null;
                    int? autoOnNaturalUpperHeight = null;
                    int? autoOffNaturalUpperHeight = null;
                    int? autoOnDescriptionRowHeight = null;
                    int? autoOffDescriptionRowHeight = null;

                    for (int cycle = 0; cycle < 3; cycle++)
                    {
                        foreach (bool autoTargetEnabled in new[] { true, false })
                        {
                            autoTarget.Checked = autoTargetEnabled;
                            Application.DoEvents();
                            main.PerformLayout();
                            upper.PerformLayout();
                            encodingCard.PerformLayout();
                            encodingFields.PerformLayout();
                            qualityLayout.PerformLayout();

                            Assert.Equal(!autoTargetEnabled, targetSize.Enabled);
                            Assert.True(description.Visible);
                            Assert.True(selector.Visible);
                            Assert.Equal(QualityTarget.MaximumQuality, selector.SelectedTarget);
                            Assert.Equal(1, selector.Segments.Count(segment => segment.Checked));
                            Assert.All(selector.Segments, segment =>
                            {
                                Assert.True(segment.Visible);
                                Assert.True(segment.Width > 0);
                            });
                            Assert.True(qualityLayout.ClientRectangle.Contains(selector.Bounds),
                                $"Selector is outside its assigned quality row at {size}: selector={selector.Bounds}, layout={qualityLayout.ClientRectangle}.");
                            Assert.True(description.Left >= 0 && description.Right <= qualityLayout.ClientSize.Width,
                                $"Description exceeds its quality row at {size}: description={description.Bounds}, layout={qualityLayout.ClientRectangle}, max={description.MaximumSize}.");
                            Assert.True(description.Bottom <= selector.Top,
                                $"Description crowds the selector at {size}: description={description.Bounds}, selector={selector.Bounds}, rows=[{string.Join(",", qualityLayout.GetRowHeights())}].");

                            Size measuredDescription = TextRenderer.MeasureText(
                                description.Text,
                                description.Font,
                                new Size(Math.Max(1, description.ClientSize.Width), 4096),
                                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding);
                            Assert.True(measuredDescription.Height <= description.Height + 2,
                                $"Description is clipped at {size}: measured={measuredDescription}, label={description.Bounds}, text='{description.Text}'.");

                            Rectangle selectorInFields = encodingFields.RectangleToClient(selector.RectangleToScreen(selector.ClientRectangle));
                            Rectangle targetInFields = encodingFields.RectangleToClient(targetSize.RectangleToScreen(targetSize.ClientRectangle));
                            Rectangle descriptionInFields = encodingFields.RectangleToClient(description.RectangleToScreen(description.ClientRectangle));
                            Assert.False(selectorInFields.IntersectsWith(targetInFields),
                                $"Selector overlaps Target Size at {size}: selector={selectorInFields}, target={targetInFields}.");
                            Assert.False(descriptionInFields.IntersectsWith(targetInFields),
                                $"Description overlaps Target Size at {size}: description={descriptionInFields}, target={targetInFields}.");

                            int cardHeight = encodingCard.PreferredSize.Height;
                            int naturalUpperHeight = (int)Invoke(main, "GetPreferredQueueEncodingControlsHeight")!;
                            int descriptionRowHeight = qualityLayout.GetRowHeights()[qualityLayout.GetRow(description)];
                            if (autoTargetEnabled)
                            {
                                autoOnCardHeight ??= cardHeight;
                                autoOnNaturalUpperHeight ??= naturalUpperHeight;
                                autoOnDescriptionRowHeight ??= descriptionRowHeight;
                                Assert.InRange(Math.Abs(cardHeight - autoOnCardHeight.Value), 0, 1);
                                Assert.InRange(Math.Abs(naturalUpperHeight - autoOnNaturalUpperHeight.Value), 0, 1);
                                Assert.InRange(Math.Abs(descriptionRowHeight - autoOnDescriptionRowHeight.Value), 0, 1);
                            }
                            else
                            {
                                Assert.Contains("Manual target size is authoritative", description.Text, StringComparison.Ordinal);
                                Assert.False(selector.Enabled);
                                autoOffCardHeight ??= cardHeight;
                                autoOffNaturalUpperHeight ??= naturalUpperHeight;
                                autoOffDescriptionRowHeight ??= descriptionRowHeight;
                                Assert.InRange(Math.Abs(cardHeight - autoOffCardHeight.Value), 0, 1);
                                Assert.InRange(Math.Abs(naturalUpperHeight - autoOffNaturalUpperHeight.Value), 0, 1);
                                Assert.InRange(Math.Abs(descriptionRowHeight - autoOffDescriptionRowHeight.Value), 0, 1);
                            }

                            Assert.Equal(autoTargetHandlers, EventHandlerCount(autoTarget, "CheckedChanged"));
                            Assert.Equal(speedHandlers, EventHandlerCount(speed, "SelectedIndexChanged"));
                            Assert.Equal(selectorHandlers, QualityTargetChangedHandlerCount(selector));
                            Assert.Single(Descendants(main), control => ReferenceEquals(control, autoTarget));
                            Assert.Single(Descendants(main), control => ReferenceEquals(control, selector));
                        }
                    }

                    _output.WriteLine(
                        $"Auto target reflow {size.Width}x{size.Height}: on card/upper/description-row=" +
                        $"{autoOnCardHeight}/{autoOnNaturalUpperHeight}/{autoOnDescriptionRowHeight}; " +
                        $"off={autoOffCardHeight}/{autoOffNaturalUpperHeight}/{autoOffDescriptionRowHeight}; " +
                        $"textBounds={description.Bounds}, selectorBounds={selector.Bounds}, fieldRows=[{string.Join(",", encodingFields.GetRowHeights())}].");
                }

                main.ClientSize = new Size(1800, 480);
                Application.DoEvents();
                main.PerformLayout();
                upper.PerformLayout();
                encodingCard.PerformLayout();
                encodingFields.PerformLayout();
                qualityLayout.PerformLayout();
                autoTarget.Checked = false;
                Application.DoEvents();
                main.PerformLayout();
                upper.PerformLayout();
                encodingCard.PerformLayout();
                encodingFields.PerformLayout();
                qualityLayout.PerformLayout();
                Assert.True(upper.AutoScroll || upper.ClientSize.Height >= encodingCard.PreferredSize.Height,
                    $"Constrained layout must expose the Encoding Controls card or its scroll range; upper={upper.ClientSize}, extent={upper.DisplayRectangle}, card={encodingCard.Bounds}, preferred={encodingCard.PreferredSize}.");
                Assert.False(description.RectangleToScreen(description.ClientRectangle)
                    .IntersectsWith(selector.RectangleToScreen(selector.ClientRectangle)));
                Assert.False(selector.RectangleToScreen(selector.ClientRectangle)
                    .IntersectsWith(targetSize.RectangleToScreen(targetSize.ClientRectangle)));
                Assert.Equal(QualityTarget.MaximumQuality, selector.SelectedTarget);
                Assert.Equal(autoTargetHandlers, EventHandlerCount(autoTarget, "CheckedChanged"));
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Automatic target toggle layout UI test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static T Field<T>(MainForm main, string name) where T : class =>
        (T)(typeof(MainForm).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
            ?? throw new MissingFieldException(name));

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
                yield return descendant;
        }
    }

    private static IEnumerable<Control> Ancestors(Control control)
    {
        for (Control? current = control; current != null; current = current.Parent)
            yield return current;
    }

    private static Rectangle BoundsInFormClient(Form form, Control control)
    {
        if (ReferenceEquals(form, control))
            return new Rectangle(Point.Empty, form.ClientSize);
        Control parent = control.Parent ?? throw new InvalidOperationException($"{control.GetType().Name} has no parent.");
        return form.RectangleToClient(parent.RectangleToScreen(control.Bounds));
    }

    private void WriteLayoutSnapshot(
        MainForm main,
        string mode,
        Size size,
        Control upper,
        Control queueCard,
        Control encodingCard,
        SplitContainer split,
        DataGridView grid)
    {
        Control folders = Field<Control>(main, "pnlFolderSetupCard");
        Control panelEncode = Field<Control>(main, "panelEncode");
        TableLayoutPanel encodeLayout = Field<TableLayoutPanel>(main, "tlEncode");
        Control menu = Field<Control>(main, "menuStrip1");
        Control summary = main.Controls.Find("queueWorkspaceSummaryCards", true).Single();
        Control search = main.Controls.Find("queueWorkspaceViewToolbar", true).Single();
        Control estimate = main.Controls.Find("queueWorkspaceEstimateStrip", true).Single();
        Control inspector = Field<Control>(main, "_encodeInfoHeader");

        Rectangle upperBounds = BoundsInFormClient(main, upper);
        Rectangle queueBounds = BoundsInFormClient(main, queueCard);
        Rectangle encodingBounds = BoundsInFormClient(main, encodingCard);
        Rectangle splitBounds = BoundsInFormClient(main, split);
        Rectangle summaryBounds = BoundsInFormClient(main, summary);
        Rectangle gridBounds = BoundsInFormClient(main, grid);
        Rectangle inspectorBounds = BoundsInFormClient(main, split.Panel2);
        int cardBottom = Math.Max(queueBounds.Bottom, encodingBounds.Bottom);
        int upperToSummary = summaryBounds.Top - upperBounds.Bottom;
        int cardToSummary = summaryBounds.Top - cardBottom;
        bool stacked = Field<TableLayoutPanel>(main, "_queueEncodingControlsLayout").ColumnCount == 1;

        _output.WriteLine(
            $"Layout {mode} {size.Width}x{size.Height} at {main.DeviceDpi} DPI: {(stacked ? "stacked" : "side-by-side")}; " +
            $"FormClient={BoundsInFormClient(main, main)}, menu={BoundsInFormClient(main, menu)}, " +
            $"Folders={BoundsInFormClient(main, folders)}, upperHost={upperBounds}, " +
            $"QueueCard={queueBounds}, EncodingCard={encodingBounds}, upper-to-summary={upperToSummary}px, " +
            $"card-to-summary={cardToSummary}px, summaryTop={summaryBounds.Top}, " +
            $"search={BoundsInFormClient(main, search)}, estimates={BoundsInFormClient(main, estimate)}, " +
            $"grid={gridBounds}, inspectorPane={inspectorBounds}, inspector={BoundsInFormClient(main, inspector)}.");

        if (size.Width == 1300)
        {
            int[] rows = encodeLayout.GetRowHeights();
            int upperRow = encodeLayout.GetRow(Field<Control>(main, "_queueEncodingControlsViewport"));
            string rowSummary = string.Join(", ", encodeLayout.RowStyles
                .Cast<RowStyle>()
                .Select((style, index) => $"{index}:{style.SizeType}/{style.Height:0.#}={rows[index]}"));
            string ancestorChain = string.Join(" -> ", Ancestors(encodeLayout)
                .Select(control => $"{control.GetType().Name}({control.Name}):{BoundsInFormClient(main, control)}"));
            _output.WriteLine(
                $"Layout {mode} 1300px ancestor chain={ancestorChain}; " +
                $"upperRow={upperRow}:{rows[upperRow]}px, rows=[{rowSummary}], " +
                $"viewportMinimum={upper.MinimumSize}, preferredUpper={(int)Invoke(main, "GetPreferredQueueEncodingControlsHeight")!}px, " +
                $"upperLayout={BoundsInFormClient(main, Field<Control>(main, "_queueEncodingControlsLayout"))}/" +
                $"preferred={Field<Control>(main, "_queueEncodingControlsLayout").PreferredSize}, " +
                $"queueHeight={Field<Control>(main, "pnlQueueControlsCard").Height}/preferred={Field<Control>(main, "pnlQueueControlsCard").PreferredSize}, " +
                $"encodingHeight={Field<Control>(main, "pnlEncodingProfileCard").Height}/preferred={Field<Control>(main, "pnlEncodingProfileCard").PreferredSize}, " +
                $"queueGridSize={grid.ClientSize}, splitSize={split.ClientSize}, splitterDistance={split.SplitterDistance}.");
        }
    }

    private static int ClickHandlerCount(Control control)
    {
        PropertyInfo eventsProperty = typeof(Control).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Control).FullName, "Events");
        var events = (EventHandlerList?)eventsProperty.GetValue(control)
            ?? throw new InvalidOperationException("Control event list is unavailable.");
        FieldInfo[] clickKeys = typeof(Control).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(object) && field.Name.Contains("click", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (FieldInfo key in clickKeys)
        {
            if (events[key.GetValue(null)!] is Delegate handlers)
                return handlers.GetInvocationList().Length;
        }

        throw new MissingFieldException(typeof(Control).FullName, "Click event key");
    }

    private static int EventHandlerCount(Control control, string eventName)
    {
        PropertyInfo eventsProperty = typeof(Control).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(typeof(Control).FullName, "Events");
        var events = (EventHandlerList?)eventsProperty.GetValue(control)
            ?? throw new InvalidOperationException("Control event list is unavailable.");
        for (Type? type = control.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo[] eventKeys = type.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(field => field.FieldType == typeof(object) && field.Name.Contains(eventName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (FieldInfo key in eventKeys)
            {
                if (events[key.GetValue(null)!] is Delegate handlers)
                    return handlers.GetInvocationList().Length;
            }
        }

        throw new MissingFieldException(control.GetType().FullName, $"{eventName} event key");
    }

    private static int QualityTargetChangedHandlerCount(QualityTargetSegmentSelector selector)
    {
        FieldInfo eventField = typeof(QualityTargetSegmentSelector).GetField(
            "SelectedTargetChanged", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(QualityTargetSegmentSelector).FullName, "SelectedTargetChanged");
        return ((Delegate?)eventField.GetValue(selector))?.GetInvocationList().Length ?? 0;
    }

    private static object? Invoke(MainForm main, string methodName, params object[] args)
    {
        MethodInfo method = typeof(MainForm).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(methodName);
        return method.Invoke(main, args);
    }

    private static string CheckboxKey(CheckBox checkbox)
    {
        MethodInfo method = typeof(MainForm).GetMethod("GetCheckboxPersistenceKey", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("GetCheckboxPersistenceKey");
        return (string)(method.Invoke(null, new object[] { checkbox })
            ?? throw new InvalidOperationException("Checkbox persistence key is null."));
    }
}
