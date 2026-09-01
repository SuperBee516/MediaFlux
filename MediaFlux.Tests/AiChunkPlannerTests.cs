using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiChunkPlannerTests
{
    private readonly AiChunkPlanner _planner = new();

    [Theory]
    [InlineData(640, 480, 720)]
    [InlineData(1280, 720, 360)]
    [InlineData(1920, 1080, 180)]
    [InlineData(3840, 2160, 60)]
    public void PlansResolutionAppropriateChunksWithHighVram(int width, int height, int expected)
        => Assert.Equal(expected, Plan(width, height, AiRestorationScale.X2, 16).FrameCount);

    [Fact]
    public void UnknownGpuUsesConservativeChunk() => Assert.Equal(180, Plan(640, 480, AiRestorationScale.X2, null).FrameCount);

    [Fact]
    public void LowVramCapsChunk() => Assert.Equal(90, Plan(640, 480, AiRestorationScale.X2, 2).FrameCount);

    [Fact]
    public void HighVramAllowsLargerSdChunk() => Assert.Equal(720, Plan(640, 480, AiRestorationScale.X2, 16).FrameCount);

    [Fact]
    public void PlannerHonorsMinimumAndMaximumBounds()
    {
        Assert.Equal(AiChunkPlanner.MinimumFramesPerChunk, Plan(7680, 4320, AiRestorationScale.X4, 2).FrameCount);
        Assert.Equal(AiChunkPlanner.MaximumFramesPerChunk, Plan(320, 240, AiRestorationScale.X1, 24).FrameCount);
    }

    [Fact]
    public void PlannerIsDeterministic()
    {
        AiChunkPlannerInput input = Input(1920, 1080, AiRestorationScale.X3, 8);
        Assert.Equal(_planner.Plan(input), _planner.Plan(input));
    }

    [Fact]
    public void StorageLimitCapsOtherwiseLargerChunk()
    {
        var storage = new AiTemporaryStorageEstimate(0, 301, false, false, 1, 1);
        AiChunkPlan plan = _planner.Plan(new(640, 480, AiRestorationScale.X2, 16L * Gibibyte, storage, "ncnn-vulkan"));
        Assert.Equal(300, plan.FrameCount);
        Assert.Contains("temporary storage", plan.DecisionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecisionReportShowsStorageConstraintAndEstimates()
    {
        var storage = new AiTemporaryStorageEstimate(0, 301, false, false, 1, 1);
        var input = new AiChunkPlannerInput(640, 480, AiRestorationScale.X2, 16L * Gibibyte, storage, "ncnn-vulkan");
        AiChunkPlannerDecision decision = _planner.DescribeDecision(input, _planner.Plan(input));

        Assert.Equal(720, decision.DefaultChunkSize);
        Assert.Equal(300, decision.StorageLimitedChunkSize);
        Assert.Equal(AiChunkPlanner.MaximumFramesPerChunk, decision.VramLimitedChunkSize);
        Assert.Equal(300, decision.FinalSelectedChunkSize);
        Assert.Equal("Temporary storage", decision.DeterminingConstraint);
        Assert.Equal(300, decision.EstimatedTemporaryStoragePerChunk);
    }

    [Fact]
    public void DecisionReportShowsVramConstraintWhenStorageIsNotLimiting()
    {
        AiChunkPlannerInput input = Input(640, 480, AiRestorationScale.X2, 2);
        AiChunkPlannerDecision decision = _planner.DescribeDecision(input, _planner.Plan(input));

        Assert.Equal(90, decision.VramLimitedChunkSize);
        Assert.Equal(90, decision.FinalSelectedChunkSize);
        Assert.Equal("GPU VRAM", decision.DeterminingConstraint);
    }

    [Fact]
    public void DecisionReportRecordsUnavailableGpuConservativeLimit()
    {
        AiChunkPlannerInput input = Input(640, 480, AiRestorationScale.X2, null);
        AiChunkPlannerDecision decision = _planner.DescribeDecision(input, _planner.Plan(input));

        Assert.Null(decision.DedicatedGpuVramBytes);
        Assert.Equal(180, decision.VramLimitedChunkSize);
        Assert.Equal("GPU VRAM", decision.DeterminingConstraint);
    }

    [Fact]
    public void PreviewSizedOperationRemainsOneChunkWhenItFitsThePlan()
    {
        AiChunkPlan plan = Plan(1920, 1080, AiRestorationScale.X2, 16);
        Assert.True(150 <= plan.FrameCount);
    }

    private AiChunkPlan Plan(int width, int height, AiRestorationScale scale, int? vramGiB)
        => _planner.Plan(Input(width, height, scale, vramGiB));

    private static AiChunkPlannerInput Input(int width, int height, AiRestorationScale scale, int? vramGiB)
        => new(width, height, scale, vramGiB is int value ? value * Gibibyte : null,
            new AiTemporaryStorageEstimate(0, long.MaxValue, false, false, 0, 0), "ncnn-vulkan");

    private const long Gibibyte = 1024L * 1024 * 1024;
}
