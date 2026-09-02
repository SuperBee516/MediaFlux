using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class NcnnPerformanceTuningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DirectoryArgumentsEmitTypedThreadsAndTileWithoutChangingQualityArguments()
    {
        Directory.CreateDirectory(_root);
        string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output");
        Directory.CreateDirectory(input);
        var backend = new AiRestorationBackendService(_root);
        var settings = new VideoRestorationSettings { AiMode = AiRestorationMode.Animation, AiScale = AiRestorationScale.X2, AiDevice = "Auto" };
        var model = new AiRestorationModel("anime", "Anime", AiRestorationMode.Animation, new[] { AiRestorationScale.X2 }, _root, "a.param", "a.bin", "ncnn-vulkan", "anime-x2");
        var session = new AiRestorationSession(new(true, "ncnn-vulkan", "ai.exe", "backend-v1", true, new[] { "Auto" }, new[] { model }, null), model);

        IReadOnlyList<string> arguments = backend.BuildDirectoryArguments(session, settings, input, output, new(new(2, 2, 2), 512));

        Assert.Contains("-j", arguments); Assert.Contains("2:2:2", arguments);
        Assert.Contains("-t", arguments); Assert.Contains("512", arguments);
        Assert.Contains("-s", arguments); Assert.Contains("2", arguments);
        Assert.Contains("-g", arguments); Assert.Contains("auto", arguments);
        Assert.Contains("-f", arguments); Assert.Contains("png", arguments);
        Assert.DoesNotContain("-x", arguments); // TTA remains disabled.
    }

    [Fact]
    public void StagedCandidatesAvoidCartesianSearch()
    {
        Assert.Equal(new[] { "1:2:2", "2:2:2", "4:4:4" }, NcnnPerformanceAutoTuner.ThreadCandidates().Select(candidate => candidate.Threads!.ToString()));
        Assert.Equal(new[] { 256, 512, 1024 }, NcnnPerformanceAutoTuner.TileCandidates(1920, 1080));
        Assert.Equal(new[] { 256, 512 }, NcnnPerformanceAutoTuner.TileCandidates(3840, 2160));
    }

    [Fact]
    public void FastestValidCandidateWinsAndInvalidCandidatesAreRejected()
    {
        NcnnTuningBenchmarkResult? winner = NcnnPerformanceAutoTuner.SelectWinner(new[]
        {
            Result(new( NcnnThreadConfiguration.OneTwoTwo), 40, true),
            Result(new(NcnnThreadConfiguration.TwoTwoTwo, 512), 60, true),
            Result(new(NcnnThreadConfiguration.FourFourFour, 1024), 120, false)
        });

        Assert.NotNull(winner);
        Assert.Equal("2:2:2", winner!.Configuration.Threads!.ToString());
        Assert.Equal(512, winner.Configuration.TileSize);
    }

    [Fact]
    public void NoValidCandidateRetainsSafeFallback()
    {
        NcnnTuningBenchmarkResult? winner = NcnnPerformanceAutoTuner.SelectWinner(new[] { Result(new(NcnnThreadConfiguration.OneTwoTwo), 0, false) });

        Assert.Null(winner);
        Assert.True(NcnnRuntimeConfiguration.SafeDefault.UsesBackendDefaults);
    }

    [Fact]
    public void CacheRoundTripsAndKeysSeparateHardwareAndWorkloads()
    {
        Directory.CreateDirectory(_root);
        var cache = new NcnnPerformanceTuningCacheService(Path.Combine(_root, "cache.json"));
        NcnnTuningCacheKey key = NcnnTuningCacheKey.Create("GPU A", "backend-v1", "anime-x2", 2, "1080p");
        cache.Store(key, new(NcnnThreadConfiguration.TwoTwoTwo, 512));

        Assert.True(cache.TryGet(key, out NcnnRuntimeConfiguration restored));
        Assert.Equal("2:2:2", restored.Threads!.ToString()); Assert.Equal(512, restored.TileSize);
        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU B", "backend-v1", "anime-x2", 2, "1080p"), out _));
        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU A", "backend-v2", "anime-x2", 2, "1080p"), out _));
        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU A", "backend-v1", "anime-x2", 4, "1080p"), out _));
        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU A", "backend-v1", "anime-x2", 2, "4K+"), out _));
    }

    [Fact]
    public void CorruptOrFutureCacheFailsAsMiss()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cache.json");
        var cache = new NcnnPerformanceTuningCacheService(path);
        File.WriteAllText(path, "not-json");
        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU", "backend", "model", 2, "1080p"), out _));
        File.WriteAllText(path, "{\"Version\":99,\"Entries\":[]}");
        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU", "backend", "model", 2, "1080p"), out _));
    }

    [Fact]
    public void PriorCacheSchemaFailsAsMissSoFalseSuccessSelectionsAreRetuned()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "cache.json");
        File.WriteAllText(path, "{\"Version\":1,\"Entries\":[{\"Key\":\"GPU|backend|model|4|1080p\",\"Configuration\":{\"Threads\":{\"Load\":1,\"Process\":2,\"Save\":2},\"TileSize\":1024}}]}");

        var cache = new NcnnPerformanceTuningCacheService(path);

        Assert.False(cache.TryGet(NcnnTuningCacheKey.Create("GPU", "backend", "model", 4, "1080p"), out _));
    }

    [Fact]
    public void CacheCanBeInvalidatedForExplicitRetuning()
    {
        Directory.CreateDirectory(_root);
        var cache = new NcnnPerformanceTuningCacheService(Path.Combine(_root, "cache.json"));
        NcnnTuningCacheKey key = NcnnTuningCacheKey.Create("GPU", "backend", "model", 2, "1080p");
        cache.Store(key, new(NcnnThreadConfiguration.TwoTwoTwo, 512));
        cache.Invalidate();

        Assert.False(cache.TryGet(key, out _));
    }

    [Fact]
    public void RuntimeSelectionIsIncludedInPerformanceDiagnostics()
    {
        var timing = new PerformanceTimingService();
        timing.SetNcnnRuntimeSelection(new(new(NcnnThreadConfiguration.TwoTwoTwo, 512), NcnnRuntimeConfigurationSource.AutoTuned, 10, 15));

        string summary = timing.BuildSummary();

        Assert.Contains("NCNN Performance Configuration", summary);
        Assert.Contains("Threads: 2:2:2", summary);
        Assert.Contains("Tile: 512", summary);
        Assert.Contains("Baseline FPS: 10", summary);
        Assert.Contains("Selected FPS: 15", summary);
        Assert.Contains("Improvement: 50%", summary);
    }

    [Fact]
    public void ResolutionClassesAreDeterministic()
    {
        Assert.Equal("SD", NcnnPerformanceAutoTuner.ResolutionClass(640, 480));
        Assert.Equal("720p", NcnnPerformanceAutoTuner.ResolutionClass(1280, 720));
        Assert.Equal("1080p", NcnnPerformanceAutoTuner.ResolutionClass(1920, 1080));
        Assert.Equal("4K+", NcnnPerformanceAutoTuner.ResolutionClass(3840, 2160));
    }

    private static NcnnTuningBenchmarkResult Result(NcnnRuntimeConfiguration configuration, double fps, bool valid) => new(configuration, fps, TimeSpan.FromSeconds(1), null, null, valid, valid ? "valid" : "invalid");

    public void Dispose() { try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { } }
}
