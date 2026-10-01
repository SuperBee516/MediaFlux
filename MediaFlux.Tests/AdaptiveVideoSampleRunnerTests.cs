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
            RepresentativeSample sample = StorageSavingsSampleSelector.Positions(request.SourceDuration)[0];
            Task<RepresentativeSampleEvidence> work = runner.MeasureAsync(request, 24, sample, cancellation.Token);
            if (outcome == "success")
            {
                var measured = await work;
                Assert.Equal(30_000, measured.VideoBytes); // packet bytes, not file or audio bytes
                Assert.Equal(25, measured.MeasuredSeconds);
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

    private sealed class Probe(AdaptiveQualitySelectionRequest request) : IMediaProbeService
    {
        public Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(new MediaProbeResult
        {
            Success = true, DurationSeconds = 25, Streams = [new()
            {
                CodecType = "video", CodecName = "hevc", Width = request.Video.Geometry.Width,
                Height = request.Video.Geometry.Height, PixelFormat = "yuv420p"
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
