using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class VideoRestorationPreviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxPreviewTests", Guid.NewGuid().ToString("N"));
    public VideoRestorationPreviewTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } VideoRestorationPipeline.ClearAvailableFilters(); FfmpegRestorationCapabilityService.ClearCacheForTesting(); }

    [Fact]
    public void PreviewUsesCentralPipelineFilterChain()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationRestore };
        var request = new VideoRestorationPreviewRequest("source.mkv", TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(42), settings);
        string chain = VideoRestorationPipeline.BuildFilterChain(settings, EncodingService.ScaleMode.None);
        Assert.Contains(chain, VideoRestorationPreviewService.BuildStillArguments("source.mkv", request.Position, chain, "preview.png"));
    }

    [Fact]
    public void PreviewKeepsEncodingFilterOrderWhenNormalScalingIsSelected()
    {
        string chain = VideoRestorationPreviewService.BuildEffectivePreviewFilterChain(new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationLight }, EncodingService.ScaleMode.To720p);
        Assert.Contains("hqdn3d", chain);
        Assert.EndsWith("scale=-2:720:flags=lanczos", chain);
    }

    [Fact]
    public void OriginalAndRestoredStillUseTheSameAccurateTimestamp()
    {
        TimeSpan timestamp = TimeSpan.FromSeconds(42.5);
        List<string> original = VideoRestorationPreviewService.BuildStillArguments("source.mkv", timestamp, "", "original.png").ToList();
        List<string> restored = VideoRestorationPreviewService.BuildStillArguments("source.mkv", timestamp, "hqdn3d=1:1:2:2", "restored.png").ToList();
        Assert.Equal(original[original.IndexOf("-ss") + 1], restored[restored.IndexOf("-ss") + 1]);
        Assert.True(original.IndexOf("-ss") > original.IndexOf("-i")); // Accurate seek after input for both images.
    }

    [Fact]
    public void SameRequestReusesCacheButChangedRestorationDoesNot()
    {
        var off = new VideoRestorationPreviewRequest(Path.Combine(_root, "source.mkv"), TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(42), new VideoRestorationSettings());
        File.WriteAllText(off.SourcePath, "source");
        string originalFirst = VideoRestorationPreviewService.BuildCacheKey(off, "");
        string originalAgain = VideoRestorationPreviewService.BuildCacheKey(off, "");
        string restored = VideoRestorationPreviewService.BuildCacheKey(off, "hqdn3d=1:1:2:2");
        Assert.Equal(originalFirst, originalAgain);
        Assert.NotEqual(originalFirst, restored);
    }

    [Fact]
    public async Task IdenticalStillRequestsReuseFrameFiles()
    {
        string source = Path.Combine(_root, "source.mkv"); string ffmpeg = Path.Combine(_root, "ffmpeg.exe"); File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool");
        var runner = new PreviewRunner();
        var capabilities = new FfmpegRestorationCapabilityService(runner);
        var service = new VideoRestorationPreviewService(ffmpeg, runner, capabilities, Path.Combine(_root, "cache"));
        var request = new VideoRestorationPreviewRequest(source, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationLight });
        using VideoRestorationStillPreview first = await service.GenerateStillAsync(request);
        using VideoRestorationStillPreview second = await service.GenerateStillAsync(request);
        Assert.Equal(2, runner.FrameCalls); // Original/restored once each; both are cached afterwards.
        Assert.Equal(1, runner.InventoryCalls); // The same executable identity reuses capability validation.
    }

    [Fact]
    public void RecommendationPreviewNeverChangesEncodeSettingsUntilApplied()
    {
        var encode = new VideoRestorationSettings();
        var recommendation = new VideoRestorationRecommendation(new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationRestore }, 70, "test");
        var selection = new VideoRestorationPreviewSelection(encode, recommendation);
        Assert.True(selection.PreviewRecommendation());
        Assert.Equal(VideoRestorationPreset.Off, selection.EncodeSettings.Preset);
        Assert.True(selection.DiffersFromEncode);
        Assert.Equal(VideoRestorationPreset.VintageAnimationRestore, selection.ApplyToEncodeSettings().Preset);
    }

    [Fact]
    public void RepresentativePositionsAvoidExtremeFramesAndHandleShortSources()
    {
        IReadOnlyList<TimeSpan> positions = VideoRestorationPreviewService.BuildRepresentativePositions(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(positions); Assert.All(positions, position => Assert.InRange(position.TotalSeconds, 0, 10));
        Assert.Empty(VideoRestorationPreviewService.BuildRepresentativePositions(TimeSpan.Zero));
    }

    [Fact]
    public async Task CancellationDoesNotLeaveGeneratedPreviewFiles()
    {
        string source = Path.Combine(_root, "source.mkv"); string ffmpeg = Path.Combine(_root, "ffmpeg.exe"); File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool");
        var runner = new PreviewRunner(cancelFrames: true);
        var service = new VideoRestorationPreviewService(ffmpeg, runner, new FfmpegRestorationCapabilityService(runner), Path.Combine(_root, "cache"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateStillAsync(new VideoRestorationPreviewRequest(source, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), new VideoRestorationSettings())));
        Assert.Empty(Directory.Exists(Path.Combine(_root, "cache")) ? Directory.EnumerateFiles(Path.Combine(_root, "cache")) : Array.Empty<string>());
    }

    private sealed class PreviewRunner(bool cancelFrames = false) : IMediaToolProcessRunner
    {
        public int FrameCalls { get; private set; }
        public int InventoryCalls { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Contains("-filters"))
            {
                InventoryCalls++;
                string filters = string.Join("\n", Enumerable.Range(0, 24).Select(i => $" T. filter{i} V->V Test.")) + "\n TS hqdn3d V->V Test.\n TS deband V->V Test.\n T. deblock V->V Test.\n TS unsharp V->V Test.";
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardError = filters });
            }
            if (cancelFrames) return Task.FromCanceled<MediaToolProcessResult>(new CancellationToken(true));
            FrameCalls++;
            string output = request.Arguments.Last(); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllBytes(output, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLxWQAAAABJRU5ErkJggg=="));
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        }
    }
}
