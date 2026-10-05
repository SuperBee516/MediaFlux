# MediaFlux Encoding Engine V2 — Phase 3 fast estimates

Phase 3 adds a deterministic estimate path for an already-resolved source and a V2 encoding intent. It is advisory: it does not create an encode plan, dispatch queue work, or decide whether production may encode a source.

## Commands

Each command takes one UTF-8 JSON request file and writes one machine-readable JSON result to stdout:

```powershell
MediaFlux.exe --v2-estimate request.json
MediaFlux.exe --v2-estimate-quality-set request.json
MediaFlux.exe --v2-benchmark-estimates request.json
```

The request uses the V2 encoder, codec, quality, preset, geometry, container, and audio choices. `sourcePath` must be fully qualified. `sourceFacts` is optional; when omitted, the command probes once with FFprobe. When supplied, no media tool is started and the facts are reused for every quality value.

Example single estimate:

```json
{
  "schemaVersion": 1,
  "sourcePath": "D:\\Media\\input.mp4",
  "encoder": "libx265",
  "codec": "hevc",
  "container": "mp4",
  "qualityMode": "crf",
  "qualityValue": 27,
  "preset": "medium",
  "geometryMode": "fitBox",
  "maxWidth": 1280,
  "maxHeight": 720,
  "audioMode": "aac",
  "audioBitrateKbps": 160,
  "littleSavingsThresholdPercent": 5,
  "processAll": false
}
```

An optional `sourceFacts` object can carry `sourcePath`, `sourceSizeBytes`, `durationSeconds`, `videoCodec`, `width`, `height`, optional `frameRate` and `videoBitrateKbps`, and an `audioStreams` array. Each audio stream can supply `codec`, `bitrateKbps`, `sizeBytes`, and `durationSeconds`. For example:

```json
"sourceFacts": {
  "sourcePath": "D:\\Media\\input.mp4",
  "sourceSizeBytes": 2500000000,
  "durationSeconds": 3600,
  "videoCodec": "h264",
  "width": 1920,
  "height": 1080,
  "frameRate": 29.97,
  "videoBitrateKbps": 5400,
  "audioStreams": [
    { "codec": "aac", "bitrateKbps": 160, "durationSeconds": 3600 }
  ]
}
```

For a quality set, add `qualityValues`, for example `[20, 24, 28, 32]`. The command resolves facts once and returns one estimate per value. For the benchmark command, `repetitions` is the total calculation count (2–10,000; default 100). `qualityValues` is optional and is cycled through on each iteration.

Results include source identity/size/duration, resolved output geometry, requested encoder/quality/audio settings, video/audio/container/total bytes, signed savings bytes and percent, savings classification, confidence, estimate basis, and calculation time. Top-level fields report whether probing occurred and count FFprobe, production FFmpeg, sample FFmpeg, and adaptive candidate generation. Estimate paths report zero for production FFmpeg, sample FFmpeg, and adaptive candidate generation.

## Estimate model

The estimator accepts source facts and performs arithmetic only. It does not open media, create a plan, access user history, or launch FFmpeg.

1. Use measured source video bitrate when present. Otherwise derive total source rate from bytes/duration and subtract source audio stream rate/size facts (falling back to 128 kb/s per stream) and a 1% (at least 16 kb/s) container allowance. Output audio policy does not alter that source-rate derivation.
2. Convert source video rate to bits/pixel/frame using source dimensions and frame rate. Missing frame rate uses 30 fps and lowers confidence.
3. Divide measured source BPP by a static codec BPP reference, take the square root, and bound the resulting content factor to `0.55–1.65`. This bounds source-rate uncertainty; quality and geometry monotonicity come from their direct multiplicative terms and are not clamped. Static references are H.264 `0.085`, HEVC `0.055`, AV1 `0.045`, VP9 `0.055`, Theora/MPEG-4 `0.10`, and MPEG-2 `0.14` bits/pixel/frame. Unknown source codecs use `0.10` and low confidence.
4. Calculate target BPP from the target codec reference (`0.085` H.264, `0.055` HEVC, `0.045` AV1), an encoder factor, the content factor, the quality factor, and the preset factor. Software encoder factors are `1.0`; NVENC factors are `1.18` for H.264, `1.25` for HEVC, and `1.30` for AV1.
5. The quality factor is `2^((quality anchor - quality value) / 6)`. Anchors are CRF/CQ/QP 23 for H.264, 27 for libx265, 25 for HEVC NVENC, 30 for SVT-AV1, and 25 for AV1 NVENC. Lower CRF/CQ/QP values therefore increase estimated video bytes.
6. Preset factors account for slower settings producing fewer bytes at the same quality: x264/x265 use `ultrafast 1.38`, `superfast 1.25`, `veryfast 1.16`, `faster 1.10`, `fast 1.05`, `medium 1.00`, `slow 0.96`, `slower 0.92`, and `veryslow 0.88`. NVENC p1–p7 use `1.25, 1.15, 1.07, 1.00, 0.96, 0.93, 0.90`. SVT-AV1 uses `1 + (preset - 6) × 0.035`.
7. Multiply target BPP by target pixels/frame and source frame rate, then by duration. V2 Phase 1 has no output frame-rate control, so this model preserves source frame rate.
8. Estimate copied audio from stream size, then stream bitrate, then a 128 kb/s per-stream fallback. AAC uses the selected target bitrate per source audio stream; `none` produces zero audio bytes.
9. Add container overhead: 1.5% for MP4 or 2% for Matroska, with a 4,096-byte minimum.

Static factors are version-controlled and deliberately simple. They are initial model assumptions, not learned user history or a claim of encoder equivalence. The benchmark and real-media results below delimit their accuracy.

Confidence is `High` for a known source codec, in-range source-rate factor, explicit source video bitrate and frame rate, and known copied-audio rates. It is `Medium` when frame rate, source video rate, or copied-audio rate needs a fallback, or when the target is a hardware encoder/SVT-AV1. It is `Low` for an unknown source codec or a source-rate factor outside the `0.55–1.65` model envelope. Confidence never suppresses the numeric estimate.

## V1 audit and dependency boundary

| Component | Classification | Phase 3 use |
| --- | --- | --- |
| `SizeEstimateService` | Adapted, then reimplemented | The static codec BPP references and bounded square-root source-complexity idea were inspected and reimplemented in `V2FastEstimate.cs`. No V1 estimate method is called. V1 bitrate floors, adaptive storage options, historical projections, and profile orchestration are excluded. |
| `EstimateBackgroundService` | Do not use | Depends on V1 preview, history, recommendation, queue, and background orchestration. |
| `MediaInfoService` | Do not use | Its V1 cache/probe orchestration is not part of the V2 fast calculation. |
| `MediaProbeResult` / `MediaProbeStreamInfo` | Reuse data shapes | `V2FastEstimateSourceFacts.FromMediaFacts` maps already-resolved FFprobe facts to the V2 estimate contract. |
| `V2SourceMetadataProvider` / `FfprobeService` | Reuse at the metadata boundary | Used only if source facts were not supplied. The calculation itself never probes. |
| `V2EncodePlanner` pure encoder/quality/preset validators | Reuse | Keeps the estimator aligned with the six Phase 1 encoder/codec pairs and accepted quality/preset ranges. It does not build or execute a plan. |

The estimate implementation has no reference to `EncodingService`, `EstimateBackgroundService`, `MainForm`, V1 calibration, adaptive Storage Savings, Policy C, SmartEncode, prediction shadow, empirical preview, or sample-encode code. The headless estimate branch constructs only an FFprobe metadata provider when facts are missing. Queue admission and MainForm remain unchanged.

## Savings classification

`NoSavings` means estimated output is at least source size and remains recommended skip even when `processAll` is true. `LittleSavings` means positive savings below the supplied threshold; the provisional default is 5%, and `processAll` clears only this recommendation. `MeaningfulSavings` means savings meet or exceed the threshold. This is classification only; Phase 3 does not skip or admit queue jobs.

## Validation record

Starting baseline: branch `develop`, HEAD `1461fdb2cd52c7e7236b926f2aac12b7946c883d`, `origin/develop` `676d6a8582df504b3cfb3ce673c044d4aa8127d0` (origin eight commits ahead), version `1.8.1`. Phase 1 `c3a26cd` and Phase 2 are present. The six protected pre-existing dirty files remained outside Phase 3 scope. Baseline Release build passed; existing V2 tests passed 32/32. The baseline full suite was interrupted after about 3.5 minutes without a summary while its test host was still using CPU; it is not counted as a pass.

Phase 3 focused tests cover deterministic results, quality and geometry monotonicity, all six Phase 1 encoder/codec pairs, high/low source-rate normalization, audio copy/AAC/none, all savings categories and Process All semantics, supplied facts vs one-time probing, malformed/unsupported input, command registration and JSON output, zero production FFmpeg invocation, benchmark result fields, dependency audit, and 100-row responsiveness.

The existing ignored Sintel trailer fixture is 44,448,221 bytes, 52.25 seconds, 1920×1080 at 24 fps, Theora with one Vorbis stream. Matching V2 libx264 ultrafast CRF outputs were produced and validated at 640×360 with AAC 128 kb/s. Signed error is `(estimate - actual) / actual`.

| Source / output configuration | Estimate bytes | Actual bytes | Signed error | Direction |
| --- | ---: | ---: | ---: | --- |
| Sintel → libx264 CRF 20, 640×360, AAC 128 | 7,856,353 | 8,832,701 | −11.05% | Under |
| Sintel → libx264 CRF 24, 640×360, AAC 128 | 5,263,185 | 5,803,113 | −9.30% | Under |
| Sintel → libx264 CRF 28, 640×360, AAC 128 | 3,629,592 | 3,854,848 | −5.84% | Under |
| Sintel → libx264 CRF 32, 640×360, AAC 128 | 2,600,493 | 2,679,187 | −2.94% | Under |
| Sintel → h264_nvenc CQ 28, 640×360, AAC 128 | 3,226,541 | 2,974,942 | +8.46% | Over |
| Synthetic 1080p high-rate H.264 → libx265 CRF 27, audio copy | 3,021,253 | 1,617,291 | +86.81% | Over; low confidence |
| Synthetic 720p HEVC → libx264 CRF 26, 640×360, no audio | 172,104 | 277,458 | −37.97% | Under |
| Synthetic low-rate 360p H.264 → libx265 CRF 28, no audio | 98,135 | 120,842 | −18.79% | Under; classified NoSavings |

The four Sintel quality outputs are strictly monotonic in actual size. They were validated/promoted by the V2 runner; the Sintel source remained present. The three synthetic fixtures were generated as short 2–3 second clips under ignored `TestResults/EncodingV2Certification/phase3-validation-20261005`. The high-rate H.264 case shows a material outlier even after source-rate normalization, and the HEVC conversion is also rough. These values should be displayed as estimates with confidence, not used as authoritative pre-encode acceptance. They do not justify tuning to this small corpus.

Benchmark results from the headless benchmark on Sintel, four quality values cycling across 200 calculations:

| Measurement | Result |
| --- | ---: |
| Cold FFprobe | 56.121 ms |
| First calculation | 3.3553 ms |
| Warm P50 | 0.0017 ms |
| Warm P95 | 0.0037 ms |
| Warm maximum | 0.0199 ms |
| Total calculations / calculation time | 200 / 3.772 ms |
| Whole operation including process startup, probe, and JSON | 102.13 ms |
| FFprobe / production FFmpeg invocations | 1 / 0 |

With supplied source facts, 100 calculations completed in 48.87 ms for the whole command, with 0 FFprobe calls, 0 production FFmpeg calls, and 0 sample encodes. The focused test also verifies 100 calculations stay below one second. Repeated quality estimation has no sampling or history wait.

Final Release validation: `dotnet build MediaFlux.sln -c Release --no-restore` passed with 0 warnings and 0 errors. The Phase 1/2/3 filter passed 48/48, including 16 new Phase 3 tests. The final full-suite run completed with 2,442 passed, 2 skipped, and 0 failed (2,444 total; 8m32s). An earlier full run had one order-sensitive failure in `QueueEstimateSummaryUiTests.RapidQualityTargetChangesRefreshTheLatestEstimateGeneration`, which saw a missing V1 `_estimatedSizeMap` entry; that test then passed alone and the full UI test class passed 31/31. The final full suite passed after the localized audio source-rate refinement. The initial baseline full suite was interrupted without a summary and is not counted as a pass.

## Limitations and next integration boundary

Phase 3 supports `libx264`/H.264, `h264_nvenc`/H.264, `libx265`/HEVC, `hevc_nvenc`/HEVC, `libsvtav1`/AV1, and `av1_nvenc`/AV1—the current Phase 1 pairs. NVENC was exercised using the existing local Sintel output. A target whose production encoder or codec pair is unsupported is rejected using planner validation.

Accuracy is directional rather than laboratory-grade: errors in this small set range from 2.94% to 86.81% absolute, with a 10.18% median absolute percentage error. The estimator is appropriate for an advisory Estimated Output display and fast quality-change refresh. Keep automatic queue skipping/admission off until a later bounded calibration step demonstrates that the classifications are reliable on broader source and encoder shapes. Actual validated output bytes remain the final production fact.

No GUI, queue-admission, source-deletion, retry, adaptive sampling, Policy C, history, preview, or version behavior was changed for Phase 3.
