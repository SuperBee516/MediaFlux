using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodingIntelligencePresentationTests
{
    [Fact]
    public void AutoContainerAndStreamsRenderAsConcisePlannedOutput()
    {
        var plan = Plan(
            container: new(OutputContainerSelection.Auto, OutputContainer.Matroska, "ASS subtitles require Matroska."),
            audio: [new(1, "audio", "eac3", StreamCompatibilityAction.Copy, null, "Compatible")],
            subtitles: [new(2, "subtitle", "ass", StreamCompatibilityAction.Transcode, "subrip", "Convert")]);

        EncodingIntelligencePresentation.Model result = EncodingIntelligencePresentation.Create(plan);

        string output = Item(result, "Planned output");
        Assert.Contains("Auto → Matroska", output);
        Assert.Contains("Encoder: nvenc (HEVC) · GPU", output);
        Assert.Contains("Audio: copy 1", output);
        Assert.Contains("Subtitles: convert 1", output);
    }

    [Fact]
    public void HistoricalPredictionIsClearlySeparateFromAuthoritativeTarget()
    {
        var plan = Plan(estimates: new(0, 4915, null, new(18, 1, EncodingHistoricalConfidence.High, 2, 1.8, 2.2, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11), 4600, 4500, 4900, .58, "test")));

        EncodingIntelligencePresentation.Model result = EncodingIntelligencePresentation.Create(plan);

        Assert.Equal("4.8 GB", Item(result, "Target"));
        Assert.Contains("Historical size: 4.39 GB–4.79 GB", Item(result, "Expected result"));
        Assert.Contains("High confidence · 18 comparable jobs", Item(result, "Expected result"));
    }

    [Fact]
    public void InsufficientHistoryAndRecoveryOutcomeRemainDistinct()
    {
        var plan = Plan(estimates: new(null, null, null, new(0, 0, EncodingHistoricalConfidence.None, null, null, null, null, null, null, null, null, null, null, "history")));
        var outcome = new EncodingExecutionOutcome(plan.PlanId, [], [new(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption, EncodingRecoveryMode.Strict, EncodingRecoveryMode.Tolerant, 1, 1, EncodingRecoveryResult.Succeeded)], TerminalResult: EncodingTerminalResult.CompletedAfterRecovery);

        EncodingIntelligencePresentation.Model result = EncodingIntelligencePresentation.Create(plan, outcome);

        Assert.Equal("Not enough comparable history yet", Item(result, "Expected result"));
        Assert.Contains("Recovered from localized source video corruption", Item(result, "Recovery"));
        Assert.Contains("Completed after recovery", Item(result, "Lifecycle"));
    }

    [Fact]
    public void ReasonsAndFinalizationFailureUseFriendlyText()
    {
        var plan = Plan(reasons: [new(EncodingDecisionReasonCode.GeometryNormalized, "1920×1081 was normalized to 1920×1080.")]);
        var outcome = new EncodingExecutionOutcome(plan.PlanId, [], [], TerminalResult: EncodingTerminalResult.FinalizationFailed);

        EncodingIntelligencePresentation.Model result = EncodingIntelligencePresentation.Create(plan, outcome);

        Assert.Contains("Output dimensions were normalized", Assert.Single(result.Reasons).Value);
        Assert.Equal("Finalization failed", Item(result, "Lifecycle"));
    }

    [Fact]
    public void PreflightAndValidationStatesUseMeaningfulLifecycleText()
    {
        var plan = Plan();
        var preflight = new EncodingExecutionOutcome(plan.PlanId,
            [new(EncodingPreflightCheckKind.SourceProbe, EncodingPreflightStatus.Passed)], []);
        var validationFailure = new EncodingExecutionOutcome(plan.PlanId, [], [],
            new EncodingValidationOutcome(EncodingLifecycleStatus.Failed, EncodingLifecycleStatus.Failed,
                EncodingLifecycleStatus.NotRun, EncodingLifecycleStatus.NotRun, EncodingLifecycleStatus.NotRun,
                EncodingLifecycleStatus.NotRun), TerminalResult: EncodingTerminalResult.ValidationFailed);

        Assert.Equal("Preflight passed", Item(EncodingIntelligencePresentation.Create(plan, preflight), "Lifecycle"));
        Assert.Equal("Validation failed", Item(EncodingIntelligencePresentation.Create(plan, validationFailure), "Lifecycle"));
    }

    [Fact]
    public void WarningsAndFutureReasonCodesDegradeGracefully()
    {
        var plan = Plan(
            reasons: [new((EncodingDecisionReasonCode)999, "")],
            risks: [new(EncodingRiskSeverity.Warning, EncodingRiskCategory.SourceDecode, "decode", "Source timing needs attention.")]);

        EncodingIntelligencePresentation.Model result = EncodingIntelligencePresentation.Create(plan);

        Assert.Equal("1 warning: Source timing needs attention.", Item(result, "Source health"));
        Assert.Equal("A configured planning decision applies.", Assert.Single(result.Reasons).Value);
    }

    [Fact]
    public void PresentationKeyIgnoresProgressButChangesForLifecycle()
    {
        var plan = Plan();
        var preflight = new EncodingExecutionOutcome(plan.PlanId,
            [new(EncodingPreflightCheckKind.SourceProbe, EncodingPreflightStatus.Passed)], []);
        var recovered = new EncodingExecutionOutcome(plan.PlanId,
            [new(EncodingPreflightCheckKind.SourceProbe, EncodingPreflightStatus.Passed)],
            [new(EncodingRecoveryKind.VideoDecode, EncodingRecoveryFailureClass.SourceVideoCorruption,
                EncodingRecoveryMode.Strict, EncodingRecoveryMode.Tolerant, 1, 1, EncodingRecoveryResult.Succeeded)]);

        Assert.Equal(EncodingIntelligencePresentation.GetKey(plan, preflight),
            EncodingIntelligencePresentation.GetKey(plan, preflight));
        Assert.NotEqual(EncodingIntelligencePresentation.GetKey(plan, preflight),
            EncodingIntelligencePresentation.GetKey(plan, recovered));
    }

    private static string Item(EncodingIntelligencePresentation.Model model, string label) => Assert.Single(model.Summary, item => item.Label == label).Value;
    private static EncodingPlan Plan(
        EncodingPlanContainer? container = null,
        IReadOnlyList<EncodingPlanStream>? audio = null,
        IReadOnlyList<EncodingPlanStream>? subtitles = null,
        EncodingPlanEstimates? estimates = null,
        IReadOnlyList<EncodingDecisionReason>? reasons = null,
        IReadOnlyList<EncodingRisk>? risks = null) => new()
    {
        IsAvailable = true,
        Source = new("h264", 1920, 1080, 24, 600),
        Video = new("Reencode", "hevc_nvenc", "nvenc", 1920, 1080, 1920, 1080, "yuv420p"),
        Hardware = new(true, "nvenc", true),
        Container = container,
        Audio = audio ?? Array.Empty<EncodingPlanStream>(),
        Subtitles = subtitles ?? Array.Empty<EncodingPlanStream>(),
        Estimates = estimates ?? new(null, null, null),
        DecisionReasons = reasons ?? Array.Empty<EncodingDecisionReason>(),
        Risks = risks ?? Array.Empty<EncodingRisk>(),
        RecoveryCapabilities = new([new(EncodingRecoveryKind.VideoDecode, EncodingRecoveryMode.Strict, true, 1, [EncodingRecoveryFailureClass.SourceVideoCorruption], [], "")])
    };
}
