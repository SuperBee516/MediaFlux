using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class HelpGuideTests
{
    [Fact]
    public void ShippedGuideLoadsRequiredTopicsAndRelatedLinks()
    {
        HelpGuideDocument guide = new HelpGuideService().LoadDefault();
        Assert.Null(guide.Error);
        string[] required = ["getting-started", "encode-queue-encoding", "presets-and-encoder-settings", "encoder-benchmark-and-diagnostics", "library-analyzer", "statistics", "storage-optimization", "duplicates-exact", "duplicates-visual", "duplicate-families", "keeper-and-review-concepts", "duplicate-cleanup", "scheduled-maintenance", "file-and-context-menu-actions", "settings", "troubleshooting-and-diagnostics"];
        Assert.All(required, id => Assert.NotNull(guide.FindTopic(id)));
        Assert.Contains("duplicate-cleanup", guide.FindTopic("storage-optimization")!.RelatedTopicIds);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, HelpGuideService.RelativeGuidePath)));
    }

    [Fact]
    public void MissingAndMalformedGuidesShowSafeFallbackTopic()
    {
        var service = new HelpGuideService();
        HelpGuideDocument missing = service.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.md"));
        HelpGuideDocument malformed = service.Parse("A document without a level-two topic");
        Assert.NotNull(missing.Error); Assert.Equal("guide-unavailable", Assert.Single(missing.Topics).Id);
        Assert.NotNull(malformed.Error); Assert.Equal("guide-unavailable", Assert.Single(malformed.Topics).Id);
    }

    [Fact]
    public void ViewerNavigatesTopicsAndRendersRelatedLinks()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var guide = new HelpGuideService().Parse("# Guide\n\n## First Topic\n\nSee [Second Topic](#second-topic).\n\n## Second Topic\n\nDone.");
                using var form = new HelpGuideForm(guide, "second-topic");
                Assert.True(form.SelectTopic("first-topic"));
                TreeView tree = (TreeView)(typeof(HelpGuideForm).GetField("_topics", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) ?? throw new MissingFieldException("_topics"));
                Assert.Equal("First Topic", tree.SelectedNode!.Text);
                FlowLayoutPanel related = (FlowLayoutPanel)(typeof(HelpGuideForm).GetField("_related", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) ?? throw new MissingFieldException("_related"));
                Assert.Contains(related.Controls.OfType<LinkLabel>(), link => link.Text == "Second Topic");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void HelpMenuExposesUserGuideWithF1Shortcut()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm();
                MenuStrip menu = form.Controls.OfType<MenuStrip>().First();
                ToolStripMenuItem guide = AllItems(menu.Items).OfType<ToolStripMenuItem>().Single(item => item.Name == "userGuideToolStripMenuItem");
                Assert.Equal("User Guide", guide.Text); Assert.Equal(Keys.F1, guide.ShortcutKeys); Assert.True(form.KeyPreview);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<ToolStripItem> AllItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items) { yield return item; if (item is ToolStripDropDownItem dropDown) foreach (ToolStripItem child in AllItems(dropDown.DropDownItems)) yield return child; }
    }
}
