using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class FfprobeServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _ffprobePath;
    private readonly string _mediaPath;

    public FfprobeServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-FfprobeTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ffprobePath = Path.Combine(_root, "ffprobe.exe");
        _mediaPath = Path.Combine(_root, "VTS_01_1.VOB");
        File.WriteAllBytes(_ffprobePath, new byte[] { 1 });
        File.WriteAllBytes(_mediaPath, new byte[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task ProbeAsyncParsesRichStreamAndChapterMetadata()
    {
        var runner = new FakeProcessRunner
        {
            Result = new MediaToolProcessResult
            {
                ExitCode = 0,
                StandardOutput =
                    """
                    {
                      "streams": [
                        {
                          "index": 0,
                          "id": "0x1e0",
                          "codec_name": "mpeg2video",
                          "codec_long_name": "MPEG-2 video",
                          "profile": "Main",
                          "level": 8,
                          "bit_rate": "6500000",
                          "codec_type": "video",
                          "width": 720,
                          "height": 480,
                          "display_aspect_ratio": "16:9",
                          "pix_fmt": "yuv420p",
                          "field_order": "tt",
                          "bits_per_raw_sample": "8",
                          "color_range": "tv",
                          "color_space": "smpte170m",
                          "color_transfer": "bt709",
                          "color_primaries": "smpte170m",
                          "avg_frame_rate": "30000/1001",
                          "time_base": "1/90000",
                          "duration": "120.5",
                          "disposition": { "default": 1 }
                        },
                        {
                          "index": 1,
                          "id": "0x80",
                          "codec_name": "ac3",
                          "codec_type": "audio",
                          "channels": 6,
                          "channel_layout": "5.1(side)",
                          "time_base": "1/90000",
                          "tags": { "language": "eng" },
                          "disposition": { "default": 1 }
                        },
                        {
                          "index": 2,
                          "id": "0x20",
                          "codec_name": "dvd_subtitle",
                          "codec_type": "subtitle",
                          "tags": { "language": "spa" },
                          "disposition": { "forced": 1 }
                        }
                      ],
                      "chapters": [
                        {
                          "id": 0,
                          "start_time": "0.0",
                          "end_time": "60.0",
                          "tags": { "title": "Chapter 1" }
                        }
                      ],
                      "format": {
                        "format_name": "mpeg",
                        "duration": "120.5",
                        "size": "1048576",
                        "bit_rate": "7000000"
                      }
                    }
                    """
            }
        };
        var service = new FfprobeService(_ffprobePath, runner);

        var result = await service.ProbeAsync(_mediaPath);

        Assert.True(result.Success);
        Assert.Equal("mpeg", result.FormatName);
        Assert.Equal(120.5, result.DurationSeconds);
        Assert.Equal(1_048_576, result.SizeBytes);
        Assert.Equal(7_000_000, result.BitRate);
        Assert.Equal(3, result.Streams.Count);
        Assert.Equal("0x1e0", result.Streams[0].Id);
        Assert.Equal(720, result.Streams[0].Width);
        Assert.Equal("Main", result.Streams[0].Profile);
        Assert.Equal(8, result.Streams[0].Level);
        Assert.Equal(6_500_000, result.Streams[0].BitRate);
        Assert.Equal(8, result.Streams[0].BitsPerRawSample);
        Assert.Equal("smpte170m", result.Streams[0].ColorSpace);
        Assert.Equal(30000d / 1001d, result.Streams[0].FrameRate!.Value, precision: 6);
        Assert.Equal("eng", result.Streams[1].Language);
        Assert.True(result.Streams[2].Dispositions["forced"]);
        Assert.Equal("Chapter 1", Assert.Single(result.Chapters).Title);
        Assert.Equal(_mediaPath, runner.LastRequest?.Arguments[^1]);
        Assert.Contains("-show_streams", runner.LastRequest?.Arguments ?? Array.Empty<string>());
        Assert.Contains("-show_chapters", runner.LastRequest?.Arguments ?? Array.Empty<string>());
    }

    [Fact]
    public async Task NonzeroExitReturnsUsefulProbeFailure()
    {
        var runner = new FakeProcessRunner
        {
            Result = new MediaToolProcessResult
            {
                ExitCode = 1,
                StandardOutput = """{ "error": { "code": -1094995529, "string": "Invalid data found when processing input" } }""",
                StandardError = "probe failed"
            }
        };
        var service = new FfprobeService(_ffprobePath, runner);

        var result = await service.ProbeAsync(_mediaPath);

        Assert.False(result.Success);
        Assert.Contains("Invalid data", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TimedOutProcessReturnsExplicitFailure()
    {
        var runner = new FakeProcessRunner
        {
            Result = new MediaToolProcessResult
            {
                ExitCode = -1,
                TimedOut = true
            }
        };
        var service = new FfprobeService(
            _ffprobePath,
            runner,
            TimeSpan.FromSeconds(12));

        var result = await service.ProbeAsync(_mediaPath);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedIntoProbeFailure()
    {
        var runner = new CancelingProcessRunner();
        var service = new FfprobeService(_ffprobePath, runner);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ProbeAsync(_mediaPath, cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProcessRunner : IMediaToolProcessRunner
    {
        public MediaToolProcessResult Result { get; init; } = new();
        public MediaToolProcessRequest? LastRequest { get; private set; }

        public Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class CancelingProcessRunner : IMediaToolProcessRunner
    {
        public Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
