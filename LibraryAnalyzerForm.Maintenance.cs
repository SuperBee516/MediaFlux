using MediaFlux.Services.LibraryCatalog;
using System.Numerics;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly DataGridView _maintenanceGrid=CreateGrid(); private readonly DataGridView _maintenanceHistory=CreateGrid();
    private readonly Label _maintenanceStatus=new(){Dock=DockStyle.Bottom,Height=30,Padding=new Padding(8,7,0,0),Text="Scheduled maintenance is disabled until enabled per location."};

    private void BuildScheduledMaintenanceTab()
    {
        var tab=new TabPage("Scheduled Maintenance"){Padding=new Padding(10)};var actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,WrapContents=true};
        AddButton(actions,"Edit schedule…",(_,_)=>EditSelectedMaintenance());
        AddButton(actions,"Run Now",async(_,_)=>await RunSelectedMaintenanceAsync());
        AddButton(actions,"Enable / disable",async(_,_)=>await ToggleSelectedMaintenanceAsync());
        AddButton(actions,"Pause / defer current",(_,_)=>_runtime.Maintenance.DeferCurrent());
        AddButton(actions,"Refresh",async(_,_)=>await RefreshMaintenanceAsync());
        AddMaintenanceColumn(_maintenanceGrid,"Location",260);AddMaintenanceColumn(_maintenanceGrid,"Enabled",70);AddMaintenanceColumn(_maintenanceGrid,"Schedule",120);AddMaintenanceColumn(_maintenanceGrid,"Window",105);AddMaintenanceColumn(_maintenanceGrid,"Next run",135);AddMaintenanceColumn(_maintenanceGrid,"Last run",135);AddMaintenanceColumn(_maintenanceGrid,"Status",110);AddMaintenanceColumn(_maintenanceGrid,"Actions",360,true);
        AddMaintenanceColumn(_maintenanceHistory,"Started",135);AddMaintenanceColumn(_maintenanceHistory,"Location",240);AddMaintenanceColumn(_maintenanceHistory,"Trigger",80);AddMaintenanceColumn(_maintenanceHistory,"Outcome",90);AddMaintenanceColumn(_maintenanceHistory,"Counts",260);AddMaintenanceColumn(_maintenanceHistory,"Details",420,true);
        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=360,Panel1MinSize=170,Panel2MinSize=120};
        split.Panel1.Controls.Add(_maintenanceGrid);split.Panel2.Controls.Add(_maintenanceHistory);tab.Controls.Add(split);tab.Controls.Add(_maintenanceStatus);tab.Controls.Add(actions);_tabs.TabPages.Add(tab);
    }

    private async Task RefreshMaintenanceAsync()
    {
        try
        {
            (IReadOnlyList<LibraryMaintenanceProfileView> profiles,IReadOnlyList<LibraryMaintenanceRun> history,IReadOnlyDictionary<long,string> paths)=await Task.Run(()=>
            {var p=_runtime.MaintenanceCatalog.GetMaintenanceProfiles(DateTime.UtcNow);var h=_runtime.MaintenanceCatalog.GetMaintenanceHistory(limit:100);return(p,h,_runtime.Catalog.GetLocations().ToDictionary(x=>x.Id,x=>x.Path));});
            if(IsDisposed)return;_maintenanceGrid.Rows.Clear();foreach(var v in profiles){LibraryMaintenanceProfile p=v.Profile;int row=_maintenanceGrid.Rows.Add(v.LocationPath,p.Enabled?"Yes":"No",p.Cadence,$"{p.StartTime:hh\\:mm}–{p.EndTime:hh\\:mm}",v.NextRunUtc?.ToLocalTime().ToString("g")??"—",v.LastRunUtc?.ToLocalTime().ToString("g")??"Never",v.LastOutcome?.ToString()??"Not run",DescribeMaintenanceActions(p));_maintenanceGrid.Rows[row].Tag=v;}
            _maintenanceHistory.Rows.Clear();foreach(var run in history){paths.TryGetValue(run.LocationId,out string? path);_maintenanceHistory.Rows.Add(run.StartedUtc.ToLocalTime().ToString("g"),path??$"Location {run.LocationId}",run.Trigger,run.Outcome,$"{run.NewFiles:N0} new · {run.ChangedFiles:N0} changed · {run.IntegrityQueued:N0} scrub",run.Details);}
        }catch(Exception ex){if(!IsDisposed)ShowError("Scheduled maintenance could not be refreshed.",ex);}
    }

    private LibraryMaintenanceProfileView? SelectedMaintenance()=>_maintenanceGrid.SelectedRows.Cast<DataGridViewRow>().Select(x=>x.Tag).OfType<LibraryMaintenanceProfileView>().FirstOrDefault();
    private async Task RunSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;_maintenanceStatus.Text=$"Starting maintenance for {selected.LocationPath}…";await _runtime.Maintenance.RunNowAsync(selected.Profile.LocationId);await RefreshMaintenanceAsync();}
    private async Task ToggleSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;await Task.Run(()=>_runtime.MaintenanceCatalog.SaveMaintenanceProfile(selected.Profile with{Enabled=!selected.Profile.Enabled,UpdatedUtc=DateTime.UtcNow}));await RefreshMaintenanceAsync();}
    private void EditSelectedMaintenance()
    {
        var selected=SelectedMaintenance();if(selected==null)return;LibraryMaintenanceProfile p=selected.Profile;
        using var dialog=new MediaFluxForm{Text="Scheduled Maintenance",Size=new Size(620,650),MinimumSize=new Size(560,590),StartPosition=FormStartPosition.CenterParent};
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(16)};
        var enabled=new CheckBox{Text="Enable scheduled maintenance for this location",Checked=p.Enabled,AutoSize=true};
        var cadence=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=220};cadence.Items.AddRange(Enum.GetNames<LibraryMaintenanceCadence>());cadence.SelectedIndex=(int)p.Cadence;
        var start=TimePicker(p.StartTime);var end=TimePicker(p.EndTime);var missed=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=240};missed.Items.AddRange(Enum.GetNames<LibraryMaintenanceMissedRun>());missed.SelectedIndex=(int)p.MissedRun;
        var days=new CheckedListBox{Height=100,Width=260,CheckOnClick=true};foreach(DayOfWeek day in Enum.GetValues<DayOfWeek>())days.Items.Add(day,(p.Days&(LibraryMaintenanceDays)(1<<(int)day))!=0);
        var checks=Enum.GetValues<LibraryMaintenanceActions>().Where(x=>x!=LibraryMaintenanceActions.None&&x!=LibraryMaintenanceActions.Default&&BitOperations.IsPow2((uint)x)).Select(action=>new CheckBox{Text=ActionLabel(action),Checked=p.Actions.HasFlag(action),AutoSize=true,Tag=action}).ToArray();
        var age=new NumericUpDown{Minimum=0,Maximum=3650,Value=p.PeriodicQuickScrubDays,Width=90};
        panel.Controls.Add(new Label{Text=selected.LocationPath,AutoSize=true,MaximumSize=new Size(550,0),ForeColor=LibraryAnalyzerAccentColor});panel.Controls.Add(enabled);AddLabeled(panel,"Schedule",cadence);AddLabeled(panel,"Window starts",start);AddLabeled(panel,"Window ends",end);AddLabeled(panel,"Missed runs",missed);AddLabeled(panel,"Weekly days",days);panel.Controls.Add(new Label{Text="Maintenance actions",AutoSize=true,Font=new Font(Font,FontStyle.Bold)});panel.Controls.AddRange(checks);AddLabeled(panel,"Periodic Quick Scrub age in days (0 disables)",age);
        var save=new Button{Text="Save",AutoSize=true};var cancel=new Button{Text="Cancel",AutoSize=true};save.Click+=(_,_)=>dialog.DialogResult=DialogResult.OK;cancel.Click+=(_,_)=>dialog.DialogResult=DialogResult.Cancel;var buttons=new FlowLayoutPanel{AutoSize=true};buttons.Controls.Add(save);buttons.Controls.Add(cancel);panel.Controls.Add(buttons);dialog.Controls.Add(panel);dialog.AcceptButton=save;dialog.CancelButton=cancel;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;LibraryMaintenanceDays selectedDays=LibraryMaintenanceDays.None;for(int i=0;i<days.Items.Count;i++)if(days.GetItemChecked(i))selectedDays|=(LibraryMaintenanceDays)(1<<i);LibraryMaintenanceActions selectedActions=checks.Where(x=>x.Checked).Aggregate(LibraryMaintenanceActions.None,(value,x)=>value|(LibraryMaintenanceActions)x.Tag!);
        _runtime.MaintenanceCatalog.SaveMaintenanceProfile(p with{Enabled=enabled.Checked,Cadence=(LibraryMaintenanceCadence)cadence.SelectedIndex,Days=selectedDays,StartTime=start.Value.TimeOfDay,EndTime=end.Value.TimeOfDay,MissedRun=(LibraryMaintenanceMissedRun)missed.SelectedIndex,Actions=selectedActions,PeriodicQuickScrubDays=(int)age.Value,UpdatedUtc=DateTime.UtcNow});_ = RefreshMaintenanceAsync();
    }

    private static DateTimePicker TimePicker(TimeSpan value)=>new(){Format=DateTimePickerFormat.Time,ShowUpDown=true,Width=120,Value=DateTime.Today+value};
    private static void AddLabeled(FlowLayoutPanel panel,string text,Control control){panel.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(3,10,3,0)});panel.Controls.Add(control);}
    private static void AddMaintenanceColumn(DataGridView grid,string name,int width,bool fill=false){var c=new DataGridViewTextBoxColumn{Name=name,HeaderText=name,Width=width,ReadOnly=true};if(fill)c.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.Columns.Add(c);}
    private static string DescribeMaintenanceActions(LibraryMaintenanceProfile p)=>string.Join(", ",Enum.GetValues<LibraryMaintenanceActions>().Where(x=>x!=LibraryMaintenanceActions.None&&x!=LibraryMaintenanceActions.Default&&BitOperations.IsPow2((uint)x)&&p.Actions.HasFlag(x)).Select(ActionLabel).Append(p.PeriodicQuickScrubDays>0?$"Quick Scrub > {p.PeriodicQuickScrubDays} days":"").Where(x=>x.Length>0));
    private static string ActionLabel(LibraryMaintenanceActions action)=>action switch{LibraryMaintenanceActions.IncrementalScan=>"Incremental scan",LibraryMaintenanceActions.Metadata=>"Metadata refresh",LibraryMaintenanceActions.ExactDuplicates=>"Exact duplicate refresh",LibraryMaintenanceActions.VisualDuplicates=>"Visual duplicate refresh",LibraryMaintenanceActions.QuickScrubNew=>"Quick Scrub new files",LibraryMaintenanceActions.QuickScrubNeverChecked=>"Quick Scrub never checked",LibraryMaintenanceActions.QuickScrubStale=>"Quick Scrub stale",LibraryMaintenanceActions.QuickScrubFailed=>"Retry failed/interrupted Quick Scrubs",_=>action.ToString()};
    private void Maintenance_ProgressChanged(LibraryMaintenanceProgress p){if(IsDisposed||!IsHandleCreated)return;BeginInvoke(()=>_maintenanceStatus.Text=$"{p.Stage}: {p.Details}".TrimEnd(' ',':'));}
}
