using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DeepMediaAnalysisServiceTests
{
    [Fact]
    public void ParsesAndAggregatesIdetMultiFrameStatistics()
    {
        string output =
            "[Parsed_idet_0] Multi frame detection: TFF: 12 BFF: 3 Progressive: 80 Undetermined: 5\n" +
            "[Parsed_idet_0] Multi frame detection: TFF: 2 BFF: 1 Progressive: 20 Undetermined: 0";

        bool parsed = DeepMediaAnalysisService.TryParseIdet(
            output,
            out int interlaced,
            out int progressive);

        Assert.True(parsed);
        Assert.Equal(18, interlaced);
        Assert.Equal(100, progressive);
    }

    [Fact]
    public void AnalyzesBinaryPpmWithoutTreatingPixelWhitespaceAsHeader()
    {
        byte[] pixels =
        {
            10, 10, 10,
            255, 255, 255,
            0, 0, 0,
            255, 0, 0
        };
        byte[] header = Encoding.ASCII.GetBytes("P6\n2 2\n255\n");
        byte[] ppm = header.Concat(pixels).ToArray();

        bool parsed = DeepMediaAnalysisService.TryAnalyzePpm(
            ppm,
            out DeepMediaAnalysisService.FrameEvidence evidence);

        Assert.True(parsed);
        Assert.Equal(3, evidence.QuantizedColorCount);
        Assert.True(evidence.EdgeDensity > 0);
    }

    [Fact]
    public void LargeProjectionDisagreementMovesCandidateToReview()
    {
        var baseline = new SmartEncodeRecommendation
        {
            Kind = SmartEncodeRecommendationKind.StrongCandidate,
            Confidence = SmartEncodeConfidence.Medium,
            EstimatedSavingsMb = 500,
            EstimatedSavingsPercent = 50,
            PrimaryReason = "Significant savings.",
            Reasons = new[] { "Significant savings." }
        };
        var analysis = new DeepMediaAnalysisResult
        {
            ProjectedOutputMb = 900,
            InterlaceStatus = SampledInterlaceStatus.Progressive
        };

        SmartEncodeRecommendation refined =
            new SmartEncodeDecisionService().RefineWithDeepAnalysis(
                baseline,
                analysis,
                SmartEncodeContentHint.Auto,
                intendedOutputMb: 500);

        Assert.Equal(SmartEncodeRecommendationKind.Review, refined.Kind);
        Assert.Equal(SmartEncodeConfidence.Medium, refined.Confidence);
        Assert.Contains(
            refined.Reasons,
            reason => reason.Contains("80%", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SampledInterlaceAlwaysRequiresReview()
    {
        var baseline = Candidate();
        var analysis = new DeepMediaAnalysisResult
        {
            ProjectedOutputMb = 500,
            InterlaceStatus = SampledInterlaceStatus.Interlaced,
            InterlacedFrames = 50,
            ProgressiveFrames = 10
        };

        SmartEncodeRecommendation refined =
            new SmartEncodeDecisionService().RefineWithDeepAnalysis(
                baseline,
                analysis,
                SmartEncodeContentHint.Auto,
                intendedOutputMb: 500);

        Assert.Equal(SmartEncodeRecommendationKind.Review, refined.Kind);
        Assert.Equal(SmartEncodeConfidence.High, refined.Confidence);
        Assert.Contains(
            refined.Reasons,
            reason => reason.Contains("deinterlace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LiveActionHintOverridesConservativeSyntheticHeuristic()
    {
        var analysis = new DeepMediaAnalysisResult
        {
            ProjectedOutputMb = 500,
            InterlaceStatus = SampledInterlaceStatus.Progressive,
            PossibleSyntheticContent = true
        };

        SmartEncodeRecommendation refined =
            new SmartEncodeDecisionService().RefineWithDeepAnalysis(
                Candidate(),
                analysis,
                SmartEncodeContentHint.LiveAction,
                intendedOutputMb: 500);

        Assert.Equal(SmartEncodeRecommendationKind.StrongCandidate, refined.Kind);
        Assert.Contains(
            refined.Reasons,
            reason => reason.Contains("overrides", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(SmartEncodeContentHint.Animation)]
    [InlineData(SmartEncodeContentHint.ScreenContent)]
    public void ExplicitSyntheticContentHintRequiresReview(
        SmartEncodeContentHint hint)
    {
        SmartEncodeRecommendation refined =
            new SmartEncodeDecisionService().RefineWithDeepAnalysis(
                Candidate(),
                new DeepMediaAnalysisResult
                {
                    ProjectedOutputMb = 500,
                    InterlaceStatus = SampledInterlaceStatus.Progressive
                },
                hint,
                intendedOutputMb: 500);

        Assert.Equal(SmartEncodeRecommendationKind.Review, refined.Kind);
        Assert.Contains(
            refined.Reasons,
            reason => reason.Contains("content-specific", StringComparison.OrdinalIgnoreCase));
    }

    private static SmartEncodeRecommendation Candidate()
    {
        return new SmartEncodeRecommendation
        {
            Kind = SmartEncodeRecommendationKind.StrongCandidate,
            Confidence = SmartEncodeConfidence.Medium,
            EstimatedSavingsMb = 500,
            EstimatedSavingsPercent = 50,
            PrimaryReason = "Significant savings.",
            Reasons = new[] { "Significant savings." }
        };
    }
}
