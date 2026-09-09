using System.Reflection;
using MediaFlux.Models;
using System.Drawing;
using System.Windows.Forms;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

[Collection("LibraryAnalyzerUi")]
public sealed class LibraryMaintenanceUiTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"MediaFlux-MaintenanceUi",Guid.NewGuid().ToString("N"));
    public LibraryMaintenanceUiTests()=>Directory.CreateDirectory(_root);
    [Fact] public void ScheduledMaintenanceTabShowsAnalysisModeConflictAndDisabledLocation()
    {
        if(!OperatingSystem.IsWindows())return;Exception? failure=null;var thread=new Thread(()=>{try{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());using var catalog=new SqliteLibraryCatalog(Path.Combine(_root,"ui.db"),Path.Combine(_root,"b"),Path.Combine(_root,"r"));catalog.Initialize();catalog.UpsertLocation(new(Path.Combine(_root,"media")));using var runtime=new LibraryAnalyzerRuntime(catalog,new[]{".mkv"},new Probe(),new Visual());using var form=new LibraryAnalyzerForm(runtime);form.Show();TabControl tabs=Field<TabControl>(form,"_tabs");tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(x=>x.Text=="Scheduled Maintenance");Task task=Invoke(form,"RefreshMaintenanceAsync");Pump(task);DataGridView grid=Field<DataGridView>(form,"_maintenanceGrid");Assert.Single(grid.Rows.Cast<DataGridViewRow>(),x=>!x.IsNewRow);Assert.Equal("No",grid.Rows[0].Cells["Enabled"].Value);Assert.Equal("Incremental",grid.Rows[0].Cells["Mode"].Value);Assert.Equal("Wait",grid.Rows[0].Cells["Conflict"].Value);Assert.Contains("Refresh / scan library catalog",grid.Rows[0].Cells["Actions"].Value?.ToString());DataGridView history=Field<DataGridView>(form,"_maintenanceHistory");Assert.Contains(history.Columns.Cast<DataGridViewColumn>(),x=>x.Name=="Jobs");Assert.Contains(history.Columns.Cast<DataGridViewColumn>(),x=>x.Name=="Mode");form.Close();Application.DoEvents();}catch(Exception ex){failure=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));if(failure!=null)throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    [Fact] public void MaintenanceProgressCarriesCountersCurrentItemAndCompletionState()
    {
        var running=new LibraryMaintenanceProgress(7,2,"Scanning","10 indexed","Y:\\Media",10,20,"Y:\\Media\\movie.mkv",false);
        Assert.Equal(10,running.Completed);Assert.Equal(20,running.Total);Assert.Equal("Y:\\Media\\movie.mkv",running.CurrentItem);Assert.False(running.IsIndeterminate);Assert.True(running.IsActive);
        var completed=running with{Outcome=LibraryMaintenanceOutcome.Completed,IsActive=false,IsIndeterminate=true};Assert.Equal(LibraryMaintenanceOutcome.Completed,completed.Outcome);Assert.False(completed.IsActive);
    }
    [Fact] public void VisualPreviewTogglePersistsAndMissingSelectionDoesNotStartProcessing()
    {
        if(!OperatingSystem.IsWindows())return;Exception? failure=null;var thread=new Thread(()=>{try{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());using var catalog=new SqliteLibraryCatalog(Path.Combine(_root,"live.db"),Path.Combine(_root,"b2"),Path.Combine(_root,"r2"));catalog.Initialize();using var runtime=new LibraryAnalyzerRuntime(catalog,new[]{".mkv"},new Probe(),new Visual());var state=new LibraryAnalyzerUiState{ShowVisualComparisonPreview=true};using var form=new LibraryAnalyzerForm(runtime,reviewOptions:new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(UiState:state));form.Show();CheckBox enabled=Field<CheckBox>(form,"_visualComparisonPreviewEnabled");SplitContainer details=Field<SplitContainer>(form,"_visualDetailSplit");Assert.True(enabled.Checked);enabled.Checked=false;form.GetType().GetMethod("ApplyVisualComparisonPreviewLayout",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,null);Assert.True(details.Panel2Collapsed);Assert.False(state.ShowVisualComparisonPreview);enabled.Checked=true;form.GetType().GetMethod("ApplyVisualComparisonPreviewLayout",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(form,null);Assert.False(details.Panel2Collapsed);VisualSimilarityMemberRecord missing=new(1,1,"Z:\\missing.mkv","Z:\\",1,DateTime.UtcNow,IndexedFileAvailability.Missing,"",null,null,null,null,false,false,false,false,"");Task preview=(Task)(form.GetType().GetMethod("UpdateVisualComparisonPreviewAsync",BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(form,new object[]{new[]{missing}})??throw new MissingMethodException());Pump(preview);Assert.Contains("unavailable",Field<Label>(form,"_visualPreviewStatus").Text,StringComparison.OrdinalIgnoreCase);form.Close();Application.DoEvents();}catch(Exception ex){failure=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));if(failure!=null)throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    [Fact]
    public void VisualPreviewSharesResultsRowAndLeavesMembersFullWidth()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "visual-layout.db"), Path.Combine(_root, "b3"), Path.Combine(_root, "r3"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new Probe(), new Visual());
                var state = new LibraryAnalyzerUiState { ShowVisualComparisonPreview = true };
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(UiState: state));
                form.Show();
                DataGridView groups = Field<DataGridView>(form, "_visualGroupsGrid");
                DataGridView members = Field<DataGridView>(form, "_visualMembersGrid");
                Panel preview = Field<Panel>(form, "_visualComparisonPreview");
                SplitContainer top = Field<SplitContainer>(form, "_visualDetailSplit");
                SplitContainer outer = Field<SplitContainer>(form, "_visualResultsMembersSplit");
                Assert.Equal(Orientation.Vertical, top.Orientation);
                Assert.Equal(Orientation.Horizontal, outer.Orientation);
                Assert.Same(top.Panel1, groups.Parent);
                Assert.Same(top.Panel2, preview.Parent);
                Assert.Same(outer.Panel2, members.Parent);

                top.SplitterDistance = 300;
                CheckBox enabled = Field<CheckBox>(form, "_visualComparisonPreviewEnabled");
                enabled.Checked = false;
                Application.DoEvents();
                Assert.True(top.Panel2Collapsed);
                Assert.Same(top.Panel1, groups.Parent);
                Assert.Same(outer.Panel2, members.Parent);
                enabled.Checked = true;
                Application.DoEvents();
                top.SplitterDistance = 300;
                Application.DoEvents();
                int expectedSplitterDistance = top.SplitterDistance;
                form.Close();
                Application.DoEvents();
                Assert.Contains(state.SplitterDistances, entry => entry.Key.Contains("Duplicates — Visual", StringComparison.Ordinal) && entry.Value == expectedSplitterDistance);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    [Fact]
    public void VisualPreviewWorkspaceUsesSafeResizableBoundsAndRestoresBothSplitters()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "visual-workspace.db"), Path.Combine(_root, "b4"), Path.Combine(_root, "r4"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new Probe(), new Visual());
                var state = new LibraryAnalyzerUiState { ShowVisualComparisonPreview = true };
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(UiState: state));
                form.Show();
                TabControl tabs = Field<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Duplicates — Visual");
                Application.DoEvents();
                SplitContainer details = Field<SplitContainer>(form, "_visualDetailSplit");
                SplitContainer outer = Field<SplitContainer>(form, "_visualResultsMembersSplit");
                Panel preview = Field<Panel>(form, "_visualComparisonPreview");
                PictureBox left = Field<PictureBox>(form, "_visualPreviewLeft");
                PictureBox right = Field<PictureBox>(form, "_visualPreviewRight");
                Label leftStatus = Field<Label>(form, "_visualPreviewLeftStatus");
                Label rightStatus = Field<Label>(form, "_visualPreviewRightStatus");
                Label previewStatus = Field<Label>(form, "_visualPreviewStatus");
                Assert.Equal(PictureBoxSizeMode.Zoom, left.SizeMode);
                Assert.Equal(PictureBoxSizeMode.Zoom, right.SizeMode);
                Assert.True(leftStatus.Visible && rightStatus.Visible && previewStatus.Visible);

                foreach (Size size in new[] { new Size(1100, 700), new Size(1360, 840), new Size(1800, 1100) })
                {
                    form.Size = size;
                    Application.DoEvents();
                    Assert.False(details.Panel2Collapsed);
                    Assert.True(details.Panel2.Width >= details.Panel2MinSize);
                    Assert.InRange(details.SplitterDistance, details.Panel1MinSize,
                        details.ClientSize.Width - details.SplitterWidth - details.Panel2MinSize);
                    Assert.InRange(outer.SplitterDistance, outer.Panel1MinSize,
                        outer.ClientSize.Height - outer.SplitterWidth - outer.Panel2MinSize);
                    Assert.True(preview.Visible);
                    Assert.True(preview.ClientSize.Height > 0);
                }

                int initialPreviewHeight = preview.ClientSize.Height;
                int maximumOuterDistance = outer.ClientSize.Height - outer.SplitterWidth - outer.Panel2MinSize;
                outer.SplitterDistance = Math.Min(maximumOuterDistance, outer.SplitterDistance + 120);
                Application.DoEvents();
                Assert.True(preview.ClientSize.Height > initialPreviewHeight);

                int savedDetail = details.SplitterDistance;
                int savedOuter = outer.SplitterDistance;
                Size persistedSize = form.Size;
                form.Close();
                Application.DoEvents();
                Assert.Contains(state.SplitterDistances, entry => entry.Key.EndsWith(".Split1", StringComparison.Ordinal) && entry.Value == savedDetail);
                Assert.Contains(state.SplitterDistances, entry => entry.Key.EndsWith(".Split0", StringComparison.Ordinal) && entry.Value == savedOuter);

                using var restored = new LibraryAnalyzerForm(runtime,
                    reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(UiState: state));
                restored.Show();
                TabControl restoredTabs = Field<TabControl>(restored, "_tabs");
                restoredTabs.SelectedTab = restoredTabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Duplicates — Visual");
                restored.Size = persistedSize;
                Application.DoEvents();
                SplitContainer restoredDetails = Field<SplitContainer>(restored, "_visualDetailSplit");
                SplitContainer restoredOuter = Field<SplitContainer>(restored, "_visualResultsMembersSplit");
                Assert.InRange(restoredDetails.SplitterDistance, restoredDetails.Panel1MinSize,
                    restoredDetails.ClientSize.Width - restoredDetails.SplitterWidth - restoredDetails.Panel2MinSize);
                Assert.Contains(state.SplitterDistances, entry => entry.Key.EndsWith(".Split1", StringComparison.Ordinal) && entry.Value == restoredDetails.SplitterDistance);
                Assert.InRange(restoredOuter.SplitterDistance, restoredOuter.Panel1MinSize,
                    restoredOuter.ClientSize.Height - restoredOuter.SplitterWidth - restoredOuter.Panel2MinSize);
                Assert.Contains(state.SplitterDistances, entry => entry.Key.EndsWith(".Split0", StringComparison.Ordinal) && entry.Value == restoredOuter.SplitterDistance);
                CheckBox enabled = Field<CheckBox>(restored, "_visualComparisonPreviewEnabled");
                enabled.Checked = false;
                Application.DoEvents();
                Assert.True(restoredDetails.Panel2Collapsed);
                Assert.Equal(restoredDetails.Panel1.ClientSize.Width, restoredDetails.ClientSize.Width);
                restored.Close();
                Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    [Fact]
    public void VisualPreviewFocusKeepsReviewCommandsAndRestoresWorkspace()
    {
        if (!OperatingSystem.IsWindows()) return;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "visual-focus.db"), Path.Combine(_root, "b5"), Path.Combine(_root, "r5"));
                catalog.Initialize();
                using var runtime = new LibraryAnalyzerRuntime(catalog, new[] { ".mkv" }, new Probe(), new Visual());
                var state = new LibraryAnalyzerUiState { ShowVisualComparisonPreview = true };
                using var form = new LibraryAnalyzerForm(runtime, reviewOptions: new LibraryAnalyzerForm.LibraryAnalyzerReviewOptions(UiState: state));
                form.Show();
                TabControl tabs = Field<TabControl>(form, "_tabs");
                tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(page => page.Text == "Duplicates — Visual");
                Application.DoEvents();
                SplitContainer workspace = Field<SplitContainer>(form, "_visualResultsMembersSplit");
                Button focus = Field<Button>(form, "_visualPreviewFocusButton");
                Control actionArea = form.Controls.Cast<Control>().SelectMany(Descendants).First(control => control.Name == "VisualActionArea");
                foreach (string text in new[] { "Review & Compare…", "Keep Selected File", "Protect Selected File", "Mark Match as Reviewed", "Ignore This Match", "Recheck This Match", "Preview Files to Delete…", "Previous match", "Next match" })
                    Assert.Contains(text, Descendants(actionArea).OfType<Button>().Select(button => button.Text));
                Assert.True(actionArea.Height < 140);
                int normal = workspace.SplitterDistance;
                focus.PerformClick();
                Application.DoEvents();
                Assert.Equal("Restore Workspace", focus.Text);
                Assert.True(workspace.SplitterDistance >= normal);
                Assert.Contains("match", Field<Label>(form, "_visualFocusMatchLabel").Text, StringComparison.OrdinalIgnoreCase);
                focus.PerformClick();
                Application.DoEvents();
                Assert.Equal("Expand Preview", focus.Text);
                Assert.Equal(normal, workspace.SplitterDistance);
                form.Close();
                Application.DoEvents();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    private sealed class Probe:ILibraryMetadataProbe{public string ToolVersion=>"test";public Task<MediaProbeResult> ProbeAsync(string p,CancellationToken t)=>Task.FromResult(new MediaProbeResult{Success=false});}
    private sealed class Visual:ILibraryVisualFingerprintExtractor{public string ToolVersion=>"test";public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate c,CancellationToken t)=>Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());}
    private static T Field<T>(object o,string n)=>(T)(o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(o)??throw new MissingFieldException(n));private static Task Invoke(object o,string n)=>(Task)(o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(o,null)??throw new MissingMethodException(n));private static void Pump(Task t){while(!t.IsCompleted){Application.DoEvents();Thread.Sleep(10);}t.GetAwaiter().GetResult();}
    private static IEnumerable<Control> Descendants(Control root){foreach(Control child in root.Controls){yield return child;foreach(Control descendant in Descendants(child))yield return descendant;}}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
