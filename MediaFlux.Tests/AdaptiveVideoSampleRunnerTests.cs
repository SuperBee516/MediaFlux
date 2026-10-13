using System.Diagnostics;
using MediaFlux.Models;
using MediaFlux.Services;
using MediaFlux.Services.Encoders;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AdaptiveVideoSampleRunnerTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("bad-measurement")]
    public async Task RunnerMeasuresVideoPacketsAndCleansOwnedFilesOnEveryExit(string outcome)
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxAdaptiveRunnerTests", Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        var request = AdaptiveStorageSavingsTests.Request();
        var packets = new PacketRunner { Fail = outcome == "bad-measurement" };
        var runner = new AdaptiveVideoSampleRunner("unused", "ffprobe", new Probe(request),
            (_, _, _, output) => output, packets,
            async (output, arguments, token) =>
            {
                await File.WriteAllBytesAsync(output, new byte[123], token);
                if (outcome == "failure") throw new InvalidOperationException("injected sample failure");
                if (outcome == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            }, root);
        try
        {
            RepresentativeSample sample = new("Measured window", TimeSpan.Zero, TimeSpan.FromSeconds(25));
            Task<RepresentativeSampleEvidence> work = runner.MeasureAsync(request, 24, sample, cancellation.Token);
            if (outcome == "success")
            {
                var measured = await work;
                Assert.Equal(30_000, measured.VideoBytes); // packet bytes, not file or audio bytes
                Assert.Equal(24.98, measured.MeasuredSeconds);
                Assert.Contains("v:0", packets.LastRequest!.Arguments);
            }
            else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
            else if (outcome == "bad-measurement") await Assert.ThrowsAsync<InvalidDataException>(() => work);
            else await Assert.ThrowsAsync<InvalidOperationException>(() => work);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InflatedEncodedDurationCannotUnderstateVideoRate()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxAdaptiveDurationTests", Guid.NewGuid().ToString("N"));
        var request = AdaptiveStorageSavingsTests.Request();
        var packets = new PacketRunner();
        var runner = new AdaptiveVideoSampleRunner("unused", "ffprobe", new Probe(request, 100),
            (_, _, _, output) => output, packets,
            (output, _, token) => File.WriteAllBytesAsync(output, new byte[123], token), root);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.MeasureAsync(request, 24,
                new("Window", TimeSpan.Zero, TimeSpan.FromSeconds(25)), CancellationToken.None));
            Assert.Null(packets.LastRequest);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExistingOneFrameRoundingRemainsAccepted()
    {
        await AssertDurationOutcomeAsync(24.98, 30, 0, 0, accepted: true);
    }

    [Theory]
    [InlineData(0.066)]
    [InlineData(0.0)]
    public async Task MissingSourceTimelineRetainsLegacyWholeWindowComparison(double? streamStartSeconds)
    {
        // Missing source timing retains the previous whole-window comparison.
        await AssertDurationOutcomeAsync(24.933333, 30, streamStartSeconds, 0, accepted: false);
    }

    [Fact]
    public async Task PositiveSourceStartBeginningWindowUsesOnlyAvailableSourceVideo()
    {
        const double sourceVideoStart = 0.066016;
        const double sourceVideoEnd = 30.032683;
        const double measuredSeconds = 24.933333;
        double expectedAvailable = Math.Max(0, Math.Min(25, sourceVideoEnd) - Math.Max(0, sourceVideoStart));
        double tolerance = 1d / 30 + 0.001;

        Assert.Equal(24.933984, expectedAvailable, 6);
        Assert.True(Math.Abs(measuredSeconds - expectedAvailable) <= tolerance);
        Assert.True(Math.Abs(measuredSeconds - 25) > tolerance);
        await AssertDurationOutcomeAsync(measuredSeconds, 30, 0.066016, 0, accepted: true,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd);
    }

    [Fact]
    public async Task Mp4BeginningSampleUsesRebasedVideoTimelineWhenSourceStreamStartsLate()
    {
        // FFmpeg 8.1.2 sample output for SRC-8650153C0361: the MPEG-TS source
        // starts video 0.023222 s after the format start, while MP4 output
        // rebases its first frame to 0 and reports 750 frames / 25.025 s.
        await AssertDurationOutcomeAsync(25.025, 30000d / 1001, 0, 0, accepted: true,
            sourceVideoStartSeconds: 0.023222, sourceVideoEndSeconds: 530.987522,
            outputContainer: OutputContainer.Mp4);
    }

    [Theory]
    [InlineData(24000d / 1001)]
    [InlineData(24.0)]
    [InlineData(30000d / 1001)]
    [InlineData(30.0)]
    [InlineData(60000d / 1001)]
    [InlineData(60.0)]
    public async Task Mp4PositiveSourceStartAcceptsFrameQuantizationAndRejectsTwoFrameTruncation(double framesPerSecond)
    {
        const double requestedSeconds = 25;
        const double sourceVideoStart = 0.023222;
        const double sourceVideoEnd = 530.987522;
        double frameRoundedDuration = Math.Ceiling(requestedSeconds * framesPerSecond) / framesPerSecond;

        await AssertDurationOutcomeAsync(frameRoundedDuration, framesPerSecond, 0, 0, accepted: true,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd,
            outputContainer: OutputContainer.Mp4);
        await AssertDurationOutcomeAsync(requestedSeconds - 2d / framesPerSecond, framesPerSecond, 0, 0, accepted: false,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd,
            outputContainer: OutputContainer.Mp4);
    }

    [Fact]
    public async Task PositiveSourceStartDoesNotReduceLaterWindowDuration()
    {
        const double sourceVideoStart = 0.066016;
        const double sourceVideoEnd = 30.032683;
        const double sampleStart = 5;
        const double measuredSeconds = 24.966667;
        double expectedAvailable = Math.Max(0, Math.Min(sampleStart + 25, sourceVideoEnd) -
            Math.Max(sampleStart, sourceVideoStart));

        Assert.Equal(25, expectedAvailable);
        await AssertDurationOutcomeAsync(measuredSeconds, 30, 0.033008, sampleStart, accepted: true,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd);
    }

    [Fact]
    public async Task ZeroStartSourceNormalWindowRetainsFullRequestedDuration()
    {
        await AssertDurationOutcomeAsync(25, 30, 0, 0, accepted: true,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
    }

    [Fact]
    public async Task WindowExtendingPastSourceVideoEndUsesAvailableIntersection()
    {
        const double sampleStart = 90;
        const double sourceVideoEnd = 100;
        double expectedAvailable = Math.Max(0, Math.Min(sampleStart + 25, sourceVideoEnd) - Math.Max(sampleStart, 0));

        Assert.Equal(10, expectedAvailable);
        await AssertDurationOutcomeAsync(expectedAvailable, 30, 0, sampleStart, accepted: true,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: sourceVideoEnd);
    }

    [Fact]
    public async Task WindowPastSourceVideoEndDoesNotHideMoreThanOneFrameOfTruncation()
    {
        await AssertDurationOutcomeAsync(9.9, 30, 0, 90, accepted: false,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
    }

    [Fact]
    public async Task DurationGuardUsesStrictGreaterThanAtTheOneFramePlusOneMillisecondBoundary()
    {
        const double requestedSeconds = 25;
        double tolerance = 1d / 30 + 0.001;
        double boundary = requestedSeconds - tolerance;

        // IEEE-754 subtraction rounds the exact-boundary difference slightly
        // below the computed tolerance. Adjacent representable measured values
        // show the strict '>' decision on either side.
        await AssertDurationOutcomeAsync(boundary, 30, 0, 0, accepted: true,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
        await AssertDurationOutcomeAsync(Math.BitIncrement(boundary), 30, 0, 0, accepted: true,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
        await AssertDurationOutcomeAsync(Math.BitDecrement(boundary), 30, 0, 0, accepted: false,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
    }

    [Fact]
    public async Task DurationGuardAcceptsOneFrameAndRejectsTwoFramesShortAtThirtyFps()
    {
        await AssertDurationOutcomeAsync(25 - 1d / 30, 30, 0, 0, accepted: true,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
        await AssertDurationOutcomeAsync(25 - 2d / 30, 30, 0, 0, accepted: false,
            sourceVideoStartSeconds: 0, sourceVideoEndSeconds: 100);
    }

    [Fact]
    public async Task PositiveSourceStartStillRejectsGenuineTwoFrameTruncation()
    {
        const double sourceVideoStart = 0.066016;
        const double sourceVideoEnd = 30.032683;
        double expectedAvailable = 25 - sourceVideoStart;

        await AssertDurationOutcomeAsync(expectedAvailable - 2d / 30, 30, 0.066016, 0, accepted: false,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd);
    }

    [Fact]
    public async Task PositiveSourceStartAcceptsOneFrameDeficitFromExpectedAvailableDuration()
    {
        const double sourceVideoStart = 0.066016;
        const double sourceVideoEnd = 30.032683;
        double expectedAvailable = 25 - sourceVideoStart;

        await AssertDurationOutcomeAsync(expectedAvailable - 1d / 30, 30, 0.066016, 0, accepted: true,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd);
    }

    [Fact]
    public async Task PositiveSourceStartUsesStrictToleranceBoundaryAgainstExpectedDuration()
    {
        const double sourceVideoStart = 0.066016;
        const double sourceVideoEnd = 30.032683;
        double expectedAvailable = 25 - sourceVideoStart;
        double boundary = expectedAvailable - (1d / 30 + 0.001);

        await AssertDurationOutcomeAsync(boundary, 30, 0.066016, 0, accepted: true,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd);
        await AssertDurationOutcomeAsync(Math.BitDecrement(boundary), 30, 0.066016, 0, accepted: false,
            sourceVideoStartSeconds: sourceVideoStart, sourceVideoEndSeconds: sourceVideoEnd);
    }

    [Theory]
    [InlineData(null, 30.0)]
    [InlineData(0.066, null)]
    [InlineData(0.066, double.NaN)]
    [InlineData(0.066, 0.05)]
    public async Task UnknownOrInvalidSourceTimelineFallsBackToExistingComparison(
        double? sourceVideoStartSeconds, double? sourceVideoEndSeconds)
    {
        await AssertDurationOutcomeAsync(24.933333, 30, 0.066, 0, accepted: false,
            sourceVideoStartSeconds: sourceVideoStartSeconds, sourceVideoEndSeconds: sourceVideoEndSeconds);
    }

    [Theory]
    [InlineData(23.976023976023978)] // 24000:1001
    [InlineData(24.0)]
    [InlineData(25.0)]
    [InlineData(29.97002997002997)] // 30000:1001
    [InlineData(30.0)]
    [InlineData(50.0)]
    [InlineData(59.94005994005994)] // 60000:1001
    [InlineData(60.0)]
    public async Task DurationToleranceScalesWithRepresentativeCfrRates(double framesPerSecond)
    {
        await AssertDurationOutcomeAsync(25 - 1d / framesPerSecond, framesPerSecond, 0, 0, accepted: true);
        await AssertDurationOutcomeAsync(25 - 2d / framesPerSecond, framesPerSecond, 0, 0, accepted: false);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task DurationComparisonIsBypassedWhenOutputRateIsUnavailableOrInvalid(double? framesPerSecond)
    {
        await AssertDurationOutcomeAsync(24.933333, framesPerSecond, 0.066, 0, accepted: true);
    }

    [Theory]
    [InlineData(0.0, 24.933333, 0.066016, true)]
    [InlineData(5.0, 24.966667, 0.033008, true)]
    public async Task TimelineAwareContractAcceptsBeginningAndLaterSyntheticWindowMeasurements(
        double sampleStartSeconds, double measuredSeconds, double sampleStreamStartSeconds, bool accepted)
    {
        // The values characterize FFmpeg 8.1.2 output from the external
        // synthetic timing experiment recorded with this audit. The unit test
        // itself remains deterministic and does not launch FFmpeg.
        await AssertDurationOutcomeAsync(measuredSeconds, 30, sampleStreamStartSeconds,
            sampleStartSeconds, accepted, sourceVideoStartSeconds: 0.066016, sourceVideoEndSeconds: 30.032683);
    }

    [Fact]
    public async Task FiniteAverageRateIsUsedWithoutASeparateVfrClassification()
    {
        // MediaProbeStreamInfo exposes a scalar FrameRate, not a CFR/VFR flag.
        await AssertDurationOutcomeAsync(24.933333, 29.97, 0.066, 0, accepted: false);
    }

    private static async Task AssertDurationOutcomeAsync(double measuredSeconds, double? framesPerSecond,
        double? sampleStreamStartSeconds, double sampleStartSeconds, bool accepted,
        double? sourceVideoStartSeconds = null, double? sourceVideoEndSeconds = null,
        OutputContainer? outputContainer = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxAdaptiveDurationContractTests", Guid.NewGuid().ToString("N"));
        AdaptiveQualitySelectionRequest baseRequest = AdaptiveStorageSavingsTests.Request();
        var request = baseRequest with
        {
            Video = baseRequest.Video with
            {
                Container = outputContainer ?? baseRequest.Video.Container
            },
            SourceVideoStartOffsetSeconds = sourceVideoStartSeconds,
            SourceVideoEndOffsetSeconds = sourceVideoEndSeconds
        };
        var packets = new PacketRunner();
        var runner = new AdaptiveVideoSampleRunner("unused", "ffprobe",
            new Probe(request, measuredSeconds, framesPerSecond, sampleStreamStartSeconds),
            (_, _, _, output) => output, packets,
            (output, _, token) => File.WriteAllBytesAsync(output, new byte[123], token), root);
        try
        {
            RepresentativeSample sample = new("Window", TimeSpan.FromSeconds(sampleStartSeconds), TimeSpan.FromSeconds(25));
            if (accepted)
            {
                RepresentativeSampleEvidence evidence = await runner.MeasureAsync(request, 24, sample, CancellationToken.None);
                Assert.Equal(measuredSeconds, evidence.MeasuredSeconds);
                Assert.Equal(30_000, evidence.VideoBytes);
                Assert.NotNull(packets.LastRequest);
            }
            else
            {
                InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
                    () => runner.MeasureAsync(request, 24, sample, CancellationToken.None));
                Assert.Contains("duration does not match", exception.Message);
                Assert.Null(packets.LastRequest);
            }
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ChangedKnownHdrColorSignalingCannotAuthorizeAdaptiveSelection()
    {
        string root = Path.Combine(Path.GetTempPath(), "MediaFluxAdaptiveColorTests", Guid.NewGuid().ToString("N"));
        var request = AdaptiveStorageSavingsTests.Request();
        request = request with { Video = request.Video with { SourceColorTransfer = "smpte2084", SourceColorPrimaries = "bt2020" } };
        var runner = new AdaptiveVideoSampleRunner("unused", "ffprobe", new Probe(request),
            (_, _, _, output) => output, new PacketRunner(),
            (output, _, token) => File.WriteAllBytesAsync(output, new byte[123], token), root);
        try
        {
            var result = await new StorageSavingsSampleSelector(runner).SelectAsync(request, null, CancellationToken.None);
            Assert.Equal(AdaptiveSelectionDisposition.NotSuitable, result.Disposition);
            Assert.Equal(request.Envelope.PreferredQuality, result.SelectedQuality);
            Assert.Contains("color signaling", result.Reason);
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationKillsAndDrainsAnActualOwnedProcessBeforeReturning()
    {
        using var cancellation = new CancellationTokenSource();
        int processId = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AdaptiveVideoSampleRunner.RunProcessAsync(
            "powershell.exe", "-NoProfile -NonInteractive -Command \"[System.Threading.ManualResetEvent]::new($false).WaitOne()\"",
            cancellation.Token, id => { processId = id; cancellation.Cancel(); }));
        Assert.True(processId > 0);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [Theory]
    [InlineData(VideoEncoderIds.Nvenc, "hevc_nvenc", "p7", true, "yuv420p10le", "-cq 26", "p010le")]
    [InlineData(VideoEncoderIds.Libx265, "libx265", "slow", true, "yuv420p10le", "-crf 26", "yuv420p10le")]
    [InlineData(VideoEncoderIds.Libx265, "libx265", "medium", false, "yuv420p", "-crf 26", "yuv420p")]
    public void VideoOnlySampleRetainsProductionEncoderPresetQualityGeometryAndBitDepth(
        string id, string codec, string preset, bool tenBit, string sourcePixels, string qualityFlag, string plannedPixels)
    {
        var builder = new FfmpegCommandBuilder(EncoderRegistry.Default, _ => 128);
        var geometry = new VideoOutputGeometryPlan(1920, 1080, 1280, 720, 1280, 720, "1280:720", plannedPixels, 2, "downscale");
        string full = builder.Build(Command(false));
        string sample = builder.Build(Command(true));
        // Compare the complete generated video argument block, including filters,
        // pixel/residency flags, rate control, tune, B frames, lookahead and AQ.
        Assert.Equal(VideoArguments(full), VideoArguments(sample));
        Assert.Equal(full[..full.IndexOf("-i ", StringComparison.Ordinal)],
            sample[..sample.IndexOf("-ss ", StringComparison.Ordinal)]);
        foreach (string argument in new[] { $"-c:v {codec}", $"-preset {preset}", qualityFlag, "1280:720" })
        { Assert.Contains(argument, full); Assert.Contains(argument, sample); }
        Assert.Contains("-map 0:v:0 -an -sn -dn", sample);
        Assert.DoesNotContain("-c:a", sample);
        Assert.DoesNotContain("-map 0:a", sample);
        Assert.DoesNotContain("-c:s copy", sample);
        Assert.Contains("-ss 37.5", sample);
        Assert.Contains("-t 25", sample);
        Assert.DoesNotContain("-ss 37.5", full);
        Assert.Contains("\\\\server\\media\\source.mkv", sample);

        static string VideoArguments(string arguments)
        {
            int codec = arguments.IndexOf("-c:v ", StringComparison.Ordinal);
            int filter = arguments.IndexOf("-vf ", StringComparison.Ordinal);
            int start = filter >= 0 ? Math.Min(filter, codec) : codec;
            int audio = arguments.IndexOf("-c:a ", start, StringComparison.Ordinal);
            int mux = arguments.IndexOf("-f ", start, StringComparison.Ordinal);
            return arguments[start..(audio >= 0 ? audio : mux)].Trim();
        }

        FfmpegCommandRequest Command(bool videoOnly) => new()
        {
            Input = EncodingInputSource.FromFile("\\\\server\\media\\source.mkv"), OutputPath = "output.mkv",
            Encoder = new(id, VideoCodecFamily.Hevc, codec), UseGpu = id == VideoEncoderIds.Nvenc,
            TargetMb = null, ScaleMode = EncodingService.ScaleMode.To720p, EncoderPreset = preset,
            QualityValue = 26, TenBit = tenBit, AudioChannels = 2, ConcurrentEncoderSessions = true,
            MapMode = EncodingService.StreamMapMode.KeepAll, CopySubtitles = true, CopyDataStreams = true,
            CopyAttachments = true, ForceMp4CompatibleAudio = false, KnownDuration = TimeSpan.FromSeconds(100),
            ContainerDecision = new() { Resolved = OutputContainer.Matroska, CopySubtitles = true, CopyAttachments = true },
            SourcePixelFormat = sourcePixels, PlannedVideoGeometry = geometry,
            NvencCudaFormatConversionSupported = true, VideoOnly = videoOnly,
            SampleStart = videoOnly ? TimeSpan.FromSeconds(37.5) : null, SampleDuration = videoOnly ? TimeSpan.FromSeconds(25) : null
        };
    }

    private sealed class Probe(AdaptiveQualitySelectionRequest request, double seconds = 24.98,
        double? frameRate = 30, double? startTimeSeconds = 0) : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaProbeResult
        {
            Success = true, DurationSeconds = 123, Streams = [new()
            {
                CodecType = "video", CodecName = "hevc", Width = request.Video.Geometry.Width,
                Height = request.Video.Geometry.Height, PixelFormat = "yuv420p", DurationSeconds = seconds,
                FrameRate = frameRate, StartTimeSeconds = startTimeSeconds
            }]
        });
    }
    private sealed class PacketRunner : IMediaToolProcessRunner
    {
        public bool Fail { get; init; }
        public MediaToolProcessRequest? LastRequest { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new MediaToolProcessResult { ExitCode = Fail ? 1 : 0, StandardOutput = "10000\n20000\n" });
        }
    }
}
