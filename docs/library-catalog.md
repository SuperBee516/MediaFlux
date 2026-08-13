# Library catalog foundation

The Library Analyzer catalog is a local, reconstructable SQLite database stored at
`%LocalAppData%\MediaFlux\UserData\data\library-catalog.db`. It is deliberately
separate from the existing Duplicate Finder caches and results.

## Catalog scope

Schema version 12 keeps observed facts, derived analysis, user decisions, integrity,
and maintenance state separate:

- `library_locations` contains configured roots and their current scan generation.
- `indexed_files` contains path identity and inexpensive file-system facts.
- `file_location_memberships` maps one file to every configured root that contains it.
- `scan_runs` records durable generations, completion state, counters, and errors.
- `media_metadata` stores one current FFprobe-derived fact set per indexed file,
  including the source size/timestamp, metadata schema version, tool version,
  retry state, stream summaries, and probe outcome.
- `file_hash_facts` stores source-fact-bound quick fingerprints and full SHA-256
  evidence with independent algorithm versions.
- `duplicate_analysis_runs`, `exact_duplicate_groups`, and
  `exact_duplicate_members` contain resumable derived analysis and physical-copy
  accounting. They can be rebuilt from current observed facts.
- `duplicate_group_decisions` and `duplicate_file_protections` preserve manual
  keepers, review/ignore state, and path protection independently of derived group ids.
- `duplicate_cleanup_plans`, plan items, and the append-only cleanup audit preserve
  previews, revalidation outcomes, and completed user actions.
- `visual_fingerprints` and `visual_hash_bands` store source-fact-bound, versioned
  perceptual evidence and its indexed candidate keys.
- `visual_analysis_runs`, `visual_candidate_pairs`, and
  `visual_similarity_groups` store restart-safe derived work and published review pairs.
- `visual_group_decisions` preserves keeper/review/ignore choices independently of
  derived result ids.
- `location_scan_accelerators` stores the minimum trusted NTFS journal checkpoint
  required for the conservative no-change shortcut.
- `library_presence_observations`, `library_reanalysis_queue`, and
  `library_decision_events` preserve recovery evidence and reversible decision history.
- `visual_families`, members, edges, and decisions provide stable multi-file review
  without turning weak pair edges into automatic cleanup authority.
- `media_integrity_results` and `media_integrity_queue` store source-fact-bound Quick
  or Full Scrub outcomes and bounded retry work.
- `library_maintenance_profiles`, runs, and candidates store disabled-by-default
  per-location schedules, bounded history, and changed-file targeting.

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

`LibraryEnrichmentCoordinator` uses a bounded worker queue and a shared storage-device
gate. Local partitions are grouped by Windows physical device number where available;
network paths are grouped by server/share, and volume/root identity is the safe fallback. It
reuses the existing single-pass `FfprobeService` JSON result and stores container,
duration, bitrate, video, audio, subtitle, chapter, attachment, and color/HDR facts.
Successful current metadata is reused when file facts, metadata version, and FFprobe
tool identity remain current. Failed probes use durable capped retry state. Enrichment
backs off while the main encode queue is active.

After a successful authoritative scan, local NTFS roots retain the journal id and
`NextUsn` observed before enumeration. A later scan may skip traversal only when the
same volume and journal report exactly the same `NextUsn`. Any journal activity,
reset, discontinuity, access failure, unsupported filesystem, ReFS volume, or network
path falls back to the existing authoritative scanner. Journal data is never used for
missing-file reconciliation.

## Analytics and exact duplicates

Statistics are computed with catalog aggregate queries. Location, codec, resolution,
container, dynamic-range, metadata-health, largest-file, and exact-duplicate totals do
not materialize the full file catalog in application memory. Duplicate group/member
views use bounded database pages.

`LibraryDuplicateAnalysisCoordinator` groups present files by size, computes a
three-region SHA-256 quick fingerprint only for repeated sizes, and computes full
SHA-256 only for matching size/fingerprint survivors. Quick fingerprints never prove
duplication. Hash writes are committed in bounded batches, reads are limited globally
and through the shared physical-storage scheduler, and active encoding pauses new reads. Stable file identities
propagate valid evidence across hard-link aliases and ensure aliases count as one
physical copy for reclaimable storage.

Inventory changes remove stale hash facts and affected derived groups immediately.
Canceled work retains completed hash evidence; exact groups are published only after a
complete transactional rebuild. Keeper recommendations adapt catalog DTOs to the
existing UI-independent scoring service without coupling the legacy Duplicate Finder
to the Library Analyzer.

Cleanup always starts with a durable preview. Execution revalidates the keeper and
each candidate against current group membership, availability, protection, path,
size, modified time, stable identity, and SHA-256. Hard-link aliases and ignored or
protected groups/files are excluded. Recycle Bin and quarantine remain recoverable
choices; permanent deletion is separately warning-gated and uses the same evidence
revalidation. A validated keeper is never included in a plan.

## Policies and reclamation

Library Policy Profiles are stored as versioned JSON outside the reconstructable
catalog. Evaluation reads catalog facts in 500-row batches, normalizes unknown or
unsupported encoder combinations to non-actionable results, and caches at most twelve
UI pages. Policy results are recommendations; they do not change files or start work.

Storage Reclamation combines bounded candidate streams from exact duplicates,
reviewed visual pairs/families, and optional policy re-encode opportunities. It
deduplicates physical identities, caps each source, and stores a versioned advisory
JSON plan. **Projected reclaim** includes review-dependent estimates, **Ready reclaim**
counts currently selected executable opportunities, and **Actually reclaimed** is
never inferred from a projection. Cleanup and encode handoffs revalidate current state
through their owning services.

## Integrity and scheduled maintenance

Quick Scrub decodes beginning/middle/end regions; Full Scrub is an explicit manual
whole-stream decode. Neither operation modifies media. Results become stale when the
indexed size, timestamp, or stable identity changes, and unavailable locations do not
become confirmed missing files.

Scheduled maintenance is disabled by default. Per-location profiles can invoke the
existing incremental scan, metadata, exact/visual analysis, and targeted Quick Scrub
queues inside a time window. Encoding has priority. Scheduled maintenance cannot
approve or execute cleanup, cannot enqueue or start media encodes, and never schedules
Full Scrub. Interrupted runs are recovered as interrupted history rather than silently
continued as successful work.

## Visual similarity

`LibraryVisualAnalysisCoordinator` extracts six representative 9x8 grayscale frames
with one FFmpeg process per file and stores aligned 64-bit difference hashes. Valid
fingerprints are reused until the indexed size, timestamp, volume/file identity,
algorithm version, or FFmpeg tool version changes. Work is bounded by a small worker
pool, the shared physical-storage scheduler, and the encoding activity throttle.

Candidate generation is database-backed rather than pairwise. Each sample hash is
split into four indexed 16-bit bands. SQLite joins only matching bands from bounded
buckets, requires multiple band collisions, and applies a three-percent/three-second
duration constraint before exact Hamming scoring. This deliberately discards very
common bands to prevent quadratic candidate explosions. Accepted results are
two-member review pairs rather than transitive clusters, avoiding false-match
amplification through weak graph edges.

Visual matches expose confidence, aligned-frame evidence, duration delta, codec and
resolution differences, and durable review/ignore/Not Match/keeper state. Similarity
evidence is advisory: cleanup requires an explicit reviewed plan, a valid keeper (or
an explicit delete-both decision), current evidence, and the same protected-file and
action confirmation gates used by catalog cleanup.

## Migrations and recovery

`PRAGMA application_id` identifies MediaFlux catalogs and `PRAGMA user_version`
records the schema version. Ordered migrations run one transaction at a time. Before
upgrading an existing catalog, the SQLite backup API creates a consistent copy under
`catalog-backups`. Quick integrity checks run before and after migration.

Initialization failures are returned without silently deleting or rebuilding the
catalog. A rebuild is an explicit operation that moves the database, WAL, and shared
memory files into `catalog-recovery` before creating an empty catalog. These operations
never enumerate, move, rename, or delete indexed media files.

Raw inventory and derived analysis are reconstructable. Manual keeper choices,
protected files, ignored/reviewed groups, cleanup plans, and cleanup audit history are
not. `CreateUserDataBackup` exports decision and audit tables into a compact standalone SQLite file,
and an export is attempted automatically before catalog rebuild. Rebuild still proceeds
if export is impossible for a damaged database; the complete database/WAL set remains
preserved in the recovery archive for forensic recovery. `RestoreUserDataBackup`
integrity-checks a compatible backup and merges only exact decisions, path protections,
and visual decisions. Cleanup plans and audit rows remain available in the backup for
audit purposes but are never imported or re-executed. Protected paths and manual keeper
references are path-based: they reattach after a rebuild when paths remain stable, while
renamed or moved roots can require those choices to be reviewed and assigned again.
The normal application backup archives the entire user-data directory and is the
complete backup/restore path for maintenance profiles, integrity history, custom
policies, reclamation plans, encoding history/statistics, and configuration.
