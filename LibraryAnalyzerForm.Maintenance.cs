using MediaFlux.Services.LibraryCatalog;
using System.Numerics;
using System.Diagnostics;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly DataGridView _maintenanceGrid=CreateGrid(); private readonly DataGridView _maintenanceHistory=CreateGrid();
    private readonly Label _maintenanceStatus=new(){Dock=DockStyle.Bottom,Height=30,Padding=new Padding(8,7,0,0),Text="Scheduled maintenance is disabled until enabled per location."};
    private readonly Label _maintenanceActivity=new(){Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(8,4,8,0),Text="No scheduled job is active."};
    private readonly Label _maintenanceCurrentItem=new(){Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(8,0,8,4),ForeColor=SystemColors.GrayText};
    private readonly ProgressBar _maintenanceProgress=new(){Dock=DockStyle.Fill,Style=ProgressBarStyle.Marquee,Visible=false};
    private long _lastMaintenanceUiUpdateTicks;

    private void BuildScheduledMaintenanceTab()
    {
        var tab=new TabPage("Scheduled Maintenance"){Padding=new Padding(10)};var actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42,WrapContents=true};
        AddButton(actions,"Edit schedule…",(_,_)=>EditSelectedMaintenance());
        AddButton(actions,"Remove schedule",async(_,_)=>await RemoveSelectedMaintenanceAsync());
        AddButton(actions,"Run Now",async(_,_)=>await RunSelectedMaintenanceAsync());
        AddButton(actions,"Enable / disable",async(_,_)=>await ToggleSelectedMaintenanceAsync());
        AddButton(actions,"Pause / defer current",(_,_)=>_runtime.Maintenance.DeferCurrent());
        AddButton(actions,"Refresh",async(_,_)=>await RefreshMaintenanceAsync());
        AddMaintenanceColumn(_maintenanceGrid,"Location",240);AddMaintenanceColumn(_maintenanceGrid,"Enabled",70);AddMaintenanceColumn(_maintenanceGrid,"Schedule",110);AddMaintenanceColumn(_maintenanceGrid,"Mode",105);AddMaintenanceColumn(_maintenanceGrid,"Conflict",85);AddMaintenanceColumn(_maintenanceGrid,"Window",105);AddMaintenanceColumn(_maintenanceGrid,"Next run",135);AddMaintenanceColumn(_maintenanceGrid,"Last run",135);AddMaintenanceColumn(_maintenanceGrid,"Status",110);AddMaintenanceColumn(_maintenanceGrid,"Actions",360,true);
        AddMaintenanceColumn(_maintenanceHistory,"Started",135);AddMaintenanceColumn(_maintenanceHistory,"Location",220);AddMaintenanceColumn(_maintenanceHistory,"Jobs",230);AddMaintenanceColumn(_maintenanceHistory,"Mode",95);AddMaintenanceColumn(_maintenanceHistory,"Trigger",80);AddMaintenanceColumn(_maintenanceHistory,"Outcome",90);AddMaintenanceColumn(_maintenanceHistory,"Counts",260);AddMaintenanceColumn(_maintenanceHistory,"Details",420,true);
        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=360,Panel1MinSize=170,Panel2MinSize=120};
        split.Panel1.Controls.Add(_maintenanceGrid);split.Panel2.Controls.Add(_maintenanceHistory);
        var activity=new TableLayoutPanel{Dock=DockStyle.Top,Height=58,ColumnCount=2,RowCount=2,Padding=new Padding(0,2,0,2)};
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));activity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,220));
        activity.RowStyles.Add(new RowStyle(SizeType.Percent,50));activity.RowStyles.Add(new RowStyle(SizeType.Percent,50));
        activity.Controls.Add(_maintenanceActivity,0,0);activity.Controls.Add(_maintenanceCurrentItem,0,1);activity.Controls.Add(_maintenanceProgress,1,0);activity.SetRowSpan(_maintenanceProgress,2);
        tab.Controls.Add(split);tab.Controls.Add(_maintenanceStatus);tab.Controls.Add(activity);tab.Controls.Add(actions);_tabs.TabPages.Add(tab);
    }

    private async Task RefreshMaintenanceAsync()
    {
        try
        {
            (IReadOnlyList<LibraryMaintenanceProfileView> profiles,IReadOnlyList<LibraryMaintenanceRun> history,IReadOnlyDictionary<long,string> paths)=await Task.Run(()=>
            {var p=_runtime.MaintenanceCatalog.GetMaintenanceProfiles(DateTime.UtcNow);var h=_runtime.MaintenanceCatalog.GetMaintenanceHistory(limit:100);return(p,h,_runtime.Catalog.GetLocations().ToDictionary(x=>x.Id,x=>x.Path));});
            if(IsDisposed)return;_maintenanceGrid.Rows.Clear();foreach(var v in profiles){LibraryMaintenanceProfile p=v.Profile;int row=_maintenanceGrid.Rows.Add(v.LocationPath,p.Enabled?"Yes":"No",p.Cadence,p.AnalysisMode==LibraryMaintenanceAnalysisMode.FullReanalysis?"Full":"Incremental",p.ConflictBehavior==LibraryMaintenanceConflictBehavior.Skip?"Skip":"Wait",$"{p.StartTime:hh\\:mm}–{p.EndTime:hh\\:mm}",v.NextRunUtc?.ToLocalTime().ToString("g")??"—",v.LastRunUtc?.ToLocalTime().ToString("g")??"Never",v.LastOutcome?.ToString()??"Not run",DescribeMaintenanceActions(p));_maintenanceGrid.Rows[row].Tag=v;}
            _maintenanceHistory.Rows.Clear();foreach(var run in history){paths.TryGetValue(run.LocationId,out string? path);string outcome=run.Stage=="Skipped"?"Skipped":run.Outcome.ToString();_maintenanceHistory.Rows.Add(run.StartedUtc.ToLocalTime().ToString("g"),path??$"Location {run.LocationId}",DescribeMaintenanceActions(run.Actions,run.AnalyzeFamilies),run.AnalysisMode==LibraryMaintenanceAnalysisMode.FullReanalysis?"Full":"Incremental",run.Trigger,outcome,$"{run.NewFiles:N0} new · {run.ChangedFiles:N0} changed · {run.MetadataQueued:N0} metadata · {run.ExactProcessed:N0} exact · {run.VisualProcessed:N0} visual",run.Details);}
        }catch(Exception ex){if(!IsDisposed)ShowError("Scheduled maintenance could not be refreshed.",ex);}
    }

    private LibraryMaintenanceProfileView? SelectedMaintenance()=>_maintenanceGrid.SelectedRows.Cast<DataGridViewRow>().Select(x=>x.Tag).OfType<LibraryMaintenanceProfileView>().FirstOrDefault();
    private async Task RunSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;_maintenanceStatus.Text=$"Starting maintenance for {selected.LocationPath}…";await _runtime.Maintenance.RunNowAsync(selected.Profile.LocationId);await RefreshMaintenanceAsync();}
    private async Task ToggleSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;await Task.Run(()=>_runtime.MaintenanceCatalog.SaveMaintenanceProfile(selected.Profile with{Enabled=!selected.Profile.Enabled,UpdatedUtc=DateTime.UtcNow}));await RefreshMaintenanceAsync();}
    private async Task RemoveSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;if(MessageBox.Show(this,$"Remove the scheduled analysis job for {selected.LocationPath}?\n\nThe library location and job history will be kept.","Remove Scheduled Analysis",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;LibraryMaintenanceProfile defaults=new(selected.Profile.LocationId,selected.Profile.Version,false,LibraryMaintenanceCadence.ManualOnly,LibraryMaintenanceDays.All,TimeSpan.FromHours(1),TimeSpan.FromHours(6),LibraryMaintenanceMissedRun.RunAtNextWindow,LibraryMaintenanceActions.Default,0,selected.Profile.CreatedUtc,DateTime.UtcNow,selected.Profile.LastScheduledUtc);await Task.Run(()=>_runtime.MaintenanceCatalog.SaveMaintenanceProfile(defaults));await RefreshMaintenanceAsync();}
    private void EditSelectedMaintenance()
    {
        var selected=SelectedMaintenance();if(selected==null)return;LibraryMaintenanceProfile p=selected.Profile;
        using var dialog=new MediaFluxForm{Text="Scheduled Library Analysis",Size=new Size(640,760),MinimumSize=new Size(580,680),StartPosition=FormStartPosition.CenterParent};
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(16)};
        var enabled=new CheckBox{Text="Enable scheduled maintenance for this location",Checked=p.Enabled,AutoSize=true};
        var cadence=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=220};cadence.Items.AddRange(Enum.GetNames<LibraryMaintenanceCadence>());cadence.SelectedIndex=(int)p.Cadence;
        var mode=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=220};mode.Items.AddRange(new object[]{"Incremental (new, changed, or missing analysis)","Full Reanalysis"});mode.SelectedIndex=(int)p.AnalysisMode;
        var conflict=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=280};conflict.Items.AddRange(new object[]{"Wait until active encoding is finished","Skip this occurrence"});conflict.SelectedIndex=p.ConflictBehavior==LibraryMaintenanceConflictBehavior.Skip?1:0;
        var start=TimePicker(p.StartTime);var end=TimePicker(p.EndTime);var missed=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=240};missed.Items.AddRange(Enum.GetNames<LibraryMaintenanceMissedRun>());missed.SelectedIndex=(int)p.MissedRun;
        var days=new CheckedListBox{Height=100,Width=260,CheckOnClick=true};foreach(DayOfWeek day in Enum.GetValues<DayOfWeek>())days.Items.Add(day,(p.Days&(LibraryMaintenanceDays)(1<<(int)day))!=0);
        var checks=Enum.GetValues<LibraryMaintenanceActions>().Where(x=>x!=LibraryMaintenanceActions.None&&x!=LibraryMaintenanceActions.Default&&BitOperations.IsPow2((uint)x)).Select(action=>new CheckBox{Text=ActionLabel(action),Checked=p.Actions.HasFlag(action),AutoSize=true,Tag=action}).ToArray();
        var families=new CheckBox{Text="Analyze Duplicate Families (generates missing visual fingerprints first)",Checked=p.AnalyzeFamilies,AutoSize=true};
        var age=new NumericUpDown{Minimum=0,Maximum=3650,Value=p.PeriodicQuickScrubDays,Width=90};
        panel.Controls.Add(new Label{Text=selected.LocationPath,AutoSize=true,MaximumSize=new Size(550,0),ForeColor=LibraryAnalyzerAccentColor});panel.Controls.Add(enabled);AddLabeled(panel,"Schedule",cadence);AddLabeled(panel,"Analysis mode",mode);AddLabeled(panel,"When encoding is active",conflict);AddLabeled(panel,"Window starts",start);AddLabeled(panel,"Window ends",end);AddLabeled(panel,"Missed runs",missed);AddLabeled(panel,"Weekly days",days);panel.Controls.Add(new Label{Text="Maintenance and analysis jobs",AutoSize=true,Font=new Font(Font,FontStyle.Bold)});panel.Controls.AddRange(checks);panel.Controls.Add(families);AddLabeled(panel,"Periodic Quick Scrub age in days (0 disables)",age);
        var save=new Button{Text="Save",AutoSize=true};var cancel=new Button{Text="Cancel",AutoSize=true};save.Click+=(_,_)=>dialog.DialogResult=DialogResult.OK;cancel.Click+=(_,_)=>dialog.DialogResult=DialogResult.Cancel;var buttons=new FlowLayoutPanel{AutoSize=true};buttons.Controls.Add(save);buttons.Controls.Add(cancel);panel.Controls.Add(buttons);dialog.Controls.Add(panel);dialog.AcceptButton=save;dialog.CancelButton=cancel;
        if(dialog.ShowDialog(this)!=DialogResult.OK)return;LibraryMaintenanceDays selectedDays=LibraryMaintenanceDays.None;for(int i=0;i<days.Items.Count;i++)if(days.GetItemChecked(i))selectedDays|=(LibraryMaintenanceDays)(1<<i);LibraryMaintenanceActions selectedActions=checks.Where(x=>x.Checked).Aggregate(LibraryMaintenanceActions.None,(value,x)=>value|(LibraryMaintenanceActions)x.Tag!);
        _runtime.MaintenanceCatalog.SaveMaintenanceProfile(p with{Enabled=enabled.Checked,Cadence=(LibraryMaintenanceCadence)cadence.SelectedIndex,Days=selectedDays,StartTime=start.Value.TimeOfDay,EndTime=end.Value.TimeOfDay,MissedRun=(LibraryMaintenanceMissedRun)missed.SelectedIndex,Actions=selectedActions,PeriodicQuickScrubDays=(int)age.Value,AnalysisMode=(LibraryMaintenanceAnalysisMode)mode.SelectedIndex,ConflictBehavior=conflict.SelectedIndex==1?LibraryMaintenanceConflictBehavior.Skip:LibraryMaintenanceConflictBehavior.Wait,AnalyzeFamilies=families.Checked,UpdatedUtc=DateTime.UtcNow});_ = RefreshMaintenanceAsync();
    }

    private static DateTimePicker TimePicker(TimeSpan value)=>new(){Format=DateTimePickerFormat.Time,ShowUpDown=true,Width=120,Value=DateTime.Today+value};
    private static void AddLabeled(FlowLayoutPanel panel,string text,Control control){panel.Controls.Add(new Label{Text=text,AutoSize=true,Margin=new Padding(3,10,3,0)});panel.Controls.Add(control);}
    private static void AddMaintenanceColumn(DataGridView grid,string name,int width,bool fill=false){var c=new DataGridViewTextBoxColumn{Name=name,HeaderText=name,Width=width,ReadOnly=true};if(fill)c.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.Columns.Add(c);}
    private static string DescribeMaintenanceActions(LibraryMaintenanceProfile p)=>string.Join(", ",DescribeMaintenanceActions(p.Actions,p.AnalyzeFamilies),p.PeriodicQuickScrubDays>0?$"Quick Scrub > {p.PeriodicQuickScrubDays} days":"").Trim(' ', ',');
    private static string DescribeMaintenanceActions(LibraryMaintenanceActions actions,bool families)=>string.Join(", ",Enum.GetValues<LibraryMaintenanceActions>().Where(x=>x!=LibraryMaintenanceActions.None&&x!=LibraryMaintenanceActions.Default&&BitOperations.IsPow2((uint)x)&&actions.HasFlag(x)).Select(ActionLabel).Append(families?"Duplicate families":"").Where(x=>x.Length>0));
    private static string ActionLabel(LibraryMaintenanceActions action)=>action switch{LibraryMaintenanceActions.IncrementalScan=>"Refresh / scan library catalog",LibraryMaintenanceActions.Metadata=>"Refresh missing or changed metadata",LibraryMaintenanceActions.ExactDuplicates=>"Analyze Exact Duplicates",LibraryMaintenanceActions.VisualDuplicates=>"Analyze Visual Duplicates",LibraryMaintenanceActions.QuickScrubNew=>"Quick Scrub new files",LibraryMaintenanceActions.QuickScrubNeverChecked=>"Quick Scrub never checked",LibraryMaintenanceActions.QuickScrubStale=>"Quick Scrub stale",LibraryMaintenanceActions.QuickScrubFailed=>"Retry failed/interrupted Quick Scrubs",_=>action.ToString()};
    private void Maintenance_ProgressChanged(LibraryMaintenanceProgress p)
    {
        if(IsDisposed||!IsHandleCreated)return;
        long now=Stopwatch.GetTimestamp();
        if(p.IsActive&&p.Outcome==null&&_lastMaintenanceUiUpdateTicks!=0&&Stopwatch.GetElapsedTime(_lastMaintenanceUiUpdateTicks,now)<TimeSpan.FromMilliseconds(150))return;
        _lastMaintenanceUiUpdateTicks=now;
        BeginInvoke(() =>
        {
            if(IsDisposed)return;
            string state=p.Outcome?.ToString()??(p.IsActive?"Running":"Idle");
            _maintenanceActivity.Text=$"{state}: {p.JobName} · {p.Stage}".Trim(' ','·');
            _maintenanceCurrentItem.Text=string.IsNullOrWhiteSpace(p.CurrentItem)?p.Details:$"{p.Details} · {p.CurrentItem}".Trim(' ','·');
            _maintenanceStatus.Text=$"{p.Stage}: {p.Details}".TrimEnd(' ',':');
            ConfigureProgress(_maintenanceProgress,p.IsActive,p.Completed,p.Total,!p.IsIndeterminate);
        });
    }
}
