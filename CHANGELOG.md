# MediaFlux Changelog

All notable changes to MediaFlux are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Pre-encode sample comparisons for the beginning, middle, and end of a video,
  with synchronized original/encoded playback, measured size and speed
  projections, and one-click quality, compression, or codec adjustments.
- Persistent FFmpeg/FFprobe availability guidance, including Settings status,
  recovery actions, and fail-fast checks for operations that require the tools.
- Automated tests for queue size estimation.
- Release-workflow verification that launches the published application and
  detects startup failures.
- A provider-based video encoder architecture with stable encoder IDs and
  shared validation for NVENC, QSV, libx264, libx265, and SVT-AV1.
- Full CPU HEVC encoding through `libx265`, including CRF and target-size modes,
  8-bit and 10-bit output, scaling, and all native x265 speed presets.
- FFmpeg encoder capability detection for the configured executable, with
  unavailable encoder/codec combinations removed from the UI.
- Command-matrix and opt-in live workflow tests covering software and hardware
  encoder paths.

### Changed

- Queue size estimates now use each row's effective encoding settings and work
  for profiles without a manually entered target size.
- The application window and published executable now use the MediaFlux icon.
- Duplicate review no longer uses an unnecessary asynchronous wrapper.
- Encoder, preset, queue, DVD, sample-comparison, preview, and estimate flows now
  use one stable encoder selection instead of reconstructing backend choices
  from UI display text.
- Encoder changes refresh the codec and preset lists while preserving the
  existing encoding workflow and defaulting upgraded configurations to NVENC.
- Video output explicitly preserves input metadata and chapters through the
  shared FFmpeg command pipeline.

### Fixed

- DVD title analysis now uses IFO navigation timing when VOB packet timestamps
  wrap or reset, preventing valid remuxes and direct encodes from being rejected
  or budgeted against an inflated duration.
- Pre-encode sample comparisons now generate missing packet timestamps when
  clipping legacy AVI sources and retry timestamp-specific remux failures with
  a lossless normalized video sample.
- Pre-encode sample comparisons and the error-log viewer no longer retain or
  load unbounded FFmpeg diagnostics, preventing memory exhaustion during
  comparison failures and keeping large logs readable.
- A startup crash caused by accessing the main window handle too early.
- Missing or stale profile estimates when no manual target size is configured.

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

[Unreleased]: https://github.com/SuperBee516/MediaFlux/compare/v0.1.3...HEAD
[0.1.3]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.3
[0.1.2]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.2
[0.1.1]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.1
[0.1.0]: https://github.com/SuperBee516/MediaFlux/releases/tag/v0.1.0
