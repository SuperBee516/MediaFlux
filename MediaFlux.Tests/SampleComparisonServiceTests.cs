using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SampleComparisonServiceTests
{
    [Fact]
    public void StreamCopyArgumentsGenerateTimestampsBeforeOpeningInput()
    {
        string arguments = SampleComparisonService.BuildStreamCopyClipArguments(
            @"F:\Incoming\legacy video.avi",
            TimeSpan.FromSeconds(12.5),
            TimeSpan.FromSeconds(25),
            @"C:\Temp\sample.mkv");

        int genPts = arguments.IndexOf("-fflags +genpts", StringComparison.Ordinal);
        int input = arguments.IndexOf("-i \"F:\\Incoming\\legacy video.avi\"", StringComparison.Ordinal);

        Assert.True(genPts >= 0);
        Assert.True(input > genPts);
        Assert.Contains("-c copy", arguments);
        Assert.Contains("-avoid_negative_ts make_zero", arguments);
    }

    [Fact]
    public async Task UnknownTimestampFailureRetriesWithNormalizedVideo()
    {
        var attempts = new List<(string Arguments, string Operation)>();
        var progress = new RecordingProgress();
        var service = new SampleComparisonService(
            Path.GetTempPath(),
            (arguments, operation, _) =>
            {
                attempts.Add((arguments, operation));
                if (attempts.Count == 1)
                {
                    throw new InvalidOperationException(
                        "FFmpeg failed (exit code -22). Can't write packet with unknown timestamp");
                }

                return Task.CompletedTask;
            });

        await service.PrepareSourceClipAsync(
            "source.avi",
            TimeSpan.Zero,
            TimeSpan.FromSeconds(25),
            "sample.mkv",
            "Beginning",
            progress,
            CancellationToken.None);

        Assert.Equal(2, attempts.Count);
        Assert.Contains("-c copy", attempts[0].Arguments);
        Assert.Contains("-c:v ffv1", attempts[1].Arguments);
        Assert.Contains("-c:a aac", attempts[1].Arguments);
        Assert.DoesNotContain("-c copy", attempts[1].Arguments);
        Assert.Contains("normalizing timestamps", attempts[1].Operation);
        Assert.Contains(
            progress.Messages,
            message => message.Contains("missing timestamps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnrelatedFfmpegFailureIsNotRetried()
    {
        int attempts = 0;
        var service = new SampleComparisonService(
            Path.GetTempPath(),
            (_, _, _) =>
            {
                attempts++;
                throw new InvalidOperationException(
                    "FFmpeg failed while opening the input: Permission denied");
            });

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareSourceClipAsync(
                "source.avi",
                TimeSpan.Zero,
                TimeSpan.FromSeconds(25),
                "sample.mkv",
                "Beginning",
                progress: null,
                CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Contains("Permission denied", error.Message);
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public List<string> Messages { get; } = new();

        public void Report(string value) => Messages.Add(value);
    }
}
