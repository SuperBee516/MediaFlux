using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class DvdFolderAnalysisServiceTests : IDisposable
{
    private readonly string _root;

    public DvdFolderAnalysisServiceTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "MediaFlux-DvdTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task NormalTitleSetExcludesMenuVobsAndCombinesProgramSegments()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VIDEO_TS.VOB");
        CreateFile(videoTs, "VTS_01_0.VOB");
        CreateFile(videoTs, "VTS_01_1.VOB");
        CreateFile(videoTs, "VTS_01_2.VOB");
        CreateFile(videoTs, "VTS_01_3.VOB");
        CreateFile(videoTs, "VTS_01_4.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal("VTS_01", candidate.TitleSetId);
        Assert.Equal(new[] { 1, 2, 3, 4 }, candidate.Segments.Select(x => x.SegmentNumber));
        Assert.DoesNotContain(candidate.Segments, segment =>
            Path.GetFileName(segment.Path).EndsWith("_0.VOB", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(240, candidate.CombinedDurationSeconds);
        Assert.Equal("mpeg2video", candidate.VideoCodec);
        Assert.Equal(720, candidate.VideoWidth);
        Assert.Equal(480, candidate.VideoHeight);
        Assert.Equal("16:9", candidate.DisplayAspectRatio);
        Assert.Equal(1, candidate.AudioStreamCount);
        Assert.Equal(1, candidate.SubtitleStreamCount);
        Assert.Equal(new[] { "eng" }, candidate.Languages);
        Assert.True(candidate.IsValidForConversion);
        Assert.Same(candidate, result.RecommendedCandidate);
    }

    [Fact]
    public async Task MultipleTitleSetsRemainSeparate()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        CreateFile(videoTs, "VTS_01_2.VOB");
        CreateFile(videoTs, "VTS_02_1.VOB");
        CreateFile(videoTs, "VTS_02_2.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        Assert.Equal(2, result.Candidates.Count);
        Assert.All(result.Candidates, candidate => Assert.Equal(2, candidate.Segments.Count));
        Assert.Equal(
            new[] { "VTS_01", "VTS_02" },
            result.Candidates.Select(candidate => candidate.TitleSetId));
    }

    [Fact]
    public async Task SegmentOrderingIsNumericRatherThanLexicographic()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_10.VOB");
        CreateFile(videoTs, "VTS_01_2.VOB");
        CreateFile(videoTs, "VTS_01_1.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        Assert.Equal(
            new[] { 1, 2, 10 },
            Assert.Single(result.Candidates).Segments.Select(segment => segment.SegmentNumber));
    }

    [Fact]
    public async Task MissingMiddleSegmentsAreReportedAndInvalidateRecommendation()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        CreateFile(videoTs, "VTS_01_3.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(new[] { 2 }, candidate.MissingSegmentNumbers);
        Assert.Contains(candidate.Warnings, warning =>
            warning.Contains("VTS_01_2.VOB", StringComparison.OrdinalIgnoreCase));
        Assert.False(candidate.IsValidForConversion);
        Assert.Null(result.RecommendedCandidate);
    }

    [Fact]
    public async Task SegmentNumberingMustBeginAtOne()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_2.VOB");
        CreateFile(videoTs, "VTS_01_3.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.StartsAtSegmentOne);
        Assert.Contains(candidate.Warnings, warning =>
            warning.Contains("does not begin", StringComparison.OrdinalIgnoreCase));
        Assert.False(candidate.IsValidForConversion);
    }

    [Theory]
    [InlineData("vts_01_1.vob", "VTS_01_2.VoB")]
    [InlineData("VtS_01_1.VOB", "vTs_01_2.vOb")]
    public async Task LowercaseAndMixedCaseExtensionsAreAccepted(string first, string second)
    {
        string videoTs = CreateVideoTs("video_ts");
        CreateFile(videoTs, first);
        CreateFile(videoTs, second);

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.Equal(new[] { 1, 2 }, candidate.Segments.Select(segment => segment.SegmentNumber));
        Assert.True(candidate.IsValidForConversion);
    }

    [Fact]
    public async Task ParentFolderAndDirectVideoTsFolderResolveToSameDirectory()
    {
        string parent = Path.Combine(_root, "Movie");
        string videoTs = CreateVideoTs(parent: parent);
        CreateFile(videoTs, "VTS_01_1.VOB");

        DvdFolderAnalysisResult parentResult = await AnalyzeAsync(parent);
        DvdFolderAnalysisResult directResult = await AnalyzeAsync(videoTs);

        Assert.Equal(
            Path.GetFullPath(videoTs),
            parentResult.VideoTsFolderPath,
            ignoreCase: true);
        Assert.Equal(parentResult.VideoTsFolderPath, directResult.VideoTsFolderPath, ignoreCase: true);
        Assert.Single(parentResult.Candidates);
        Assert.Single(directResult.Candidates);
    }

    [Fact]
    public async Task MenuOnlyFolderProducesSpecificError()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VIDEO_TS.VOB");
        CreateFile(videoTs, "VTS_01_0.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        Assert.Empty(result.Candidates);
        Assert.Contains("Only menu VOB", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FolderWithoutProgramOrMenuVobsReportsNoCandidates()
    {
        string videoTs = CreateVideoTs();

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        Assert.Empty(result.Candidates);
        Assert.Contains("No valid DVD title sets", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParentWithoutVideoTsFolderProducesClearError()
    {
        string parent = Path.Combine(_root, "Not a DVD");
        Directory.CreateDirectory(parent);

        DvdFolderAnalysisResult result = await AnalyzeAsync(parent);

        Assert.Empty(result.Candidates);
        Assert.Contains("No VIDEO_TS folder", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingSelectedFolderProducesClearError()
    {
        string missing = Path.Combine(_root, "Missing");

        DvdFolderAnalysisResult result = await AnalyzeAsync(missing);

        Assert.Empty(result.Candidates);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZeroByteSegmentIsReportedAndNotProbed()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB", byteCount: 0);
        var probe = new RecordingProbeService(_ => CreateProbeResult(60));

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs, probe);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.IsValidForConversion);
        Assert.Contains(candidate.Warnings, warning =>
            warning.Contains("empty", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(probe.ProbedPaths);
    }

    [Fact]
    public async Task LockedSegmentIsReportedAsUnreadable()
    {
        string videoTs = CreateVideoTs();
        string segment = CreateFile(videoTs, "VTS_01_1.VOB");
        using var locked = new FileStream(
            segment,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.IsValidForConversion);
        Assert.Contains(candidate.Warnings, warning =>
            warning.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProbeFailureIsVisibleAndPreventsRecommendation()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        var probe = new RecordingProbeService(_ =>
            MediaProbeResult.Failed("Invalid data found when processing input"));

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs, probe);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.IsValidForConversion);
        Assert.Contains(candidate.Warnings, warning =>
            warning.Contains("accessible VIDEO_TS", StringComparison.OrdinalIgnoreCase));
        Assert.Null(result.RecommendedCandidate);
    }

    [Fact]
    public async Task InconsistentStreamsAcrossSegmentsAreReported()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        CreateFile(videoTs, "VTS_01_2.VOB");
        var probe = new RecordingProbeService(path =>
            CreateProbeResult(
                60,
                audioCodec: path.EndsWith("_2.VOB", StringComparison.OrdinalIgnoreCase)
                    ? "mp2"
                    : "ac3"));

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs, probe);

        DvdTitleCandidate candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.HasConsistentStreams);
        Assert.False(candidate.IsValidForConversion);
        Assert.Contains(candidate.Warnings, warning =>
            warning.Contains("consistent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LongestValidCandidateIsRecommendedWithoutRemovingOtherCandidates()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        CreateFile(videoTs, "VTS_02_1.VOB");
        var probe = new RecordingProbeService(path =>
            CreateProbeResult(
                Path.GetFileName(path).StartsWith("VTS_02", StringComparison.OrdinalIgnoreCase)
                    ? 6_000
                    : 3_000));

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs, probe);

        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("VTS_02", result.RecommendedCandidate?.TitleSetId);
        Assert.True(result.RecommendedCandidate?.IsLikelyMainFeature);
        Assert.Contains(
            "longest valid detected title set",
            result.RecommendedCandidate?.RecommendationReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Candidates, candidate => candidate.TitleSetId == "VTS_01");
    }

    [Fact]
    public async Task CloseDurationCandidatesProduceAmbiguityWarning()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        CreateFile(videoTs, "VTS_02_1.VOB");
        var probe = new RecordingProbeService(path =>
            CreateProbeResult(
                Path.GetFileName(path).StartsWith("VTS_01", StringComparison.OrdinalIgnoreCase)
                    ? 6_000
                    : 5_700));

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs, probe);

        Assert.True(result.HasAmbiguousMainFeature);
        Assert.Contains("similar durations", result.AmbiguityWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("VTS_01", result.RecommendedCandidate?.TitleSetId);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task MissingIfoFilesProduceWarningWithoutHidingVobCandidates()
    {
        string videoTs = Path.Combine(_root, "VIDEO_TS");
        Directory.CreateDirectory(videoTs);
        CreateFile(videoTs, "VTS_01_1.VOB");

        DvdFolderAnalysisResult result = await AnalyzeAsync(videoTs);

        Assert.False(result.ResemblesDvdVideo);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("IFO", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task CancellationStopsPendingProbeAnalysis()
    {
        string videoTs = CreateVideoTs();
        CreateFile(videoTs, "VTS_01_1.VOB");
        var probe = new CancelableProbeService();
        var service = new DvdFolderAnalysisService(probe);
        using var cancellation = new CancellationTokenSource();

        Task<DvdFolderAnalysisResult> analysis = service.AnalyzeAsync(
            videoTs,
            cancellationToken: cancellation.Token);
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analysis);
    }

    private Task<DvdFolderAnalysisResult> AnalyzeAsync(
        string selectedFolder,
        IMediaProbeService? probeService = null)
    {
        var service = new DvdFolderAnalysisService(
            probeService ?? new RecordingProbeService(_ => CreateProbeResult(60)));
        return service.AnalyzeAsync(selectedFolder);
    }

    private string CreateVideoTs(string name = "VIDEO_TS", string? parent = null)
    {
        parent ??= _root;
        string videoTs = Path.Combine(parent, name);
        Directory.CreateDirectory(videoTs);
        CreateFile(videoTs, "VIDEO_TS.IFO");
        return videoTs;
    }

    private static string CreateFile(string folder, string name, int byteCount = 32)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, byteCount).ToArray());
        return path;
    }

    private static MediaProbeResult CreateProbeResult(
        double durationSeconds,
        string audioCodec = "ac3")
    {
        return new MediaProbeResult
        {
            Success = true,
            FormatName = "mpeg",
            DurationSeconds = durationSeconds,
            Streams = new MediaProbeStreamInfo[]
            {
                new()
                {
                    Index = 0,
                    Id = "0x1e0",
                    CodecType = "video",
                    CodecName = "mpeg2video",
                    TimeBase = "1/90000",
                    Width = 720,
                    Height = 480,
                    DisplayAspectRatio = "16:9",
                    FrameRate = 30000d / 1001d,
                    FieldOrder = "tt"
                },
                new()
                {
                    Index = 1,
                    Id = "0x80",
                    CodecType = "audio",
                    CodecName = audioCodec,
                    TimeBase = "1/90000",
                    Channels = 6,
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
            }
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingProbeService : IMediaProbeService
    {
        private readonly Func<string, MediaProbeResult> _resultFactory;
        private readonly object _sync = new();

        public RecordingProbeService(Func<string, MediaProbeResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public List<string> ProbedPaths { get; } = new();

        public Task<MediaProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
                ProbedPaths.Add(path);
            return Task.FromResult(_resultFactory(path));
        }
    }

    private sealed class CancelableProbeService : IMediaProbeService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<MediaProbeResult> ProbeAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateProbeResult(60);
        }
    }
}
