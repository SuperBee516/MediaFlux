using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfmpegNvencRuntimeCapabilityTests
{
    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("hevc_nvenc")]
    [InlineData("av1_nvenc")]
    public void SuccessfulSyntheticEncodeMeansAvailable(string encoder)
    {
        FfmpegNvencRuntimeCapability result = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", encoder, 0, "ffmpeg version 8.1.2", "frame=1");
        Assert.Equal(FfmpegNvencRuntimeState.Available, result.State);
    }

    [Theory]
    [InlineData("h264_nvenc")]
    [InlineData("hevc_nvenc")]
    [InlineData("av1_nvenc")]
    public void ProbeUsesFrameGeometryThatInitializedNvencOnRtx4090(string encoder)
    {
        string[] args = FfmpegNvencRuntimeCapabilityService.BuildProbeArguments(encoder);
        Assert.Equal("color=c=black:s=256x256:r=1", args[Array.IndexOf(args, "-i") + 1]);
        Assert.Equal(encoder, args[Array.IndexOf(args, "-c:v") + 1]);
        Assert.Equal("null", args[Array.IndexOf(args, "-f", 4) + 1]);
    }

    [Fact]
    public void MissingEncoderIsDistinctFromRuntimeInitializationFailure()
    {
        FfmpegNvencRuntimeCapability missing = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 1, "", "Unknown encoder 'hevc_nvenc'");
        FfmpegNvencRuntimeCapability unavailable = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 1, "", "OpenEncodeSessionEx failed: out of memory");
        Assert.Equal(FfmpegNvencRuntimeState.EncoderMissing, missing.State);
        Assert.Equal(FfmpegNvencRuntimeState.Unavailable, unavailable.State);
    }

    [Theory]
    [InlineData("h264_nvenc", "Frame Dimension less than the minimum supported value.")]
    [InlineData("hevc_nvenc", "Frame dimensions are less than the minimum supported value.")]
    public void RejectedProbeGeometryIsNotReportedAsGpuFailure(string encoder, string stderr)
    {
        FfmpegNvencRuntimeCapability result = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", encoder, -22, "", "InitializeEncoder failed: invalid param (8): " + stderr);
        Assert.Equal(FfmpegNvencRuntimeState.ProbeFailed, result.State);
        Assert.Contains("test frame", result.Diagnostic);
        Assert.Contains(stderr, result.RawDiagnostic);
    }

    [Fact]
    public void UnknownProbeErrorRemainsBlockingWithoutClaimingGpuFailure()
    {
        FfmpegNvencRuntimeCapability result = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 1, "", "Invalid argument in synthetic input");
        Assert.Equal(FfmpegNvencRuntimeState.ProbeFailed, result.State);
        Assert.False(result.IsAvailable);
    }

    [Fact]
    public void NonfatalWarningDoesNotOverrideSuccessfulExitCode()
    {
        FfmpegNvencRuntimeCapability result = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", 0, "", "Unknown encoder mentioned in a warning; frame=1");
        Assert.Equal(FfmpegNvencRuntimeState.Available, result.State);
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
        VideoEncoderSelection selection = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc).Selection;
        FfmpegNvencRuntimeCapability? second = await service.CheckRequestedAsync("ffmpeg.exe", selection);
        Assert.Same(first, second);
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("OpenEncodeSessionEx failed", false)]
    [InlineData("synthetic input failed", false)]
    [InlineData("", true)]
    public async Task TransientFailureDoesNotPoisonCapabilityCache(string stderr, bool timedOut)
    {
        var runner = new SequenceRunner((timedOut ? null : 1, stderr, timedOut), (0, "frame=1", false));
        var service = new FfmpegNvencRuntimeCapabilityService(runner);
        FfmpegNvencRuntimeCapability first = await service.CheckAsync("ffmpeg.exe", "hevc_nvenc");
        FfmpegNvencRuntimeCapability second = await service.CheckAsync("ffmpeg.exe", "hevc_nvenc");
        Assert.False(first.IsAvailable);
        Assert.True(second.IsAvailable);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task MissingEncoderFailureIsCached()
    {
        var runner = new SequenceRunner((1, "Unknown encoder 'hevc_nvenc'", false));
        var service = new FfmpegNvencRuntimeCapabilityService(runner);
        Assert.Equal(FfmpegNvencRuntimeState.EncoderMissing,
            (await service.CheckAsync("ffmpeg.exe", "hevc_nvenc")).State);
        Assert.Equal(FfmpegNvencRuntimeState.EncoderMissing,
            (await service.CheckAsync("ffmpeg.exe", "hevc_nvenc")).State);
        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task RequestedEncoderGateBlocksNvencFailureAndSkipsCpu()
    {
        var runner = new SequenceRunner((1, "OpenEncodeSessionEx failed", false));
        var service = new FfmpegNvencRuntimeCapabilityService(runner);
        VideoEncoderSelection cpu = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Libx265, VideoCodecFamily.Hevc).Selection;
        VideoEncoderSelection nvenc = EncoderRegistry.Default.Resolve(
            VideoEncoderIds.Nvenc, VideoCodecFamily.Hevc).Selection;

        Assert.Null(await service.CheckRequestedAsync("ffmpeg.exe", cpu));
        Assert.Equal(0, runner.Calls);
        FfmpegNvencRuntimeCapability? blocked = await service.CheckRequestedAsync("ffmpeg.exe", nvenc);
        Assert.NotNull(blocked);
        Assert.False(blocked.IsAvailable);
        Assert.Equal(FfmpegNvencRuntimeState.Unavailable, blocked.State);
    }

    [Fact]
    public void BlockedDiagnosticUsesCentralErrorLog()
    {
        string marker = "NVENC gate test " + Guid.NewGuid().ToString("N");
        FfmpegNvencRuntimeCapability failure = FfmpegNvencRuntimeCapabilityService.Classify(
            "ffmpeg.exe", "hevc_nvenc", -22, "", "Frame dimensions are less than the minimum supported value.");
        FfmpegNvencRuntimeCapabilityService.LogBlocked(failure, marker);
        string logPath = ErrorLogService.GetDefaultLogPath(AppPaths.UserDataDirectory);
        Assert.Equal(Path.Combine(AppPaths.LogsDirectory, "mediaflux-errors.log"), logPath);
        string tail = ErrorLogService.ReadTail(logPath, 16 * 1024, out _);
        Assert.Contains(marker, tail);
        Assert.Contains(failure.RawDiagnostic, tail);
        Assert.Contains("Central Error Log", FfmpegNvencRuntimeCapabilityService.BlockedMessage(failure));
    }

    [Fact]
    public async Task LiveH264AndHevcProbesInitializeWhenEnabled()
    {
        if (Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_NVENC") != "1")
            return;
        string? ffmpeg = Environment.GetEnvironmentVariable("MEDIAFLUX_LIVE_FFMPEG_PATH");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            return;

        FfmpegNvencRuntimeCapabilityService.Shared.ClearCache();
        foreach (string encoder in new[] { "h264_nvenc", "hevc_nvenc" })
        {
            FfmpegNvencRuntimeCapability result =
                await FfmpegNvencRuntimeCapabilityService.Shared.CheckAsync(ffmpeg, encoder);
            Assert.True(result.IsAvailable, result.Diagnostic + Environment.NewLine + result.RawDiagnostic);
            Assert.Equal(0, result.ExitCode);
        }
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

    private sealed class SequenceRunner : IFfmpegNvencProbeRunner
    {
        private readonly (int? ExitCode, string Stderr, bool TimedOut)[] _results;
        public int Calls { get; private set; }
        public SequenceRunner(params (int? ExitCode, string Stderr, bool TimedOut)[] results) =>
            _results = results;
        public Task<(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut)> RunAsync(
            string ffmpegPath, string encoder, TimeSpan timeout, CancellationToken cancellationToken)
        {
            (int? exitCode, string stderr, bool timedOut) = _results[Math.Min(Calls++, _results.Length - 1)];
            return Task.FromResult((exitCode, "ffmpeg version test", stderr, timedOut));
        }
    }
}
