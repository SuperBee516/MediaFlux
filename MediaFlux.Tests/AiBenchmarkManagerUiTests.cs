using System.Threading;
using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Services;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiBenchmarkManagerUiTests
{
    [Fact]
    public void WindowSizePersistsRestoresAndIgnoresInvalidOrMaximizedBounds()
    {
        Exception? failure = null; Size? restored = null; Size? clamped = null; Size? afterMaximizedClose = null;
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxAiBenchmarkManagerTests", Guid.NewGuid().ToString("N")); string configPath = Path.Combine(root, "config.json");
        var thread = new Thread(() =>
        {
            AiBenchmarkManagerForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext()); var config = new Config(); form = new AiBenchmarkManagerForm(config: config, configPath: configPath); Assert.Equal(new Size(1500, 950), form.Size); form.Show(); Application.DoEvents(); AssertInsideWorkingArea(form); form.Size = new Size(1320, 820); Application.DoEvents(); Size persisted = form.Size; form.Close(); form.Dispose(); form = null;
                Config saved = Config.Load(configPath); Assert.Equal(persisted.Width, saved.AiBenchmarkManagerWindowWidth); Assert.Equal(persisted.Height, saved.AiBenchmarkManagerWindowHeight);
                using (var next = new AiBenchmarkManagerForm(config: saved, configPath: configPath)) restored = next.Size;
                saved.AiBenchmarkManagerWindowWidth = 1; saved.AiBenchmarkManagerWindowHeight = 1; saved.Save(configPath); using (var invalid = new AiBenchmarkManagerForm(config: Config.Load(configPath), configPath: configPath)) clamped = invalid.Size;
                var normal = new Config { AiBenchmarkManagerWindowWidth = 1400, AiBenchmarkManagerWindowHeight = 840 }; normal.Save(configPath); form = new AiBenchmarkManagerForm(config: Config.Load(configPath), configPath: configPath); form.Show(); Application.DoEvents(); Size normalVisible = form.Size; form.WindowState = FormWindowState.Maximized; Application.DoEvents(); form.Close(); form.Dispose(); form = null; afterMaximizedClose = new Size(Config.Load(configPath).AiBenchmarkManagerWindowWidth, Config.Load(configPath).AiBenchmarkManagerWindowHeight); Assert.Equal(normalVisible, afterMaximizedClose);
            }
            catch (Exception ex) { failure = ex; }
            finally { WinFormsTestLifecycle.CloseAndDispose(form); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The window-size persistence test timed out."); if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.NotNull(restored); Assert.NotNull(clamped);
    }

    [Fact]
    public void ComparisonPercentageHandlesZeroAndFormatsPrecisely()
    {
        Assert.Equal("Unavailable", AiBenchmarkManagerForm.PercentageDifference(0, 1.2));
        Assert.Equal("Same", AiBenchmarkManagerForm.PercentageDifference(0, 0));
        Assert.Equal("+233.33%", AiBenchmarkManagerForm.PercentageDifference(.36, 1.2));
    }

    [Fact]
    public void BestSelectionUsesComparableGroupsAndExcludesInvalidRows()
    {
        AiBenchmarkRecord validSlow = Record(1, "model-a", 2, 0.5, true);
        AiBenchmarkRecord validFast = Record(2, "model-a", 2, 1.2, true);
        AiBenchmarkRecord incompatible = Record(3, "model-b", 2, 3, true);
        AiBenchmarkRecord obsolete = Record(4, "model-a", 2, 99, false);
        Assert.Equal(new[] { 2L, 3L }, AiBenchmarkManagerForm.BestIds(new[] { validSlow, validFast, incompatible, obsolete }));
    }

    [Fact]
    public void PopulatedManagerUsesNormalizedGridAndStructuredInspectors()
    {
        Exception? failure = null; string? details = null; string[]? columns = null; string? summary = null;
        var thread = new Thread(() =>
        {
            AiBenchmarkManagerForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string path = Path.Combine(Path.GetTempPath(), "MediaFluxAiBenchmarkManagerTests", Guid.NewGuid().ToString("N"), "data.db"); var db = new AiBenchmarkDatabase(path); db.Store(Entry("model-a", .3035724028491089));
                form = new AiBenchmarkManagerForm(new AiBenchmarkManagementService(db)); form.Show(); Application.DoEvents();
                DataGridView grid = form.Controls.Find("benchmarkGrid", true).OfType<DataGridView>().Single(); columns = grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).Select(c => c.HeaderText).ToArray(); summary = form.Controls.Find("summaryResults", true).Single().Text; details = form.Controls.Find("benchmarkDetails", true).Single().Controls.OfType<Label>().Select(l => l.Text).FirstOrDefault(t => t.Contains("Average FPS", StringComparison.Ordinal));
                foreach (var expected in new[] { ("Results", "Results", "1"), ("BestvalidFPS", "Best valid FPS", "0.30"), ("GPU", "GPU", "GPU"), ("Backend", "Backend", "ncnn-vulkan"), ("Model", "Model", "model-a"), ("Scale", "Scale", "2x"), ("Precision", "Precision", "FP32") }) { TableLayoutPanel tile = form.Controls.Find("summary" + expected.Item1, true).OfType<TableLayoutPanel>().Single(); Label title = tile.Controls.Find("summary" + expected.Item1 + "Title", false).OfType<Label>().Single(); Label value = tile.Controls.Find("summary" + expected.Item1 + "Value", false).OfType<Label>().Single(); Assert.Equal(expected.Item2, title.Text); Assert.Contains(expected.Item3, value.Text); Assert.True(tile.ClientRectangle.Contains(title.Bounds)); Assert.True(tile.ClientRectangle.Contains(value.Bounds)); Assert.True(value.Bottom < tile.ClientRectangle.Bottom); Assert.False(title.Bounds.IntersectsWith(value.Bounds)); }
                foreach (string key in new[] { "Results", "BestvalidFPS", "GPU", "Backend", "Model", "Scale", "Precision" }) { TableLayoutPanel tile = form.Controls.Find("summary" + key, true).OfType<TableLayoutPanel>().Single(); PictureBox icon = tile.Controls.Find("summary" + key + "Icon", false).OfType<PictureBox>().Single(); Assert.NotNull(icon.Image); Assert.True(tile.ClientRectangle.Contains(icon.Bounds)); }
                foreach (var command in new[] { ("RunBenchmark", "Run Benchmark"), ("Re-runSelected", "Re-run Selected"), ("CompareSelected", "Compare Selected"), ("Refresh", "Refresh") }) { Button button = form.Controls.Find("benchmarkCommand" + command.Item1, true).OfType<Button>().Single(); Assert.Equal(command.Item2, button.Text); Assert.NotNull(button.Image); Assert.True(button.Image!.Width < button.ClientSize.Width); Assert.True(button.ClientSize.Height > button.Image.Height); } FlowLayoutPanel commands = form.Controls.Find("benchmarkCommands", true).OfType<FlowLayoutPanel>().Single(); var commandItems = commands.Controls.OfType<ToolStrip>().SelectMany(strip => strip.Items.Cast<ToolStripItem>()).OfType<ToolStripDropDownButton>().ToArray(); Assert.NotNull(commandItems.Single(item => item.Name == "benchmarkCommandImportExport").Image); Assert.NotNull(commandItems.Single(item => item.Name == "benchmarkCommandMore").Image); Assert.NotNull(form.Controls.Find("benchmarkDetailsLabelTitleIcon", true).OfType<PictureBox>().Single().Image); Assert.NotNull(form.Controls.Find("benchmarkComparisonLabelIcon", true).OfType<PictureBox>().Single().Image);
                TableLayoutPanel summaryHost = form.Controls.Find("benchmarkSummary", true).OfType<TableLayoutPanel>().Single(); SplitContainer mainHost = form.Controls.Find("benchmarkMainSplit", true).OfType<SplitContainer>().Single(); Assert.True(summaryHost.Bottom < mainHost.Top); Assert.True(mainHost.Top - summaryHost.Bottom >= 1); var tiles = summaryHost.Controls.OfType<TableLayoutPanel>().ToArray(); for (int i = 0; i < tiles.Length; i++) for (int j = i + 1; j < tiles.Length; j++) Assert.False(tiles[i].Bounds.IntersectsWith(tiles[j].Bounds));
            }
            catch (Exception ex) { failure = ex; }
            finally { WinFormsTestLifecycle.CloseAndDispose(form); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The populated WinForms test timed out."); if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.NotNull(columns); Assert.DoesNotContain("Runtime profile", columns!); Assert.Contains("Average FPS", columns!); Assert.Contains("\n1", summary); Assert.NotNull(details);
    }

    [Fact]
    public void EmptyBenchmarkManagerShowsWithUsableControlsAndLegalSplitters()
    {
        Exception? failure = null;
        string? status = null;
        int[]? normalDistances = null;
        int[]? minimumDistances = null;
        var thread = new Thread(() =>
        {
            AiBenchmarkManagerForm? form = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                string path = Path.Combine(Path.GetTempPath(), "MediaFluxAiBenchmarkManagerTests", Guid.NewGuid().ToString("N"), "empty.db");
                form = new AiBenchmarkManagerForm(new AiBenchmarkManagementService(new AiBenchmarkDatabase(path)), new Config(), Path.Combine(Path.GetDirectoryName(path)!, "config.json"));
                form.Show();
                Application.DoEvents();
                Assert.True(form.Controls.Find("benchmarkCommands", true).Single().Bounds.Width > 0);
                AssertInsideWorkingArea(form);
                Assert.True(form.Controls.Find("benchmarkGrid", true).Single().Bounds.Size.Width > 0);
                Assert.True(form.Controls.Find("benchmarkDetails", true).Single().Bounds.Size.Width > 0);
                Assert.True(form.Controls.Find("benchmarkComparison", true).Single().Bounds.Size.Width > 0);
                status = form.Controls.Find("benchmarkStatus", true).Single().Text;
                SplitContainer main = form.Controls.Find("benchmarkMainSplit", true).OfType<SplitContainer>().Single();
                SplitContainer lower = form.Controls.Find("benchmarkDetailsComparisonSplit", true).OfType<SplitContainer>().Single();
                normalDistances = new[] { main.SplitterDistance, lower.SplitterDistance };
                AssertLegal(main);
                AssertLegal(lower);
                AssertContained(form.Controls.Find("benchmarkDetails", true).Single());
                AssertContained(form.Controls.Find("benchmarkComparison", true).Single());
                AssertContained(form.Controls.Find("benchmarkFpsChart", true).Single());
                form.Size = form.MinimumSize;
                Application.DoEvents();
                minimumDistances = new[] { main.SplitterDistance, lower.SplitterDistance };
                AssertLegal(main);
                AssertLegal(lower);
                form.Size = new Size(1650, 1000);
                Application.DoEvents();
                AssertLegal(main);
                AssertLegal(lower);
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
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "The WinForms construction test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        Assert.Equal("No benchmark results.", status);
        Assert.NotNull(normalDistances);
        Assert.NotNull(minimumDistances);
    }

    private static void AssertLegal(SplitContainer splitter)
    {
        int available = (splitter.Orientation == Orientation.Vertical ? splitter.ClientSize.Width : splitter.ClientSize.Height) - splitter.SplitterWidth;
        Assert.InRange(splitter.SplitterDistance, splitter.Panel1MinSize, available - splitter.Panel2MinSize);
    }

    private static void AssertContained(Control control)
    {
        Assert.True(control.Width > 0 && control.Height > 0);
        Assert.True(new Rectangle(Point.Empty, control.Parent!.ClientSize).Contains(control.Bounds));
    }

    private static void AssertInsideWorkingArea(Form form)
    {
        Rectangle workingArea = Screen.FromControl(form).WorkingArea;
        Assert.True(workingArea.IntersectsWith(form.Bounds), $"Window must remain associated with the monitor working area: {form.Bounds} / {workingArea}.");
        Assert.True(form.Width > 0 && form.Height > 0, "Window must retain a usable size after clamping.");
    }

    private static AiBenchmarkRecord Record(long id, string model, int scale, double fps, bool stable) => new(id, Entry(model, fps, scale, stable));
    private static AiBenchmarkDatabaseEntry Entry(string model, double fps, int scale = 2, bool stable = true) => new(new("ncnn-vulkan", "backend-1", model, "GPU", "driver", "FP32", scale, "1080p"), NcnnRuntimeConfiguration.SafeDefault, fps, null, stable, DateTimeOffset.UtcNow, "test");
}
