using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SmartEncodeDecisionServiceTests
{
    private readonly SmartEncodeDecisionService _service = new();

    [Theory]
    [InlineData(600, SmartEncodeRecommendationKind.StrongCandidate)]
    [InlineData(800, SmartEncodeRecommendationKind.ModerateCandidate)]
    [InlineData(900, SmartEncodeRecommendationKind.Skip)]
    [InlineData(1000, SmartEncodeRecommendationKind.Skip)]
    public void SavingsClassifiesCandidatesAgainstConfiguredMinimum(
        double estimatedOutputMb,
        SmartEncodeRecommendationKind expected)
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(),
            DefaultIntent(estimatedOutputMb));

        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public void CustomMinimumSavingsControlsSkipBoundary()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(),
            DefaultIntent(estimatedOutputMb: 800, minimumSavingsPercent: 25));

        Assert.Equal(SmartEncodeRecommendationKind.Skip, result.Kind);
        Assert.Contains("25", result.PrimaryReason);
    }

    [Theory]
    [InlineData("tt")]
    [InlineData("bb")]
    [InlineData("tb")]
    [InlineData("bt")]
    public void InterlacedSourceRequiresReview(string fieldOrder)
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(fieldOrder: fieldOrder),
            DefaultIntent(estimatedOutputMb: 500));

        Assert.Equal(SmartEncodeRecommendationKind.Review, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("interlaced", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UpscalingRequiresReviewEvenWhenSavingsLookStrong()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(height: 720),
            DefaultIntent(estimatedOutputMb: 500, targetHeight: 1080));

        Assert.Equal(SmartEncodeRecommendationKind.Review, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("above the source", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AudioHeavySourceRequiresReview()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(
                videoBitrateKbps: 500,
                totalBitrateKbps: 1000,
                audioBitrateKbps: 500,
                audioStreamCount: 2),
            DefaultIntent(estimatedOutputMb: 500));

        Assert.Equal(SmartEncodeRecommendationKind.Review, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("Audio accounts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MoreEfficientSourceToLessEfficientTargetRequiresReview()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(videoCodec: "av1"),
            DefaultIntent(estimatedOutputMb: 700, targetCodec: "libx264"));

        Assert.Equal(SmartEncodeRecommendationKind.Review, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("more storage-efficient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EfficientLowBitrateSourceExplainsWhySavingsAreQuestionable()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(videoCodec: "hevc", videoBitrateKbps: 1200),
            DefaultIntent(estimatedOutputMb: 900, targetCodec: "hevc_nvenc"));

        Assert.Equal(SmartEncodeRecommendationKind.Skip, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("already low", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("already uses", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LikelyAnimationRequiresProfileReview()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(isLikelyAnimation: true),
            DefaultIntent(estimatedOutputMb: 500));

        Assert.Equal(SmartEncodeRecommendationKind.Review, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("animation", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"C:\Videos\legacy.avi", "avi")]
    [InlineData(@"C:\Videos\capture.ts", "mpegts")]
    [InlineData(@"C:\Videos\capture.m2ts", "mpegts")]
    public void EfficientVideoInLegacyContainerCanBeRemuxedInstead(
        string path,
        string formatName)
    {
        SmartEncodeSourceInfo source = DefaultSource(
            videoCodec: "h264",
            path: path,
            formatName: formatName);

        SmartEncodeRecommendation result = Evaluate(
            source,
            DefaultIntent(estimatedOutputMb: 900));

        Assert.Equal(SmartEncodeRecommendationKind.RemuxOnly, result.Kind);
        Assert.Contains(
            result.Reasons,
            reason => reason.Contains("MKV", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ModernContainerDoesNotProduceRemuxOnlyRecommendation()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(
                videoCodec: "h264",
                path: @"C:\Videos\source.mkv",
                formatName: "matroska"),
            DefaultIntent(estimatedOutputMb: 900));

        Assert.Equal(SmartEncodeRecommendationKind.Skip, result.Kind);
    }

    [Fact]
    public void InterlaceReviewTakesPriorityOverLegacyContainerRemux()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(
                videoCodec: "h264",
                fieldOrder: "tt",
                path: @"C:\Videos\source.avi",
                formatName: "avi"),
            DefaultIntent(estimatedOutputMb: 900));

        Assert.Equal(SmartEncodeRecommendationKind.Review, result.Kind);
    }

    [Fact]
    public void MissingEssentialMetadataIsUnavailableInsteadOfInvented()
    {
        SmartEncodeRecommendation result = Evaluate(
            DefaultSource(width: 0),
            DefaultIntent(estimatedOutputMb: 500));

        Assert.Equal(SmartEncodeRecommendationKind.Unavailable, result.Kind);
        Assert.Equal(SmartEncodeConfidence.Low, result.Confidence);
    }

    [Fact]
    public void RecommendationPreferencesRoundTripThroughConfig()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-SmartEncodeConfigTests",
            Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "config.json");

        try
        {
            var config = new Config
            {
                SmartRecommendationsEnabled = false,
                MinimumExpectedSavingsPercent = 22.5,
                WarnBeforeEncodingSkippedOrReviewItems = false,
                ShowRecommendationColumn = false
            };

            config.Save(path);
            Config loaded = Config.Load(path);

            Assert.False(loaded.SmartRecommendationsEnabled);
            Assert.Equal(22.5, loaded.MinimumExpectedSavingsPercent);
            Assert.False(loaded.WarnBeforeEncodingSkippedOrReviewItems);
            Assert.False(loaded.ShowRecommendationColumn);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private SmartEncodeRecommendation Evaluate(
        SmartEncodeSourceInfo source,
        SmartEncodeIntent intent)
    {
        return _service.Evaluate(source, intent);
    }

    private static SmartEncodeSourceInfo DefaultSource(
        int width = 1920,
        int height = 1080,
        string videoCodec = "h264",
        string fieldOrder = "progressive",
        int videoBitrateKbps = 5000,
        int totalBitrateKbps = 5200,
        int audioBitrateKbps = 192,
        int audioStreamCount = 1,
        bool isLikelyAnimation = false,
        string path = @"C:\Videos\source.mkv",
        string formatName = "matroska")
    {
        return new SmartEncodeSourceInfo
        {
            Path = path,
            SourceMb = 1000,
            DurationSeconds = 1800,
            Width = width,
            Height = height,
            FramesPerSecond = 30,
            VideoBitrateKbps = videoBitrateKbps,
            TotalBitrateKbps = totalBitrateKbps,
            AudioBitrateKbps = audioBitrateKbps,
            VideoStreamCount = 1,
            AudioStreamCount = audioStreamCount,
            SubtitleStreamCount = 1,
            VideoCodec = videoCodec,
            FormatName = formatName,
            FieldOrder = fieldOrder,
            IsLikelyAnimation = isLikelyAnimation
        };
    }

    private static SmartEncodeIntent DefaultIntent(
        double estimatedOutputMb,
        string targetCodec = "hevc_nvenc",
        int? targetHeight = null,
        double minimumSavingsPercent = 15)
    {
        return new SmartEncodeIntent
        {
            TargetCodec = targetCodec,
            TargetHeight = targetHeight,
            EstimatedOutputMb = estimatedOutputMb,
            MinimumSavingsPercent = minimumSavingsPercent
        };
    }
}
