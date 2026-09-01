using MediaFlux.Models;
using MediaFlux.Services;
using Xunit;

namespace MediaFlux.Tests;

public sealed class AiModelManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "MediaFluxModelManagerTests", Guid.NewGuid().ToString("N"));
    public AiModelManagerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task NcnnDiscoveryPreservesLogicalIdentityAndResolvedPairNames()
    {
        string models = Path.Combine(_root, "ncnn"); Directory.CreateDirectory(models);
        WritePair(models, "realesr-animevideov3-x2");
        var manager = new AiModelManager();

        AiModelDiscoverySummary summary = await manager.DiscoverNcnnAsync(models);
        AiManagedModel model = Assert.Single(summary.Available);
        AiRestorationModel restored = model.ToNcnnRestorationModel();

        Assert.Equal("realesr-animevideov3", model.Identity.LogicalModel);
        Assert.Equal("ncnn-vulkan", model.Identity.Backend);
        Assert.Equal(AiRestorationScale.X2, model.Identity.Scale);
        Assert.Equal(AiRestorationMode.Animation, model.Identity.Mode);
        Assert.NotEmpty(model.Identity.Hash);
        Assert.Equal("realesr-animevideov3-x2", restored.BackendModelName);
        Assert.Equal(Path.Combine(models, "realesr-animevideov3-x2.param"), restored.ParamPath);
    }

    [Fact]
    public async Task OnnxDiscoveryRequiresReadableVersionedMetadataAndValidHash()
    {
        string models = Path.Combine(_root, "onnx"); Directory.CreateDirectory(models);
        string path = Path.Combine(models, "anime-x2.onnx"); File.WriteAllText(path, "onnx-model");
        var manager = new AiModelManager();

        Assert.False((await manager.ValidateOnnxAsync(path)).IsValid);
        AiModelMetadata saved = await manager.SaveMetadataAsync(path, Metadata());
        AiModelValidationResult validation = await manager.ValidateOnnxAsync(path);
        AiModelDiscoverySummary summary = await manager.DiscoverOnnxAsync(models);

        Assert.True(validation.IsValid);
        AiModelMetadata loaded = Assert.IsType<AiModelMetadata>(await manager.LoadMetadataAsync(path));
        Assert.Equal(saved.Identity.LogicalModel, loaded.Identity.LogicalModel);
        Assert.Equal(saved.Identity.Hash, loaded.Identity.Hash);
        Assert.Equal(saved.SchemaVersion, loaded.SchemaVersion);
        Assert.Single(summary.Available);
        Assert.Equal("anime-onnx", AiModelManager.Find(summary.Available, "anime-onnx", "nvidia-tensorrt", AiRestorationScale.X2, AiRestorationMode.Animation)?.Identity.LogicalModel);
        Assert.Contains("\"SchemaVersion\": 1", await File.ReadAllTextAsync(AiModelManager.MetadataPath(path)));
    }

    [Fact]
    public async Task OnnxValidationRejectsHashIdentityAndSchemaVersionMismatches()
    {
        string path = Path.Combine(_root, "model.onnx"); File.WriteAllText(path, "onnx-model");
        var manager = new AiModelManager();
        AiModelMetadata saved = await manager.SaveMetadataAsync(path, Metadata());

        File.AppendAllText(path, "changed");
        Assert.Contains("hash", (await manager.ValidateOnnxAsync(path)).Reason, StringComparison.OrdinalIgnoreCase);

        await File.WriteAllTextAsync(path, "onnx-model");
        await File.WriteAllTextAsync(AiModelManager.MetadataPath(path), System.Text.Json.JsonSerializer.Serialize(saved with { SchemaVersion = 99 }));
        Assert.Contains("schema", (await manager.ValidateOnnxAsync(path)).Reason, StringComparison.OrdinalIgnoreCase);

        await manager.SaveMetadataAsync(path, Metadata() with { Identity = Metadata().Identity with { Backend = "unsupported" } });
        Assert.Contains("unsupported identity", (await manager.ValidateOnnxAsync(path)).Reason, StringComparison.OrdinalIgnoreCase);

        AiModelMetadata compatible = await manager.SaveMetadataAsync(path, Metadata());
        Assert.Contains("identity or version", (await manager.ValidateOnnxAsync(path, compatible.Identity with { Version = "2.0" })).Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CacheIsLazyBoundedAndInvalidatesChangedModels()
    {
        string models = Path.Combine(_root, "ncnn"); Directory.CreateDirectory(models); WritePair(models, "realesr-animevideov3-x2");
        var manager = new AiModelManager(cacheCapacity: 1);
        AiManagedModel model = Assert.Single((await manager.DiscoverNcnnAsync(models)).Available);

        using AiModelLease first = manager.Acquire(model);
        using AiModelLease second = manager.Acquire(model);
        Assert.Equal(1, manager.CachedModelCount);
        manager.Invalidate(model.PrimaryPath);
        Assert.Equal(0, manager.CachedModelCount);

        AiManagedModel other = model with { PrimaryPath = Path.Combine(models, "other.param"), Identity = model.Identity with { Hash = "other" } };
        using AiModelLease third = manager.Acquire(other);
        Assert.Equal(1, manager.CachedModelCount);
    }

    [Fact]
    public async Task ModelManagerDiagnosticsSummarizeAvailableMissingAndInvalidModels()
    {
        string models = Path.Combine(_root, "ncnn"); Directory.CreateDirectory(models); File.WriteAllText(Path.Combine(models, "realesr-animevideov3-x2.param"), "param");
        var logs = new List<string>(); var manager = new AiModelManager(log: logs.Add);

        AiModelDiscoverySummary summary = await manager.DiscoverNcnnAsync(models);

        Assert.Empty(summary.Available);
        Assert.Equal(5, summary.MissingCount);
        Assert.Single(logs);
        Assert.Contains("Model Manager", logs[0]);
        Assert.Contains("Missing: 5", logs[0]);
    }

    private static AiModelMetadata Metadata() => new(new("anime-onnx", "nvidia-tensorrt", AiRestorationScale.X2, AiRestorationMode.Animation, "1.0", new[] { "nvidia-tensorrt" }, ""), DateTimeOffset.UtcNow, new[] { "TensorRT discovery" }, "Anime ONNX");
    private static void WritePair(string directory, string name) { File.WriteAllText(Path.Combine(directory, name + ".param"), "param"); File.WriteAllText(Path.Combine(directory, name + ".bin"), "bin"); }
}
