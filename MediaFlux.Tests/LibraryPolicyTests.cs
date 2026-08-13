using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-PolicyTests", Guid.NewGuid().ToString("N"));

    public LibraryPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BuiltInsAreNormalizedStableAndProtected()
    {
        Assert.Equal(3, LibraryPolicyBuiltIns.All.Count);
        Assert.All(LibraryPolicyBuiltIns.All, policy =>
        {
            Assert.True(policy.IsBuiltIn);
            Assert.Equal(LibraryPolicyDefinition.CurrentSchemaVersion, policy.SchemaVersion);
            Assert.False(string.IsNullOrWhiteSpace(policy.Id));
            Assert.InRange(policy.QualityValue, 0, 51);
        });
        var store = new LibraryPolicyStore(Path.Combine(_root, "policies.json"));
        Assert.Throws<InvalidOperationException>(() => store.Delete(LibraryPolicyBuiltIns.GeneralArchiveId));
        Assert.Throws<InvalidOperationException>(() => store.Save(LibraryPolicyBuiltIns.All[0]));
    }

    [Fact]
    public void CustomPoliciesClonePersistEditDeleteAndNormalizeInvalidValues()
    {
        string path = Path.Combine(_root, "nested", "policies.json");
        var store = new LibraryPolicyStore(path);
        LibraryPolicyDefinition custom = LibraryPolicyBuiltIns.All[0].CloneAsCustom(" My policy ");
        custom.QualityValue = 999;
        custom.MinimumExpectedSavingsPercent = -50;
        custom.PreferredBitDepth = 9;
        custom.IncludedSourceCodecs = new() { " H264 ", "h264", "" };
        store.Save(custom);
        LibraryPolicyDefinition loaded = Assert.Single(store.LoadCustom());
        Assert.Equal("My policy", loaded.Name);
        Assert.Equal(51, loaded.QualityValue);
        Assert.Equal(0, loaded.MinimumExpectedSavingsPercent);
        Assert.Equal(8, loaded.PreferredBitDepth);
        Assert.Equal(new[] { "h264" }, loaded.IncludedSourceCodecs);

        loaded.Name = "Renamed";
        store.Save(loaded);
        Assert.Equal("Renamed", Assert.Single(store.LoadCustom()).Name);
        LibraryPolicyDefinition clone = store.Clone(loaded.Id, "Second");
        Assert.NotEqual(loaded.Id, clone.Id);
        Assert.Equal(2, store.LoadCustom().Count);
        store.Delete(loaded.Id);
        Assert.Equal(clone.Id, Assert.Single(store.LoadCustom()).Id);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void SafetyAndApplicabilityExclusionsAreExplainable(bool protectedFile, bool exact, bool visual, bool family)
    {
        LibraryPolicyEvaluationResult result = Evaluate(Facts(isProtected: protectedFile, exact: exact, visual: visual, family: family));
        Assert.Equal(LibraryPolicyComplianceState.NotApplicable, result.State);
        Assert.NotEmpty(result.Reasons);
        Assert.Null(result.ProjectedReclaimableBytes);
    }

    [Fact]
    public void MissingMetadataAndUnavailableCapabilityDoNotInventAnAction()
    {
        Assert.Equal(LibraryPolicyComplianceState.UnableToEvaluate,
            Evaluate(Facts(duration: null)).State);
        LibraryPolicyCapabilitySnapshot none = new();
        LibraryPolicyEvaluationResult unsupported = new LibraryPolicyEvaluationEngine().Evaluate(Policy(), Facts(), none);
        Assert.Equal(LibraryPolicyComplianceState.UnableToEvaluate, unsupported.State);
        Assert.Equal(LibraryPolicySuggestedAction.Unsupported, unsupported.SuggestedAction);
        Assert.Contains("not currently available", string.Join(" ", unsupported.Reasons), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingNamedPresetRequiresAttentionWithoutSubstitution()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.EncodingPresetName = "Gone preset";
        LibraryPolicyEvaluationResult result = new LibraryPolicyEvaluationEngine().Evaluate(policy, Facts(), Capabilities());
        Assert.Equal(LibraryPolicyComplianceState.UnableToEvaluate, result.State);
        Assert.Contains("Gone preset", string.Join(" ", result.Reasons));
    }

    [Fact]
    public void NamedPresetSuppliesEffectiveTargetCharacteristics()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.EncodingPresetName = "Archive AV1";
        var preset = new EncodingPreset
        {
            Name = "Archive AV1", EncoderId = VideoEncoderIds.SvtAv1, VideoCodec = nameof(VideoCodecFamily.Av1),
            EncoderPreset = "6", QualityValue = 28, TenBit = true, OutputContainer = nameof(OutputContainerSelection.Matroska),
            CompressionProfile = "High Compression", ScaleMode = "1080p"
        };
        LibraryPolicyCapabilitySnapshot capabilities = Capabilities(VideoCodecFamily.Av1);
        capabilities = new LibraryPolicyCapabilitySnapshot
        {
            AvailableEncoderCodecs = capabilities.AvailableEncoderCodecs,
            TenBitEncoderIds = capabilities.TenBitEncoderIds,
            EncodingPresetNames = new HashSet<string> { preset.Name },
            EncodingPresets = new Dictionary<string, EncodingPreset>(StringComparer.OrdinalIgnoreCase) { [preset.Name] = preset }
        };
        LibraryPolicyEvaluationResult result = new LibraryPolicyEvaluationEngine().Evaluate(policy, Facts(size: 12L * GiB), capabilities);
        Assert.Equal(VideoCodecFamily.Av1, result.ProposedCodec);
        Assert.Equal(VideoEncoderIds.SvtAv1, result.EncoderId);
        Assert.Equal("6", result.EncoderPreset);
        Assert.Equal(OutputContainerSelection.Matroska, result.TargetContainer);
        Assert.False(result.PreserveSourceResolution);
        Assert.Equal(1080, result.MaximumOutputHeight);
    }

    [Fact]
    public void MinimumSizeAndDurationReturnNotApplicable()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.MinimumFileSizeBytes = 10L * GiB;
        Assert.Equal(LibraryPolicyComplianceState.NotApplicable, Evaluate(Facts(size: GiB), policy).State);
        policy.MinimumFileSizeBytes = 0;
        policy.MinimumDurationSeconds = 10_000;
        Assert.Equal(LibraryPolicyComplianceState.NotApplicable, Evaluate(Facts(duration: 3600), policy).State);
    }

    [Fact]
    public void InefficientH264ProducesRoundedProjectionAndCandidate()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.RequireDeepAnalysisForMarginalCases = false;
        policy.MinimumConfidence = LibraryPolicyConfidence.Low;
        LibraryPolicyEvaluationResult result = Evaluate(Facts(size: 12L * GiB, bitrate: 30_000_000), policy);
        Assert.Equal(LibraryPolicyComplianceState.OptimizationCandidate, result.State);
        Assert.Equal(LibraryPolicySuggestedAction.Reencode, result.SuggestedAction);
        Assert.InRange(result.ProjectedOutputBytes!.Value, 1, result.OriginalSizeBytes - 1);
        Assert.Equal(0, result.ProjectedOutputBytes.Value % (1024 * 1024));
        Assert.True(result.ProjectedReclaimableBytes > 0);
        Assert.True(result.ProjectedSavingsPercent > 0);
        Assert.Contains("metadata", result.ProjectionBasis, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("hevc", VideoCodecFamily.Hevc)]
    [InlineData("av1", VideoCodecFamily.Av1)]
    public void AlreadyEfficientModernCodecCanBeCompliant(string sourceCodec, VideoCodecFamily target)
    {
        LibraryPolicyDefinition policy = Policy(target);
        policy.MinimumExpectedSavingsPercent = 80;
        policy.MinimumExpectedSavingsBytes = 20L * GiB;
        LibraryPolicyEvaluationResult result = Evaluate(Facts(codec: sourceCodec, size: GiB, bitrate: 2_000_000), policy, Capabilities(target));
        Assert.Equal(LibraryPolicyComplianceState.Compliant, result.State);
        Assert.Equal(LibraryPolicySuggestedAction.None, result.SuggestedAction);
    }

    [Fact]
    public void EfficientH264CanBeCompliantWhenBenefitIsBelowThreshold()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.MinimumExpectedSavingsPercent = 80;
        policy.MinimumExpectedSavingsBytes = 20L * GiB;
        Assert.Equal(LibraryPolicyComplianceState.Compliant,
            Evaluate(Facts(size: 400L * 1024 * 1024, bitrate: 900_000), policy).State);
    }

    [Fact]
    public void HdrBitDepthLossAndUnreviewedVisualMatchRequireReview()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.PreferredBitDepth = 8;
        LibraryPolicyEvaluationResult hdr = Evaluate(Facts(bitDepth: 10, transfer: "smpte2084"), policy);
        Assert.Equal(LibraryPolicyComplianceState.ReviewRequired, hdr.State);
        Assert.Contains(hdr.ReviewReasons, reason => reason.Contains("bit depth", StringComparison.OrdinalIgnoreCase));
        LibraryPolicyEvaluationResult visual = Evaluate(Facts(unreviewed: true), Policy());
        Assert.Equal(LibraryPolicyComplianceState.ReviewRequired, visual.State);
    }

    [Fact]
    public void TenBitAndExplicitContainerCapabilitiesArePreserved()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.TargetContainer = OutputContainerSelection.Matroska;
        LibraryPolicyEvaluationResult result = Evaluate(Facts(), policy);
        Assert.Equal(10, result.PreferredBitDepth);
        Assert.Equal(OutputContainerSelection.Matroska, result.TargetContainer);

        LibraryPolicyCapabilitySnapshot source = Capabilities();
        LibraryPolicyCapabilitySnapshot noTenBit = new()
        {
            AvailableEncoderCodecs = source.AvailableEncoderCodecs,
            TenBitEncoderIds = new HashSet<string>(),
            EncodingPresetNames = source.EncodingPresetNames
        };
        LibraryPolicyEvaluationResult unsupported = new LibraryPolicyEvaluationEngine().Evaluate(policy, Facts(), noTenBit);
        Assert.Equal(LibraryPolicyComplianceState.UnableToEvaluate, unsupported.State);
    }

    [Fact]
    public void ExplicitMp4WarningsPropagateToReview()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.TargetContainer = OutputContainerSelection.Mp4;
        LibraryPolicyFileFacts facts = Facts(subtitles: new[] { new LibrarySubtitleStreamMetadata("ass", "eng") }, attachments: 1);
        LibraryPolicyEvaluationResult result = Evaluate(facts, policy);
        Assert.Equal(LibraryPolicyComplianceState.ReviewRequired, result.State);
        Assert.Contains(result.ReviewReasons, reason => reason.Contains("MP4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LegacyContainerCanProduceRemuxOnlyCandidate()
    {
        LibraryPolicyDefinition policy = Policy();
        policy.MinimumExpectedSavingsPercent = 80;
        policy.MinimumExpectedSavingsBytes = 20L * GiB;
        LibraryPolicyEvaluationResult result = Evaluate(Facts(path: "legacy.ts", format: "mpegts", codec: "hevc", size: GiB, bitrate: 2_000_000), policy);
        Assert.Equal(LibraryPolicySuggestedAction.RemuxOnly, result.SuggestedAction);
        Assert.Equal(LibraryPolicyComplianceState.OptimizationCandidate, result.State);
        Assert.Equal(0, result.ProjectedReclaimableBytes);
    }

    [Fact]
    public void ServiceUsesBoundedBatchesAndPagedResultsForLargeCatalog()
    {
        var catalog = new FakeCatalog(Enumerable.Range(1, 2_345).Select(index => Facts(id: index, path: $"{index}.mkv")).ToArray());
        var service = new LibraryPolicyEvaluationService(catalog);
        var query = new LibraryPolicyResultQuery(Offset: 200, Limit: 75);
        LibraryPolicyDefinition policy = Policy();
        LibraryPolicyCapabilitySnapshot capabilities = Capabilities();
        (LibraryPolicyEvaluationPage page, LibraryPolicyEvaluationSummary summary) = service.Evaluate(policy, query, capabilities);
        Assert.Equal(2_345, summary.FilesEvaluated);
        Assert.Equal(75, page.Results.Count);
        Assert.Equal(201, page.Results[0].FileId);
        Assert.All(catalog.RequestedLimits, limit => Assert.InRange(limit, 1, 500));
        Assert.Equal(new[] { 0, 500, 1000, 1500, 2000 }, catalog.RequestedOffsets);
        int callsAfterFirstPass = catalog.RequestedOffsets.Count;
        service.Evaluate(policy, query, capabilities);
        Assert.Equal(callsAfterFirstPass, catalog.RequestedOffsets.Count);
        service.Evaluate(policy, query with { Offset = 275 }, capabilities);
        Assert.Equal(callsAfterFirstPass + 1, catalog.RequestedOffsets.Count);
        service.Invalidate();
        service.Evaluate(policy, query, capabilities);
        Assert.True(catalog.RequestedOffsets.Count > callsAfterFirstPass + 1);
    }

    [Fact]
    public void SqlitePolicyFactsQueryRunsAgainstCurrentSchemaAndRemainsBounded()
    {
        using var catalog = new SqliteLibraryCatalog(Path.Combine(_root, "policy-catalog.db"),
            Path.Combine(_root, "backups"), Path.Combine(_root, "recovery"));
        catalog.Initialize();
        Assert.Empty(catalog.QueryPolicyFileFacts(0, 50_000));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static LibraryPolicyEvaluationResult Evaluate(LibraryPolicyFileFacts facts, LibraryPolicyDefinition? policy = null,
        LibraryPolicyCapabilitySnapshot? capabilities = null) =>
        new LibraryPolicyEvaluationEngine().Evaluate(policy ?? Policy(), facts, capabilities ?? Capabilities());

    private static LibraryPolicyDefinition Policy(VideoCodecFamily codec = VideoCodecFamily.Hevc) => new()
    {
        Name = "Test", MinimumFileSizeBytes = 0, MinimumDurationSeconds = 0,
        PreferredCodec = codec, EncoderId = codec == VideoCodecFamily.Av1 ? VideoEncoderIds.SvtAv1 : VideoEncoderIds.Libx265,
        EncoderPreset = "slow", PreferredBitDepth = 10, MinimumExpectedSavingsPercent = 20,
        MinimumExpectedSavingsBytes = 100L * 1024 * 1024, MinimumConfidence = LibraryPolicyConfidence.Medium,
        RequireDeepAnalysisForMarginalCases = false
    };

    private static LibraryPolicyCapabilitySnapshot Capabilities(VideoCodecFamily codec = VideoCodecFamily.Hevc)
    {
        string encoder = codec == VideoCodecFamily.Av1 ? VideoEncoderIds.SvtAv1 : VideoEncoderIds.Libx265;
        string ffmpeg = codec == VideoCodecFamily.Av1 ? "libsvtav1" : "libx265";
        return new LibraryPolicyCapabilitySnapshot
        {
            AvailableEncoderCodecs = new Dictionary<string, string> { [LibraryPolicyCapabilitySnapshot.Key(encoder, codec)] = ffmpeg },
            TenBitEncoderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { encoder },
            EncodingPresetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Known" }
        };
    }

    private static LibraryPolicyFileFacts Facts(long id = 1, string path = "video.mkv", string format = "matroska", string codec = "h264",
        long size = 8L * GiB, long? bitrate = 18_000_000, double? duration = 3600, int? bitDepth = 8,
        string transfer = "bt709", bool isProtected = false, bool exact = false, bool visual = false, bool family = false,
        bool unreviewed = false, IReadOnlyList<LibrarySubtitleStreamMetadata>? subtitles = null, int attachments = 0) => new(
            id, path, Path.GetFileName(path), format, codec, "Main", 1920, 1080, 24, size, bitrate, duration,
            bitDepth, "progressive", transfer, "bt709", new[] { new LibraryAudioStreamMetadata("aac", 2, "stereo", "eng") },
            subtitles ?? Array.Empty<LibrarySubtitleStreamMetadata>(), 0, attachments, LibraryProbeStatus.Succeeded, "",
            isProtected, exact, visual, family, unreviewed);

    private const long GiB = 1024L * 1024 * 1024;

    private sealed class FakeCatalog(IReadOnlyList<LibraryPolicyFileFacts> facts) : ILibraryPhase2Catalog
    {
        public List<int> RequestedOffsets { get; } = new();
        public List<int> RequestedLimits { get; } = new();
        public IReadOnlyList<LibraryStorageOptimizationCandidate> QueryStorageOptimizationCandidates(int limit = 500) => Array.Empty<LibraryStorageOptimizationCandidate>();
        public string GetPolicyFactsRevision() => "synthetic-v1";
        public IReadOnlyList<LibraryPolicyFileFacts> QueryPolicyFileFacts(int offset, int limit)
        {
            RequestedOffsets.Add(offset);
            RequestedLimits.Add(limit);
            return facts.Skip(offset).Take(limit).ToArray();
        }
    }
}
