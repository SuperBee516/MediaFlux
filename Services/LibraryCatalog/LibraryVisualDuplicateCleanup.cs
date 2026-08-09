using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed record VisualCleanupProposalItem(
        VisualSimilarityGroupRecord Group,
        VisualSimilarityMemberRecord Keeper,
        VisualSimilarityMemberRecord Candidate,
        string KeeperReason,
        bool HasExactEvidence,
        byte[]? ExactHash);

    public sealed record VisualCleanupProposal(
        IReadOnlyList<VisualCleanupProposalItem> Items,
        int ExcludedGroups,
        long ReclaimableBytes,
        bool IncludesUnreviewed,
        bool IsTruncated);

    public sealed class LibraryVisualDuplicateCleanupService
    {
        private readonly ILibraryCatalog _inventory;
        private readonly ILibraryAnalysisCatalog _analysis;
        private readonly ILibraryVisualCatalog _visual;
        private readonly ILibraryDuplicateFileActions _actions;
        private readonly ILibraryFileIdentityProvider _identityProvider;
        private readonly Func<bool> _isEncodingActive;
        private readonly DuplicateKeeperPreferences _preferences;

        public LibraryVisualDuplicateCleanupService(ILibraryCatalog inventory, ILibraryAnalysisCatalog analysis,
            ILibraryVisualCatalog visual, DuplicateKeeperPreferences? preferences = null, Func<bool>? isEncodingActive = null)
            : this(inventory, analysis, visual, new WindowsLibraryDuplicateFileActions(), new WindowsLibraryFileIdentityProvider(), preferences, isEncodingActive) { }

        internal LibraryVisualDuplicateCleanupService(ILibraryCatalog inventory, ILibraryAnalysisCatalog analysis,
            ILibraryVisualCatalog visual, ILibraryDuplicateFileActions actions, ILibraryFileIdentityProvider identityProvider,
            DuplicateKeeperPreferences? preferences = null, Func<bool>? isEncodingActive = null)
        {
            _inventory=inventory; _analysis=analysis; _visual=visual; _actions=actions; _identityProvider=identityProvider;
            _preferences=(preferences??new DuplicateKeeperPreferences()).Clone(); _preferences.Normalize();
            _isEncodingActive=isEncodingActive??(()=>false);
        }

        public VisualCleanupProposal BuildProposal(bool includeUnreviewed=false, double minimumConfidence=95,
            IReadOnlyCollection<long>? groupIds=null, int maximumItems=5000)
        {
            minimumConfidence=Math.Clamp(minimumConfidence,0,100);
            maximumItems=Math.Clamp(maximumItems,1,10_000);
            HashSet<long>? selected=groupIds?.ToHashSet();
            var proposed=new List<VisualCleanupProposalItem>(); int excluded=0; int offset=0; bool truncated=false;
            while(true)
            {
                VisualSimilarityGroupPage page=_visual.QueryVisualGroups(new VisualGroupQuery(
                    Reviewed: includeUnreviewed?null:true, Ignored:false, MinimumConfidence:includeUnreviewed?minimumConfidence:0,
                    SortColumn:"reclaimable", Descending:true, Offset:offset, Limit:500));
                if(page.Groups.Count==0) break;
                foreach(VisualSimilarityGroupRecord group in page.Groups)
                {
                    if(selected!=null && !selected.Contains(group.GroupId)) continue;
                    if(group.Ignored || (!includeUnreviewed && !group.Reviewed) || (includeUnreviewed && !group.Reviewed && group.ConfidenceScore<minimumConfidence)) { excluded++; continue; }
                    IReadOnlyList<VisualSimilarityMemberRecord> members=_visual.GetVisualGroupMembers(group.GroupId);
                    if(members.Count!=2) { excluded++; continue; }
                    VisualSimilarityMemberRecord? keeper=members.FirstOrDefault(x=>x.IsManualKeeper)
                        ?? members.FirstOrDefault(x=>x.IsProtected)
                        ?? members.FirstOrDefault(x=>x.IsSuggestedKeeper);
                    string reason=keeper?.IsManualKeeper==true?"Manual keeper selection":keeper?.IsProtected==true?"Protected keeper":"Keeper recommendation";
                    if(keeper==null)
                    {
                        DuplicateKeeperEvaluation score=DuplicateKeeperScoringService.Evaluate(members.Select(ToLegacyItem).ToArray(),_preferences);
                        if(score.RequiresReview || score.Keeper==null) { excluded++; continue; }
                        keeper=members.First(x=>string.Equals(x.FullPath,score.Keeper.Path,StringComparison.OrdinalIgnoreCase));
                        reason=score.Explanation;
                        _visual.SetVisualSuggestedKeeper(group.GroupId,keeper.FileId);
                    }
                    VisualSimilarityMemberRecord candidate=members.Single(x=>x.FileId!=keeper.FileId);
                    if(!Eligible(keeper,false) || !Eligible(candidate,true) || SamePhysicalFile(keeper,candidate)) { excluded++; continue; }
                    LibraryFileHashFact? kh=_analysis.GetFileHashFact(keeper.FileId), ch=_analysis.GetFileHashFact(candidate.FileId);
                    bool exact=HashFactIsCurrent(kh,keeper) && HashFactIsCurrent(ch,candidate) && kh!.FullHash!.SequenceEqual(ch!.FullHash!);
                    proposed.Add(new VisualCleanupProposalItem(group,keeper,candidate,reason,exact,exact?kh!.FullHash:null));
                    if(proposed.Count>=maximumItems){truncated=offset+page.Groups.Count<page.TotalCount;break;}
                }
                if(proposed.Count>=maximumItems) break;
                offset+=page.Groups.Count;
                if(offset>=page.TotalCount) break;
            }
            HashSet<long> keeperIds=proposed.Select(x=>x.Keeper.FileId).ToHashSet();
            var safe=proposed.Where(x=>!keeperIds.Contains(x.Candidate.FileId)).GroupBy(x=>x.Candidate.FileId).Select(x=>x.First()).ToArray();
            excluded+=proposed.Count-safe.Length;
            return new VisualCleanupProposal(safe,excluded,safe.Sum(x=>x.Candidate.SizeBytes),includeUnreviewed,truncated);
        }

        public VisualCleanupPlanRecord CreatePlan(IReadOnlyCollection<VisualCleanupProposalItem> approved,
            DuplicateCleanupAction action, string quarantineRoot="", bool allowUnreviewed=false, double minimumConfidence=95)
        {
            if(approved.Count==0) throw new ArgumentException("Select at least one visual cleanup candidate.",nameof(approved));
            var items=new List<VisualCleanupPlanItemRecord>();
            foreach(VisualCleanupProposalItem proposal in approved)
            {
                IndexedFileRecord? source=_inventory.GetFileByPath(proposal.Candidate.FullPath);
                IndexedFileRecord? keeper=_inventory.GetFileByPath(proposal.Keeper.FullPath);
                if(source==null||keeper==null||source.Id!=proposal.Candidate.FileId||keeper.Id!=proposal.Keeper.FileId) continue;
                items.Add(new VisualCleanupPlanItemRecord(0,proposal.Group.GroupKey,proposal.Group.GroupId,source.Id,keeper.Id,
                    source.FullPath,source.SizeBytes,source.LastWriteTimeUtc,source.VolumeId,source.FileIdentity,
                    keeper.FullPath,keeper.SizeBytes,keeper.LastWriteTimeUtc,keeper.VolumeId,keeper.FileIdentity,
                    proposal.Group.ConfidenceScore,proposal.ExactHash,DuplicateCleanupItemStatus.Planned,"",""));
            }
            if(items.Count==0) throw new InvalidOperationException("No safe visual cleanup candidates remain.");
            long id=_visual.CreateVisualCleanupPlan(action,quarantineRoot,allowUnreviewed,minimumConfidence,items);
            return _visual.GetVisualCleanupPlan(id)??throw new InvalidOperationException("The visual cleanup plan could not be reloaded.");
        }

        public async Task<DuplicateCleanupExecutionResult> ExecutePlanAsync(long planId,CancellationToken token=default)
        {
            if(_isEncodingActive()) throw new InvalidOperationException("Stop the active encode before duplicate cleanup.");
            VisualCleanupPlanRecord plan=_visual.GetVisualCleanupPlan(planId)??throw new KeyNotFoundException($"Visual cleanup plan {planId} does not exist.");
            if(plan.Status!=DuplicateCleanupStatus.Ready) throw new InvalidOperationException("Only a ready visual cleanup plan can be executed.");
            int succeeded=0,excluded=0,failed=0;
            foreach(VisualCleanupPlanItemRecord item in plan.Items)
            {
                token.ThrowIfCancellationRequested();
                string? error=await ValidateAsync(plan,item,token).ConfigureAwait(false);
                if(error!=null){Record(plan,item,DuplicateCleanupItemStatus.Excluded,"",error);excluded++;continue;}
                string destination="";
                try
                {
                    destination=plan.Action switch
                    {
                        DuplicateCleanupAction.RecycleBin=>Recycle(item.SourcePath),
                        DuplicateCleanupAction.Quarantine=>_actions.Quarantine(item.SourcePath,plan.QuarantineRoot,item.GroupId,item.FileId),
                        DuplicateCleanupAction.PermanentDelete=>DeletePermanent(item.SourcePath),
                        _=>throw new InvalidOperationException("Unknown cleanup action.")
                    };
                    Record(plan,item,DuplicateCleanupItemStatus.Succeeded,destination,item.ExactHash==null?"User-approved visual duplicate cleanup succeeded.":"Exact hash evidence confirmed; cleanup succeeded."); succeeded++;
                }
                catch(Exception ex){Record(plan,item,DuplicateCleanupItemStatus.Failed,destination,ex.Message);failed++;}
            }
            _visual.CompleteVisualCleanupPlan(planId,failed>0?DuplicateCleanupStatus.Failed:DuplicateCleanupStatus.Completed);
            return new DuplicateCleanupExecutionResult(planId,succeeded,excluded,failed,"");
        }

        private async Task<string?> ValidateAsync(VisualCleanupPlanRecord plan,VisualCleanupPlanItemRecord item,CancellationToken token)
        {
            VisualSimilarityGroupRecord? group=_visual.GetVisualGroupByKey(item.GroupKey);
            if(group==null||group.Ignored) return "The visual match is no longer current or is ignored.";
            if(group.ConfidenceScore + 0.001 < item.ConfidenceScore) return "The current visual confidence is lower than the approved evidence.";
            if(!plan.AllowUnreviewed&&!group.Reviewed) return "The visual match is no longer reviewed.";
            if(plan.AllowUnreviewed&&!group.Reviewed&&group.ConfidenceScore<plan.MinimumConfidence) return "The unreviewed match no longer meets the confidence threshold.";
            if(group.ManualKeeperFileId.HasValue&&group.ManualKeeperFileId!=item.KeeperFileId) return "The manual keeper decision changed.";
            if(!group.ManualKeeperFileId.HasValue&&group.SuggestedKeeperFileId!=item.KeeperFileId) return "The keeper recommendation changed.";
            IReadOnlyList<VisualSimilarityMemberRecord> members=_visual.GetVisualGroupMembers(group.GroupId);
            VisualSimilarityMemberRecord? keeper=members.FirstOrDefault(x=>x.FileId==item.KeeperFileId), candidate=members.FirstOrDefault(x=>x.FileId==item.FileId);
            if(keeper==null||candidate==null) return "The files are no longer members of the visual match.";
            if(candidate.IsProtected) return "The candidate is protected.";
            string? keeperError=ValidateSnapshot(keeper,item.KeeperPath,item.KeeperSizeBytes,item.KeeperLastWriteUtc,item.KeeperVolumeId,item.KeeperFileIdentity);
            if(keeperError!=null) return "Keeper validation failed: "+keeperError;
            string? candidateError=ValidateSnapshot(candidate,item.SourcePath,item.SourceSizeBytes,item.SourceLastWriteUtc,item.SourceVolumeId,item.SourceFileIdentity);
            if(candidateError!=null) return candidateError;
            if(SamePhysicalFile(keeper,candidate)) return "Keeper and candidate resolve to the same physical file.";
            VisualFingerprintFact? kf=_visual.GetVisualFingerprint(keeper.FileId),cf=_visual.GetVisualFingerprint(candidate.FileId);
            if(kf?.Status!=VisualFingerprintStatus.Succeeded||cf?.Status!=VisualFingerprintStatus.Succeeded||
               kf.SourceSizeBytes!=keeper.SizeBytes||cf.SourceSizeBytes!=candidate.SizeBytes||
               kf.SourceLastWriteUtc.Ticks!=keeper.LastWriteUtc.Ticks||cf.SourceLastWriteUtc.Ticks!=candidate.LastWriteUtc.Ticks)
                return "Visual fingerprint evidence is stale; rescan and rerun visual analysis.";
            if(item.ExactHash!=null)
            {
                byte[] a=await ExactDuplicateHashService.ComputeFullAsync(ToHashCandidate(keeper),token).ConfigureAwait(false);
                byte[] b=await ExactDuplicateHashService.ComputeFullAsync(ToHashCandidate(candidate),token).ConfigureAwait(false);
                if(!a.SequenceEqual(item.ExactHash)||!b.SequenceEqual(item.ExactHash)) return "Exact hash evidence changed.";
            }
            return null;
        }

        private string? ValidateSnapshot(VisualSimilarityMemberRecord member,string path,long size,DateTime modified,string volume,string identity)
        {
            if(member.Availability!=IndexedFileAvailability.Present||!string.Equals(member.FullPath,path,StringComparison.OrdinalIgnoreCase)||member.SizeBytes!=size||member.LastWriteUtc.Ticks!=modified.Ticks) return "The indexed path, size, or modification state changed.";
            if(!File.Exists(path)) return "The file no longer exists.";
            var info=new FileInfo(path); if(info.Length!=size||info.LastWriteTimeUtc.Ticks!=modified.Ticks) return "The on-disk file changed after preview.";
            LibraryFileIdentity current=_identityProvider.GetIdentity(path);
            if(!string.IsNullOrWhiteSpace(identity)&&(!string.Equals(current.VolumeId,volume,StringComparison.OrdinalIgnoreCase)||!string.Equals(current.FileId,identity,StringComparison.OrdinalIgnoreCase))) return "The stable file identity changed.";
            return null;
        }

        private static bool Eligible(VisualSimilarityMemberRecord m,bool candidate)=>m.Availability==IndexedFileAvailability.Present&&(!candidate||!m.IsProtected)&&File.Exists(m.FullPath);
        private static bool HashFactIsCurrent(LibraryFileHashFact? fact, VisualSimilarityMemberRecord member) =>
            fact?.FullHash != null && fact.FullVersion == ExactDuplicateHashService.FullVersion &&
            fact.SourceSizeBytes == member.SizeBytes && fact.SourceLastWriteUtc.Ticks == member.LastWriteUtc.Ticks;
        private IndexedFileRecord? FileRecord(VisualSimilarityMemberRecord m)=>_inventory.GetFileByPath(m.FullPath);
        private bool SamePhysicalFile(VisualSimilarityMemberRecord a,VisualSimilarityMemberRecord b){var x=FileRecord(a);var y=FileRecord(b);return x!=null&&y!=null&&!string.IsNullOrWhiteSpace(x.VolumeId)&&!string.IsNullOrWhiteSpace(x.FileIdentity)&&string.Equals(x.VolumeId,y.VolumeId,StringComparison.OrdinalIgnoreCase)&&string.Equals(x.FileIdentity,y.FileIdentity,StringComparison.OrdinalIgnoreCase);}
        internal static DuplicateItem ToLegacyItem(VisualSimilarityMemberRecord m)=>new(m.FullPath,m.SizeBytes,m.VideoCodec,m.Width??0,m.Height??0,m.DurationSeconds??0,m.TotalBitRate.HasValue?(int)Math.Clamp(m.TotalBitRate.Value/1000,0,int.MaxValue):0,m.LastWriteUtc,m.LastWriteUtc,m.IsProtected,"","");
        private LibraryHashCandidate ToHashCandidate(VisualSimilarityMemberRecord m){IndexedFileRecord f=FileRecord(m)!;return new LibraryHashCandidate(f.Id,f.FullPath,f.PathKey,f.SizeBytes,f.LastWriteTimeUtc,f.VolumeId,f.FileIdentity);}
        private string Recycle(string path){_actions.Recycle(path);return "Recycle Bin";} private string DeletePermanent(string path){_actions.DeletePermanent(path);return "Permanently deleted";}
        private void Record(VisualCleanupPlanRecord plan,VisualCleanupPlanItemRecord item,DuplicateCleanupItemStatus status,string destination,string message){_visual.UpdateVisualCleanupPlanItem(plan.PlanId,item.FileId,status,destination,status==DuplicateCleanupItemStatus.Succeeded?"":message);_visual.AppendVisualCleanupAudit(plan.PlanId,item.FileId,item.SourcePath,destination,plan.Action,status,message);}
    }
}
