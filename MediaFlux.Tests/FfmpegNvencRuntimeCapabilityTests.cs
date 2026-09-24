using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegNvencRuntimeCapabilityTests
{
    [Fact]
    public void SuccessfulSyntheticEncodeMeansAvailable()
    {
        FfmpegNvencRuntimeCapability result = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 0, "ffmpeg version 8.1.2", "frame=1");
        Assert.Equal(FfmpegNvencRuntimeState.Available, result.State);
    }

    [Fact]
    public void MissingEncoderIsDistinctFromRuntimeInitializationFailure()
    {
        FfmpegNvencRuntimeCapability missing = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 1, "", "Unknown encoder 'hevc_nvenc'");
        FfmpegNvencRuntimeCapability unavailable = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 1, "", "generic encoder initialization failed");
        Assert.Equal(FfmpegNvencRuntimeState.EncoderMissing, missing.State);
        Assert.Equal(FfmpegNvencRuntimeState.Unavailable, unavailable.State);
    }

    [Fact]
    public void ApiAndMinimumDriverRequirementsAreParsed()
    {
        FfmpegNvencRuntimeCapability api = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 1, "", "Driver does not support the required nvenc API version.\r\nRequired: 13.1 Found: 13.0");
        Assert.Equal(FfmpegNvencRuntimeState.DriverIncompatible, api.State);
        Assert.Equal("13.1", api.RequiredApiVersion);
        Assert.Equal("13.0", api.DetectedApiVersion);

        FfmpegNvencRuntimeCapability driver = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "h264_nvenc", 1, "", "The minimum required Nvidia driver for nvenc is 610.00 or newer");
        Assert.Equal(FfmpegNvencRuntimeState.DriverIncompatible, driver.State);
        Assert.Equal("610.00", driver.MinimumDriverVersion);
        Assert.Contains("610.00", driver.Diagnostic);
    }

    [Fact]
    public async Task ResultIsCachedByPathAndEncoder()
    {
        var runner = new FakeRunner();
        var service = new FfmpegNvencRuntimeCapabilityService(runner);
        FfmpegNvencRuntimeCapability first = await service.CheckAsync("ffmpeg.exe", "hevc_nvenc");
        FfmpegNvencRuntimeCapability second = await service.CheckAsync("ffmpeg.exe", "hevc_nvenc");
        Assert.Same(first, second);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public void TimedOutProbeIsDistinct()
    {
        FfmpegNvencRuntimeCapability result = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "h264_nvenc", null, "", "", timedOut: true);
        Assert.Equal(FfmpegNvencRuntimeState.TimedOut, result.State);
    }

    [Fact]
    public async Task CancellationIsPropagatedAndDoesNotPoisonCache()
    {
        var runner = new FakeRunner(blockUntilCanceled: true);
        var service = new FfmpegNvencRuntimeCapabilityService(runner);
        using var cancellation = new CancellationTokenSource();
        Task<FfmpegNvencRuntimeCapability> probe = service.CheckAsync("ffmpeg.exe", "hevc_nvenc", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        Assert.Equal(1, runner.Calls);
    }

    private sealed class FakeRunner : IFfmpegNvencProbeRunner
    {
        private readonly bool _blockUntilCanceled;
        public FakeRunner(bool blockUntilCanceled = false) => _blockUntilCanceled = blockUntilCanceled;
        public int Calls { get; private set; }
        public async Task<(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut)> RunAsync(
            string ffmpegPath, string encoder, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls++;
            if (_blockUntilCanceled) await Task.Delay(Timeout.Infinite, cancellationToken);
            return (0, "ffmpeg version test", "frame=1", false);
        }
    }
}
