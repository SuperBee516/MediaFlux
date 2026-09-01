# MediaFlux Architecture

## Library Analyzer

The standalone Library Analyzer uses one application-lifetime
`LibraryAnalyzerRuntime`. UI code depends on `ILibraryCatalog` and coordinator
contracts rather than SQLite connections.

`LibraryScanCoordinator` performs streaming, recursive discovery with a bounded
producer/consumer channel. It records cheap file-system facts and stable Windows file
identity where available, commits prepared batches through the catalog's single
writer, and applies backpressure before enqueueing metadata work. Pause, resume, and
cancellation do not discard committed batches. Only a fully successful authoritative
enumeration advances missing-file reconciliation; offline, inaccessible, canceled,
interrupted, and superseded roots preserve prior records without treating the root as
empty.

`LibraryEnrichmentCoordinator` runs a small bounded FFprobe worker pool. Work is
durable through source-fact, metadata-version, tool-version, status, attempt, and retry
fields. Per-volume gates avoid concurrent probes against one storage device, while an
encoding-activity seam throttles probes during active encoding. One FFprobe JSON pass
supplies format, video, audio, subtitle, chapter, attachment, color, and HDR facts.

The analyzer form pages file queries directly from SQLite and loads only the current
200-row page. Overview, policy summaries, and location statistics use aggregate or
bounded batch queries. Exact duplicate groups, visual pairs/families, integrity
results, and maintenance history are durable catalog-backed layers.

Exact analysis uses size grouping, quick fingerprints, and full SHA-256 confirmation.
Visual analysis uses versioned representative-frame hashes and indexed band matching,
then publishes review pairs and families without quadratic all-pairs comparison.
Manual keepers, Not Match, review/ignore state, and path protections are stored apart
from rebuildable evidence. Cleanup planners remain advisory until explicit execution;
execution revalidates current paths, file facts, evidence, keepers, and protection.

Library Policy evaluation reads catalog facts in bounded batches and caches only a
small number of result pages. Storage Reclamation combines non-overlapping exact,
reviewed-visual, and policy opportunities into a versioned advisory JSON plan.
Projected and ready bytes are forecasts; only validated cleanup execution can
contribute actually reclaimed bytes, while policy re-encodes are handed to the normal
encode queue without starting it.

Integrity Quick Scrub samples representative decode regions. Full Scrub explicitly
decodes complete selected streams and is manual-only. Results and retry queues are
source-fact-bound. Scheduled maintenance is disabled by default and coordinates the
existing scanner, metadata, duplicate, visual, and Quick Scrub services. It yields to
active encoding, cannot approve cleanup or start encoding, and cannot schedule Full
Scrub.

## Encoding flow

The main encode queue, DVD "encode using current settings" workflow, and sample
comparison workflow all execute video jobs through `EncodingService`.

`EncodingService` owns process execution, cancellation, FFmpeg progress parsing,
duration probing, container resolution, and collision-safe staged output creation. It
no longer constructs encoder arguments directly.

The preferred service API accepts an `EncodingRequest` with a stable encoder
selection. Existing positional overloads remain as compatibility wrappers. Before
FFmpeg starts, the service creates an internal `FfmpegCommandRequest` and passes
it to `FfmpegCommandBuilder`.

## Encoder providers

Encoder-specific behavior is isolated under `Services/Encoders`.

- `EncoderRegistry` resolves stable encoder IDs and codec families.
- `IVideoEncoderProvider` defines encoder capabilities and argument-generation
  responsibilities.
- NVENC, QSV, libx264, libx265, and SVT-AV1 have provider implementations
  representing the application's current command paths.
- `EncoderCapabilities` describes supported codec families, bit depth,
  concurrency, preset metadata, and quality ranges without depending on UI
  display text.

Providers own:

- FFmpeg video codec selection
- Hardware input acceleration
- Encoder-aware scaling and pixel formats
- Constant-quality arguments
- Target-size bitrate arguments
- Presets and backend-specific tuning
- Encoder-specific preset and quality normalization

`FfmpegCommandBuilder` owns the shared pipeline:

- Input and stream mapping
- Metadata and chapter mapping
- Subtitle copy or exclusion
- Audio copy or AAC conversion
- Target-size bitrate budgeting
- MP4/MKV stream compatibility and container-specific output arguments

For normal encodes, `EncodeOutputValidationService` probes the source and staged
output, validates codec, resolution, bit depth, selected streams, chapters, metadata,
duration, and container, and performs representative decode-integrity checks.
`EncodeOutputFinalizationService` promotes without overwrite and revalidates the final
file. Cancellation or failure preserves a recoverable staged file where possible.
`SourceDeletionService` is the only post-encode deletion gate and requires encode
success, completed finalization, an existing distinct final path, and unchanged
verified final size and modification identity.

## Observability and finalized records

`EncodingDiagnosticsService` maintains one bounded session per active encode and
retains at most 300 one-second samples. It parses existing FFmpeg progress output and
adds practical Windows CPU/process-memory telemetry, concurrent-job counts, and
structured maintenance overlap. Unsupported GPU-engine, VRAM, and exact storage-wait
counters remain explicitly unavailable. Deterministic observations are descriptive
only and never alter encoder selection, presets, quality, concurrency, or scheduling.

Completed summaries are attached to append-only encoding statistics schema 3 and to
job-history records. Raw samples and full source paths are not exported by the
Diagnostics clipboard action.

## Persistence boundaries

- `config.json`, encoding presets, queue snapshots, custom policies, and the latest
  reclamation plan use backward-compatible JSON with conservative defaults.
- Job history uses recoverable JSON Lines plus bounded external logs. Encoding
  statistics uses append-only JSON Lines and stable logical operation IDs.
- The Library Analyzer SQLite catalog owns inventory, metadata, derived evidence,
  decisions, cleanup audits, integrity state, and maintenance profiles/history.
  `PRAGMA user_version` is currently 12; migrations are transactional, sequential,
  backup-before-upgrade, and integrity-checked.
- Whole-application backups include the persistent user-data manifest: configuration,
  catalog, policies, plans, history, statistics, profiles, and user-created assets.
  Regenerable AI intermediates, preview caches, staging folders, and temporary files
  are cleaned or skipped before the archive is created. The narrower
  catalog decision export is intended for rebuild recovery and deliberately restores
  user decisions without replaying cleanup plans or audit actions.

## Compatibility boundary

The encoding UI binds directly to `EncoderCapabilities`, codec-family display
options, and `EncoderPresetOption` values. Changing the encoder rebuilds the
format and preset lists from the selected provider, so unsupported combinations
cannot be selected.

Configuration, named encoding presets, and exported queue settings store stable
encoder IDs, codec families, and native preset values. Legacy display strings
remain in the saved-preset and queue schemas for backward compatibility.
`VideoEncoderCompatibility` maps older generic CPU selections to libx264,
libx265, or SVT-AV1 based on the saved format. Existing configuration files with
no encoder selection continue to default to NVENC.

The legacy `LastEncodingSpeedPreset`, `NvencPreset`, and `DualNvenc` JSON fields
remain only at this persistence boundary so existing user files can migrate.
Runtime queue, DVD, and sample-comparison code uses encoder-neutral names and
the structured request API.

`EncoderRegistry.ResolveLegacyCodec` and the existing `EncodingService`
overloads remain available for callers that still supply FFmpeg codec names.

## Smart Encode recommendations

`EstimateBackgroundService` obtains cached media facts from `MediaInfoService`,
calculates the row-specific output estimate, and then evaluates it through the
UI-independent `SmartEncodeDecisionService`. The decision service compares the
source against the current `VideoEncoderSelection`, target height, aggregate
audio cost, and configured minimum savings.

Recommendations are explainable values rather than automatic commands. Each
result includes a category, confidence, projected savings, and ordered reasons.
Interlacing, upscaling, multiple video streams, likely animation, unusually
audio-heavy files, and conversion to a less efficient codec produce Review
results. Otherwise, projected savings produce Strong candidate, Moderate
candidate, or Skip.

The queue keeps recommendation state in `RowMeta` only for the current file and
settings. Queue exports do not persist derived recommendations, sample
projections, or frame-analysis results; they do persist the user's explicit
per-row content hint. Changing a decision-relevant encoding option starts a new
analysis generation, cancels stale work, and replaces the row result when the
current generation completes. The pre-encode prompt applies after the explicit
full-queue or selected-file scope is captured and retains exact-duplicate
exclusions.

`DeepMediaAnalysisService` is an optional selected-row path. It reuses
`SampleComparisonService` in projection-only mode so temporary original and
encoded clips are cleaned without building preview videos. Samples run in
quality mode rather than target-size mode, which keeps the observed projection
independent from the metadata estimate. FFmpeg `idet` scans the same
beginning/middle/end regions, while small RGB samples feed a deliberately
conservative color/edge heuristic for possible animation or screen content.
`SmartEncodeDecisionService.RefineWithDeepAnalysis` preserves the baseline
savings calculation and only raises confidence, lowers confidence, or moves
questionable evidence to Review. Deep analysis never changes a row's encode
target or selected profile.

Phase 3 adds a narrow `RemuxOnly` decision and an explicit execution path.
`SmartEncodeDecisionService` emits it only when a single efficient video stream
is in a recognized legacy container, projected encoding savings are below the
configured minimum, and no higher-priority Review condition exists. Modern MP4,
Matroska, and WebM containers are not labeled for remux solely because their
video is efficient.

`MediaRemuxService` copies video, audio, subtitle, and attachment streams into
MKV while retaining metadata and chapters. It never invokes an encoder or
retries with transcoding. Output is written to a uniquely named partial file,
probed after FFmpeg exits, and promoted only when the video/audio/subtitle codec
sets, attachment set, chapter count, and duration agree with the source.
Cancellation or failure removes the partial file and leaves the source
untouched. Ordinary remux jobs are recorded separately from Encode and DVD
Remux history entries.

## Capability validation

`FfmpegEncoderCapabilityService` inspects the exact `ffmpeg.exe` resolved by
`FfmpegToolResolver` with `-encoders`. Results are cached by executable path,
size, and modification time. The main encoder and codec lists omit selections
that the configured FFmpeg build does not advertise. If inspection fails,
MediaFlux reports that availability is unknown without inventing a negative
result.

The UI guards queue encoding, DVD encoding with current settings, and sample
comparison before work starts. `EncodingService` independently checks the
encoder again immediately before command construction so non-UI callers receive
the same protection.

`EncodingRequestValidator` is the shared normalization boundary for:

- Stable encoder ID, codec family, and FFmpeg codec agreement
- Encoder-native presets and quality ranges
- 8-bit and 10-bit capability rules
- Target-size and audio-channel validation
- Hardware decode and concurrent-session eligibility

Software encoders always normalize hardware acceleration off. Their providers
therefore cannot emit CUDA, NVENC, or other hardware-backend input arguments.
Queue, DVD, sample comparison, preview, and estimation workflows now snapshot or
pass stable `VideoEncoderSelection` values rather than reconstructing the
encoder from independent display strings.

## Testing

`MediaFlux.Tests` contains command characterization tests for the current NVENC,
QSV, libx264, libx265, and SVT-AV1 paths. Registry tests cover the mapping between
legacy codec names, stable encoder IDs, codec families, and declared
capabilities. The libx265 provider exposes every native speed preset, clamps CRF
to the supported 0-51 range, and uses the normalized values for both
constant-quality and target-bitrate commands.

The command matrix builds every declared encoder/codec pair and verifies that
backend-specific arguments do not leak into another provider. Configuration and
named-preset tests cover both stable round trips and legacy NVENC/CPU migration.

`LiveEncoderWorkflowTests` can exercise the installed tools without making the
normal test suite depend on FFmpeg. Set `MEDIAFLUX_LIVE_FFMPEG_PATH` and
optionally `MEDIAFLUX_LIVE_FFPROBE_PATH` to run real libx265 quality and
target-size encodes. Setting `MEDIAFLUX_LIVE_NVENC=1` additionally enables the
NVENC smoke encode on compatible hardware. The live checks probe codec, pixel
format, dimensions, audio handling, subtitle exclusion, and metadata
preservation.
