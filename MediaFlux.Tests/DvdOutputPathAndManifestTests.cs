using System.Text;
using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdOutputPathAndManifestTests : IDisposable
{
    private readonly string _root;

    public DvdOutputPathAndManifestTests()
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
    public void ManifestEscapesApostrophesSpacesAndUnicodeWithoutBom()
    {
        string source = Path.Combine(_root, "Movie O'Brien 日本", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(source, segmentCount: 2);
        string tempRoot = Path.Combine(_root, "temp");
        var builder = new DvdConcatManifestBuilder(tempRoot);

        string operationDirectory;
        string manifestPath;
        using (DvdConcatManifest manifest = builder.Create(candidate))
        {
            operationDirectory = manifest.OperationDirectory;
            manifestPath = manifest.ManifestPath;
            string text = File.ReadAllText(manifest.ManifestPath);
            byte[] bytes = File.ReadAllBytes(manifest.ManifestPath);

            Assert.StartsWith("ffconcat version 1.0", text, StringComparison.Ordinal);
            Assert.Contains("Movie O'\\''Brien 日本", text, StringComparison.Ordinal);
            Assert.Contains("VTS_01_1.VOB", text, StringComparison.Ordinal);
            Assert.Contains("VTS_01_2.VOB", text, StringComparison.Ordinal);
            Assert.Contains("exact_stream_id 0x1e0", text, StringComparison.Ordinal);
            Assert.False(bytes.Length >= 3 &&
                         bytes[0] == 0xEF &&
                         bytes[1] == 0xBB &&
                         bytes[2] == 0xBF);
            Assert.Equal(0, manifest.GetConcatStreamIndex(0));
            Assert.Equal(1, manifest.GetConcatStreamIndex(1));
        }

        Assert.False(File.Exists(manifestPath));
        Assert.False(Directory.Exists(operationDirectory));
    }

    [Fact]
    public void ManifestEscapingPreservesUncPaths()
    {
        string escaped = DvdConcatManifestBuilder.EscapeManifestPath(
            @"\\server\share\Movie O'Brien 日本\VIDEO_TS\VTS_01_1.VOB");

        Assert.Equal(
            "//server/share/Movie O'\\''Brien 日本/VIDEO_TS/VTS_01_1.VOB",
            escaped);
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
    public void ManifestOrdersSegmentsNumerically()
    {
        string source = Path.Combine(_root, "Ordering", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(source, segmentNumbers: new[] { 10, 2, 1 });
        var builder = new DvdConcatManifestBuilder(Path.Combine(_root, "temp-order"));

        using DvdConcatManifest manifest = builder.Create(candidate);
        string text = File.ReadAllText(manifest.ManifestPath);

        int first = text.IndexOf("VTS_01_1.VOB", StringComparison.Ordinal);
        int second = text.IndexOf("VTS_01_2.VOB", StringComparison.Ordinal);
        int tenth = text.IndexOf("VTS_01_10.VOB", StringComparison.Ordinal);
        Assert.True(first < second && second < tenth);
    }

    [Fact]
    public void ManifestCreationAndCleanupDoNotModifySourceFiles()
    {
        string source = Path.Combine(_root, "Immutable", "VIDEO_TS");
        DvdTitleCandidate candidate = CreateCandidate(source, segmentCount: 2);
        var before = candidate.Segments.ToDictionary(
            segment => segment.Path,
            segment => File.ReadAllBytes(segment.Path));
        var builder = new DvdConcatManifestBuilder(Path.Combine(_root, "temp-safe"));

        using (builder.Create(candidate))
        {
        }

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
