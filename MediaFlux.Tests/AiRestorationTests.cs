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
        string[] frames = AiRestorationIntermediateVideoService.ExpectedFrames(_root, AiChunkPlanner.MinimumFramesPerChunk);
        Assert.Equal("frame-00000000.png", Path.GetFileName(frames[0]));
        Assert.Equal("frame-00000059.png", Path.GetFileName(frames[^1]));
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
    public async Task DirectoryBatchUsesOneBackendInvocationForAnEntireChunkAndReportsProgress()
    {
        string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        string[] inputs = AiRestorationIntermediateVideoService.ExpectedFrames(input, 3);
        string[] outputs = AiRestorationIntermediateVideoService.ExpectedFrames(output, 3);
        foreach (string frame in inputs) WritePng(frame, 2, 3);
        var runner = new DirectoryBatchRunner(outputs, 4, 6, TimeSpan.FromMilliseconds(300));
        var service = new AiRestorationBackendService(_root, runner);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "anime", AiScale = AiRestorationScale.X2, AiDevice = "Auto" };
        var session = new AiRestorationSession(new(true, "ncnn-vulkan", "ai.exe", "test", true, new[] { "Auto" }, Array.Empty<AiRestorationModel>(), null), new("anime", "Anime", AiRestorationMode.Animation, new[] { AiRestorationScale.X2 }, Path.Combine(_root, "models"), "a.param", "a.bin", "ncnn-vulkan", "realesr-animevideov3-x2"));
        var progress = new List<int>();

        await service.ProcessDirectoryAsync(session, settings, input, output, outputs, progress.Add);

        Assert.Single(runner.Requests);
        IReadOnlyList<string> arguments = runner.Requests[0].Arguments;
        Assert.Equal(input, arguments[arguments.ToList().IndexOf("-i") + 1]);
        Assert.Equal(output, arguments[arguments.ToList().IndexOf("-o") + 1]);
        Assert.Equal("realesr-animevideov3-x2", arguments[arguments.ToList().IndexOf("-n") + 1]);
        Assert.Equal("png", arguments[arguments.ToList().IndexOf("-f") + 1]);
        Assert.Equal(3, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.OrderBy(value => value)));
        AiRestorationIntermediateVideoService.ValidateRestoredFrameSet(inputs, outputs, AiRestorationScale.X2);
    }

    [Fact]
    public void RestoredFrameValidationRejectsMissingUnexpectedAndWrongSizedOutputs()
    {
        string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input); Directory.CreateDirectory(output);
        string[] inputs = AiRestorationIntermediateVideoService.ExpectedFrames(input, 2);
        string[] outputs = AiRestorationIntermediateVideoService.ExpectedFrames(output, 2);
        foreach (string frame in inputs) WritePng(frame, 2, 2);
        WritePng(outputs[0], 4, 4);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateRestoredFrameSet(inputs, outputs, AiRestorationScale.X2));
        WritePng(outputs[1], 4, 4); WritePng(Path.Combine(output, "unexpected.png"), 4, 4);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateRestoredFrameSet(inputs, outputs, AiRestorationScale.X2));
        File.Delete(Path.Combine(output, "unexpected.png")); WritePng(outputs[1], 3, 4);
        Assert.Throws<AiRestorationValidationException>(() => AiRestorationIntermediateVideoService.ValidateRestoredFrameSet(inputs, outputs, AiRestorationScale.X2));
    }

    [Fact]
    public void BatchedEtaWaitsForAStableThroughputSample()
    {
        Assert.Null(AiRestorationProgressEstimator.EstimateRemaining(11, 180, TimeSpan.FromSeconds(10)));
        Assert.Null(AiRestorationProgressEstimator.EstimateRemaining(12, 180, TimeSpan.FromSeconds(2)));
        TimeSpan remaining = Assert.IsType<TimeSpan>(AiRestorationProgressEstimator.EstimateRemaining(30, 180, TimeSpan.FromSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(50), remaining);
    }

    [Fact]
    public async Task BatchCancellationPropagatesToBackendAndCleansPartialOwnedOutput()
    {
        string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input); Directory.CreateDirectory(output);
        string source = AiRestorationIntermediateVideoService.ExpectedFrames(input, 1)[0], target = AiRestorationIntermediateVideoService.ExpectedFrames(output, 1)[0];
        WritePng(source, 2, 2);
        var runner = new CancellingBatchRunner(target);
        var service = new AiRestorationBackendService(_root, runner);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "anime", AiScale = AiRestorationScale.X2 };
        var session = new AiRestorationSession(new(true, "ncnn-vulkan", "ai.exe", "test", true, new[] { "Auto" }, Array.Empty<AiRestorationModel>(), null), new("anime", "Anime", AiRestorationMode.Animation, new[] { AiRestorationScale.X2 }, Path.Combine(_root, "models"), "a.param", "a.bin", "ncnn-vulkan"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessDirectoryAsync(session, settings, input, output, new[] { target }, null, cancellation.Token));

        Assert.True(runner.CancellationObserved);
        Assert.Empty(Directory.EnumerateFiles(output));
    }

    [Fact]
    public async Task IntermediateUsesOneBatchPerChunkAndReportsMonotonicOverallProgress()
    {
        string source = Path.Combine(_root, "source.mkv"), ffmpeg = Path.Combine(_root, "ffmpeg.exe"), ffprobe = Path.Combine(_root, "ffprobe.exe"), ai = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models");
        File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool"); File.WriteAllText(ffprobe, "tool"); File.WriteAllText(ai, "tool"); Directory.CreateDirectory(models); WritePair(models, "realesr-animevideov3-x2");
        var runner = new IntermediateBatchRunner(ai); var backend = new AiRestorationBackendService(_root, runner);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3", AiScale = AiRestorationScale.X2, AiBackendPath = ai, AiModelsDirectory = models };
        VideoRestorationPipelinePlan plan = VideoRestorationPipeline.BuildPlan(settings, EncodingService.ScaleMode.None);
        var updates = new List<AiIntermediateProgress>();
        var logs = new List<string>();
        var progress = new CapturingProgress(updates);
        var service = new AiRestorationIntermediateVideoService(ffmpeg, ffprobe, Path.Combine(_root, "staging"), backend, runner, log: logs.Add, dedicatedGpuVramProvider: () => null);

        using AiIntermediateVideoResult result = await service.CreateAsync(new AiIntermediateVideoRequest(source, 30, TimeSpan.FromSeconds(7), settings, plan, SourceWidth: 2, SourceHeight: 2), progress);

        Assert.Equal(2, runner.BatchCalls);
        int[] overall = updates.Where(update => update.Stage == AiIntermediateStage.AiProcessing).Select(update => update.Current).ToArray();
        Assert.True(overall.SequenceEqual(overall.OrderBy(value => value)));
        Assert.Equal(210, overall[^1]);
        Assert.True(File.Exists(result.Path));
        string plannerLog = Assert.Single(logs, log => log.StartsWith("[AI Chunk Planner]", StringComparison.Ordinal));
        Assert.Contains("Resolution:", plannerLog); Assert.Contains("AI Scale:", plannerLog); Assert.Contains("GPU VRAM:", plannerLog); Assert.Contains("Estimated Temporary Storage:", plannerLog); Assert.Contains("Chosen Chunk Size:", plannerLog); Assert.Contains("Decision Reason:", plannerLog);
    }

    [Fact]
    public async Task FiveSecondPreviewSizedIntermediateUsesOneBatchInvocation()
    {
        string source = Path.Combine(_root, "source.mkv"), ffmpeg = Path.Combine(_root, "ffmpeg.exe"), ffprobe = Path.Combine(_root, "ffprobe.exe"), ai = Path.Combine(_root, "ai.exe"), models = Path.Combine(_root, "models");
        File.WriteAllText(source, "source"); File.WriteAllText(ffmpeg, "tool"); File.WriteAllText(ffprobe, "tool"); File.WriteAllText(ai, "tool"); Directory.CreateDirectory(models); WritePair(models, "realesr-animevideov3-x2");
        var runner = new IntermediateBatchRunner(ai); var backend = new AiRestorationBackendService(_root, runner);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiModelId = "realesr-animevideov3", AiScale = AiRestorationScale.X2, AiBackendPath = ai, AiModelsDirectory = models };
        var service = new AiRestorationIntermediateVideoService(ffmpeg, ffprobe, Path.Combine(_root, "staging"), backend, runner, dedicatedGpuVramProvider: () => null);

        using AiIntermediateVideoResult result = await service.CreateAsync(new AiIntermediateVideoRequest(source, 30, TimeSpan.FromSeconds(5), settings, VideoRestorationPipeline.BuildPlan(settings, EncodingService.ScaleMode.None), SourceWidth: 2, SourceHeight: 2));

        Assert.Equal(1, runner.BatchCalls);
        Assert.Equal(150, result.FrameCount);
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
    private sealed class DirectoryBatchRunner : IMediaToolProcessRunner
    {
        private readonly IReadOnlyList<string> _outputs; private readonly int _width, _height; private readonly TimeSpan _delay;
        public List<MediaToolProcessRequest> Requests { get; } = new();
        public DirectoryBatchRunner(IReadOnlyList<string> outputs, int width, int height, TimeSpan delay) { _outputs = outputs; _width = width; _height = height; _delay = delay; }
        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); foreach (string output in _outputs) { await Task.Delay(_delay, cancellationToken); WritePng(output, _width, _height); } return new MediaToolProcessResult { ExitCode = 0 }; }
    }
    private sealed class CancellingBatchRunner : IMediaToolProcessRunner
    {
        private readonly string _partial; public bool CancellationObserved { get; private set; }
        public CancellingBatchRunner(string partial) => _partial = partial;
        public async Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        { WritePng(_partial, 4, 4); try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); } catch (OperationCanceledException) { CancellationObserved = true; throw; } return new MediaToolProcessResult(); }
    }
    private sealed class CapturingProgress(List<AiIntermediateProgress> updates) : IProgress<AiIntermediateProgress>
    { public void Report(AiIntermediateProgress value) => updates.Add(value); }
    private sealed class IntermediateBatchRunner(string aiPath) : IMediaToolProcessRunner
    {
        private readonly Dictionary<string, int> _frameCounts = new(StringComparer.OrdinalIgnoreCase);
        public int BatchCalls { get; private set; }
        public Task<MediaToolProcessResult> RunAsync(MediaToolProcessRequest request, CancellationToken cancellationToken = default)
        {
            if (request.FileName.Equals(aiPath, StringComparison.OrdinalIgnoreCase))
            {
                if (request.Arguments.SequenceEqual(new[] { "-h" })) return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardError = "NCNN Vulkan GPU 0" });
                BatchCalls++; string batchOutput = request.Arguments[request.Arguments.ToList().IndexOf("-o") + 1]; string input = request.Arguments[request.Arguments.ToList().IndexOf("-i") + 1];
                foreach (string source in Directory.EnumerateFiles(input, "*.png").OrderBy(path => path)) WritePng(Path.Combine(batchOutput, Path.GetFileName(source)), 4, 4);
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
            }
            if (request.FileName.EndsWith("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                string path = request.Arguments.Last(); int count = _frameCounts.TryGetValue(path, out int value) ? value : 150;
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0, StandardOutput = $"codec_name=ffv1\nwidth=4\nheight=4\npix_fmt=yuv420p\ntime_base=1/30\nnb_frames={count}\nr_frame_rate=30/1\nduration={(count / 30d).ToString(System.Globalization.CultureInfo.InvariantCulture)}" });
            }
            if (request.Arguments.Contains("-frames:v"))
            {
                int count = int.Parse(request.Arguments[request.Arguments.ToList().IndexOf("-frames:v") + 1]); string pattern = request.Arguments.Last(); string directory = Path.GetDirectoryName(pattern)!;
                Directory.CreateDirectory(directory); foreach (string frame in AiRestorationIntermediateVideoService.ExpectedFrames(directory, count)) WritePng(frame, 2, 2);
                return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
            }
            string output = request.Arguments.Last(); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (request.Arguments.Contains("concat")) _frameCounts[output] = _frameCounts.Where(pair => Path.GetFileName(pair.Key).StartsWith("chunk-", StringComparison.OrdinalIgnoreCase)).Sum(pair => pair.Value);
            else { string pattern = request.Arguments[request.Arguments.ToList().IndexOf("-i") + 1]; _frameCounts[output] = Directory.EnumerateFiles(Path.GetDirectoryName(pattern)!, "*.png").Count(); }
            File.WriteAllBytes(output, new byte[2048]); return Task.FromResult(new MediaToolProcessResult { ExitCode = 0 });
        }
    }
    private static void WritePng(string path, int width, int height)
    {
        byte[] bytes = new byte[64];
        bytes[0] = 137; bytes[1] = 80; bytes[2] = 78; bytes[3] = 71; bytes[4] = 13; bytes[5] = 10; bytes[6] = 26; bytes[7] = 10;
        bytes[11] = 13; bytes[12] = 73; bytes[13] = 72; bytes[14] = 68; bytes[15] = 82;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        File.WriteAllBytes(path, bytes);
    }
    private static void WritePair(string directory, string name) { File.WriteAllText(Path.Combine(directory, name + ".param"), "param"); File.WriteAllText(Path.Combine(directory, name + ".bin"), "bin"); }
}
