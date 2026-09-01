# MediaFlux Changelog

All notable changes to MediaFlux are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.80] - 2026-09-01

### AI Restoration Usability

- Added built-in restoration presets for common restoration workflows.
- Added user restoration profiles with save, load, rename, and delete.
- Profiles are stored as versioned JSON documents.
- Added preset/profile selectors within Custom restoration mode.
- Improved restoration workflow by separating built-in presets from user profiles.
- Recommendations never overwrite saved profiles.
- Existing restoration, preview, AI processing, scheduling, and performance behavior remain unchanged.

## [0.1.79] - 2026-09-01

### Performance Instrumentation (Phase 5.1)

Added internal performance diagnostics to aid future optimization.

- Stage timing throughout the encode pipeline.
- AI throughput metrics: total frames, total chunks, average AI FPS, average chunk duration, and fastest/slowest chunk.
- Largest Time Consumers summary showing where encode time is spent.
- Hardware correlation for CPU, GPU, VRAM, RAM, storage, Windows version, FFmpeg version, and AI backend/version.
- AI jobs include lightweight hardware sampling for GPU, CPU, VRAM, and disk throughput.
- Diagnostics are appended to the existing MediaFlux Performance Summary.
- No changes to encoding behavior, AI restoration algorithms, previews, scheduling, or output quality.

## [0.1.78] - 2026-09-01

### AI Final-Resolution Fix

- Fixed AI full encodes incorrectly retaining the AI intermediate/upscaled resolution.
- AI model scale is now correctly treated as an intermediate restoration scale only.
- Final output resolution now follows the configured Restoration/Encode resolution plan.
- \`Original\` output correctly restores the source dimensions after AI processing.
- Explicit final resize remains authoritative and is applied exactly once.
- Final FFmpeg output and output validation now use the same planned dimensions.
- Added diagnostics showing source, AI intermediate, AI scale, planned final resolution, and final scale decision.
- Preview behavior remains unchanged.
- VFR AI support remains unsupported.

## [0.1.77] - 2026-09-01

### AI Batch Processing Optimization

- Major AI restoration performance optimization.
- Replaced per-frame Real-ESRGAN process launches with one directory-mode batch invocation per 180-frame chunk.
- Reduces backend process launches by roughly 180× for full-video AI restoration.
- Five-second AI motion previews use the same batch-processing path.
- Added exact batch output filename/count/dimension validation.
- Preserved live AI progress, monotonic overall progress, and conservative ETA calculation.
- Improved per-chunk diagnostics including elapsed time, effective FPS, model/scale/device, exit/timeout/stderr.
- Improved cancellation and cleanup of partial AI batch outputs.
- Conventional encoding behavior unchanged.
- VFR AI support remains unsupported.

## [0.1.76] - 2026-08-31

### AI Encode-Stage Stabilization

- Added live AI restoration stage/progress reporting during full encodes.
- Added per-frame AI restoration progress and calculated ETA.
- Added visible AI extraction, restoration, reassembly, and validation stages.
- Final FFmpeg encode correctly transitions status back to Encoding.
- Optimized AI operations by resolving backend/model validation once and reusing it throughout processing.
- Conventional encoding, cancellation, and diagnostics remain unchanged.
- VFR AI support remains unsupported.

## [0.1.75] - 2026-08-31

### AI Intermediate Finalization Fixes

- Fixed AI motion-preview and full-encode intermediate finalization.
- Single-chunk AI previews now safely bypass unnecessary concat processing.
- Fixed Matroska staging filename and extension handling.
- Multi-chunk intermediates validate codec, resolution, pixel format, time base, and frame rate before joining.
- Improved FFmpeg diagnostics for AI intermediate failures, including command context, exit status, stderr, and chunk metadata.

## 🎞️ Video Restoration Phase 4B — AI Quality & Reliability

MediaFlux Video Restoration now provides more source-aware AI preview guidance,
stronger reliability safeguards, and safer CFR-only AI processing.

### What's New

- Added temporal stability analysis for AI motion previews
- Added curated AI configuration comparison with bounded session results
- Added AI-aware restoration recommendations and explainable confidence changes
- Improved source picture-condition and compression-artifact analysis
- Added long AI-job temp-space checks, cleanup, cancellation handling, and diagnostics
- Added multi-window source timing characterization and AI eligibility safeguards
- Added CFR-safe AI full encoding plus still and five-second motion previews
- Added live AI restoration stage/progress reporting during full encodes
- Added per-frame AI restoration progress and calculated ETA
- Added visible AI extraction, restoration, reassembly, and validation stages
- Final FFmpeg encode correctly transitions status back to Encoding
- Optimized AI operations by resolving backend/model validation once and reusing it throughout processing
- Fixed AI full encodes incorrectly retaining the AI intermediate/upscaled resolution
- AI model scale is treated as an intermediate restoration scale only; final output follows the configured resolution plan
- \`Original\` output restores source dimensions after AI processing, while explicit final resize remains authoritative exactly once
- Final FFmpeg output and validation use the same planned dimensions, with diagnostics for source/intermediate/final resolution decisions
- Replaced per-frame Real-ESRGAN process launches with one directory-mode batch invocation per 180-frame chunk
- Reduced backend process launches by roughly 180× for full-video AI restoration
- Five-second AI motion previews use the same batch-processing path
- Added exact batch output filename/count/dimension validation
- Preserved live AI progress, monotonic overall progress, and conservative ETA calculation
- Improved per-chunk diagnostics, cancellation, and cleanup of partial AI batch outputs
- Preserved original audio, subtitles, metadata, chapters, and ancillary streams through split-source encoding
- Added saved-job and scheduled-job support for AI restoration through the shared encode path

### Safety / Behavior

- Conventional restoration and AI Off remain fully supported
- AI restoration remains opt-in and never silently falls back to non-AI processing
- AI restoration currently requires verified CFR video
- VFR, irregular, or unverifiable timing is safely rejected for AI restoration while conventional restoration and normal encoding remain available
- Explicit restoration settings remain user-controlled; analysis and recommendations never silently enable AI

### Current Limitations

- VFR AI restoration is not supported; no VFR capability is exposed in the application
- Temporal analysis is bounded and heuristic rather than a temporal ML model
- AI restoration requires a locally available, validated backend/model/device

### AI Model Resolution Fixes

- Fixed Real-ESRGAN NCNN discovery for scale-specific `realesr-animevideov3-x2`, `-x3`, and `-x4` model files
- `realesr-animevideov3` now resolves automatically to the correct scale-specific backend model
- Fixed already-suffixed model handling so duplicated names such as `-x2-x2` cannot occur
- Complete matching `.bin` and `.param` pairs are required before a model is offered
- Correctly validates `realesrgan-x4plus` and `realesrgan-x4plus-anime` compatibility and supported scale
- Preview, validation, and full encode share the same resolved backend model
- Advanced Settings now provides detected compatible model selection and clearer unavailable-state validation

## 🤖 Video Restoration Phase 4A — AI Restoration

MediaFlux now includes optional GPU-accelerated AI restoration for low-resolution and degraded video, with particular support for vintage animation.

### What's New

- Added optional NCNN/Vulkan AI restoration with Animation and General model modes
- Added backend, model, Vulkan device, scale, and capability validation
- Added AI scale controls independent of final output resolution
- Added bounded deterministic frame processing and validated FFV1/MKV intermediates
- Added AI still-frame and five-second motion preview processing
- Added split-source encoding support so AI video can retain original ancillary streams

### Safety and Reliability

- AI restoration remains Off by default and never silently falls back to conventional encoding
- Pre-AI, AI, post-AI, and final scaling stages are centrally planned
- Frame sets, timing, resolution, intermediate files, and final previews are validated before promotion
- Temporary AI artifacts are cleaned after processing, failure, or cancellation
- VFR or ambiguous timing sources are rejected rather than guessed

### Current Limitations

- AI intermediate processing currently requires a sufficiently deterministic constant frame rate
- No Python runtime, automatic model downloads, VapourSynth, or temporal/interpolation models are included

## 🎞️ Video Restoration Phase 3

MediaFlux Video Restoration now includes a dedicated visual comparison workflow,
making it possible to inspect restoration results before committing to a full encode.

### ✨ What's New

- Added **Preview Restoration...** to Video Restoration controls
- Added dedicated **Original | Restored** comparison window
- Added Side-by-Side, Original-only, and Restored-only viewing modes
- Added representative sample navigation across the source
- Added random sample selection
- Added synchronized timestamp-based Original/Restored comparisons
- Added five-second motion previews for evaluating restoration during playback
- Added restoration-analysis information directly to the preview workflow
- Added full-duration video timeline/scrubber with clickable and keyboard-adjustable seeking
- Added current/total timestamp display and debounced preview refresh during scrubbing

### 🔍 Restoration Comparison

Users can now preview:

- Restoration Off
- Current restoration settings
- Analyze / Recommend result

Recommended restoration can be previewed without changing the actual encode configuration.

**Apply to Encode Settings** is the explicit action that transfers previewed settings to the
encode configuration.

### 🧠 Analyze / Recommend Integration

- Existing Phase 2 analysis results are shown in the preview
- Analyze / Recommend can be run directly from the preview workflow
- Recommendations can be visually evaluated before being applied
- Restoration remains fully user-controlled

### 🎬 Preview Accuracy

- Original and Restored frames use the same source timestamp
- Restored previews use the central MediaFlux restoration pipeline
- Preview uses the same restoration filter ordering and capability validation as normal encoding
- Restoration resizing and normal encode scaling follow the same effective ordering as the real encoder
- Resolution changes are clearly indicated
- Effective preview filter chains are logged for diagnostics

### ⚡ Performance & Caching

- Still and motion previews are cached to avoid unnecessary FFmpeg regeneration
- Original frames can be reused when only restoration settings change
- Identical preview requests reuse cached results
- Preview cache is bounded to prevent uncontrolled temporary-file growth
- Existing FFmpeg capability inventory is reused

### 🛡️ Safety & Behavior

- Previewing restoration does not silently alter encode settings
- Analyze / Recommend does not automatically enable restoration
- Only **Apply to Encode Settings** changes the active restoration configuration
- Preview generation supports cancellation
- Temporary preview data is managed automatically
- Restoration capability validation remains centralized

### 📌 Current Limitations

- Interactive wipe/divider comparison is not included yet
- Motion comparison uses MediaFlux's existing external-player workflow
- AI restoration/upscaling and VapourSynth are not included yet

### 🛠️ Preview Reliability & Stability

- Added request identities and cancellation gating so stale asynchronous previews cannot replace newer selections
- Reorganized preview controls into Navigation, Restoration Preview, Analysis, and Actions groups
- Replaced ambiguous preview actions with an explicit No Restoration / Current Settings / Recommended selector
- Added concise ready, analyzing, generating, cached, canceled, and failure status feedback
- Added still-image validation before cache reuse and bounded cleanup of stale staging files
- Fixed motion preview generation to use unique staging files, await FFmpeg completion, validate output size and video duration with FFprobe, and atomically promote only valid clips
- Fixed invalid, truncated, canceled, or failed motion previews being exposed to the external player or reused from cache
- Serialized generation per cache artifact to prevent concurrent operations from competing over the same output path

### 🔧 Additional Fixes / Improvements

- Added bounded, cache-aware preview generation without changing the normal encoding behavior.

### 🎞️ Introducing Video Restoration

MediaFlux now includes the foundation of a new Video Restoration system designed
to improve older, lower-quality, and archival video during encoding—particularly
vintage animation, DVD sources, VHS/TV captures, and similar material.

Phase 1 introduces:

- Video Restoration integration with the normal encoding pipeline
- Vintage Animation – Light and Vintage Animation – Restore presets
- DVD Animation Restore and VHS / TV Capture Restore presets
- Custom restoration controls for denoise, artifact cleanup, debanding, sharpening,
  deinterlace, color adjustment, and optional aspect-preserving resize
- Restoration support for saved jobs and scheduled encoding
- Restoration logging and validation

## 🎞️ Video Restoration Phase 2

MediaFlux Video Restoration is now more intelligent, source-aware, and safer when evaluating restoration capabilities.

### What's New

- Added Analyze / Recommend for Video Restoration
- Added bounded source sampling for picture-condition analysis
- Added conservative Noise and Banding classification
- Added restoration recommendations with clear explanations
- Animation Encode Hint now contributes to restoration recommendations
- Added FFmpeg restoration filter capability detection
- Added safer restoration preflight validation before encoding

### Improvements

- Restoration recommendations now account for detected source characteristics
- Compare uses the same restoration configuration and validation path as normal encoding
- Normal, scheduled, and saved-job encoding now validate required FFmpeg restoration filters before launching FFmpeg
- FFmpeg capability results are cached to avoid unnecessary repeated checks
- Update window now presents richer, more readable release notes

### Fixes

- Fixed FFmpeg restoration capability detection incorrectly reporting built-in filters as unavailable
- Corrected parsing of real FFmpeg `-filters` output, including flags such as `TS` and `T.`
- Fixed empty/malformed filter inventories being cached as authoritative
- Prevented inventory failures from being misreported as missing restoration filters
- Confirmed parser support for hqdn3d, deblock, deband, and unsharp

### Safety / Behavior

- Restoration remains opt-in and is never automatically enabled
- Explicit user restoration settings always take precedence
- Uncertain analysis results are reported as Unknown rather than guessed
- Telecine/IVTC decisions remain conservative and are not automatically forced
- Known-missing filters fail cleanly before FFmpeg is launched
- Failed or uncertain capability detection is reported as `Unknown`, not falsely as unavailable

### Current Limitations

- Blocking detection remains conservative; MPEG-2 SD material may infer Moderate blocking while other uncertain cases remain Unknown
- Noise/banding analysis uses lightweight FFmpeg signal statistics rather than dedicated artifact-detection models
- AI restoration, VapourSynth, and Original-vs-Restored preview comparison are not included yet

Restoration remains disabled by default, so existing encoding behavior is unchanged
unless explicitly enabled. This is the foundation for future MediaFlux restoration
capabilities.

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
