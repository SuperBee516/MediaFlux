using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MediaFlux.Models;

namespace MediaFlux.Services;

public enum AiModelFormat { Ncnn, Onnx, Other }
public sealed record AiModelIdentity(string LogicalModel, string Backend, AiRestorationScale Scale, AiRestorationMode Mode, string Version, IReadOnlyList<string> SupportedBackends, string Hash);
public sealed record AiModelMetadata(AiModelIdentity Identity, DateTimeOffset CreatedUtc, IReadOnlyList<string> Compatibility, string? DisplayName = null, int SchemaVersion = AiModelManager.MetadataSchemaVersion);
public sealed record AiManagedModel(AiModelIdentity Identity, AiModelFormat Format, string DisplayName, string PrimaryPath, string? SecondaryPath, AiModelMetadata? Metadata)
{
    public AiRestorationModel ToNcnnRestorationModel() => new(Identity.LogicalModel, DisplayName, Identity.Mode, new[] { Identity.Scale }, Path.GetDirectoryName(PrimaryPath)!, PrimaryPath, SecondaryPath!, Identity.Backend, Path.GetFileNameWithoutExtension(PrimaryPath));
}
public sealed record AiModelValidationResult(string Path, AiManagedModel? Model, bool IsValid, string Reason)
{
    public static AiModelValidationResult Invalid(string path, string reason) => new(path, null, false, reason);
}
public sealed record AiModelDiscoverySummary(IReadOnlyList<AiManagedModel> Available, IReadOnlyList<AiModelValidationResult> Invalid, int MissingCount)
{
    public string Describe() => $"Model Manager{Environment.NewLine}NCNN Models / TensorRT ONNX Models{Environment.NewLine}Available: {Available.Count}{Environment.NewLine}Missing: {MissingCount}{Environment.NewLine}Invalid: {Invalid.Count}";
}

/// <summary>Metadata-only cached model representation. No runtime model is loaded or executed.</summary>
public sealed class AiModelLease : IDisposable
{
    private readonly AiModelCache _cache; private readonly string _key; private int _disposed;
    internal AiModelLease(AiModelCache cache, string key, AiManagedModel model) { _cache = cache; _key = key; Model = model; }
    public AiManagedModel Model { get; }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _cache.Release(_key); }
}

/// <summary>Bounded, thread-safe lazy cache for immutable model metadata.</summary>
public sealed class AiModelCache : IDisposable
{
    private sealed class Entry { public Entry(AiManagedModel model) { Loader = new(() => model, LazyThreadSafetyMode.ExecutionAndPublication); LastUsedUtc = DateTimeOffset.UtcNow; } public Lazy<AiManagedModel> Loader { get; } public int Leases; public DateTimeOffset LastUsedUtc; }
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase); private readonly int _maximumEntries; private int _disposed;
    public AiModelCache(int maximumEntries = 128) { _maximumEntries = Math.Max(1, maximumEntries); }
    public AiModelLease Acquire(AiManagedModel model)
    {
        ThrowIfDisposed(); string key = Key(model); Entry entry = _entries.GetOrAdd(key, _ => new Entry(model));
        lock (entry) { ThrowIfDisposed(); entry.Leases++; entry.LastUsedUtc = DateTimeOffset.UtcNow; Trim(); return new(this, key, entry.Loader.Value); }
    }
    internal void Release(string key) { if (_entries.TryGetValue(key, out Entry? entry)) lock (entry) { entry.Leases = Math.Max(0, entry.Leases - 1); entry.LastUsedUtc = DateTimeOffset.UtcNow; } }
    public void Invalidate(string path) { foreach (KeyValuePair<string, Entry> pair in _entries.Where(pair => pair.Key.StartsWith(Path.GetFullPath(path) + "|", StringComparison.OrdinalIgnoreCase))) _entries.TryRemove(pair.Key, out _); }
    public int Count => _entries.Count;
    private void Trim()
    {
        while (_entries.Count > _maximumEntries)
        {
            KeyValuePair<string, Entry>? candidate = _entries.Where(pair => pair.Value.Leases == 0).OrderBy(pair => pair.Value.LastUsedUtc).Cast<KeyValuePair<string, Entry>?>().FirstOrDefault();
            if (candidate is null || !_entries.TryRemove(candidate.Value.Key, out _)) return;
        }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _entries.Clear(); }
    private static string Key(AiManagedModel model) => Path.GetFullPath(model.PrimaryPath) + "|" + model.Identity.Hash;
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(AiModelCache)); }
}

/// <summary>Unified model discovery and validation for NCNN pairs, ONNX files, and future formats.</summary>
public sealed class AiModelManager : IDisposable
{
    public const int MetadataSchemaVersion = 1;
    private sealed record ModelVariant(AiRestorationScale Scale, string ResolvedModelName);
    private sealed record KnownModel(string LogicalModel, string DisplayName, AiRestorationMode Mode, IReadOnlyList<ModelVariant> Variants);
    private static readonly KnownModel[] NcnnKnownModels =
    {
        new("realesr-animevideov3", "Real-ESRGAN AnimeVideo v3", AiRestorationMode.Animation, new[] { new ModelVariant(AiRestorationScale.X2, "realesr-animevideov3-x2"), new ModelVariant(AiRestorationScale.X3, "realesr-animevideov3-x3"), new ModelVariant(AiRestorationScale.X4, "realesr-animevideov3-x4") }),
        new("realesrgan-x4plus-anime", "Real-ESRGAN x4plus Anime", AiRestorationMode.Animation, new[] { new ModelVariant(AiRestorationScale.X4, "realesrgan-x4plus-anime") }),
        new("realesrgan-x4plus", "Real-ESRGAN x4plus", AiRestorationMode.General, new[] { new ModelVariant(AiRestorationScale.X4, "realesrgan-x4plus") })
    };
    private readonly AiModelCache _cache; private readonly Action<string>? _log;
    public AiModelManager(int cacheCapacity = 128, Action<string>? log = null) { _cache = new(cacheCapacity); _log = log; }
    public async Task<AiModelDiscoverySummary> DiscoverNcnnAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return new(Array.Empty<AiManagedModel>(), Array.Empty<AiModelValidationResult>(), NcnnKnownModels.Sum(model => model.Variants.Count));
        var available = new List<AiManagedModel>(); var invalid = new List<AiModelValidationResult>(); int missing = 0;
        foreach (KnownModel known in NcnnKnownModels)
            foreach (ModelVariant variant in known.Variants)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string param = Path.Combine(directory, variant.ResolvedModelName + ".param"), bin = Path.Combine(directory, variant.ResolvedModelName + ".bin");
                if (!File.Exists(param) || !File.Exists(bin)) { missing++; continue; }
                if (new FileInfo(param).Length == 0 || new FileInfo(bin).Length == 0) { invalid.Add(AiModelValidationResult.Invalid(param, "NCNN model pair is empty.")); continue; }
                string hash = await HashAsync(new[] { param, bin }, cancellationToken).ConfigureAwait(false);
                var identity = new AiModelIdentity(known.LogicalModel, "ncnn-vulkan", variant.Scale, known.Mode, "ncnn-model-pair-v1", new[] { "ncnn-vulkan" }, hash);
                available.Add(new AiManagedModel(identity, AiModelFormat.Ncnn, known.DisplayName, param, bin, null));
            }
        Log(new AiModelDiscoverySummary(available, invalid, missing)); return new(available, invalid, missing);
    }
    public async Task<AiModelDiscoverySummary> DiscoverOnnxAsync(string directory, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return new(Array.Empty<AiManagedModel>(), Array.Empty<AiModelValidationResult>(), 0);
        var available = new List<AiManagedModel>(); var invalid = new List<AiModelValidationResult>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.onnx", SearchOption.TopDirectoryOnly))
        {
            AiModelValidationResult validation = await ValidateOnnxAsync(path, cancellationToken).ConfigureAwait(false);
            if (validation.IsValid) available.Add(validation.Model!); else invalid.Add(validation);
        }
        Log(new AiModelDiscoverySummary(available, invalid, 0)); return new(available, invalid, 0);
    }
    public async Task<AiModelValidationResult> ValidateOnnxAsync(string path, CancellationToken cancellationToken = default)
        => await ValidateOnnxAsync(path, expected: null, cancellationToken: cancellationToken).ConfigureAwait(false);
    public async Task<AiModelValidationResult> ValidateOnnxAsync(string path, AiModelIdentity? expected, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return AiModelValidationResult.Invalid(path, "ONNX model is missing or empty.");
        AiModelMetadata? metadata = await LoadMetadataAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata is null) return AiModelValidationResult.Invalid(path, "ONNX model metadata is missing or invalid.");
        if (metadata.SchemaVersion != MetadataSchemaVersion) return AiModelValidationResult.Invalid(path, "ONNX model metadata schema version is unsupported.");
        AiModelIdentity identity = metadata.Identity;
        if (!identity.Backend.Equals("nvidia-tensorrt", StringComparison.OrdinalIgnoreCase) || identity.Scale is < AiRestorationScale.X1 or > AiRestorationScale.X4 || identity.Mode == AiRestorationMode.Off || string.IsNullOrWhiteSpace(identity.LogicalModel) || string.IsNullOrWhiteSpace(identity.Version)) return AiModelValidationResult.Invalid(path, "ONNX model metadata contains an unsupported identity.");
        string hash = await HashAsync(new[] { path }, cancellationToken).ConfigureAwait(false);
        if (!hash.Equals(identity.Hash, StringComparison.OrdinalIgnoreCase)) return AiModelValidationResult.Invalid(path, "ONNX model hash does not match metadata.");
        if (expected is not null && (!identity.LogicalModel.Equals(expected.LogicalModel, StringComparison.OrdinalIgnoreCase) || !identity.Backend.Equals(expected.Backend, StringComparison.OrdinalIgnoreCase) || identity.Scale != expected.Scale || identity.Mode != expected.Mode || !identity.Version.Equals(expected.Version, StringComparison.OrdinalIgnoreCase))) return AiModelValidationResult.Invalid(path, "ONNX model identity or version does not match the requested model.");
        var model = new AiManagedModel(identity, AiModelFormat.Onnx, metadata.DisplayName ?? identity.LogicalModel, path, null, metadata);
        return new(path, model, true, "Validated.");
    }
    public async Task<AiModelMetadata?> LoadMetadataAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        string path = MetadataPath(modelPath); if (!File.Exists(path)) return null;
        try { await using FileStream stream = File.OpenRead(path); return await JsonSerializer.DeserializeAsync<AiModelMetadata>(stream, cancellationToken: cancellationToken).ConfigureAwait(false); }
        catch (JsonException) { return null; }
    }
    public async Task<AiModelMetadata> SaveMetadataAsync(string modelPath, AiModelMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (metadata.SchemaVersion != MetadataSchemaVersion) throw new AiRestorationValidationException("Unsupported AI model metadata schema.");
        string hash = await HashAsync(new[] { modelPath }, cancellationToken).ConfigureAwait(false);
        metadata = metadata with { Identity = metadata.Identity with { Hash = hash } };
        Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath(modelPath))!);
        string staging = MetadataPath(modelPath) + ".staging";
        await File.WriteAllTextAsync(staging, JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
        File.Move(staging, MetadataPath(modelPath), true); _cache.Invalidate(modelPath); return metadata;
    }
    public AiModelLease Acquire(AiManagedModel model) => _cache.Acquire(model);
    public void Invalidate(string modelPath) => _cache.Invalidate(modelPath);
    public int CachedModelCount => _cache.Count;
    public static AiManagedModel? Find(IReadOnlyList<AiManagedModel> models, string logicalModel, string backend, AiRestorationScale scale, AiRestorationMode mode) => models.FirstOrDefault(model => model.Identity.LogicalModel.Equals(logicalModel, StringComparison.OrdinalIgnoreCase) && model.Identity.Backend.Equals(backend, StringComparison.OrdinalIgnoreCase) && model.Identity.Scale == scale && model.Identity.Mode == mode);
    public static string MetadataPath(string modelPath) => modelPath + ".mediaflux.json";
    private void Log(AiModelDiscoverySummary summary) => _log?.Invoke(summary.Describe());
    private static async Task<string> HashAsync(IEnumerable<string> paths, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string path in paths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            await using FileStream stream = File.OpenRead(path); byte[] buffer = new byte[81920]; int read;
            while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    public void Dispose() => _cache.Dispose();
}
