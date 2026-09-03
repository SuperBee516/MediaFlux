using Microsoft.Data.Sqlite;

namespace MediaFlux.Services;

/// <summary>Persistent, backend-neutral measurements for validated AI runtime configurations.</summary>
public sealed record AiBenchmarkDatabaseKey(
    string BackendId,
    string BackendIdentity,
    string Model,
    string GpuIdentity,
    string DriverVersion,
    string Precision,
    int Scale,
    string ResolutionClass);

public sealed record AiBenchmarkDatabaseEntry(
    AiBenchmarkDatabaseKey Key,
    NcnnRuntimeConfiguration Configuration,
    double FramesPerSecond,
    long? PeakVramBytes,
    bool IsStable,
    DateTimeOffset Timestamp,
    string Summary);

/// <summary>
/// SQLite-backed AI benchmark database. Validity is identity-based: a driver, backend, model,
/// GPU, precision, scale, or resolution-class change cannot match an earlier measurement.
/// </summary>
public sealed class AiBenchmarkDatabase
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly object _gate = new();

    public AiBenchmarkDatabase(string? path = null) => _path = path ?? AppPaths.AiBenchmarkDatabaseFile;

    public bool TryGetFastestStable(AiBenchmarkDatabaseKey key, out AiBenchmarkDatabaseEntry entry)
    {
        entry = default!;
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT thread_load,thread_process,thread_save,tile_size,fps,peak_vram_bytes,stable,recorded_utc_ticks,summary
                    FROM ai_benchmark_results
                    WHERE backend_id=$backend_id AND backend_identity=$backend_identity AND model=$model
                      AND gpu_identity=$gpu_identity AND driver_version=$driver_version AND precision=$precision
                      AND scale=$scale AND resolution_class=$resolution_class AND stable=1
                    ORDER BY fps DESC,recorded_utc_ticks DESC LIMIT 1;
                    """;
                AddKeyParameters(command, key);
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read()) return false;
                NcnnThreadConfiguration? threads = reader.IsDBNull(0) ? null : new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
                int? tile = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                entry = new(key, new NcnnRuntimeConfiguration(threads, tile), reader.GetDouble(4), reader.IsDBNull(5) ? null : reader.GetInt64(5), reader.GetInt64(6) != 0, new DateTimeOffset(reader.GetInt64(7), TimeSpan.Zero), reader.GetString(8));
                return true;
            }
        }
        catch { return false; }
    }

    public void Store(AiBenchmarkDatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Configuration.Validate();
        entry.Configuration.Threads?.Validate();
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO ai_benchmark_results(
                        backend_id,backend_identity,model,gpu_identity,driver_version,precision,scale,resolution_class,
                        thread_load,thread_process,thread_save,tile_size,fps,peak_vram_bytes,stable,recorded_utc_ticks,summary)
                    VALUES($backend_id,$backend_identity,$model,$gpu_identity,$driver_version,$precision,$scale,$resolution_class,
                        $thread_load,$thread_process,$thread_save,$tile_size,$fps,$peak_vram_bytes,$stable,$recorded_utc_ticks,$summary);
                    """;
                AddKeyParameters(command, entry.Key);
                command.Parameters.AddWithValue("$thread_load", (object?)entry.Configuration.Threads?.Load ?? DBNull.Value);
                command.Parameters.AddWithValue("$thread_process", (object?)entry.Configuration.Threads?.Process ?? DBNull.Value);
                command.Parameters.AddWithValue("$thread_save", (object?)entry.Configuration.Threads?.Save ?? DBNull.Value);
                command.Parameters.AddWithValue("$tile_size", (object?)entry.Configuration.TileSize ?? DBNull.Value);
                command.Parameters.AddWithValue("$fps", entry.FramesPerSecond);
                command.Parameters.AddWithValue("$peak_vram_bytes", (object?)entry.PeakVramBytes ?? DBNull.Value);
                command.Parameters.AddWithValue("$stable", entry.IsStable ? 1 : 0);
                command.Parameters.AddWithValue("$recorded_utc_ticks", entry.Timestamp.UtcTicks);
                command.Parameters.AddWithValue("$summary", entry.Summary ?? "");
                command.ExecuteNonQuery();
            }
        }
        catch
        {
            // Benchmarks remain optional diagnostics; persistence errors cannot affect restoration.
        }
    }

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode=WAL;
            PRAGMA user_version={SchemaVersion};
            CREATE TABLE IF NOT EXISTS ai_benchmark_results(
                id INTEGER PRIMARY KEY,
                backend_id TEXT NOT NULL, backend_identity TEXT NOT NULL, model TEXT NOT NULL,
                gpu_identity TEXT NOT NULL, driver_version TEXT NOT NULL, precision TEXT NOT NULL,
                scale INTEGER NOT NULL, resolution_class TEXT NOT NULL,
                thread_load INTEGER NULL, thread_process INTEGER NULL, thread_save INTEGER NULL, tile_size INTEGER NULL,
                fps REAL NOT NULL, peak_vram_bytes INTEGER NULL, stable INTEGER NOT NULL,
                recorded_utc_ticks INTEGER NOT NULL, summary TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_ai_benchmark_validity
                ON ai_benchmark_results(backend_id,backend_identity,model,gpu_identity,driver_version,precision,scale,resolution_class,stable,fps DESC);
            """;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void AddKeyParameters(SqliteCommand command, AiBenchmarkDatabaseKey key)
    {
        command.Parameters.AddWithValue("$backend_id", key.BackendId);
        command.Parameters.AddWithValue("$backend_identity", key.BackendIdentity);
        command.Parameters.AddWithValue("$model", key.Model);
        command.Parameters.AddWithValue("$gpu_identity", key.GpuIdentity);
        command.Parameters.AddWithValue("$driver_version", key.DriverVersion);
        command.Parameters.AddWithValue("$precision", key.Precision);
        command.Parameters.AddWithValue("$scale", key.Scale);
        command.Parameters.AddWithValue("$resolution_class", key.ResolutionClass);
    }
}
