using Microsoft.VisualBasic.FileIO;
using MediaFlux.Services;
using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog
{
    internal interface ILibraryDuplicateFileActions
    {
        void Recycle(string path);
        void DeletePermanent(string path);
        string Quarantine(string path, string quarantineRoot, long groupId, long fileId);
    }

    internal sealed class WindowsLibraryDuplicateFileActions : ILibraryDuplicateFileActions
    {
        public void Recycle(string path) => FileSystem.DeleteFile(
            path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

        public void DeletePermanent(string path) => File.Delete(path);

        public string Quarantine(string path, string quarantineRoot, long groupId, long fileId)
        {
            string folder = Path.Combine(quarantineRoot, $"group-{groupId}");
            Directory.CreateDirectory(folder);
            string destination = Path.Combine(folder, $"{fileId}-{Path.GetFileName(path)}");
            if (File.Exists(destination)) destination = Path.Combine(folder, $"{fileId}-{Guid.NewGuid():N}-{Path.GetFileName(path)}");
            File.Move(path, destination);
            return destination;
        }
    }

    public sealed record DuplicateCleanupExecutionResult(
        long PlanId,
        int Succeeded,
        int Excluded,
        int Failed,
        string ErrorText,
        long ReclaimedBytes = 0);

    public sealed record ExactCleanupCandidate(
        long GroupId,
        long FileId,
        long KeeperFileId,
        long SizeBytes);

    public sealed class LibraryDuplicateCleanupService
    {
        public const int CleanupBatchSize = 500;
        private readonly ILibraryCatalog _inventory;
        private readonly ILibraryAnalysisCatalog _analysis;
        private readonly ILibraryRecoveryCatalog? _recovery;
        private readonly ILibraryDuplicateFileActions _actions;
        private readonly ILibraryFileIdentityProvider _identityProvider;
        private readonly Func<bool> _isEncodingActive;
        private DuplicateKeeperPreferences _keeperPreferences;

        public LibraryDuplicateCleanupService(
            ILibraryCatalog inventory,
            ILibraryAnalysisCatalog analysis,
            Func<bool>? isEncodingActive = null)
            : this(inventory, analysis, null, new WindowsLibraryDuplicateFileActions(), new WindowsLibraryFileIdentityProvider(), isEncodingActive)
        {
        }

        public LibraryDuplicateCleanupService(
            ILibraryCatalog inventory,
            ILibraryAnalysisCatalog analysis,
            DuplicateKeeperPreferences? keeperPreferences,
            Func<bool>? isEncodingActive = null)
            : this(inventory, analysis, keeperPreferences, new WindowsLibraryDuplicateFileActions(), new WindowsLibraryFileIdentityProvider(), isEncodingActive)
        {
        }

        internal LibraryDuplicateCleanupService(
            ILibraryCatalog inventory,
            ILibraryAnalysisCatalog analysis,
            DuplicateKeeperPreferences? keeperPreferences,
            ILibraryDuplicateFileActions actions,
            ILibraryFileIdentityProvider identityProvider,
            Func<bool>? isEncodingActive = null)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
            _recovery = inventory as ILibraryRecoveryCatalog;
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
            _isEncodingActive = isEncodingActive ?? (() => false);
            _keeperPreferences = keeperPreferences?.Clone() ?? new DuplicateKeeperPreferences();
            _keeperPreferences.Normalize();
        }

        public void UpdateKeeperPreferences(DuplicateKeeperPreferences preferences)
        {
            ArgumentNullException.ThrowIfNull(preferences);
            DuplicateKeeperPreferences copy = preferences.Clone();
            copy.Normalize();
            _keeperPreferences = copy;
        }

        // This is the same catalog-side safety screen used while creating a cleanup
        // plan. It deliberately does not create a plan or touch files.
        public IReadOnlyList<ExactCleanupCandidate> GetEligibleCandidates(int maximumCandidates = 10_000)
        {
            maximumCandidates = Math.Clamp(maximumCandidates, 1, 50_000);
            var candidates = new List<ExactCleanupCandidate>();
            int offset = 0;
            while (candidates.Count < maximumCandidates)
            {
                ExactDuplicateGroupPage page = _analysis.QueryDuplicateGroups(new DuplicateGroupQuery(
                    SortColumn: "reclaimable", Descending: true, Offset: offset, Limit: 500));
                if (page.Groups.Count == 0) break;
                foreach (ExactDuplicateGroupRecord group in page.Groups)
                {
                    if (group.Ignored) continue;
                    IReadOnlyList<ExactDuplicateMemberRecord> members = _analysis.GetDuplicateGroupMembers(group.GroupId);
                    if (members.Count < 2) continue;
                    ExactDuplicateMemberRecord keeper = SelectKeeper(members);
                    LibraryFileHashFact? keeperFact = _analysis.GetFileHashFact(keeper.FileId);
                    if (keeperFact?.FullHash == null || !File.Exists(keeper.FullPath)) continue;
                    var legacyItems = members.Select(member => LibraryDuplicateAnalysisCoordinator.ToLegacyItem(member) with
                    {
                        Recommendation = member.FileId == keeper.FileId ? "Selected keeper" : "Trash candidate",
                        KeeperReason = member.FileId == keeper.FileId ? "Catalog keeper" : "Not selected to keep"
                    }).ToList();
                    var legacyGroup = new DuplicateGroup((int)Math.Min(int.MaxValue, group.GroupId), "Exact", 100,
                        "Matching size and SHA-256", "Catalog SHA-256", 0, 0, 0, 0, legacyItems);
                    var usedPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keeper.PhysicalIdentityKey };
                    foreach (ExactDuplicateMemberRecord member in members)
                    {
                        DuplicateItem legacy = legacyItems.First(x => string.Equals(x.Path, member.FullPath, StringComparison.OrdinalIgnoreCase));
                        LibraryFileHashFact? fact = _analysis.GetFileHashFact(member.FileId);
                        if (member.FileId == keeper.FileId || member.Availability != IndexedFileAvailability.Present || member.IsProtected ||
                            member.IsHardLinkAlias || !File.Exists(member.FullPath) || !usedPhysical.Add(member.PhysicalIdentityKey) ||
                            !DuplicateCleanupPolicy.CanCleanupItem(legacyGroup, legacy) || fact?.FullHash == null ||
                            !fact.FullHash.SequenceEqual(keeperFact.FullHash))
                            continue;
                        candidates.Add(new ExactCleanupCandidate(group.GroupId, member.FileId, keeper.FileId, member.SizeBytes));
                        if (candidates.Count >= maximumCandidates) break;
                    }
                    if (candidates.Count >= maximumCandidates) break;
                }
                offset += page.Groups.Count;
                if (offset >= page.TotalCount) break;
            }
            return candidates;
        }

        public DuplicateCleanupPlanSummary CreatePlan(
            IReadOnlyCollection<long> groupIds,
            DuplicateCleanupAction action,
            string quarantineRoot = "",
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(groupIds);
            if (groupIds.Count == 0) throw new ArgumentException("Select at least one exact duplicate group.", nameof(groupIds));
            long planId = _analysis.BeginCleanupPlan(action, quarantineRoot);
            try
            {
                foreach (long[] batch in groupIds.Distinct().Chunk(CleanupBatchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendGroupBatch(planId, batch, cancellationToken);
                }
                return FinishPlan(planId);
            }
            catch (OperationCanceledException)
            {
                _analysis.CompleteCleanupPlan(planId, DuplicateCleanupStatus.Failed, "Cleanup planning was canceled before the plan became ready.");
                throw;
            }
            catch
            {
                _analysis.CompleteCleanupPlan(planId, DuplicateCleanupStatus.Failed, "Cleanup planning failed before the plan became ready.");
                throw;
            }
        }

        public DuplicateCleanupPlanSummary CreatePlanForCandidates(
            IReadOnlyCollection<ExactCleanupCandidate> approvedCandidates,
            DuplicateCleanupAction action,
            string quarantineRoot = "",
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(approvedCandidates);
            if (approvedCandidates.Count == 0) throw new ArgumentException("Select at least one exact duplicate candidate.", nameof(approvedCandidates));
            HashSet<(long GroupId, long FileId)> currentlyEligible = GetEligibleCandidates(50_000)
                .Select(item => (item.GroupId, item.FileId)).ToHashSet();
            var items = new List<DuplicateCleanupPlanItemRecord>();
            foreach (ExactCleanupCandidate approved in approvedCandidates
                         .DistinctBy(item => (item.GroupId, item.FileId)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!currentlyEligible.Contains((approved.GroupId, approved.FileId))) continue;
                ExactDuplicateGroupRecord? group = _analysis.GetDuplicateGroup(approved.GroupId);
                IReadOnlyList<ExactDuplicateMemberRecord> members = _analysis.GetDuplicateGroupMembers(
                    approved.GroupId, new[] { approved.FileId, approved.KeeperFileId });
                ExactDuplicateMemberRecord? candidate = members.FirstOrDefault(item => item.FileId == approved.FileId);
                ExactDuplicateMemberRecord? keeper = members.FirstOrDefault(item => item.FileId == approved.KeeperFileId);
                LibraryFileHashFact? hash = _analysis.GetFileHashFact(approved.FileId);
                if (group == null || group.Ignored || candidate == null || keeper == null || hash?.FullHash == null ||
                    group.ManualKeeperFileId.GetValueOrDefault(group.SuggestedKeeperFileId ?? 0) != keeper.FileId)
                    continue;
                items.Add(new DuplicateCleanupPlanItemRecord(0, approved.GroupId, candidate.FileId, keeper.FileId,
                    candidate.FullPath, candidate.PathKey, candidate.SizeBytes, candidate.LastWriteUtc,
                    candidate.VolumeId, candidate.FileIdentity, hash.FullHash, DuplicateCleanupItemStatus.Planned, "", ""));
            }
            if (items.Count == 0) throw new InvalidOperationException("No selected exact candidates remain eligible after revalidation.");
            long planId = _analysis.CreateCleanupPlan(action, quarantineRoot, items);
            return _analysis.GetCleanupPlanSummary(planId)
                ?? throw new InvalidOperationException("The selected exact cleanup plan could not be reloaded.");
        }

        public DuplicateCleanupPlanSummary CreatePlanForAllEligible(
            DuplicateCleanupAction action,
            string quarantineRoot = "",
            CancellationToken cancellationToken = default)
        {
            long planId = _analysis.BeginCleanupPlan(action, quarantineRoot);
            long afterGroupId = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<long> groupIds = _analysis.GetCleanupEligibleDuplicateGroupIds(afterGroupId, CleanupBatchSize);
                    if (groupIds.Count == 0) break;
                    AppendGroupBatch(planId, groupIds, cancellationToken);
                    afterGroupId = groupIds[^1];
                    if (groupIds.Count < CleanupBatchSize) break;
                }
                return FinishPlan(planId);
            }
            catch (OperationCanceledException)
            {
                _analysis.CompleteCleanupPlan(planId, DuplicateCleanupStatus.Failed, "Cleanup planning was canceled before the plan became ready.");
                throw;
            }
            catch
            {
                _analysis.CompleteCleanupPlan(planId, DuplicateCleanupStatus.Failed, "Cleanup planning failed before the plan became ready.");
                throw;
            }
        }

        private DuplicateCleanupPlanSummary FinishPlan(long planId)
        {
            DuplicateCleanupPlanSummary draft = _analysis.GetCleanupPlanSummary(planId, includeLocations: false)
                ?? throw new InvalidOperationException("The cleanup plan could not be reloaded.");
            if (draft.TotalItems == 0)
            {
                _analysis.CompleteCleanupPlan(planId, DuplicateCleanupStatus.Failed,
                    "No safe cleanup candidates remain after keeper, protection, availability, hard-link, and hash checks.");
                throw new InvalidOperationException("No safe cleanup candidates remain after keeper, protection, availability, hard-link, and hash checks.");
            }
            _analysis.MarkCleanupPlanReady(planId);
            return _analysis.GetCleanupPlanSummary(planId) ?? throw new InvalidOperationException("The ready cleanup plan could not be reloaded.");
        }

        private void AppendGroupBatch(long planId, IReadOnlyCollection<long> groupIds, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int inserted = _analysis.AppendEligibleCleanupGroups(planId, groupIds);
                if (inserted < CleanupBatchSize) return;
            }
        }

        public async Task<DuplicateCleanupExecutionResult> ExecutePlanAsync(
            long planId,
            CancellationToken cancellationToken = default,
            IProgress<DuplicateCleanupProgress>? progress = null)
        {
            if (_isEncodingActive()) throw new InvalidOperationException("Stop the active encode before duplicate cleanup.");
            DuplicateCleanupPlanSummary plan = _analysis.GetCleanupPlanSummary(planId, includeLocations: false) ?? throw new KeyNotFoundException($"Cleanup plan {planId} does not exist.");
            if (plan.Status != DuplicateCleanupStatus.Ready) throw new InvalidOperationException("Only a ready cleanup plan can be executed.");
            _analysis.MarkCleanupPlanRunning(planId);
            long succeeded = plan.SucceededItems, excluded = plan.ExcludedItems, failed = plan.FailedItems, reclaimed = plan.ReclaimedBytes;
            string fatal = "";
            long afterGroupId = 0, afterFileId = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<DuplicateCleanupPlanItemRecord> batch = _analysis.GetCleanupPlanItemsBatch(
                        planId, afterGroupId, afterFileId, CleanupBatchSize, DuplicateCleanupItemStatus.Planned);
                    if (batch.Count == 0) break;
                    foreach (DuplicateCleanupPlanItemRecord item in batch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ExactDuplicateGroupRecord? group = _analysis.GetDuplicateGroup(item.GroupId);
                        long? currentKeeperId = group?.ManualKeeperFileId ?? group?.SuggestedKeeperFileId;
                        if (group == null || group.Ignored || currentKeeperId != item.KeeperFileId)
                        {
                            Exclude(planId, plan.Action, item, group?.Ignored == true ? "The duplicate group is now ignored." : "The keeper decision changed or the group is no longer active.");
                            excluded++;
                            continue;
                        }
                        IReadOnlyList<ExactDuplicateMemberRecord> currentMembers = _analysis.GetDuplicateGroupMembers(
                            item.GroupId, new[] { item.KeeperFileId, item.FileId });
                        ExactDuplicateMemberRecord? keeper = currentMembers.FirstOrDefault(member => member.FileId == item.KeeperFileId);
                        ExactDuplicateMemberRecord? current = currentMembers.FirstOrDefault(member => member.FileId == item.FileId);
                        string? keeperError = await ValidateMemberAsync(keeper, item.FullHash, cancellationToken).ConfigureAwait(false);
                        if (keeperError != null)
                        {
                            Exclude(planId, plan.Action, item, "Keeper validation failed immediately before action: " + keeperError);
                            excluded++;
                            continue;
                        }
                        string? validation = await ValidatePlanItemAsync(item, current, item.FullHash, cancellationToken).ConfigureAwait(false);
                        if (validation != null)
                        {
                            Exclude(planId, plan.Action, item, validation); excluded++; continue;
                        }
                        string destination = "";
                        try
                        {
                            destination = plan.Action switch
                            {
                                DuplicateCleanupAction.RecycleBin => Recycle(item.SourcePath),
                                DuplicateCleanupAction.Quarantine => _actions.Quarantine(item.SourcePath, plan.QuarantineRoot, item.GroupId, item.FileId),
                                DuplicateCleanupAction.PermanentDelete => DeletePermanent(item.SourcePath),
                                _ => throw new InvalidOperationException("Unknown cleanup action.")
                            };
                            _analysis.RecordCleanupPlanItemOutcome(planId, item.FileId, item.SourcePath, destination, plan.Action,
                                DuplicateCleanupItemStatus.Succeeded, "", "Validated exact duplicate cleanup succeeded.");
                            _recovery?.MarkFileRemovedByCleanup(item.FileId, item.SourcePath,
                                $"Exact cleanup plan {planId} completed using {plan.Action}.", exactPlanId: planId,
                                sourcePlanItemFileId: item.FileId);
                            succeeded++; reclaimed += item.SourceSizeBytes;
                        }
                        catch (Exception ex)
                        {
                            _analysis.RecordCleanupPlanItemOutcome(planId, item.FileId, item.SourcePath, destination, plan.Action,
                                DuplicateCleanupItemStatus.Failed, ex.Message, ex.Message);
                            failed++;
                        }
                    }
                    DuplicateCleanupPlanItemRecord last = batch[^1];
                    afterGroupId = last.GroupId;
                    afterFileId = last.FileId;
                    progress?.Report(new DuplicateCleanupProgress(planId, plan.TotalItems, succeeded + excluded + failed,
                        succeeded, excluded, failed, reclaimed));
                }
            }
            catch (OperationCanceledException)
            {
                fatal = $"Canceled after {succeeded:N0} successful action(s).";
            }
            DuplicateCleanupStatus final = failed > 0 || fatal.Length > 0 ? DuplicateCleanupStatus.Failed : DuplicateCleanupStatus.Completed;
            _analysis.CompleteCleanupPlan(planId, final, fatal);
            DuplicateCleanupPlanSummary completed = _analysis.GetCleanupPlanSummary(planId, includeLocations: false)
                ?? throw new InvalidOperationException("The completed cleanup plan could not be reloaded.");
            return new DuplicateCleanupExecutionResult(planId, checked((int)completed.SucceededItems), checked((int)completed.ExcludedItems),
                checked((int)completed.FailedItems), completed.ErrorText, completed.ReclaimedBytes);
        }

        private async Task<string?> ValidatePlanItemAsync(DuplicateCleanupPlanItemRecord item, ExactDuplicateMemberRecord? current, byte[] expected, CancellationToken token)
        {
            if (current == null) return "The file is no longer a member of the exact group.";
            if (current.IsProtected) return "The file is protected.";
            if (current.Availability != IndexedFileAvailability.Present) return "The file is unavailable or missing.";
            if (current.FileId == item.KeeperFileId) return "The keeper can never be a cleanup candidate.";
            if (current.IsHardLinkAlias) return "Hard-link aliases are excluded from cleanup.";
            if (!string.Equals(current.FullPath, item.SourcePath, StringComparison.OrdinalIgnoreCase) || current.SizeBytes != item.SourceSizeBytes || current.LastWriteUtc.Ticks != item.SourceLastWriteUtc.Ticks)
                return "The indexed path, size, or modification state changed.";
            if (!File.Exists(item.SourcePath)) return "The file no longer exists.";
            var info = new FileInfo(item.SourcePath);
            if (info.Length != item.SourceSizeBytes || info.LastWriteTimeUtc.Ticks != item.SourceLastWriteUtc.Ticks) return "The on-disk file changed after the plan was created.";
            LibraryFileIdentity identity = _identityProvider.GetIdentity(item.SourcePath);
            if (!string.IsNullOrWhiteSpace(item.SourceFileIdentity) &&
               (!string.Equals(identity.VolumeId, item.SourceVolumeId, StringComparison.OrdinalIgnoreCase) || !string.Equals(identity.FileId, item.SourceFileIdentity, StringComparison.OrdinalIgnoreCase)))
                return "The stable file identity changed.";
            byte[] actual = await ExactDuplicateHashService.ComputeFullAsync(new LibraryHashCandidate(item.FileId, item.SourcePath, item.SourcePathKey, item.SourceSizeBytes, item.SourceLastWriteUtc, item.SourceVolumeId, item.SourceFileIdentity), token).ConfigureAwait(false);
            return actual.SequenceEqual(expected) ? null : "The SHA-256 no longer matches the validated keeper.";
        }

        private static async Task<string?> ValidateMemberAsync(ExactDuplicateMemberRecord? member, byte[] expected, CancellationToken token)
        {
            if (member == null) return "The keeper is no longer in the group.";
            if (member.Availability != IndexedFileAvailability.Present || !File.Exists(member.FullPath)) return "The keeper is missing or unavailable.";
            try
            {
                byte[] hash = await ExactDuplicateHashService.ComputeFullAsync(new LibraryHashCandidate(member.FileId, member.FullPath, member.PathKey, member.SizeBytes, member.LastWriteUtc, member.VolumeId, member.FileIdentity), token).ConfigureAwait(false);
                return hash.SequenceEqual(expected) ? null : "The keeper SHA-256 changed.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { return ex.Message; }
        }

        private string Recycle(string path) { _actions.Recycle(path); return "Recycle Bin"; }
        private string DeletePermanent(string path) { _actions.DeletePermanent(path); return "Permanently deleted"; }
        private void Exclude(long planId, DuplicateCleanupAction action, DuplicateCleanupPlanItemRecord item, string message)
        {
            _analysis.RecordCleanupPlanItemOutcome(planId, item.FileId, item.SourcePath, "", action,
                DuplicateCleanupItemStatus.Excluded, message, message);
        }

        private ExactDuplicateMemberRecord SelectKeeper(IReadOnlyList<ExactDuplicateMemberRecord> members)
        {
            ExactDuplicateMemberRecord? manual = members.FirstOrDefault(member => member.IsManualKeeper);
            if (manual != null) return manual;
            ExactDuplicateMemberRecord? suggested = members.FirstOrDefault(member => member.IsSuggestedKeeper);
            if (suggested != null) return suggested;
            return ExactDuplicateKeeperPolicy.Select(members, _keeperPreferences).Keeper;
        }
    }
}
