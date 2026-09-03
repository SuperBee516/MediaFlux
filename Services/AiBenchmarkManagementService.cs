using System.Text.Json;

namespace MediaFlux.Services;

/// <summary>Read-mostly management boundary for persisted benchmarks. It intentionally never
/// changes the tuning cache; normal identity validation continues to govern cache reuse.</summary>
public sealed class AiBenchmarkManagementService
{
    public const int ExchangeFormatVersion = 1;
    private readonly AiBenchmarkDatabase _database;

    public AiBenchmarkManagementService(AiBenchmarkDatabase? database = null) => _database = database ?? new AiBenchmarkDatabase();
    public IReadOnlyList<AiBenchmarkRecord> List() => _database.List();
    public int DeleteSelected(IEnumerable<AiBenchmarkRecord> records) => _database.Delete(records.Select(record => record.Id));
    public int DeleteObsolete() => _database.DeleteObsolete();

    public string CreateExportJson(IEnumerable<AiBenchmarkRecord> records) =>
        JsonSerializer.Serialize(new AiBenchmarkExchangeDocument(ExchangeFormatVersion, "MediaFlux AI Benchmark Manager", DateTimeOffset.UtcNow, records.Select(record => record.Entry).ToArray()), JsonOptions);

    public async Task ExportAsync(string path, IEnumerable<AiBenchmarkRecord> records, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, CreateExportJson(records), token).ConfigureAwait(false);
    }

    public async Task<AiBenchmarkImportResult> ImportAsync(string path, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            await using FileStream stream = File.OpenRead(path);
            AiBenchmarkExchangeDocument? document = await JsonSerializer.DeserializeAsync<AiBenchmarkExchangeDocument>(stream, JsonOptions, token).ConfigureAwait(false);
            if (document is null) return new(0, 0, "The benchmark file is empty.");
            if (document.Version != ExchangeFormatVersion) return new(0, 0, $"Unsupported benchmark file version {document.Version}. This MediaFlux version supports {ExchangeFormatVersion}.");
            int imported = 0, rejected = 0;
            foreach (AiBenchmarkDatabaseEntry entry in document.Entries ?? Array.Empty<AiBenchmarkDatabaseEntry>())
            {
                token.ThrowIfCancellationRequested();
                if (!IsValid(entry)) { rejected++; continue; }
                if (_database.TryStore(entry)) imported++; else rejected++;
            }
            return new(imported, rejected, rejected == 0 ? "Import completed." : "Import completed with rejected invalid entries.");
        }
        catch (JsonException) { return new(0, 0, "The selected file is not a valid AI benchmark export."); }
        catch (IOException ex) { return new(0, 0, ex.Message); }
    }

    internal static bool IsValid(AiBenchmarkDatabaseEntry? entry)
    {
        if (entry is null || entry.FramesPerSecond < 0 || entry.Timestamp == default || entry.Key.Scale is < 1 or > 4 ||
            string.IsNullOrWhiteSpace(entry.Key.BackendId) || string.IsNullOrWhiteSpace(entry.Key.BackendIdentity) ||
            string.IsNullOrWhiteSpace(entry.Key.Model) || string.IsNullOrWhiteSpace(entry.Key.GpuIdentity) ||
            string.IsNullOrWhiteSpace(entry.Key.DriverVersion) || string.IsNullOrWhiteSpace(entry.Key.Precision) ||
            string.IsNullOrWhiteSpace(entry.Key.ResolutionClass)) return false;
        try { entry.Configuration.Validate(); entry.Configuration.Threads?.Validate(); return true; } catch { return false; }
    }

    private sealed record AiBenchmarkExchangeDocument(int Version, string Product, DateTimeOffset ExportedAt, IReadOnlyList<AiBenchmarkDatabaseEntry>? Entries);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}

public sealed record AiBenchmarkImportResult(int Imported, int Rejected, string Message);
