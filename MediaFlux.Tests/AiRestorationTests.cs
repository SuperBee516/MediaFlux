using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiRestorationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxAiTests", Guid.NewGuid().ToString("N"));
    public AiRestorationTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } AiRestorationBackendService.InvalidateCache(); }

    [Fact]
    public void AiOffPreservesExistingFfmpegPipeline()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.VintageAnimationLight };
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(settings, EncodingService.ScaleMode.None);
        Assert.False(plan.UsesAi);
        Assert.Equal(VideoRestorationPipeline.BuildFilterChain(settings, EncodingService.ScaleMode.None), plan.ConventionalFilterChain);
    }

    [Fact]
    public void AiPlanKeepsSharpenAfterTheAiStage()
    {
        var settings = new VideoRestorationSettings { Preset = VideoRestorationPreset.Custom, Denoise = VideoRestorationStrength.Light, Deblock = VideoRestorationStrength.Light, Deband = VideoRestorationStrength.Light, Sharpen = VideoRestorationStrength.Medium, AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3" };
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(settings, EncodingService.ScaleMode.None);
        Assert.True(plan.UsesAi); Assert.Contains("hqdn3d", plan.PreAiFilterChain); Assert.Contains("deblock", plan.PreAiFilterChain); Assert.DoesNotContain("unsharp", plan.PreAiFilterChain); Assert.Contains("deband", plan.PostAiFilterChain); Assert.Contains("unsharp", plan.PostAiFilterChain);
    }

    [Fact]
    public async Task DiscoversScaleSpecificAnimeModelsAndCachesByIdentity()
    {
        string exe = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models"); File.WriteAllText(exe, "tool"); Directory.CreateDirectory(models);
        WritePair(models, "realesr-animevideov3-x2"); WritePair(models, "realesr-animevideov3-x3"); WritePair(models, "realesr-animevideov3-x4");
        var runner = new AiRunner(); var service = new AiRestorationBackendService(_root, runner);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3", AiScale = AiRestorationScale.X2, AiBackendPath = exe, AiModelsDirectory = models };
        AiRestorationCapabilities first = await service.GetCapabilitiesAsync(settings); AiRestorationCapabilities second = await service.GetCapabilitiesAsync(settings);
        Assert.Equal(3, first.Models.Count); Assert.True(first.VulkanAvailable); Assert.Equal(first.Identity, second.Identity); Assert.Equal(1, runner.Calls);
        foreach (AiRestorationScale scale in new[] { AiRestorationScale.X2, AiRestorationScale.X3, AiRestorationScale.X4 }) { settings.AiScale = scale; AiRestorationModel resolved = await service.ValidateSelectionAsync(settings); Assert.Equal($"realesr-animevideov3-x{(int)scale}", resolved.BackendModelName); }
    }

    [Fact]
    public async Task DiscoversUnsuffixedX4PlusPairsWithTheirSupportedScale()
    {
        string exe = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models"); File.WriteAllText(exe, "tool"); Directory.CreateDirectory(models);
        WritePair(models, "realesrgan-x4plus"); WritePair(models, "realesrgan-x4plus-anime");
        var service = new AiRestorationBackendService(_root, new AiRunner());
        var general = new VideoRestorationSettings { AiMode = AiRestorationMode.General, AiModelId = "realesrgan-x4plus", AiScale = AiRestorationScale.X4, AiBackendPath = exe, AiModelsDirectory = models };
        var anime = general.Clone(); anime.AiMode = AiRestorationMode.Animation; anime.AiModelId = "realesrgan-x4plus-anime";
        Assert.Equal("realesrgan-x4plus", (await service.ValidateSelectionAsync(general)).BackendModelName);
        Assert.Equal("realesrgan-x4plus-anime", (await service.ValidateSelectionAsync(anime)).BackendModelName);
        general.AiScale = AiRestorationScale.X2; await Assert.ThrowsAsync<AiRestorationValidationException>(() => service.ValidateSelectionAsync(general));
    }

    [Fact]
    public async Task AlreadySuffixedAnimeIdResolvesOnceAndCommandUsesExactBackendModelName()
    {
        string exe = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models"), input = Path.Combine(_root, "input.png"), output = Path.Combine(_root, "output.png"); File.WriteAllText(exe, "tool"); Directory.CreateDirectory(models); WritePair(models, "realesr-animevideov3-x2"); File.WriteAllBytes(input, new byte[64]);
        var service = new AiRestorationBackendService(_root, new AiRunner()); var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3-x2", AiScale = AiRestorationScale.X2, AiBackendPath = exe, AiModelsDirectory = models };
        AiRestorationCapabilities capabilities = await service.GetCapabilitiesAsync(settings); AiRestorationModel model = await service.ValidateSelectionAsync(settings); IReadOnlyList<string> arguments = service.BuildFrameArguments(capabilities, model, settings, input, output);
        Assert.Equal("realesr-animevideov3-x2", arguments[arguments.ToList().IndexOf("-n") + 1]); Assert.DoesNotContain("realesr-animevideov3-x2-x2", arguments);
    }

    [Fact]
    public async Task IncompleteScaleSpecificPairIsUnavailable()
    {
        string exe = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models"); File.WriteAllText(exe, "tool"); Directory.CreateDirectory(models); File.WriteAllText(Path.Combine(models, "realesr-animevideov3-x3.param"), "param");
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3", AiScale = AiRestorationScale.X3, AiBackendPath = exe, AiModelsDirectory = models };
        AiRestorationCapabilities capabilities = await new AiRestorationBackendService(_root, new AiRunner()).GetCapabilitiesAsync(settings); Assert.Empty(capabilities.Models); await Assert.ThrowsAsync<AiRestorationValidationException>(() => new AiRestorationBackendService(_root, new AiRunner()).ValidateSelectionAsync(settings));
    }

    [Fact]
    public async Task IncompleteOrUnsupportedSelectionIsRejectedBeforeProcessing()
    {
        string exe = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models"); File.WriteAllText(exe, "tool"); Directory.CreateDirectory(models);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "missing", AiBackendPath = exe, AiModelsDirectory = models };
        await Assert.ThrowsAsync<AiRestorationValidationException>(() => new AiRestorationBackendService(_root, new AiRunner()).ValidateSelectionAsync(settings));
    }

    [Fact]
    public void IntermediateFrameNamesAreDeterministicAndBounded()
    {
        string[] frames = AiRestorationIntermediateVideoService.ExpectedFrames(_root, AiRestorationFrameProcessor.MaximumFramesPerChunk);
        Assert.Equal("frame-00000000.png", Path.GetFileName(frames[0]));
        Assert.Equal("frame-00000179.png", Path.GetFileName(frames[^1]));
    }

    [Fact]
    public async Task FrameProcessorReportsEachCompletedFrameInChronologicalOrder()
    {
        string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        string[] frames = AiRestorationIntermediateVideoService.ExpectedFrames(input, 3);
        foreach (string frame in frames) File.WriteAllBytes(frame, new byte[64]);
        var reports = new List<(int Current, int Total)>();

        await new AiRestorationFrameProcessor().ProcessChunkAsync(
            frames,
            output,
            (source, destination, _) => { File.Copy(source, destination); return Task.CompletedTask; },
            (current, total) => reports.Add((current, total)));

        Assert.Equal(new[] { (1, 3), (2, 3), (3, 3) }, reports);
    }

    [Fact]
    public void IntermediateValidationRejectsMissingOrExtraFrames()
    {
        string directory = Path.Combine(_root, "frames"); Directory.CreateDirectory(directory);
        string[] expected = AiRestorationIntermediateVideoService.ExpectedFrames(directory, 2);
        File.WriteAllBytes(expected[0], new byte[64]);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateFrameSet(directory, expected));
        File.WriteAllBytes(expected[1], new byte[64]); File.WriteAllBytes(Path.Combine(directory, "frame-00000002.png"), new byte[64]);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateFrameSet(directory, expected));
    }

    [Fact]
    public void SingleValidatedChunkBypassesConcatAndMultipleChunksRequireIt()
    {
        Assert.False(AiRestorationIntermediateVideoService.ShouldJoinChunks(1));
        Assert.True(AiRestorationIntermediateVideoService.ShouldJoinChunks(2));
    }

    [Fact]
    public void CompatibleChunksCanJoinAndConcatPathsAreEscaped()
    {
        var chunks = new[]
        {
            Chunk("C:\\AI O'Brien\\chunk-00000.mkv"),
            Chunk("C:\\AI O'Brien\\chunk-00001.mkv")
        };
        AiRestorationIntermediateVideoService.ValidateChunkCompatibility(chunks);
        string line = Assert.Single(AiRestorationIntermediateVideoService.BuildConcatListLines(new[] { chunks[0].Path }));
        Assert.Equal("file 'C:\\AI O'\\''Brien\\chunk-00000.mkv'", line);
    }

    [Fact]
    public void IncompatibleChunksFailBeforeConcatWouldAlterTiming()
    {
        var chunks = new[] { Chunk("first.mkv"), Chunk("second.mkv") with { TimeBase = "1/1000" } };
        AiRestorationValidationException exception = Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateChunkCompatibility(chunks));
        Assert.Contains("incompatible", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time_base", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IntermediateProcessFailureContainsFfmpegDiagnostics()
    {
        string diagnostic = AiRestorationIntermediateVideoService.BuildFailureDiagnostic("join AI chunks", "C:\\tools\\ffmpeg.exe", new[] { "-f", "concat", "list.ffconcat" }, new MediaToolProcessResult { ExitCode = 7, StandardError = "concat failed\ninvalid data" }, new[] { Chunk("chunk-00000.mkv"), Chunk("chunk-00001.mkv") });
        Assert.Contains("executable=C:\\tools\\ffmpeg.exe", diagnostic);
        Assert.Contains("exitCode=7", diagnostic);
        Assert.Contains("chunks=2", diagnostic);
        Assert.Contains("concat failed invalid data", diagnostic);
        Assert.Contains("chunk-00000.mkv", diagnostic);
    }

    private static AiIntermediateChunkMetadata Chunk(string path) => new(path, "ffv1", 640, 480, "yuv420p", "1/30", "30/1", 180, 6);

    private sealed class AiRunner : IMediaToolProcessRunner
    {
        public int Calls { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardError = "NCNN Vulkan GPU 0" }); }
    }
    private static void WritePair(string directory, string name) { File.WriteAllText(Path.Combine(directory, name + ".param"), "param"); File.WriteAllText(Path.Combine(directory, name + ".bin"), "bin"); }
}
