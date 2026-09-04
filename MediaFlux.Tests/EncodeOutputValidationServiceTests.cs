using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class EncodeOutputValidationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _sourcePath;
    private readonly string _outputPath;

    public EncodeOutputValidationServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-EncodeValidationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _sourcePath = Path.Combine(_root, "source unusual ' name [1].mkv");
        _outputPath = Path.Combine(_root, ".output unusual.mediaflux-test.mp4.partial");
        File.WriteAllBytes(_sourcePath, new byte[96 * 1024]);
        File.WriteAllBytes(_outputPath, new byte[96 * 1024]);
    }

    [Fact]
    public async Task ValidNormalEncodePassesProbeAndDecodeValidation()
    {
        var decode = new FakeDecodeService();
        var service = CreateService(SourceProbe(), OutputProbe(), decode);

        EncodeOutputValidationResult result =
            await service.ValidateStagedAsync(Request());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.Evidence);
        Assert.Equal(2, ((FakeProbeService)serviceProbe!).Calls);
        Assert.Equal(1, decode.Calls);
        Assert.Contains("decode-integrity", result.Summary);
    }

    [Theory]
    [InlineData(189871L, 189840L, 59.94)]
    [InlineData(215130L, 215102L, 59.94)]
    public void SmallMeasuredFrameBoundaryDeltaWithMatchingDurationPasses(long expected, long actual, double fps)
    {
        string error = EncodeOutputValidationService.ValidateProbe(
            FrameRequest(expected, FrameCountProvenance.Measured),
            FrameProbe(100, expected, fps), FrameProbe(100, actual, fps, output: true));

        Assert.Equal("", error);
    }

    [Fact]
    public void SameFrameDeltaWithMaterialDurationLossFails()
    {
        string error = EncodeOutputValidationService.ValidateProbe(
            FrameRequest(189871, FrameCountProvenance.Measured),
            FrameProbe(100, 189871, 59.94), FrameProbe(96, 189840, 59.94, output: true));

        Assert.Contains("duration differs", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaterialMeasuredFrameLossFails()
    {
        string error = EncodeOutputValidationService.ValidateProbe(
            FrameRequest(189871, FrameCountProvenance.Measured),
            FrameProbe(100, 189871, 59.94), FrameProbe(100, 189700, 59.94, output: true));

        Assert.Contains("frame deficit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SubtitleInflatedMkv_CompleteMappedMp4PassesAgainstVideoTimeline()
    {
        MediaProbeResult source = TopologyProbe(1475.25, 1419.96, 34045, includeOutlierSubtitle: true);
        MediaProbeResult output = TopologyProbe(1419.96, 1419.96, 34045, includeOutlierSubtitle: false, format: "mov,mp4,m4a,3gp,3g2,mj2");
        EncodeOutputValidationRequest request = TopologyRequest();

        Assert.Equal("", EncodeOutputValidationService.ValidateProbe(request, source, output));
    }

    [Fact]
    public void SubtitleInflatedMkv_TruncatedMp4StillFails()
    {
        MediaProbeResult source = TopologyProbe(1475.25, 1419.96, 34045, includeOutlierSubtitle: true);
        MediaProbeResult output = TopologyProbe(1364.67, 1364.67, 32700, includeOutlierSubtitle: false, format: "mov,mp4,m4a,3gp,3g2,mj2");

        string error = EncodeOutputValidationService.ValidateProbe(TopologyRequest(), source, output);

        Assert.Contains("duration differs", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CorruptOrUnprobeableOutputFailsClosed()
    {
        var service = CreateService(
            SourceProbe(),
            MediaProbeResult.Failed("invalid data"),
            new FakeDecodeService());

        EncodeOutputValidationResult result =
            await service.ValidateStagedAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("could not read", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-video")]
    [InlineData("wrong-codec")]
    [InlineData("wrong-resolution")]
    [InlineData("duration-mismatch")]
    [InlineData("missing-audio")]
    public async Task StructuralMismatchFailsWithSpecificReason(string scenario)
    {
        MediaProbeResult output = scenario switch
        {
            "missing-video" => OutputProbe(includeVideo: false),
            "wrong-codec" => OutputProbe(videoCodec: "h264"),
            "wrong-resolution" => OutputProbe(width: 1280, height: 720),
            "duration-mismatch" => OutputProbe(duration: 70),
            "missing-audio" => OutputProbe(audioCount: 0),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var service = CreateService(
            SourceProbe(),
            output,
            new FakeDecodeService());

        EncodeOutputValidationResult result =
            await service.ValidateStagedAsync(Request());

        Assert.False(result.Success);
        Assert.True(
            result.ErrorMessage.Contains(
                scenario switch
                {
                    "missing-video" => "video stream",
                    "wrong-codec" => "codec",
                    "wrong-resolution" => "resolution",
                    "duration-mismatch" => "duration",
                    "missing-audio" => "audio stream",
                    _ => ""
                },
                StringComparison.OrdinalIgnoreCase),
            result.ErrorMessage);
    }

    [Fact]
    public async Task ZeroOrTriviallySmallOutputFailsBeforeProbe()
    {
        File.WriteAllBytes(_outputPath, new byte[32]);
        var service = CreateService(
            SourceProbe(),
            OutputProbe(),
            new FakeDecodeService());

        EncodeOutputValidationResult result =
            await service.ValidateStagedAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("suspiciously small", result.ErrorMessage);
        Assert.Equal(1, ((FakeProbeService)serviceProbe!).Calls);
    }

    [Fact]
    public async Task SubtitlePreservationIsRequiredOnlyWhenMappingPromisesIt()
    {
        MediaProbeResult source = SourceProbe(subtitleCount: 1);
        MediaProbeResult withoutSubtitle = OutputProbe(subtitleCount: 0);

        var required = CreateService(source, withoutSubtitle, new FakeDecodeService());
        EncodeOutputValidationResult requiredResult =
            await required.ValidateStagedAsync(Request(copySubtitles: true));
        Assert.False(requiredResult.Success);
        Assert.Contains("subtitle", requiredResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var intentionallyDisabled =
            CreateService(source, withoutSubtitle, new FakeDecodeService());
        EncodeOutputValidationResult disabledResult =
            await intentionallyDisabled.ValidateStagedAsync(
                Request(copySubtitles: false));
        Assert.True(disabledResult.Success, disabledResult.ErrorMessage);
    }

    [Fact]
    public async Task DecodeSpotCheckFailureRejectsOtherwiseValidMedia()
    {
        var service = CreateService(
            SourceProbe(),
            OutputProbe(),
            new FakeDecodeService(success: false));

        EncodeOutputValidationResult result =
            await service.ValidateStagedAsync(Request());

        Assert.False(result.Success);
        Assert.Contains(
            "decode-integrity",
            result.ErrorMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameSizeOutputChangeDuringProbeFailsClosed()
    {
        DateTime changedTime = DateTime.UtcNow.AddMinutes(2);
        var probe = new FakeProbeService(path =>
        {
            if (path.Equals(_sourcePath, StringComparison.OrdinalIgnoreCase))
                return SourceProbe();

            File.SetLastWriteTimeUtc(_outputPath, changedTime);
            return OutputProbe();
        });
        var service = new EncodeOutputValidationService(
            probe,
            new FakeDecodeService());

        EncodeOutputValidationResult result =
            await service.ValidateStagedAsync(Request());

        Assert.False(result.Success);
        Assert.Contains("changed", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SameSizeModificationChangeBetweenStagedAndPromotedValidationFailsClosed()
    {
        string finalPath = Request().FinalOutputPath;
        File.Move(_outputPath, finalPath);
        long currentTicks = new FileInfo(finalPath).LastWriteTimeUtc.Ticks;
        var service = CreateService(
            SourceProbe(),
            OutputProbe(),
            new FakeDecodeService());
        var staged = new EncodeOutputValidationEvidence
        {
            SourceProbe = SourceProbe(),
            OutputProbe = OutputProbe(),
            OutputSizeBytes = new FileInfo(finalPath).Length,
            OutputLastWriteUtcTicks = currentTicks - TimeSpan.TicksPerSecond
        };

        EncodeOutputValidationResult result =
            await service.ValidatePromotedAsync(Request(), staged);

        Assert.False(result.Success);
        Assert.Contains("modification identity", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationRejectsWrongBitDepthContainerChaptersAndMetadata()
    {
        EncodeOutputValidationRequest tenBitRequest = Request(tenBit: true);
        Assert.Contains(
            "10-bit",
            EncodeOutputValidationService.ValidateProbe(
                tenBitRequest,
                SourceProbe(),
                OutputProbe()));

        MediaProbeResult wrongContainer = CloneProbe(
            OutputProbe(),
            formatName: "matroska,webm");
        Assert.Contains(
            "container",
            EncodeOutputValidationService.ValidateProbe(
                Request(),
                SourceProbe(),
                wrongContainer),
            StringComparison.OrdinalIgnoreCase);

        MediaProbeResult missingChapters = CloneProbe(
            OutputProbe(),
            chapters: Array.Empty<MediaProbeChapterInfo>());
        Assert.Contains(
            "chapter",
            EncodeOutputValidationService.ValidateProbe(
                Request(),
                SourceProbe(),
                missingChapters),
            StringComparison.OrdinalIgnoreCase);

        MediaProbeResult missingTitle = CloneProbe(
            OutputProbe(),
            formatTags: new Dictionary<string, string>());
        Assert.Contains(
            "title metadata",
            EncodeOutputValidationService.ValidateProbe(
                Request(),
                SourceProbe(),
                missingTitle),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitFinalResolutionStillRejectsAiIntermediateDimensions()
    {
        EncodeOutputValidationRequest request = Request(expectedWidth: 640, expectedHeight: 480);
        string error = EncodeOutputValidationService.ValidateProbe(request, SourceProbe(), OutputProbe(width: 1280, height: 960));

        Assert.Contains("1280", error);
        Assert.Contains("640", error);
    }

    [Fact]
    public void DegenerateSourceChapterDroppedByFfmpegPassesAfterNormalization()
    {
        MediaProbeResult source = CloneProbe(
            SourceProbe(),
            durationSeconds: 420.908,
            chapters: Chapters(
                (3, 0.000000, 0.000292, "Chapter 03"),
                (4, 0.000292, 379.629542, "Chapter 04"),
                (5, 379.629542, 420.908000, "Chapter 05")));
        MediaProbeResult output = CloneProbe(
            OutputProbe(duration: 420.825),
            chapters: Chapters(
                (4, 0.000000, 379.546542, "Chapter 04"),
                (5, 379.546542, 420.825000, "Chapter 05")));
        var log = new List<string>();

        string error = EncodeOutputValidationService.ValidateProbe(
            Request(), source, output, log.Add);

        Assert.Equal("", error);
        Assert.Contains(log, entry =>
            entry.Contains("Ignored malformed/degenerate source chapter 3", StringComparison.Ordinal) &&
            entry.Contains("below the 50 ms tolerance", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(10d, 10d)]
    [InlineData(10d, 9.9d)]
    public void ZeroOrNegativeDurationChapterIsIgnored(double start, double end)
    {
        MediaProbeResult source = CloneProbe(
            SourceProbe(),
            chapters: Chapters(
                (1, start, end, "Broken"),
                (2, 10, 60, "Meaningful")));
        MediaProbeResult output = CloneProbe(
            OutputProbe(),
            chapters: Chapters((2, 10, 60, "Meaningful")));

        string error = EncodeOutputValidationService.ValidateProbe(
            Request(), source, output);

        Assert.Equal("", error);
    }

    [Fact]
    public void SmallChapterTimestampRebasingAndRoundingPasses()
    {
        MediaProbeResult source = CloneProbe(
            SourceProbe(),
            durationSeconds: 180.083,
            chapters: Chapters(
                (1, 0.083, 120.083, "One"),
                (2, 120.083, 180.083, "Two")));
        MediaProbeResult output = CloneProbe(
            OutputProbe(duration: 180),
            chapters: Chapters(
                (1, 0, 120, "One"),
                (2, 120, 180, "Two")));

        Assert.Equal(
            "",
            EncodeOutputValidationService.ValidateProbe(Request(), source, output));
    }

    [Fact]
    public void MissingMeaningfulChapterFailsPreservationValidation()
    {
        MediaProbeResult source = CloneProbe(
            SourceProbe(),
            chapters: Chapters(
                (1, 0, 40, "One"),
                (2, 40, 100, "Two")));
        MediaProbeResult output = CloneProbe(
            OutputProbe(),
            chapters: Chapters((1, 0, 100, "One")));

        string error = EncodeOutputValidationService.ValidateProbe(
            Request(), source, output);

        Assert.Contains("Meaningful chapter preservation failed", error);
    }

    [Fact]
    public void NormalMatchingChaptersContinueToPass()
    {
        MediaProbeResult chapters = CloneProbe(
            SourceProbe(),
            chapters: Chapters(
                (1, 0, 40, "One"),
                (2, 40, 100, "Two")));

        Assert.Equal(
            "",
            EncodeOutputValidationService.ValidateProbe(
                Request(),
                chapters,
                CloneProbe(OutputProbe(), chapters: chapters.Chapters)));
    }

    [Fact]
    public void DvdValidationUsesCombinedLogicalDurationNotFirstSegmentDuration()
    {
        EncodeOutputValidationRequest request = new()
        {
            Input = new EncodingInputSource
            {
                Kind = EncodingInputKind.DvdPhysicalConcat,
                InputPath = "concat:segment-one|segment-two",
                SourcePath = _root,
                SourceFiles = new[] { _sourcePath },
                KnownDurationSeconds = 100,
                KnownAudioStreamCount = 1,
                AllowSourceDeletion = false
            },
            OutputPath = _outputPath,
            FinalOutputPath = Path.Combine(_root, "dvd final.mp4"),
            Encoder = new VideoEncoderSelection(
                VideoEncoderIds.Libx265,
                VideoCodecFamily.Hevc,
                "libx265")
        };

        MediaProbeResult representativeSegment = CloneProbe(
            SourceProbe(),
            durationSeconds: 20);
        string error = EncodeOutputValidationService.ValidateProbe(
            request,
            representativeSegment,
            OutputProbe(duration: 100));

        Assert.Equal("", error);
    }

    [Fact]
    public void DecodePositionsCoverBeginningMiddleAndEndWithoutDuplicates()
    {
        Assert.Equal(
            new[] { 0d, 50d, 99d },
            FfmpegDecodeIntegritySpotCheckService.BuildPositions(100));
        Assert.Equal(
            2,
            FfmpegDecodeIntegritySpotCheckService.BuildPositions(1).Count);
        Assert.Single(
            FfmpegDecodeIntegritySpotCheckService.BuildPositions(null));
    }

    private IMediaProbeService? serviceProbe;

    private EncodeOutputValidationService CreateService(
        MediaProbeResult source,
        MediaProbeResult output,
        IDecodeIntegritySpotCheckService decode)
    {
        var probe = new FakeProbeService(path =>
            path.Equals(_sourcePath, StringComparison.OrdinalIgnoreCase)
                ? source
                : output);
        serviceProbe = probe;
        return new EncodeOutputValidationService(probe, decode);
    }

    private EncodeOutputValidationRequest Request(
        bool copySubtitles = false,
        bool tenBit = false,
        int? expectedWidth = null,
        int? expectedHeight = null) => new()
    {
        Input = EncodingInputSource.FromFile(_sourcePath),
        OutputPath = _outputPath,
        FinalOutputPath = Path.Combine(_root, "final output.mp4"),
        Encoder = new VideoEncoderSelection(
            VideoEncoderIds.Libx265,
            VideoCodecFamily.Hevc,
            "libx265"),
        ScaleMode = EncodingService.ScaleMode.None,
        TenBit = tenBit,
        MapMode = EncodingService.StreamMapMode.KeepAll,
        CopySubtitles = copySubtitles,
        ExpectedVideoWidth = expectedWidth,
        ExpectedVideoHeight = expectedHeight
    };

    private static EncodeOutputValidationRequest FrameRequest(long expected, FrameCountProvenance provenance) => new()
    {
        Input = EncodingInputSource.FromFile("frame-topology.mkv"),
        Encoder = new VideoEncoderSelection(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265"),
        ExpectedVideoFrameCount = expected,
        ExpectedVideoFrameCountProvenance = provenance,
        ExpectedDurationSeconds = 100,
        ContainerDecision = new OutputContainerDecision { Requested = OutputContainerSelection.Mp4, Resolved = OutputContainer.Mp4, Reason = "test" }
    };

    private static MediaProbeResult FrameProbe(double duration, long frames, double fps, bool output = false) => new()
    {
        Success = true,
        FormatName = output ? "mov,mp4,m4a,3gp,3g2,mj2" : "matroska,webm",
        DurationSeconds = duration,
        Streams = new[]
        {
            new MediaProbeStreamInfo { Index = 0, CodecType = "video", CodecName = output ? "hevc" : "h264", Width = 1920, Height = 1080, PixelFormat = "yuv420p", DurationSeconds = duration, FrameCount = frames, FrameRate = fps },
            new MediaProbeStreamInfo { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 2 }
        }
    };

    private static MediaProbeResult CloneProbe(
        MediaProbeResult source,
        string? formatName = null,
        double? durationSeconds = null,
        IReadOnlyList<MediaProbeChapterInfo>? chapters = null,
        IReadOnlyDictionary<string, string>? formatTags = null) => new()
    {
        Success = source.Success,
        ErrorMessage = source.ErrorMessage,
        FormatName = formatName ?? source.FormatName,
        SizeBytes = source.SizeBytes,
        DurationSeconds = durationSeconds ?? source.DurationSeconds,
        BitRate = source.BitRate,
        Streams = source.Streams,
        Chapters = chapters ?? source.Chapters,
        FormatTags = formatTags ?? source.FormatTags
    };

    private static MediaProbeResult SourceProbe(
        int subtitleCount = 0) =>
        Probe(
            format: "matroska,webm",
            videoCodec: "h264",
            width: 1920,
            height: 1080,
            duration: 100,
            audioCount: 1,
            subtitleCount: subtitleCount,
            chapterCount: 2,
            title: "Validation Test");

    private static MediaProbeResult OutputProbe(
        bool includeVideo = true,
        string videoCodec = "hevc",
        int width = 1920,
        int height = 1080,
        double duration = 100,
        int audioCount = 1,
        int subtitleCount = 0) =>
        Probe(
            format: "mov,mp4,m4a,3gp,3g2,mj2",
            videoCodec: videoCodec,
            width: width,
            height: height,
            duration: duration,
            audioCount: audioCount,
            subtitleCount: subtitleCount,
            chapterCount: 2,
            title: "Validation Test",
            includeVideo: includeVideo);

    private static MediaProbeResult Probe(
        string format,
        string videoCodec,
        int width,
        int height,
        double duration,
        int audioCount,
        int subtitleCount,
        int chapterCount,
        string title,
        bool includeVideo = true)
    {
        var streams = new List<MediaProbeStreamInfo>();
        if (includeVideo)
        {
            streams.Add(new MediaProbeStreamInfo
            {
                Index = 0,
                CodecType = "video",
                CodecName = videoCodec,
                PixelFormat = "yuv420p",
                Width = width,
                Height = height,
                DurationSeconds = duration
            });
        }
        streams.AddRange(Enumerable.Range(0, audioCount).Select(index =>
            new MediaProbeStreamInfo
            {
                Index = 10 + index,
                CodecType = "audio",
                CodecName = "aac",
                Channels = 2
            }));
        streams.AddRange(Enumerable.Range(0, subtitleCount).Select(index =>
            new MediaProbeStreamInfo
            {
                Index = 20 + index,
                CodecType = "subtitle",
                CodecName = "mov_text"
            }));

        return new MediaProbeResult
        {
            Success = true,
            FormatName = format,
            DurationSeconds = duration,
            Streams = streams,
            Chapters = Enumerable.Range(0, chapterCount)
                .Select(index => new MediaProbeChapterInfo { Id = index })
                .ToArray(),
            FormatTags = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = title
            }
        };
    }

    private static EncodeOutputValidationRequest TopologyRequest() => new()
    {
        Input = EncodingInputSource.FromFile("topology.mkv"),
        Encoder = new VideoEncoderSelection(VideoEncoderIds.Libx265, VideoCodecFamily.Hevc, "libx265"),
        ExpectedDurationSeconds = 1419.96,
        ExpectedVideoFrameCount = 34045,
        ContainerDecision = new OutputContainerDecision { Requested = OutputContainerSelection.Mp4, Resolved = OutputContainer.Mp4, Reason = "test" }
    };

    private static MediaProbeResult TopologyProbe(double containerDuration, double videoDuration, long frames, bool includeOutlierSubtitle, string format = "matroska,webm") => new()
    {
        Success = true, FormatName = format, DurationSeconds = containerDuration,
        Streams = new MediaProbeStreamInfo[]
        {
            new() { Index = 0, CodecType = "video", CodecName = format.StartsWith("mov") ? "hevc" : "h264", Width = 1920, Height = 1080, PixelFormat = "yuv420p", DurationSeconds = videoDuration, FrameCount = frames, FrameRate = 34045d / 1419.96d },
            new() { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 2, DurationSeconds = 1420 },
            new() { Index = 2, CodecType = "audio", CodecName = "aac", Channels = 2, DurationSeconds = 1420 }
        }.Concat(includeOutlierSubtitle ? new[] { new MediaProbeStreamInfo { Index = 3, CodecType = "subtitle", CodecName = "ass", DurationSeconds = 1475.25 } } : Array.Empty<MediaProbeStreamInfo>()).ToArray()
    };

    private static IReadOnlyList<MediaProbeChapterInfo> Chapters(
        params (int Id, double Start, double End, string Title)[] values) =>
        values.Select(value => new MediaProbeChapterInfo
        {
            Id = value.Id,
            StartSeconds = value.Start,
            EndSeconds = value.End,
            Title = value.Title
        }).ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
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

    private sealed class FakeDecodeService : IDecodeIntegritySpotCheckService
    {
        private readonly bool _success;

        public FakeDecodeService(bool success = true)
        {
            _success = success;
        }

        public int Calls { get; private set; }

        public Task<DecodeIntegritySpotCheckResult> CheckAsync(
            string outputPath,
            double? durationSeconds,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DecodeIntegritySpotCheckResult
            {
                Success = _success,
                ErrorMessage = _success ? "" : "simulated corrupt frame",
                PositionsSeconds = new[] { 0d, 50d, 99d }
            });
        }
    }
}
