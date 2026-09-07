using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SourceAudioDecodePreflightServiceTests
{
    [Fact]
    public async Task CorruptPrimaryCopiedAudioFailsBeforeEncode()
    {
        var runner = new FakeRunner(new MediaToolProcessResult { ExitCode = 1, StandardError = "Error while decoding stream #0:1: Invalid data found when processing input" });
        SourceAudioDecodePreflightResult result = await Service(runner).ValidateCopiedStreamsAsync(Input(), Decision(1));

        Assert.False(result.Success);
        Assert.Equal(1, result.StreamIndex);
        Assert.True(result.IsReliableCorruption);
        Assert.Contains("primary selected", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptSecondaryCopiedAudioFailsRatherThanBeingSilentlyDropped()
    {
        var runner = new FakeRunner(new MediaToolProcessResult(), new MediaToolProcessResult { ExitCode = 1, StandardError = "Header missing" });
        SourceAudioDecodePreflightResult result = await Service(runner).ValidateCopiedStreamsAsync(Input(), Decision(1, 2));

        Assert.False(result.Success);
        Assert.Equal(2, result.StreamIndex);
        Assert.Contains("secondary selected", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task NormalCopiedAudioPassesAndUsesStrictDecodeArguments()
    {
        var runner = new FakeRunner(new MediaToolProcessResult());
        SourceAudioDecodePreflightResult result = await Service(runner).ValidateCopiedStreamsAsync(Input(), Decision(1));

        Assert.True(result.Success);
        Assert.Contains("-xerror", runner.Requests.Single().Arguments);
        Assert.Contains("explode", runner.Requests.Single().Arguments);
        Assert.Contains("0:1", runner.Requests.Single().Arguments);
    }

    [Fact]
    public async Task TranscodedAudioUsesTheEncodeDecodePathInsteadOfASecondPreflight()
    {
        var runner = new FakeRunner();
        OutputContainerDecision decision = new() { Requested = OutputContainerSelection.Mp4, Resolved = OutputContainer.Mp4, Reason = "test", StreamPlans = new[] { new StreamCompatibilityPlan(1, "audio", "dts", StreamCompatibilityAction.Transcode, "test", "aac") } };

        SourceAudioDecodePreflightResult result = await Service(runner).ValidateCopiedStreamsAsync(Input(), decision);

        Assert.True(result.Success);
        Assert.Empty(runner.Requests);
    }

    private static SourceAudioDecodePreflightService Service(FakeRunner runner) => new("ffmpeg.exe", runner);
    private static EncodingInputSource Input() => EncodingInputSource.FromFile("source.mkv");
    private static OutputContainerDecision Decision(params int[] indexes) => new()
    {
        Requested = OutputContainerSelection.Mp4,
        Resolved = OutputContainer.Mp4,
        Reason = "test",
        StreamPlans = indexes.Select(index => new StreamCompatibilityPlan(index, "audio", "aac", StreamCompatibilityAction.Copy, "test")).ToArray()
    };

    private sealed class FakeRunner(params MediaToolProcessResult[] results) : IMediaToolProcessRunner
    {
        private readonly Queue<MediaToolProcessResult> _results = new(results);
        public List<MediaToolProcessRequest> Requests { get; } = new();
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : new MediaToolProcessResult());
        }
    }
}
