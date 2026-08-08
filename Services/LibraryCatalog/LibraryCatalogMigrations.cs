using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    internal sealed record LibraryCatalogMigration(int Version, string Name, string Sql);

    internal static class LibraryCatalogMigrations
    {
        public const int CurrentVersion = 3;

        public static IReadOnlyList<LibraryCatalogMigration> All { get; } =
            new[]
            {
                new LibraryCatalogMigration(
                    1,
                    "Initial inventory catalog",
                    """
                    CREATE TABLE library_locations (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        path TEXT NOT NULL,
                        path_key TEXT NOT NULL UNIQUE,
                        include_subfolders INTEGER NOT NULL CHECK (include_subfolders IN (0, 1)),
                        is_enabled INTEGER NOT NULL CHECK (is_enabled IN (0, 1)),
                        availability_state INTEGER NOT NULL DEFAULT 0 CHECK (availability_state BETWEEN 0 AND 3),
                        last_error TEXT NOT NULL DEFAULT '',
                        current_generation INTEGER NOT NULL DEFAULT 0 CHECK (current_generation >= 0),
                        created_utc_ticks INTEGER NOT NULL,
                        updated_utc_ticks INTEGER NOT NULL,
                        last_completed_scan_utc_ticks INTEGER NULL
                    ) STRICT;

                    CREATE TABLE indexed_files (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        full_path TEXT NOT NULL,
                        path_key TEXT NOT NULL UNIQUE,
                        file_name TEXT NOT NULL,
                        extension TEXT NOT NULL,
                        size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                        creation_utc_ticks INTEGER NULL,
                        last_write_utc_ticks INTEGER NOT NULL,
                        volume_id TEXT NOT NULL DEFAULT '',
                        file_identity TEXT NOT NULL DEFAULT '',
                        availability_state INTEGER NOT NULL DEFAULT 0 CHECK (availability_state BETWEEN 0 AND 2),
                        last_seen_utc_ticks INTEGER NOT NULL,
                        created_utc_ticks INTEGER NOT NULL,
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE TABLE scan_runs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        location_id INTEGER NOT NULL REFERENCES library_locations(id) ON DELETE CASCADE,
                        generation INTEGER NOT NULL CHECK (generation > 0),
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 3),
                        started_utc_ticks INTEGER NOT NULL,
                        completed_utc_ticks INTEGER NULL,
                        discovered_files INTEGER NOT NULL DEFAULT 0 CHECK (discovered_files >= 0),
                        unchanged_files INTEGER NOT NULL DEFAULT 0 CHECK (unchanged_files >= 0),
                        new_files INTEGER NOT NULL DEFAULT 0 CHECK (new_files >= 0),
                        changed_files INTEGER NOT NULL DEFAULT 0 CHECK (changed_files >= 0),
                        missing_files INTEGER NOT NULL DEFAULT 0 CHECK (missing_files >= 0),
                        error_count INTEGER NOT NULL DEFAULT 0 CHECK (error_count >= 0),
                        error_text TEXT NOT NULL DEFAULT '',
                        UNIQUE (location_id, generation)
                    ) STRICT;

                    CREATE INDEX ix_indexed_files_identity
                        ON indexed_files(volume_id, file_identity)
                        WHERE volume_id <> '' AND file_identity <> '';
                    CREATE INDEX ix_indexed_files_availability_path
                        ON indexed_files(availability_state, path_key);
                    CREATE INDEX ix_indexed_files_size
                        ON indexed_files(size_bytes);
                    CREATE INDEX ix_scan_runs_location_status
                        ON scan_runs(location_id, status, started_utc_ticks DESC);
                    """),
                new LibraryCatalogMigration(
                    2,
                    "Overlapping root membership",
                    """
                    CREATE TABLE file_location_memberships (
                        location_id INTEGER NOT NULL REFERENCES library_locations(id) ON DELETE CASCADE,
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        relative_path TEXT NOT NULL,
                        relative_path_key TEXT NOT NULL,
                        last_seen_generation INTEGER NOT NULL CHECK (last_seen_generation > 0),
                        availability_state INTEGER NOT NULL DEFAULT 0 CHECK (availability_state BETWEEN 0 AND 2),
                        last_seen_utc_ticks INTEGER NOT NULL,
                        PRIMARY KEY (location_id, file_id),
                        UNIQUE (location_id, relative_path_key)
                    ) STRICT;

                    CREATE INDEX ix_file_memberships_file
                        ON file_location_memberships(file_id, location_id);
                    CREATE INDEX ix_file_memberships_reconciliation
                        ON file_location_memberships(location_id, last_seen_generation, availability_state);
                    """),
                new LibraryCatalogMigration(
                    3,
                    "Versioned media metadata and interruption recovery",
                    """
                    ALTER TABLE scan_runs RENAME TO scan_runs_v2;

                    CREATE TABLE scan_runs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        location_id INTEGER NOT NULL REFERENCES library_locations(id) ON DELETE CASCADE,
                        generation INTEGER NOT NULL CHECK (generation > 0),
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        started_utc_ticks INTEGER NOT NULL,
                        completed_utc_ticks INTEGER NULL,
                        discovered_files INTEGER NOT NULL DEFAULT 0 CHECK (discovered_files >= 0),
                        unchanged_files INTEGER NOT NULL DEFAULT 0 CHECK (unchanged_files >= 0),
                        new_files INTEGER NOT NULL DEFAULT 0 CHECK (new_files >= 0),
                        changed_files INTEGER NOT NULL DEFAULT 0 CHECK (changed_files >= 0),
                        missing_files INTEGER NOT NULL DEFAULT 0 CHECK (missing_files >= 0),
                        error_count INTEGER NOT NULL DEFAULT 0 CHECK (error_count >= 0),
                        error_text TEXT NOT NULL DEFAULT '',
                        UNIQUE (location_id, generation)
                    ) STRICT;

                    INSERT INTO scan_runs (
                        id, location_id, generation, status, started_utc_ticks,
                        completed_utc_ticks, discovered_files, unchanged_files,
                        new_files, changed_files, missing_files, error_count, error_text)
                    SELECT id, location_id, generation, status, started_utc_ticks,
                           completed_utc_ticks, discovered_files, unchanged_files,
                           new_files, changed_files, missing_files, error_count, error_text
                    FROM scan_runs_v2;

                    DROP TABLE scan_runs_v2;

                    CREATE INDEX ix_scan_runs_location_status
                        ON scan_runs(location_id, status, started_utc_ticks DESC);

                    CREATE TABLE media_metadata (
                        file_id INTEGER PRIMARY KEY REFERENCES indexed_files(id) ON DELETE CASCADE,
                        metadata_version INTEGER NOT NULL CHECK (metadata_version > 0),
                        probe_tool_version TEXT NOT NULL,
                        probe_status INTEGER NOT NULL CHECK (probe_status BETWEEN 0 AND 3),
                        attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                        next_retry_utc_ticks INTEGER NULL,
                        last_attempt_utc_ticks INTEGER NULL,
                        last_success_utc_ticks INTEGER NULL,
                        source_size_bytes INTEGER NOT NULL CHECK (source_size_bytes >= 0),
                        source_last_write_utc_ticks INTEGER NOT NULL,
                        format_name TEXT NOT NULL DEFAULT '',
                        duration_seconds REAL NULL,
                        total_bitrate INTEGER NULL,
                        video_codec TEXT NOT NULL DEFAULT '',
                        video_profile TEXT NOT NULL DEFAULT '',
                        video_level INTEGER NULL,
                        width INTEGER NULL,
                        height INTEGER NULL,
                        frame_rate REAL NULL,
                        pixel_format TEXT NOT NULL DEFAULT '',
                        bit_depth INTEGER NULL,
                        field_order TEXT NOT NULL DEFAULT '',
                        color_range TEXT NOT NULL DEFAULT '',
                        color_space TEXT NOT NULL DEFAULT '',
                        color_transfer TEXT NOT NULL DEFAULT '',
                        color_primaries TEXT NOT NULL DEFAULT '',
                        audio_streams_json TEXT NOT NULL DEFAULT '[]',
                        subtitle_streams_json TEXT NOT NULL DEFAULT '[]',
                        chapter_count INTEGER NOT NULL DEFAULT 0 CHECK (chapter_count >= 0),
                        attachment_count INTEGER NOT NULL DEFAULT 0 CHECK (attachment_count >= 0),
                        error_message TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE INDEX ix_media_metadata_work
                        ON media_metadata(probe_status, next_retry_utc_ticks, metadata_version);
                    CREATE INDEX ix_media_metadata_video_codec
                        ON media_metadata(video_codec);
                    """)
            };

        public static void Apply(
            SqliteConnection connection,
            int currentVersion,
            int targetVersion,
            int applicationId)
        {
            if (currentVersion < 0 || targetVersion < currentVersion || targetVersion > CurrentVersion)
                throw new ArgumentOutOfRangeException(nameof(targetVersion));

            foreach (LibraryCatalogMigration migration in All.Where(item =>
                         item.Version > currentVersion && item.Version <= targetVersion))
            {
                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();

                command.CommandText = $"PRAGMA application_id = {applicationId}; PRAGMA user_version = {migration.Version};";
                command.ExecuteNonQuery();
                transaction.Commit();
            }
        }
    }
}
