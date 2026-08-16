using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed record VisualCleanupProposalItem(
        VisualSimilarityGroupRecord Group,
        VisualSimilarityMemberRecord Keeper,
        VisualSimilarityMemberRecord Candidate,
        string KeeperReason,
        bool HasExactEvidence,
        byte[]? ExactHash,
        VisualCleanupIntent Intent = VisualCleanupIntent.DeleteCandidate,
        long? FamilyId = null)
    {
        public long ReclaimableBytes => Candidate.SizeBytes + (Intent == VisualCleanupIntent.DeleteBoth ? Keeper.SizeBytes : 0);
    }

    public sealed record VisualCleanupProposal(
        IReadOnlyList<VisualCleanupProposalItem> Items,
        int ExcludedGroups,
        long ReclaimableBytes,
        bool IncludesUnreviewed,
        bool IsTruncated);

    public sealed class LibraryVisualDuplicateCleanupService
    {
        private const int CleanupBatchSize = 500;
        private readonly ILibraryCatalog _inventory;
        private readonly ILibraryAnalysisCatalog _analysis;
        private readonly ILibraryVisualCatalog _visual;
        private readonly ILibraryVisualFamilyCatalog? _families;
        private readonly ILibraryRecoveryCatalog? _recovery;
        private readonly ILibraryDuplicateFileActions _actions;
        private readonly ILibraryFileIdentityProvider _identityProvider;
        private readonly Func<bool> _isEncodingActive;
        private DuplicateKeeperPreferences _preferences;
        private readonly object _preferencesSync = new();

        public LibraryVisualDuplicateCleanupService(ILibraryCatalog inventory, ILibraryAnalysisCatalog analysis,
            ILibraryVisualCatalog visual, DuplicateKeeperPreferences? preferences = null, Func<bool>? isEncodingActive = null)
            : this(inventory, analysis, visual, new WindowsLibraryDuplicateFileActions(), new WindowsLibraryFileIdentityProvider(), preferences, isEncodingActive) { }

        internal LibraryVisualDuplicateCleanupService(ILibraryCatalog inventory, ILibraryAnalysisCatalog analysis,
            ILibraryVisualCatalog visual, ILibraryDuplicateFileActions actions, ILibraryFileIdentityProvider identityProvider,
            DuplicateKeeperPreferences? preferences = null, Func<bool>? isEncodingActive = null)
        {
            _inventory=inventory; _analysis=analysis; _visual=visual; _actions=actions; _identityProvider=identityProvider;
            _families=visual as ILibraryVisualFamilyCatalog;
            _recovery=inventory as ILibraryRecoveryCatalog;
            _preferences=(preferences??new DuplicateKeeperPreferences()).Clone(); _preferences.Normalize();
            _isEncodingActive=isEncodingActive??(()=>false);
        }

        public void UpdateKeeperPreferences(DuplicateKeeperPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            DuplicateKeeperPreferences normalized = preferences.Clone();
            normalized.Normalize();
            lock (_preferencesSync) _preferences = normalized;
        }

        public VisualCleanupProposal BuildProposal(bool includeUnreviewed=false, double minimumConfidence=95,
            IReadOnlyCollection<long>? groupIds=null, int maximumItems=5000, bool includeFamilyPairs=false)
        {
            minimumConfidence=Math.Clamp(minimumConfidence,0,100);
            maximumItems=Math.Clamp(maximumItems,1,10_000);
            HashSet<long>? selected=groupIds?.ToHashSet();
            var proposed=new List<VisualCleanupProposalItem>(); int excluded=0; int offset=0; bool truncated=false;
            while(true)
            {
                VisualSimilarityGroupPage page=_visual.QueryVisualGroups(new VisualGroupQuery(
                    Reviewed: includeUnreviewed?null:true, Ignored:false, NotMatch:false, MinimumConfidence:includeUnreviewed?minimumConfidence:0,
                    SortColumn:"reclaimable", Descending:true, Offset:offset, Limit:500, IncludeFamilyPairs:includeFamilyPairs));
                if(page.Groups.Count==0) break;
                foreach(VisualSimilarityGroupRecord group in page.Groups)
                {
                    if(selected!=null && !selected.Contains(group.GroupId)) continue;
                    if(group.Ignored || group.NotMatch || (!includeUnreviewed && !group.Reviewed) || (includeUnreviewed && !group.Reviewed && group.ConfidenceScore<minimumConfidence)) { excluded++; continue; }
                    IReadOnlyList<VisualSimilarityMemberRecord> members=_visual.GetVisualGroupMembers(group.GroupId);
                    if(members.Count!=2) { excluded++; continue; }
                    VisualSimilarityMemberRecord? keeper=members.FirstOrDefault(x=>x.IsManualKeeper)
                        ?? members.FirstOrDefault(x=>x.IsProtected)
                        ?? members.FirstOrDefault(x=>x.IsSuggestedKeeper);
                    string reason=keeper?.IsManualKeeper==true?"Manual keeper selection":keeper?.IsProtected==true?"Protected keeper":"Keeper recommendation";
                    if(keeper==null)
                    {
                        DuplicateKeeperPreferences preferences; lock (_preferencesSync) preferences=_preferences.Clone();
                        DuplicateKeeperEvaluation score=DuplicateKeeperScoringService.Evaluate(members.Select(ToLegacyItem).ToArray(),preferences,DuplicateKeeperScoringContext.Visual,group.ConfidenceScore);
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

        public VisualCleanupProposal BuildDeleteBothProposal(long groupId)
        {
            VisualSimilarityGroupRecord? group = _visual.GetVisualGroup(groupId);
            if (group == null || group.Ignored || group.NotMatch)
                return new VisualCleanupProposal(Array.Empty<VisualCleanupProposalItem>(), 1, 0, false, false);
            IReadOnlyList<VisualSimilarityMemberRecord> members = _visual.GetVisualGroupMembers(groupId);
            if (members.Count != 2 || members.Any(member => !Eligible(member, true)) || SamePhysicalFile(members[0], members[1]))
                return new VisualCleanupProposal(Array.Empty<VisualCleanupProposalItem>(), 1, 0, false, false);
            LibraryFileHashFact? firstHash = _analysis.GetFileHashFact(members[0].FileId);
            LibraryFileHashFact? secondHash = _analysis.GetFileHashFact(members[1].FileId);
            bool exact = HashFactIsCurrent(firstHash, members[0]) && HashFactIsCurrent(secondHash, members[1]) &&
                         firstHash!.FullHash!.SequenceEqual(secondHash!.FullHash!);
            var item = new VisualCleanupProposalItem(group, members[1], members[0],
                "Explicit Delete Both selection — no keeper will remain", exact, exact ? firstHash!.FullHash : null,
                VisualCleanupIntent.DeleteBoth);
            return new VisualCleanupProposal(new[] { item }, 0, item.ReclaimableBytes, false, false);
        }

        public VisualCleanupPlanRecord CreatePlan(IReadOnlyCollection<VisualCleanupProposalItem> approved,
            DuplicateCleanupAction action, string quarantineRoot="", bool allowUnreviewed=false, double minimumConfidence=95)
        {
            if(approved.Count==0) throw new ArgumentException("Select at least one visual cleanup candidate.",nameof(approved));
            VisualCleanupPlanItemRecord[] items=CreatePlanItems(approved);
            if(items.Length==0) throw new InvalidOperationException("No safe visual cleanup candidates remain.");
            long id=_visual.CreateVisualCleanupPlan(action,quarantineRoot,allowUnreviewed,minimumConfidence,items);
            return _visual.GetVisualCleanupPlan(id)??throw new InvalidOperationException("The visual cleanup plan could not be reloaded.");
        }

        public long BeginPlan(
            DuplicateCleanupAction action,
            string quarantineRoot = "",
            bool allowUnreviewed = false,
            double minimumConfidence = 95) =>
            _visual.BeginVisualCleanupPlan(action, quarantineRoot, allowUnreviewed, minimumConfidence);

        public int AppendPlanItems(long planId, IReadOnlyCollection<VisualCleanupProposalItem> approved)
        {
            VisualCleanupPlanItemRecord[] items = CreatePlanItems(approved);
            foreach (VisualCleanupPlanItemRecord[] batch in items.Chunk(CleanupBatchSize))
                _visual.AppendVisualCleanupPlanItems(planId, batch);
            return items.Length;
        }

        public VisualCleanupPlanSummary ReadyPlan(long planId)
        {
            _visual.MarkVisualCleanupPlanReady(planId);
            return _visual.GetVisualCleanupPlanSummary(planId)
                ?? throw new InvalidOperationException("The ready visual cleanup plan could not be reloaded.");
        }

        public VisualCleanupPlanSummary GetPlanSummary(long planId) =>
            _visual.GetVisualCleanupPlanSummary(planId)
            ?? throw new KeyNotFoundException($"Visual cleanup plan {planId} does not exist.");

        public void FailPlan(long planId, string reason) =>
            _visual.CompleteVisualCleanupPlan(planId, DuplicateCleanupStatus.Failed, reason);

        private VisualCleanupPlanItemRecord[] CreatePlanItems(
            IReadOnlyCollection<VisualCleanupProposalItem> approved)
        {
            var items = new List<VisualCleanupPlanItemRecord>(approved.Count);
            foreach (VisualCleanupProposalItem proposal in approved)
            {
                IndexedFileRecord? source = _inventory.GetFileByPath(proposal.Candidate.FullPath);
                IndexedFileRecord? keeper = _inventory.GetFileByPath(proposal.Keeper.FullPath);
                if (source == null || keeper == null || source.Id != proposal.Candidate.FileId ||
                    keeper.Id != proposal.Keeper.FileId)
                    continue;
                items.Add(new VisualCleanupPlanItemRecord(
                    0, proposal.Group.GroupKey, proposal.Group.GroupId, source.Id, keeper.Id,
                    source.FullPath, source.SizeBytes, source.LastWriteTimeUtc, source.VolumeId,
                    source.FileIdentity, keeper.FullPath, keeper.SizeBytes, keeper.LastWriteTimeUtc,
                    keeper.VolumeId, keeper.FileIdentity, proposal.Group.ConfidenceScore,
                    proposal.ExactHash, proposal.Intent, DuplicateCleanupItemStatus.Planned, "", "",
                    proposal.FamilyId));
            }
            return items.ToArray();
        }

        public async Task<DuplicateCleanupExecutionResult> ExecutePlanAsync(
            long planId,
            CancellationToken token = default,
            IProgress<DuplicateCleanupProgress>? progress = null)
        {
            if (_isEncodingActive())
                throw new InvalidOperationException("Stop the active encode before duplicate cleanup.");
            VisualCleanupPlanSummary plan = _visual.GetVisualCleanupPlanSummary(planId, includeLocations: false)
                ?? throw new KeyNotFoundException($"Visual cleanup plan {planId} does not exist.");
            if (plan.Status != DuplicateCleanupStatus.Ready)
                throw new InvalidOperationException("Only a ready visual cleanup plan can be executed.");
            _visual.MarkVisualCleanupPlanRunning(planId);
            long succeeded = plan.SucceededItems;
            long excluded = plan.ExcludedItems;
            long failed = plan.FailedItems;
            long reclaimed = plan.ReclaimedBytes;
            long afterGroupId = 0;
            long afterFileId = 0;
            string fatal = "";
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    IReadOnlyList<VisualCleanupPlanItemRecord> batch =
                        _visual.GetVisualCleanupPlanItemsBatch(
                            planId, afterGroupId, afterFileId, CleanupBatchSize,
                            DuplicateCleanupItemStatus.Planned);
                    if (batch.Count == 0) break;
                    foreach (VisualCleanupPlanItemRecord item in batch)
                    {
                        token.ThrowIfCancellationRequested();
                        string? error = await ValidateAsync(plan, item, token).ConfigureAwait(false);
                        if (error != null)
                        {
                            Record(plan, item, DuplicateCleanupItemStatus.Excluded, "", error);
                            excluded++;
                            if (item.Intent == VisualCleanupIntent.DeleteBoth)
                            {
                                _visual.AppendVisualCleanupAudit(
                                    plan.PlanId, item.KeeperFileId, item.KeeperPath, "", plan.Action,
                                    DuplicateCleanupItemStatus.Excluded, error);
                                excluded++;
                            }
                            continue;
                        }
                        if (item.Intent == VisualCleanupIntent.DeleteBoth)
                        {
                            (int itemSucceeded, int itemFailed) = ExecuteDeleteBoth(plan, item);
                            succeeded += itemSucceeded;
                            failed += itemFailed;
                            if (itemFailed == 0)
                                reclaimed += item.SourceSizeBytes + item.KeeperSizeBytes;
                            continue;
                        }
                        string destination = "";
                        try
                        {
                            destination = ExecuteAction(
                                plan, item.GroupId, item.FileId, item.SourcePath);
                            Record(
                                plan, item, DuplicateCleanupItemStatus.Succeeded, destination,
                                item.ExactHash == null
                                    ? "User-approved visual duplicate cleanup succeeded."
                                    : "Exact hash evidence confirmed; cleanup succeeded.");
                            _recovery?.MarkFileRemovedByCleanup(
                                item.FileId, item.SourcePath,
                                $"Visual cleanup plan {plan.PlanId} completed using {plan.Action}.", visualPlanId: plan.PlanId,
                                sourcePlanItemFileId: item.FileId);
                            succeeded++;
                            reclaimed += item.SourceSizeBytes;
                        }
                        catch (Exception ex)
                        {
                            Record(
                                plan, item, DuplicateCleanupItemStatus.Failed, destination, ex.Message);
                            failed++;
                        }
                    }
                    VisualCleanupPlanItemRecord last = batch[^1];
                    afterGroupId = last.GroupId;
                    afterFileId = last.FileId;
                    progress?.Report(new DuplicateCleanupProgress(
                        planId, plan.TotalItems, succeeded + excluded + failed,
                        succeeded, excluded, failed, reclaimed));
                }
            }
            catch (OperationCanceledException)
            {
                fatal = $"Canceled after {succeeded:N0} successful action(s).";
            }
            DuplicateCleanupStatus final =
                failed > 0 || fatal.Length > 0
                    ? DuplicateCleanupStatus.Failed
                    : DuplicateCleanupStatus.Completed;
            _visual.CompleteVisualCleanupPlan(planId, final, fatal);
            VisualCleanupPlanSummary completed =
                _visual.GetVisualCleanupPlanSummary(planId, includeLocations: false)
                ?? throw new InvalidOperationException("The completed visual cleanup plan could not be reloaded.");
            return new DuplicateCleanupExecutionResult(
                planId, checked((int)completed.SucceededItems),
                checked((int)completed.ExcludedItems), checked((int)completed.FailedItems),
                completed.ErrorText, completed.ReclaimedBytes);
        }

        private (int Succeeded, int Failed) ExecuteDeleteBoth(VisualCleanupPlanSummary plan, VisualCleanupPlanItemRecord item)
        {
            int succeeded = 0, failed = 0;
            var outcomes = new List<string>(2);
            foreach ((long fileId, string path) in new[] { (item.FileId, item.SourcePath), (item.KeeperFileId, item.KeeperPath) })
            {
                string destination = "";
                try
                {
                    destination = ExecuteAction(plan, item.GroupId, fileId, path);
                    _visual.AppendVisualCleanupAudit(plan.PlanId,fileId,path,destination,plan.Action,DuplicateCleanupItemStatus.Succeeded,
                        item.ExactHash==null?"User-approved Delete Both visual cleanup succeeded; no keeper remains.":"Exact hash evidence confirmed; Delete Both cleanup succeeded; no keeper remains.");
                    _recovery?.MarkFileRemovedByCleanup(fileId, path,
                        $"Visual Delete Both plan {plan.PlanId} completed using {plan.Action}.", visualPlanId: plan.PlanId,
                        sourcePlanItemFileId: item.FileId);
                    outcomes.Add(destination);
                    succeeded++;
                }
                catch (Exception ex)
                {
                    _visual.AppendVisualCleanupAudit(plan.PlanId,fileId,path,destination,plan.Action,DuplicateCleanupItemStatus.Failed,ex.Message);
                    outcomes.Add(ex.Message);
                    failed++;
                }
            }
            _visual.UpdateVisualCleanupPlanItem(plan.PlanId,item.FileId,
                failed == 0 ? DuplicateCleanupItemStatus.Succeeded : DuplicateCleanupItemStatus.Failed,
                string.Join("; ", outcomes), failed == 0 ? "" : "One or more Delete Both actions failed.");
            return (succeeded, failed);
        }

        private string ExecuteAction(VisualCleanupPlanSummary plan, long groupId, long fileId, string path) => plan.Action switch
        {
            DuplicateCleanupAction.RecycleBin => Recycle(path),
            DuplicateCleanupAction.Quarantine => _actions.Quarantine(path,plan.QuarantineRoot,groupId,fileId),
            DuplicateCleanupAction.PermanentDelete => DeletePermanent(path),
            _ => throw new InvalidOperationException("Unknown cleanup action.")
        };

        private async Task<string?> ValidateAsync(VisualCleanupPlanSummary plan,VisualCleanupPlanItemRecord item,CancellationToken token)
        {
            if (item.FamilyId.HasValue)
            {
                if (_families == null)
                    return "The persisted visual family state is unavailable.";
                VisualFamilyRecord? family = _families.GetVisualFamily(item.FamilyId.Value);
                if (family == null || family.Eligibility != LibraryMatchEligibilityState.Active)
                    return "The visual family is stale or no longer active.";
                if (!family.Reviewed || family.Ignored)
                    return family.Ignored
                        ? "The visual family is now ignored."
                        : "The visual family is no longer reviewed.";
                if (!family.ManualKeeperFileId.HasValue)
                    return "The visual family no longer has a persisted manual keeper.";
                if (family.ManualKeeperFileId.Value != item.KeeperFileId)
                    return "The visual family keeper decision changed.";
                IReadOnlyList<VisualFamilyMemberRecord> familyMembers =
                    _families.GetVisualFamilyMembers(family.FamilyId);
                VisualFamilyMemberRecord? familyKeeper =
                    familyMembers.FirstOrDefault(member => member.FileId == item.KeeperFileId);
                VisualFamilyMemberRecord? familyCandidate =
                    familyMembers.FirstOrDefault(member => member.FileId == item.FileId);
                if (familyKeeper == null || familyCandidate == null)
                    return "The keeper or candidate is no longer in the visual family.";
                if (familyKeeper.Availability != IndexedFileAvailability.Present ||
                    !File.Exists(familyKeeper.FullPath))
                    return "The visual family keeper is missing or unavailable.";
                if (familyCandidate.IsProtected)
                    return "The visual family candidate is protected.";
            }
            VisualSimilarityGroupRecord? group=_visual.GetVisualGroupByKey(item.GroupKey);
            if(group==null||group.Ignored||group.NotMatch) return "The visual match is no longer current, is ignored, or was marked not a match.";
            if(group.ConfidenceScore + 0.001 < item.ConfidenceScore) return "The current visual confidence is lower than the approved evidence.";
            if(!item.FamilyId.HasValue && item.Intent != VisualCleanupIntent.DeleteBoth && !plan.AllowUnreviewed&&!group.Reviewed) return "The visual match is no longer reviewed.";
            if(!item.FamilyId.HasValue && item.Intent != VisualCleanupIntent.DeleteBoth && plan.AllowUnreviewed&&!group.Reviewed&&group.ConfidenceScore<plan.MinimumConfidence) return "The unreviewed match no longer meets the confidence threshold.";
            if(!item.FamilyId.HasValue && item.Intent != VisualCleanupIntent.DeleteBoth && group.ManualKeeperFileId.HasValue&&group.ManualKeeperFileId!=item.KeeperFileId) return "The manual keeper decision changed.";
            if(!item.FamilyId.HasValue && item.Intent != VisualCleanupIntent.DeleteBoth && !group.ManualKeeperFileId.HasValue&&group.SuggestedKeeperFileId!=item.KeeperFileId) return "The keeper recommendation changed.";
            IReadOnlyList<VisualSimilarityMemberRecord> members=_visual.GetVisualGroupMembers(group.GroupId);
            VisualSimilarityMemberRecord? keeper=members.FirstOrDefault(x=>x.FileId==item.KeeperFileId), candidate=members.FirstOrDefault(x=>x.FileId==item.FileId);
            if(keeper==null||candidate==null) return "The files are no longer members of the visual match.";
            if(candidate.IsProtected) return "The candidate is protected.";
            if(item.Intent == VisualCleanupIntent.DeleteBoth && keeper.IsProtected) return "Delete Both is blocked because one or more files are protected.";
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
        internal static DuplicateItem ToLegacyItem(VisualSimilarityMemberRecord m)=>new(m.FullPath,m.SizeBytes,m.VideoCodec,m.Width??0,m.Height??0,m.DurationSeconds??0,m.TotalBitRate.HasValue?(int)Math.Clamp(m.TotalBitRate.Value/1000,0,int.MaxValue):0,m.LastWriteUtc,m.LastWriteUtc,m.IsProtected,"","") { FrameRate=m.FrameRate??0 };
        private LibraryHashCandidate ToHashCandidate(VisualSimilarityMemberRecord m){IndexedFileRecord f=FileRecord(m)!;return new LibraryHashCandidate(f.Id,f.FullPath,f.PathKey,f.SizeBytes,f.LastWriteTimeUtc,f.VolumeId,f.FileIdentity);}
        private string Recycle(string path){_actions.Recycle(path);return "Recycle Bin";} private string DeletePermanent(string path){_actions.DeletePermanent(path);return "Permanently deleted";}
        private void Record(VisualCleanupPlanSummary plan,VisualCleanupPlanItemRecord item,DuplicateCleanupItemStatus status,string destination,string message){_visual.UpdateVisualCleanupPlanItem(plan.PlanId,item.FileId,status,destination,status==DuplicateCleanupItemStatus.Succeeded?"":message);_visual.AppendVisualCleanupAudit(plan.PlanId,item.FileId,item.SourcePath,destination,plan.Action,status,message);}
    }
}
