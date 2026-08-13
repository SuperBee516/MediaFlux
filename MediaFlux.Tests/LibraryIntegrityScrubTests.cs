using MediaFlux.Services;
using MediaFlux.Services.LibraryCatalog;
using Xunit;

namespace MediaFlux.Tests;

public sealed class LibraryIntegrityScrubTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFlux-IntegrityScrubTests", Guid.NewGuid().ToString("N"));
    public LibraryIntegrityScrubTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void QuickPositionsAdaptForShortMediaWithoutDuplicateRegions()
    {
        Assert.Equal(new[] { 0d }, LibraryIntegrityScrubService.BuildQuickPositions(.5));
        Assert.Equal(new[] { 0d }, LibraryIntegrityScrubService.BuildQuickPositions(null));
    }

    [Fact]
    public async Task HealthyQuickScrubChecksBeginningMiddleAndEnd()
    {
        string path = CreateFile("healthy.mkv", 4096); var runner = new ScriptedRunner();
        LibraryIntegrityRunResult result = await Service(runner).ScrubAsync(Item(path, LibraryIntegrityScrubType.Quick, duration: 100));
        Assert.Equal(LibraryIntegrityResultState.Passed, result.Result.State);
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal(new[] { 0d, 49.5d, 99d }, result.QuickPositionsSeconds);
        Assert.All(runner.Requests, request => Assert.Contains("0:v:0", request.Arguments));
    }

    [Theory]
    [InlineData(1, "error while decoding", LibraryIntegrityErrorCategory.VideoDecodeError)]
    [InlineData(2, "Invalid PTS timestamp", LibraryIntegrityErrorCategory.InvalidTimestamps)]
    [InlineData(3, "partial file truncated at end of file", LibraryIntegrityErrorCategory.TruncatedMedia)]
    public async Task QuickScrubClassifiesBeginningMiddleAndEndFailures(int failureCall, string diagnostic, LibraryIntegrityErrorCategory category)
    {
        string path = CreateFile($"failure-{failureCall}.mkv", 4096); var runner = new ScriptedRunner(failureCall, diagnostic);
        LibraryIntegrityRunResult result = await Service(runner).ScrubAsync(Item(path, LibraryIntegrityScrubType.Quick, duration: 100));
        Assert.Equal(LibraryIntegrityResultState.Failed, result.Result.State);
        Assert.Equal(category, result.Result.ErrorCategory);
        Assert.Equal(failureCall, runner.Requests.Count);
    }

    [Fact]
    public async Task MissingVideoUnavailableFileAndCancellationAreDistinct()
    {
        string path = CreateFile("missing-video.mkv", 100);
        LibraryIntegrityRunResult missingVideo = await Service(new ScriptedRunner()).ScrubAsync(Item(path, LibraryIntegrityScrubType.Quick) with { VideoCodec = "" });
        Assert.Equal(LibraryIntegrityErrorCategory.MissingVideoStream, missingVideo.Result.ErrorCategory);
        LibraryIntegrityQueueItem unavailableItem = Item(path, LibraryIntegrityScrubType.Quick); File.Delete(path);
        LibraryIntegrityRunResult missingFile = await Service(new ScriptedRunner()).ScrubAsync(unavailableItem);
        Assert.Equal(LibraryIntegrityErrorCategory.FileDisappeared, missingFile.Result.ErrorCategory);
        path = CreateFile("cancel.mkv", 100); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        LibraryIntegrityRunResult cancelled = await Service(new ScriptedRunner()).ScrubAsync(Item(path, LibraryIntegrityScrubType.Quick), cancellationToken: cancellation.Token);
        Assert.Equal(LibraryIntegrityResultState.Cancelled, cancelled.Result.State);
    }

    [Fact]
    public async Task HealthyFullScrubMapsVideoAndAudioAndReportsCompleteBytes()
    {
        string path = CreateFile("full.mkv", 8192); var runner = new ScriptedRunner(progressMicros: 120_000_000);
        LibraryIntegrityRunResult result = await Service(runner).ScrubAsync(Item(path, LibraryIntegrityScrubType.Full, duration: 120) with { AudioStreamCount = 2 });
        Assert.Equal(LibraryIntegrityResultState.Passed, result.Result.State);
        Assert.Equal(8192, result.Result.BytesChecked);
        Assert.Contains("0:a?", Assert.Single(runner.Requests).Arguments);
        Assert.Contains("-xerror", runner.Requests[0].Arguments);
    }

    [Theory]
    [InlineData("moov atom not found", LibraryIntegrityErrorCategory.ContainerReadFailure)]
    [InlineData("audio channel decode failure", LibraryIntegrityErrorCategory.AudioDecodeError)]
    [InlineData("truncated partial file", LibraryIntegrityErrorCategory.TruncatedMedia)]
    public async Task FullScrubClassifiesCorruptionAudioAndTruncation(string diagnostic, LibraryIntegrityErrorCategory category)
    {
        string path = CreateFile(Guid.NewGuid() + ".mkv", 1024); var runner = new ScriptedRunner(1, diagnostic);
        LibraryIntegrityRunResult result = await Service(runner).ScrubAsync(Item(path, LibraryIntegrityScrubType.Full) with { AudioStreamCount = 1 });
        Assert.Equal(category, result.Result.ErrorCategory);
    }

    [Fact]
    public async Task FileChangingDuringFullScrubCannotRecordPass()
    {
        string path = CreateFile("changing.mkv", 1024); var runner = new ScriptedRunner(onRun: () => File.AppendAllText(path, "changed"));
        LibraryIntegrityRunResult result = await Service(runner).ScrubAsync(Item(path, LibraryIntegrityScrubType.Full));
        Assert.Equal(LibraryIntegrityResultState.Stale, result.Result.State);
        Assert.Equal(LibraryIntegrityErrorCategory.FileChanged, result.Result.ErrorCategory);
    }

    [Fact]
    public async Task LiveFfmpegHealthyAndTruncatedFixturesWhenConfigured()
    {
        string ffmpeg = Environment.GetEnvironmentVariable("MEDIAFLUX_TEST_FFMPEG") ?? "";
        if (!File.Exists(ffmpeg)) return;
        string healthy = Path.Combine(_root, "live-healthy.mkv"); var process = new MediaToolProcessRunner();
        MediaToolProcessResult generated = await process.RunAsync(new MediaToolProcessRequest
        {
            FileName = ffmpeg, Timeout = TimeSpan.FromSeconds(30), Arguments = new[]
            { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-f", "lavfi", "-i", "sine=frequency=1000", "-t", "4", "-c:v", "libx264", "-c:a", "aac", "-y", healthy }
        });
        Assert.Equal(0, generated.ExitCode);
        var service = new LibraryIntegrityScrubService(ffmpeg, process, "live");
        LibraryIntegrityRunResult quick = await service.ScrubAsync(Item(healthy, LibraryIntegrityScrubType.Quick, 4));
        LibraryIntegrityRunResult full = await service.ScrubAsync(Item(healthy, LibraryIntegrityScrubType.Full, 4) with { AudioStreamCount = 1 });
        Assert.Equal(LibraryIntegrityResultState.Passed, quick.Result.State);
        Assert.Equal(LibraryIntegrityResultState.Passed, full.Result.State);

        string truncated = Path.Combine(_root, "live-truncated.mkv"); File.Copy(healthy, truncated);
        using (FileStream stream = new(truncated, FileMode.Open, FileAccess.Write, FileShare.None)) stream.SetLength(Math.Max(1, stream.Length / 3));
        LibraryIntegrityRunResult damagedQuick = await service.ScrubAsync(Item(truncated, LibraryIntegrityScrubType.Quick, 4) with { AudioStreamCount = 1 });
        LibraryIntegrityRunResult damaged = await service.ScrubAsync(Item(truncated, LibraryIntegrityScrubType.Full, 4) with { AudioStreamCount = 1 });
        Assert.Equal(LibraryIntegrityResultState.Failed, damagedQuick.Result.State);
        Assert.Equal(LibraryIntegrityResultState.Failed, damaged.Result.State);
        Assert.Contains(damaged.Result.ErrorCategory, new[] { LibraryIntegrityErrorCategory.TruncatedMedia, LibraryIntegrityErrorCategory.ContainerReadFailure, LibraryIntegrityErrorCategory.VideoDecodeError });
    }

    private LibraryIntegrityScrubService Service(IMediaToolProcessRunner runner)
    {
        string tool = CreateFile("ffmpeg.exe", 1); return new LibraryIntegrityScrubService(tool, runner, "synthetic-1");
    }
    private LibraryIntegrityQueueItem Item(string path, LibraryIntegrityScrubType type, double? duration = 60)
    {
        var info = new FileInfo(path); return new(1, 1, path, "volume", info.Length, info.LastWriteTimeUtc, "identity", "h264", duration, 1,
            type, LibraryIntegrityQueueStatus.Running, 1, 3, "batch", "", DateTime.UtcNow, DateTime.UtcNow);
    }
    private string CreateFile(string name, int bytes) { string path = Path.Combine(_root, name); File.WriteAllBytes(path, new byte[bytes]); return path; }

    private sealed class ScriptedRunner : IMediaToolProcessRunner
    {
        private readonly int _failureCall; private readonly string _diagnostic; private readonly long? _progressMicros; private readonly Action? _onRun;
        public List<MediaToolProcessRequest> Requests { get; } = new();
        public ScriptedRunner(int failureCall = 0, string diagnostic = "", long? progressMicros = null, Action? onRun = null)
        { _failureCall = failureCall; _diagnostic = diagnostic; _progressMicros = progressMicros; _onRun = onRun; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(request); _onRun?.Invoke();
            if (_progressMicros.HasValue) request.StandardOutputLineCallback?.Invoke($"out_time_us={_progressMicros}");
            return Task.FromResult(new MediaToolProcessResult { ExitCode = Requests.Count == _failureCall ? 1 : 0,
                StandardOutput = "frame=1\nprogress=end\n", StandardError = Requests.Count == _failureCall ? _diagnostic : "" });
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
