using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdRemuxServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _ffmpegPath;
    private readonly string _videoTs;
    private readonly string _outputFolder;
    private readonly DvdTitleCandidate _candidate;

    public DvdRemuxServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdRemuxTests",
            Guid.NewGuid().ToString("N"));
        _ffmpegPath = Path.Combine(_root, "ffmpeg.exe");
        _videoTs = Path.Combine(_root, "Movie", "VIDEO_TS");
        _outputFolder = Path.Combine(_root, "output");
        Directory.CreateDirectory(_videoTs);
        Directory.CreateDirectory(_outputFolder);
        File.WriteAllBytes(_ffmpegPath, new byte[] { 1 });
        _candidate = CreateCandidate();
    }

    [Fact]
    public async Task SuccessfulRemuxUsesOnlyStreamCopyValidatesAndMovesStagedOutput()
    {
        var runner = new FakeProcessRunner(async (request, cancellationToken) =>
        {
            request.StandardOutputLineCallback?.Invoke("out_time_us=60000000");
            await File.WriteAllBytesAsync(
                request.Arguments[^1],
                new byte[] { 9, 8, 7 },
                cancellationToken);
            return new MediaToolProcessResult { ExitCode = 0 };
        });
        var validator = new FakeValidator(success: true);
        DvdRemuxService service = CreateService(runner, validator);
        string output = Path.Combine(_outputFolder, "Movie.mkv");
        var sourceBefore = SnapshotSources();

        DvdRemuxResult result = await service.RemuxAsync(CreateOptions(output));

        Assert.True(result.Success);
        Assert.Equal(output, result.OutputPath);
        Assert.True(File.Exists(output));
        Assert.Equal(1, validator.CallCount);
        Assert.NotNull(runner.LastRequest);
        Assert.Contains(
            runner.LastRequest!.Arguments,
            argument => argument.StartsWith("concat:file:", StringComparison.Ordinal));
        Assert.DoesNotContain("-f", runner.LastRequest.Arguments);
        Assert.DoesNotContain("-safe", runner.LastRequest.Arguments);
        AssertArgumentPair(runner.LastRequest.Arguments, "-fflags", "+genpts");
        AssertArgumentPair(runner.LastRequest.Arguments, "-c", "copy");
        Assert.DoesNotContain(runner.LastRequest.Arguments, argument =>
            argument.Contains("libx", StringComparison.OrdinalIgnoreCase) ||
            argument.Contains("nvenc", StringComparison.OrdinalIgnoreCase) ||
            argument.Equals("aac", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, runner.LastRequest.Arguments.Count(argument => argument == "-map"));
        Assert.Empty(Directory.EnumerateFiles(_outputFolder, "*.partial.mkv"));
        AssertSourcesUnchanged(sourceBefore);
    }

    [Fact]
    public async Task RemuxFailureIdentifiesOptionalStreamAndNeverCreatesFinalOutput()
    {
        var runner = new FakeProcessRunner(async (request, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(
                request.Arguments[^1],
                new byte[] { 1, 2 },
                cancellationToken);
            return new MediaToolProcessResult
            {
                ExitCode = 1,
                StandardError =
                    "Could not find tag for codec dvd_subtitle in stream #0:3"
            };
        });
        var validator = new FakeValidator(success: true);
        DvdRemuxService service = CreateService(runner, validator);
        string output = Path.Combine(_outputFolder, "failed.mkv");

        DvdRemuxResult result = await service.RemuxAsync(CreateOptions(output));

        Assert.False(result.Success);
        Assert.Equal(3, result.FailedSourceStreamIndex);
        Assert.Contains("subtitle", result.FailedStreamDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not automatically re-encode", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
        Assert.Equal(0, validator.CallCount);
        Assert.Empty(Directory.EnumerateFiles(_outputFolder, "*.partial.mkv"));
    }

    [Fact]
    public async Task CancellationRemovesIncompleteStagingOutput()
    {
        var runner = new FakeProcessRunner(async (request, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(
                request.Arguments[^1],
                new byte[] { 1, 2, 3 },
                CancellationToken.None);
            throw new OperationCanceledException(cancellationToken);
        });
        DvdRemuxService service = CreateService(runner, new FakeValidator(success: true));
        string output = Path.Combine(_outputFolder, "canceled.mkv");

        DvdRemuxResult result = await service.RemuxAsync(CreateOptions(output));

        Assert.True(result.WasCanceled);
        Assert.False(result.Success);
        Assert.True(result.CleanupSucceeded);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_outputFolder, "*.partial.mkv"));
    }

    [Fact]
    public async Task ValidationFailureDeletesStagingAndDoesNotReportSuccess()
    {
        var runner = CreateSuccessfulRunner();
        var validator = new FakeValidator(
            success: false,
            error: "The output is suspiciously shorter than the source.");
        DvdRemuxService service = CreateService(runner, validator);
        string output = Path.Combine(_outputFolder, "invalid.mkv");

        DvdRemuxResult result = await service.RemuxAsync(CreateOptions(output));

        Assert.False(result.Success);
        Assert.Contains("failed validation", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(_outputFolder, "*.partial.mkv"));
    }

    [Fact]
    public async Task ExistingOutputIsPreservedWhenReplacementFailsValidation()
    {
        string output = Path.Combine(_outputFolder, "existing.mkv");
        byte[] original = Encoding.UTF8.GetBytes("existing-good-file");
        File.WriteAllBytes(output, original);
        var validator = new FakeValidator(success: false, error: "Invalid output");
        DvdRemuxService service = CreateService(CreateSuccessfulRunner(), validator);

        DvdRemuxResult result = await service.RemuxAsync(
            CreateOptions(output, overwrite: true));

        Assert.False(result.Success);
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Empty(Directory.EnumerateFiles(_outputFolder, "*.partial.mkv"));
    }

    [Fact]
    public async Task ExistingOutputWithoutExplicitOverwriteIsRejectedBeforeFfmpeg()
    {
        string output = Path.Combine(_outputFolder, "existing-no.mkv");
        File.WriteAllBytes(output, new byte[] { 1 });
        var runner = CreateSuccessfulRunner();
        DvdRemuxService service = CreateService(runner, new FakeValidator(success: true));

        DvdRemuxResult result = await service.RemuxAsync(
            CreateOptions(output, overwrite: false));

        Assert.False(result.Success);
        Assert.Contains("already exists", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task OutputInsideVideoTsIsRejectedAndSourcesRemainUntouched()
    {
        string output = Path.Combine(_videoTs, "Movie.mkv");
        var runner = CreateSuccessfulRunner();
        DvdRemuxService service = CreateService(runner, new FakeValidator(success: true));
        var sourceBefore = SnapshotSources();

        DvdRemuxResult result = await service.RemuxAsync(CreateOptions(output));

        Assert.False(result.Success);
        Assert.Contains("source VIDEO_TS", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.CallCount);
        AssertSourcesUnchanged(sourceBefore);
    }

    [Fact]
    public async Task UnselectedOptionalStreamsAreNotMapped()
    {
        var runner = CreateSuccessfulRunner();
        DvdRemuxService service = CreateService(runner, new FakeValidator(success: true));
        var options = new DvdImportOptions
        {
            Candidate = _candidate,
            OutputMode = DvdOutputMode.LosslessRemuxToMkv,
            OutputPath = Path.Combine(_outputFolder, "video-only.mkv"),
            SelectedAudioStreamIndexes = Array.Empty<int>(),
            SelectedSubtitleStreamIndexes = Array.Empty<int>()
        };

        DvdRemuxResult result = await service.RemuxAsync(options);

        Assert.True(result.Success);
        Assert.Equal(1, runner.LastRequest!.Arguments.Count(argument => argument == "-map"));
    }

    private DvdRemuxService CreateService(
        IMediaToolProcessRunner runner,
        IDvdOutputValidationService validator)
    {
        return new DvdRemuxService(
            _ffmpegPath,
            runner,
            validator);
    }

    private FakeProcessRunner CreateSuccessfulRunner()
    {
        return new FakeProcessRunner(async (request, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(
                request.Arguments[^1],
                new byte[] { 5, 4, 3 },
                cancellationToken);
            return new MediaToolProcessResult { ExitCode = 0 };
        });
    }

    private DvdImportOptions CreateOptions(string output, bool overwrite = false) => new()
    {
        Candidate = _candidate,
        OutputMode = DvdOutputMode.LosslessRemuxToMkv,
        OutputPath = output,
        SelectedAudioStreamIndexes = new[] { 2 },
        SelectedSubtitleStreamIndexes = new[] { 3 },
        OverwriteExistingOutput = overwrite
    };

    private DvdTitleCandidate CreateCandidate()
    {
        var streams = new MediaProbeStreamInfo[]
        {
            new()
            {
                Index = 0,
                Id = "0x1bf",
                CodecType = "data",
                CodecName = "dvd_nav_packet",
                TimeBase = "1/90000"
            },
            new()
            {
                Index = 1,
                Id = "0x1e0",
                CodecType = "video",
                CodecName = "mpeg2video",
                TimeBase = "1/90000",
                Width = 720,
                Height = 480
            },
            new()
            {
                Index = 2,
                Id = "0x80",
                CodecType = "audio",
                CodecName = "ac3",
                TimeBase = "1/90000",
                Language = "eng"
            },
            new()
            {
                Index = 3,
                Id = "0x20",
                CodecType = "subtitle",
                CodecName = "dvd_subtitle",
                TimeBase = "1/90000",
                Language = "eng"
            }
        };
        var segments = Enumerable.Range(1, 2).Select(number =>
        {
            string path = Path.Combine(_videoTs, $"VTS_01_{number}.VOB");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"source-segment-{number}"));
            return new DvdSegmentInfo
            {
                Path = path,
                SegmentNumber = number,
                SizeBytes = new FileInfo(path).Length,
                IsReadable = true,
                ProbeResult = new MediaProbeResult
                {
                    Success = true,
                    DurationSeconds = 60,
                    Streams = streams
                }
            };
        }).ToArray();
        return new DvdTitleCandidate
        {
            TitleSetId = "VTS_01",
            Segments = segments,
            StartsAtSegmentOne = true,
            HasConsistentStreams = true,
            IsValidForConversion = true,
            CombinedDurationSeconds = 120,
            CombinedSizeBytes = segments.Sum(segment => segment.SizeBytes),
            VideoCodec = "mpeg2video",
            VideoWidth = 720,
            VideoHeight = 480,
            AudioStreamCount = 1,
            SubtitleStreamCount = 1
        };
    }

    private Dictionary<string, byte[]> SnapshotSources()
    {
        return _candidate.Segments.ToDictionary(
            segment => segment.Path,
            segment => File.ReadAllBytes(segment.Path));
    }

    private void AssertSourcesUnchanged(IReadOnlyDictionary<string, byte[]> before)
    {
        foreach (DvdSegmentInfo segment in _candidate.Segments)
            Assert.Equal(before[segment.Path], File.ReadAllBytes(segment.Path));
    }

    private static void AssertArgumentPair(
        IReadOnlyList<string> arguments,
        string option,
        string value)
    {
        int index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count);
        Assert.Equal(value, arguments[index + 1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProcessRunner : IMediaToolProcessRunner
    {
        private readonly Func<
            MediaToolProcessRequest,
            CancellationToken,
            Task<MediaToolProcessResult>> _handler;

        public FakeProcessRunner(
            Func<
                MediaToolProcessRequest,
                CancellationToken,
                Task<MediaToolProcessResult>> handler)
        {
            _handler = handler;
        }

        public int CallCount { get; private set; }
        public MediaToolProcessRequest? LastRequest { get; private set; }

        public async Task<MediaToolProcessResult> RunAsync(
            MediaToolProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return await _handler(request, cancellationToken);
        }
    }

    private sealed class FakeValidator : IDvdOutputValidationService
    {
        private readonly bool _success;
        private readonly string _error;

        public FakeValidator(bool success, string error = "")
        {
            _success = success;
            _error = error;
        }

        public int CallCount { get; private set; }

        public Task<DvdOutputValidationResult> ValidateAsync(
            string outputPath,
            DvdTitleCandidate sourceCandidate,
            int expectedAudioStreams,
            int expectedSubtitleStreams,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new DvdOutputValidationResult
            {
                Success = _success,
                ErrorMessage = _error
            });
        }
    }
}
