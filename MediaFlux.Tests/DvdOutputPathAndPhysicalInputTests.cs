using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdOutputPathAndPhysicalInputTests : IDisposable
{
    private readonly string _root;

    public DvdOutputPathAndPhysicalInputTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdPhase2Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DvdImportOptionsDefaultToLosslessRemux()
    {
        var options = new DvdImportOptions();

        Assert.Equal(DvdOutputMode.LosslessRemuxToMkv, options.OutputMode);
    }

    [Fact]
    public void SanitizationRemovesInvalidWindowsFilenameCharactersAndReservedNames()
    {
        string sanitized = OutputPathService.SanitizeFileName("Movie: Name? <Final>. ");
        string reserved = OutputPathService.SanitizeFileName("CON");

        Assert.DoesNotContain(':', sanitized);
        Assert.DoesNotContain('?', sanitized);
        Assert.DoesNotContain('<', sanitized);
        Assert.DoesNotContain('>', sanitized);
        Assert.False(sanitized.EndsWith('.'));
        Assert.Equal("_CON", reserved);
    }

    [Fact]
    public void DirectVideoTsUsesParentMovieNameForSingleRecommendedCandidate()
    {
        string videoTs = Path.Combine(_root, "Movie Name", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(videoTs);
        var analysis = new DvdFolderAnalysisResult
        {
            SelectedFolderPath = videoTs,
            VideoTsFolderPath = videoTs,
            Candidates = new[] { candidate },
            RecommendedCandidate = candidate
        };

        string baseName = OutputPathService.BuildDefaultDvdBaseName(analysis, candidate);

        Assert.Equal("Movie Name", baseName);
    }

    [Fact]
    public void SecondaryCandidateIncludesTitleSetInDefaultName()
    {
        string parent = Path.Combine(_root, "Concert");
        string videoTs = Path.Combine(parent, "VIDEO_TS");
        DvdTitleCandidate primary = CreateCandidate(videoTs, "VTS_01");
        DvdTitleCandidate secondary = CreateCandidate(videoTs, "VTS_02");
        var analysis = new DvdFolderAnalysisResult
        {
            SelectedFolderPath = parent,
            VideoTsFolderPath = videoTs,
            Candidates = new[] { primary, secondary },
            RecommendedCandidate = primary
        };

        string baseName = OutputPathService.BuildDefaultDvdBaseName(analysis, secondary);

        Assert.Equal("Concert - VTS_02", baseName);
    }

    [Fact]
    public void CustomNamingPatternExpandsMovieAndTitleSetTokens()
    {
        string parent = Path.Combine(_root, "Concert");
        string videoTs = Path.Combine(parent, "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(videoTs, "VTS_02");
        var analysis = new DvdFolderAnalysisResult
        {
            SelectedFolderPath = parent,
            VideoTsFolderPath = videoTs,
            Candidates = new[] { candidate },
            RecommendedCandidate = candidate
        };

        string baseName = OutputPathService.BuildDefaultDvdBaseName(
            analysis,
            candidate,
            "{MovieName} [{TitleSet}]");

        Assert.Equal("Concert [VTS_02]", baseName);
    }

    [Fact]
    public void ExistingDefaultOutputGetsCollisionSafeSuffix()
    {
        string folder = Path.Combine(_root, "output");
        Directory.CreateDirectory(folder);
        string requested = Path.Combine(folder, "Movie.mkv");
        File.WriteAllBytes(requested, new byte[] { 1 });

        string collisionSafe = OutputPathService.GetCollisionSafePath(requested);

        Assert.Equal(Path.Combine(folder, "Movie (1).mkv"), collisionSafe);
    }

    [Fact]
    public void EncodeOutputUsesMp4ExtensionWithoutChangingTheBaseName()
    {
        string path = OutputPathService.EnsureMp4Extension(
            Path.Combine(_root, "output", "Movie Name.mkv"));

        Assert.Equal(
            Path.Combine(_root, "output", "Movie Name.mp4"),
            path);
    }

    [Fact]
    public void PhysicalInputSupportsApostrophesSpacesAndUnicodeWithoutTempFiles()
    {
        string source = Path.Combine(_root, "Movie O'Brien 日本", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(source, segmentCount: 2);
        DvdPhysicalInput input = DvdPhysicalInputBuilder.Create(candidate);

        Assert.StartsWith("concat:file:", input.InputUrl, StringComparison.Ordinal);
        Assert.Contains("Movie O'Brien 日本", input.InputUrl, StringComparison.Ordinal);
        Assert.Contains("VTS_01_1.VOB", input.InputUrl, StringComparison.Ordinal);
        Assert.Contains("|file:", input.InputUrl, StringComparison.Ordinal);
        Assert.Contains("VTS_01_2.VOB", input.InputUrl, StringComparison.Ordinal);
        Assert.Equal(2, input.SourceFiles.Count);
        Assert.Empty(Directory.EnumerateDirectories(_root, "dvd-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void PhysicalInputFileUrlPreservesUncPaths()
    {
        string url = DvdPhysicalInputBuilder.ToFileUrl(
            @"\\server\share\Movie O'Brien 日本\VIDEO_TS\VTS_01_1.VOB");

        Assert.Equal(
            "file://server/share/Movie O'Brien 日本/VIDEO_TS/VTS_01_1.VOB",
            url);
    }

    [Fact]
    public void SourceSafetyHandlesNetworkStylePathsCaseInsensitively()
    {
        Assert.True(OutputPathService.IsPathWithinDirectory(
            @"\\SERVER\share\Movie\VIDEO_TS\output.mkv",
            @"\\server\share\Movie\VIDEO_TS"));
        Assert.False(OutputPathService.IsPathWithinDirectory(
            @"\\server\share\Movie\output.mkv",
            @"\\server\share\Movie\VIDEO_TS"));
    }

    [Fact]
    public void PhysicalInputOrdersSegmentsNumerically()
    {
        string source = Path.Combine(_root, "Ordering", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(source, segmentNumbers: new[] { 10, 2, 1 });
        DvdPhysicalInput input = DvdPhysicalInputBuilder.Create(candidate);

        int first = input.InputUrl.IndexOf("VTS_01_1.VOB", StringComparison.Ordinal);
        int second = input.InputUrl.IndexOf("VTS_01_2.VOB", StringComparison.Ordinal);
        int tenth = input.InputUrl.IndexOf("VTS_01_10.VOB", StringComparison.Ordinal);
        Assert.True(first < second && second < tenth);
    }

    [Fact]
    public void PhysicalInputCreationDoesNotModifySourceFiles()
    {
        string source = Path.Combine(_root, "Immutable", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(source, segmentCount: 2);
        var before = candidate.Segments.ToDictionary(
            segment => segment.Path,
            segment => File.ReadAllBytes(segment.Path));
        DvdPhysicalInput input = DvdPhysicalInputBuilder.Create(candidate);

        Assert.Equal(candidate.Segments.Count, input.SourceFiles.Count);
        foreach (DvdSegmentInfo segment in candidate.Segments)
            Assert.Equal(before[segment.Path], File.ReadAllBytes(segment.Path));
    }

    private DvdTitleCandidate CreateCandidate(
        string videoTs,
        string titleSetId = "VTS_01",
        int segmentCount = 1,
        int[]? segmentNumbers = null)
    {
        Directory.CreateDirectory(videoTs);
        segmentNumbers ??= Enumerable.Range(1, segmentCount).ToArray();
        var streams = CreateStreams();
        var segments = segmentNumbers.Select(number =>
        {
            string path = Path.Combine(videoTs, $"{titleSetId}_{number}.VOB");
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
                    DurationSeconds = 60,
                    Streams = streams
                }
            };
        }).ToArray();

        return new DvdTitleCandidate
        {
            TitleSetId = titleSetId,
            Segments = segments,
            StartsAtSegmentOne = true,
            HasConsistentStreams = true,
            IsValidForConversion = true,
            CombinedDurationSeconds = segments.Length * 60,
            CombinedSizeBytes = segments.Sum(segment => segment.SizeBytes),
            VideoCodec = "mpeg2video",
            VideoWidth = 720,
            VideoHeight = 480,
            AudioStreamCount = 1,
            SubtitleStreamCount = 1
        };
    }

    private static IReadOnlyList<MediaProbeStreamInfo> CreateStreams() =>
        new MediaProbeStreamInfo[]
        {
            new()
            {
                Index = 0,
                Id = "0x1e0",
                CodecType = "video",
                CodecName = "mpeg2video",
                TimeBase = "1/90000",
                Width = 720,
                Height = 480
            },
            new()
            {
                Index = 1,
                Id = "0x80",
                CodecType = "audio",
                CodecName = "ac3",
                TimeBase = "1/90000",
                Language = "eng"
            },
            new()
            {
                Index = 2,
                Id = "0x20",
                CodecType = "subtitle",
                CodecName = "dvd_subtitle",
                TimeBase = "1/90000",
                Language = "eng"
            }
        };

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
