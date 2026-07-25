using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdEncodingInputFactoryTests : IDisposable
{
    private readonly string _root;

    public DvdEncodingInputFactoryTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdPhase3Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void CreatesDirectConcatInputWithSelectedStreamsAndLogicalOutputName()
    {
        DvdImportOptions options = CreateOptions();
        var factory = new DvdEncodingInputFactory();

        EncodingInputSource input = factory.Create(options);

        Assert.Equal(EncodingInputKind.DvdPhysicalConcat, input.Kind);
        Assert.StartsWith("concat:", input.InputPath, StringComparison.Ordinal);
        Assert.Equal(
            options.Candidate.Segments.Select(segment => segment.Path),
            input.SourceFiles);
        Assert.Equal(
            Path.GetDirectoryName(options.Candidate.Segments[0].Path),
            input.SourcePath);
        Assert.Equal("Movie Name", input.OutputBaseName);
        Assert.Equal(options.Candidate.CombinedDurationSeconds, input.KnownDurationSeconds);
        Assert.Equal(new[] { 1 }, input.VideoStreamIndexes);
        Assert.Equal(new[] { 2 }, input.AudioStreamIndexes);
        Assert.Empty(input.SubtitleStreamIndexes);
        Assert.False(input.AllowSourceDeletion);
        Assert.False(input.ShouldDeleteSource(deleteRequested: true));
    }

    [Fact]
    public void CreatingPhysicalInputDoesNotCreateTemporaryFilesOrModifySources()
    {
        DvdImportOptions options = CreateOptions();
        byte[][] sourceBefore = options.Candidate.Segments
            .Select(segment => File.ReadAllBytes(segment.Path))
            .ToArray();
        var factory = new DvdEncodingInputFactory();

        EncodingInputSource input = factory.Create(options);

        Assert.StartsWith("concat:", input.InputPath, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateDirectories(_root, "dvd-*", SearchOption.AllDirectories));
        for (int index = 0; index < options.Candidate.Segments.Count; index++)
        {
            Assert.Equal(
                sourceBefore[index],
                File.ReadAllBytes(options.Candidate.Segments[index].Path));
        }
    }

    [Fact]
    public void DvdInputBuildsConcatArgumentsAndExplicitStreamMaps()
    {
        DvdImportOptions options = CreateOptions(includeSubtitle: true);
        var factory = new DvdEncodingInputFactory();

        EncodingInputSource input = factory.Create(options);
        string arguments = EncodingService.BuildInputAndMappingArgumentsForTesting(
            input,
            copySubtitles: true,
            copyDataStreams: false);

        Assert.Contains("-fflags +genpts", arguments);
        Assert.DoesNotContain("-f concat", arguments);
        Assert.Contains($"-i \"{input.InputPath}\"", arguments);
        Assert.Contains("-map 0:1", arguments);
        Assert.Contains("-map 0:2", arguments);
        Assert.Contains("-map 0:3", arguments);
        Assert.Contains("-dn", arguments);
    }

    [Fact]
    public void OrdinaryFileInputRetainsNormalInputAndMappingBehavior()
    {
        string path = Path.Combine(_root, "ordinary video.mp4");
        File.WriteAllBytes(path, new byte[] { 1 });
        EncodingInputSource input = EncodingInputSource.FromFile(path);

        string arguments = EncodingService.BuildInputAndMappingArgumentsForTesting(input);

        Assert.Equal(path, input.SourcePath);
        Assert.Equal("ordinary video", input.OutputBaseName);
        Assert.True(input.AllowSourceDeletion);
        Assert.True(input.ShouldDeleteSource(deleteRequested: true));
        Assert.DoesNotContain("-f concat", arguments);
        Assert.Contains($"-i \"{path}\"", arguments);
        Assert.Contains("-map 0:v:0", arguments);
        Assert.Contains("-map 0:a?", arguments);
        Assert.Contains("-map 0:s?", arguments);
    }

    [Fact]
    public void FactoryRejectsRemuxOptionsInsteadOfStartingAnEncode()
    {
        DvdImportOptions encodeOptions = CreateOptions();
        var remuxOptions = new DvdImportOptions
        {
            Candidate = encodeOptions.Candidate,
            OutputMode = DvdOutputMode.LosslessRemuxToMkv,
            OutputPath = encodeOptions.OutputPath
        };
        var factory = new DvdEncodingInputFactory();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => factory.Create(remuxOptions));

        Assert.Contains("not an encode operation", error.Message);
    }

    private DvdImportOptions CreateOptions(bool includeSubtitle = false)
    {
        string videoTs = Path.Combine(_root, "Movie O'Brien 日本", "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        IReadOnlyList<MediaProbeStreamInfo> streams = new MediaProbeStreamInfo[]
        {
            new()
            {
                Index = 0,
                Id = "0x1bf",
                CodecType = "data",
                CodecName = "dvd_nav_packet"
            },
            new()
            {
                Index = 1,
                Id = "0x1e0",
                CodecType = "video",
                CodecName = "mpeg2video",
                Width = 720,
                Height = 480
            },
            new()
            {
                Index = 2,
                Id = "0x80",
                CodecType = "audio",
                CodecName = "ac3",
                Language = "eng"
            },
            new()
            {
                Index = 3,
                Id = "0x20",
                CodecType = "subtitle",
                CodecName = "dvd_subtitle",
                Language = "eng"
            }
        };
        var segments = Enumerable.Range(1, 2)
            .Select(number =>
            {
                string path = Path.Combine(videoTs, $"VTS_01_{number}.VOB");
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes($"segment-{number}"));
                return new DvdSegmentInfo
                {
                    Path = path,
                    SegmentNumber = number,
                    SizeBytes = new FileInfo(path).Length,
                    IsReadable = true,
                    ProbeResult = new MediaProbeResult
                    {
                        Success = true,
                        DurationSeconds = 30,
                        Streams = streams
                    }
                };
            })
            .ToArray();
        var candidate = new DvdTitleCandidate
        {
            TitleSetId = "VTS_01",
            Segments = segments,
            StartsAtSegmentOne = true,
            HasConsistentStreams = true,
            IsValidForConversion = true,
            CombinedSizeBytes = segments.Sum(segment => segment.SizeBytes),
            CombinedDurationSeconds = 60,
            VideoCodec = "mpeg2video",
            VideoWidth = 720,
            VideoHeight = 480,
            FrameRate = 29.97,
            AudioStreamCount = 1,
            SubtitleStreamCount = 1
        };
        return new DvdImportOptions
        {
            Candidate = candidate,
            OutputMode = DvdOutputMode.EncodeUsingCurrentSettings,
            OutputPath = Path.Combine(_root, "output", "Movie Name.mp4"),
            SelectedAudioStreamIndexes = new[] { 2 },
            SelectedSubtitleStreamIndexes = includeSubtitle
                ? new[] { 3 }
                : Array.Empty<int>()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
