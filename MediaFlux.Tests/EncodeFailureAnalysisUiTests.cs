using System.Reflection;
using System.Windows.Forms;
using MediaFlux.Models;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class EncodeFailureAnalysisUiTests
{
    [Fact]
    public void DetailsAnalysisFollowsSelectedFailedRowAndClearsForNoSelection()
    {
        if (!OperatingSystem.IsWindows()) return;

        Exception? failure = null;
        string source = Path.Combine(Path.GetTempPath(), $"failure-details-{Guid.NewGuid():N}.mkv");
        MainForm? main = null;
        var thread = new Thread(() =>
        {
            try
            {
                File.WriteAllBytes(source, [1, 2, 3]);
                main = new MainForm();
                main.CreateControl();
                main.Show();
                Application.DoEvents();
                Type formType = typeof(MainForm);
                TabControl tabs = (TabControl)(formType.GetField("_encodeInfoTabs", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
                    ?? throw new MissingFieldException("_encodeInfoTabs"));
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Details");
                Application.DoEvents();
                DataGridView queue = (DataGridView)(formType.GetField(
                    "dgvEncodeQueue", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
                    ?? throw new MissingFieldException("dgvEncodeQueue"));
                MethodInfo add = formType.GetMethod("AddEncodeItemIfNotPresent", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("AddEncodeItemIfNotPresent");
                Assert.True((bool)add.Invoke(main, [source, false, false])!);
                DataGridViewRow row = queue.Rows.Cast<DataGridViewRow>().Single();

                MethodInfo ensure = formType.GetMethod("EnsureRowMeta", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("EnsureRowMeta");
                object meta = ensure.Invoke(main, [row])!;
                meta.GetType().GetField("FailureAnalysis", BindingFlags.Instance | BindingFlags.Public)!.SetValue(
                    meta,
                    new EncodeFailureAnalysis(
                        EncodeFailureCategory.VideoEncoder,
                        "Video encoder initialization",
                        "The requested video encoder could not initialize.",
                        "NVENC was unavailable.",
                        "Select a software encoder.",
                        "Unknown encoder 'hevc_nvenc'.",
                        EncodeFailureConfidence.High,
                        new EncodeFailurePlanContext("GPU (NVENC)", "hevc_nvenc", "p5", "8-bit", "Mp4", "Off")));
                row.Cells["colStatus"].Value = "Failed";
                Assert.Equal("Failed", row.Cells["colStatus"].Value?.ToString());
                Assert.NotNull(meta.GetType().GetField("FailureAnalysis", BindingFlags.Instance | BindingFlags.Public)!.GetValue(meta));

                MethodInfo update = formType.GetMethod("UpdateFailureAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException("UpdateFailureAnalysis");
                update.Invoke(main, new object[] { new[] { row } });

                GroupBox group = (GroupBox)(formType.GetField("_failureAnalysisGroup", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
                    ?? throw new MissingFieldException("_failureAnalysisGroup"));
                Label stage = (Label)(formType.GetField("_failureAnalysisStageLabel", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(main)
                    ?? throw new MissingFieldException("_failureAnalysisStageLabel"));
                Assert.Equal("Video encoder initialization", stage.Text);
                Assert.True(group.Visible);

                update.Invoke(main, new object[] { Array.Empty<DataGridViewRow>() });
                Assert.False(group.Visible);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { File.Delete(source); } catch { }
                WinFormsTestLifecycle.CloseAndDispose(main);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Failure-analysis UI smoke test timed out.");
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
