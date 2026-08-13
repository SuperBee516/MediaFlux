# MediaFlux Changelog

All notable changes to MediaFlux are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Safer Encoding

- Added transactional encoding: output is written to a staged file, validated with
  FFprobe and representative decode checks, promoted without overwrite, and verified
  again before it is considered successful.
- Source deletion now fails closed until encode success, staged validation, promotion,
  and final verification all complete. It also rechecks the final output identity and
  retains the source after cancellation, validation failure, or output change.
- Added MP4, Matroska (MKV), and per-file Auto container selection. Auto keeps selected
  streams safely and explains the resolved container; container choice cannot bypass
  finalization safeguards.
- Retained provider-based NVENC, QSV, libx264, libx265, and SVT-AV1 selection with
  stable IDs, capability validation, calibrated targeting, and Smart Encode guidance.

### Smarter Storage Optimization

- Added built-in and custom Library Policy Profiles with explainable compliance,
  confidence, projected savings, and conservative unsupported/review outcomes.
- Added advisory Storage Reclamation planning across exact duplicates, reviewed visual
  matches/families, and policy re-encode opportunities, with physical-copy
  deduplication and explicit **Projected reclaim**, **Ready reclaim**, and
  **Actually reclaimed** accounting.
- Added runtime confidence, historical throughput, savings-per-compute-hour estimates,
  and compute-aware ordering without overstating synthetic prediction accuracy.
- Cleanup continues through the established revalidation/confirmation services;
  re-encode opportunities are revalidated and handed to the normal queue without
  starting encoding or changing global settings.

### Library Health & Integrity

- Expanded the SQLite Library Analyzer through schema 12 with scalable exact and
  visual duplicate families, persistent keeper/Not Match/protection decisions,
  recovery evidence, and bounded database paging.
- Added non-destructive Quick Scrub and explicit Full Scrub, source-fact-bound results,
  bounded retry queues, stale-result detection, and unavailable-location safety.
- Full Scrub remains manual and is never selected by scheduled maintenance.

### Automation

- Added disabled-by-default per-location Scheduled Maintenance with explicit windows,
  missed-run policy, manual Run Now, bounded history, changed-file targeting, and
  safe cancellation/restart behavior.
- Maintenance coordinates existing scan, metadata, duplicate, visual, and targeted
  Quick Scrub services. Encoding has priority; maintenance cannot approve/delete
  media, enqueue/start encodes, or schedule Full Scrub.

### Diagnostics & Performance

- Added a live Diagnostics tab and persistent bounded summaries for FFmpeg speed, FPS,
  bitrate, elapsed/ETA, encoder/preset, concurrency, CPU/memory signals, finalization
  overhead, and maintenance overlap.
- Unsupported GPU-engine, VRAM, and exact storage-wait counters are reported as
  unavailable. Diagnostic observations are deterministic and never alter encoder
  behavior or settings.

### Reliability / Scalability

- Preserved bounded scan/enrichment queues, 200-row catalog UI pages, indexed visual
  candidate generation, bounded policy/reclamation inputs, and 300-sample diagnostic
  retention for large libraries.
- Added sequential migration/restart coverage for every supported catalog schema,
  backward-compatible JSON/statistics loading, complete application-data backup
  coverage, and cancellation/disposal hardening.
- Corrected stale architecture and README descriptions so implemented catalog,
  optimization, integrity, maintenance, and diagnostics layers are documented as
  current functionality.

## [0.1.3] - 2026-07-18

### Fixed

- Automatic target-size calculation now uses improved media metadata and
  produces more accurate queue estimates.

## [0.1.2] - 2026-07-18

### Changed

- Expanded the README with additional MediaFlux screenshots.

## [0.1.1] - 2026-07-18

### Added

- GitHub Releases-based application updates with an in-app update prompt.
- Automated Windows release packaging through GitHub Actions.
- Release documentation for maintainers.
- Dedicated application-data paths for durable settings, history, logs, and
  other user data.

### Changed

- Replaced the network-folder, timestamp-based updater with versioned GitHub
  releases.
- Improved backup handling and migration of existing user data.

## [0.1.0] - 2026-07-18

### Added

- Initial MediaFlux release, established as an independent project from
  GoEncode.
- Batch media encoding with CPU and GPU codec support, presets, progress
  reporting, and target-size estimates.
- Duplicate-media analysis and configurable keeper scoring.
- Folder imports, watch folders, Explorer integration, audio tools, history,
  backups, and configurable application settings.

[Unreleased]: https://github.com/SuperBee516/MediaFlux/compare/v0.1.41...HEAD
[0.1.3]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.3
[0.1.2]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.2
[0.1.1]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.1
[0.1.0]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.0
