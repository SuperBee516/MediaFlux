namespace MediaFlux.Models;

public enum LibraryPolicyComplianceState
{
    Compliant = 0,
    OptimizationCandidate = 1,
    ReviewRequired = 2,
    NotApplicable = 3,
    UnableToEvaluate = 4
}

public enum LibraryPolicySuggestedAction
{
    None = 0,
    Reencode = 1,
    RemuxOnly = 2,
    Review = 3,
    Unsupported = 4
}

public enum LibraryPolicyConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum RuntimeEstimateConfidence
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public sealed class LibraryPolicyDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Custom policy";
    public bool IsBuiltIn { get; set; }
    public long MinimumFileSizeBytes { get; set; } = 500L * 1024 * 1024;
    public double MinimumDurationSeconds { get; set; } = 60;
    public List<string> IncludedSourceCodecs { get; set; } = new();
    public List<string> ExcludedSourceCodecs { get; set; } = new();
    public int? MinimumHeight { get; set; }
    public int? MaximumHeight { get; set; }
    public bool IncludeHdr { get; set; } = true;
    public bool IncludeSdr { get; set; } = true;
    public bool ExcludeProtectedFiles { get; set; } = true;
    public bool ExcludeDuplicateCleanupCandidates { get; set; } = true;
    public VideoCodecFamily PreferredCodec { get; set; } = VideoCodecFamily.Hevc;
    public string EncoderId { get; set; } = VideoEncoderIds.Libx265;
    public string EncoderPreset { get; set; } = "slow";
    public string EncodingPresetName { get; set; } = "";
    public bool PreserveSourceResolution { get; set; } = true;
    public int? MaximumOutputHeight { get; set; }
    public int PreferredBitDepth { get; set; } = 10;
    public bool PreserveHdr { get; set; } = true;
    public OutputContainerSelection TargetContainer { get; set; } = OutputContainerSelection.Auto;
    public int QualityValue { get; set; } = 22;
    public double MinimumExpectedSavingsPercent { get; set; } = 20;
    public long MinimumExpectedSavingsBytes { get; set; } = 250L * 1024 * 1024;
    public LibraryPolicyConfidence MinimumConfidence { get; set; } = LibraryPolicyConfidence.Medium;
    public bool SkipAlreadyEfficientFiles { get; set; } = true;
    public bool RequireDeepAnalysisForMarginalCases { get; set; } = true;
    public bool AllowRemuxOnly { get; set; } = true;

    public LibraryPolicyDefinition CloneAsCustom(string? name = null) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        Id = Guid.NewGuid().ToString("N"),
        Name = string.IsNullOrWhiteSpace(name) ? $"{Name} copy" : name.Trim(),
        IsBuiltIn = false,
        MinimumFileSizeBytes = MinimumFileSizeBytes,
        MinimumDurationSeconds = MinimumDurationSeconds,
        IncludedSourceCodecs = new(IncludedSourceCodecs),
        ExcludedSourceCodecs = new(ExcludedSourceCodecs),
        MinimumHeight = MinimumHeight,
        MaximumHeight = MaximumHeight,
        IncludeHdr = IncludeHdr,
        IncludeSdr = IncludeSdr,
        ExcludeProtectedFiles = ExcludeProtectedFiles,
        ExcludeDuplicateCleanupCandidates = ExcludeDuplicateCleanupCandidates,
        PreferredCodec = PreferredCodec,
        EncoderId = EncoderId,
        EncoderPreset = EncoderPreset,
        EncodingPresetName = EncodingPresetName,
        PreserveSourceResolution = PreserveSourceResolution,
        MaximumOutputHeight = MaximumOutputHeight,
        PreferredBitDepth = PreferredBitDepth,
        PreserveHdr = PreserveHdr,
        TargetContainer = TargetContainer,
        QualityValue = QualityValue,
        MinimumExpectedSavingsPercent = MinimumExpectedSavingsPercent,
        MinimumExpectedSavingsBytes = MinimumExpectedSavingsBytes,
        MinimumConfidence = MinimumConfidence,
        SkipAlreadyEfficientFiles = SkipAlreadyEfficientFiles,
        RequireDeepAnalysisForMarginalCases = RequireDeepAnalysisForMarginalCases,
        AllowRemuxOnly = AllowRemuxOnly
    };

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "Custom policy" : Name.Trim();
        IncludedSourceCodecs = NormalizeCodecs(IncludedSourceCodecs);
        ExcludedSourceCodecs = NormalizeCodecs(ExcludedSourceCodecs);
        MinimumFileSizeBytes = Math.Clamp(MinimumFileSizeBytes, 0, 100L * 1024 * 1024 * 1024 * 1024);
        MinimumDurationSeconds = Math.Clamp(MinimumDurationSeconds, 0, 24 * 60 * 60);
        MinimumHeight = NormalizeHeight(MinimumHeight);
        MaximumHeight = NormalizeHeight(MaximumHeight);
        MaximumOutputHeight = NormalizeHeight(MaximumOutputHeight);
        if (MinimumHeight.HasValue && MaximumHeight.HasValue && MinimumHeight > MaximumHeight)
            (MinimumHeight, MaximumHeight) = (MaximumHeight, MinimumHeight);
        if (!Enum.IsDefined(PreferredCodec)) PreferredCodec = VideoCodecFamily.Hevc;
        if (!Enum.IsDefined(TargetContainer)) TargetContainer = OutputContainerSelection.Auto;
        if (!Enum.IsDefined(MinimumConfidence)) MinimumConfidence = LibraryPolicyConfidence.Medium;
        EncoderId = EncoderId?.Trim() ?? "";
        EncoderPreset = EncoderPreset?.Trim() ?? "";
        EncodingPresetName = EncodingPresetName?.Trim() ?? "";
        PreferredBitDepth = PreferredBitDepth >= 10 ? 10 : 8;
        QualityValue = Math.Clamp(QualityValue, 0, 51);
        MinimumExpectedSavingsPercent = Math.Clamp(MinimumExpectedSavingsPercent, 0, 90);
        MinimumExpectedSavingsBytes = Math.Clamp(MinimumExpectedSavingsBytes, 0, 100L * 1024 * 1024 * 1024 * 1024);
    }

    private static List<string> NormalizeCodecs(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();

    private static int? NormalizeHeight(int? value) => value is > 0 ? Math.Clamp(value.Value, 144, 8640) : null;
}

public sealed class LibraryPolicyEvaluationResult
{
    public string PolicyId { get; init; } = "";
    public string PolicyName { get; init; } = "";
    public long FileId { get; init; }
    public string FullPath { get; init; } = "";
    public string LocationPath { get; init; } = "";
    public string PhysicalIdentityKey { get; init; } = "";
    public LibraryPolicyComplianceState State { get; init; }
    public LibraryPolicySuggestedAction SuggestedAction { get; init; }
    public double OpportunityScore { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReviewReasons { get; init; } = Array.Empty<string>();
    public string CurrentCharacteristics { get; init; } = "";
    public string ProposedCharacteristics { get; init; } = "";
    public long OriginalSizeBytes { get; init; }
    public long? ProjectedOutputBytes { get; init; }
    public long? ProjectedReclaimableBytes { get; init; }
    public double? ProjectedSavingsPercent { get; init; }
    public LibraryPolicyConfidence Confidence { get; init; }
    public string ProjectionBasis { get; init; } = "";
    public VideoCodecFamily ProposedCodec { get; init; }
    public string EncoderId { get; init; } = "";
    public string EncoderPreset { get; init; } = "";
    public string EncodingPresetName { get; init; } = "";
    public int QualityValue { get; init; }
    public int PreferredBitDepth { get; init; }
    public bool PreserveSourceResolution { get; init; }
    public int? MaximumOutputHeight { get; init; }
    public bool PreserveHdr { get; init; }
    public OutputContainerSelection TargetContainer { get; init; }
    public double? SourceDurationSeconds { get; init; }
    public int? SourceHeight { get; init; }
    public int? SourceBitDepth { get; init; }
    public double? EstimatedProcessingSeconds { get; init; }
    public double? EstimatedSpeedX { get; init; }
    public double? SavingsEfficiencyBytesPerHour { get; init; }
    public RuntimeEstimateConfidence RuntimeConfidence { get; init; }
    public int RuntimeSampleCount { get; init; }
    public string RuntimeExplanation { get; init; } = "";
}

public sealed record LibraryPolicyQueueItem(
    string FullPath,
    string PolicyId,
    string PolicyName,
    VideoCodecFamily ProposedCodec,
    string EncoderId,
    string EncoderPreset,
    string EncodingPresetName,
    int QualityValue,
    int PreferredBitDepth,
    bool PreserveSourceResolution,
    int? MaximumOutputHeight,
    bool PreserveHdr,
    OutputContainerSelection TargetContainer,
    long? ProjectedOutputBytes,
    LibraryPolicyConfidence Confidence,
    double? EstimatedProcessingSeconds = null,
    double? SavingsEfficiencyBytesPerHour = null,
    RuntimeEstimateConfidence RuntimeConfidence = RuntimeEstimateConfidence.Unknown,
    string RuntimeExplanation = "");
