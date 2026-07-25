using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdOutputValidationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _outputPath;
    private readonly DvdTitleCandidate _candidate;

    public DvdOutputValidationServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdValidationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _outputPath = Path.Combine(_root, "output.mkv");
        File.WriteAllBytes(_outputPath, new byte[] { 1, 2, 3 });
        _candidate = new DvdTitleCandidate
        {
            TitleSetId = "VTS_01",
            CombinedDurationSeconds = 1_000
        };
    }

    [Fact]
    public async Task ValidOutputPassesAllChecks()
    {
        var service = CreateService(CreateProbe(
            duration: 995,
            audioStreams: 2,
            subtitleStreams: 1));

        DvdOutputValidationResult result = await service.ValidateAsync(
            _outputPath,
            _candidate,
            expectedAudioStreams: 2,
            expectedSubtitleStreams: 1);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task MissingOrEmptyOutputFailsBeforeProbe()
    {
        string missing = Path.Combine(_root, "missing.mkv");
        var probe = new RecordingProbeService(CreateProbe(1_000, 1, 0));
        var service = new DvdOutputValidationService(probe);

        DvdOutputValidationResult missingResult = await service.ValidateAsync(
            missing,
            _candidate,
            1,
            0);
        File.WriteAllBytes(_outputPath, Array.Empty<byte>());
        DvdOutputValidationResult emptyResult = await service.ValidateAsync(
            _outputPath,
            _candidate,
            1,
            0);

        Assert.False(missingResult.Success);
        Assert.False(emptyResult.Success);
        Assert.Contains("empty", emptyResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task UnreadableProbeResultFailsValidation()
    {
        var service = CreateService(MediaProbeResult.Failed("Invalid Matroska data"));

        DvdOutputValidationResult result = await service.ValidateAsync(
            _outputPath,
            _candidate,
            0,
            0);

        Assert.False(result.Success);
        Assert.Contains("FFprobe", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutputWithoutVideoFailsValidation()
    {
        var probe = new MediaProbeResult
        {
            Success = true,
            DurationSeconds = 1_000,
            Streams = new[]
            {
                new MediaProbeStreamInfo { CodecType = "audio", CodecName = "ac3" }
            }
        };
        var service = CreateService(probe);

        DvdOutputValidationResult result = await service.ValidateAsync(
            _outputPath,
            _candidate,
            1,
            0);

        Assert.False(result.Success);
        Assert.Contains("video stream", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuspiciouslyShortOutputFailsValidation()
    {
        var service = CreateService(CreateProbe(
            duration: 800,
            audioStreams: 1,
            subtitleStreams: 0));

        DvdOutputValidationResult result = await service.ValidateAsync(
            _outputPath,
            _candidate,
            1,
            0);

        Assert.False(result.Success);
        Assert.Contains("shorter", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 1, 1, 0, "audio")]
    [InlineData(1, 0, 1, 1, "subtitle")]
    public async Task MissingSelectedStreamsFailValidation(
        int actualAudio,
        int actualSubtitles,
        int expectedAudio,
        int expectedSubtitles,
        string expectedMessage)
    {
        var service = CreateService(CreateProbe(
            duration: 1_000,
            audioStreams: actualAudio,
            subtitleStreams: actualSubtitles));

        DvdOutputValidationResult result = await service.ValidateAsync(
            _outputPath,
            _candidate,
            expectedAudio,
            expectedSubtitles);

        Assert.False(result.Success);
        Assert.Contains(expectedMessage, result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static DvdOutputValidationService CreateService(MediaProbeResult result)
    {
        return new DvdOutputValidationService(new RecordingProbeService(result));
    }

    private static MediaProbeResult CreateProbe(
        double duration,
        int audioStreams,
        int subtitleStreams)
    {
        var streams = new List<MediaProbeStreamInfo>
        {
            new() { CodecType = "video", CodecName = "mpeg2video" }
        };
        streams.AddRange(Enumerable.Range(0, audioStreams).Select(_ =>
            new MediaProbeStreamInfo { CodecType = "audio", CodecName = "ac3" }));
        streams.AddRange(Enumerable.Range(0, subtitleStreams).Select(_ =>
            new MediaProbeStreamInfo { CodecType = "subtitle", CodecName = "dvd_subtitle" }));
        return new MediaProbeResult
        {
            Success = true,
            DurationSeconds = duration,
            Streams = streams
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingProbeService : IMediaProbeService
    {
        private readonly MediaProbeResult _result;

        public RecordingProbeService(MediaProbeResult result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(_result);
        }
    }
}
