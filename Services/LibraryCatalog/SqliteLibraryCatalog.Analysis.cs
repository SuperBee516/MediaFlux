using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    public sealed partial class SqliteLibraryCatalog
    {
        private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

        public LibraryInventoryBatchResult UpsertInventoryBatchDetailed(
            LibraryScanHandle scan,
            IReadOnlyCollection<LibraryInventoryEntry> entries,
            int currentMetadataVersion)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ArgumentNullException.ThrowIfNull(entries);
            if (currentMetadataVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(currentMetadataVersion));
            ThrowIfDisposed();
            if (entries.Count == 0)
                return new LibraryInventoryBatchResult(0, 0, 0, 0, Array.Empty<LibraryInventoryMutation>());

            return WithWriteTransaction((connection, transaction) =>
            {
                EnsureRunningScan(connection, transaction, scan);
                using SqliteCommand lookupCommand = CreateInventoryLookupCommand(connection, transaction);
                using SqliteCommand fileCommand = CreateFileUpsertCommand(connection, transaction);
                using SqliteCommand membershipCommand = CreateMembershipUpsertCommand(connection, transaction);
                var mutations = new List<LibraryInventoryMutation>(entries.Count);
                int newFiles = 0;
                int changedFiles = 0;
                int unchangedFiles = 0;

                foreach (LibraryInventoryEntry entry in entries)
                {
                    ArgumentNullException.ThrowIfNull(entry);
                    if (entry.SizeBytes < 0)
                        throw new ArgumentOutOfRangeException(nameof(entries), "Catalog file sizes cannot be negative.");

                    (string fullPath, string pathKey) = LibraryCatalogPathNormalizer.NormalizeFullPath(entry.FullPath);
                    (string relativePath, string relativePathKey) = LibraryCatalogPathNormalizer.NormalizeRelativePath(entry.RelativePath);
                    long lastWriteTicks = ToUtcTicks(entry.LastWriteTimeUtc);
                    long seenTicks = ToUtcTicks(entry.SeenUtc ?? DateTime.UtcNow);

                    lookupCommand.Parameters["$path_key"].Value = pathKey;
                    lookupCommand.Parameters["$metadata_version"].Value = currentMetadataVersion;
                    long existingId = 0;
                    bool factsChanged = false;
                    bool metadataFresh = false;
                    using (SqliteDataReader reader = lookupCommand.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            existingId = reader.GetInt64(0);
                            factsChanged = reader.GetInt64(1) != entry.SizeBytes ||
                                           reader.GetInt64(2) != lastWriteTicks ||
                                           !string.Equals(reader.GetString(3), entry.VolumeId ?? "", StringComparison.Ordinal) ||
                                           !string.Equals(reader.GetString(4), entry.FileIdentity ?? "", StringComparison.Ordinal);
                            metadataFresh = !factsChanged && reader.GetInt32(5) != 0;
                        }
                    }

                    LibraryInventoryChangeKind changeKind;
                    if (existingId == 0)
                    {
                        changeKind = LibraryInventoryChangeKind.New;
                        newFiles++;
                    }
                    else if (factsChanged)
                    {
                        changeKind = LibraryInventoryChangeKind.Changed;
                        changedFiles++;
                    }
                    else
                    {
                        changeKind = LibraryInventoryChangeKind.Unchanged;
                        unchangedFiles++;
                    }

                    SetFileUpsertParameters(fileCommand, entry, fullPath, pathKey, seenTicks);
                    long fileId = Convert.ToInt64(fileCommand.ExecuteScalar());
                    if (factsChanged)
                    {
                        using SqliteCommand invalidate = connection.CreateCommand();
                        invalidate.Transaction = transaction;
                        invalidate.CommandText =
                            """
                            DELETE FROM exact_duplicate_groups
                            WHERE id IN (SELECT group_id FROM exact_duplicate_members WHERE file_id = $file_id);
                            DELETE FROM file_hash_facts WHERE file_id = $file_id;
                            DELETE FROM visual_similarity_groups
                            WHERE left_file_id = $file_id OR right_file_id = $file_id;
                            DELETE FROM visual_fingerprints WHERE file_id = $file_id;
                            """;
                        invalidate.Parameters.AddWithValue("$file_id", fileId);
                        invalidate.ExecuteNonQuery();
                    }
                    membershipCommand.Parameters["$location_id"].Value = scan.LocationId;
                    membershipCommand.Parameters["$file_id"].Value = fileId;
                    membershipCommand.Parameters["$relative_path"].Value = relativePath;
                    membershipCommand.Parameters["$relative_path_key"].Value = relativePathKey;
                    membershipCommand.Parameters["$generation"].Value = scan.Generation;
                    membershipCommand.Parameters["$availability"].Value = (int)entry.Availability;
                    membershipCommand.Parameters["$last_seen"].Value = seenTicks;
                    membershipCommand.ExecuteNonQuery();
                    UpsertPresenceObservationCore(connection, transaction, scan.LocationId, fileId,
                        LibraryPresenceObservationState.Present, "authoritative-scan", "File observed during scan.");

                    if (existingId == 0 && !string.IsNullOrWhiteSpace(entry.VolumeId) && !string.IsNullOrWhiteSpace(entry.FileIdentity))
                        RecordPossibleMoveCore(connection, transaction, scan, fileId, entry.VolumeId, entry.FileIdentity);

                    mutations.Add(new LibraryInventoryMutation(
                        fileId,
                        fullPath,
                        entry.VolumeId ?? "",
                        entry.SizeBytes,
                        lastWriteTicks,
                        changeKind,
                        !metadataFresh));
                }

                return new LibraryInventoryBatchResult(
                    mutations.Count,
                    newFiles,
                    changedFiles,
                    unchangedFiles,
                    mutations);
            });
        }

        public LibraryReconciliationResult ReconcileCompletedScan(LibraryScanHandle scan)
        {
            ArgumentNullException.ThrowIfNull(scan);
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                EnsureRunningScan(connection, transaction, scan);
                using SqliteCommand membershipCommand = connection.CreateCommand();
                membershipCommand.Transaction = transaction;
                membershipCommand.CommandText =
                    """
                    UPDATE file_location_memberships
                    SET availability_state = $missing
                    WHERE location_id = $location_id
                      AND last_seen_generation < $generation
                      AND availability_state <> $missing;
                    """;
                membershipCommand.Parameters.AddWithValue("$missing", (int)IndexedFileAvailability.Missing);
                membershipCommand.Parameters.AddWithValue("$location_id", scan.LocationId);
                membershipCommand.Parameters.AddWithValue("$generation", scan.Generation);
                long missingMemberships = membershipCommand.ExecuteNonQuery();

                using (SqliteCommand observations = connection.CreateCommand())
                {
                    observations.Transaction = transaction;
                    observations.CommandText =
                        "INSERT INTO library_presence_observations(location_id,file_id,state,consecutive_observations,related_file_id,source,details,last_observed_utc_ticks) " +
                        "SELECT m.location_id,m.file_id,CASE WHEN o.state=$moved AND o.related_file_id IS NOT NULL THEN $moved ELSE $confirmed END," +
                        "CASE WHEN o.state IN($moved,$confirmed) THEN o.consecutive_observations+1 ELSE 1 END,o.related_file_id,'authoritative-scan'," +
                        "CASE WHEN o.state=$moved AND o.related_file_id IS NOT NULL THEN 'Stable identity confirmed at a new path; the old path is no longer present.' ELSE 'File was absent from a completed authoritative scan.' END,$now " +
                        "FROM file_location_memberships m LEFT JOIN library_presence_observations o ON o.location_id=m.location_id AND o.file_id=m.file_id " +
                        "WHERE m.location_id=$location AND m.last_seen_generation<$generation " +
                        "ON CONFLICT(location_id,file_id) DO UPDATE SET state=excluded.state,consecutive_observations=excluded.consecutive_observations," +
                        "related_file_id=excluded.related_file_id,source=excluded.source,details=excluded.details,last_observed_utc_ticks=excluded.last_observed_utc_ticks;";
                    observations.Parameters.AddWithValue("$moved", (int)LibraryPresenceObservationState.MovedOrRenamed);
                    observations.Parameters.AddWithValue("$confirmed", (int)LibraryPresenceObservationState.ConfirmedMissing);
                    observations.Parameters.AddWithValue("$location", scan.LocationId);
                    observations.Parameters.AddWithValue("$generation", scan.Generation);
                    observations.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    observations.ExecuteNonQuery();
                }

                using SqliteCommand fileCommand = connection.CreateCommand();
                fileCommand.Transaction = transaction;
                fileCommand.CommandText =
                    """
                    UPDATE indexed_files
                    SET availability_state = CASE
                            WHEN EXISTS (
                                SELECT 1 FROM file_location_memberships present
                                WHERE present.file_id = indexed_files.id
                                  AND present.availability_state = $present
                            ) THEN $present
                            WHEN EXISTS (
                                SELECT 1 FROM file_location_memberships unavailable
                                WHERE unavailable.file_id = indexed_files.id
                                  AND unavailable.availability_state = $unavailable
                            ) THEN $unavailable
                            ELSE $missing
                        END,
                        updated_utc_ticks = $now
                    WHERE id IN (
                        SELECT file_id FROM file_location_memberships WHERE location_id = $location_id
                    )
                      AND availability_state <> CASE
                            WHEN EXISTS (
                                SELECT 1 FROM file_location_memberships present
                                WHERE present.file_id = indexed_files.id
                                  AND present.availability_state = $present
                            ) THEN $present
                            WHEN EXISTS (
                                SELECT 1 FROM file_location_memberships unavailable
                                WHERE unavailable.file_id = indexed_files.id
                                  AND unavailable.availability_state = $unavailable
                            ) THEN $unavailable
                            ELSE $missing
                        END;
                    """;
                fileCommand.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                fileCommand.Parameters.AddWithValue("$missing", (int)IndexedFileAvailability.Missing);
                fileCommand.Parameters.AddWithValue("$unavailable", (int)IndexedFileAvailability.Unavailable);
                fileCommand.Parameters.AddWithValue("$location_id", scan.LocationId);
                fileCommand.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                fileCommand.ExecuteNonQuery();
                RefreshVisualLifecycleCore(connection, transaction, null, preserveRetired: true);

                using SqliteCommand countCommand = connection.CreateCommand();
                countCommand.Transaction = transaction;
                countCommand.CommandText =
                    """
                    SELECT COUNT(*) FROM indexed_files
                    WHERE availability_state = $missing
                      AND id IN (
                          SELECT file_id FROM file_location_memberships WHERE location_id = $location_id
                      );
                    """;
                countCommand.Parameters.AddWithValue("$missing", (int)IndexedFileAvailability.Missing);
                countCommand.Parameters.AddWithValue("$location_id", scan.LocationId);
                return new LibraryReconciliationResult(
                    missingMemberships,
                    Convert.ToInt64(countCommand.ExecuteScalar()));
            });
        }

        public void SetLocationAvailability(
            long locationId,
            LibraryLocationAvailability availability,
            string error = "",
            bool markMembershipsUnavailable = false)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand locationCommand = connection.CreateCommand();
                locationCommand.Transaction = transaction;
                locationCommand.CommandText =
                    """
                    UPDATE library_locations
                    SET availability_state = $availability, last_error = $error, updated_utc_ticks = $now
                    WHERE id = $location_id;
                    """;
                locationCommand.Parameters.AddWithValue("$availability", (int)availability);
                locationCommand.Parameters.AddWithValue("$error", error ?? "");
                locationCommand.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                locationCommand.Parameters.AddWithValue("$location_id", locationId);
                if (locationCommand.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException($"Library location {locationId} does not exist.");

                if (markMembershipsUnavailable)
                {
                    using SqliteCommand memberships = connection.CreateCommand();
                    memberships.Transaction = transaction;
                    memberships.CommandText =
                        """
                        UPDATE file_location_memberships
                        SET availability_state = $unavailable
                        WHERE location_id = $location_id;

                        UPDATE indexed_files
                        SET availability_state = CASE
                                WHEN EXISTS (
                                    SELECT 1 FROM file_location_memberships present
                                    WHERE present.file_id = indexed_files.id
                                      AND present.availability_state = $present
                                ) THEN $present
                                ELSE $unavailable
                            END,
                            updated_utc_ticks = $now
                        WHERE id IN (
                            SELECT file_id FROM file_location_memberships WHERE location_id = $location_id
                        );
                        """;
                    memberships.Parameters.AddWithValue("$unavailable", (int)IndexedFileAvailability.Unavailable);
                    memberships.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                    memberships.Parameters.AddWithValue("$location_id", locationId);
                    memberships.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    memberships.ExecuteNonQuery();
                    using SqliteCommand observations = connection.CreateCommand();
                    observations.Transaction = transaction;
                    observations.CommandText =
                        "INSERT INTO library_presence_observations(location_id,file_id,state,consecutive_observations,source,details,last_observed_utc_ticks) " +
                        "SELECT location_id,file_id,$state,1,'location-check',$details,$now FROM file_location_memberships WHERE location_id=$location " +
                        "ON CONFLICT(location_id,file_id) DO UPDATE SET state=excluded.state," +
                        "consecutive_observations=CASE WHEN library_presence_observations.state=excluded.state THEN library_presence_observations.consecutive_observations+1 ELSE 1 END," +
                        "related_file_id=NULL,source=excluded.source,details=excluded.details,last_observed_utc_ticks=excluded.last_observed_utc_ticks;";
                    observations.Parameters.AddWithValue("$state", (int)LibraryPresenceObservationState.Unavailable);
                    observations.Parameters.AddWithValue("$details", error ?? "The library location is unavailable.");
                    observations.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                    observations.Parameters.AddWithValue("$location", locationId);
                    observations.ExecuteNonQuery();
                    RefreshVisualLifecycleCore(connection, transaction, null, preserveRetired: true);
                }

                return null;
            });
        }

        public IReadOnlyList<LibraryLocationRecord> GetLocations(bool includeDisabled = true)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, path, path_key, include_subfolders, is_enabled,
                       availability_state, last_error, current_generation,
                       created_utc_ticks, updated_utc_ticks, last_completed_scan_utc_ticks
                FROM library_locations
                WHERE $include_disabled = 1 OR is_enabled = 1
                ORDER BY path_key;
                """;
            command.Parameters.AddWithValue("$include_disabled", includeDisabled ? 1 : 0);
            using SqliteDataReader reader = command.ExecuteReader();
            var locations = new List<LibraryLocationRecord>();
            while (reader.Read())
                locations.Add(ReadLocationRecord(reader));
            return locations;
        }

        public IReadOnlyDictionary<long, long> GetLocationFileCounts()
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT l.id,COUNT(m.file_id) FROM library_locations l LEFT JOIN file_location_memberships m ON m.location_id=l.id GROUP BY l.id;";
            using SqliteDataReader reader = command.ExecuteReader();
            var result = new Dictionary<long, long>();
            while (reader.Read()) result[reader.GetInt64(0)] = reader.GetInt64(1);
            return result;
        }

        public void RemoveLocation(long locationId, bool removeOrphanedFiles)
        {
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM library_locations WHERE id = $location_id;";
                command.Parameters.AddWithValue("$location_id", locationId);
                if (command.ExecuteNonQuery() != 1)
                    throw new KeyNotFoundException($"Library location {locationId} does not exist.");

                if (removeOrphanedFiles)
                {
                    command.CommandText =
                        """
                        DELETE FROM indexed_files
                        WHERE NOT EXISTS (
                            SELECT 1 FROM file_location_memberships membership
                            WHERE membership.file_id = indexed_files.id
                        );
                        """;
                    command.Parameters.Clear();
                    command.ExecuteNonQuery();
                }
                return null;
            });
        }

        public int RecoverInterruptedWork()
        {
            ThrowIfDisposed();
            return WithWriteTransaction((connection, transaction) =>
            {
                long now = DateTime.UtcNow.Ticks;
                using (SqliteCommand locations = connection.CreateCommand())
                {
                    locations.Transaction = transaction;
                    locations.CommandText =
                        """
                        UPDATE library_locations
                        SET availability_state = $unknown,
                            last_error = 'The previous scan was interrupted before completion.',
                            updated_utc_ticks = $now
                        WHERE id IN (SELECT location_id FROM scan_runs WHERE status = $running);
                        """;
                    locations.Parameters.AddWithValue("$unknown", (int)LibraryLocationAvailability.Unknown);
                    locations.Parameters.AddWithValue("$running", (int)LibraryScanStatus.Running);
                    locations.Parameters.AddWithValue("$now", now);
                    locations.ExecuteNonQuery();
                }

                using SqliteCommand scans = connection.CreateCommand();
                scans.Transaction = transaction;
                scans.CommandText =
                    """
                    UPDATE scan_runs
                    SET status = $interrupted,
                        completed_utc_ticks = $now,
                        error_count = error_count + 1,
                        error_text = CASE WHEN error_text = ''
                            THEN 'Interrupted before MediaFlux completed the scan.'
                            ELSE error_text END
                    WHERE status = $running;
                    """;
                scans.Parameters.AddWithValue("$interrupted", (int)LibraryScanStatus.Interrupted);
                scans.Parameters.AddWithValue("$running", (int)LibraryScanStatus.Running);
                scans.Parameters.AddWithValue("$now", now);
                int recovered = scans.ExecuteNonQuery();

                using SqliteCommand metadata = connection.CreateCommand();
                metadata.Transaction = transaction;
                metadata.CommandText =
                    """
                    UPDATE media_metadata
                    SET probe_status = $pending,
                        error_message = CASE WHEN error_message = ''
                            THEN 'Metadata enrichment was interrupted and will be retried.'
                            ELSE error_message END,
                        updated_utc_ticks = $now
                    WHERE probe_status = $in_progress;
                    """;
                metadata.Parameters.AddWithValue("$pending", (int)LibraryProbeStatus.Pending);
                metadata.Parameters.AddWithValue("$in_progress", (int)LibraryProbeStatus.InProgress);
                metadata.Parameters.AddWithValue("$now", now);
                metadata.ExecuteNonQuery();
                return recovered;
            });
        }

        public IReadOnlyList<LibraryEnrichmentCandidate> ClaimEnrichmentBatch(
            int limit,
            int metadataVersion,
            string probeToolVersion,
            DateTime utcNow)
        {
            ThrowIfDisposed();
            limit = Math.Clamp(limit, 1, MaximumPageSize);
            long nowTicks = ToUtcTicks(utcNow);
            return WithWriteTransaction((connection, transaction) =>
            {
                using SqliteCommand select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT file.id, file.full_path, file.volume_id, file.size_bytes,
                           file.last_write_utc_ticks,
                           CASE WHEN metadata.file_id IS NULL
                                     OR metadata.metadata_version <> $metadata_version
                                     OR metadata.probe_tool_version <> $tool_version
                                     OR metadata.source_size_bytes <> file.size_bytes
                                     OR metadata.source_last_write_utc_ticks <> file.last_write_utc_ticks
                                THEN 1 ELSE metadata.attempt_count + 1 END
                    FROM indexed_files file
                    LEFT JOIN media_metadata metadata ON metadata.file_id = file.id
                    WHERE file.availability_state = $present
                      AND (
                          metadata.file_id IS NULL
                          OR metadata.metadata_version <> $metadata_version
                          OR metadata.probe_tool_version <> $tool_version
                          OR metadata.source_size_bytes <> file.size_bytes
                          OR metadata.source_last_write_utc_ticks <> file.last_write_utc_ticks
                          OR metadata.probe_status = $pending
                          OR (metadata.probe_status = $failed
                              AND metadata.next_retry_utc_ticks IS NOT NULL
                              AND metadata.next_retry_utc_ticks <= $now)
                      )
                      AND (metadata.probe_status IS NULL OR metadata.probe_status <> $in_progress)
                    ORDER BY file.id
                    LIMIT $limit;
                    """;
                select.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
                select.Parameters.AddWithValue("$metadata_version", metadataVersion);
                select.Parameters.AddWithValue("$tool_version", probeToolVersion ?? "");
                select.Parameters.AddWithValue("$pending", (int)LibraryProbeStatus.Pending);
                select.Parameters.AddWithValue("$failed", (int)LibraryProbeStatus.Failed);
                select.Parameters.AddWithValue("$in_progress", (int)LibraryProbeStatus.InProgress);
                select.Parameters.AddWithValue("$now", nowTicks);
                select.Parameters.AddWithValue("$limit", limit);
                var candidates = new List<LibraryEnrichmentCandidate>();
                using (SqliteDataReader reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        candidates.Add(new LibraryEnrichmentCandidate(
                            reader.GetInt64(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetInt64(3),
                            FromUtcTicks(reader.GetInt64(4)),
                            reader.GetInt32(5)));
                    }
                }

                using SqliteCommand claim = connection.CreateCommand();
                claim.Transaction = transaction;
                claim.CommandText =
                    """
                    INSERT INTO media_metadata (
                        file_id, metadata_version, probe_tool_version, probe_status,
                        attempt_count, next_retry_utc_ticks, last_attempt_utc_ticks,
                        source_size_bytes, source_last_write_utc_ticks, updated_utc_ticks)
                    VALUES ($file_id, $metadata_version, $tool_version, $in_progress,
                            $attempt_count, NULL, $now, $size, $last_write, $now)
                    ON CONFLICT(file_id) DO UPDATE SET
                        metadata_version = excluded.metadata_version,
                        probe_tool_version = excluded.probe_tool_version,
                        probe_status = excluded.probe_status,
                        attempt_count = excluded.attempt_count,
                        next_retry_utc_ticks = NULL,
                        last_attempt_utc_ticks = excluded.last_attempt_utc_ticks,
                        source_size_bytes = excluded.source_size_bytes,
                        source_last_write_utc_ticks = excluded.source_last_write_utc_ticks,
                        updated_utc_ticks = excluded.updated_utc_ticks;
                    """;
                claim.Parameters.Add("$file_id", SqliteType.Integer);
                claim.Parameters.AddWithValue("$metadata_version", metadataVersion);
                claim.Parameters.AddWithValue("$tool_version", probeToolVersion ?? "");
                claim.Parameters.AddWithValue("$in_progress", (int)LibraryProbeStatus.InProgress);
                claim.Parameters.Add("$attempt_count", SqliteType.Integer);
                claim.Parameters.AddWithValue("$now", nowTicks);
                claim.Parameters.Add("$size", SqliteType.Integer);
                claim.Parameters.Add("$last_write", SqliteType.Integer);
                claim.Prepare();
                foreach (LibraryEnrichmentCandidate candidate in candidates)
                {
                    claim.Parameters["$file_id"].Value = candidate.FileId;
                    claim.Parameters["$attempt_count"].Value = candidate.AttemptCount;
                    claim.Parameters["$size"].Value = candidate.SizeBytes;
                    claim.Parameters["$last_write"].Value = candidate.LastWriteUtc.Ticks;
                    claim.ExecuteNonQuery();
                }
                return candidates;
            });
        }

        public void SaveMediaMetadata(LibraryMediaMetadata metadata)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            ThrowIfDisposed();
            WithWriteTransaction<object?>((connection, transaction) =>
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO media_metadata (
                        file_id, metadata_version, probe_tool_version, probe_status,
                        attempt_count, next_retry_utc_ticks, last_attempt_utc_ticks,
                        last_success_utc_ticks, source_size_bytes, source_last_write_utc_ticks,
                        format_name, duration_seconds, total_bitrate, video_codec,
                        video_profile, video_level, width, height, frame_rate, pixel_format,
                        bit_depth, field_order, color_range, color_space, color_transfer,
                        color_primaries, audio_streams_json, subtitle_streams_json,
                        chapter_count, attachment_count, error_message, updated_utc_ticks)
                    VALUES (
                        $file_id, $metadata_version, $tool_version, $status,
                        $attempt_count, $next_retry, $last_attempt, $last_success,
                        $source_size, $source_last_write, $format, $duration, $bitrate,
                        $video_codec, $profile, $level, $width, $height, $frame_rate,
                        $pixel_format, $bit_depth, $field_order, $color_range, $color_space,
                        $color_transfer, $color_primaries, $audio, $subtitles,
                        $chapters, $attachments, $error, $updated)
                    ON CONFLICT(file_id) DO UPDATE SET
                        metadata_version=excluded.metadata_version,
                        probe_tool_version=excluded.probe_tool_version,
                        probe_status=excluded.probe_status,
                        attempt_count=excluded.attempt_count,
                        next_retry_utc_ticks=excluded.next_retry_utc_ticks,
                        last_attempt_utc_ticks=excluded.last_attempt_utc_ticks,
                        last_success_utc_ticks=excluded.last_success_utc_ticks,
                        source_size_bytes=excluded.source_size_bytes,
                        source_last_write_utc_ticks=excluded.source_last_write_utc_ticks,
                        format_name=excluded.format_name,
                        duration_seconds=excluded.duration_seconds,
                        total_bitrate=excluded.total_bitrate,
                        video_codec=excluded.video_codec,
                        video_profile=excluded.video_profile,
                        video_level=excluded.video_level,
                        width=excluded.width,
                        height=excluded.height,
                        frame_rate=excluded.frame_rate,
                        pixel_format=excluded.pixel_format,
                        bit_depth=excluded.bit_depth,
                        field_order=excluded.field_order,
                        color_range=excluded.color_range,
                        color_space=excluded.color_space,
                        color_transfer=excluded.color_transfer,
                        color_primaries=excluded.color_primaries,
                        audio_streams_json=excluded.audio_streams_json,
                        subtitle_streams_json=excluded.subtitle_streams_json,
                        chapter_count=excluded.chapter_count,
                        attachment_count=excluded.attachment_count,
                        error_message=excluded.error_message,
                        updated_utc_ticks=excluded.updated_utc_ticks;
                    """;
                AddMetadataParameters(command, metadata);
                command.ExecuteNonQuery();
                return null;
            });
        }

        public LibraryMediaMetadata? GetMediaMetadata(long fileId)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = MetadataSelectSql + " WHERE file_id = $file_id;";
            command.Parameters.AddWithValue("$file_id", fileId);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read() ? ReadMetadata(reader) : null;
        }

        public LibraryOverview GetOverview(int metadataVersion)
        {
            ThrowIfDisposed();
            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    (SELECT COUNT(*) FROM indexed_files),
                    (SELECT COALESCE(SUM(size_bytes), 0) FROM indexed_files),
                    (SELECT COUNT(*) FROM indexed_files file
                     LEFT JOIN media_metadata metadata ON metadata.file_id = file.id
                     WHERE file.availability_state = $present AND (
                         metadata.file_id IS NULL OR metadata.metadata_version <> $metadata_version
                         OR metadata.source_size_bytes <> file.size_bytes
                         OR metadata.source_last_write_utc_ticks <> file.last_write_utc_ticks
                         OR metadata.probe_status IN ($pending, $in_progress))),
                    (SELECT COUNT(*) FROM library_locations WHERE availability_state IN ($unavailable, $error)),
                    (SELECT MAX(last_completed_scan_utc_ticks) FROM library_locations),
                    (SELECT COUNT(*) FROM scan_runs WHERE status = $running);
                """;
            command.Parameters.AddWithValue("$present", (int)IndexedFileAvailability.Present);
            command.Parameters.AddWithValue("$metadata_version", metadataVersion);
            command.Parameters.AddWithValue("$pending", (int)LibraryProbeStatus.Pending);
            command.Parameters.AddWithValue("$in_progress", (int)LibraryProbeStatus.InProgress);
            command.Parameters.AddWithValue("$unavailable", (int)LibraryLocationAvailability.Unavailable);
            command.Parameters.AddWithValue("$error", (int)LibraryLocationAvailability.Error);
            command.Parameters.AddWithValue("$running", (int)LibraryScanStatus.Running);
            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();
            return new LibraryOverview(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : FromUtcTicks(reader.GetInt64(4)),
                reader.GetInt64(5));
        }

        public LibraryFilePage QueryFiles(LibraryFileQuery query)
        {
            ArgumentNullException.ThrowIfNull(query);
            ThrowIfDisposed();
            int limit = Math.Clamp(query.Limit, 1, 1_000);
            int offset = Math.Max(0, query.Offset);
            string orderColumn = query.SortColumn.ToLowerInvariant() switch
            {
                "name" => "file.file_name",
                "size" => "file.size_bytes",
                "modified" => "file.last_write_utc_ticks",
                "codec" => "metadata.video_codec",
                "duration" => "metadata.duration_seconds",
                "bitrate" => "metadata.total_bitrate",
                _ => "file.path_key"
            };
            string direction = query.Descending ? "DESC" : "ASC";
            string search = (query.Search ?? "").Trim();

            using SqliteConnection connection = _database.OpenConnection(readOnly: true);
            string filters =
                " WHERE ($search = '' OR file.file_name LIKE $search_pattern OR file.full_path LIKE $search_pattern)" +
                " AND ($location_id IS NULL OR EXISTS (SELECT 1 FROM file_location_memberships selected_membership WHERE selected_membership.file_id = file.id AND selected_membership.location_id = $location_id))" +
                " AND ($availability IS NULL OR file.availability_state = $availability)" +
                " AND ($probe_status IS NULL OR COALESCE(metadata.probe_status, $pending) = $probe_status)";

            using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.CommandText =
                "SELECT COUNT(*) FROM indexed_files file LEFT JOIN media_metadata metadata ON metadata.file_id = file.id" + filters + ";";
            AddFileQueryParameters(countCommand, query, search);
            long total = Convert.ToInt64(countCommand.ExecuteScalar());

            using SqliteCommand pageCommand = connection.CreateCommand();
            pageCommand.CommandText =
                """
                SELECT file.id, file.file_name, file.full_path,
                       COALESCE((SELECT MIN(location.path) FROM file_location_memberships membership
                                 JOIN library_locations location ON location.id = membership.location_id
                                 WHERE membership.file_id = file.id), ''),
                       file.size_bytes, file.last_write_utc_ticks, file.availability_state,
                       COALESCE(metadata.format_name, ''), COALESCE(metadata.video_codec, ''),
                       metadata.width, metadata.height, metadata.total_bitrate,
                       metadata.duration_seconds, COALESCE(metadata.probe_status, $pending),
                       COALESCE(metadata.error_message, ''),
                       EXISTS(SELECT 1 FROM duplicate_file_protections protection WHERE protection.path_key=file.path_key)
                FROM indexed_files file
                LEFT JOIN media_metadata metadata ON metadata.file_id = file.id
                """ + filters + $" ORDER BY {orderColumn} {direction}, file.id {direction} LIMIT $limit OFFSET $offset;";
            AddFileQueryParameters(pageCommand, query, search);
            pageCommand.Parameters.AddWithValue("$limit", limit);
            pageCommand.Parameters.AddWithValue("$offset", offset);
            using SqliteDataReader reader = pageCommand.ExecuteReader();
            var files = new List<LibraryFileViewRecord>(limit);
            while (reader.Read())
            {
                files.Add(new LibraryFileViewRecord(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), FromUtcTicks(reader.GetInt64(5)),
                    (IndexedFileAvailability)reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11),
                    reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    (LibraryProbeStatus)reader.GetInt32(13), reader.GetString(14), reader.GetBoolean(15)));
            }
            return new LibraryFilePage(total, files);
        }

        private static SqliteCommand CreateInventoryLookupCommand(SqliteConnection connection, SqliteTransaction transaction)
        {
            SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT file.id, file.size_bytes, file.last_write_utc_ticks,
                       file.volume_id, file.file_identity,
                       CASE WHEN metadata.file_id IS NOT NULL
                                  AND metadata.metadata_version = $metadata_version
                                  AND metadata.probe_status = 2
                                  AND metadata.source_size_bytes = file.size_bytes
                                  AND metadata.source_last_write_utc_ticks = file.last_write_utc_ticks
                            THEN 1 ELSE 0 END
                FROM indexed_files file
                LEFT JOIN media_metadata metadata ON metadata.file_id = file.id
                WHERE file.path_key = $path_key;
                """;
            command.Parameters.Add("$path_key", SqliteType.Text);
            command.Parameters.Add("$metadata_version", SqliteType.Integer);
            command.Prepare();
            return command;
        }

        private static LibraryLocationRecord ReadLocationRecord(SqliteDataReader reader) => new(
            reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3) != 0, reader.GetInt32(4) != 0,
            (LibraryLocationAvailability)reader.GetInt32(5), reader.GetString(6), reader.GetInt64(7),
            FromUtcTicks(reader.GetInt64(8)), FromUtcTicks(reader.GetInt64(9)),
            reader.IsDBNull(10) ? null : FromUtcTicks(reader.GetInt64(10)));

        private static void AddMetadataParameters(SqliteCommand command, LibraryMediaMetadata metadata)
        {
            object Db(object? value) => value ?? DBNull.Value;
            command.Parameters.AddWithValue("$file_id", metadata.FileId);
            command.Parameters.AddWithValue("$metadata_version", metadata.MetadataVersion);
            command.Parameters.AddWithValue("$tool_version", metadata.ProbeToolVersion ?? "");
            command.Parameters.AddWithValue("$status", (int)metadata.ProbeStatus);
            command.Parameters.AddWithValue("$attempt_count", metadata.AttemptCount);
            command.Parameters.AddWithValue("$next_retry", Db(metadata.NextRetryUtc?.Ticks));
            command.Parameters.AddWithValue("$last_attempt", Db(metadata.LastAttemptUtc?.Ticks));
            command.Parameters.AddWithValue("$last_success", Db(metadata.LastSuccessUtc?.Ticks));
            command.Parameters.AddWithValue("$source_size", metadata.SourceSizeBytes);
            command.Parameters.AddWithValue("$source_last_write", metadata.SourceLastWriteUtc.Ticks);
            command.Parameters.AddWithValue("$format", metadata.FormatName ?? "");
            command.Parameters.AddWithValue("$duration", Db(metadata.DurationSeconds));
            command.Parameters.AddWithValue("$bitrate", Db(metadata.TotalBitRate));
            command.Parameters.AddWithValue("$video_codec", metadata.VideoCodec ?? "");
            command.Parameters.AddWithValue("$profile", metadata.VideoProfile ?? "");
            command.Parameters.AddWithValue("$level", Db(metadata.VideoLevel));
            command.Parameters.AddWithValue("$width", Db(metadata.Width));
            command.Parameters.AddWithValue("$height", Db(metadata.Height));
            command.Parameters.AddWithValue("$frame_rate", Db(metadata.FrameRate));
            command.Parameters.AddWithValue("$pixel_format", metadata.PixelFormat ?? "");
            command.Parameters.AddWithValue("$bit_depth", Db(metadata.BitDepth));
            command.Parameters.AddWithValue("$field_order", metadata.FieldOrder ?? "");
            command.Parameters.AddWithValue("$color_range", metadata.ColorRange ?? "");
            command.Parameters.AddWithValue("$color_space", metadata.ColorSpace ?? "");
            command.Parameters.AddWithValue("$color_transfer", metadata.ColorTransfer ?? "");
            command.Parameters.AddWithValue("$color_primaries", metadata.ColorPrimaries ?? "");
            command.Parameters.AddWithValue("$audio", JsonSerializer.Serialize(metadata.AudioStreams, MetadataJsonOptions));
            command.Parameters.AddWithValue("$subtitles", JsonSerializer.Serialize(metadata.SubtitleStreams, MetadataJsonOptions));
            command.Parameters.AddWithValue("$chapters", metadata.ChapterCount);
            command.Parameters.AddWithValue("$attachments", metadata.AttachmentCount);
            command.Parameters.AddWithValue("$error", metadata.ErrorMessage ?? "");
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.Ticks);
        }

        private const string MetadataSelectSql =
            """
            SELECT file_id, metadata_version, probe_tool_version, probe_status,
                   attempt_count, next_retry_utc_ticks, last_attempt_utc_ticks,
                   last_success_utc_ticks, source_size_bytes, source_last_write_utc_ticks,
                   format_name, duration_seconds, total_bitrate, video_codec, video_profile,
                   video_level, width, height, frame_rate, pixel_format, bit_depth, field_order,
                   color_range, color_space, color_transfer, color_primaries,
                   audio_streams_json, subtitle_streams_json, chapter_count, attachment_count,
                   error_message
            FROM media_metadata
            """;

        private static LibraryMediaMetadata ReadMetadata(SqliteDataReader reader)
        {
            T? ReadJson<T>(string json) => JsonSerializer.Deserialize<T>(json, MetadataJsonOptions);
            return new LibraryMediaMetadata(
                reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2),
                (LibraryProbeStatus)reader.GetInt32(3), reader.GetInt32(4),
                reader.IsDBNull(5) ? null : FromUtcTicks(reader.GetInt64(5)),
                reader.IsDBNull(6) ? null : FromUtcTicks(reader.GetInt64(6)),
                reader.IsDBNull(7) ? null : FromUtcTicks(reader.GetInt64(7)),
                reader.GetInt64(8), FromUtcTicks(reader.GetInt64(9)), reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetDouble(11),
                reader.IsDBNull(12) ? null : reader.GetInt64(12),
                reader.GetString(13), reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetInt32(15),
                reader.IsDBNull(16) ? null : reader.GetInt32(16),
                reader.IsDBNull(17) ? null : reader.GetInt32(17),
                reader.IsDBNull(18) ? null : reader.GetDouble(18), reader.GetString(19),
                reader.IsDBNull(20) ? null : reader.GetInt32(20), reader.GetString(21),
                reader.GetString(22), reader.GetString(23), reader.GetString(24), reader.GetString(25),
                ReadJson<List<LibraryAudioStreamMetadata>>(reader.GetString(26)) ?? new(),
                ReadJson<List<LibrarySubtitleStreamMetadata>>(reader.GetString(27)) ?? new(),
                reader.GetInt32(28), reader.GetInt32(29), reader.GetString(30));
        }

        private static void AddFileQueryParameters(SqliteCommand command, LibraryFileQuery query, string search)
        {
            command.Parameters.AddWithValue("$search", search);
            command.Parameters.AddWithValue("$search_pattern", $"%{search}%");
            command.Parameters.AddWithValue("$location_id", query.LocationId.HasValue ? query.LocationId.Value : DBNull.Value);
            command.Parameters.AddWithValue("$availability", query.Availability.HasValue ? (int)query.Availability.Value : DBNull.Value);
            command.Parameters.AddWithValue("$probe_status", query.ProbeStatus.HasValue ? (int)query.ProbeStatus.Value : DBNull.Value);
            command.Parameters.AddWithValue("$pending", (int)LibraryProbeStatus.Pending);
        }
    }
}
