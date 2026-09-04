using System.Diagnostics;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class LibraryMaintenanceCoordinator : IDisposable
{
    private readonly ILibraryMaintenanceCatalog _catalog; private readonly ILibraryCatalog _locations;
    private readonly LibraryScanCoordinator _scanner; private readonly LibraryEnrichmentCoordinator _metadata;
    private readonly LibraryDuplicateAnalysisCoordinator _exact; private readonly LibraryVisualAnalysisCoordinator _visual;
    private readonly LibraryIntegrityCoordinator _integrity; private readonly Func<bool> _isEncodingActive;
    private readonly Func<DateTime> _utcNow; private readonly TimeZoneInfo _timeZone; private readonly CancellationTokenSource _shutdown=new();
    private readonly SemaphoreSlim _runGate=new(1,1); private CancellationTokenSource? _active; private Task? _loop; private bool _disposed;
    public event Action<LibraryMaintenanceProgress>? ProgressChanged;
    public bool IsRunning => _active != null;

    public LibraryMaintenanceCoordinator(ILibraryMaintenanceCatalog catalog, ILibraryCatalog locations, LibraryScanCoordinator scanner,
        LibraryEnrichmentCoordinator metadata, LibraryDuplicateAnalysisCoordinator exact, LibraryVisualAnalysisCoordinator visual,
        LibraryIntegrityCoordinator integrity, Func<bool>? isEncodingActive=null, Func<DateTime>? utcNow=null, TimeZoneInfo? timeZone=null, bool start=true)
    {
        _catalog=catalog;_locations=locations;_scanner=scanner;_metadata=metadata;_exact=exact;_visual=visual;_integrity=integrity;
        _isEncodingActive=isEncodingActive??(()=>false);_utcNow=utcNow??(()=>DateTime.UtcNow);_timeZone=timeZone??TimeZoneInfo.Local;
        _catalog.RecoverInterruptedMaintenance(_utcNow()); if(start)_loop=Task.Run(()=>LoopAsync(_shutdown.Token));
    }

    public Task RunNowAsync(long locationId,CancellationToken token=default)=>RunProfileAsync(_catalog.GetMaintenanceProfile(locationId),LibraryMaintenanceTrigger.Manual,true,token);
    public void DeferCurrent(){_active?.Cancel();_scanner.Cancel();}
    public async Task CheckDueAsync(bool startup,CancellationToken token=default)
    {
        foreach(var view in _catalog.GetMaintenanceProfiles(_utcNow(),_timeZone).Where(x=>LibraryMaintenanceScheduleCalculator.IsDue(x.Profile,_utcNow(),startup,_timeZone)))
        { if(token.IsCancellationRequested)return; await RunProfileAsync(view.Profile,startup?LibraryMaintenanceTrigger.Startup:LibraryMaintenanceTrigger.Scheduled,false,token).ConfigureAwait(false); }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try { await Task.Delay(1500,token).ConfigureAwait(false); await CheckDueAsync(true,token).ConfigureAwait(false);
            while(!token.IsCancellationRequested){await Task.Delay(TimeSpan.FromMinutes(1),token).ConfigureAwait(false);await CheckDueAsync(false,token).ConfigureAwait(false);} }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        catch(Exception ex){ErrorLogService.Append(AppPaths.UserDataDirectory,"Scheduled library maintenance",exception:ex);}
    }

    private async Task RunProfileAsync(LibraryMaintenanceProfile profile,LibraryMaintenanceTrigger trigger,bool ignoreWindow,CancellationToken external)
    {
        if(!await _runGate.WaitAsync(0,external).ConfigureAwait(false))return;
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(external,_shutdown.Token);_active=linked;long runId=0;DateTime started=_utcNow();
        long newFiles=0,changedFiles=0,missingFiles=0,metadata=0,exact=0,visual=0,integrity=0,warnings=0;string stage="Starting",details="",locationPath="";
        long lastProgressTicks=0;
        try
        {
            runId=_catalog.BeginMaintenanceRun(profile.LocationId,trigger,started); LibraryLocationRecord location=_locations.GetLocation(profile.LocationId)??throw new KeyNotFoundException("Maintenance location no longer exists.");locationPath=location.Path;
            void Publish(string value,string message="",long completed=0,long total=0,string currentItem="",bool force=false)
            {
                long now=Stopwatch.GetTimestamp();
                if(!force&&lastProgressTicks!=0&&Stopwatch.GetElapsedTime(lastProgressTicks,now)<TimeSpan.FromMilliseconds(150))return;
                lastProgressTicks=now;
                ProgressChanged?.Invoke(new(runId,profile.LocationId,value,message,location.Path,completed,total,currentItem,
                    !(total>0&&completed>=0&&completed<=total)));
            }
            void Report(string value,string message=""){stage=value;details=message;LibraryMaintenanceActivity.Update(true,value=="Waiting",value,location.Path);_catalog.UpdateMaintenanceRunStage(runId,value,message);Publish(value,message,force:true);}
            bool WindowOpen()=>ignoreWindow||LibraryMaintenanceScheduleCalculator.IsWithinWindow(TimeZoneInfo.ConvertTimeFromUtc(_utcNow(),_timeZone),profile.StartTime,profile.EndTime);
            void RequireWindow(){if(!WindowOpen())throw new MaintenanceDeferredException("The configured maintenance window is closed.");}
            RequireWindow();
            if(_isEncodingActive()&&profile.ConflictBehavior==LibraryMaintenanceConflictBehavior.Skip){Report("Skipped","Active encoding was in progress; this occurrence was skipped by policy.");Complete(LibraryMaintenanceOutcome.Deferred,details);return;}
            while(_isEncodingActive())
            {
                Report("Waiting","Waiting for active encoding to finish.");
                await Task.Delay(1000,linked.Token).ConfigureAwait(false);RequireWindow();
            }
            LibraryMaintenanceActions analysisActions=LibraryMaintenanceActions.IncrementalScan|LibraryMaintenanceActions.Metadata|LibraryMaintenanceActions.ExactDuplicates|LibraryMaintenanceActions.VisualDuplicates;
            bool needsCatalogRefresh=(profile.Actions&analysisActions)!=0||profile.AnalyzeFamilies;
            if(needsCatalogRefresh)
            {
                if(_scanner.IsScanning)throw new MaintenanceDeferredException("Another Library Analyzer scan is already active.");
                Report("Scanning",location.Path); LibraryScanResult scan=await _scanner.ScanLocationAsync(profile.LocationId,LibraryEnrichmentCoordinator.CurrentMetadataVersion,new Progress<LibraryScanProgress>(p=>
                    Publish("Scanning",$"{p.DiscoveredFiles:N0} discovered · {p.WrittenFiles:N0} indexed",p.WrittenFiles,0,p.CurrentPath)),linked.Token,
                    // Maintenance claims its durable metadata candidates in the
                    // dedicated stage below. Do not also enqueue from the scan;
                    // that creates a timing window where the same file can be
                    // probed twice when the worker finishes between stages.
                    false,m=>_catalog.RecordMaintenanceCandidates(runId,m)).ConfigureAwait(false);
                newFiles=scan.NewFiles;changedFiles=scan.ChangedFiles;missingFiles=scan.MissingFiles;
                if(scan.Outcome==LibraryScanOutcome.Unavailable){Complete(LibraryMaintenanceOutcome.Unavailable,"Location unavailable; catalog data was preserved.");return;}
                if(scan.Outcome!=LibraryScanOutcome.Completed)throw new InvalidOperationException(scan.ErrorMessage.Length==0?$"Scan ended as {scan.Outcome}.":scan.ErrorMessage);
                if(profile.Actions.HasFlag(LibraryMaintenanceActions.Metadata))metadata=newFiles+changedFiles;
            }
            RequireWindow();long analysisCandidates=_catalog.PrepareMaintenanceAnalysisCandidates(runId,profile);
            if(profile.Actions.HasFlag(LibraryMaintenanceActions.Metadata))
            {
                Report("Metadata","Queuing missing or changed metadata in bounded batches.");long after=0,queuedHere=0;while(true){IReadOnlyList<LibraryEnrichmentCandidate> candidates=_catalog.GetMaintenanceEnrichmentCandidates(runId,after);if(candidates.Count==0)break;foreach(var candidate in candidates){await _metadata.EnqueueAsync(new(candidate.FileId,candidate.FullPath,candidate.VolumeId,candidate.SizeBytes,candidate.LastWriteUtc,candidate.AttemptCount),linked.Token).ConfigureAwait(false);queuedHere++;Publish("Metadata",$"{queuedHere:N0} queued",queuedHere,analysisCandidates,candidate.FullPath);}after=candidates[^1].FileId;}metadata=Math.Max(metadata,queuedHere);
                while(_metadata.IsRunning){linked.Token.ThrowIfCancellationRequested();if(_isEncodingActive())LibraryMaintenanceActivity.Defer("Metadata deferred for active encoding",location.Path);if(!WindowOpen())throw new MaintenanceDeferredException("Window closed after metadata work was queued.");await Task.Delay(250,linked.Token).ConfigureAwait(false);}
            }
            if(profile.Actions.HasFlag(LibraryMaintenanceActions.ExactDuplicates)){RequireWindow();if(_exact.IsRunning)throw new MaintenanceDeferredException("Exact duplicate analysis is already active.");Report("Exact duplicates",$"{profile.AnalysisMode}; {analysisCandidates:N0} catalog candidates.");EventHandler<LibraryDuplicateAnalysisProgress> exactProgress=(_,p)=>Publish("Exact duplicates",$"{p.QuickHashed:N0} quick · {p.FullHashed:N0} full hashes · {p.ExactGroups:N0} groups",p.FullHashed,p.SizeCandidates,p.CurrentPath);_exact.ProgressChanged+=exactProgress;try{LibraryDuplicateAnalysisResult value=await _exact.AnalyzeAsync(linked.Token).ConfigureAwait(false);if(value.Status!=DuplicateAnalysisStatus.Completed)throw new InvalidOperationException(value.ErrorText);exact=value.QuickHashed+value.FullHashed;warnings+=value.ErrorCount;}finally{_exact.ProgressChanged-=exactProgress;}}
            if(profile.Actions.HasFlag(LibraryMaintenanceActions.VisualDuplicates)||profile.AnalyzeFamilies){RequireWindow();if(_visual.IsRunning)throw new MaintenanceDeferredException("Visual duplicate analysis is already active.");Report("Visual analysis",$"{profile.AnalysisMode}; missing fingerprints are generated first.");EventHandler<LibraryVisualAnalysisProgress> visualProgress=(_,p)=>Publish("Visual analysis",$"{p.FingerprintedFiles:N0} fingerprinted · {p.MatchPairs:N0} matches",p.FingerprintedFiles,p.EligibleFiles,p.CurrentPath);_visual.ProgressChanged+=visualProgress;try{LibraryVisualAnalysisResult value=await _visual.AnalyzeAsync(linked.Token).ConfigureAwait(false);if(value.Status!=DuplicateAnalysisStatus.Completed)throw new InvalidOperationException(value.ErrorText);visual=value.FingerprintedFiles;warnings+=value.ErrorCount;if(profile.AnalyzeFamilies)Report("Duplicate families","Families rebuilt from the newly published visual analysis.");}finally{_visual.ProgressChanged-=visualProgress;}}
            RequireWindow();long integrityAfter=0;while(true){IReadOnlyList<long> ids=_catalog.GetMaintenanceIntegrityFileIdsPage(runId,profile,_utcNow(),integrityAfter);if(ids.Count==0)break;Report("Quick Scrub",$"Queuing targeted integrity checks ({integrity+ids.Count:N0} prepared).");_integrity.QueueFiles(ids,LibraryIntegrityScrubType.Quick,$"maintenance-{runId}");integrity+=ids.Count;integrityAfter=ids[^1];}if(integrity>0&&_isEncodingActive())LibraryMaintenanceActivity.Defer("Quick Scrub deferred for active encoding",location.Path);
            Complete(LibraryMaintenanceOutcome.Completed,$"{profile.AnalysisMode} completed: {newFiles:N0} new, {changedFiles:N0} changed, {metadata:N0} metadata, {exact:N0} exact hashes, {visual:N0} visual fingerprints, {integrity:N0} integrity checks queued.");
            void Complete(LibraryMaintenanceOutcome outcome,string message){_catalog.CompleteMaintenanceRun(new(runId,profile.LocationId,trigger,outcome,stage,started,_utcNow(),newFiles,changedFiles,missingFiles,metadata,exact,visual,integrity,warnings,message,profile.Actions,profile.AnalysisMode,profile.ConflictBehavior,profile.AnalyzeFamilies));ProgressChanged?.Invoke(new(runId,profile.LocationId,stage,message,location.Path,0,0,"",true,outcome,false));}
        }
        catch(MaintenanceDeferredException ex){if(runId>0){_catalog.CompleteMaintenanceRun(new(runId,profile.LocationId,trigger,LibraryMaintenanceOutcome.Deferred,stage,started,_utcNow(),newFiles,changedFiles,missingFiles,metadata,exact,visual,integrity,warnings,ex.Message,profile.Actions,profile.AnalysisMode,profile.ConflictBehavior,profile.AnalyzeFamilies));ProgressChanged?.Invoke(new(runId,profile.LocationId,stage,ex.Message,locationPath,0,0,"",true,LibraryMaintenanceOutcome.Deferred,false));}}
        catch(OperationCanceledException){if(runId>0){const string message="Maintenance was deferred or cancelled at a safe subsystem boundary.";_catalog.CompleteMaintenanceRun(new(runId,profile.LocationId,trigger,LibraryMaintenanceOutcome.Cancelled,stage,started,_utcNow(),newFiles,changedFiles,missingFiles,metadata,exact,visual,integrity,warnings,message,profile.Actions,profile.AnalysisMode,profile.ConflictBehavior,profile.AnalyzeFamilies));ProgressChanged?.Invoke(new(runId,profile.LocationId,stage,message,locationPath,0,0,"",true,LibraryMaintenanceOutcome.Cancelled,false));}}
        catch(Exception ex){if(runId>0){_catalog.CompleteMaintenanceRun(new(runId,profile.LocationId,trigger,LibraryMaintenanceOutcome.Failed,stage,started,_utcNow(),newFiles,changedFiles,missingFiles,metadata,exact,visual,integrity,warnings+1,ex.Message,profile.Actions,profile.AnalysisMode,profile.ConflictBehavior,profile.AnalyzeFamilies));ProgressChanged?.Invoke(new(runId,profile.LocationId,stage,ex.Message,locationPath,0,0,"",true,LibraryMaintenanceOutcome.Failed,false));}ErrorLogService.Append(AppPaths.UserDataDirectory,"Scheduled library maintenance",sourcePath:null,exception:ex,details:$"Location {profile.LocationId}, stage {stage}");}
        finally{LibraryMaintenanceActivity.Clear();_active=null;_runGate.Release();}
    }

    public void Dispose(){if(_disposed)return;_disposed=true;_shutdown.Cancel();DeferCurrent();try{_loop?.Wait(TimeSpan.FromSeconds(10));}catch{} _active?.Dispose();_shutdown.Dispose();_runGate.Dispose();}
    private sealed class MaintenanceDeferredException(string message):Exception(message);
}
