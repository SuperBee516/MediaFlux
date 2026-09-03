using System.Text.Json;
using System.Collections.Concurrent;

namespace MediaFlux.Services;

/// <summary>Versioned, best-effort cache for successful NCNN runtime selections.</summary>
public sealed class NcnnPerformanceTuningCacheService
{
    // Version 2 rejects selections made before successful Vulkan errors were
    // distinguished from healthy inference output.
    public const int CurrentSchemaVersion = 2;
    private readonly string _path;
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public NcnnPerformanceTuningCacheService(string? path = null)
    {
        _path = path ?? AppPaths.NcnnPerformanceTuningCacheFile;
        _gate = Locks.GetOrAdd(Path.GetFullPath(_path), _ => new object());
    }

    public bool TryGet(NcnnTuningCacheKey key, out NcnnRuntimeConfiguration configuration)
    {
        configuration = NcnnRuntimeConfiguration.SafeDefault;
        try
        {
            lock (_gate)
            {
                NcnnTuningCacheDocument? document = Read();
                NcnnTuningCacheEntry? entry = document?.Entries.FirstOrDefault(candidate => candidate.Key == key.Value);
                if (entry is null) return false;
                entry.Configuration.Validate();
                entry.Configuration.Threads?.Validate();
                configuration = entry.Configuration;
                return true;
            }
        }
        catch { return false; }
    }

    public void Store(NcnnTuningCacheKey key, NcnnRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate(); configuration.Threads?.Validate();
        try
        {
            lock (_gate)
            {
                NcnnTuningCacheDocument document = Read() ?? new(CurrentSchemaVersion, new());
                if (document.Version != CurrentSchemaVersion) document = new(CurrentSchemaVersion, new());
                document.Entries.RemoveAll(entry => entry.Key == key.Value);
                document.Entries.Add(new(key.Value, configuration));
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
                File.Move(temporary, _path, true);
            }
        }
        catch
        {
            // Persistent tuning is optional. A write failure must never affect restoration.
        }
    }

    public void Invalidate() { try { lock (_gate) { if (File.Exists(_path)) File.Delete(_path); } } catch { } }

    private NcnnTuningCacheDocument? Read()
    {
        if (!File.Exists(_path)) return null;
        NcnnTuningCacheDocument? document = JsonSerializer.Deserialize<NcnnTuningCacheDocument>(File.ReadAllText(_path));
        return document?.Version == CurrentSchemaVersion ? document : null;
    }

    private sealed record NcnnTuningCacheDocument(int Version, List<NcnnTuningCacheEntry> Entries);
    private sealed record NcnnTuningCacheEntry(string Key, NcnnRuntimeConfiguration Configuration);
}

public sealed record NcnnTuningCacheKey(string Value)
{
    public static NcnnTuningCacheKey Create(string gpuIdentity, string backendIdentity, string model, int scale, string resolutionClass, string? driverVersion = null) =>
        new($"{gpuIdentity.Trim()}|{backendIdentity.Trim()}|{model.Trim()}|{scale}|{resolutionClass}|driver={(driverVersion ?? "").Trim()}");
}
