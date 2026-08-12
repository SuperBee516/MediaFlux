using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class SampleComparisonServiceTests
{
    [Fact]
    public void ProjectionUsesMeasuredDurationsInsteadOfRequestedClipLengths()
    {
        long tenMiB = 10L * 1024 * 1024;
        SampleProjectionCalculation result = SampleComparisonService.CalculateProjection(
            new[]
            {
                new SampleProjectionMeasurement(tenMiB, 8, 8),
                new SampleProjectionMeasurement(tenMiB, 9, 8),
                new SampleProjectionMeasurement(tenMiB, 10, 8)
            },
            sourceDurationSeconds: 60);

        Assert.Equal(66.67, result.ProjectedFinalMb, precision: 2);
        Assert.Equal(27, result.SampledMediaSeconds, precision: 3);
        Assert.False(result.UsedDurationFallback);
        Assert.NotEqual(75, result.ProjectedFinalMb);
    }

    [Fact]
    public void ProjectionReportsRangeAndHighConfidenceForConsistentSamples()
    {
        SampleProjectionCalculation result = SampleComparisonService.CalculateProjection(
            new[]
            {
                new SampleProjectionMeasurement(8L * 1024 * 1024, 8, 8),
                new SampleProjectionMeasurement(9L * 1024 * 1024, 9, 8),
                new SampleProjectionMeasurement(10L * 1024 * 1024, 10, 8)
            },
            sourceDurationSeconds: 120);

        Assert.Equal(SmartEncodeConfidence.High, result.Confidence);
        Assert.Equal(120, result.ProjectedFinalMb, precision: 2);
        Assert.Equal(110.4, result.ProjectedLowerMb, precision: 2);
        Assert.Equal(129.6, result.ProjectedUpperMb, precision: 2);
    }

    [Fact]
    public void MissingMeasuredDurationFallsBackAndWidensRange()
    {
        SampleProjectionCalculation result = SampleComparisonService.CalculateProjection(
            new[]
            {
                new SampleProjectionMeasurement(8L * 1024 * 1024, 0, 8),
                new SampleProjectionMeasurement(0, 0, 8)
            },
            sourceDurationSeconds: 80);

        Assert.True(result.UsedDurationFallback);
        Assert.Equal(SmartEncodeConfidence.Low, result.Confidence);
        Assert.Equal(1.25, result.ProjectedUpperMb / result.ProjectedFinalMb, precision: 2);
        Assert.Equal(1, result.SampleCount);
    }

    [Fact]
    public void SamplePositionsAvoidRedundantOverlappingClipsForShortVideos()
    {
        var shortVideo = SampleComparisonService.BuildSamplePositions(
            TimeSpan.FromSeconds(10),
            requestedClipSeconds: 8);
        var mediumVideo = SampleComparisonService.BuildSamplePositions(
            TimeSpan.FromSeconds(20),
            requestedClipSeconds: 8);
        var longVideo = SampleComparisonService.BuildSamplePositions(
            TimeSpan.FromMinutes(30),
            requestedClipSeconds: 8);

        Assert.Single(shortVideo);
        Assert.Equal("Full video", shortVideo[0].Label);
        Assert.Equal(2, mediumVideo.Count);
        Assert.Equal(3, longVideo.Count);
    }

    [Fact]
    public void FullVideoSampleHasHighConfidenceDespiteSingleSample()
    {
        SampleProjectionCalculation result = SampleComparisonService.CalculateProjection(
            new[]
            {
                new SampleProjectionMeasurement(5L * 1024 * 1024, 10, 10)
            },
            sourceDurationSeconds: 10);

        Assert.Equal(SmartEncodeConfidence.High, result.Confidence);
        Assert.Equal(5, result.ProjectedFinalMb, precision: 2);
    }

    [Fact]
    public void ProjectionAddsMappedAncillaryStreamBudget()
    {
        SampleProjectionCalculation result = SampleComparisonService.CalculateProjection(
            new[]
            {
                new SampleProjectionMeasurement(1L * 1024 * 1024, 10, 10)
            },
            sourceDurationSeconds: 100,
            additionalMappedBitrateKbps: 819.2);

        Assert.Equal(20, result.ProjectedFinalMb, precision: 2);
    }

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
        Assert.Contains("-map 0:a?", arguments);
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
