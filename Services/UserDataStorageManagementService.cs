namespace MediaFlux.Services;

/// <summary>
/// Owns lifecycle rules for MediaFlux-generated UserData.  It deliberately never treats
/// configuration, queues, profiles, catalog state, history, or user assets as cache.
/// Size collection is asynchronous because a large forensic working directory can contain
/// millions of files.
/// </summary>
public enum UserDataStorageCategory
{
    AiFailureForensics,
    RestorationPreviews,
    DuplicatePreviews,
    FramePreviews,
    TemporaryStaging,
    Logs,
    CatalogSafetyArtifacts,
    RegenerableRuntimeCache,
    PersistentUserData
}

public enum UserDataCleanupScope
{
    ExpiredGeneratedData,
    AiFailureForensics,
    Previews,
    TemporaryStaging,
    Logs,
    CatalogSafetyArtifacts,
    RegenerableRuntimeCache
}

public sealed record UserDataStorageItem(UserDataStorageCategory Category, string Path, long Bytes, int Files);
public sealed record UserDataStorageSnapshot(IReadOnlyList<UserDataStorageItem> Items)
{
    public long TotalBytes => Items.Aggregate(0L, (total, item) => SaturatingAdd(total, item.Bytes));
    private static long SaturatingAdd(long value, long addition) => addition > 0 && value > long.MaxValue - addition ? long.MaxValue : value + Math.Max(0, addition);
}
public sealed record UserDataCleanupResult(int DeletedFiles, int DeletedDirectories, long FreedBytes, IReadOnlyList<string> Errors);

public sealed class UserDataStorageManagementService
{
    public static readonly TimeSpan AiForensicsRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan PreviewRetention = TimeSpan.FromDays(30);
    public static readonly TimeSpan TemporaryRetention = TimeSpan.FromDays(7);
    public static readonly TimeSpan CatalogSafetyRetention = TimeSpan.FromDays(30);
    public const int MaximumRetainedAiForensics = 3;
    public const long MaximumRetainedAiForensicsBytes = 20L * 1024 * 1024 * 1024;
    public const int MaximumCatalogSafetyArtifacts = 10;

    private readonly string _userDataDirectory;
    private readonly Func<bool> _hasActiveOperations;
    public UserDataStorageManagementService(string? userDataDirectory = null, Func<bool>? hasActiveOperations = null)
    { _userDataDirectory = Path.GetFullPath(userDataDirectory ?? AppPaths.UserDataDirectory); _hasActiveOperations = hasActiveOperations ?? (() => false); }

    /// <summary>Runs off the UI thread; callers can bind the returned categories in Settings later.</summary>
    public Task<UserDataStorageSnapshot> GetSnapshotAsync(CancellationToken token = default) => Task.Run(() => GetSnapshot(token), token);
    public Task<UserDataCleanupResult> CleanupAsync(UserDataCleanupScope scope, CancellationToken token = default) => Task.Run(() => Cleanup(scope, DateTime.UtcNow, token), token);

    /// <summary>Small startup pass: only named, stale MediaFlux-generated content is considered.</summary>
    public UserDataCleanupResult CleanupExpiredGeneratedData(DateTime? utcNow = null) => Cleanup(UserDataCleanupScope.ExpiredGeneratedData, utcNow ?? DateTime.UtcNow, CancellationToken.None);

    private UserDataStorageSnapshot GetSnapshot(CancellationToken token)
    {
        var items = new List<UserDataStorageItem>();
        if (!Directory.Exists(_userDataDirectory)) return new(items);
        foreach (FileInfo file in SafeFilesTopLevel(_userDataDirectory))
        {
            token.ThrowIfCancellationRequested();
            items.Add(new(UserDataStorageCategory.PersistentUserData, file.FullName, file.Length, 1));
        }
        foreach (DirectoryInfo directory in SafeDirectories(_userDataDirectory, "*"))
        {
            if (directory.Name.Equals("data", StringComparison.OrdinalIgnoreCase)) continue;
            token.ThrowIfCancellationRequested();
            (long bytes, int files) = Measure(directory.FullName, token);
            items.Add(new(directory.Name.Equals("temp", StringComparison.OrdinalIgnoreCase) ? UserDataStorageCategory.TemporaryStaging : UserDataStorageCategory.PersistentUserData, directory.FullName, bytes, files));
        }
        string data = Data;
        if (!Directory.Exists(data)) return new(items);
        foreach (FileInfo file in SafeFilesTopLevel(data))
        {
            token.ThrowIfCancellationRequested();
            items.Add(new(IsRegenerableFile(file.Name) ? UserDataStorageCategory.RegenerableRuntimeCache : UserDataStorageCategory.PersistentUserData, file.FullName, file.Length, 1));
        }
        foreach (DirectoryInfo directory in SafeDirectories(data, "*"))
        {
            token.ThrowIfCancellationRequested();
            (long bytes, int files) = Measure(directory.FullName, token);
            items.Add(new(CategoryForDirectory(directory.Name), directory.FullName, bytes, files));
        }
        return new(items);
    }

    private UserDataCleanupResult Cleanup(UserDataCleanupScope scope, DateTime now, CancellationToken token)
    {
        var result = new MutableResult();
        bool all = scope == UserDataCleanupScope.ExpiredGeneratedData;
        if (all || scope == UserDataCleanupScope.AiFailureForensics) PruneAiForensics(now, result, token);
        bool active = _hasActiveOperations();
        if (!active && (all || scope == UserDataCleanupScope.Previews))
        {
            PruneFiles(Path.Combine(Data, "restoration-previews"), PreviewRetention, result, token, maxFiles: 20);
            PruneFiles(Path.Combine(Data, "duplicate-previews"), PreviewRetention, result, token);
            PruneFiles(Path.Combine(Data, "frame-previews"), PreviewRetention, result, token);
        }
        if (!active && (all || scope == UserDataCleanupScope.TemporaryStaging)) PruneTemporary(now, result, token);
        if (all || scope == UserDataCleanupScope.Logs) PruneFiles(Path.Combine(Data, "logs"), TimeSpan.FromDays(30), result, token, excludeName: "mediaflux-errors.log");
        if (all || scope == UserDataCleanupScope.CatalogSafetyArtifacts)
        {
            PruneFiles(Path.Combine(Data, "catalog-backups"), CatalogSafetyRetention, result, token, maxFiles: MaximumCatalogSafetyArtifacts);
            PruneDirectories(Path.Combine(Data, "catalog-recovery"), CatalogSafetyRetention, result, token, maxDirectories: MaximumCatalogSafetyArtifacts);
        }
        if (!active && scope == UserDataCleanupScope.RegenerableRuntimeCache)
        {
            DeleteFile(AppPaths.NcnnPerformanceTuningCacheFile, result);
            DeleteFile(Path.Combine(Data, "ai-benchmark-history.json"), result);
            DeleteDirectory(Path.Combine(Data, "tensorrt-engines"), result);
        }
        if (!active && all) PruneRegenerableCaches(now, result, token);
        return new(result.Files, result.Directories, result.Bytes, result.Errors);
    }

    private void PruneAiForensics(DateTime now, MutableResult result, CancellationToken token)
    {
        string root = Path.Combine(Data, "ai-intermediates");
        if (!Directory.Exists(root)) return;
        DirectoryInfo[] candidates = SafeDirectories(root, "ai-intermediate-*").OrderByDescending(d => d.LastWriteTimeUtc).ToArray();
        long retainedBytes = 0;
        for (int index = 0; index < candidates.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            DirectoryInfo candidate = candidates[index];
            if (AiProductionHardeningService.IsActive(candidate.FullName)) continue;
            if (candidate.LastWriteTimeUtc < now - AiForensicsRetention || index >= MaximumRetainedAiForensics) { DeleteDirectory(candidate.FullName, result); continue; }
            (long bytes, _) = Measure(candidate.FullName, token);
            if (bytes > MaximumRetainedAiForensicsBytes - retainedBytes) DeleteDirectory(candidate.FullName, result);
            else retainedBytes += bytes;
        }
    }

    private void PruneTemporary(DateTime now, MutableResult result, CancellationToken token)
    {
        string temp = Path.Combine(_userDataDirectory, "temp");
        PruneFiles(temp, TemporaryRetention, result, token);
        foreach (DirectoryInfo directory in SafeDirectories(temp, "*"))
        {
            token.ThrowIfCancellationRequested();
            if (directory.LastWriteTimeUtc < now - TemporaryRetention) DeleteDirectory(directory.FullName, result);
        }
        foreach (string root in new[] { Path.Combine(Data, "staging"), Path.Combine(Data, "encode-staging"), Path.Combine(Data, "temporary-encodes") })
            PruneDirectories(root, TemporaryRetention, result, token);
    }
    private void PruneRegenerableCaches(DateTime now, MutableResult result, CancellationToken token)
    {
        // Benchmark reruns and TensorRT engines are derived from installed models/runtime and are
        // safe to recreate. Keep a recent bounded set; no engine is loaded during startup cleanup.
        PruneDirectories(Path.Combine(Data, "ai-benchmark-reruns"), PreviewRetention, result, token, maxDirectories: 5);
        PruneFiles(Path.Combine(Data, "tensorrt-engines"), TimeSpan.FromDays(90), result, token);
        string benchmarks = Path.Combine(Data, "ai-benchmarks.db");
        if (File.Exists(benchmarks)) _ = new AiBenchmarkDatabase(benchmarks).PruneOlderThan(TimeSpan.FromDays(365), now);
        PruneTransientRootFiles(Data, now - TemporaryRetention, result, token);
    }
    private static void PruneTransientRootFiles(string root, DateTime cutoff, MutableResult result, CancellationToken token)
    {
        foreach (FileInfo file in SafeFilesTopLevel(root))
        { token.ThrowIfCancellationRequested(); if (file.LastWriteTimeUtc < cutoff && (file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || file.Name.Contains(".partial", StringComparison.OrdinalIgnoreCase) || file.Name.Contains(".staging", StringComparison.OrdinalIgnoreCase))) DeleteFile(file.FullName, result); }
    }

    private static void PruneFiles(string root, TimeSpan retention, MutableResult result, CancellationToken token, int? maxFiles = null, string? excludeName = null)
    {
        if (!Directory.Exists(root)) return;
        FileInfo[] files = SafeFiles(root).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
        DateTime cutoff = DateTime.UtcNow - retention;
        for (int index = 0; index < files.Length; index++)
        {
            token.ThrowIfCancellationRequested(); FileInfo file = files[index];
            if (file.Name.Equals(excludeName, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.LastWriteTimeUtc < cutoff || maxFiles is int maximum && index >= maximum) DeleteFile(file.FullName, result);
        }
    }
    private static void PruneDirectories(string root, TimeSpan retention, MutableResult result, CancellationToken token, int? maxDirectories = null)
    {
        if (!Directory.Exists(root)) return; DateTime cutoff = DateTime.UtcNow - retention;
        DirectoryInfo[] directories = SafeDirectories(root, "*").OrderByDescending(d => d.LastWriteTimeUtc).ToArray();
        for (int index = 0; index < directories.Length; index++)
        { token.ThrowIfCancellationRequested(); if (directories[index].LastWriteTimeUtc < cutoff || maxDirectories is int max && index >= max) DeleteDirectory(directories[index].FullName, result); }
    }

    private static UserDataStorageCategory CategoryForDirectory(string name) => name.ToLowerInvariant() switch
    {
        "ai-intermediates" => UserDataStorageCategory.AiFailureForensics,
        "restoration-previews" => UserDataStorageCategory.RestorationPreviews,
        "duplicate-previews" => UserDataStorageCategory.DuplicatePreviews,
        "frame-previews" => UserDataStorageCategory.FramePreviews,
        "staging" or "encode-staging" or "temporary-encodes" => UserDataStorageCategory.TemporaryStaging,
        "logs" => UserDataStorageCategory.Logs,
        "catalog-backups" or "catalog-recovery" => UserDataStorageCategory.CatalogSafetyArtifacts,
        "ai-benchmark-reruns" or "tensorrt-engines" => UserDataStorageCategory.RegenerableRuntimeCache,
        _ => UserDataStorageCategory.PersistentUserData
    };
    private static bool IsRegenerableFile(string name) => name.Equals("ncnn-performance-tuning.json", StringComparison.OrdinalIgnoreCase) || name.Equals("ai-benchmark-history.json", StringComparison.OrdinalIgnoreCase);
    private string Data => Path.Combine(_userDataDirectory, "data");
    private static IEnumerable<FileInfo> SafeFilesTopLevel(string root) { try { return new DirectoryInfo(root).EnumerateFiles().ToArray(); } catch { return Array.Empty<FileInfo>(); } }
    private static IEnumerable<DirectoryInfo> SafeDirectories(string root, string pattern) { try { return new DirectoryInfo(root).EnumerateDirectories(pattern).Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0).ToArray(); } catch { return Array.Empty<DirectoryInfo>(); } }
    private static IEnumerable<FileInfo> SafeFiles(string root) { try { return new DirectoryInfo(root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToArray(); } catch { return Array.Empty<FileInfo>(); } }
    private static (long Bytes, int Files) Measure(string path, CancellationToken token) { try { if (File.Exists(path)) return (new FileInfo(path).Length, 1); if (!Directory.Exists(path)) return (0, 0); long bytes = 0; int files = 0; foreach (FileInfo file in SafeFiles(path)) { token.ThrowIfCancellationRequested(); bytes = bytes > long.MaxValue - file.Length ? long.MaxValue : bytes + file.Length; files++; } return (bytes, files); } catch { return (0, 0); } }
    private static void DeleteFile(string path, MutableResult result) { try { var file = new FileInfo(path); if (!file.Exists) return; long size = file.Length; file.Delete(); result.Files++; result.Bytes += size; } catch (Exception ex) { result.Errors.Add($"{path}: {ex.Message}"); } }
    private static void DeleteDirectory(string path, MutableResult result) { try { var directory = new DirectoryInfo(path); if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return; (long bytes, int files) = Measure(path, CancellationToken.None); directory.Delete(true); result.Directories++; result.Files += files; result.Bytes += bytes; } catch (Exception ex) { result.Errors.Add($"{path}: {ex.Message}"); } }
    private sealed class MutableResult { public int Files; public int Directories; public long Bytes; public List<string> Errors { get; } = new(); }
}
