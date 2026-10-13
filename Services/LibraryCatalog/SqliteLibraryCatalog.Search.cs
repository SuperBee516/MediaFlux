using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace MediaFlux.Services.LibraryCatalog
{
    // Microsoft.Data.Sqlite executes ADO.NET async calls synchronously and
    // SqliteCommand.Cancel is a no-op. SQLite's progress callback cooperatively
    // interrupts VM execution on this dedicated read connection.
    internal sealed class CatalogSearchReadCancellation : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly CancellationToken _token;
        private readonly delegate_progress? _callback;

        internal CatalogSearchReadCancellation(SqliteConnection connection, CancellationToken token)
        {
            _connection = connection;
            _token = token;
            token.ThrowIfCancellationRequested();
            if (token.CanBeCanceled)
            {
                _callback = static state => ((CancellationToken)state).IsCancellationRequested ? 1 : 0;
                raw.sqlite3_progress_handler(connection.Handle, 1_000, _callback, token);
            }
        }

        internal T Run<T>(Func<T> operation)
        {
            _token.ThrowIfCancellationRequested();
            try
            {
                T result = operation();
                _token.ThrowIfCancellationRequested();
                return result;
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode == raw.SQLITE_INTERRUPT && _token.IsCancellationRequested)
            {
                throw new OperationCanceledException("The catalog search was canceled.", exception, _token);
            }
        }

        internal void Check() => _token.ThrowIfCancellationRequested();

        public void Dispose()
        {
            if (_callback != null)
                raw.sqlite3_progress_handler(_connection.Handle, 0, null!, null!);
        }
    }

    public sealed partial class SqliteLibraryCatalog
    {
        internal LibraryFilePage QueryFilesWithCountPageGapForTesting(
            LibraryFileQuery query, Action afterCount, CancellationToken cancellationToken = default) =>
            QueryFilesCore(query, cancellationToken, afterCount);

        private static string FileTechnicalFactsSql() => ", " + string.Join(", ", new[]
        {
            GuardedSearchPropertySql("video.fps"),
            GuardedSearchPropertySql("video.bitrate"),
            GuardedSearchPropertySql("video.bit_depth")
        });

        private static string GuardedSearchPropertySql(string id)
        {
            CatalogSearchPropertyDefinition property = CatalogSearchRegistry.Get(id);
            return LibraryCatalogSqlExpressions.Guard(
                property.Expression, property.Info.RequiredMetadataVersion);
        }

        private static string SearchProjectionSql()
        {
            return ", " + string.Join(", ", new[]
            {
                LibraryCatalogSqlExpressions.MetadataState,
                GuardedSearchPropertySql("video.bitrate"),
                GuardedSearchPropertySql("video.fps"),
                GuardedSearchPropertySql("video.average_fps"),
                GuardedSearchPropertySql("video.nominal_fps"),
                GuardedSearchPropertySql("video.frame_rate_basis"),
                GuardedSearchPropertySql("video.bit_depth"),
                GuardedSearchPropertySql("video.stream_count"),
                GuardedSearchPropertySql("stream.audio_count"),
                GuardedSearchPropertySql("stream.subtitle_count"),
                GuardedSearchPropertySql("video.pixels_per_second"),
                GuardedSearchPropertySql("video.source_bpp"),
                GuardedSearchPropertySql("video.coded_orientation"),
                GuardedSearchPropertySql("video.resolution_class")
            });
        }

        private static LibraryFileTechnicalFacts ReadFileTechnicalFacts(SqliteDataReader reader)
        {
            const int start = 18;
            return new LibraryFileTechnicalFacts(
                reader.IsDBNull(start) ? null : reader.GetDouble(start),
                reader.IsDBNull(start + 1) ? null : reader.GetInt64(start + 1),
                reader.IsDBNull(start + 2) ? null : reader.GetInt32(start + 2));
        }

        private static CatalogSearchProjection ReadSearchProjection(SqliteDataReader reader)
        {
            const int start = 18;
            return new CatalogSearchProjection(
                reader.GetString(start),
                reader.IsDBNull(start + 1) ? null : reader.GetInt64(start + 1),
                reader.IsDBNull(start + 2) ? null : reader.GetDouble(start + 2),
                reader.IsDBNull(start + 3) ? null : reader.GetDouble(start + 3),
                reader.IsDBNull(start + 4) ? null : reader.GetDouble(start + 4),
                reader.IsDBNull(start + 5) ? null : reader.GetString(start + 5),
                reader.IsDBNull(start + 6) ? null : reader.GetInt32(start + 6),
                reader.IsDBNull(start + 7) ? null : reader.GetInt32(start + 7),
                reader.IsDBNull(start + 8) ? null : Convert.ToInt32(reader.GetInt64(start + 8)),
                reader.IsDBNull(start + 9) ? null : Convert.ToInt32(reader.GetInt64(start + 9)),
                reader.IsDBNull(start + 10) ? null : reader.GetDouble(start + 10),
                reader.IsDBNull(start + 11) ? null : reader.GetDouble(start + 11),
                reader.IsDBNull(start + 12) ? null : reader.GetString(start + 12),
                reader.IsDBNull(start + 13) ? null : reader.GetString(start + 13));
        }
    }
}
