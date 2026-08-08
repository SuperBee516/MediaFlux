# Library catalog foundation

The Library Analyzer catalog is a local, reconstructable SQLite database stored at
`%LocalAppData%\MediaFlux\UserData\data\library-catalog.db`. It is deliberately
separate from the existing Duplicate Finder caches and results.

## Catalog scope

Schema version 3 stores raw inventory, reconciliation state, and versioned media
probe facts:

- `library_locations` contains configured roots and their current scan generation.
- `indexed_files` contains path identity and inexpensive file-system facts.
- `file_location_memberships` maps one file to every configured root that contains it.
- `scan_runs` records durable generations, completion state, counters, and errors.
- `media_metadata` stores one current FFprobe-derived fact set per indexed file,
  including the source size/timestamp, metadata schema version, tool version,
  retry state, stream summaries, and probe outcome.

Hashes, duplicate groups, statistics, cleanup plans, and user review decisions are
intentionally absent. Later derived analysis should reference stable file identifiers
without being mixed into inventory or probe-fact tables.

## Runtime configuration

MediaFlux uses the bundled SQLite runtime supplied by the pinned NuGet packages; no
machine-wide SQLite installation is required. Bootstrap enables and verifies:

- `journal_mode=WAL`
- `synchronous=NORMAL`
- foreign-key enforcement on every connection
- a 10-second busy timeout
- automatic WAL checkpoints at 2,000 pages
- a 64 MiB journal-size limit

All catalog mutations pass through `SqliteLibraryCatalog` and its single-writer gate.
Inventory batches use prepared statements inside one transaction. Queries open
short-lived read-only connections and use path, volume/file identity, membership,
generation, and availability indexes.

## Inventory and enrichment

`LibraryScanCoordinator` walks roots as a stream, skips directory reparse points,
filters through the same configured video extensions as the encode queue, and writes
bounded batches through a finite channel. A failed, canceled, interrupted, superseded,
or unavailable scan never runs missing-file reconciliation. Successful enumeration
updates root membership generations before marking unseen memberships missing.

`LibraryEnrichmentCoordinator` uses a bounded worker queue and a per-volume gate. It
reuses the existing single-pass `FfprobeService` JSON result and stores container,
duration, bitrate, video, audio, subtitle, chapter, attachment, and color/HDR facts.
Successful current metadata is reused when file facts, metadata version, and FFprobe
tool identity remain current. Failed probes use durable capped retry state. Enrichment
backs off while the main encode queue is active.

## Migrations and recovery

`PRAGMA application_id` identifies MediaFlux catalogs and `PRAGMA user_version`
records the schema version. Ordered migrations run one transaction at a time. Before
upgrading an existing catalog, the SQLite backup API creates a consistent copy under
`catalog-backups`. Quick integrity checks run before and after migration.

Initialization failures are returned without silently deleting or rebuilding the
catalog. A rebuild is an explicit operation that moves the database, WAL, and shared
memory files into `catalog-recovery` before creating an empty catalog. These operations
never enumerate, move, rename, or delete indexed media files.

The inventory catalog is reconstructable. Future non-reconstructable state such as
manual keeper choices, protected files, ignored groups, and cleanup audit history must
have an independent backup/export policy before those tables are introduced.
