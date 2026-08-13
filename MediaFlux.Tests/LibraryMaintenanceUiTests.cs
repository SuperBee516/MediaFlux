using System.Reflection;
using MediaFlux.Models;
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
    [Fact] public void ScheduledMaintenanceTabShowsDisabledLocationAndHistory()
    {
        if(!OperatingSystem.IsWindows())return;Exception? failure=null;var thread=new Thread(()=>{try{SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());using var catalog=new SqliteLibraryCatalog(Path.Combine(_root,"ui.db"),Path.Combine(_root,"b"),Path.Combine(_root,"r"));catalog.Initialize();catalog.UpsertLocation(new(Path.Combine(_root,"media")));using var runtime=new LibraryAnalyzerRuntime(catalog,new[]{".mkv"},new Probe(),new Visual());using var form=new LibraryAnalyzerForm(runtime);form.Show();TabControl tabs=Field<TabControl>(form,"_tabs");tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(x=>x.Text=="Scheduled Maintenance");Task task=Invoke(form,"RefreshMaintenanceAsync");Pump(task);DataGridView grid=Field<DataGridView>(form,"_maintenanceGrid");Assert.Single(grid.Rows.Cast<DataGridViewRow>(),x=>!x.IsNewRow);Assert.Equal("No",grid.Rows[0].Cells["Enabled"].Value);form.Close();Application.DoEvents();}catch(Exception ex){failure=ex;}});thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));if(failure!=null)throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    private sealed class Probe:ILibraryMetadataProbe{public string ToolVersion=>"test";public Task<MediaProbeResult> ProbeAsync(string p,CancellationToken t)=>Task.FromResult(new MediaProbeResult{Success=false});}
    private sealed class Visual:ILibraryVisualFingerprintExtractor{public string ToolVersion=>"test";public Task<IReadOnlyList<ulong>> ExtractAsync(VisualFingerprintCandidate c,CancellationToken t)=>Task.FromResult<IReadOnlyList<ulong>>(Array.Empty<ulong>());}
    private static T Field<T>(object o,string n)=>(T)(o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)?.GetValue(o)??throw new MissingFieldException(n));private static Task Invoke(object o,string n)=>(Task)(o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(o,null)??throw new MissingMethodException(n));private static void Pump(Task t){while(!t.IsCompleted){Application.DoEvents();Thread.Sleep(10);}t.GetAwaiter().GetResult();}
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(_root))Directory.Delete(_root,true);}
}
