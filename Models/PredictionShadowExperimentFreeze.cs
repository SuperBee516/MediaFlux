using System.Text.Json.Serialization;

namespace MediaFlux.Models;

/// <summary>Schema-1 Gate 3 protocol and roster, independent of forecasts and encode events.</summary>
public sealed record PredictionShadowExperimentFreeze
{
    public required int SchemaVersion { get; init; } = 1;
    public required string ExperimentId { get; init; }
    public required string ProtocolRevision { get; init; }
    public required DateTime FrozenUtc { get; init; }
    public required string MediaFluxVersion { get; init; }
    public required string GitCommit { get; init; }
    public required PredictionShadowFreezeSettings Settings { get; init; }
    public required PredictionShadowFreezeComparators Comparators { get; init; }
    public required IReadOnlyList<PredictionShadowFreezeStratum> Strata { get; init; }
    public required IReadOnlyList<PredictionShadowFreezeTarget> Targets { get; init; }
    public required IReadOnlyList<PredictionShadowFreezeReserve> Reserves { get; init; }
    public required IReadOnlyList<PredictionShadowFreezeExclusion> Exclusions { get; init; }
    public required PredictionShadowFreezeReplacementPolicy ReplacementPolicy { get; init; }
    public required PredictionShadowFreezeAcceptanceCriteria AcceptanceCriteria { get; init; }
    public required IReadOnlyList<PredictionShadowFreezeJournalSnapshot> JournalSnapshot { get; init; }
}

public sealed record PredictionShadowFreezeSettings
{
    public required string SourceCodec { get; init; }
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }
    public required double MinimumFps { get; init; }
    public required double MaximumFps { get; init; }
    public required string QualityMode { get; init; }
    public required string QualityResolutionPolicy { get; init; }
    public required string QualityTarget { get; init; }
    public required string ExpectedSourceAssessment { get; init; }
    public required int ExpectedCq { get; init; }
    public required string Encoder { get; init; }
    public required string OutputCodec { get; init; }
    public required string Preset { get; init; }
    public required int BitDepth { get; init; }
    public required string EncoderSettingsSignature { get; init; }
    public required bool AutoTargetSize { get; init; }
    public required bool NoPerItemTargetSizeOverride { get; init; }
    public required bool NoExplicitCqOverride { get; init; }
    public required string ConcurrencyPolicy { get; init; }
    public required int AutomaticNvencConcurrency { get; init; }
    public required bool StartOneTargetAtATime { get; init; }
    public required bool AllowScaling { get; init; }
    public required bool AllowRestoration { get; init; }
    public required bool AllowFilteringOrMaterialTransformation { get; init; }
    public required string TransformationRestrictions { get; init; }
}

public sealed record PredictionShadowFreezeComparator(
    [property: JsonRequired] string Version, [property: JsonRequired] string Definition);

public sealed record PredictionShadowFreezeComparators
{
    public required int K { get; init; }
    public required PredictionShadowFreezeComparator BaselineRatio { get; init; }
    public required PredictionShadowFreezeComparator BaselineDirect { get; init; }
    public required PredictionShadowFreezeComparator TemporalRatio { get; init; }
    public required PredictionShadowFreezeComparator TemporalDirect { get; init; }
    public required bool NoExtrapolation { get; init; }
    public required bool NoWeighting { get; init; }
    public required bool NoSpatialCorrection { get; init; }
    public required bool NoFittedCoefficient { get; init; }
    public required bool NoCqMixing { get; init; }
    public required string ChronologyAndCutoffRule { get; init; }
    public required string FamilyIndependenceRule { get; init; }
    public required string OtherRestrictions { get; init; }
}

/// <summary>Both bitrate bounds are inclusive and measured in video-stream bits/second.</summary>
public sealed record PredictionShadowFreezeStratum(
    [property: JsonRequired] PredictionShadowExperimentStratum Stratum,
    [property: JsonRequired] long MinimumVideoBitrateBps, [property: JsonRequired] long MaximumVideoBitrateBps);

/// <summary>Uses the same path/length/last-write binding as durable queue assignments.</summary>
public sealed record PredictionShadowFreezeSource(
    long? CandidateId, [property: JsonRequired] string SourcePath, [property: JsonRequired] long SourceLengthBytes,
    [property: JsonRequired] long SourceLastWriteTimeUtcTicks, [property: JsonRequired] string FamilyKey);

public sealed record PredictionShadowFreezeTarget(
    [property: JsonRequired] int Slot, [property: JsonRequired] PredictionShadowExperimentStratum Stratum,
    [property: JsonRequired] PredictionShadowFreezeSource Source,
    [property: JsonRequired] int OriginalPoolRank, [property: JsonRequired] PredictionShadowExperimentRole Role);

public sealed record PredictionShadowFreezeReserve(
    [property: JsonRequired] PredictionShadowExperimentStratum Stratum, [property: JsonRequired] int ReserveOrder,
    [property: JsonRequired] PredictionShadowFreezeSource Source, [property: JsonRequired] int OriginalPoolRank);

public sealed record PredictionShadowFreezeExclusion(
    [property: JsonRequired] PredictionShadowFreezeSource Source, [property: JsonRequired] string Reason);

public sealed record PredictionShadowFreezeReplacementPolicy
{
    public required int InitialAttempt { get; init; }
    public required int ReplacementAttempt { get; init; }
    public required int MaximumReplacementsPerSlot { get; init; }
    public required int MaximumReplacementsPerStratum { get; init; }
    public required bool SameStratumRequired { get; init; }
    public required bool ConsumeReservesInOrder { get; init; }
    public required bool PreserveExperimentSlotAndStratum { get; init; }
    public required bool PreserveInvalidAttemptRecords { get; init; }
    public required PredictionShadowExperimentRole ReplacementRole { get; init; }
    public required IReadOnlyList<string> ValidReasons { get; init; }
    public required IReadOnlyList<string> ProhibitedOutcomeBasedReasons { get; init; }
}

/// <summary>APE thresholds are percentages; improvement is max(absolute points, relative baseline percent).</summary>
public sealed record PredictionShadowFreezeAcceptanceCriteria
{
    public required int RequiredValidIndependentOutcomes { get; init; }
    public required int RequiredPostBootstrapBaselineAvailability { get; init; }
    public required int MinimumJointlySupportedTemporalTargets { get; init; }
    public required int MinimumJointlySupportedTargetsPerStratum { get; init; }
    public required string ApeDefinition { get; init; }
    public required string P90Definition { get; init; }
    public required string JointlySupportedSetRule { get; init; }
    public required bool DirectComparisonAppliesToBaselineAndTemporal { get; init; }
    public required bool NoUnexplainedCompatibilityOrInfrastructureAbstentions { get; init; }
    public required double MaximumTemporalRatioMedianApePercent { get; init; }
    public required double MaximumTemporalRatioP90ApePercent { get; init; }
    public required double MaximumTemporalDirectMedianApePercent { get; init; }
    public required double MaximumTemporalDirectP90ApePercent { get; init; }
    public required double MinimumMeanApeImprovementPercentagePoints { get; init; }
    public required double MinimumMeanApeImprovementRelativePercent { get; init; }
    public required bool TemporalMedianMustNotWorsen { get; init; }
    public required bool DirectMeanMustBeStrictlyLowerThanRatio { get; init; }
    public required bool DirectMedianMustBeNoHigherThanRatio { get; init; }
    public required double MaximumSupportedTemporalApePercent { get; init; }
}

public enum PredictionShadowFreezeJournalType { ResearchShadowObservations, FinalizedStatistics }

/// <summary>Generation is the numeric .oldN suffix, or 0 for active. SourcePath distinguishes .old0 and naming variants. No journal data is copied.</summary>
public sealed record PredictionShadowFreezeJournalSnapshot(
    [property: JsonRequired] PredictionShadowFreezeJournalType JournalType, [property: JsonRequired] int Generation,
    [property: JsonRequired] string SourcePath, [property: JsonRequired] long LengthBytes, [property: JsonRequired] string Sha256);
