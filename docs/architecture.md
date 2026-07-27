# MediaFlux Architecture

## Encoding flow

The main encode queue, DVD "encode using current settings" workflow, and sample
comparison workflow all execute video jobs through `EncodingService`.

`EncodingService` owns process execution, cancellation, FFmpeg progress parsing,
diagnostic capture, duration probing, and collision-safe output creation. It no
longer constructs encoder arguments directly.

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
- Output and MP4 fast-start arguments

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
