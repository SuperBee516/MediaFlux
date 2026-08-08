using Microsoft.VisualBasic.FileIO;
using MediaFlux.Services;

namespace MediaFlux.Services.LibraryCatalog
{
    internal interface ILibraryDuplicateFileActions
    {
        void Recycle(string path);
        string Quarantine(string path, string quarantineRoot, long groupId, long fileId);
    }

    internal sealed class WindowsLibraryDuplicateFileActions : ILibraryDuplicateFileActions
    {
        public void Recycle(string path) => FileSystem.DeleteFile(
            path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

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
        string ErrorText);

    public sealed class LibraryDuplicateCleanupService
    {
        private readonly ILibraryCatalog _inventory;
        private readonly ILibraryAnalysisCatalog _analysis;
        private readonly ILibraryDuplicateFileActions _actions;
        private readonly ILibraryFileIdentityProvider _identityProvider;
        private readonly Func<bool> _isEncodingActive;

        public LibraryDuplicateCleanupService(
            ILibraryCatalog inventory,
            ILibraryAnalysisCatalog analysis,
            Func<bool>? isEncodingActive = null)
            : this(inventory, analysis, new WindowsLibraryDuplicateFileActions(), new WindowsLibraryFileIdentityProvider(), isEncodingActive)
        {
        }

        internal LibraryDuplicateCleanupService(
            ILibraryCatalog inventory,
            ILibraryAnalysisCatalog analysis,
            ILibraryDuplicateFileActions actions,
            ILibraryFileIdentityProvider identityProvider,
            Func<bool>? isEncodingActive = null)
        {
            _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
            _analysis = analysis ?? throw new ArgumentNullException(nameof(analysis));
            _actions = actions ?? throw new ArgumentNullException(nameof(actions));
            _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
            _isEncodingActive = isEncodingActive ?? (() => false);
        }

        public DuplicateCleanupPlanRecord CreatePlan(
            IReadOnlyCollection<long> groupIds,
            DuplicateCleanupAction action,
            string quarantineRoot = "")
        {
            ArgumentNullException.ThrowIfNull(groupIds);
            if (groupIds.Count == 0) throw new ArgumentException("Select at least one exact duplicate group.", nameof(groupIds));
            var items = new List<DuplicateCleanupPlanItemRecord>();
            foreach (long groupId in groupIds.Distinct())
            {
                ExactDuplicateGroupRecord? groupRecord = _analysis.GetDuplicateGroup(groupId);
                if (groupRecord == null || groupRecord.Ignored) continue;
                IReadOnlyList<ExactDuplicateMemberRecord> members = _analysis.GetDuplicateGroupMembers(groupId);
                if (members.Count < 2) continue;
                ExactDuplicateMemberRecord keeper = members.FirstOrDefault(x => x.IsManualKeeper)
                    ?? members.FirstOrDefault(x => x.IsSuggestedKeeper)
                    ?? members.FirstOrDefault(x => x.IsProtected)
                    ?? members[0];
                LibraryFileHashFact? keeperFact = _analysis.GetFileHashFact(keeper.FileId);
                if (keeperFact?.FullHash == null) continue;
                var legacyItems = members.Select(member => LibraryDuplicateAnalysisCoordinator.ToLegacyItem(member) with
                {
                    Recommendation = member.FileId == keeper.FileId ? (member.IsProtected ? "Protected keeper" : "Selected keeper") :
                        member.IsProtected ? "Protected reference" : "Trash candidate",
                    KeeperReason = member.FileId == keeper.FileId ? "Catalog keeper" : "Not selected to keep"
                }).ToList();
                var legacyGroup = new DuplicateGroup((int)Math.Min(int.MaxValue, groupId), "Exact", 100, "Matching size and SHA-256", "Catalog SHA-256", 0, 0, 0, 0, legacyItems);
                var usedPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { keeper.PhysicalIdentityKey };
                foreach (ExactDuplicateMemberRecord member in members)
                {
                    DuplicateItem legacy = legacyItems.First(x => string.Equals(x.Path, member.FullPath, StringComparison.OrdinalIgnoreCase));
                    if (member.FileId == keeper.FileId || member.Availability != IndexedFileAvailability.Present || member.IsProtected ||
                       member.IsHardLinkAlias || !usedPhysical.Add(member.PhysicalIdentityKey) ||
                       !DuplicateCleanupPolicy.CanCleanupItem(legacyGroup, legacy)) continue;
                    LibraryFileHashFact? fact = _analysis.GetFileHashFact(member.FileId);
                    if (fact?.FullHash == null || !fact.FullHash.SequenceEqual(keeperFact.FullHash)) continue;
                    items.Add(new DuplicateCleanupPlanItemRecord(0, groupId, member.FileId, keeper.FileId, member.FullPath, member.PathKey,
                        member.SizeBytes, member.LastWriteUtc, member.VolumeId, member.FileIdentity, fact.FullHash,
                        DuplicateCleanupItemStatus.Planned, "", ""));
                }
            }
            if (items.Count == 0) throw new InvalidOperationException("No safe cleanup candidates remain after keeper, protection, availability, and hard-link checks.");
            long planId = _analysis.CreateCleanupPlan(action, quarantineRoot, items);
            return _analysis.GetCleanupPlan(planId) ?? throw new InvalidOperationException("The cleanup plan could not be reloaded.");
        }

        public async Task<DuplicateCleanupExecutionResult> ExecutePlanAsync(long planId, CancellationToken cancellationToken = default)
        {
            if (_isEncodingActive()) throw new InvalidOperationException("Stop the active encode before duplicate cleanup.");
            DuplicateCleanupPlanRecord plan = _analysis.GetCleanupPlan(planId) ?? throw new KeyNotFoundException($"Cleanup plan {planId} does not exist.");
            if (plan.Status != DuplicateCleanupStatus.Ready) throw new InvalidOperationException("Only a ready cleanup plan can be executed.");
            int succeeded = 0, excluded = 0, failed = 0;
            string fatal = "";
            foreach (IGrouping<long, DuplicateCleanupPlanItemRecord> groupItems in plan.Items.GroupBy(x => x.GroupId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long keeperId = groupItems.Select(x => x.KeeperFileId).Distinct().Single();
                ExactDuplicateMemberRecord? keeper = _analysis.GetDuplicateGroupMembers(groupItems.Key).FirstOrDefault(x => x.FileId == keeperId);
                byte[] expected = groupItems.First().FullHash;
                string? keeperError = await ValidateMemberAsync(keeper, expected, cancellationToken).ConfigureAwait(false);
                if (keeperError != null)
                {
                    foreach (DuplicateCleanupPlanItemRecord item in groupItems)
                    {
                        Exclude(plan, item, "Keeper validation failed: " + keeperError);
                        excluded++;
                    }
                    continue;
                }
                foreach (DuplicateCleanupPlanItemRecord item in groupItems)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    keeperError = await ValidateMemberAsync(keeper, expected, cancellationToken).ConfigureAwait(false);
                    if (keeperError != null)
                    {
                        Exclude(plan, item, "Keeper validation failed immediately before action: " + keeperError);
                        excluded++;
                        continue;
                    }
                    ExactDuplicateMemberRecord? current = _analysis.GetDuplicateGroupMembers(item.GroupId).FirstOrDefault(x => x.FileId == item.FileId);
                    string? validation = await ValidatePlanItemAsync(item, current, expected, cancellationToken).ConfigureAwait(false);
                    if (validation != null)
                    {
                        Exclude(plan, item, validation); excluded++; continue;
                    }
                    string destination = "";
                    try
                    {
                        destination = plan.Action == DuplicateCleanupAction.RecycleBin
                            ? Recycle(item.SourcePath)
                            : _actions.Quarantine(item.SourcePath, plan.QuarantineRoot, item.GroupId, item.FileId);
                        _analysis.UpdateCleanupPlanItem(plan.PlanId, item.FileId, DuplicateCleanupItemStatus.Succeeded, destination, "");
                        _analysis.AppendCleanupAudit(plan.PlanId, item.FileId, item.SourcePath, destination, plan.Action, DuplicateCleanupItemStatus.Succeeded, "Validated exact duplicate cleanup succeeded.");
                        succeeded++;
                    }
                    catch (Exception ex)
                    {
                        _analysis.UpdateCleanupPlanItem(plan.PlanId, item.FileId, DuplicateCleanupItemStatus.Failed, destination, ex.Message);
                        _analysis.AppendCleanupAudit(plan.PlanId, item.FileId, item.SourcePath, destination, plan.Action, DuplicateCleanupItemStatus.Failed, ex.Message);
                        failed++;
                    }
                }
            }
            DuplicateCleanupStatus final = failed > 0 ? DuplicateCleanupStatus.Failed : DuplicateCleanupStatus.Completed;
            _analysis.CompleteCleanupPlan(planId, final, fatal);
            return new DuplicateCleanupExecutionResult(planId, succeeded, excluded, failed, fatal);
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
        private void Exclude(DuplicateCleanupPlanRecord plan, DuplicateCleanupPlanItemRecord item, string message)
        {
            _analysis.UpdateCleanupPlanItem(plan.PlanId, item.FileId, DuplicateCleanupItemStatus.Excluded, "", message);
            _analysis.AppendCleanupAudit(plan.PlanId, item.FileId, item.SourcePath, "", plan.Action, DuplicateCleanupItemStatus.Excluded, message);
        }
    }
}
