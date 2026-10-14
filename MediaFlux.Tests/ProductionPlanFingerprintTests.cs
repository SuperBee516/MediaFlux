using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class ProductionPlanFingerprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-ProductionPlan", Guid.NewGuid().ToString("N"));
    private readonly string _source;

    public ProductionPlanFingerprintTests()
    {
        Directory.CreateDirectory(_root);
        _source = Path.Combine(_root, "source.mkv");
        File.WriteAllBytes(_source, [1, 2, 3, 4]);
    }

    [Fact]
    public void UnchangedPlanHasStableFingerprintAndBoundEstimate()
    {
        ProductionPlanFingerprintInput input = Input();
        ProductionPlanIdentity first = ProductionPlanFingerprintService.Create(input, [_source]);
        ProductionPlanIdentity second = ProductionPlanFingerprintService.Create(input, [_source]);
        var estimate = new PlanBoundEstimate(first, 7, "metadata-arithmetic", 500_000,
            EstimateAuthorityLevel.Preliminary, Confidence: "Low");

        Assert.Equal(first, second);
        Assert.True(estimate.Matches(second));
        Assert.Equal(EstimateAuthorityLevel.Preliminary, estimate.Authority);
    }

    [Fact]
    public void ChangingQualityEncoderPresetOrBitDepthChangesFingerprint()
    {
        ProductionPlanFingerprintInput input = Input();
        ProductionPlanIdentity baseline = ProductionPlanFingerprintService.Create(input, [_source]);

        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { QualityValue = 25 }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { EncoderId = "libx265", HardwareImplementation = "software" }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { Preset = "p6" }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { BitDepth = 10 }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { OutputWidth = 1280 }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { OutputHeight = 720 }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { QualityIntent = "automatic:highquality" }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { RateControlMode = "target-size" }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { GeometryMode = "custom" }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { Container = "mp4" }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { AudioMode = "stereo-transcode", AudioChannels = 2, AudioBitrateKbps = 192 }, [_source]).PlanFingerprint);
        Assert.NotEqual(baseline.PlanFingerprint,
            ProductionPlanFingerprintService.Create(input with { SubtitleMode = "omit", CopyDataStreams = false, CopyAttachments = false, StreamMapMode = "first-audio-only" }, [_source]).PlanFingerprint);
    }

    [Fact]
    public void StorageSavingsPolicyIsPartOfTheProductionFingerprint()
    {
        ProductionPlanFingerprintInput input = Input() with
        {
            StorageSavingsPolicy = "policy:enabled;maximum=500000000"
        };
        ProductionPlanIdentity baseline = ProductionPlanFingerprintService.Create(input, [_source]);
        ProductionPlanIdentity changedPolicy = ProductionPlanFingerprintService.Create(
            input with { StorageSavingsPolicy = "policy:enabled;maximum=450000000" }, [_source]);

        Assert.NotEqual(baseline.PlanFingerprint, changedPolicy.PlanFingerprint);
    }

    [Fact]
    public void SourceFileChangeInvalidatesEstimateBinding()
    {
        ProductionPlanIdentity identity = ProductionPlanFingerprintService.Create(Input(), [_source]);
        var estimate = new PlanBoundEstimate(identity, 1, "metadata-arithmetic", 100,
            EstimateAuthorityLevel.Preliminary);
        File.WriteAllBytes(_source, [1, 2, 3, 4, 5]);
        ProductionPlanIdentity changedSource = ProductionPlanFingerprintService.Create(Input(), [_source]);

        Assert.NotEqual(identity.SourceIdentity, changedSource.SourceIdentity);
        Assert.False(estimate.Matches(changedSource));
    }

    [Fact]
    public void QualityResolutionCanRefinePlanIdentityWithoutChangingSourceBinding()
    {
        ProductionPlanIdentity identity = ProductionPlanFingerprintService.Create(Input(), [_source]);
        ProductionPlanIdentity resolved = ProductionPlanFingerprintService.WithQuality(identity, 26);

        Assert.Equal(identity.SourceIdentity, resolved.SourceIdentity);
        Assert.NotEqual(identity.PlanFingerprint, resolved.PlanFingerprint);
        Assert.Equal(26, resolved.Inputs.QualityValue);
    }

    [Fact]
    public void ManualNumericIntentKeepsCanonicalFingerprintIntentText()
    {
        var context = new EncodingDecisionContext(
            new MediaProbeResult
            {
                Success = true,
                Streams = [new MediaProbeStreamInfo
                {
                    Index = 0, CodecType = "video", CodecName = "hevc",
                    Width = 1920, Height = 1080, FrameRate = 30, BitRate = 8_000_000
                }]
            },
            EncodingInputSource.FromFile(_source),
            new VideoEncoderSelection(VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc, "hevc_nvenc"),
            UseGpu: true,
            TargetMb: null,
            ScaleMode: EncodingService.ScaleMode.None,
            Restoration: new VideoRestorationSettings(),
            EncoderPreset: "p5",
            QualityValue: 24,
            TenBit: false,
            AudioChannels: null,
            MapMode: EncodingService.StreamMapMode.KeepAll,
            CopySubtitles: true,
            CopyDataStreams: true,
            CopyAttachments: true,
            ContainerConfigured: OutputContainerSelection.Matroska,
            CompatibilityPolicy: ContainerCompatibilityPolicy.Intelligent,
            KnownDuration: TimeSpan.FromSeconds(10),
            QualityIntent: EncodingQualityIntent.ManualNumeric(24));
        EncodingPlan plan = EncodingPlanService.Create(context);
        ProductionPlanIdentity identity = ProductionPlanFingerprintService.Create(
            context, EncodingPlanService.GetExecutionValues(plan));

        Assert.Equal("manual-numeric", identity.Inputs.QualityIntent);
    }

    private static ProductionPlanFingerprintInput Input() => new(
        "hevc_nvenc", "hevc", "cq", "manual-numeric", 24, "p5", 8, "none", null,
        "preserve-source-timing", "{}", "copy-compatible", null, null, "copy-compatible",
        true, true, "matroska", "intelligent", "hardware", false, "keep-all", null);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
