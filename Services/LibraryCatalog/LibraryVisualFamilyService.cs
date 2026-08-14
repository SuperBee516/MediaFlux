using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class LibraryVisualFamilyService
{
    private const int FamilyBatchSize = 500;
    private readonly ILibraryVisualFamilyCatalog _families;
    private readonly LibraryVisualDuplicateCleanupService _visualCleanup;
    private DuplicateKeeperPreferences _preferences;

    public LibraryVisualFamilyService(ILibraryVisualFamilyCatalog families,
        LibraryVisualDuplicateCleanupService visualCleanup, DuplicateKeeperPreferences? preferences = null)
    {
        _families = families;
        _visualCleanup = visualCleanup;
        _preferences = preferences?.Clone() ?? new DuplicateKeeperPreferences();
        _preferences.Normalize();
    }

    public void UpdateKeeperPreferences(DuplicateKeeperPreferences preferences)
    {
        _preferences = preferences.Clone();
        _preferences.Normalize();
    }

    public LibraryKeeperExplanation Explain(long familyId)
    {
        VisualFamilyRecord? family = _families.GetVisualFamily(familyId);
        IReadOnlyList<VisualFamilyMemberRecord> members = _families.GetVisualFamilyMembers(familyId);
        return new LibraryKeeperExplanationService().Explain(members.Select(ToVisualMember).ToArray(), _preferences,
            family?.MinimumConfidence ?? 100);
    }

    public long? RefreshSuggestedKeeper(long familyId, double minimumAutomationMargin)
    {
        VisualFamilyRecord? family = _families.GetVisualFamily(familyId);
        if (family == null) return null;
        if (family.ManualKeeperFileId.HasValue) return family.ManualKeeperFileId;
        IReadOnlyList<VisualFamilyMemberRecord> members = _families.GetVisualFamilyMembers(familyId);
        DuplicateKeeperEvaluation automation = DuplicateKeeperScoringService.EvaluateAutomation(
            members.Select(member => LibraryVisualDuplicateCleanupService.ToLegacyItem(ToVisualMember(member))).ToArray(),
            _preferences, DuplicateKeeperScoringContext.Visual, family.MinimumConfidence);
        long? keeperId = automation.RequiresReview || automation.Keeper == null || automation.Margin < minimumAutomationMargin
            ? null
            : members.FirstOrDefault(x => string.Equals(x.FullPath, automation.Keeper.Path, StringComparison.OrdinalIgnoreCase))?.FileId;
        _families.SetVisualFamilySuggestedKeeper(familyId, keeperId);
        return keeperId;
    }

    public VisualFamilyCleanupProposal BuildCleanupProposal(long familyId)
    {
        VisualFamilyRecord family = _families.GetVisualFamily(familyId)
            ?? throw new KeyNotFoundException($"Visual family {familyId} does not exist.");
        IReadOnlyList<VisualFamilyMemberRecord> members = _families.GetVisualFamilyMembers(familyId);
        VisualFamilyMemberRecord fallback = members.FirstOrDefault()
            ?? new VisualFamilyMemberRecord(
                familyId, 0, "", "", 0, DateTime.MinValue,
                IndexedFileAvailability.Missing, "", null, null, null, null,
                false, false, false, false, "", 0);
        if (family.Eligibility != LibraryMatchEligibilityState.Active)
            return new VisualFamilyCleanupProposal(
                family, fallback, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0,
                "Stale family state: " + (string.IsNullOrWhiteSpace(family.EligibilityReason)
                    ? "the family is no longer active."
                    : family.EligibilityReason));
        if (!family.Reviewed)
            return new VisualFamilyCleanupProposal(
                family, fallback, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0, "The family is not reviewed.");
        if (family.Ignored)
            return new VisualFamilyCleanupProposal(
                family, fallback, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0, "The family is ignored.");
        if (!family.ManualKeeperFileId.HasValue)
            return new VisualFamilyCleanupProposal(
                family, fallback, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0,
                "No valid persisted manual keeper is selected.");
        VisualFamilyMemberRecord? keeper = family.ManualKeeperFileId.HasValue
            ? members.FirstOrDefault(x => x.FileId == family.ManualKeeperFileId.Value)
            : null;
        if (keeper == null)
            return new VisualFamilyCleanupProposal(
                family, fallback, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0,
                "The persisted keeper is no longer a member of the family.");
        if (keeper.Availability != IndexedFileAvailability.Present || !File.Exists(keeper.FullPath))
            return new VisualFamilyCleanupProposal(
                family, keeper, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0,
                "The persisted keeper is missing or unavailable.");
        if (!SnapshotMatchesDisk(keeper))
            return new VisualFamilyCleanupProposal(
                family, keeper, Array.Empty<VisualCleanupProposalItem>(),
                Math.Max(0, members.Count - 1), 0,
                "The persisted keeper changed after it was cataloged.");

        IReadOnlyList<VisualFamilyEdgeRecord> edges = _families.GetVisualFamilyEdges(familyId);
        var proposals = new List<VisualCleanupProposalItem>();
        int excluded = 0;
        bool sawProtected = false;
        bool sawMissing = false;
        bool sawStale = false;
        bool sawActivePlan = false;
        foreach (VisualFamilyMemberRecord candidate in members.Where(x => x.FileId != keeper.FileId))
        {
            if (candidate.IsProtected)
            {
                sawProtected = true;
                excluded++;
                continue;
            }
            if (candidate.Availability != IndexedFileAvailability.Present ||
                !File.Exists(candidate.FullPath))
            {
                sawMissing = true;
                excluded++;
                continue;
            }
            if (!SnapshotMatchesDisk(candidate))
            {
                sawMissing = true;
                excluded++;
                continue;
            }
            if (_families.IsFileInActiveCleanupPlan(candidate.FileId))
            {
                sawActivePlan = true;
                excluded++;
                continue;
            }
            VisualFamilyEdgeRecord? edge = edges.FirstOrDefault(x =>
                (x.LeftFileId == keeper.FileId && x.RightFileId == candidate.FileId) ||
                (x.RightFileId == keeper.FileId && x.LeftFileId == candidate.FileId));
            if (edge == null)
            {
                sawStale = true;
                excluded++;
                continue;
            }
            VisualCleanupProposal pair = _visualCleanup.BuildProposal(includeUnreviewed: true,
                minimumConfidence: edge.Confidence, groupIds: new[] { edge.VisualGroupId }, maximumItems: 1, includeFamilyPairs: true);
            VisualCleanupProposalItem? item = pair.Items.FirstOrDefault();
            if (item == null)
            {
                sawStale = true;
                excluded++;
                continue;
            }
            if (item.Keeper.FileId != keeper.FileId)
                item = item with { Keeper = item.Candidate, Candidate = item.Keeper, KeeperReason = "Family keeper with direct pair evidence" };
            if (item.Keeper.FileId != keeper.FileId || item.Candidate.FileId != candidate.FileId)
            {
                sawStale = true;
                excluded++;
                continue;
            }
            proposals.Add(item with { FamilyId = familyId });
        }
        VisualCleanupProposalItem[] unique = proposals.GroupBy(x => x.Candidate.FileId).Select(x => x.First()).ToArray();
        excluded += proposals.Count - unique.Length;
        string reason = unique.Length > 0
            ? ""
            : sawMissing
                ? "One or more files are missing, unavailable, or changed."
                : sawProtected
                    ? "All remaining cleanup candidates are protected."
                    : sawActivePlan
                        ? "All remaining candidates are already in an active cleanup plan."
                        : sawStale
                            ? "The persisted visual evidence is stale or no longer supports cleanup."
                            : "Nothing remains to clean.";
        return new VisualFamilyCleanupProposal(
            family, keeper, unique, excluded,
            unique.Sum(x => x.Candidate.SizeBytes), reason);
    }

    public VisualFamilyBatchCleanupPlanResult CreateBatchCleanupPlan(
        IReadOnlyCollection<long>? selectedFamilyIds,
        bool allReviewedFamilies,
        DuplicateCleanupAction action,
        string quarantineRoot = "",
        CancellationToken cancellationToken = default)
    {
        if (!allReviewedFamilies && (selectedFamilyIds == null || selectedFamilyIds.Count == 0))
            throw new ArgumentException("Select at least one visual family.", nameof(selectedFamilyIds));
        long planId = _visualCleanup.BeginPlan(
            action, quarantineRoot, allowUnreviewed: true, minimumConfidence: 0);
        long requested = 0;
        long eligible = 0;
        var reasons = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            void PlanFamily(long familyId)
            {
                cancellationToken.ThrowIfCancellationRequested();
                requested++;
                VisualFamilyCleanupProposal proposal;
                try
                {
                    proposal = BuildCleanupProposal(familyId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AddReason("The family could not be reloaded: " + ex.Message);
                    return;
                }
                if (proposal.Items.Count == 0)
                {
                    AddReason(string.IsNullOrWhiteSpace(proposal.ExclusionReason)
                        ? "Nothing remains to clean."
                        : proposal.ExclusionReason);
                    return;
                }
                int appended = _visualCleanup.AppendPlanItems(planId, proposal.Items);
                if (appended == 0)
                {
                    AddReason("The family files changed while the persisted plan was being created.");
                    return;
                }
                eligible++;
            }

            void AddReason(string reason)
            {
                reasons.TryGetValue(reason, out long count);
                reasons[reason] = count + 1;
            }

            if (allReviewedFamilies)
            {
                long afterFamilyId = 0;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<long> familyIds =
                        _families.GetReviewedVisualFamilyIds(afterFamilyId, FamilyBatchSize);
                    if (familyIds.Count == 0) break;
                    foreach (long familyId in familyIds) PlanFamily(familyId);
                    afterFamilyId = familyIds[^1];
                    if (familyIds.Count < FamilyBatchSize) break;
                }
            }
            else
            {
                foreach (long familyId in selectedFamilyIds!.Distinct().Order())
                    PlanFamily(familyId);
            }

            VisualCleanupPlanSummary draft = _visualCleanup.GetPlanSummary(planId);
            VisualCleanupPlanSummary summary;
            if (draft.TotalItems == 0)
            {
                _visualCleanup.FailPlan(
                    planId, "No eligible reviewed visual family cleanup candidates remain.");
                summary = _visualCleanup.GetPlanSummary(planId);
            }
            else
            {
                summary = _visualCleanup.ReadyPlan(planId);
            }
            return new VisualFamilyBatchCleanupPlanResult(
                planId, summary, requested, eligible, requested - eligible, reasons);
        }
        catch (OperationCanceledException)
        {
            _visualCleanup.FailPlan(
                planId, "Visual family cleanup planning was canceled before the plan became ready.");
            throw;
        }
        catch
        {
            _visualCleanup.FailPlan(
                planId, "Visual family cleanup planning failed before the plan became ready.");
            throw;
        }
    }

    private static VisualSimilarityMemberRecord ToVisualMember(VisualFamilyMemberRecord member) => new(
        member.FamilyId, member.FileId, member.FullPath, member.LocationPath, member.SizeBytes, member.LastWriteUtc,
        member.Availability, member.VideoCodec, member.Width, member.Height, member.TotalBitRate, member.DurationSeconds,
        member.IsProtected, member.IsSuggestedKeeper, member.IsManualKeeper, member.IsHdr, member.AudioSummary, member.FrameRate);

    private static bool SnapshotMatchesDisk(VisualFamilyMemberRecord member)
    {
        var file = new FileInfo(member.FullPath);
        return file.Exists && file.Length == member.SizeBytes &&
               file.LastWriteTimeUtc.Ticks == member.LastWriteUtc.Ticks;
    }
}
