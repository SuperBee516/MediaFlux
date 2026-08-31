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
    public async Task DiscoversOnlyCompleteModelsAndCachesByIdentity()
    {
        string exe = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models"); File.WriteAllText(exe, "tool"); Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "realesr-animevideov3.param"), "param"); File.WriteAllText(Path.Combine(models, "realesr-animevideov3.bin"), "bin");
        var runner = new AiRunner(); var service = new AiRestorationBackendService(_root, runner);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3", AiScale = AiRestorationScale.X2, AiBackendPath = exe, AiModelsDirectory = models };
        AiRestorationCapabilities first = await service.GetCapabilitiesAsync(settings); AiRestorationCapabilities second = await service.GetCapabilitiesAsync(settings);
        Assert.Single(first.Models); Assert.True(first.VulkanAvailable); Assert.Equal(first.Identity, second.Identity); Assert.Equal(1, runner.Calls);
        Assert.Equal("realesr-animevideov3", (await service.ValidateSelectionAsync(settings)).Id);
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
    public void IntermediateValidationRejectsMissingOrExtraFrames()
    {
        string directory = Path.Combine(_root, "frames"); Directory.CreateDirectory(directory);
        string[] expected = AiRestorationIntermediateVideoService.ExpectedFrames(directory, 2);
        File.WriteAllBytes(expected[0], new byte[64]);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateFrameSet(directory, expected));
        File.WriteAllBytes(expected[1], new byte[64]); File.WriteAllBytes(Path.Combine(directory, "frame-00000002.png"), new byte[64]);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateFrameSet(directory, expected));
    }

    private sealed class AiRunner : IMediaToolProcessRunner
    {
        public int Calls { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardError = "NCNN Vulkan GPU 0" }); }
    }
}
