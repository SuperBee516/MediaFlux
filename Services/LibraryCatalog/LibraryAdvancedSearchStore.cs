using System.Collections.Concurrent;
using System.Text.Json;
using MediaFlux.Services;

namespace MediaFlux.Services.LibraryCatalog;

public sealed record LibrarySavedSearch(string Name, CatalogSearchDefinition Definition);

/// <summary>Persists validated Advanced Search definitions in MediaFlux user data.</summary>
public sealed class LibraryAdvancedSearchStore
{
    public const int CurrentStoreVersion = 1;
    public const int MaximumNameLength = 80;

    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly object _sync;
    private readonly Action<string, Exception?> _diagnostic;

    public LibraryAdvancedSearchStore(string? path = null, Action<string, Exception?>? diagnostic = null)
    {
        _path = Path.GetFullPath(path ?? AppPaths.LibraryAdvancedSearchesFile);
        _sync = PathLocks.GetOrAdd(_path, static _ => new object());
        _diagnostic = diagnostic ?? ((message, exception) =>
            ErrorLogService.Append(AppPaths.UserDataDirectory, "Library Analyzer saved searches", _path, exception,
                message));
    }

    public string StoragePath => _path;

    public IReadOnlyList<LibrarySavedSearch> Load()
    {
        lock (_sync)
            return Array.AsReadOnly(ReadLocked().Searches.ToArray());
    }

    public LibrarySavedSearch SaveNew(string name, CatalogSearchDefinition definition)
    {
        string normalizedName = NormalizeName(name);
        CatalogSearchDefinition normalizedDefinition = NormalizeDefinition(definition);
        lock (_sync)
        {
            LoadResult loaded = ReadLocked();
            EnsureWritable(loaded);
            EnsureNameAvailable(loaded.Searches, normalizedName, excludedName: null);
            var searches = loaded.Searches.ToList();
            var saved = new LibrarySavedSearch(normalizedName, normalizedDefinition);
            searches.Add(saved);
            WriteLocked(searches, loaded.RecoveryRequired);
            return saved;
        }
    }

    public LibrarySavedSearch Update(string existingName, CatalogSearchDefinition definition)
    {
        string key = NormalizeName(existingName);
        CatalogSearchDefinition normalizedDefinition = NormalizeDefinition(definition);
        lock (_sync)
        {
            LoadResult loaded = ReadLocked();
            EnsureWritable(loaded);
            int index = FindIndex(loaded.Searches, key);
            if (index < 0)
                throw new KeyNotFoundException($"Saved search '{key}' no longer exists.");
            LibrarySavedSearch current = loaded.Searches[index];
            var updated = current with { Definition = normalizedDefinition };
            List<LibrarySavedSearch> searches = loaded.Searches.ToList();
            searches[index] = updated;
            WriteLocked(searches, loaded.RecoveryRequired);
            return updated;
        }
    }

    public LibrarySavedSearch Rename(string existingName, string newName)
    {
        string key = NormalizeName(existingName);
        string normalizedName = NormalizeName(newName);
        lock (_sync)
        {
            LoadResult loaded = ReadLocked();
            EnsureWritable(loaded);
            int index = FindIndex(loaded.Searches, key);
            if (index < 0)
                throw new KeyNotFoundException($"Saved search '{key}' no longer exists.");
            EnsureNameAvailable(loaded.Searches, normalizedName, key);
            var renamed = loaded.Searches[index] with { Name = normalizedName };
            List<LibrarySavedSearch> searches = loaded.Searches.ToList();
            searches[index] = renamed;
            WriteLocked(searches, loaded.RecoveryRequired);
            return renamed;
        }
    }

    public bool Delete(string name)
    {
        string key = NormalizeName(name);
        lock (_sync)
        {
            LoadResult loaded = ReadLocked();
            EnsureWritable(loaded);
            List<LibrarySavedSearch> searches = loaded.Searches.ToList();
            int index = FindIndex(searches, key);
            if (index < 0) return false;
            searches.RemoveAt(index);
            WriteLocked(searches, loaded.RecoveryRequired);
            return true;
        }
    }

    private LoadResult ReadLocked()
    {
        if (!File.Exists(_path)) return new LoadResult(new List<LibrarySavedSearch>(), false, false);

        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path), JsonOptions);
        }
        catch (Exception exception)
        {
            Log("Saved-search JSON is malformed or unreadable; the original file was retained.", exception);
            return new LoadResult(new List<LibrarySavedSearch>(), true, false);
        }

        if (document == null)
        {
            Log("Saved-search document is empty or null; the original file was retained.", null);
            return new LoadResult(new List<LibrarySavedSearch>(), true, false);
        }

        if (document.Version != CurrentStoreVersion)
        {
            Log($"Saved-search store version {document.Version} is not supported; the file was retained and will not be overwritten.", null);
            return new LoadResult(new List<LibrarySavedSearch>(), false, true);
        }

        bool recoveryRequired = false;
        if (document.Searches == null)
        {
            Log("Saved-search document has no search collection; the original file was retained.", null);
            return new LoadResult(new List<LibrarySavedSearch>(), true, false);
        }

        var searches = new List<LibrarySavedSearch>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SavedSearchDocument? item in document.Searches)
        {
            try
            {
                if (item == null) throw new InvalidDataException("Saved-search entry is null.");
                string name = NormalizeName(item.Name);
                if (!string.Equals(name, item.Name, StringComparison.Ordinal))
                    throw new InvalidDataException("Saved-search name contains leading or trailing whitespace.");
                CatalogSearchDefinition definition = NormalizeDefinition(item.Definition!);
                if (!names.Add(name))
                    throw new InvalidDataException($"Saved-search name '{name}' is duplicated without regard to case.");
                searches.Add(new LibrarySavedSearch(name, definition));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
                                               NullReferenceException or KeyNotFoundException)
            {
                recoveryRequired = true;
                Log("An invalid saved-search entry was skipped; the original file was retained for recovery.", exception);
            }
        }

        return new LoadResult(Sort(searches), recoveryRequired, false);
    }

    private void WriteLocked(IReadOnlyList<LibrarySavedSearch> searches, bool recoveryRequired)
    {
        var document = new Document
        {
            Version = CurrentStoreVersion,
            Searches = Sort(searches).Select(item => (SavedSearchDocument?)new SavedSearchDocument
            {
                Name = item.Name,
                Definition = CloneDefinition(item.Definition)
            }).ToList()
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);

        if (recoveryRequired && File.Exists(_path))
        {
            string recoveryPath = _path + ".recovery-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N") + ".json";
            File.Copy(_path, recoveryPath, overwrite: false);
        }

        string temporary = Path.Combine(directory, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception exception)
        {
            Log("Saved searches could not be written. The previous primary file was retained where possible.", exception);
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) { Log("A temporary saved-search file could not be removed.", exception); }
        }
    }

    private static CatalogSearchDefinition NormalizeDefinition(CatalogSearchDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        CatalogSearchDefinitionValidator.Validate(definition);
        return CloneDefinition(definition);
    }

    private static CatalogSearchDefinition CloneDefinition(CatalogSearchDefinition definition) => definition with
    {
        Conditions = Array.AsReadOnly(definition.Conditions.Select(condition => condition with
        {
            Values = condition.Values == null ? null : Array.AsReadOnly(condition.Values.ToArray())
        }).ToArray())
    };

    private static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Enter a name for the saved search.", nameof(name));
        string normalized = name.Trim();
        if (normalized.Length > MaximumNameLength)
            throw new ArgumentException($"Saved-search names are limited to {MaximumNameLength} characters.", nameof(name));
        if (normalized.Any(char.IsControl))
            throw new ArgumentException("Saved-search names cannot contain control characters.", nameof(name));
        return normalized;
    }

    private static void EnsureNameAvailable(IReadOnlyList<LibrarySavedSearch> searches, string name, string? excludedName)
    {
        if (searches.Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                 (excludedName == null || !item.Name.Equals(excludedName, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException($"A saved search named '{name}' already exists.");
    }

    private static int FindIndex(IReadOnlyList<LibrarySavedSearch> searches, string name)
    {
        for (int i = 0; i < searches.Count; i++)
            if (searches[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static List<LibrarySavedSearch> Sort(IEnumerable<LibrarySavedSearch> searches) => searches
        .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(item => item.Name, StringComparer.Ordinal)
        .ToList();

    private static void EnsureWritable(LoadResult loaded)
    {
        if (loaded.UnsupportedVersion)
            throw new InvalidOperationException("This saved-search file was written by a newer or unsupported version of MediaFlux and will not be overwritten.");
    }

    private void Log(string message, Exception? exception)
    {
        try { _diagnostic(message, exception); }
        catch { /* Diagnostics must never prevent the Library Analyzer from opening. */ }
    }

    private sealed record LoadResult(List<LibrarySavedSearch> Searches, bool RecoveryRequired, bool UnsupportedVersion);

    private sealed class Document
    {
        public int Version { get; set; }
        public List<SavedSearchDocument?>? Searches { get; set; }
    }

    private sealed class SavedSearchDocument
    {
        public string? Name { get; set; }
        public CatalogSearchDefinition? Definition { get; set; }
    }
}
