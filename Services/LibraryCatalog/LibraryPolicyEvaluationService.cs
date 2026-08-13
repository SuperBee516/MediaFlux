using MediaFlux.Models;
using MediaFlux.Services.Encoders;
using System.Text.Json;

namespace MediaFlux.Services.LibraryCatalog;

public sealed class LibraryPolicyCapabilitySnapshot
{
    public IReadOnlyDictionary<string, string> AvailableEncoderCodecs { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> TenBitEncoderIds { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> EncodingPresetNames { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, EncodingPreset> EncodingPresets { get; init; } =
        new Dictionary<string, EncodingPreset>(StringComparer.OrdinalIgnoreCase);
    public string InspectionError { get; init; } = "";

    public static string Key(string encoderId, VideoCodecFamily codec) => $"{encoderId}|{codec}";

    public bool TryGetFfmpegCodec(string encoderId, VideoCodecFamily codec, out string ffmpegCodec) =>
        AvailableEncoderCodecs.TryGetValue(Key(encoderId, codec), out ffmpegCodec!);
}

public static class LibraryPolicyCapabilityFactory
{
    public static LibraryPolicyCapabilitySnapshot Create(
        string ffmpegPath,
        IEnumerable<EncodingPreset>? encodingPresets = null)
    {
        FfmpegEncoderCapabilities inspected = FfmpegEncoderCapabilityService.GetCapabilities(ffmpegPath);
        var codecs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tenBit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EncoderCapabilities capabilities in EncoderRegistry.Default.GetCapabilities())
        {
            if (capabilities.SupportsTenBit) tenBit.Add(capabilities.Id);
            foreach (VideoCodecFamily codec in capabilities.SupportedCodecs)
            {
                VideoEncoderSelection selection = EncoderRegistry.Default.Resolve(capabilities.Id, codec).Selection;
                if (!inspected.InspectionSucceeded || inspected.Contains(selection.FfmpegCodec))
                    codecs[LibraryPolicyCapabilitySnapshot.Key(capabilities.Id, codec)] = selection.FfmpegCodec;
            }
        }
        EncodingPreset[] presets = (encodingPresets ?? Array.Empty<EncodingPreset>()).ToArray();
        return new LibraryPolicyCapabilitySnapshot
        {
            AvailableEncoderCodecs = codecs,
            TenBitEncoderIds = tenBit,
            EncodingPresetNames = new HashSet<string>(
                presets.Select(preset => preset.Name),
                StringComparer.OrdinalIgnoreCase),
            EncodingPresets = presets.Where(preset => !string.IsNullOrWhiteSpace(preset.Name))
                .GroupBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase),
            InspectionError = inspected.InspectionSucceeded ? "" : inspected.ErrorMessage ?? "Encoder inspection failed."
        };
    }
}

public sealed class LibraryPolicyEvaluationEngine
{
    private readonly SmartEncodeDecisionService _smart = new();

    public LibraryPolicyEvaluationResult Evaluate(
        LibraryPolicyDefinition sourcePolicy,
        LibraryPolicyFileFacts file,
        LibraryPolicyCapabilitySnapshot capabilities)
    {
        ArgumentNullException.ThrowIfNull(sourcePolicy);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(capabilities);
        LibraryPolicyDefinition policy = sourcePolicy.CloneAsCustom(sourcePolicy.Name);
        policy.Id = sourcePolicy.Id;
        policy.IsBuiltIn = sourcePolicy.IsBuiltIn;
        policy.Normalize();

        string projectionProfile = "Medium Quality (Default)";
        var reasons = new List<string>();
        var review = new List<string>();
        string current = DescribeCurrent(file);
        string proposed = DescribeProposed(policy, file);

        LibraryPolicyEvaluationResult Finish(
            LibraryPolicyComplianceState state,
            LibraryPolicySuggestedAction action,
            LibraryPolicyConfidence confidence,
            long? outputBytes = null,
            double? savingsPercent = null,
            double score = 0,
            string basis = "") => new()
        {
            PolicyId = policy.Id,
            PolicyName = policy.Name,
            FileId = file.FileId,
            FullPath = file.FullPath,
            LocationPath = file.LocationPath,
            PhysicalIdentityKey = !string.IsNullOrWhiteSpace(file.VolumeId) && !string.IsNullOrWhiteSpace(file.FileIdentity)
                ? $"{file.VolumeId}|{file.FileIdentity}" : file.FullPath,
            State = state,
            SuggestedAction = action,
            OpportunityScore = Math.Round(Math.Clamp(score, 0, 100), 1),
            Reasons = reasons.ToArray(),
            ReviewReasons = review.ToArray(),
            CurrentCharacteristics = current,
            ProposedCharacteristics = proposed,
            OriginalSizeBytes = file.SizeBytes,
            ProjectedOutputBytes = outputBytes,
            ProjectedReclaimableBytes = outputBytes.HasValue ? Math.Max(0, file.SizeBytes - outputBytes.Value) : null,
            ProjectedSavingsPercent = savingsPercent,
            Confidence = confidence,
            ProjectionBasis = basis,
            ProposedCodec = policy.PreferredCodec,
            EncoderId = policy.EncoderId,
            EncoderPreset = policy.EncoderPreset,
            EncodingPresetName = policy.EncodingPresetName,
            QualityValue = policy.QualityValue,
            PreferredBitDepth = policy.PreferredBitDepth,
            PreserveSourceResolution = policy.PreserveSourceResolution,
            MaximumOutputHeight = policy.MaximumOutputHeight,
            PreserveHdr = policy.PreserveHdr,
            TargetContainer = policy.TargetContainer,
            SourceDurationSeconds = file.DurationSeconds,
            SourceHeight = file.Height,
            SourceBitDepth = file.BitDepth
        };

        if (file.ProbeStatus != LibraryProbeStatus.Succeeded || string.IsNullOrWhiteSpace(file.VideoCodec))
        {
            reasons.Add(string.IsNullOrWhiteSpace(file.ProbeError)
                ? "Required catalog metadata is unavailable."
                : $"Catalog metadata could not be read: {file.ProbeError}");
            return Finish(LibraryPolicyComplianceState.UnableToEvaluate, LibraryPolicySuggestedAction.Unsupported, LibraryPolicyConfidence.Low);
        }
        if (file.SizeBytes < policy.MinimumFileSizeBytes)
        {
            reasons.Add($"File size is below the policy minimum of {FormatBytes(policy.MinimumFileSizeBytes)}.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        if (file.DurationSeconds is not > 0 || file.Width is not > 0 || file.Height is not > 0 || file.FrameRate is not > 0)
        {
            reasons.Add("Duration, resolution, or frame-rate metadata required for a trustworthy projection is missing.");
            return Finish(LibraryPolicyComplianceState.UnableToEvaluate, LibraryPolicySuggestedAction.Unsupported, LibraryPolicyConfidence.Low);
        }
        if (file.DurationSeconds < policy.MinimumDurationSeconds)
        {
            reasons.Add($"Duration is below the policy minimum of {TimeSpan.FromSeconds(policy.MinimumDurationSeconds):g}.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        string normalizedCodec = NormalizeCodec(file.VideoCodec);
        if (policy.IncludedSourceCodecs.Count > 0 && !policy.IncludedSourceCodecs.Any(value => CodecMatches(normalizedCodec, value)))
        {
            reasons.Add("The source codec is outside this policy's included codec set.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        if (policy.ExcludedSourceCodecs.Any(value => CodecMatches(normalizedCodec, value)))
        {
            reasons.Add("The source codec is explicitly excluded by this policy.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        if (policy.MinimumHeight.HasValue && file.Height < policy.MinimumHeight || policy.MaximumHeight.HasValue && file.Height > policy.MaximumHeight)
        {
            reasons.Add("The source resolution is outside this policy's applicability range.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        bool isHdr = IsHdr(file);
        if ((isHdr && !policy.IncludeHdr) || (!isHdr && !policy.IncludeSdr))
        {
            reasons.Add(isHdr ? "HDR sources are not applicable to this policy." : "SDR sources are not applicable to this policy.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        if (policy.ExcludeProtectedFiles && file.IsProtected)
        {
            reasons.Add("The file is protected in Library Analyzer.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        if (policy.ExcludeDuplicateCleanupCandidates &&
            (file.IsExactDuplicate || file.IsReviewedVisualCleanupCandidate || file.IsReviewedFamilyCleanupCandidate))
        {
            reasons.Add(file.IsExactDuplicate
                ? "The file belongs to a current exact-duplicate group; cleanup should be resolved before re-encoding."
                : "The file is a reviewed visual cleanup candidate; projected bytes are excluded to prevent double counting.");
            return Finish(LibraryPolicyComplianceState.NotApplicable, LibraryPolicySuggestedAction.None, LibraryPolicyConfidence.High);
        }
        if (file.HasUnreviewedVisualMatch)
            review.Add("The file has an unreviewed visual match; duplicate review should precede an expensive encode.");

        if (!string.IsNullOrWhiteSpace(policy.EncodingPresetName))
        {
            if (!capabilities.EncodingPresets.TryGetValue(policy.EncodingPresetName, out EncodingPreset? namedPreset))
            {
                reasons.Add($"Referenced encoding preset '{policy.EncodingPresetName}' is unavailable; no substitute was selected.");
                return Finish(LibraryPolicyComplianceState.UnableToEvaluate, LibraryPolicySuggestedAction.Unsupported, LibraryPolicyConfidence.High);
            }
            VideoCodecFamily presetCodec = VideoEncoderCompatibility.ParseCodecFamily(
                string.IsNullOrWhiteSpace(namedPreset.VideoCodec) ? namedPreset.VideoFormat : namedPreset.VideoCodec);
            policy.PreferredCodec = presetCodec;
            policy.EncoderId = VideoEncoderCompatibility.ResolveEncoderId(
                string.IsNullOrWhiteSpace(namedPreset.EncoderId) ? namedPreset.EncoderMode : namedPreset.EncoderId, presetCodec);
            policy.EncoderPreset = string.IsNullOrWhiteSpace(namedPreset.EncoderPreset) ? namedPreset.NvencPreset ?? "" : namedPreset.EncoderPreset;
            policy.QualityValue = namedPreset.QualityValue ?? policy.QualityValue;
            policy.PreferredBitDepth = namedPreset.TenBit ? 10 : 8;
            if (Enum.TryParse(namedPreset.OutputContainer, true, out OutputContainerSelection presetContainer)) policy.TargetContainer = presetContainer;
            projectionProfile = string.IsNullOrWhiteSpace(namedPreset.CompressionProfile) ? projectionProfile : namedPreset.CompressionProfile;
            if (!string.IsNullOrWhiteSpace(namedPreset.ScaleMode) && !namedPreset.ScaleMode.Contains("none", StringComparison.OrdinalIgnoreCase))
            {
                int? height = ParsePresetHeight(namedPreset.ScaleMode);
                if (height.HasValue) { policy.PreserveSourceResolution = false; policy.MaximumOutputHeight = height; }
            }
            proposed = DescribeProposed(policy, file);
        }

        if (!capabilities.TryGetFfmpegCodec(policy.EncoderId, policy.PreferredCodec, out string targetCodec))
        {
            reasons.Add($"Encoder '{policy.EncoderId}' is not currently available for {policy.PreferredCodec}.");
            if (!string.IsNullOrWhiteSpace(capabilities.InspectionError)) reasons.Add(capabilities.InspectionError);
            return Finish(LibraryPolicyComplianceState.UnableToEvaluate, LibraryPolicySuggestedAction.Unsupported, LibraryPolicyConfidence.Low);
        }
        if (policy.PreferredBitDepth >= 10 && !capabilities.TenBitEncoderIds.Contains(policy.EncoderId))
        {
            reasons.Add($"Encoder '{policy.EncoderId}' does not support the requested 10-bit output.");
            return Finish(LibraryPolicyComplianceState.UnableToEvaluate, LibraryPolicySuggestedAction.Unsupported, LibraryPolicyConfidence.High);
        }
        if (isHdr && policy.PreserveHdr && policy.PreferredBitDepth < 10)
            review.Add("HDR preservation requires 10-bit output, but this policy requests 8-bit output.");
        if (isHdr && !policy.PreserveHdr)
            review.Add("The HDR source would not be preserved by this policy and requires explicit review.");
        if (file.BitDepth is >= 10 && policy.PreferredBitDepth < 10)
            review.Add("The policy would reduce source bit depth from 10-bit to 8-bit.");
        if (!string.IsNullOrWhiteSpace(file.FieldOrder) && file.FieldOrder is not ("progressive" or "unknown" or "unspecified"))
            review.Add($"The source is flagged as interlaced ({file.FieldOrder}).");

        int? targetHeight = policy.PreserveSourceResolution
            ? file.Height
            : policy.MaximumOutputHeight.HasValue ? Math.Min(file.Height.Value, policy.MaximumOutputHeight.Value) : file.Height;
        double sourceMb = file.SizeBytes / (1024d * 1024d);
        int totalKbps = file.TotalBitRate is > 0 ? (int)Math.Min(int.MaxValue, file.TotalBitRate.Value / 1000) : 0;
        int videoKbps = totalKbps > 0 ? Math.Max(1, (int)Math.Round(totalKbps * 0.90)) : 0;
        double projectedMb = SizeEstimateService.EstimateAutoTargetMbSmart(
            sourceMb, file.DurationSeconds.Value, file.Width.Value, file.Height.Value, file.FrameRate.Value,
            videoKbps, file.VideoCodec, projectionProfile, targetCodec, policy.QualityValue,
            targetHeight, sourceAudioStreamCount: file.AudioStreams.Count, sourceTotalBitrateKbps: totalKbps,
            sourceSubtitleStreamCount: file.SubtitleStreams.Count, sourceAttachmentStreamCount: file.AttachmentCount);
        if (projectedMb <= 0 || !double.IsFinite(projectedMb))
        {
            reasons.Add("The existing metadata estimator could not produce a reliable projection.");
            return Finish(LibraryPolicyComplianceState.UnableToEvaluate, LibraryPolicySuggestedAction.Unsupported, LibraryPolicyConfidence.Low);
        }
        long projectedBytes = (long)Math.Round(projectedMb) * 1024 * 1024;
        projectedBytes = Math.Max(1, projectedBytes);
        long savingsBytes = Math.Max(0, file.SizeBytes - projectedBytes);
        double savingsPercent = file.SizeBytes > 0 ? savingsBytes * 100d / file.SizeBytes : 0;
        LibraryPolicyConfidence confidence = file.TotalBitRate is > 0 && file.BitDepth.HasValue
            ? LibraryPolicyConfidence.Medium
            : LibraryPolicyConfidence.Low;
        string basis = confidence == LibraryPolicyConfidence.Medium
            ? "Rounded metadata-only projection using catalog FFprobe facts and the normal MediaFlux estimator."
            : "Low-confidence rounded metadata heuristic; deeper analysis is recommended.";

        var sourceProbe = new MediaProbeResult
        {
            Success = true,
            Streams = file.AudioStreams.Select((stream, index) => new MediaProbeStreamInfo
                { Index = index, CodecType = "audio", CodecName = stream.Codec })
                .Concat(file.SubtitleStreams.Select((stream, index) => new MediaProbeStreamInfo
                    { Index = 1000 + index, CodecType = "subtitle", CodecName = stream.Codec }))
                .Concat(Enumerable.Range(0, file.AttachmentCount).Select(index => new MediaProbeStreamInfo
                    { Index = 2000 + index, CodecType = "attachment", CodecName = "attachment" }))
                .ToArray()
        };
        OutputContainerDecision container = OutputContainerPolicy.Decide(
            policy.TargetContainer, sourceProbe, EncodingInputSource.FromFile(file.FullPath), EncodingService.StreamMapMode.KeepAll);
        if (policy.TargetContainer == OutputContainerSelection.Mp4 && container.CompatibilityWarnings.Count > 0)
            review.AddRange(container.CompatibilityWarnings.Select(warning => $"MP4 compatibility: {warning}."));
        reasons.Add(container.Reason);

        var smart = _smart.Evaluate(new SmartEncodeSourceInfo
        {
            Path = file.FullPath, SourceMb = sourceMb, DurationSeconds = file.DurationSeconds.Value,
            Width = file.Width.Value, Height = file.Height.Value, FramesPerSecond = file.FrameRate.Value,
            VideoBitrateKbps = videoKbps, TotalBitrateKbps = totalKbps,
            AudioStreamCount = file.AudioStreams.Count, SubtitleStreamCount = file.SubtitleStreams.Count,
            VideoStreamCount = 1, VideoCodec = file.VideoCodec, FormatName = file.FormatName, FieldOrder = file.FieldOrder
        }, new SmartEncodeIntent
        {
            TargetCodec = targetCodec, TargetHeight = targetHeight, EstimatedOutputMb = projectedBytes / (1024d * 1024d),
            MinimumSavingsPercent = policy.MinimumExpectedSavingsPercent
        });
        reasons.AddRange(smart.Reasons);

        if (review.Count > 0)
            return Finish(LibraryPolicyComplianceState.ReviewRequired, LibraryPolicySuggestedAction.Review, confidence,
                projectedBytes, savingsPercent, Score(savingsPercent, savingsBytes), basis);
        if (policy.AllowRemuxOnly && smart.Kind == SmartEncodeRecommendationKind.RemuxOnly)
            return Finish(LibraryPolicyComplianceState.OptimizationCandidate, LibraryPolicySuggestedAction.RemuxOnly,
                confidence, file.SizeBytes, 0, 20, "Lossless stream-copy opportunity; little size change is expected.");

        bool belowPercent = savingsPercent < policy.MinimumExpectedSavingsPercent;
        bool belowAbsolute = savingsBytes < policy.MinimumExpectedSavingsBytes;
        bool sourceEfficient = CodecEfficiency(file.VideoCodec) >= CodecEfficiency(targetCodec);
        if ((belowPercent || belowAbsolute) && (policy.SkipAlreadyEfficientFiles || sourceEfficient))
        {
            reasons.Insert(0, sourceEfficient
                ? "Compliant: the source is already efficient and projected benefit is below policy thresholds."
                : "Compliant: projected conversion benefit is not worthwhile under this policy.");
            return Finish(LibraryPolicyComplianceState.Compliant, LibraryPolicySuggestedAction.None, confidence,
                projectedBytes, savingsPercent, 0, basis);
        }
        if (confidence < policy.MinimumConfidence)
        {
            review.Add($"Projection confidence is {confidence}, below the policy minimum of {policy.MinimumConfidence}.");
            return Finish(LibraryPolicyComplianceState.ReviewRequired, LibraryPolicySuggestedAction.Review, confidence,
                projectedBytes, savingsPercent, Score(savingsPercent, savingsBytes), basis);
        }
        bool marginal = savingsPercent < policy.MinimumExpectedSavingsPercent + 5 ||
                        savingsBytes < policy.MinimumExpectedSavingsBytes + 100L * 1024 * 1024;
        if (policy.RequireDeepAnalysisForMarginalCases && marginal)
        {
            review.Add("The opportunity is near a policy threshold; optional deeper analysis is recommended before encoding.");
            return Finish(LibraryPolicyComplianceState.ReviewRequired, LibraryPolicySuggestedAction.Review, confidence,
                projectedBytes, savingsPercent, Score(savingsPercent, savingsBytes), basis);
        }
        reasons.Insert(0, $"Optimization candidate: projected storage reduction is approximately {savingsPercent:0}% ({FormatBytes(savingsBytes)})." );
        return Finish(LibraryPolicyComplianceState.OptimizationCandidate, LibraryPolicySuggestedAction.Reencode, confidence,
            projectedBytes, savingsPercent, Score(savingsPercent, savingsBytes), basis);
    }

    private static double Score(double percent, long bytes) =>
        Math.Min(100, percent * 1.4 + Math.Log10(Math.Max(1, bytes / (1024d * 1024d))) * 12);
    private static bool IsHdr(LibraryPolicyFileFacts file) =>
        file.ColorTransfer.Contains("2084", StringComparison.OrdinalIgnoreCase) ||
        file.ColorTransfer.Contains("pq", StringComparison.OrdinalIgnoreCase) ||
        file.ColorTransfer.Contains("hlg", StringComparison.OrdinalIgnoreCase) ||
        file.ColorTransfer.Contains("b67", StringComparison.OrdinalIgnoreCase) ||
        file.ColorPrimaries.Contains("2020", StringComparison.OrdinalIgnoreCase);
    private static string NormalizeCodec(string value) => value.Trim().ToLowerInvariant();
    private static bool CodecMatches(string source, string criterion) =>
        source.Contains(criterion, StringComparison.OrdinalIgnoreCase) || criterion.Contains(source, StringComparison.OrdinalIgnoreCase);
    private static double CodecEfficiency(string codec)
    {
        string value = codec.ToLowerInvariant();
        if (value.Contains("av1") || value.Contains("av01") || value.Contains("svt")) return 4;
        if (value.Contains("hevc") || value.Contains("265") || value.Contains("vp9")) return 3;
        if (value.Contains("h264") || value.Contains("264") || value.Contains("avc")) return 2;
        return 1;
    }
    private static string DescribeCurrent(LibraryPolicyFileFacts file) =>
        $"{file.VideoCodec.ToUpperInvariant()} {file.VideoProfile}, {file.Width?.ToString() ?? "?"}×{file.Height?.ToString() ?? "?"}, " +
        $"{(file.BitDepth.HasValue ? $"{file.BitDepth}-bit" : "unknown bit depth")}, {(IsHdr(file) ? "HDR" : "SDR")}, {file.FormatName}";
    private static string DescribeProposed(LibraryPolicyDefinition policy, LibraryPolicyFileFacts file) =>
        $"{policy.PreferredCodec} {policy.PreferredBitDepth}-bit, " +
        $"{(policy.PreserveSourceResolution ? "source resolution" : policy.MaximumOutputHeight.HasValue ? $"up to {policy.MaximumOutputHeight}p" : "policy resolution")}, " +
        $"{policy.TargetContainer} container";
    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.##} GiB"
        : $"{bytes / (1024d * 1024):0.#} MiB";
    private static int? ParsePresetHeight(string value)
    {
        foreach (int height in new[] { 2160, 1440, 1080, 720 })
            if (value.Contains(height.ToString(), StringComparison.OrdinalIgnoreCase) || height == 2160 && value.Contains("4K", StringComparison.OrdinalIgnoreCase)) return height;
        return null;
    }
}

public sealed class LibraryPolicyEvaluationService
{
    private const int BatchSize = 500;
    private readonly ILibraryPhase2Catalog _catalog;
    private readonly LibraryPolicyEvaluationEngine _engine;
    private readonly object _cacheGate = new();
    private string _summaryKey = "";
    private LibraryPolicyEvaluationSummary? _cachedSummary;
    private readonly Dictionary<string, LibraryPolicyEvaluationPage> _pageCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _pageCacheOrder = new();

    public LibraryPolicyEvaluationService(ILibraryPhase2Catalog catalog, LibraryPolicyEvaluationEngine? engine = null)
    {
        _catalog = catalog;
        _engine = engine ?? new LibraryPolicyEvaluationEngine();
    }

    public (LibraryPolicyEvaluationPage Page, LibraryPolicyEvaluationSummary Summary) Evaluate(
        LibraryPolicyDefinition policy,
        LibraryPolicyResultQuery query,
        LibraryPolicyCapabilitySnapshot capabilities)
    {
        string evaluationKey = BuildEvaluationKey(policy, capabilities) + "|catalog=" + _catalog.GetPolicyFactsRevision();
        string pageKey = $"{evaluationKey}|{query.State}|{query.Offset}|{query.Limit}";
        lock (_cacheGate)
        {
            if (_summaryKey == evaluationKey && _cachedSummary != null && _pageCache.TryGetValue(pageKey, out LibraryPolicyEvaluationPage? cachedPage))
                return (cachedPage, _cachedSummary);
        }
        int requestedOffset = Math.Max(0, query.Offset);
        int requestedLimit = Math.Clamp(query.Limit, 1, 500);
        var page = new List<LibraryPolicyEvaluationResult>(requestedLimit);
        long filteredCount = 0, evaluated = 0, compliant = 0, candidates = 0, review = 0, notApplicable = 0, unavailable = 0, bytes = 0;
        LibraryPolicyEvaluationSummary? existingSummary;
        lock (_cacheGate) existingSummary = _summaryKey == evaluationKey ? _cachedSummary : null;
        for (int catalogOffset = 0; ; catalogOffset += BatchSize)
        {
            IReadOnlyList<LibraryPolicyFileFacts> facts = _catalog.QueryPolicyFileFacts(catalogOffset, BatchSize);
            foreach (LibraryPolicyFileFacts fact in facts)
            {
                LibraryPolicyEvaluationResult result = _engine.Evaluate(policy, fact, capabilities);
                evaluated++;
                switch (result.State)
                {
                    case LibraryPolicyComplianceState.Compliant: compliant++; break;
                    case LibraryPolicyComplianceState.OptimizationCandidate: candidates++; bytes += result.ProjectedReclaimableBytes ?? 0; break;
                    case LibraryPolicyComplianceState.ReviewRequired: review++; break;
                    case LibraryPolicyComplianceState.NotApplicable: notApplicable++; break;
                    case LibraryPolicyComplianceState.UnableToEvaluate: unavailable++; break;
                }
                if (query.State.HasValue && result.State != query.State.Value) continue;
                if (filteredCount >= requestedOffset && page.Count < requestedLimit) page.Add(result);
                filteredCount++;
            }
            if (existingSummary != null && page.Count >= requestedLimit) break;
            if (facts.Count < BatchSize) break;
        }
        LibraryPolicyEvaluationSummary summary = existingSummary ??
            new LibraryPolicyEvaluationSummary(evaluated, compliant, candidates, review, notApplicable, unavailable, bytes);
        long totalCount = query.State switch
        {
            null => summary.FilesEvaluated,
            LibraryPolicyComplianceState.Compliant => summary.Compliant,
            LibraryPolicyComplianceState.OptimizationCandidate => summary.OptimizationCandidates,
            LibraryPolicyComplianceState.ReviewRequired => summary.ReviewRequired,
            LibraryPolicyComplianceState.NotApplicable => summary.NotApplicable,
            LibraryPolicyComplianceState.UnableToEvaluate => summary.UnableToEvaluate,
            _ => filteredCount
        };
        var resultPage = new LibraryPolicyEvaluationPage(totalCount, page);
        lock (_cacheGate)
        {
            if (_summaryKey != evaluationKey)
            {
                _summaryKey = evaluationKey;
                _pageCache.Clear();
                _pageCacheOrder.Clear();
            }
            _cachedSummary = summary;
            if (!_pageCache.ContainsKey(pageKey)) _pageCacheOrder.Enqueue(pageKey);
            _pageCache[pageKey] = resultPage;
            while (_pageCacheOrder.Count > 12) _pageCache.Remove(_pageCacheOrder.Dequeue());
        }
        return (resultPage, summary);
    }

    public void Invalidate()
    {
        lock (_cacheGate)
        {
            _summaryKey = "";
            _cachedSummary = null;
            _pageCache.Clear();
            _pageCacheOrder.Clear();
        }
    }

    public IReadOnlyList<LibraryPolicyEvaluationResult> EvaluateForPlanning(
        LibraryPolicyDefinition policy,
        LibraryPolicyCapabilitySnapshot capabilities,
        bool includeReviewRequired,
        int maximumResults = 50_000,
        CancellationToken cancellationToken = default)
    {
        maximumResults = Math.Clamp(maximumResults, 1, 50_000);
        var results = new List<LibraryPolicyEvaluationResult>(Math.Min(maximumResults, 4096));
        for (int offset = 0; results.Count < maximumResults; offset += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<LibraryPolicyFileFacts> facts = _catalog.QueryPolicyFileFacts(offset, BatchSize);
            foreach (LibraryPolicyFileFacts fact in facts)
            {
                LibraryPolicyEvaluationResult result = _engine.Evaluate(policy, fact, capabilities);
                if (result.State == LibraryPolicyComplianceState.OptimizationCandidate ||
                    includeReviewRequired && result.State == LibraryPolicyComplianceState.ReviewRequired)
                {
                    results.Add(result);
                    if (results.Count >= maximumResults) break;
                }
            }
            if (facts.Count < BatchSize) break;
        }
        return results;
    }

    private static string BuildEvaluationKey(LibraryPolicyDefinition policy, LibraryPolicyCapabilitySnapshot capabilities) =>
        JsonSerializer.Serialize(policy) + "|" +
        string.Join(';', capabilities.AvailableEncoderCodecs.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}")) + "|" +
        string.Join(';', capabilities.TenBitEncoderIds.OrderBy(item => item, StringComparer.OrdinalIgnoreCase)) + "|" +
        JsonSerializer.Serialize(capabilities.EncodingPresets.OrderBy(item => item.Key).Select(item => item.Value)) + "|" +
        capabilities.InspectionError;
}
