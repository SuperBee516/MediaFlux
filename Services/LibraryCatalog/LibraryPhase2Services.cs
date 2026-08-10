using MediaFlux.Models;
using MediaFlux.Services;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class LibraryKeeperExplanationService
{
    public LibraryKeeperExplanation Explain(IReadOnlyList<VisualSimilarityMemberRecord> members,
        DuplicateKeeperPreferences preferences)
    {
        DuplicateItem[] items = members.Select(LibraryVisualDuplicateCleanupService.ToLegacyItem).ToArray();
        DuplicateKeeperEvaluation evaluation = DuplicateKeeperScoringService.Evaluate(items, preferences, DuplicateKeeperScoringContext.Visual);
        DuplicateKeeperEvaluation automation = DuplicateKeeperScoringService.EvaluateAutomation(items, preferences, DuplicateKeeperScoringContext.Visual);
        long? keeperId = evaluation.Keeper == null ? null : members.FirstOrDefault(x =>
            string.Equals(x.FullPath, evaluation.Keeper.Path, StringComparison.OrdinalIgnoreCase))?.FileId;
        VisualSimilarityMemberRecord? keeper = keeperId.HasValue ? members.FirstOrDefault(x => x.FileId == keeperId.Value) : null;
        var factors = new List<string>();
        if (keeper != null)
        {
            if (keeper.Width.HasValue && keeper.Height.HasValue)
                factors.Add($"{keeper.Width}×{keeper.Height} resolution");
            if (!string.IsNullOrWhiteSpace(keeper.VideoCodec)) factors.Add($"{keeper.VideoCodec.ToUpperInvariant()} codec");
            if (keeper.TotalBitRate.HasValue) factors.Add($"{keeper.TotalBitRate.Value / 1_000_000d:0.##} Mbps");
            if (keeper.IsHdr) factors.Add("HDR");
            if (!string.IsNullOrWhiteSpace(keeper.AudioSummary)) factors.Add(keeper.AudioSummary);
            factors.Add($"{keeper.SizeBytes / 1024d / 1024d:0.#} MiB");
        }
        string summary = evaluation.Keeper == null
            ? evaluation.Explanation
            : $"{evaluation.Explanation} Automation margin: {automation.Margin:0.0}.";
        return new LibraryKeeperExplanation(keeperId,
            evaluation.Keeper != null && evaluation.Scores.TryGetValue(evaluation.Keeper.Path, out double score) ? score : 0,
            automation.Margin, automation.RequiresReview, summary, factors);
    }
}

public sealed class LibraryMassReviewService
{
    private readonly ILibraryVisualCatalog _visual;
    private readonly LibraryMatchEligibilityService _eligibility;
    private DuplicateKeeperPreferences _preferences;

    public LibraryMassReviewService(ILibraryVisualCatalog visual, LibraryMatchEligibilityService eligibility,
        DuplicateKeeperPreferences preferences)
    {
        _visual = visual; _eligibility = eligibility; _preferences = preferences.Clone(); _preferences.Normalize();
    }

    public void UpdatePreferences(DuplicateKeeperPreferences preferences)
    {
        _preferences = preferences.Clone();
        _preferences.Normalize();
    }

    public LibraryMassReviewPreview CreatePreview(LibraryVisualReviewAutomationOptions sourceOptions)
    {
        LibraryVisualReviewAutomationOptions options = sourceOptions.Normalize();
        string batchId = Guid.NewGuid().ToString("N");
        var eligible = new List<LibraryMassReviewPreviewItem>();
        var excluded = new List<LibraryMassReviewPreviewItem>();
        int offset = 0;
        while (eligible.Count < options.MaximumMassReviewMatches)
        {
            VisualSimilarityGroupPage page = _visual.QueryVisualGroups(new VisualGroupQuery(
                Reviewed: false, Ignored: false, NotMatch: false, MinimumConfidence: options.MinimumVisualConfidence,
                SortColumn: "confidence", Descending: true, Offset: offset, Limit: 200));
            if (page.Groups.Count == 0) break;
            foreach (VisualSimilarityGroupRecord group in page.Groups)
            {
                LibraryMassReviewPreviewItem item = Assess(group, options);
                if (string.IsNullOrEmpty(item.ExclusionReason)) eligible.Add(item);
                else excluded.Add(item);
                if (eligible.Count >= options.MaximumMassReviewMatches) break;
            }
            offset += page.Groups.Count;
            if (offset >= page.TotalCount) break;
        }
        return new LibraryMassReviewPreview(batchId, options, eligible, excluded);
    }

    public LibraryMassReviewApplyResult Apply(LibraryMassReviewPreview preview, IEnumerable<long>? includedGroupIds = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        HashSet<long> included = includedGroupIds?.ToHashSet() ?? preview.EligibleItems.Select(x => x.GroupId).ToHashSet();
        int applied = 0, excluded = 0;
        var messages = new List<string>();
        foreach (LibraryMassReviewPreviewItem previewItem in preview.EligibleItems.Where(x => included.Contains(x.GroupId)))
        {
            VisualSimilarityGroupRecord? group = _visual.GetVisualGroup(previewItem.GroupId);
            if (group == null || group.Reviewed || group.Ignored || group.NotMatch)
            {
                excluded++; messages.Add($"{previewItem.GroupKey}: changed since preview."); continue;
            }
            LibraryMassReviewPreviewItem revalidated = Assess(group, preview.Options);
            if (!string.IsNullOrEmpty(revalidated.ExclusionReason) || revalidated.KeeperFileId != previewItem.KeeperFileId)
            {
                excluded++; messages.Add($"{previewItem.GroupKey}: {revalidated.ExclusionReason ?? "keeper recommendation changed since preview."}"); continue;
            }
            _visual.SaveVisualDecision(new VisualGroupDecision(group.GroupId, revalidated.KeeperFileId, true, false, false,
                preview.BatchId, "mass-review"));
            applied++;
        }
        return new LibraryMassReviewApplyResult(preview.BatchId, applied, excluded, messages);
    }

    private LibraryMassReviewPreviewItem Assess(VisualSimilarityGroupRecord group, LibraryVisualReviewAutomationOptions options)
    {
        IReadOnlyList<VisualSimilarityMemberRecord> members = _visual.GetVisualGroupMembers(group.GroupId);
        string exclusion = "";
        if (group.ManualKeeperFileId.HasValue) exclusion = "A manual keeper decision requires individual review.";
        else if (!_eligibility.EvaluateVisualGroup(group.GroupId).IsActive) exclusion = "Match is no longer eligible.";
        else if (members.Count != 2) exclusion = "Only pair matches are eligible for this phase.";
        else if (members.Any(x => x.IsProtected)) exclusion = "A protected member requires manual review.";
        else if (members.Any(x => x.Availability != IndexedFileAvailability.Present)) exclusion = "A member is unavailable.";
        DuplicateKeeperEvaluation automation = DuplicateKeeperScoringService.EvaluateAutomation(
            members.Select(LibraryVisualDuplicateCleanupService.ToLegacyItem).ToArray(), _preferences, DuplicateKeeperScoringContext.Visual);
        if (string.IsNullOrEmpty(exclusion) && (automation.RequiresReview || automation.Keeper == null || automation.Margin < options.MinimumAutomationMargin))
            exclusion = $"Automation margin {automation.Margin:0.0} is below {options.MinimumAutomationMargin:0.0}.";
        VisualSimilarityMemberRecord? keeper = automation.Keeper == null ? null : members.FirstOrDefault(x =>
            string.Equals(x.FullPath, automation.Keeper.Path, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(exclusion) && keeper == null) exclusion = "No unambiguous keeper recommendation is available.";
        double score = keeper != null && automation.Scores.TryGetValue(keeper.FullPath, out double value) ? value : 0;
        return new LibraryMassReviewPreviewItem(group.GroupId, group.GroupKey, group.ConfidenceScore, keeper?.FileId ?? 0,
            keeper?.FullPath ?? "", score, automation.Margin, automation.Explanation, members, true, exclusion);
    }
}

public sealed class LibraryRecommendationService
{
    private readonly ILibraryAnalysisCatalog _analysis;
    private readonly LibraryDuplicateCleanupService _exactCleanup;
    private readonly LibraryVisualDuplicateCleanupService _visualCleanup;
    private readonly ILibraryPhase2Catalog _catalog;
    private ILibraryVisualFamilyCatalog? _families;
    private LibraryVisualFamilyService? _familyService;

    public LibraryRecommendationService(ILibraryAnalysisCatalog analysis, LibraryDuplicateCleanupService exactCleanup,
        LibraryVisualDuplicateCleanupService visualCleanup, ILibraryPhase2Catalog catalog)
    { _analysis = analysis; _exactCleanup = exactCleanup; _visualCleanup = visualCleanup; _catalog = catalog; }

    public void AttachFamilies(ILibraryVisualFamilyCatalog families, LibraryVisualFamilyService service)
    {
        _families = families;
        _familyService = service;
    }

    public LibraryCleanupRecommendationDashboard GetCleanupDashboard(double highConfidence = 95)
    {
        ExactCleanupCandidate[] exact = _exactCleanup.GetEligibleCandidates().ToArray();
        HashSet<long> exactCandidates = exact.Select(x => x.FileId).ToHashSet();
        long exactBytes = exact.Sum(x => x.SizeBytes);
        VisualCleanupProposal reviewed = _visualCleanup.BuildProposal();
        VisualCleanupProposal potential = _visualCleanup.BuildProposal(includeUnreviewed: true, minimumConfidence: highConfidence);
        VisualCleanupProposalItem[] reviewedItems = reviewed.Items.Where(x => !exactCandidates.Contains(x.Candidate.FileId)).ToArray();
        VisualCleanupProposalItem[] unreviewed = potential.Items.Where(x => !x.Group.Reviewed && !exactCandidates.Contains(x.Candidate.FileId)).ToArray();
        var categories = new List<LibraryCleanupRecommendationCategory>
        {
            new("Exact duplicates", "Safe/current SHA-256 evidence", exactCandidates.Count, exactBytes, "Eligible exact duplicate candidates; no cleanup is run.")
        };
        if (_families != null && _familyService != null)
        {
            VisualFamilyRecord[] reviewedFamilies = _families.QueryVisualFamilies(new VisualFamilyQuery(Reviewed: true, Ignored: false, Limit: 500)).Families.ToArray();
            VisualCleanupProposalItem[] familyItems = reviewedFamilies.SelectMany(family => _familyService.BuildCleanupProposal(family.FamilyId).Items)
                .Where(x => !exactCandidates.Contains(x.Candidate.FileId)).GroupBy(x => x.Candidate.FileId).Select(x => x.First()).ToArray();
            categories.Add(new("Reviewed visual families", "Reviewed family recommendation", familyItems.Length,
                familyItems.Sum(x => x.Candidate.SizeBytes), "Family candidates with direct keeper-to-candidate evidence and current cleanup eligibility."));
        }
        categories.Add(new("Reviewed visual duplicates", "Reviewed recommendation", reviewedItems.Length,
            reviewedItems.Sum(x => x.Candidate.SizeBytes), "Reviewed visual matches that remain eligible for separate cleanup preview."));
        categories.Add(new("High-confidence visual candidates", "Review required", unreviewed.Length,
            unreviewed.Sum(x => x.Candidate.SizeBytes), "Unreviewed visual suggestions. They are estimates, not safe cleanup."));
        return new LibraryCleanupRecommendationDashboard(categories, DateTime.UtcNow);
    }

    public IReadOnlyList<LibraryStorageOptimizationCandidate> GetStorageOptimizationCandidates(int limit = 500) =>
        _catalog.QueryStorageOptimizationCandidates(limit);
}
