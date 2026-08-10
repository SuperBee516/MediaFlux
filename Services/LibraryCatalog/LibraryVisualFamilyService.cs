using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class LibraryVisualFamilyService
{
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
        IReadOnlyList<VisualFamilyMemberRecord> members = _families.GetVisualFamilyMembers(familyId);
        return new LibraryKeeperExplanationService().Explain(members.Select(ToVisualMember).ToArray(), _preferences);
    }

    public long? RefreshSuggestedKeeper(long familyId, double minimumAutomationMargin)
    {
        VisualFamilyRecord? family = _families.GetVisualFamily(familyId);
        if (family == null) return null;
        if (family.ManualKeeperFileId.HasValue) return family.ManualKeeperFileId;
        IReadOnlyList<VisualFamilyMemberRecord> members = _families.GetVisualFamilyMembers(familyId);
        DuplicateKeeperEvaluation automation = DuplicateKeeperScoringService.EvaluateAutomation(
            members.Select(member => LibraryVisualDuplicateCleanupService.ToLegacyItem(ToVisualMember(member))).ToArray(),
            _preferences, DuplicateKeeperScoringContext.Visual);
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
        VisualFamilyMemberRecord? keeper = family.ManualKeeperFileId.HasValue
            ? members.FirstOrDefault(x => x.FileId == family.ManualKeeperFileId.Value)
            : null;
        if (!family.Reviewed || family.Ignored || keeper == null)
            return new VisualFamilyCleanupProposal(family, keeper ?? members.First(), Array.Empty<VisualCleanupProposalItem>(), members.Count - 1, 0);
        if (keeper.Availability != IndexedFileAvailability.Present || !File.Exists(keeper.FullPath))
            return new VisualFamilyCleanupProposal(family, keeper, Array.Empty<VisualCleanupProposalItem>(), members.Count - 1, 0);

        IReadOnlyList<VisualFamilyEdgeRecord> edges = _families.GetVisualFamilyEdges(familyId);
        var proposals = new List<VisualCleanupProposalItem>();
        int excluded = 0;
        foreach (VisualFamilyMemberRecord candidate in members.Where(x => x.FileId != keeper.FileId))
        {
            if (candidate.IsProtected || candidate.Availability != IndexedFileAvailability.Present ||
                _families.IsFileInActiveCleanupPlan(candidate.FileId))
            {
                excluded++;
                continue;
            }
            VisualFamilyEdgeRecord? edge = edges.FirstOrDefault(x =>
                (x.LeftFileId == keeper.FileId && x.RightFileId == candidate.FileId) ||
                (x.RightFileId == keeper.FileId && x.LeftFileId == candidate.FileId));
            if (edge == null)
            {
                excluded++;
                continue;
            }
            VisualCleanupProposal pair = _visualCleanup.BuildProposal(includeUnreviewed: true,
                minimumConfidence: edge.Confidence, groupIds: new[] { edge.VisualGroupId }, maximumItems: 1, includeFamilyPairs: true);
            VisualCleanupProposalItem? item = pair.Items.FirstOrDefault();
            if (item == null)
            {
                excluded++;
                continue;
            }
            if (item.Keeper.FileId != keeper.FileId)
                item = item with { Keeper = item.Candidate, Candidate = item.Keeper, KeeperReason = "Family keeper with direct pair evidence" };
            if (item.Keeper.FileId != keeper.FileId || item.Candidate.FileId != candidate.FileId)
            {
                excluded++;
                continue;
            }
            proposals.Add(item);
        }
        VisualCleanupProposalItem[] unique = proposals.GroupBy(x => x.Candidate.FileId).Select(x => x.First()).ToArray();
        excluded += proposals.Count - unique.Length;
        return new VisualFamilyCleanupProposal(family, keeper, unique, excluded, unique.Sum(x => x.Candidate.SizeBytes));
    }

    private static VisualSimilarityMemberRecord ToVisualMember(VisualFamilyMemberRecord member) => new(
        member.FamilyId, member.FileId, member.FullPath, member.LocationPath, member.SizeBytes, member.LastWriteUtc,
        member.Availability, member.VideoCodec, member.Width, member.Height, member.TotalBitRate, member.DurationSeconds,
        member.IsProtected, member.IsSuggestedKeeper, member.IsManualKeeper, member.IsHdr, member.AudioSummary);
}
