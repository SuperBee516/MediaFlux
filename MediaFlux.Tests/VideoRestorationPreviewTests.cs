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
        var service = new VideoRestorationPreviewService(ffmpeg, ffmpeg, runner, capabilities, Path.Combine(_root, "cache"));
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
    public void PreviewModeSelectionIsExplicitAndRecommendationRequiresAnalysis()
    {
        var selection = new VideoRestorationPreviewSelection(new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationLight });
        Assert.False(selection.SelectMode(RestorationPreviewSelectionMode.Recommended));
        Assert.True(selection.SelectMode(RestorationPreviewSelectionMode.NoRestoration));
        Assert.Equal(VideoRestorationPreset.Off, selection.PreviewSettings.Preset);
        selection.SetRecommendation(new VideoRestorationRecommendation(new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationRestore }, 70, "test"));
        Assert.True(selection.SelectMode(RestorationPreviewSelectionMode.Recommended));
        Assert.Equal(VideoRestorationPreset.VintageAnimationRestore, selection.PreviewSettings.Preset);
        Assert.Equal(VideoRestorationPreset.VintageAnimationLight, selection.EncodeSettings.Preset);
    }

    [Fact]
    public void NewerPreviewRequestMakesOlderAsynchronousResultObsolete()
    {
        var gate = new VideoRestorationPreviewOperationGate();
        long first = gate.Begin(); long second = gate.Begin();
        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
        gate.Invalidate();
        Assert.False(gate.IsCurrent(second));
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
        var service = new VideoRestorationPreviewService(ffmpeg, ffmpeg, runner, new FfmpegRestorationCapabilityService(runner), Path.Combine(_root, "cache"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GenerateStillAsync(new VideoRestorationPreviewRequest(source, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10), new VideoRestorationSettings())));
        Assert.Empty(Directory.Exists(Path.Combine(_root, "cache")) ? Directory.EnumerateFiles(Path.Combine(_root, "cache")) : Array.Empty<string>());
    }

    [Fact]
    public async Task MotionPreviewStagesValidatesAndPromotesBeforeExposure()
    {
        string source = Path.Combine(_root, "source.mkv"), ffmpeg = Path.Combine(_root, "ffmpeg.exe"); File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool");
        var runner = new MotionRunner(); var service = new VideoRestorationPreviewService(ffmpeg, ffmpeg, runner, new FfmpegRestorationCapabilityService(runner), Path.Combine(_root, "cache"));
        VideoRestorationMotionPreview result = await service.GenerateMotionAsync(new VideoRestorationPreviewRequest(source, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), new VideoRestorationSettings()), TimeSpan.FromSeconds(5));
        Assert.All(new[] { result.OriginalPath, result.RestoredPath, result.ComparisonPath }, path => Assert.True(File.Exists(path)));
        Assert.Equal(3, runner.MotionWrites);
        Assert.All(runner.MotionOutputPaths, path => Assert.Contains(".staging.mp4", path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "cache"), "*.staging.*"));
    }

    [Fact]
    public async Task InvalidCachedMotionPreviewIsRemovedAndRegenerated()
    {
        string source = Path.Combine(_root, "source.mkv"), ffmpeg = Path.Combine(_root, "ffmpeg.exe"); File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool");
        var runner = new MotionRunner(); var service = new VideoRestorationPreviewService(ffmpeg, ffmpeg, runner, new FfmpegRestorationCapabilityService(runner), Path.Combine(_root, "cache"));
        var request = new VideoRestorationPreviewRequest(source, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), new VideoRestorationSettings());
        VideoRestorationMotionPreview first = await service.GenerateMotionAsync(request); File.WriteAllBytes(first.ComparisonPath, Array.Empty<byte>());
        await service.GenerateMotionAsync(request);
        Assert.Equal(4, runner.MotionWrites);
    }

    [Fact]
    public async Task MotionFailureLeavesNoPartialOutputExposed()
    {
        string source = Path.Combine(_root, "source.mkv"), ffmpeg = Path.Combine(_root, "ffmpeg.exe"); File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool");
        var runner = new MotionRunner(failMotion: true); var service = new VideoRestorationPreviewService(ffmpeg, ffmpeg, runner, new FfmpegRestorationCapabilityService(runner), Path.Combine(_root, "cache"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GenerateMotionAsync(new VideoRestorationPreviewRequest(source, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), new VideoRestorationSettings())));
        Assert.Empty(Directory.Exists(Path.Combine(_root, "cache")) ? Directory.EnumerateFiles(Path.Combine(_root, "cache"), "*.staging.*") : Array.Empty<string>());
        Assert.Empty(Directory.Exists(Path.Combine(_root, "cache")) ? Directory.EnumerateFiles(Path.Combine(_root, "cache"), "motion-*.mp4") : Array.Empty<string>());
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

    private sealed class MotionRunner(bool failMotion = false) : IMediaToolProcessRunner
    {
        public int MotionWrites { get; private set; }
        public List<string> MotionOutputPaths { get; } = [];
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments.Contains("-filters")) return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardError = string.Join("\n", Enumerable.Range(0, 24).Select(i => $" T. filter{i} V->V Test.")) });
            if (request.Arguments.Contains("-show_entries")) return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = "codec_type=video\nduration=5.0" });
            string output = request.Arguments.Last();
            if (failMotion) return Task.FromResult(new MediaToolProcessResult { ExitCode = 1, StandardError = "synthetic ffmpeg failure" });
            Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllBytes(output, new byte[2048]); MotionWrites++; MotionOutputPaths.Add(output);
            return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        }
    }
}
