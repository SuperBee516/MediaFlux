using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class MediaRemuxServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _ffmpegPath;

    public MediaRemuxServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-MediaRemuxTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _ffmpegPath = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllBytes(_ffmpegPath, new byte[] { 1 });
    }

    [Fact]
    public void ArgumentsMapNormalStreamsAndUseOnlyStreamCopy()
    {
        IReadOnlyList<string> arguments = MediaRemuxService.BuildArguments(
            @"C:\Videos\source.ts",
            @"C:\Output\.source.partial.mkv");

        Assert.Contains("-c", arguments);
        Assert.Contains("copy", arguments);
        Assert.Contains("0:v?", arguments);
        Assert.Contains("0:a?", arguments);
        Assert.Contains("0:s?", arguments);
        Assert.Contains("0:t?", arguments);
        Assert.Contains("-map_metadata", arguments);
        Assert.Contains("-map_chapters", arguments);
        Assert.DoesNotContain("libx264", arguments);
        Assert.DoesNotContain("libx265", arguments);
        Assert.DoesNotContain("hevc_nvenc", arguments);
    }

    [Fact]
    public void RemuxHistoryTypeDoesNotRenumberExistingPersistedValues()
    {
        Assert.Equal(0, (int)JobType.Encode);
        Assert.Equal(1, (int)JobType.Download);
        Assert.Equal(2, (int)JobType.Audio);
        Assert.Equal(3, (int)JobType.DvdEncode);
        Assert.Equal(4, (int)JobType.DvdRemux);
        Assert.Equal(5, (int)JobType.Remux);
    }

    [Fact]
    public async Task SuccessfulRemuxValidatesThenPromotesStagedOutput()
    {
        string source = CreateSource();
        string output = Path.Combine(_root, "output.mkv");
        var runner = new FakeProcessRunner(request =>
        {
            File.WriteAllBytes(request.Arguments[^1], new byte[] { 1, 2, 3 });
            return new MediaToolProcessResult { ExitCode = 0 };
        });
        var probe = new FakeProbeService(_ => MatchingProbe());
        var service = new MediaRemuxService(
            _ffmpegPath,
            runner,
            probe);

        MediaRemuxResult result = await service.RemuxAsync(
            new MediaRemuxRequest
            {
                SourcePath = source,
                OutputPath = output
            });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(output, result.OutputPath);
        Assert.True(File.Exists(output));
        Assert.True(result.CleanupSucceeded);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => path.Contains(".partial.", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task ValidationFailureNeverCreatesFinalOutputAndCleansStage()
    {
        string source = CreateSource();
        string output = Path.Combine(_root, "invalid.mkv");
        int probeCall = 0;
        var probe = new FakeProbeService(_ =>
        {
            probeCall++;
            return probeCall == 1
                ? MatchingProbe()
                : MatchingProbe(audioCodec: "opus");
        });
        var runner = new FakeProcessRunner(request =>
        {
            File.WriteAllBytes(request.Arguments[^1], new byte[] { 1, 2, 3 });
            return new MediaToolProcessResult { ExitCode = 0 };
        });
        var service = new MediaRemuxService(
            _ffmpegPath,
            runner,
            probe);

        MediaRemuxResult result = await service.RemuxAsync(
            new MediaRemuxRequest
            {
                SourcePath = source,
                OutputPath = output
            });

        Assert.False(result.Success);
        Assert.Contains("stream set changed", result.ErrorMessage);
        Assert.False(File.Exists(output));
        Assert.True(result.CleanupSucceeded);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => path.Contains(".partial.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FfmpegFailureDoesNotFallBackToEncoding()
    {
        string source = CreateSource();
        string output = Path.Combine(_root, "failed.mkv");
        var runner = new FakeProcessRunner(request =>
        {
            File.WriteAllBytes(request.Arguments[^1], new byte[] { 1, 2, 3 });
            return new MediaToolProcessResult
            {
                ExitCode = 1,
                StandardError = "Subtitle codec is not supported"
            };
        });
        var service = new MediaRemuxService(
            _ffmpegPath,
            runner,
            new FakeProbeService(_ => MatchingProbe()));

        MediaRemuxResult result = await service.RemuxAsync(
            new MediaRemuxRequest
            {
                SourcePath = source,
                OutputPath = output
            });

        Assert.False(result.Success);
        Assert.Contains("did not re-encode", result.ErrorMessage);
        Assert.False(File.Exists(output));
        Assert.True(result.CleanupSucceeded);
        Assert.Single(runner.Requests);
        Assert.Contains("copy", runner.Requests[0].Arguments);
    }

    [Fact]
    public async Task CancellationDuringSourceProbeReturnsCanceledWithoutOutput()
    {
        string source = CreateSource();
        string output = Path.Combine(_root, "canceled.mkv");
        var runner = new FakeProcessRunner(_ =>
            throw new InvalidOperationException("FFmpeg must not start."));
        var probe = new FakeProbeService(_ =>
            throw new OperationCanceledException());
        var service = new MediaRemuxService(
            _ffmpegPath,
            runner,
            probe);

        MediaRemuxResult result = await service.RemuxAsync(
            new MediaRemuxRequest
            {
                SourcePath = source,
                OutputPath = output
            });

        Assert.True(result.WasCanceled);
        Assert.False(result.Success);
        Assert.Empty(runner.Requests);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void ValidationRejectsMissingChaptersOrDurationDrift()
    {
        MediaProbeResult source = MatchingProbe(chapterCount: 2);
        MediaProbeResult missingChapters = MatchingProbe(chapterCount: 1);
        MediaProbeResult durationDrift = MatchingProbe(
            chapterCount: 2,
            durationSeconds: 80);

        Assert.Contains(
            "chapter",
            MediaRemuxService.ValidateOutput(source, missingChapters),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "duration",
            MediaRemuxService.ValidateOutput(source, durationDrift),
            StringComparison.OrdinalIgnoreCase);
    }

    private string CreateSource()
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ts");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return path;
    }

    private static MediaProbeResult MatchingProbe(
        string audioCodec = "aac",
        int chapterCount = 2,
        double durationSeconds = 100)
    {
        return new MediaProbeResult
        {
            Success = true,
            DurationSeconds = durationSeconds,
            Streams = new[]
            {
                new MediaProbeStreamInfo
                {
                    Index = 0,
                    CodecType = "video",
                    CodecName = "h264"
                },
                new MediaProbeStreamInfo
                {
                    Index = 1,
                    CodecType = "audio",
                    CodecName = audioCodec
                },
                new MediaProbeStreamInfo
                {
                    Index = 2,
                    CodecType = "subtitle",
                    CodecName = "subrip"
                }
            },
            Chapters = Enumerable.Range(0, chapterCount)
                .Select(index => new MediaProbeChapterInfo { Id = index })
                .ToArray()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProcessRunner : IMediaToolProcessRunner
    {
        private readonly Func<MediaToolProcessRequest, MediaToolProcessResult> _handler;

        public FakeProcessRunner(
            Func<MediaToolProcessRequest, MediaToolProcessResult> handler)
        {
            _handler = handler;
        }

        public List<MediaToolProcessRequest> Requests { get; } = new();

        public Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_handler(request));
        }
    }

    private sealed class FakeProbeService : IMediaProbeService
    {
        private readonly Func<string, MediaProbeResult> _handler;

        public FakeProbeService(Func<string, MediaProbeResult> handler)
        {
            _handler = handler;
        }

        public int Calls { get; private set; }

        public Task<MediaProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(_handler(path));
        }
    }
}
