# Encoding Engine V2 queue runner

Phase 2 adds a sequential queue coordinator above the Phase 1 fixed-plan core. The queue runner delegates every item to `V2EncodingCore`; it does not probe, plan, encode, validate, promote, or delete media itself. Only one item can execute at a time.

## Headless command

Run a queue with:

```text
MediaFlux.exe --v2-run-queue queue.json
```

The command runs before WinForms startup, uses the same V2 core as the other headless V2 commands, writes one machine-readable result object to stdout, and writes structured queue events to stderr. An optional JSONL event log is created with `CreateNew`; its directory must already exist and its path must not overlap the request, a source, or an output.

## Request

Schema version 1 contains a stable `queueId`, unique stable `itemId` values, and an existing V2 request nested under each item. V2 media paths and encoder settings remain item-owned.

```json
{
  "schemaVersion": 1,
  "queueId": "sintel-short-20261005-a",
  "eventLogPath": "P:\\MediaFluxRuns\\sintel-short.events.jsonl",
  "items": [
    {
      "itemId": "copy-a",
      "request": {
        "schemaVersion": 1,
        "jobId": "4ab3e32c-03e7-4722-8150-40e09ae4b14f",
        "sourcePath": "P:\\MediaFluxRuns\\copy-a.ogv",
        "outputPath": "P:\\MediaFluxRuns\\copy-a.mp4",
        "encoder": "libx264",
        "codec": "h264",
        "container": "mp4",
        "qualityMode": "crf",
        "qualityValue": 28,
        "preset": "ultrafast",
        "pixelFormat": "yuv420p",
        "geometryMode": "fitBox",
        "maxWidth": 640,
        "maxHeight": 360,
        "audioMode": "aac",
        "audioBitrateKbps": 128,
        "subtitleMode": "none",
        "deleteSourceOnSuccess": false,
        "ffmpegPath": "C:\\MediaFlux\\FFmpeg\\bin\\ffmpeg.exe",
        "ffprobePath": "C:\\MediaFlux\\FFmpeg\\bin\\ffprobe.exe"
      }
    }
  ]
}
```

The queue ID and item IDs are required. Each item keeps the Phase 1 V2 request schema. Per-item `eventLogPath` is not used; queue events belong to the queue envelope.

## Result and lifecycle

The result includes queue start/end UTC timestamps, terminal status, item counts, warning count, continuation-after-failure, per-item timing, aggregate queue timing, all queue events, and each original `V2ExecutionResult` (including its exact V2 events and stage timings).

Queue states are `Queued`, `Running`, `Completed`, `CompletedWithFailures`, `Canceled`, and `Failed`. An ordinary item failure is recorded on that item and later items still run. Queue cancellation stops the active V2 item through its cancellation token, then marks all not-started items `PendingAtCancellation`. A source-deletion warning leaves the item completed and the valid output in place.

Each queue event has a monotonically increasing queue sequence, UTC timestamp, queue ID, event type, optional item ID/ordinal, terminal classification, and progress snapshot. `ItemLifecycle` embeds the original V2 lifecycle event without rewriting it. Event types include queue start/completion/cancellation, item start, item lifecycle, item terminal state, and pending-at-cancellation. The frozen FFmpeg invocation requests machine-readable progress on stderr once per second; V2 parses frame, speed, output time, bitrate, and size into advisory lifecycle events. This progress does not change the frozen plan or output acceptance.

Per-item timing combines queue wait, inter-item dispatch latency, and active item duration with the nested V2 timings for probe, plan, request-to-process-start, FFmpeg, validation, promotion, cleanup, and total item duration. Queue timing reports total elapsed, aggregate FFmpeg time, orchestration overhead, and average/maximum inter-item dispatch latency.

Exit codes are 0 for `Completed`, 1 for `CompletedWithFailures`, 130 for `Canceled`, and 2 for rejected/failed queue requests.

## Failure certification

Deterministic injection is restricted to the explicit command:

```text
MediaFlux.exe --v2-run-queue-certification certification.json
```

Its JSON has the same queue request plus an explicit certification block. A normal queue request containing this block is rejected, and `V2EncodingQueueRunner` disables it unless the caller opts into certification mode.

```json
"certification": {
  "certificationId": "phase2-middle-failure-r1",
  "failureInjections": [
    { "itemId": "copy-c", "point": "productionExecution" },
    { "itemId": "copy-e", "point": "validation" }
  ]
}
```

Supported points are `beforeProduction`, `productionExecution`, `validation`, `promotion`, and `sourceDeletion`. Injection wrappers sit at V2 dependencies, so validation/promotion/deletion safety remains owned by the single-item V2 core. The result includes configured/triggered evidence per injection. Production injection runs no FFmpeg command and never changes an FFmpeg binary or media source.
