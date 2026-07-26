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
