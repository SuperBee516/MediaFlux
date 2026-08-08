using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        public LibraryScanAcceleratorState? GetScanAcceleratorState(long locationId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT accelerator_kind,volume_identity,filesystem_name,journal_id,next_usn,lowest_valid_usn,last_authoritative_scan_utc_ticks,status_message FROM location_scan_accelerators WHERE location_id=$id;";
            command.Parameters.AddWithValue("$id", locationId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
                return null;
            return new LibraryScanAcceleratorState(
                locationId,
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                FromUtcTicks(reader.GetInt64(6)),
                reader.GetString(7));
        }

        public void SaveScanAcceleratorState(LibraryScanAcceleratorState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO location_scan_accelerators(
                        location_id,accelerator_kind,volume_identity,filesystem_name,journal_id,next_usn,
                        lowest_valid_usn,last_authoritative_scan_utc_ticks,status_message,updated_utc_ticks)
                    VALUES($location,$kind,$volume,$filesystem,$journal,$next,$lowest,$scan,$message,$now)
                    ON CONFLICT(location_id) DO UPDATE SET
                        accelerator_kind=excluded.accelerator_kind,
                        volume_identity=excluded.volume_identity,
                        filesystem_name=excluded.filesystem_name,
                        journal_id=excluded.journal_id,
                        next_usn=excluded.next_usn,
                        lowest_valid_usn=excluded.lowest_valid_usn,
                        last_authoritative_scan_utc_ticks=excluded.last_authoritative_scan_utc_ticks,
                        status_message=excluded.status_message,
                        updated_utc_ticks=excluded.updated_utc_ticks;
                    """;
                command.Parameters.AddWithValue("$location", state.LocationId);
                command.Parameters.AddWithValue("$kind", state.AcceleratorKind);
                command.Parameters.AddWithValue("$volume", state.VolumeIdentity);
                command.Parameters.AddWithValue("$filesystem", state.FileSystemName);
                command.Parameters.AddWithValue("$journal", state.JournalId);
                command.Parameters.AddWithValue("$next", state.NextUsn);
                command.Parameters.AddWithValue("$lowest", state.LowestValidUsn);
                command.Parameters.AddWithValue("$scan", state.LastAuthoritativeScanUtc.ToUniversalTime().Ticks);
                command.Parameters.AddWithValue("$message", state.StatusMessage ?? "");
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public void ClearScanAcceleratorState(long locationId)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM location_scan_accelerators WHERE location_id=$id;";
                command.Parameters.AddWithValue("$id", locationId);
                command.ExecuteNonQuery();
                return null;
            });
        }
    }
}
