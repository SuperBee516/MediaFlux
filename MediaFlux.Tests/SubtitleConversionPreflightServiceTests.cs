using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SubtitleConversionPreflightServiceTests
{
    [Fact]
    public async Task MalformedPlannedTextSubtitleIsIdentified()
    {
        var runner = new FakeRunner(new MediaToolProcessResult { ExitCode = 1, StandardError = "Invalid subtitle timestamp" });
        SubtitleConversionPreflightResult result = await new SubtitleConversionPreflightService("ffmpeg.exe", runner)
            .ValidateAsync(Input(), Decision());

        Assert.False(result.Success);
        Assert.Equal(3, result.StreamIndex);
        Assert.Contains("malformed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mov_text", runner.Request.Arguments);
    }

    [Fact]
    public async Task NormalPlannedTextSubtitlePasses()
    {
        var runner = new FakeRunner(new MediaToolProcessResult());
        SubtitleConversionPreflightResult result = await new SubtitleConversionPreflightService("ffmpeg.exe", runner)
            .ValidateAsync(Input(), Decision());

        Assert.True(result.Success);
    }

    [Fact]
    public async Task MalformedCompatibleCopiedTextSubtitleIsAlsoValidated()
    {
        var runner = new FakeRunner(new MediaToolProcessResult { ExitCode = 1 });
        OutputContainerDecision decision = new()
        {
            Requested = OutputContainerSelection.Mp4,
            Resolved = OutputContainer.Mp4,
            Reason = "test",
            StreamPlans = new[] { new StreamCompatibilityPlan(3, "subtitle", "mov_text", StreamCompatibilityAction.Copy, "test") }
        };

        SubtitleConversionPreflightResult result = await new SubtitleConversionPreflightService("ffmpeg.exe", runner)
            .ValidateAsync(Input(), decision);

        Assert.False(result.Success);
        Assert.Equal(3, result.StreamIndex);
        Assert.Contains("mov_text", runner.Request.Arguments);
    }

    private static EncodingInputSource Input() => EncodingInputSource.FromFile("source.mkv");
    private static OutputContainerDecision Decision() => new()
    {
        Requested = OutputContainerSelection.Mp4,
        Resolved = OutputContainer.Mp4,
        Reason = "test",
        StreamPlans = new[] { new StreamCompatibilityPlan(3, "subtitle", "ass", StreamCompatibilityAction.Transcode, "test", "mov_text") }
    };

    private sealed class FakeRunner(MediaToolProcessResult result) : IMediaToolProcessRunner
    {
        public MediaToolProcessRequest Request { get; private set; } = new();
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default) { Request = request; return Task.FromResult(result); }
    }
}
