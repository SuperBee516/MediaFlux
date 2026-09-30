using MediaFlux.Models;

namespace MediaFlux.Services;

/// <summary>Validation for the schema-1, 24-slot Gate 3 freeze. Does not probe media or read forecasts.</summary>
public static class PredictionShadowExperimentFreezeValidation
{
    public static void Validate(PredictionShadowExperimentFreeze freeze)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        Require(freeze.SchemaVersion == 1, "Unsupported freeze schema version.");
        Text(freeze.ExperimentId, "ExperimentId");
        Text(freeze.ProtocolRevision, "ProtocolRevision");
        Require(freeze.FrozenUtc.Kind == DateTimeKind.Utc && freeze.FrozenUtc != default, "FrozenUtc must be a populated UTC timestamp.");
        Require(Version.TryParse(freeze.MediaFluxVersion, out _), "Invalid MediaFluxVersion.");
        Require(IsHex(freeze.GitCommit, 40), "GitCommit must be a full hexadecimal commit ID.");
        ValidateSettings(freeze.Settings);
        ValidateComparators(freeze.Comparators);
        Require(freeze.Strata is { Count: 3 }, "Exactly three strata are required.");
        Require(freeze.Targets is { Count: 24 }, "Exactly 24 Target slots are required.");
        Require(freeze.Reserves is { Count: 6 }, "Exactly six reserves are required.");
        Require(freeze.Exclusions is not null, "Exclusions must be supplied, even when empty.");

        var strata = new HashSet<PredictionShadowExperimentStratum>();
        foreach (var stratum in freeze.Strata)
        {
            Require(stratum is not null && Enum.IsDefined(stratum.Stratum) && strata.Add(stratum.Stratum), "Strata must be defined and unique.");
            Require(stratum.MinimumVideoBitrateBps > 0 && stratum.MaximumVideoBitrateBps >= stratum.MinimumVideoBitrateBps, "Invalid inclusive bitrate bounds.");
        }
        var byBitrate = freeze.Strata.OrderBy(s => s.MinimumVideoBitrateBps).ToArray();
        Require(byBitrate.Zip(byBitrate.Skip(1)).All(pair => pair.First.MaximumVideoBitrateBps < pair.Second.MinimumVideoBitrateBps), "Inclusive stratum bounds must not overlap.");

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateIds = new HashSet<long>();
        var ranks = new HashSet<(PredictionShadowExperimentStratum, int)>();
        for (int i = 0; i < freeze.Targets.Count; i++)
        {
            var target = freeze.Targets[i];
            Require(target is not null && target.Slot == i + 1, "Target slots must be unique and ordered 1 through 24.");
            Require(Enum.IsDefined(target.Stratum) && target.Role == PredictionShadowExperimentRole.Target, "Invalid Target stratum or role.");
            AddRosterSource(target.Source, paths, families, candidateIds);
            Require(target.OriginalPoolRank > 0 && ranks.Add((target.Stratum, target.OriginalPoolRank)), "Invalid or duplicate original pool rank.");
        }
        foreach (var reserve in freeze.Reserves)
        {
            Require(reserve is not null && Enum.IsDefined(reserve.Stratum), "Invalid reserve stratum.");
            AddRosterSource(reserve.Source, paths, families, candidateIds);
            Require(reserve.OriginalPoolRank > 0 && ranks.Add((reserve.Stratum, reserve.OriginalPoolRank)), "Invalid or duplicate reserve pool rank.");
        }
        foreach (var stratum in Enum.GetValues<PredictionShadowExperimentStratum>())
        {
            var targets = freeze.Targets.Where(t => t.Stratum == stratum).ToArray();
            var reserves = freeze.Reserves.Where(r => r.Stratum == stratum).ToArray();
            Require(targets.Length == 8, "Exactly eight Targets per stratum are required.");
            Require(reserves.Length == 2 && reserves[0].ReserveOrder == 1 && reserves[1].ReserveOrder == 2, "Exactly two reserves in order 1, 2 per stratum are required.");
            var poolRanks = targets.Select(t => t.OriginalPoolRank).Concat(reserves.Select(r => r.OriginalPoolRank)).ToArray();
            Require(poolRanks.Zip(poolRanks.Skip(1)).All(pair => pair.First < pair.Second), "Targets and reserves must preserve original pool rank order.");
        }
        foreach (var exclusion in freeze.Exclusions)
        {
            Require(exclusion is not null, "Invalid exclusion.");
            ValidateSource(exclusion.Source);
            Text(exclusion.Reason, "Exclusion reason");
            Require(!paths.Contains(Path.GetFullPath(exclusion.Source.SourcePath)) && !families.Contains(exclusion.Source.FamilyKey) &&
                !(exclusion.Source.CandidateId is { } id && candidateIds.Contains(id)), "An excluded source appears in the roster.");
        }
        ValidateReplacementPolicy(freeze.ReplacementPolicy);
        ValidateAcceptance(freeze.AcceptanceCriteria);
        ValidateJournals(freeze.JournalSnapshot);
    }

    private static void ValidateSettings(PredictionShadowFreezeSettings settings)
    {
        Require(settings is not null, "Settings are required.");
        foreach (var value in new[] { settings.SourceCodec, settings.QualityMode, settings.QualityResolutionPolicy,
            settings.QualityTarget, settings.ExpectedSourceAssessment, settings.Encoder, settings.OutputCodec,
            settings.Preset, settings.EncoderSettingsSignature, settings.ConcurrencyPolicy, settings.TransformationRestrictions })
            Text(value, "Required protocol setting");
        Require(settings.SourceWidth > 0 && settings.SourceHeight > 0, "Invalid source geometry.");
        Require(double.IsFinite(settings.MinimumFps) && double.IsFinite(settings.MaximumFps) && settings.MinimumFps > 0 && settings.MaximumFps >= settings.MinimumFps, "Invalid FPS range.");
        Require(settings.ExpectedCq is >= 0 and <= 51 && settings.BitDepth is 8 or 10 or 12, "Invalid CQ or bit depth.");
        Require(settings.AutomaticNvencConcurrency > 0 && settings.StartOneTargetAtATime, "Invalid concurrency policy.");
        Require(settings.AutoTargetSize && settings.NoPerItemTargetSizeOverride && settings.NoExplicitCqOverride &&
            !settings.AllowScaling && !settings.AllowRestoration && !settings.AllowFilteringOrMaterialTransformation, "Invalid Gate 3 override or transformation policy.");
    }

    private static void ValidateComparators(PredictionShadowFreezeComparators comparators)
    {
        Require(comparators is not null && comparators.K == 2, "Schema-1 Gate 3 requires k = 2.");
        foreach (var comparator in new[] { comparators.BaselineRatio, comparators.BaselineDirect, comparators.TemporalRatio, comparators.TemporalDirect })
        {
            Require(comparator is not null, "All four comparator definitions are required.");
            Text(comparator.Version, "Comparator version");
            Text(comparator.Definition, "Comparator definition");
        }
        Require(comparators.NoExtrapolation && comparators.NoWeighting && comparators.NoSpatialCorrection && comparators.NoFittedCoefficient && comparators.NoCqMixing, "Invalid frozen comparator restrictions.");
        Text(comparators.ChronologyAndCutoffRule, "Chronology/cutoff rule");
        Text(comparators.FamilyIndependenceRule, "Family independence rule");
        Text(comparators.OtherRestrictions, "Other comparator restrictions");
    }

    private static void AddRosterSource(PredictionShadowFreezeSource source, HashSet<string> paths, HashSet<string> families, HashSet<long> ids)
    {
        ValidateSource(source);
        Require(paths.Add(Path.GetFullPath(source.SourcePath)), "Duplicate Target/reserve source identity.");
        Require(families.Add(source.FamilyKey), "Duplicate Target/reserve family key.");
        Require(source.CandidateId is null || ids.Add(source.CandidateId.Value), "Duplicate Target/reserve candidate ID.");
    }

    private static void ValidateSource(PredictionShadowFreezeSource source)
    {
        Require(source is not null, "Source identity is required.");
        Text(source.SourcePath, "Source path");
        Require(Path.IsPathFullyQualified(source.SourcePath), "Source path must be absolute.");
        Require(source.SourceLengthBytes > 0 && source.SourceLastWriteTimeUtcTicks > 0 && source.SourceLastWriteTimeUtcTicks <= DateTime.MaxValue.Ticks, "Invalid source binding identity.");
        Require(source.CandidateId is null or > 0, "Invalid candidate ID.");
        Text(source.FamilyKey, "Source family key");
    }

    private static void ValidateReplacementPolicy(PredictionShadowFreezeReplacementPolicy policy)
    {
        Require(policy is not null, "Replacement policy is required.");
        Require(policy.InitialAttempt == 1 && policy.ReplacementAttempt == 2 && policy.MaximumReplacementsPerSlot == 1 && policy.MaximumReplacementsPerStratum == 2 &&
            policy.SameStratumRequired && policy.ConsumeReservesInOrder && policy.PreserveExperimentSlotAndStratum && policy.PreserveInvalidAttemptRecords &&
            policy.ReplacementRole == PredictionShadowExperimentRole.Replacement, "Invalid replacement sequencing or limits.");
        Require(policy.ValidReasons is { Count: > 0 } && policy.ProhibitedOutcomeBasedReasons is { Count: > 0 }, "Replacement reasons and prohibitions are required.");
        foreach (var reason in policy.ValidReasons.Concat(policy.ProhibitedOutcomeBasedReasons)) Text(reason, "Replacement reason");
        Require(!policy.ValidReasons.Intersect(policy.ProhibitedOutcomeBasedReasons, StringComparer.OrdinalIgnoreCase).Any(), "Replacement reasons conflict with prohibitions.");
    }

    private static void ValidateAcceptance(PredictionShadowFreezeAcceptanceCriteria criteria)
    {
        Require(criteria is not null, "Acceptance criteria are required.");
        Text(criteria.ApeDefinition, "APE definition");
        Text(criteria.P90Definition, "P90 definition");
        Text(criteria.JointlySupportedSetRule, "Jointly supported evaluation set");
        Require(criteria.RequiredValidIndependentOutcomes == 24 && criteria.RequiredPostBootstrapBaselineAvailability == 23, "Invalid Gate 3 outcome/baseline counts.");
        Require(criteria.MinimumJointlySupportedTemporalTargets is >= 1 and <= 23 && criteria.MinimumJointlySupportedTargetsPerStratum is >= 1 and <= 8 &&
            criteria.MinimumJointlySupportedTemporalTargets >= 3 * criteria.MinimumJointlySupportedTargetsPerStratum, "Invalid Temporal support counts.");
        foreach (double threshold in new[] { criteria.MaximumTemporalRatioMedianApePercent, criteria.MaximumTemporalRatioP90ApePercent,
            criteria.MaximumTemporalDirectMedianApePercent, criteria.MaximumTemporalDirectP90ApePercent,
            criteria.MinimumMeanApeImprovementPercentagePoints, criteria.MinimumMeanApeImprovementRelativePercent, criteria.MaximumSupportedTemporalApePercent })
            Require(double.IsFinite(threshold) && threshold > 0 && threshold <= 100, "Invalid numerical acceptance threshold.");
        Require(criteria.MaximumTemporalRatioMedianApePercent <= criteria.MaximumTemporalRatioP90ApePercent && criteria.MaximumTemporalDirectMedianApePercent <= criteria.MaximumTemporalDirectP90ApePercent &&
            criteria.MaximumTemporalRatioP90ApePercent <= criteria.MaximumSupportedTemporalApePercent && criteria.MaximumTemporalDirectP90ApePercent <= criteria.MaximumSupportedTemporalApePercent, "Acceptance thresholds are inconsistent.");
        Require(criteria.NoUnexplainedCompatibilityOrInfrastructureAbstentions && criteria.TemporalMedianMustNotWorsen && criteria.DirectMeanMustBeStrictlyLowerThanRatio && criteria.DirectMedianMustBeNoHigherThanRatio && criteria.DirectComparisonAppliesToBaselineAndTemporal, "Required acceptance comparisons are missing.");
    }

    private static void ValidateJournals(IReadOnlyList<PredictionShadowFreezeJournalSnapshot> snapshots)
    {
        Require(snapshots is { Count: > 0 }, "Journal snapshots are required.");
        var activeTypes = new HashSet<PredictionShadowFreezeJournalType>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            Require(snapshot is not null && Enum.IsDefined(snapshot.JournalType) && snapshot.Generation >= 0, "Invalid journal generation identity.");
            Text(snapshot.SourcePath, "Journal path");
            Require(Path.IsPathFullyQualified(snapshot.SourcePath) && paths.Add(Path.GetFullPath(snapshot.SourcePath)), "Duplicate or invalid journal snapshot identity.");
            Require(snapshot.LengthBytes >= 0 && IsHex(snapshot.Sha256, 64), "Invalid journal length or SHA-256.");
            string name = Path.GetFileName(snapshot.SourcePath);
            int suffixStart = name.LastIndexOf(".old", StringComparison.OrdinalIgnoreCase);
            string suffix = suffixStart >= 0 ? name[(suffixStart + 4)..] : "";
            // Match the existing discovery convention: filename.oldN and stem.oldN,
            // including leading zeroes and distinct files with the same numeric suffix.
            bool archived = suffix.Length > 0 && suffix.All(char.IsAsciiDigit) && int.TryParse(suffix, out _);
            if (archived)
                Require(int.Parse(suffix) == snapshot.Generation, "Journal path does not match its numeric generation.");
            else
                Require(snapshot.Generation == 0 && activeTypes.Add(snapshot.JournalType), "Invalid or duplicate active journal reference.");
        }
        Require(Enum.GetValues<PredictionShadowFreezeJournalType>().All(activeTypes.Contains), "Both active research and statistics journals must be referenced.");
    }

    private static bool IsHex(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit);
    private static void Text(string? value, string name) => Require(!string.IsNullOrWhiteSpace(value), name + " is required.");
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
