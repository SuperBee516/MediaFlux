using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class QueueEstimateBenchmarkCommandTests
{
    [Fact]
    public void MetadataReplayOptionAllowsFixtureDrivenQueueLoadWithoutMediaProbes()
    {
        string[] args =
        [
            "--benchmark-queue-estimates",
            "--source-directory", "C:\\corpus",
            "--user-data", "C:\\isolated-user-data",
            "--output-directory", "C:\\existing-output",
            "--count", "300",
            "--quality-mode", "Automatic",
            "--metadata-replay"
        ];

        Assert.True(QueueEstimateBenchmarkCommand.TryParse(args, out QueueEstimateBenchmarkRequest? request, out string error), error);
        Assert.NotNull(request);
        Assert.Equal(300, request.FileCount);
        Assert.Equal("Automatic", request.QualityMode);
        Assert.True(request.MetadataReplay);
    }

    [Fact]
    public void MetadataReplayDefaultsToFalse()
    {
        string[] args =
        [
            "--benchmark-queue-estimates",
            "--source-directory", "C:\\corpus",
            "--user-data", "C:\\isolated-user-data",
            "--output-directory", "C:\\existing-output"
        ];

        Assert.True(QueueEstimateBenchmarkCommand.TryParse(args, out QueueEstimateBenchmarkRequest? request, out string error), error);
        Assert.NotNull(request);
        Assert.False(request.MetadataReplay);
    }
}
