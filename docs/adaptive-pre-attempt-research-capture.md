# Adaptive pre-attempt research capture

`Config.AdaptivePreAttemptResearchCapture.Enabled` is a separate research-only
opt-in, default `false` for fresh, absent, null, and legacy configurations. It has
no Settings UI. `StorageSavings.ExperimentalPolicyCRetryEnabled` does not activate
it, and capture does not activate retry or Storage Savings. Configure it only in
the preregistered isolated research config; this feature does not select or activate
a storage root.

The visible MainForm saved/scheduled-job route captures these options and the
resolved `AppPaths.UserDataDirectory` through its existing execution settings and
snapshot builder. The observer runs in that same EncodingService, after adaptive
selection and the frozen plan callback, before the first production FFmpeg call.
Ordinary execution has a null capture callback, creates no observer/files, and
performs no additional source-identity reads. Enabled operations without a frozen
adaptive request/selection fail closed. A skipped adaptive selection is also
recorded after its plan callback and before the existing skip exception; no
production attempt is launched.

Optional configuration fields (PascalCase as in existing config.json):

```json
{
  "AdaptivePreAttemptResearchCapture": {
    "Enabled": false,
    "CaptureRevision": "adaptive-pre-attempt-v1",
    "ImplementationId": null,
    "ExecutableSha256": null,
    "FfmpegSha256": null,
    "FfprobeSha256": null,
    "ResearchConfigSha256": null,
    "Source": null
  }
}
```

`Source`, when supplied for a one-source job, contains `CanonicalPath` and optional
`CampaignCaseId`, `StableSourceId`, `Sha256`, `ExpectedByteLength`,
`ExpectedLastWriteUtc`. Path/length/time binding is validated when the execution
attempt snapshot is created. Source path, length, and last-write UTC are frozen
then. Hashes and campaign IDs are supplied preregistration facts, not independently
verified claims by this observer. No media, executable, tool, or config hash is
calculated in the callback. Source changes detected by the supplied binding fail
before execution. Capture options are immutable records; they do not enter the
planner, selector, FFmpeg arguments, or predictor.

## Schema 1

Publication path: `<active UserData>/data/policy-c-pre-attempt/<operation-id-N>.json`.
GUID N/D operation IDs share a normalized N filename. Each file contains camelCase
UTF-8 JSON with fixed property order, string enums, existing numeric precision,
and original candidate/sample/stream order:

- `schemaVersion`, `captureRevision`, `operationId`, optional `savedJobId` and
  `queueRowId` (MainForm's stable QueueSequence).
- `source`: canonical path, byte length, last-write UTC, optional campaign case,
  opaque source ID, supplied SHA-256.
- `capturedUtc` and `runtime`: MediaFlux version, supplied implementation and
  executable/FFmpeg/FFprobe/research-config identities, resolved research root.
- `contract`: the exact resolved StorageSavingsContract, including applies,
  source denominator, percentage/absolute minimum, maximum accepted bytes, reason.
- `envelope`: exact encoder, target, mechanism, preferred quality, maximum
  compression quality, maximum increase, absolute cap, preferred-above-cap.
- `video`: exact AdaptiveVideoSettings, including encoder, GPU, preset, TenBit
  intent, concurrent sessions, source/requested/planned geometry and output pixel
  format, source pixel format, scale, container, and color metadata.
- `sourceDuration`, `restoration`, `streamPlan`: frozen plan ID, source/video,
  container/hardware, audio/subtitles, effective map/copy settings, complete
  resolved ContainerDecision including data/attachment/omitted stream actions.
- `selection`: exact AdaptiveQualitySelectionEvidence, including disposition,
  encoder/codec/target/mechanism/envelope values, selected quality, reason, sampling
  seconds, ancillary audio/subtitle/data/attachment/stream/minimum-stream
  allowances, ordered candidates with quality/projections/container allowance/
  classification and ordered samples with label/start/duration/video bytes/
  measured seconds. The selected candidate is identified by SelectedQuality in
  that original candidate list. No eligibility or classifications are recomputed.
- `adaptiveStorageSavingsEnabled`, `experimentalPolicyCRetryEnabled`,
  `researchCaptureEnabled`, `ordinaryAdaptiveApplicable`,
  `productionAttempt1Started=false`, `productionAttemptCount=0`.

Natural Policy C trigger, Phase 1 actual bytes, technical validation, retry launch,
and post-attempt retry eligibility are absent: they are not yet determined. Terminal
processing never backfills or modifies the file.

## Durability and acknowledgment

The sink uses the existing immutable freeze-store publication pattern:
same-directory unique temporary file, CreateNew, WriteThrough, UTF-8 bytes,
asynchronous flush followed by `Flush(flushToDisk: true)`, close, atomic
`File.Move(overwrite: false)`. Existing/concurrent records cause explicit failure,
even if identical. Only the invocation's unpublished temporary file is cleaned up.
Construction does not create directories. File identity syntax cannot escape the
fixed data subdirectory. This is application-level immutable publication; there
is no later update API.

Acknowledgment contains success, final path, SHA-256 of the canonical persisted
bytes, capture UTC, serialization seconds, durable-write seconds, total seconds.
The attempt retains it, and MainForm's existing diagnostic log records all fields.
Timings are acknowledgment/diagnostic facts, not self-referential fields in the
hashed evidence JSON and never inputs to selection. Failure or a negative/missing
acknowledgment throws `AdaptivePreAttemptEvidenceCaptureException`, retains the
source, reports `NotRun` with zero production attempts, and prevents FFmpeg/retry/
recovery. MainForm retains its existing single terminal history/statistics append
but uses `ResearchEvidenceCaptureFailed`, with no encode-defect analysis or
automatic queue retry. This is a research durability failure, not Phase 1 rejection.

Any campaign activation manifest frozen against the preceding implementation is
unusable after this change. A later revision must freeze new HEAD, executable and
config hashes, opt-in, schema/capture revision, source provenance/pool/order, job,
and runtime hashes. This implementation does not enroll sources, activate research
storage, authorize encodes, or make the campaign ready.
