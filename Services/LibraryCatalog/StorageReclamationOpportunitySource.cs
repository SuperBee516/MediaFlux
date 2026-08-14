using MediaFlux.Models;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class StorageReclamationOpportunitySource
{
    private readonly ILibraryCatalog _inventory;
    private readonly ILibraryAnalysisCatalog _analysis;
    private readonly ILibraryVisualCatalog _visual;
    private readonly ILibraryVisualFamilyCatalog _families;
    private readonly LibraryDuplicateCleanupService _exact;
    private readonly LibraryVisualDuplicateCleanupService _visualCleanup;
    private readonly LibraryVisualFamilyService _familyService;
    private readonly LibraryPolicyEvaluationService _policies;

    public StorageReclamationOpportunitySource(
        ILibraryCatalog inventory,
        ILibraryAnalysisCatalog analysis,
        ILibraryVisualCatalog visual,
        ILibraryVisualFamilyCatalog families,
        LibraryDuplicateCleanupService exact,
        LibraryVisualDuplicateCleanupService visualCleanup,
        LibraryVisualFamilyService familyService,
        LibraryPolicyEvaluationService policies)
    {
        _inventory = inventory; _analysis = analysis; _visual = visual; _families = families;
        _exact = exact; _visualCleanup = visualCleanup; _familyService = familyService; _policies = policies;
    }

    public IReadOnlyList<StorageReclamationOpportunity> Collect(
        StorageReclamationStrategy strategy,
        LibraryPolicyDefinition? policy,
        LibraryPolicyCapabilitySnapshot capabilities,
        CancellationToken cancellationToken = default,
        int maximumPerSource = 50_000,
        EncodingRuntimeEstimatorService? runtimeEstimator = null)
    {
        maximumPerSource = Math.Clamp(maximumPerSource, 1, 50_000);
        var opportunities = new List<StorageReclamationOpportunity>();
        var exactCandidates = _exact.GetEligibleCandidates(maximumPerSource);
        foreach (IGrouping<long, ExactCleanupCandidate> group in exactCandidates.GroupBy(item => item.GroupId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ExactDuplicateMemberRecord> members = _analysis.GetDuplicateGroupMembers(group.Key);
            foreach (ExactCleanupCandidate candidate in group)
            {
                ExactDuplicateMemberRecord? file = members.FirstOrDefault(item => item.FileId == candidate.FileId);
                ExactDuplicateMemberRecord? keeper = members.FirstOrDefault(item => item.FileId == candidate.KeeperFileId);
                if (file == null || keeper == null) continue;
                opportunities.Add(new StorageReclamationOpportunity
                {
                    FileId = file.FileId, SourcePath = file.FullPath, LocationPath = file.LocationPath,
                    PhysicalIdentityKey = file.PhysicalIdentityKey, ActionCategory = StorageReclamationActionCategory.ExactDuplicateCleanup,
                    SourceSubsystem = StorageReclamationSourceSubsystem.ExactDuplicates, ExpectedReclaimBytes = file.SizeBytes,
                    CurrentSizeBytes = file.SizeBytes, EstimatedPostOptimizationBytes = 0, SavingsAreEstimated = false,
                    Confidence = LibraryPolicyConfidence.High, SafetyState = StorageReclamationSafetyState.Ready,
                    Reason = "Current SHA-256 exact duplicate candidate; the validated keeper is retained.",
                    KeeperFileId = keeper.FileId, KeeperPath = keeper.FullPath, ExactGroupId = group.Key
                });
            }
        }

        long afterFamilyId = 0;
        while (opportunities.Count < maximumPerSource * 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<long> familyIds = _families.GetReviewedVisualFamilyIds(afterFamilyId, 500);
            foreach (long familyId in familyIds)
            {
                VisualFamilyCleanupProposal proposal = _familyService.BuildCleanupProposal(familyId);
                foreach (VisualCleanupProposalItem item in proposal.Items)
                    opportunities.Add(Visual(item, StorageReclamationActionCategory.ReviewedVisualFamilyCleanup,
                        StorageReclamationSourceSubsystem.VisualFamilies, familyId));
            }
            if (familyIds.Count < 500) break;
            afterFamilyId = familyIds[^1];
        }

        VisualCleanupProposal reviewed = _visualCleanup.BuildProposal(maximumItems: Math.Min(maximumPerSource, 10_000));
        opportunities.AddRange(reviewed.Items.Select(item => Visual(item,
            StorageReclamationActionCategory.ReviewedVisualDuplicateCleanup, StorageReclamationSourceSubsystem.VisualPairs, null)));

        if (strategy == StorageReclamationStrategy.MaximumPotential)
        {
            VisualCleanupProposal potential = _visualCleanup.BuildProposal(includeUnreviewed: true, minimumConfidence: 95,
                maximumItems: Math.Min(maximumPerSource, 10_000));
            opportunities.AddRange(potential.Items.Where(item => !item.Group.Reviewed).Select(item => Visual(item,
                StorageReclamationActionCategory.ReviewRequired, StorageReclamationSourceSubsystem.VisualPairs, null,
                StorageReclamationSafetyState.ReviewRequired)));
        }

        if (policy != null && strategy != StorageReclamationStrategy.AvoidReencoding)
        {
            bool includeReview = strategy == StorageReclamationStrategy.MaximumPotential;
            foreach (LibraryPolicyEvaluationResult result in _policies.EvaluateForPlanning(policy, capabilities, includeReview,
                         maximumPerSource, cancellationToken))
            {
                if (result.SuggestedAction == LibraryPolicySuggestedAction.RemuxOnly || result.ProjectedReclaimableBytes is not > 0) continue;
                bool ready = result.State == LibraryPolicyComplianceState.OptimizationCandidate &&
                             result.SuggestedAction == LibraryPolicySuggestedAction.Reencode &&
                             result.Confidence >= LibraryPolicyConfidence.Medium;
                StorageReclamationSafetyState safety = ready ? StorageReclamationSafetyState.Ready : StorageReclamationSafetyState.ReviewRequired;
                EncodingRuntimeEstimate estimate = runtimeEstimator?.Estimate(result) ?? new EncodingRuntimeEstimate();
                double? efficiency = estimate.EstimatedProcessingSeconds is > 0
                    ? result.ProjectedReclaimableBytes.Value * 3600d / estimate.EstimatedProcessingSeconds.Value : null;
                opportunities.Add(new StorageReclamationOpportunity
                {
                    FileId = result.FileId, SourcePath = result.FullPath, LocationPath = result.LocationPath,
                    PhysicalIdentityKey = result.PhysicalIdentityKey, ActionCategory = ready
                        ? StorageReclamationActionCategory.PolicyReencode : StorageReclamationActionCategory.ReviewRequired,
                    SourceSubsystem = StorageReclamationSourceSubsystem.LibraryPolicy,
                    ExpectedReclaimBytes = result.ProjectedReclaimableBytes.Value, Confidence = result.Confidence,
                    CurrentSizeBytes = result.OriginalSizeBytes, EstimatedPostOptimizationBytes = result.ProjectedOutputBytes,
                    SavingsAreEstimated = true,
                    SafetyState = safety, Reason = string.Join(" ", result.Reasons.Concat(result.ReviewReasons)),
                    PolicyId = result.PolicyId, PolicyName = result.PolicyName,
                    PolicyQueueIntent = ready ? ToQueueIntent(result, policy, estimate, efficiency) : null,
                    BlockingReason = ready ? "" : string.Join(" ", result.ReviewReasons),
                    EstimatedProcessingHours = estimate.EstimatedProcessingSeconds / 3600d,
                    EstimatedSpeedX = estimate.EstimatedSpeedX,
                    EstimatedFastProcessingHours = estimate.FastProcessingSeconds / 3600d,
                    EstimatedSlowProcessingHours = estimate.SlowProcessingSeconds / 3600d,
                    SavingsPerComputeHourGb = efficiency / (1024d * 1024 * 1024),
                    RuntimeConfidence = estimate.Confidence,
                    RuntimeSampleCount = estimate.SampleCount,
                    RuntimeExplanation = estimate.CohortExplanation
                });
            }
        }
        return opportunities;
    }

    private StorageReclamationOpportunity Visual(VisualCleanupProposalItem item,
        StorageReclamationActionCategory category, StorageReclamationSourceSubsystem source, long? familyId,
        StorageReclamationSafetyState safety = StorageReclamationSafetyState.Ready)
    {
        IndexedFileRecord? file = _inventory.GetFileByPath(item.Candidate.FullPath);
        string physical = file is { VolumeId.Length: > 0, FileIdentity.Length: > 0 }
            ? $"{file.VolumeId}|{file.FileIdentity}" : item.Candidate.FullPath;
        return new StorageReclamationOpportunity
        {
            FileId = item.Candidate.FileId, SourcePath = item.Candidate.FullPath, LocationPath = item.Candidate.LocationPath,
            PhysicalIdentityKey = physical, ActionCategory = category, SourceSubsystem = source,
            ExpectedReclaimBytes = item.Candidate.SizeBytes, CurrentSizeBytes = item.Candidate.SizeBytes,
            EstimatedPostOptimizationBytes = 0, SavingsAreEstimated = false,
            Confidence = item.HasExactEvidence
                ? LibraryPolicyConfidence.High : item.Group.ConfidenceScore >= 95 ? LibraryPolicyConfidence.High : LibraryPolicyConfidence.Medium,
            SafetyState = safety, Reason = item.HasExactEvidence ? "Reviewed visual candidate with exact SHA-256 evidence." : item.KeeperReason,
            KeeperFileId = item.Keeper.FileId, KeeperPath = item.Keeper.FullPath, VisualGroupId = item.Group.GroupId,
            VisualFamilyId = familyId
        };
    }

    private static LibraryPolicyQueueItem ToQueueIntent(LibraryPolicyEvaluationResult result, LibraryPolicyDefinition policy,
        EncodingRuntimeEstimate estimate, double? efficiencyBytesPerHour) => new(
        result.FullPath, result.PolicyId, result.PolicyName, result.ProposedCodec, result.EncoderId, result.EncoderPreset,
        result.EncodingPresetName, result.QualityValue, result.PreferredBitDepth, result.PreserveSourceResolution,
        result.MaximumOutputHeight, result.PreserveHdr, result.TargetContainer, result.ProjectedOutputBytes, result.Confidence,
        estimate.EstimatedProcessingSeconds, efficiencyBytesPerHour, estimate.Confidence, estimate.CohortExplanation);
}
