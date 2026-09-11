using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SourceAudioDecodePreflightServiceTests
{
    [Fact]
    public async Task HealthyCopiedAudioDoesNotLaunchAFullDurationPreflight()
    {
        var runner = new FakeRunner();
        SourceAudioDecodePreflightResult result = await new SourceAudioDecodePreflightService("ffmpeg.exe", runner)
            .ValidateCopiedStreamsAsync(EncodingInputSource.FromFile("source.mkv"), Decision(1));

        Assert.True(result.Success);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public void ReliableCorruptionIdentifiesTheAudioStreamFromEncodeDiagnostics()
    {
        const string diagnostics = "Error while decoding stream #0:3: Invalid data found when processing input";

        Assert.True(SourceAudioDecodePreflightService.IsReliableAudioDecodeFailure(diagnostics));
        Assert.Equal(3, SourceAudioDecodePreflightService.FindCorruptAudioStreamIndex(diagnostics));
    }

    [Fact]
    public void RecoveryChangesOnlyTheDamagedCopiedAudioStreamAndPreservesMetadata()
    {
        OutputContainerDecision decision = new()
        {
            Requested = OutputContainerSelection.Mp4,
            Resolved = OutputContainer.Mp4,
            Reason = "test",
            StreamPlans = new[]
            {
                new StreamCompatibilityPlan(1, "audio", "aac", StreamCompatibilityAction.Copy, "copy", Language: "eng", Title: "Main", Dispositions: new Dictionary<string, bool> { ["default"] = true }),
                new StreamCompatibilityPlan(3, "audio", "aac", StreamCompatibilityAction.Copy, "copy", Language: "jpn", Title: "Commentary", Dispositions: new Dictionary<string, bool> { ["forced"] = true })
            }
        };

        OutputContainerDecision recovered = OutputContainerPolicy.RecoverCopiedAudio(decision, 3);
        StreamCompatibilityPlan first = Assert.Single(recovered.StreamPlans, plan => plan.StreamIndex == 1);
        StreamCompatibilityPlan damaged = Assert.Single(recovered.StreamPlans, plan => plan.StreamIndex == 3);

        Assert.Equal(StreamCompatibilityAction.Copy, first.Action);
        Assert.Equal(StreamCompatibilityAction.Transcode, damaged.Action);
        Assert.Equal("aac", damaged.TargetCodec);
        Assert.Equal("jpn", damaged.Language);
        Assert.Equal("Commentary", damaged.Title);
        Assert.True(damaged.IsDispositionSet("forced"));
    }

    private static OutputContainerDecision Decision(params int[] indexes) => new()
    {
        Requested = OutputContainerSelection.Mp4,
        Resolved = OutputContainer.Mp4,
        Reason = "test",
        StreamPlans = indexes.Select(index => new StreamCompatibilityPlan(index, "audio", "aac", StreamCompatibilityAction.Copy, "copy")).ToArray()
    };

    private sealed class FakeRunner : IMediaToolProcessRunner
    {
        public List<MediaToolProcessRequest> Requests { get; } = new();
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new MediaToolProcessResult());
        }
    }
}
