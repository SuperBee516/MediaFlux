using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class PerformanceTimingServiceTests
{
    [Fact]
    public void SuccessfulSummaryIncludesCompletedExecutedStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        Complete(timing, PerformanceTimingStage.FinalEncode);
        Complete(timing, PerformanceTimingStage.OutputValidation);
        Complete(timing, PerformanceTimingStage.Finalization);
        string summary = timing.BuildSummary();

        Assert.Contains("MediaFlux Performance Summary", summary);
        Assert.Contains("Total Elapsed:", summary);
        Assert.Contains("Source Analysis:", summary);
        Assert.Contains("Final Encode:", summary);
        Assert.Contains("Output Validation:", summary);
        Assert.Contains("Finalization:", summary);
        Assert.DoesNotContain("Not Completed", summary);
    }

    [Fact]
    public void AiDisabledSummaryOmitsAiStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        Complete(timing, PerformanceTimingStage.FinalEncode);
        string summary = timing.BuildSummary();

        Assert.DoesNotContain("AI Preparation:", summary);
        Assert.DoesNotContain("AI Extraction:", summary);
        Assert.Contains("Final Encode:", summary);
    }

    [Fact]
    public void AiEnabledSummaryIncludesAccumulatedChunkStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.AiPreparation);
        Complete(timing, PerformanceTimingStage.AiExtraction);
        Complete(timing, PerformanceTimingStage.AiExtraction);
        Complete(timing, PerformanceTimingStage.AiProcessing);
        Complete(timing, PerformanceTimingStage.AiValidation);
        Complete(timing, PerformanceTimingStage.AiReassembly);
        Complete(timing, PerformanceTimingStage.AiIntermediateJoin);
        string summary = timing.BuildSummary();

        Assert.Contains("AI Preparation:", summary);
        Assert.Contains("AI Extraction:", summary);
        Assert.Contains("AI Processing:", summary);
        Assert.Contains("AI Validation:", summary);
        Assert.Contains("AI Reassembly:", summary);
        Assert.Contains("AI Intermediate Join:", summary);
    }

    [Fact]
    public void CancellationMarksUnfinishedStageWithoutHidingCompletedStages()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        using (timing.Measure(PerformanceTimingStage.FinalEncode)) { }
        string summary = timing.BuildSummary();

        Assert.Contains("Source Analysis:", summary);
        Assert.Contains("Final Encode: Not Completed", summary);
    }

    [Fact]
    public void FailureMarksTheFailingStageNotCompleted()
    {
        var timing = new PerformanceTimingService();
        using (timing.Measure(PerformanceTimingStage.OutputValidation)) { }

        Assert.Contains("Output Validation: Not Completed", timing.BuildSummary());
    }

    [Fact]
    public void MultipleExecutedStagesAreRenderedInPipelineOrder()
    {
        var timing = new PerformanceTimingService();
        Complete(timing, PerformanceTimingStage.FinalEncode);
        Complete(timing, PerformanceTimingStage.SourceAnalysis);
        string summary = timing.BuildSummary();

        Assert.True(summary.IndexOf("Source Analysis:", StringComparison.Ordinal) < summary.IndexOf("Final Encode:", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentScopesAreThreadSafe()
    {
        var timing = new PerformanceTimingService();
        Parallel.For(0, 128, _ => Complete(timing, PerformanceTimingStage.AiProcessing));
        string summary = timing.BuildSummary();

        Assert.Contains("AI Processing:", summary);
        Assert.DoesNotContain("AI Processing: Not Completed", summary);
    }

    private static void Complete(PerformanceTimingService timing, PerformanceTimingStage stage)
    {
        using PerformanceTimingService.PerformanceScope scope = timing.Measure(stage);
        scope.Complete();
    }
}
