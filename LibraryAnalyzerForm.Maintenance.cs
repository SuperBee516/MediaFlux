using MediaFlux.Services.LibraryCatalog;
using System.Diagnostics;
using System.Numerics;

namespace MediaFlux;

public sealed partial class LibraryAnalyzerForm
{
    private readonly DataGridView _maintenanceGrid=CreateGrid();
    private readonly DataGridView _maintenanceHistory=CreateGrid();
    private readonly Label _maintenanceStatus=new(){Dock=DockStyle.Bottom,Height=30,Padding=new Padding(8,7,0,0),Text="Scheduled maintenance is disabled until enabled per location."};
    private readonly Label _maintenanceActivity=new(){Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(8,4,8,0),Text="No scheduled job is active."};
    private readonly Label _maintenanceCurrentItem=new(){Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(8,0,8,4),ForeColor=SystemColors.GrayText};
    private readonly ProgressBar _maintenanceProgress=new(){Dock=DockStyle.Fill,Style=ProgressBarStyle.Marquee,Visible=false};
    private readonly Label _maintenanceEmptyState=new(){Name="maintenanceEmptyState",Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,Padding=new Padding(24),Visible=false};
    private readonly Label _maintenanceDetails=new(){Name="maintenanceSelectionDetails",Dock=DockStyle.Fill,AutoEllipsis=true,Padding=new Padding(8),ForeColor=SystemColors.GrayText,Text="Select a location to view its maintenance settings and latest result."};
    private readonly AnalyzerMetricCard _maintenanceEnabledMetric = new("Enabled schedules");
    private readonly AnalyzerMetricCard _maintenanceNextMetric = new("Next run");
    private readonly AnalyzerMetricCard _maintenanceLastMetric = new("Last result");
    private readonly AnalyzerMetricCard _maintenanceHistoryMetric = new("Run history");
    private Button _maintenanceEditButton=new();
    private Button _maintenanceRemoveButton=new();
    private Button _maintenanceRunButton=new();
    private Button _maintenanceToggleButton=new();
    private Button _maintenanceDeferButton=new();
    private ToolTip? _maintenanceToolTip=new();
    private long _lastMaintenanceUiUpdateTicks;

    private void BuildScheduledMaintenanceTab()
    {
        var tab=new TabPage("Scheduled Maintenance"){Padding=new Padding(10)};
        var actions=AnalyzerUi.ActionBar();
        _maintenanceEditButton=AddButton(actions,"Edit Schedule…",(_,_)=>EditSelectedMaintenance());
        AnalyzerUi.StylePrimary(_maintenanceEditButton);
        _maintenanceRemoveButton=AddButton(actions,"Remove Schedule",async(_,_)=>await RemoveSelectedMaintenanceAsync());
        _maintenanceRunButton=AddButton(actions,"Run Now",async(_,_)=>await RunSelectedMaintenanceAsync());
        AnalyzerUi.StyleAttention(_maintenanceRunButton);
        _maintenanceToggleButton=AddButton(actions,"Enable Schedule",async(_,_)=>await ToggleSelectedMaintenanceAsync());
        _maintenanceDeferButton=AddButton(actions,"Defer Current Maintenance",(_,_)=>_runtime.Maintenance.DeferCurrent());
        _maintenanceToolTip!.SetToolTip(_maintenanceDeferButton,"Stops the active maintenance run at a safe boundary. It can run again according to its schedule.");
        AddButton(actions,"Refresh",async(_,_)=>await RefreshMaintenanceAsync());

        AddMaintenanceColumn(_maintenanceGrid,"Location",160,true);
        AddMaintenanceColumn(_maintenanceGrid,"Schedule",180);
        AddMaintenanceColumn(_maintenanceGrid,"Next Run",130);
        AddMaintenanceColumn(_maintenanceGrid,"Tasks",175);
        AddMaintenanceColumn(_maintenanceGrid,"Last Run",130);
        AddMaintenanceColumn(_maintenanceGrid,"Result",180);
        AddMaintenanceColumn(_maintenanceGrid,"Enabled",70);
        _maintenanceGrid.Columns["Location"].MinimumWidth=140;
        _maintenanceGrid.SelectionChanged+=(_,_)=>UpdateMaintenanceSelection();

        AddMaintenanceColumn(_maintenanceHistory,"Started",135);
        AddMaintenanceColumn(_maintenanceHistory,"Location",160);
        AddMaintenanceColumn(_maintenanceHistory,"Tasks",175);
        AddMaintenanceColumn(_maintenanceHistory,"Scope",110);
        AddMaintenanceColumn(_maintenanceHistory,"Trigger",100);
        AddMaintenanceColumn(_maintenanceHistory,"Result",180);
        AddMaintenanceColumn(_maintenanceHistory,"Activity",220);
        AddMaintenanceColumn(_maintenanceHistory,"Details",300,true);

        var maintenanceContent=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Padding=new Padding(0)};
        maintenanceContent.RowStyles.Add(new RowStyle(SizeType.Percent,72));
        maintenanceContent.RowStyles.Add(new RowStyle(SizeType.Percent,28));
        maintenanceContent.Controls.Add(_maintenanceGrid,0,0);
        maintenanceContent.Controls.Add(_maintenanceDetails,0,1);
        var configuredPanel=new Panel{Dock=DockStyle.Fill};
        configuredPanel.Controls.Add(maintenanceContent);
        configuredPanel.Controls.Add(_maintenanceEmptyState);
        var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=360,Panel1MinSize=210,Panel2MinSize=120};
        split.Panel1.Controls.Add(new AnalyzerSectionPanel("Configured maintenance",configuredPanel){Dock=DockStyle.Fill});
        split.Panel2.Controls.Add(new AnalyzerSectionPanel("Maintenance history",_maintenanceHistory){Dock=DockStyle.Fill});

        var activity=new TableLayoutPanel{Dock=DockStyle.Top,Height=58,ColumnCount=2,RowCount=2,Padding=new Padding(0,2,0,2)};
        activity.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));activity.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,220));
        activity.RowStyles.Add(new RowStyle(SizeType.Percent,50));activity.RowStyles.Add(new RowStyle(SizeType.Percent,50));
        activity.Controls.Add(_maintenanceActivity,0,0);activity.Controls.Add(_maintenanceCurrentItem,0,1);activity.Controls.Add(_maintenanceProgress,1,0);activity.SetRowSpan(_maintenanceProgress,2);
        tab.Controls.Add(split);tab.Controls.Add(_maintenanceStatus);tab.Controls.Add(activity);
        tab.Controls.Add(AnalyzerUi.MetricRow(72,_maintenanceEnabledMetric,_maintenanceNextMetric,_maintenanceLastMetric,_maintenanceHistoryMetric));
        tab.Controls.Add(actions);_tabs.TabPages.Add(tab);
        UpdateMaintenanceActionState();
    }

    private async Task RefreshMaintenanceAsync()
    {
        try
        {
            (IReadOnlyList<LibraryMaintenanceProfileView> profiles,IReadOnlyList<LibraryMaintenanceRun> history,IReadOnlyDictionary<long,string> paths)=await Task.Run(() =>
            {
                var p=_runtime.MaintenanceCatalog.GetMaintenanceProfiles(DateTime.UtcNow);
                var h=_runtime.MaintenanceCatalog.GetMaintenanceHistory(limit:100);
                return(p,h,_runtime.Catalog.GetLocations().ToDictionary(x=>x.Id,x=>x.Path));
            });
            if(_lifecycleCleanupCompleted||IsDisposed||Disposing||_maintenanceGrid.IsDisposed)return;
            long? selectedId=SelectedMaintenance()?.Profile.LocationId;
            _maintenanceGrid.Rows.Clear();
            foreach(var view in profiles)
            {
                LibraryMaintenanceProfile profile=view.Profile;
                _maintenanceGrid.Rows.Add(view.LocationPath,DescribeMaintenanceCadence(profile),view.NextRunUtc?.ToLocalTime().ToString("g")??"No automatic run scheduled",DescribeMaintenanceTasks(profile),view.LastRunUtc?.ToLocalTime().ToString("g")??"Never",DescribeMaintenanceResult(view.LastOutcome,view.LastStatus,view.Availability),profile.Enabled&&profile.Cadence!=LibraryMaintenanceCadence.ManualOnly?"Yes":"No");
                _maintenanceGrid.Rows[^1].Tag=view;
            }
            _maintenanceEmptyState.Visible=profiles.Count==0;
            _maintenanceGrid.Visible=profiles.Count>0;
            _maintenanceDetails.Visible=profiles.Count>0;
            _maintenanceEmptyState.Text=profiles.Count==0
                ? "No scheduled maintenance is configured.\n\nConfigure automatic library maintenance for a location, or run maintenance manually."
                : string.Empty;
            if(selectedId.HasValue)
            {
                DataGridViewRow? match=_maintenanceGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(row=>(row.Tag as LibraryMaintenanceProfileView)?.Profile.LocationId==selectedId.Value);
                if(match!=null)match.Selected=true;
            }
            _maintenanceHistory.Rows.Clear();
            foreach(var run in history)
            {
                paths.TryGetValue(run.LocationId,out string? path);
                _maintenanceHistory.Rows.Add(run.StartedUtc.ToLocalTime().ToString("g"),path??$"Location {run.LocationId}",DescribeMaintenanceTasks(run.Actions,run.AnalyzeFamilies),run.AnalysisMode==LibraryMaintenanceAnalysisMode.FullReanalysis?"Full reanalysis":"Incremental",DescribeMaintenanceTrigger(run.Trigger),DescribeMaintenanceRunResult(run),$"{run.NewFiles:N0} new · {run.ChangedFiles:N0} changed · {run.MetadataQueued:N0} metadata · {run.ExactProcessed:N0} exact · {run.VisualProcessed:N0} visual",run.Details);
            }
            int enabled=profiles.Count(view=>view.Profile.Enabled&&view.Profile.Cadence!=LibraryMaintenanceCadence.ManualOnly);
            LibraryMaintenanceProfileView? next=profiles.Where(view=>view.Profile.Enabled&&view.Profile.Cadence!=LibraryMaintenanceCadence.ManualOnly&&view.NextRunUtc.HasValue).OrderBy(view=>view.NextRunUtc).FirstOrDefault();
            LibraryMaintenanceRun? last=history.OrderByDescending(run=>run.StartedUtc).FirstOrDefault();
            _maintenanceEnabledMetric.SetValue(enabled.ToString("N0"),$"{profiles.Count-enabled:N0} disabled");
            _maintenanceNextMetric.SetValue(next?.NextRunUtc?.ToLocalTime().ToString("g")??"No automatic run scheduled",next==null?string.Empty:$"{Path.GetFileName(next.LocationPath)} · {DescribeMaintenanceCadence(next.Profile)}");
            _maintenanceLastMetric.SetValue(last==null?"None":DescribeMaintenanceRunResult(last),last==null?"No maintenance run yet":$"{Path.GetFileName(paths.GetValueOrDefault(last.LocationId,$"Location {last.LocationId}"))} · {DescribeMaintenanceActions(last.Actions,last.AnalyzeFamilies)}");
            _maintenanceHistoryMetric.SetValue(history.Count.ToString("N0"),"Recent runs retained");
            UpdateMaintenanceSelection();
        }
        catch(Exception ex){if(!IsDisposed)ShowError("Scheduled maintenance could not be refreshed.",ex);}
    }

    private LibraryMaintenanceProfileView? SelectedMaintenance()=>_maintenanceGrid.SelectedRows.Cast<DataGridViewRow>().Select(row=>row.Tag).OfType<LibraryMaintenanceProfileView>().FirstOrDefault();
    private async Task RunSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;_maintenanceStatus.Text=$"Starting maintenance for {selected.LocationPath}…";await _runtime.Maintenance.RunNowAsync(selected.Profile.LocationId);await RefreshMaintenanceAsync();}
    private async Task ToggleSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null||selected.Profile.Cadence==LibraryMaintenanceCadence.ManualOnly&& !selected.Profile.Enabled)return;await Task.Run(()=>_runtime.MaintenanceCatalog.SaveMaintenanceProfile(selected.Profile with{Enabled=!selected.Profile.Enabled,UpdatedUtc=DateTime.UtcNow}));await RefreshMaintenanceAsync();}
    private async Task RemoveSelectedMaintenanceAsync(){var selected=SelectedMaintenance();if(selected==null)return;if(MessageBox.Show(this,$"Remove the scheduled maintenance profile for {selected.LocationPath}?\n\nThe library location and run history will be kept.","Remove Scheduled Maintenance",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;LibraryMaintenanceProfile defaults=new(selected.Profile.LocationId,selected.Profile.Version,false,LibraryMaintenanceCadence.ManualOnly,LibraryMaintenanceDays.All,TimeSpan.FromHours(1),TimeSpan.FromHours(6),LibraryMaintenanceMissedRun.RunAtNextWindow,LibraryMaintenanceActions.Default,0,selected.Profile.CreatedUtc,DateTime.UtcNow,selected.Profile.LastScheduledUtc);await Task.Run(()=>_runtime.MaintenanceCatalog.SaveMaintenanceProfile(defaults));await RefreshMaintenanceAsync();}
    private void EditSelectedMaintenance()
    {
        var selected=SelectedMaintenance();if(selected==null)return;
        using var dialog=new LibraryMaintenanceScheduleEditorDialog(selected.Profile,selected.LocationPath);
        if(dialog.ShowDialog(this)!=DialogResult.OK||dialog.SavedProfile is not LibraryMaintenanceProfile updated)return;
        _runtime.MaintenanceCatalog.SaveMaintenanceProfile(updated);_ = RefreshMaintenanceAsync();
    }

    private void UpdateMaintenanceSelection()
    {
        LibraryMaintenanceProfileView? selected=SelectedMaintenance();
        if(selected==null)
        {
            var views=_maintenanceGrid.Rows.Cast<DataGridViewRow>().Select(row=>row.Tag).OfType<LibraryMaintenanceProfileView>().ToArray();
            _maintenanceDetails.Text=views.Length>0&&views.All(view=>!view.Profile.Enabled||view.Profile.Cadence==LibraryMaintenanceCadence.ManualOnly)
                ? "Automatic maintenance is currently disabled for all locations. Select a location to review or enable its schedule."
                : "Select a location to view its maintenance settings and latest result.";
            UpdateMaintenanceActionState();
            return;
        }
        LibraryMaintenanceProfile profile=selected.Profile;
        var allViews=_maintenanceGrid.Rows.Cast<DataGridViewRow>().Select(row=>row.Tag).OfType<LibraryMaintenanceProfileView>().ToArray();
        bool allDisabled=allViews.Length>0&&allViews.All(view=>!view.Profile.Enabled||view.Profile.Cadence==LibraryMaintenanceCadence.ManualOnly);
        string window=profile.Cadence is LibraryMaintenanceCadence.Daily or LibraryMaintenanceCadence.Weekly?$"{FormatMaintenanceTime(profile.StartTime)}–{FormatMaintenanceTime(profile.EndTime)}":"Not applicable";
        string weekdays=profile.Cadence==LibraryMaintenanceCadence.Weekly?string.Join(", ",Enum.GetValues<DayOfWeek>().Where(day=>(profile.Days&(LibraryMaintenanceDays)(1<<(int)day))!=0)):"Not applicable";
        string missed=profile.MissedRun switch{LibraryMaintenanceMissedRun.RunAtNextWindow=>"Run later during the maintenance window",LibraryMaintenanceMissedRun.RunOnNextStartup=>"Run when MediaFlux next starts",_=>"Skip missed run"};
        string quick=DescribeQuickScrub(profile);
        _maintenanceDetails.Text=$"{(allDisabled?"Automatic maintenance is currently disabled for all locations. ":string.Empty)}{DescribeMaintenanceCadence(profile)} · {(profile.AnalysisMode==LibraryMaintenanceAnalysisMode.FullReanalysis?"Full reanalysis":"Incremental")} · {(profile.ConflictBehavior==LibraryMaintenanceConflictBehavior.Wait?"Wait for encoding":"Skip this run")} · Missed run: {missed} · Window: {window} · Days: {weekdays}{Environment.NewLine}Tasks: {DescribeMaintenanceActions(profile)} · Quick Scrub: {quick}{Environment.NewLine}Last result: {DescribeMaintenanceResult(selected.LastOutcome,selected.LastStatus,selected.Availability)}. {selected.LastStatus}";
        UpdateMaintenanceActionState();
    }

    private void UpdateMaintenanceActionState()
    {
        LibraryMaintenanceProfileView? selected=SelectedMaintenance();
        bool hasSelection=selected!=null;
        _maintenanceEditButton.Enabled=hasSelection;
        _maintenanceRemoveButton.Enabled=hasSelection;
        _maintenanceRunButton.Enabled=hasSelection&&!_runtime.Maintenance.IsRunning;
        bool canEnable=hasSelection&&selected!.Profile.Cadence!=LibraryMaintenanceCadence.ManualOnly;
        _maintenanceToggleButton.Enabled=hasSelection&&(selected!.Profile.Enabled||canEnable);
        _maintenanceToggleButton.Text=selected?.Profile.Enabled==true?"Disable Schedule":"Enable Schedule";
        _maintenanceDeferButton.Enabled=_runtime.Maintenance.IsRunning;
    }

    private static void AddMaintenanceColumn(DataGridView grid,string name,int width,bool fill=false){var column=new DataGridViewTextBoxColumn{Name=name,HeaderText=name,Width=width,ReadOnly=true};if(fill)column.AutoSizeMode=DataGridViewAutoSizeColumnMode.Fill;grid.Columns.Add(column);}
    private static string DescribeMaintenanceActions(LibraryMaintenanceProfile profile)
    {
        List<string> actions=DescribeMaintenanceActions(profile.Actions,profile.AnalyzeFamilies).Split(", ",StringSplitOptions.RemoveEmptyEntries).ToList();
        if(profile.PeriodicQuickScrubDays>0)actions.Add($"Recheck healthy files after {profile.PeriodicQuickScrubDays} days");
        return actions.Count==0?"No tasks selected":string.Join(", ",actions);
    }
    private static string DescribeQuickScrub(LibraryMaintenanceProfile profile)
    {
        List<string> policies=new();
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubNew))policies.Add("new files");
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubNeverChecked))policies.Add("never checked");
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubStale))policies.Add("stale results");
        if(profile.Actions.HasFlag(LibraryMaintenanceActions.QuickScrubFailed))policies.Add("failed checks");
        if(profile.PeriodicQuickScrubDays>0)policies.Add($"healthy files every {profile.PeriodicQuickScrubDays} days");
        return policies.Count==0?"Off":string.Join(", ",policies);
    }
    private static string DescribeMaintenanceActions(LibraryMaintenanceActions actions,bool families)=>string.Join(", ",Enum.GetValues<LibraryMaintenanceActions>().Where(action=>action!=LibraryMaintenanceActions.None&&action!=LibraryMaintenanceActions.Default&&BitOperations.IsPow2((uint)action)&&actions.HasFlag(action)).Select(ActionLabel).Append(families?"Build duplicate families":"").Where(label=>label.Length>0));
    private static string DescribeMaintenanceTasks(LibraryMaintenanceProfile profile)=>DescribeMaintenanceTasks(profile.Actions,profile.AnalyzeFamilies,profile.PeriodicQuickScrubDays);
    private static string DescribeMaintenanceTasks(LibraryMaintenanceActions actions,bool families,int periodicDays=0)
    {
        List<string> tasks=new();
        if(actions.HasFlag(LibraryMaintenanceActions.IncrementalScan))tasks.Add("Catalog");
        if(actions.HasFlag(LibraryMaintenanceActions.Metadata))tasks.Add("Metadata");
        if(actions.HasFlag(LibraryMaintenanceActions.ExactDuplicates))tasks.Add("Exact");
        if(actions.HasFlag(LibraryMaintenanceActions.VisualDuplicates))tasks.Add("Visual");
        if((actions&(LibraryMaintenanceActions.QuickScrubNew|LibraryMaintenanceActions.QuickScrubNeverChecked|LibraryMaintenanceActions.QuickScrubStale|LibraryMaintenanceActions.QuickScrubFailed))!=0||periodicDays>0)tasks.Add("Quick Scrub");
        if(families)tasks.Add("Duplicate families");
        return tasks.Count==0?"No tasks selected":string.Join(", ",tasks);
    }
    private static string DescribeMaintenanceCadence(LibraryMaintenanceProfile profile)=>profile.Cadence switch
    {
        LibraryMaintenanceCadence.ManualOnly=>"Manual only",
        LibraryMaintenanceCadence.Daily=>$"Daily · {FormatMaintenanceTime(profile.StartTime)}–{FormatMaintenanceTime(profile.EndTime)}",
        LibraryMaintenanceCadence.Weekly=>$"Weekly · {string.Join(", ",Enum.GetValues<DayOfWeek>().Where(day=>(profile.Days&(LibraryMaintenanceDays)(1<<(int)day))!=0).Select(day=>day.ToString()[..3]))} · {FormatMaintenanceTime(profile.StartTime)}–{FormatMaintenanceTime(profile.EndTime)}",
        LibraryMaintenanceCadence.OnStartup=>"When MediaFlux starts",
        _=>"Unsupported schedule"
    };
    private static string DescribeMaintenanceTrigger(LibraryMaintenanceTrigger trigger)=>trigger switch
    {
        LibraryMaintenanceTrigger.Scheduled=>"Scheduled",LibraryMaintenanceTrigger.Manual=>"Run manually",LibraryMaintenanceTrigger.Startup=>"At startup",LibraryMaintenanceTrigger.Recovery=>"Recovery",_=>"Other"
    };
    private static string DescribeMaintenanceRunResult(LibraryMaintenanceRun run)
    {
        string detail=run.Details??string.Empty;
        if(run.Stage.Equals("Skipped",StringComparison.OrdinalIgnoreCase)||detail.Contains("skipped",StringComparison.OrdinalIgnoreCase))return $"Skipped — {ShortReason(detail,"policy")}";
        if(run.Outcome==LibraryMaintenanceOutcome.Deferred)return $"Deferred — {ShortReason(detail,"maintenance deferred")}";
        if(run.Outcome==LibraryMaintenanceOutcome.Unavailable)return "Unavailable — location not accessible";
        if(run.Outcome==LibraryMaintenanceOutcome.Cancelled)return "Cancelled — stopped at a safe boundary";
        if(run.Outcome==LibraryMaintenanceOutcome.Interrupted)return "Cancelled — interrupted";
        return run.Outcome switch{LibraryMaintenanceOutcome.Completed=>"Completed",LibraryMaintenanceOutcome.Failed=>"Failed",LibraryMaintenanceOutcome.Running=>run.Stage.Equals("Waiting",StringComparison.OrdinalIgnoreCase)?"Waiting for encoding":"Running",_=>"Not run"};
    }
    private static string DescribeMaintenanceResult(LibraryMaintenanceOutcome? outcome,string status,LibraryLocationAvailability availability)
    {
        if(availability is LibraryLocationAvailability.Unavailable or LibraryLocationAvailability.Error)return "Unavailable — location not accessible";
        if(outcome==null)return "Not run";
        if(status.Contains("window closed",StringComparison.OrdinalIgnoreCase))return "Deferred — window closed";
        if(status.Contains("encoding",StringComparison.OrdinalIgnoreCase)&&status.Contains("skip",StringComparison.OrdinalIgnoreCase))return "Skipped — encoding active";
        return outcome switch{LibraryMaintenanceOutcome.Completed=>"Completed",LibraryMaintenanceOutcome.Deferred=>$"Deferred — {ShortReason(status,"maintenance deferred")}",LibraryMaintenanceOutcome.Unavailable=>"Unavailable — location not accessible",LibraryMaintenanceOutcome.Cancelled=>"Cancelled",LibraryMaintenanceOutcome.Interrupted=>"Cancelled — interrupted",LibraryMaintenanceOutcome.Failed=>"Failed",LibraryMaintenanceOutcome.Running=>status.Contains("encoding",StringComparison.OrdinalIgnoreCase)?"Waiting for encoding":"Running",_=>"Not run"};
    }
    private static string ShortReason(string details,string fallback)
    {
        if(string.IsNullOrWhiteSpace(details))return fallback;
        if(details.Contains("window closed",StringComparison.OrdinalIgnoreCase))return "window closed";
        if(details.Contains("encoding",StringComparison.OrdinalIgnoreCase))return "encoding active";
        if(details.Contains("unavailable",StringComparison.OrdinalIgnoreCase))return "location unavailable";
        return details.Length>70?details[..67]+"…":details;
    }
    private static string FormatMaintenanceTime(TimeSpan value)=>DateTime.Today.Add(value).ToString("h:mm tt");
    private static string ActionLabel(LibraryMaintenanceActions action)=>action switch{LibraryMaintenanceActions.IncrementalScan=>"Catalog",LibraryMaintenanceActions.Metadata=>"Metadata",LibraryMaintenanceActions.ExactDuplicates=>"Exact",LibraryMaintenanceActions.VisualDuplicates=>"Visual",LibraryMaintenanceActions.QuickScrubNew=>"Quick Scrub (new)",LibraryMaintenanceActions.QuickScrubNeverChecked=>"Quick Scrub (unchecked)",LibraryMaintenanceActions.QuickScrubStale=>"Quick Scrub (stale)",LibraryMaintenanceActions.QuickScrubFailed=>"Quick Scrub (failed)",_=>"Other task"};
    private void Maintenance_ProgressChanged(LibraryMaintenanceProgress progress)
    {
        if(!CanUseFormUi)return;
        long now=Stopwatch.GetTimestamp();
        if(progress.IsActive&&progress.Outcome==null&&_lastMaintenanceUiUpdateTicks!=0&&Stopwatch.GetElapsedTime(_lastMaintenanceUiUpdateTicks,now)<TimeSpan.FromMilliseconds(150))return;
        _lastMaintenanceUiUpdateTicks=now;
        PostToFormUi(() =>
        {
            if(_maintenanceActivity.IsDisposed)return;
            string state=progress.Outcome.HasValue?DescribeOutcome(progress.Outcome.Value):(progress.IsActive&&progress.Stage.Contains("encoding",StringComparison.OrdinalIgnoreCase)?"Waiting for encoding":progress.IsActive?"Running":"Idle");
            _maintenanceActivity.Text=$"{state}: {progress.JobName} · {progress.Stage}".Trim(' ','·');
            _maintenanceCurrentItem.Text=string.IsNullOrWhiteSpace(progress.CurrentItem)?progress.Details:$"{progress.Details} · {progress.CurrentItem}".Trim(' ','·');
            _maintenanceStatus.Text=$"{progress.Stage}: {progress.Details}".TrimEnd(' ',':');
            ConfigureProgress(_maintenanceProgress,progress.IsActive,progress.Completed,progress.Total,!progress.IsIndeterminate);
            UpdateMaintenanceActionState();
        });
    }
    private static string DescribeOutcome(LibraryMaintenanceOutcome outcome)=>outcome switch{LibraryMaintenanceOutcome.Completed=>"Completed",LibraryMaintenanceOutcome.Deferred=>"Deferred",LibraryMaintenanceOutcome.Unavailable=>"Unavailable",LibraryMaintenanceOutcome.Cancelled=>"Cancelled",LibraryMaintenanceOutcome.Interrupted=>"Cancelled — interrupted",LibraryMaintenanceOutcome.Failed=>"Failed",_=>"Running"};
}
