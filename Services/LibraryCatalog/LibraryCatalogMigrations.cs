using Microsoft.Data.Sqlite;

namespace MediaFlux.Services.LibraryCatalog
{
    internal sealed record LibraryCatalogMigration(int Version, string Name, string Sql);

    internal static class LibraryCatalogMigrations
    {
        public const int CurrentVersion = 12;

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
                    """),
                new LibraryCatalogMigration(
                    4,
                    "Library analytics and exact duplicate evidence",
                    """
                    CREATE TABLE file_hash_facts (
                        file_id INTEGER PRIMARY KEY REFERENCES indexed_files(id) ON DELETE CASCADE,
                        source_size_bytes INTEGER NOT NULL CHECK (source_size_bytes >= 0),
                        source_last_write_utc_ticks INTEGER NOT NULL,
                        source_volume_id TEXT NOT NULL DEFAULT '',
                        source_file_identity TEXT NOT NULL DEFAULT '',
                        quick_algorithm TEXT NOT NULL DEFAULT '',
                        quick_version INTEGER NOT NULL DEFAULT 0 CHECK (quick_version >= 0),
                        quick_hash BLOB NULL,
                        quick_completed_utc_ticks INTEGER NULL,
                        full_algorithm TEXT NOT NULL DEFAULT '',
                        full_version INTEGER NOT NULL DEFAULT 0 CHECK (full_version >= 0),
                        full_hash BLOB NULL,
                        full_completed_utc_ticks INTEGER NULL,
                        failure_count INTEGER NOT NULL DEFAULT 0 CHECK (failure_count >= 0),
                        error_message TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE INDEX ix_file_hash_quick_candidates
                        ON file_hash_facts(source_size_bytes, quick_version, quick_hash);
                    CREATE INDEX ix_file_hash_full_groups
                        ON file_hash_facts(source_size_bytes, full_version, full_hash)
                        WHERE full_hash IS NOT NULL;
                    CREATE INDEX ix_indexed_files_present_size
                        ON indexed_files(size_bytes, id)
                        WHERE availability_state = 0;

                    CREATE TABLE duplicate_analysis_runs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        quick_algorithm TEXT NOT NULL,
                        quick_version INTEGER NOT NULL CHECK (quick_version > 0),
                        full_algorithm TEXT NOT NULL,
                        full_version INTEGER NOT NULL CHECK (full_version > 0),
                        started_utc_ticks INTEGER NOT NULL,
                        completed_utc_ticks INTEGER NULL,
                        size_candidates INTEGER NOT NULL DEFAULT 0 CHECK (size_candidates >= 0),
                        quick_hashed INTEGER NOT NULL DEFAULT 0 CHECK (quick_hashed >= 0),
                        full_hashed INTEGER NOT NULL DEFAULT 0 CHECK (full_hashed >= 0),
                        exact_groups INTEGER NOT NULL DEFAULT 0 CHECK (exact_groups >= 0),
                        error_count INTEGER NOT NULL DEFAULT 0 CHECK (error_count >= 0),
                        error_text TEXT NOT NULL DEFAULT ''
                    ) STRICT;

                    CREATE INDEX ix_duplicate_runs_status_started
                        ON duplicate_analysis_runs(status, started_utc_ticks DESC);

                    CREATE TABLE exact_duplicate_groups (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                        full_algorithm TEXT NOT NULL,
                        full_version INTEGER NOT NULL CHECK (full_version > 0),
                        full_hash BLOB NOT NULL,
                        member_count INTEGER NOT NULL CHECK (member_count >= 2),
                        physical_copy_count INTEGER NOT NULL CHECK (physical_copy_count >= 1),
                        reclaimable_bytes INTEGER NOT NULL CHECK (reclaimable_bytes >= 0),
                        suggested_keeper_file_id INTEGER NULL REFERENCES indexed_files(id) ON DELETE SET NULL,
                        analysis_run_id INTEGER NOT NULL REFERENCES duplicate_analysis_runs(id) ON DELETE CASCADE,
                        updated_utc_ticks INTEGER NOT NULL,
                        UNIQUE (size_bytes, full_algorithm, full_version, full_hash)
                    ) STRICT;

                    CREATE TABLE exact_duplicate_members (
                        group_id INTEGER NOT NULL REFERENCES exact_duplicate_groups(id) ON DELETE CASCADE,
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        physical_identity_key TEXT NOT NULL,
                        is_hard_link_alias INTEGER NOT NULL DEFAULT 0 CHECK (is_hard_link_alias IN (0, 1)),
                        PRIMARY KEY (group_id, file_id)
                    ) STRICT;

                    CREATE INDEX ix_duplicate_groups_reclaimable
                        ON exact_duplicate_groups(reclaimable_bytes DESC, id);
                    CREATE INDEX ix_duplicate_members_file
                        ON exact_duplicate_members(file_id, group_id);

                    -- User decisions are deliberately keyed by immutable hash evidence rather
                    -- than derived group ids, so rebuilding analysis does not erase them.
                    CREATE TABLE duplicate_group_decisions (
                        size_bytes INTEGER NOT NULL CHECK (size_bytes >= 0),
                        full_algorithm TEXT NOT NULL,
                        full_version INTEGER NOT NULL CHECK (full_version > 0),
                        full_hash BLOB NOT NULL,
                        manual_keeper_path_key TEXT NOT NULL DEFAULT '',
                        reviewed INTEGER NOT NULL DEFAULT 0 CHECK (reviewed IN (0, 1)),
                        ignored INTEGER NOT NULL DEFAULT 0 CHECK (ignored IN (0, 1)),
                        updated_utc_ticks INTEGER NOT NULL,
                        PRIMARY KEY (size_bytes, full_algorithm, full_version, full_hash)
                    ) STRICT;

                    CREATE TABLE duplicate_file_protections (
                        path_key TEXT PRIMARY KEY,
                        protected_path TEXT NOT NULL,
                        reason TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE TABLE duplicate_cleanup_plans (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        action INTEGER NOT NULL CHECK (action BETWEEN 0 AND 1),
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        quarantine_root TEXT NOT NULL DEFAULT '',
                        created_utc_ticks INTEGER NOT NULL,
                        completed_utc_ticks INTEGER NULL,
                        error_text TEXT NOT NULL DEFAULT ''
                    ) STRICT;

                    CREATE TABLE duplicate_cleanup_plan_items (
                        plan_id INTEGER NOT NULL REFERENCES duplicate_cleanup_plans(id) ON DELETE CASCADE,
                        group_id INTEGER NOT NULL,
                        file_id INTEGER NOT NULL,
                        keeper_file_id INTEGER NOT NULL,
                        source_path TEXT NOT NULL,
                        source_path_key TEXT NOT NULL,
                        source_size_bytes INTEGER NOT NULL CHECK (source_size_bytes >= 0),
                        source_last_write_utc_ticks INTEGER NOT NULL,
                        source_volume_id TEXT NOT NULL DEFAULT '',
                        source_file_identity TEXT NOT NULL DEFAULT '',
                        full_hash BLOB NOT NULL,
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        destination_path TEXT NOT NULL DEFAULT '',
                        validation_error TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY (plan_id, file_id)
                    ) STRICT;

                    CREATE TABLE duplicate_cleanup_audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        plan_id INTEGER NOT NULL REFERENCES duplicate_cleanup_plans(id) ON DELETE RESTRICT,
                        file_id INTEGER NOT NULL,
                        source_path TEXT NOT NULL,
                        destination_path TEXT NOT NULL DEFAULT '',
                        action INTEGER NOT NULL CHECK (action BETWEEN 0 AND 1),
                        outcome INTEGER NOT NULL CHECK (outcome BETWEEN 0 AND 4),
                        message TEXT NOT NULL DEFAULT '',
                        occurred_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE INDEX ix_cleanup_audit_plan
                        ON duplicate_cleanup_audit(plan_id, occurred_utc_ticks);
                    CREATE INDEX ix_media_metadata_format
                        ON media_metadata(format_name);
                    CREATE INDEX ix_media_metadata_resolution
                        ON media_metadata(width, height);
                    CREATE INDEX ix_media_metadata_probe_health
                        ON media_metadata(probe_status);
                    """),
                new LibraryCatalogMigration(
                    5,
                    "Visual similarity, scan acceleration, and durable decision recovery",
                    """
                    CREATE TABLE visual_fingerprints (
                        file_id INTEGER PRIMARY KEY REFERENCES indexed_files(id) ON DELETE CASCADE,
                        source_size_bytes INTEGER NOT NULL CHECK (source_size_bytes >= 0),
                        source_last_write_utc_ticks INTEGER NOT NULL,
                        source_volume_id TEXT NOT NULL DEFAULT '',
                        source_file_identity TEXT NOT NULL DEFAULT '',
                        algorithm TEXT NOT NULL,
                        algorithm_version INTEGER NOT NULL CHECK (algorithm_version > 0),
                        sample_count INTEGER NOT NULL CHECK (sample_count >= 0),
                        frame_hashes BLOB NULL,
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 3),
                        attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
                        tool_version TEXT NOT NULL DEFAULT '',
                        error_message TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE INDEX ix_visual_fingerprint_work
                        ON visual_fingerprints(status, algorithm_version, file_id);

                    CREATE TABLE visual_hash_bands (
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        algorithm_version INTEGER NOT NULL CHECK (algorithm_version > 0),
                        band_index INTEGER NOT NULL CHECK (band_index >= 0),
                        band_key INTEGER NOT NULL,
                        PRIMARY KEY (file_id, algorithm_version, band_index)
                    ) STRICT;

                    CREATE INDEX ix_visual_bands_candidates
                        ON visual_hash_bands(algorithm_version, band_index, band_key, file_id);

                    CREATE TABLE visual_analysis_runs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        algorithm TEXT NOT NULL,
                        algorithm_version INTEGER NOT NULL CHECK (algorithm_version > 0),
                        started_utc_ticks INTEGER NOT NULL,
                        completed_utc_ticks INTEGER NULL,
                        eligible_files INTEGER NOT NULL DEFAULT 0 CHECK (eligible_files >= 0),
                        fingerprinted_files INTEGER NOT NULL DEFAULT 0 CHECK (fingerprinted_files >= 0),
                        candidate_pairs INTEGER NOT NULL DEFAULT 0 CHECK (candidate_pairs >= 0),
                        match_pairs INTEGER NOT NULL DEFAULT 0 CHECK (match_pairs >= 0),
                        error_count INTEGER NOT NULL DEFAULT 0 CHECK (error_count >= 0),
                        error_text TEXT NOT NULL DEFAULT ''
                    ) STRICT;

                    CREATE INDEX ix_visual_runs_status_started
                        ON visual_analysis_runs(status, started_utc_ticks DESC);

                    CREATE TABLE visual_candidate_pairs (
                        run_id INTEGER NOT NULL REFERENCES visual_analysis_runs(id) ON DELETE CASCADE,
                        left_file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        right_file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        band_matches INTEGER NOT NULL CHECK (band_matches > 0),
                        PRIMARY KEY (run_id, left_file_id, right_file_id),
                        CHECK (left_file_id < right_file_id)
                    ) STRICT;

                    CREATE INDEX ix_visual_candidates_page
                        ON visual_candidate_pairs(run_id, left_file_id, right_file_id);

                    CREATE TABLE visual_similarity_groups (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        group_key TEXT NOT NULL,
                        analysis_run_id INTEGER NOT NULL REFERENCES visual_analysis_runs(id) ON DELETE CASCADE,
                        left_file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        right_file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        confidence_score REAL NOT NULL CHECK (confidence_score BETWEEN 0 AND 100),
                        frame_matches INTEGER NOT NULL CHECK (frame_matches >= 0),
                        frame_comparisons INTEGER NOT NULL CHECK (frame_comparisons > 0),
                        average_hash_distance REAL NOT NULL CHECK (average_hash_distance >= 0),
                        duration_delta_seconds REAL NOT NULL CHECK (duration_delta_seconds >= 0),
                        evidence_text TEXT NOT NULL,
                        suggested_keeper_file_id INTEGER NULL REFERENCES indexed_files(id) ON DELETE SET NULL,
                        updated_utc_ticks INTEGER NOT NULL,
                        CHECK (left_file_id < right_file_id),
                        UNIQUE (analysis_run_id, group_key)
                    ) STRICT;

                    CREATE INDEX ix_visual_groups_confidence
                        ON visual_similarity_groups(confidence_score DESC, id);
                    CREATE INDEX ix_visual_groups_run
                        ON visual_similarity_groups(analysis_run_id, id);

                    CREATE TABLE visual_group_decisions (
                        group_key TEXT PRIMARY KEY,
                        manual_keeper_path_key TEXT NOT NULL DEFAULT '',
                        reviewed INTEGER NOT NULL DEFAULT 0 CHECK (reviewed IN (0, 1)),
                        ignored INTEGER NOT NULL DEFAULT 0 CHECK (ignored IN (0, 1)),
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    CREATE TABLE location_scan_accelerators (
                        location_id INTEGER PRIMARY KEY REFERENCES library_locations(id) ON DELETE CASCADE,
                        accelerator_kind TEXT NOT NULL,
                        volume_identity TEXT NOT NULL,
                        filesystem_name TEXT NOT NULL,
                        journal_id INTEGER NOT NULL,
                        next_usn INTEGER NOT NULL,
                        lowest_valid_usn INTEGER NOT NULL,
                        last_authoritative_scan_utc_ticks INTEGER NOT NULL,
                        status_message TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    """),
                new LibraryCatalogMigration(
                    6,
                    "Visual cleanup plans and permanent Library Analyzer cleanup",
                    """
                    ALTER TABLE duplicate_cleanup_plans RENAME TO duplicate_cleanup_plans_v5;
                    ALTER TABLE duplicate_cleanup_plan_items RENAME TO duplicate_cleanup_plan_items_v5;
                    ALTER TABLE duplicate_cleanup_audit RENAME TO duplicate_cleanup_audit_v5;

                    CREATE TABLE duplicate_cleanup_plans (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        action INTEGER NOT NULL CHECK (action BETWEEN 0 AND 2),
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        quarantine_root TEXT NOT NULL DEFAULT '',
                        created_utc_ticks INTEGER NOT NULL,
                        completed_utc_ticks INTEGER NULL,
                        error_text TEXT NOT NULL DEFAULT ''
                    ) STRICT;
                    CREATE TABLE duplicate_cleanup_plan_items (
                        plan_id INTEGER NOT NULL REFERENCES duplicate_cleanup_plans(id) ON DELETE CASCADE,
                        group_id INTEGER NOT NULL,file_id INTEGER NOT NULL,keeper_file_id INTEGER NOT NULL,
                        source_path TEXT NOT NULL,source_path_key TEXT NOT NULL,
                        source_size_bytes INTEGER NOT NULL CHECK (source_size_bytes >= 0),source_last_write_utc_ticks INTEGER NOT NULL,
                        source_volume_id TEXT NOT NULL DEFAULT '',source_file_identity TEXT NOT NULL DEFAULT '',full_hash BLOB NOT NULL,
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),destination_path TEXT NOT NULL DEFAULT '',validation_error TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY (plan_id,file_id)
                    ) STRICT;
                    CREATE TABLE duplicate_cleanup_audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,plan_id INTEGER NOT NULL REFERENCES duplicate_cleanup_plans(id) ON DELETE RESTRICT,
                        file_id INTEGER NOT NULL,source_path TEXT NOT NULL,destination_path TEXT NOT NULL DEFAULT '',
                        action INTEGER NOT NULL CHECK (action BETWEEN 0 AND 2),outcome INTEGER NOT NULL CHECK (outcome BETWEEN 0 AND 4),
                        message TEXT NOT NULL DEFAULT '',occurred_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    INSERT INTO duplicate_cleanup_plans SELECT * FROM duplicate_cleanup_plans_v5;
                    INSERT INTO duplicate_cleanup_plan_items SELECT * FROM duplicate_cleanup_plan_items_v5;
                    INSERT INTO duplicate_cleanup_audit SELECT * FROM duplicate_cleanup_audit_v5;
                    DROP TABLE duplicate_cleanup_audit_v5;
                    DROP TABLE duplicate_cleanup_plan_items_v5;
                    DROP TABLE duplicate_cleanup_plans_v5;
                    CREATE INDEX ix_cleanup_audit_plan ON duplicate_cleanup_audit(plan_id,occurred_utc_ticks);

                    CREATE TABLE visual_cleanup_plans (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        action INTEGER NOT NULL CHECK (action BETWEEN 0 AND 2),
                        status INTEGER NOT NULL CHECK (status BETWEEN 0 AND 4),
                        quarantine_root TEXT NOT NULL DEFAULT '',
                        allow_unreviewed INTEGER NOT NULL DEFAULT 0 CHECK (allow_unreviewed IN (0,1)),
                        minimum_confidence REAL NOT NULL CHECK (minimum_confidence BETWEEN 0 AND 100),
                        created_utc_ticks INTEGER NOT NULL,completed_utc_ticks INTEGER NULL,error_text TEXT NOT NULL DEFAULT ''
                    ) STRICT;
                    CREATE TABLE visual_cleanup_plan_items (
                        plan_id INTEGER NOT NULL REFERENCES visual_cleanup_plans(id) ON DELETE CASCADE,
                        group_key TEXT NOT NULL,group_id INTEGER NOT NULL,file_id INTEGER NOT NULL,keeper_file_id INTEGER NOT NULL,
                        source_path TEXT NOT NULL,source_size_bytes INTEGER NOT NULL CHECK(source_size_bytes>=0),source_last_write_utc_ticks INTEGER NOT NULL,
                        source_volume_id TEXT NOT NULL DEFAULT '',source_file_identity TEXT NOT NULL DEFAULT '',
                        keeper_path TEXT NOT NULL,keeper_size_bytes INTEGER NOT NULL CHECK(keeper_size_bytes>=0),keeper_last_write_utc_ticks INTEGER NOT NULL,
                        keeper_volume_id TEXT NOT NULL DEFAULT '',keeper_file_identity TEXT NOT NULL DEFAULT '',
                        confidence_score REAL NOT NULL CHECK(confidence_score BETWEEN 0 AND 100),exact_hash BLOB NULL,
                        status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 4),destination_path TEXT NOT NULL DEFAULT '',validation_error TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY(plan_id,file_id)
                    ) STRICT;
                    CREATE TABLE visual_cleanup_audit (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,plan_id INTEGER NOT NULL REFERENCES visual_cleanup_plans(id) ON DELETE RESTRICT,
                        file_id INTEGER NOT NULL,source_path TEXT NOT NULL,destination_path TEXT NOT NULL DEFAULT '',
                        action INTEGER NOT NULL CHECK(action BETWEEN 0 AND 2),outcome INTEGER NOT NULL CHECK(outcome BETWEEN 0 AND 4),
                        message TEXT NOT NULL DEFAULT '',occurred_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE INDEX ix_visual_cleanup_items_group ON visual_cleanup_plan_items(group_key,plan_id);
                    CREATE INDEX ix_visual_cleanup_audit_plan ON visual_cleanup_audit(plan_id,occurred_utc_ticks);
                    """),
                new LibraryCatalogMigration(
                    7,
                    "Visual failed-match decisions and delete-both cleanup intent",
                    """
                    ALTER TABLE visual_group_decisions
                        ADD COLUMN not_match INTEGER NOT NULL DEFAULT 0 CHECK (not_match IN (0,1));
                    ALTER TABLE visual_cleanup_plan_items
                        ADD COLUMN cleanup_intent INTEGER NOT NULL DEFAULT 0 CHECK (cleanup_intent IN (0,1));
                    """),
                new LibraryCatalogMigration(
                    8,
                    "Catalog eligibility, targeted recovery, and decision history",
                    """
                    CREATE TABLE library_presence_observations (
                        location_id INTEGER NOT NULL,
                        file_id INTEGER NOT NULL,
                        state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 6),
                        consecutive_observations INTEGER NOT NULL DEFAULT 0 CHECK(consecutive_observations >= 0),
                        related_file_id INTEGER NULL REFERENCES indexed_files(id) ON DELETE SET NULL,
                        source TEXT NOT NULL DEFAULT '',
                        details TEXT NOT NULL DEFAULT '',
                        last_observed_utc_ticks INTEGER NOT NULL,
                        PRIMARY KEY(location_id,file_id),
                        FOREIGN KEY(location_id,file_id)
                            REFERENCES file_location_memberships(location_id,file_id) ON DELETE CASCADE
                    ) STRICT;
                    CREATE INDEX ix_presence_observations_state
                        ON library_presence_observations(state,last_observed_utc_ticks,file_id);
                    CREATE INDEX ix_presence_observations_file_state
                        ON library_presence_observations(file_id,state);

                    CREATE TABLE library_reanalysis_queue (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        work_mask INTEGER NOT NULL CHECK(work_mask BETWEEN 1 AND 7),
                        status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 3),
                        attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                        maximum_attempts INTEGER NOT NULL DEFAULT 3 CHECK(maximum_attempts BETWEEN 1 AND 10),
                        batch_id TEXT NOT NULL DEFAULT '',
                        error_text TEXT NOT NULL DEFAULT '',
                        next_attempt_utc_ticks INTEGER NULL,
                        created_utc_ticks INTEGER NOT NULL,
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE UNIQUE INDEX ux_reanalysis_active_file
                        ON library_reanalysis_queue(file_id) WHERE status IN (0,1);
                    CREATE INDEX ix_reanalysis_claim
                        ON library_reanalysis_queue(status,next_attempt_utc_ticks,id);

                    CREATE TABLE library_decision_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        target_kind INTEGER NOT NULL CHECK(target_kind BETWEEN 0 AND 4),
                        target_key TEXT NOT NULL,
                        event_kind INTEGER NOT NULL CHECK(event_kind BETWEEN 0 AND 6),
                        before_state TEXT NOT NULL,
                        after_state TEXT NOT NULL,
                        batch_id TEXT NOT NULL DEFAULT '',
                        source TEXT NOT NULL DEFAULT '',
                        reversal_of_event_id INTEGER NULL REFERENCES library_decision_events(id) ON DELETE RESTRICT,
                        reversed_by_event_id INTEGER NULL REFERENCES library_decision_events(id) ON DELETE RESTRICT,
                        occurred_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE INDEX ix_decision_events_recent
                        ON library_decision_events(occurred_utc_ticks DESC,id DESC);
                    CREATE INDEX ix_decision_events_target
                        ON library_decision_events(target_kind,target_key,id DESC);
                    CREATE UNIQUE INDEX ux_decision_event_reversal
                        ON library_decision_events(reversal_of_event_id)
                        WHERE reversal_of_event_id IS NOT NULL;

                    ALTER TABLE visual_similarity_groups
                        ADD COLUMN lifecycle_state INTEGER NOT NULL DEFAULT 0 CHECK(lifecycle_state BETWEEN 0 AND 5);
                    ALTER TABLE visual_similarity_groups
                        ADD COLUMN lifecycle_reason TEXT NOT NULL DEFAULT '';
                    ALTER TABLE visual_similarity_groups
                        ADD COLUMN lifecycle_updated_utc_ticks INTEGER NULL;
                    CREATE INDEX ix_visual_groups_lifecycle
                        ON visual_similarity_groups(analysis_run_id,lifecycle_state,confidence_score DESC,id);
                    """),
                new LibraryCatalogMigration(
                    9,
                    "Conservative visual duplicate families",
                    """
                    CREATE TABLE visual_families (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        family_key TEXT NOT NULL UNIQUE,
                        analysis_run_id INTEGER NOT NULL REFERENCES visual_analysis_runs(id) ON DELETE CASCADE,
                        member_count INTEGER NOT NULL CHECK(member_count >= 3),
                        minimum_confidence REAL NOT NULL CHECK(minimum_confidence BETWEEN 0 AND 100),
                        reclaimable_bytes INTEGER NOT NULL DEFAULT 0 CHECK(reclaimable_bytes >= 0),
                        suggested_keeper_file_id INTEGER NULL REFERENCES indexed_files(id) ON DELETE SET NULL,
                        lifecycle_state INTEGER NOT NULL DEFAULT 0 CHECK(lifecycle_state BETWEEN 0 AND 5),
                        lifecycle_reason TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE INDEX ix_visual_families_active
                        ON visual_families(analysis_run_id,lifecycle_state,minimum_confidence DESC,id);

                    CREATE TABLE visual_family_members (
                        family_id INTEGER NOT NULL REFERENCES visual_families(id) ON DELETE CASCADE,
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        minimum_member_confidence REAL NOT NULL CHECK(minimum_member_confidence BETWEEN 0 AND 100),
                        PRIMARY KEY(family_id,file_id)
                    ) STRICT;
                    CREATE INDEX ix_visual_family_members_file
                        ON visual_family_members(file_id,family_id);

                    CREATE TABLE visual_family_edges (
                        family_id INTEGER NOT NULL REFERENCES visual_families(id) ON DELETE CASCADE,
                        visual_group_id INTEGER NOT NULL REFERENCES visual_similarity_groups(id) ON DELETE CASCADE,
                        left_file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        right_file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        confidence_score REAL NOT NULL CHECK(confidence_score BETWEEN 0 AND 100),
                        evidence_text TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY(family_id,visual_group_id),
                        UNIQUE(family_id,left_file_id,right_file_id)
                    ) STRICT;
                    CREATE INDEX ix_visual_family_edges_group
                        ON visual_family_edges(visual_group_id,family_id);

                    CREATE TABLE visual_family_decisions (
                        family_key TEXT PRIMARY KEY,
                        manual_keeper_path_key TEXT NOT NULL DEFAULT '',
                        reviewed INTEGER NOT NULL DEFAULT 0 CHECK(reviewed IN(0,1)),
                        ignored INTEGER NOT NULL DEFAULT 0 CHECK(ignored IN(0,1)),
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;

                    DROP INDEX ux_decision_event_reversal;
                    DROP INDEX ix_decision_events_target;
                    DROP INDEX ix_decision_events_recent;
                    ALTER TABLE library_decision_events RENAME TO library_decision_events_v8;
                    CREATE TABLE library_decision_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        target_kind INTEGER NOT NULL CHECK(target_kind BETWEEN 0 AND 5),
                        target_key TEXT NOT NULL,
                        event_kind INTEGER NOT NULL CHECK(event_kind BETWEEN 0 AND 6),
                        before_state TEXT NOT NULL,
                        after_state TEXT NOT NULL,
                        batch_id TEXT NOT NULL DEFAULT '',
                        source TEXT NOT NULL DEFAULT '',
                        reversal_of_event_id INTEGER NULL REFERENCES library_decision_events(id) ON DELETE RESTRICT,
                        reversed_by_event_id INTEGER NULL REFERENCES library_decision_events(id) ON DELETE RESTRICT,
                        occurred_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    INSERT INTO library_decision_events
                        SELECT * FROM library_decision_events_v8;
                    DROP TABLE library_decision_events_v8;
                    CREATE INDEX ix_decision_events_recent
                        ON library_decision_events(occurred_utc_ticks DESC,id DESC);
                    CREATE INDEX ix_decision_events_target
                        ON library_decision_events(target_kind,target_key,id DESC);
                    CREATE UNIQUE INDEX ux_decision_event_reversal
                        ON library_decision_events(reversal_of_event_id)
                        WHERE reversal_of_event_id IS NOT NULL;
                    """),
                new LibraryCatalogMigration(
                    10,
                    "Paged exact cleanup execution",
                    """
                    CREATE INDEX ix_cleanup_items_plan_status_group_file
                        ON duplicate_cleanup_plan_items(plan_id,status,group_id,file_id);
                    """),
                new LibraryCatalogMigration(
                    11,
                    "Persistent media integrity scrubbing",
                    """
                    CREATE TABLE media_integrity_results (
                        file_id INTEGER PRIMARY KEY REFERENCES indexed_files(id) ON DELETE CASCADE,
                        method_version INTEGER NOT NULL CHECK(method_version > 0),
                        scrub_type INTEGER NOT NULL CHECK(scrub_type BETWEEN 0 AND 1),
                        result_state INTEGER NOT NULL CHECK(result_state BETWEEN 0 AND 8),
                        source_size_bytes INTEGER NOT NULL CHECK(source_size_bytes >= 0),
                        source_last_write_utc_ticks INTEGER NOT NULL,
                        source_volume_id TEXT NOT NULL DEFAULT '',
                        source_file_identity TEXT NOT NULL DEFAULT '',
                        checked_utc_ticks INTEGER NULL,
                        bytes_checked INTEGER NOT NULL DEFAULT 0 CHECK(bytes_checked >= 0),
                        media_duration_checked_seconds REAL NOT NULL DEFAULT 0 CHECK(media_duration_checked_seconds >= 0),
                        elapsed_seconds REAL NOT NULL DEFAULT 0 CHECK(elapsed_seconds >= 0),
                        error_category INTEGER NOT NULL DEFAULT 0 CHECK(error_category BETWEEN 0 AND 11),
                        details TEXT NOT NULL DEFAULT '',
                        tool_version TEXT NOT NULL DEFAULT '',
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE INDEX ix_integrity_results_state_checked
                        ON media_integrity_results(result_state,checked_utc_ticks,file_id);

                    CREATE TABLE media_integrity_queue (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        scrub_type INTEGER NOT NULL CHECK(scrub_type BETWEEN 0 AND 1),
                        status INTEGER NOT NULL CHECK(status BETWEEN 0 AND 4),
                        attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count >= 0),
                        maximum_attempts INTEGER NOT NULL DEFAULT 3 CHECK(maximum_attempts BETWEEN 1 AND 10),
                        batch_id TEXT NOT NULL DEFAULT '',
                        error_text TEXT NOT NULL DEFAULT '',
                        created_utc_ticks INTEGER NOT NULL,
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE UNIQUE INDEX ux_integrity_queue_active_file
                        ON media_integrity_queue(file_id) WHERE status IN (0,1);
                    CREATE INDEX ix_integrity_queue_claim
                        ON media_integrity_queue(status,scrub_type,id);
                    """),
                new LibraryCatalogMigration(
                    12,
                    "Scheduled library maintenance",
                    """
                    CREATE TABLE library_maintenance_profiles (
                        location_id INTEGER PRIMARY KEY REFERENCES library_locations(id) ON DELETE CASCADE,
                        profile_version INTEGER NOT NULL CHECK(profile_version > 0),
                        enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN(0,1)),
                        cadence INTEGER NOT NULL DEFAULT 0 CHECK(cadence BETWEEN 0 AND 3),
                        days INTEGER NOT NULL DEFAULT 127 CHECK(days BETWEEN 0 AND 127),
                        start_minute INTEGER NOT NULL DEFAULT 60 CHECK(start_minute BETWEEN 0 AND 1439),
                        end_minute INTEGER NOT NULL DEFAULT 360 CHECK(end_minute BETWEEN 0 AND 1439),
                        missed_run INTEGER NOT NULL DEFAULT 2 CHECK(missed_run BETWEEN 0 AND 2),
                        actions INTEGER NOT NULL DEFAULT 83 CHECK(actions BETWEEN 0 AND 255),
                        periodic_quick_scrub_days INTEGER NOT NULL DEFAULT 0 CHECK(periodic_quick_scrub_days BETWEEN 0 AND 3650),
                        last_scheduled_utc_ticks INTEGER NULL,
                        created_utc_ticks INTEGER NOT NULL,
                        updated_utc_ticks INTEGER NOT NULL
                    ) STRICT;
                    CREATE TABLE library_maintenance_runs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        location_id INTEGER NOT NULL REFERENCES library_locations(id) ON DELETE CASCADE,
                        trigger_kind INTEGER NOT NULL CHECK(trigger_kind BETWEEN 0 AND 3),
                        outcome INTEGER NOT NULL CHECK(outcome BETWEEN 0 AND 6),
                        stage TEXT NOT NULL DEFAULT '', started_utc_ticks INTEGER NOT NULL, completed_utc_ticks INTEGER NULL,
                        new_files INTEGER NOT NULL DEFAULT 0, changed_files INTEGER NOT NULL DEFAULT 0, missing_files INTEGER NOT NULL DEFAULT 0,
                        metadata_queued INTEGER NOT NULL DEFAULT 0, exact_processed INTEGER NOT NULL DEFAULT 0, visual_processed INTEGER NOT NULL DEFAULT 0,
                        integrity_queued INTEGER NOT NULL DEFAULT 0, warning_count INTEGER NOT NULL DEFAULT 0, details TEXT NOT NULL DEFAULT ''
                    ) STRICT;
                    CREATE INDEX ix_maintenance_runs_location_started ON library_maintenance_runs(location_id,started_utc_ticks DESC);
                    CREATE TABLE library_maintenance_candidates (
                        run_id INTEGER NOT NULL REFERENCES library_maintenance_runs(id) ON DELETE CASCADE,
                        file_id INTEGER NOT NULL REFERENCES indexed_files(id) ON DELETE CASCADE,
                        change_kind INTEGER NOT NULL CHECK(change_kind BETWEEN 1 AND 2),
                        PRIMARY KEY(run_id,file_id)
                    ) STRICT;
                    CREATE INDEX ix_maintenance_candidates_run_kind ON library_maintenance_candidates(run_id,change_kind,file_id);
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
