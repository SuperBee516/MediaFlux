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

/// <summary>Stable database row identity used only for benchmark management operations.</summary>
public sealed record AiBenchmarkRecord(long Id, AiBenchmarkDatabaseEntry Entry);

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

    /// <summary>Read-only comparison lookup used to explain a strict benchmark cache miss after a driver update.</summary>
    public bool TryGetLatestStableWithDifferentDriver(AiBenchmarkDatabaseKey key, out AiBenchmarkDatabaseEntry entry)
    {
        entry = default!;
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open(); using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    SELECT driver_version,thread_load,thread_process,thread_save,tile_size,fps,peak_vram_bytes,stable,recorded_utc_ticks,summary
                    FROM ai_benchmark_results
                    WHERE backend_id=$backend_id AND backend_identity=$backend_identity AND model=$model
                      AND gpu_identity=$gpu_identity AND driver_version<>$driver_version AND precision=$precision
                      AND scale=$scale AND resolution_class=$resolution_class AND stable=1
                    ORDER BY recorded_utc_ticks DESC LIMIT 1;
                    """;
                AddKeyParameters(command, key); using SqliteDataReader reader = command.ExecuteReader(); if (!reader.Read()) return false;
                NcnnThreadConfiguration? threads = reader.IsDBNull(1) ? null : new(reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                int? tile = reader.IsDBNull(4) ? null : reader.GetInt32(4);
                entry = new(key with { DriverVersion = reader.GetString(0) }, new NcnnRuntimeConfiguration(threads, tile), reader.GetDouble(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetInt64(7) != 0, new DateTimeOffset(reader.GetInt64(8), TimeSpan.Zero), reader.GetString(9));
                return true;
            }
        }
        catch { return false; }
    }

    public void Store(AiBenchmarkDatabaseEntry entry)
    {
        TryStore(entry);
    }

    public bool TryStore(AiBenchmarkDatabaseEntry entry)
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
                return true;
            }
        }
        catch
        {
            // Benchmarks remain optional diagnostics; persistence errors cannot affect restoration.
            return false;
        }
    }

    public IReadOnlyList<AiBenchmarkRecord> List(int maximumEntries = 5000)
    {
        int limit = Math.Clamp(maximumEntries, 1, 50_000);
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT id,backend_id,backend_identity,model,gpu_identity,driver_version,precision,scale,resolution_class,thread_load,thread_process,thread_save,tile_size,fps,peak_vram_bytes,stable,recorded_utc_ticks,summary FROM ai_benchmark_results ORDER BY recorded_utc_ticks DESC,id DESC LIMIT $limit;";
                command.Parameters.AddWithValue("$limit", limit);
                using SqliteDataReader reader = command.ExecuteReader();
                var results = new List<AiBenchmarkRecord>();
                while (reader.Read()) results.Add(ReadRecord(reader));
                return results;
            }
        }
        catch { return Array.Empty<AiBenchmarkRecord>(); }
    }

    public int Delete(IEnumerable<long> ids)
    {
        long[] values = ids.Distinct().Where(id => id > 0).ToArray();
        if (values.Length == 0) return 0;
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM ai_benchmark_results WHERE id=$id;";
                SqliteParameter parameter = command.Parameters.Add("$id", SqliteType.Integer);
                int deleted = 0;
                foreach (long id in values) { parameter.Value = id; deleted += command.ExecuteNonQuery(); }
                transaction.Commit();
                return deleted;
            }
        }
        catch { return 0; }
    }

    /// <summary>Rows with failed validation are obsolete: they are never selected for tuning.</summary>
    public int DeleteObsolete()
    {
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "DELETE FROM ai_benchmark_results WHERE stable=0;";
                return command.ExecuteNonQuery();
            }
        }
        catch { return 0; }
    }

    /// <summary>Bounds historical diagnostics; current matching records remain unaffected until they age out.</summary>
    public int PruneOlderThan(TimeSpan retention, DateTimeOffset? utcNow = null)
    {
        if (retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        try
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open(); using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "DELETE FROM ai_benchmark_results WHERE recorded_utc_ticks < $cutoff;";
                command.Parameters.AddWithValue("$cutoff", (utcNow ?? DateTimeOffset.UtcNow).Subtract(retention).UtcTicks);
                return command.ExecuteNonQuery();
            }
        }
        catch { return 0; }
    }

    private static AiBenchmarkRecord ReadRecord(SqliteDataReader reader)
    {
        NcnnThreadConfiguration? threads = reader.IsDBNull(9) ? null : new(reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11));
        int? tile = reader.IsDBNull(12) ? null : reader.GetInt32(12);
        var key = new AiBenchmarkDatabaseKey(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7), reader.GetString(8));
        var entry = new AiBenchmarkDatabaseEntry(key, new NcnnRuntimeConfiguration(threads, tile), reader.GetDouble(13), reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.GetInt64(15) != 0, new DateTimeOffset(reader.GetInt64(16), TimeSpan.Zero), reader.GetString(17));
        return new(reader.GetInt64(0), entry);
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
