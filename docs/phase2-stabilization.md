# Phase 2: fast production estimation and real-media encoding

Validation date: 2026-10-04. Timings are observations on this Windows machine,
not service-level guarantees. See `phase2-stabilization-measurements.json` for
the numerical measurements. All sizes below use binary MiB; existing UI labels
still say MB.

## 1. Repository baseline

Verified before editing: branch `develop`; HEAD
`7c1e4a3cd2b899cf552d7aa0c800342227051cba`; `origin/develop`
`676d6a8582df504b3cfb3ce673c044d4aa8127d0`; ahead 2, behind 0; clean worktree;
version 1.8.0. Phase 1 commit `7c1e4a3` was present and was HEAD. Baseline
Release build: 0 warnings, 0 errors, 57.27 seconds. Baseline focused smoke:
112 passed, 0 skipped, 0 failed (`phase2-baseline.trx`).

## 2. Estimator architecture before changes

The bounded background worker read source size and metadata, resolved quality,
and calculated the arithmetic estimate. With historical calibration configured,
it then assembled cohorts, looked up historical records, evaluated calibration,
and could rediscover GPU identity. Recommendation evaluation also preceded
publishing the result. Normal queue empirical sampling was already contained
by Phase 1, but queue setup still constructed preview services and captured
execution settings that this normal path did not need.

## 3. Estimator architecture after changes

Normal queue analysis follows:

`metadata -> deterministic quality resolution -> arithmetic -> publish estimate`

Recommendation evaluation follows publication and produces a separate advice
result. Both results retain the existing generation, row ID, path, authority
revision, and active-job ownership checks before UI mutation. Advice results
cannot replace estimate values or provenance. Normal MainForm estimation
constructs no empirical provider or preview execution snapshot. Explicit
empirical service callers retain their existing opt-in path.

Settings-only quality updates reuse the analyzed row's metadata, size, and
duration snapshot. Reuse requires the same row ID and production revision.
Explicit queue analysis still validates source signatures. Start retains its
independent authoritative source and output safety checks.

### Exact arithmetic model

Implementation: `Services/SizeEstimateService.cs`, with quality resolution in
`Services/EncodingQualityPolicyService.cs`.

Let `S` be source bytes / 1,048,576, `D` duration seconds, and rates be decimal
kb/s. The corrected conversion constant is `K = 8,388.608`:

```
sourceTotalKbps = S * K / D
sourcePixelsPerSecond = sourceWidth * sourceHeight * sourceFPS
measuredSourceBpp = sourceVideoKbps * 1000 / sourcePixelsPerSecond
complexity = clamp(sqrt(measuredSourceBpp / nominalSourceBpp), 0.55, 1.65)
qualityScale = 2 ^ ((23 - effectiveQuality) / 12)
targetVideoKbps = outputWidth * outputHeight * sourceFPS
                  * nominalTargetBpp * profileScale * qualityScale
                  * complexity / 1000
subtotal = targetVideoKbps + plannedAudioKbps + mappedSubtitleKbps
targetTotalKbps = subtotal / 0.99, if 1% of that total is at least 16 kb/s
                 otherwise subtotal + 16
estimatedMiB = max(0.1, targetTotalKbps * D / K)
```

The old 8192 divisor treated decimal kb/s as binary kib/s, overestimating size
by 2.4% for a fixed bitrate. Both conversion directions and the shared direct
video replacement helper now use the correct constant.

All branches and inputs:

- Positive manual target size bypasses this profile calculation. `No
  Compression` returns source size. Invalid size/duration/geometry/FPS returns
  unavailable.
- Output height is the requested positive height or source height. Width is
  the rounded aspect-ratio width, minimum 2, rounded upward to even. Source FPS
  is retained in this projection.
- Nominal BPP: AVC 0.085; HEVC 0.055; AV1 0.045; MPEG-2 0.14; other 0.10.
  Encoder codec names select these assumptions.
- Measured audio/subtitle/data bitrates are clamped to total source bitrate.
  Missing subtitle and data rates use 8 kb/s per stream. Attachment contribution
  is attachment bytes * 8 / 1000 / duration. Data and attachments help derive
  source video but are excluded from the projected output; subtitle mapping
  remains an approximation until authoritative planning.
- Source container allowance is max(16 kb/s, 1% of source total). Measured video
  uses min(measured video, max(1, total minus ancillary)). Without measured video,
  measured audio is subtracted along with ancillary and container allowances;
  otherwise video uses max(1, 90% of total minus ancillary). Missing copied audio
  is inferred from the remaining source bitrate when audio streams exist.
- Planned audio is measured/inferred copied audio, or 192 kb/s per stream for
  requested channel counts below 6, 384 kb/s at 6 or above. The transcode branch
  multiplies by max(1, source audio stream count).
- Profile scales: Very High 0.95, High 0.85, Medium 0.75, Low 0.65, Very Low
  0.55. Existing verbose labels map to the same values. Enabled HEVC storage
  quality settings or a resolved quality intent under that enabled policy use
  scale 1. Ordinary Automatic with storage savings disabled retains its profile
  scale; it does not automatically use scale 1.
- Enabled HEVC storage bitrate mode uses source video bitrate * configured
  percentage / 100 instead of the BPP formula. Storage quality mode uses its
  configured quality unless a resolved quality target is supplied.
- Same efficient codec and unchanged geometry: an eligible Automatic source
  path with storage savings disabled caps projected video at measured source
  video bitrate. Legacy paths retain their existing source-video floor.
- Automatic quality starts at the selected provider's normalized default, then
  adds Smaller +6, Efficient +3, Balanced 0, High -3, Maximum -5. Source BPP
  below 0.035 adds 1; BPP at least 0.12 subtracts 2 except Balanced NVENC HEVC,
  which adds 1. Source height at least 2160 subtracts 1; downscaling adds 2.
  The provider normalizes the final value and selects CQ/CRF/ICQ syntax. Numeric
  intent remains normalized without Automatic adjustments. An explicit target
  size supersedes constant quality.
- Bit depth, encoder preset, and hardware performance have no separate numeric
  correction in this fast model. Provider selection affects codec and quality
  defaults; it requires no runtime hardware discovery during recalculation.
  History contributes no coefficients on the normal queue path.

This is a technically coherent, deterministic immediate projection. It is a
simplistic constant-quality model and does not justify an accuracy guarantee.
The real outputs below show its limits, especially for short high-bitrate clips.

## 4. Historical-calibration critical-path findings

Configured calibration previously delayed first publication despite being
optional. The normal worker now does no cohort search, historical accuracy
evaluation, historical direct substitution, calibration refinement, or GPU
identity discovery. It records arithmetic provenance with zero calibration
samples and no applied correction. Existing constructor/config arguments remain
compatible; enabling the old calibration flag cannot reintroduce that dependency.
No new asynchronous calibration state machine was added. Historical reporting
and explicit research services remain available independently.

## 5. Plan-history findings and fresh source probing

Every ordinary `EncodingPlanService.Create` previously constructed an
`EncodingStatisticsService` and loaded `encoding-statistics.jsonl`. Loading
walked the entire existing journal; the 3000-record retention bound was enforced
during append, not as a bounded read. Malformed lines were skipped. Forecasts
covered output ratio/size and speed/duration; they did not supply configured
CQ, deterministic quality resolution, or plan validity. Empty history produced
no cohort; failures produced `HistoryUnavailable`, after incurring the blocking
load attempt. Slow history necessarily delayed plan publication.

Plan creation now composes metadata, user settings, provider capability,
geometry/container rules, and deterministic quality without loading the journal.
`Estimates.HistoricalPrediction` is null. Optional recommendation failure remains
caught after creating an available plan. Existing source-adaptive shadow
arithmetic remains observational and cannot adjust execution quality.

The encode-time fresh structured FFprobe was retained. Queue `MediaInfo` has
lightweight aggregate fields but lacks stream indexes/dispositions, aspect ratio,
time bases, and the rich stream/timing facts needed for safe mapping and container
validation. Its size/mtime cache also does not prove the source was unchanged
throughout the probe. Replacing authoritative probing with that cache would
weaken correctness. Normal queue cache hits already reuse unchanged length/mtime;
the new settings snapshot is advisory and is not an execution cache.

## 6. Quality-change dependency findings

For all five preferences, normal warm recalculation uses cached metadata and
current settings. It performs no source stat/read/hash, FFprobe/FFmpeg, sampling,
history query, candidate search, hardware rediscovery, or preview snapshot/preset
file load. Recommendation executes after the estimate is ready. Existing
debounce is 75 ms (previously 250 ms); the existing UI result pump is 100 ms.
These explain part of visible latency; backend work was independently measured.

## 7. Code changes

Removed historical work from normal estimation and plan creation; separated
estimate and advice publication; reused analyzed metadata for quality changes;
removed unused preview setup/snapshot work; shortened debounce; corrected decimal
bitrate/binary-size units; prevented Review/Unavailable/Pending-only advice from
opening the configured Start warning. Explicit Skip/remux advice retains its
existing warning. Added small opt-in Release diagnostics through
`MEDIAFLUX_TIMING_LOG`; logging failure cannot alter production behavior.

Source existence, runtime availability, output paths/overwrite, stream/container
compatibility, timing classification/recovery, FFmpeg launch/exit, staged output
validation, promotion, and final verification retain their established checks.
Preview/advice cannot satisfy production sampling, storage acceptance, retry, or
source-deletion authority.

## 8. Exact files changed

- `MainForm.EncodeRunner.cs`
- `MainForm.EncodingPlan.cs`
- `MainForm.Estimates.cs`
- `MainForm.QueueInspector.cs`
- `MainForm.Recommendations.cs`
- `MainForm.cs`
- `Services/CriticalPathTiming.cs` (new)
- `Services/EncodingPlanService.cs`
- `Services/EncodingService.cs`
- `Services/EstimateBackgroundService.cs`
- `Services/FfprobeService.cs`
- `Services/MediaInfoService.cs`
- `Services/SizeEstimateService.cs`
- `MediaFlux.Tests/EmpiricalSizePreviewTests.cs`
- `MediaFlux.Tests/EncodingPlanServiceTests.cs`
- `MediaFlux.Tests/ProductionDirectOutputBitrateEstimatorTests.cs`
- `MediaFlux.Tests/QueueEstimateOwnershipTests.cs`
- `MediaFlux.Tests/QueueFilteringUiTests.cs`
- `MediaFlux.Tests/QueueInspectorUiTests.cs`
- `MediaFlux.Tests/QueueSelectionResponsivenessUiTests.cs`
- `MediaFlux.Tests/SizeEstimateServiceTests.cs`
- `MediaFlux.Tests/SmartEncodeDecisionServiceTests.cs`
- `docs/phase2-stabilization.md` (new)
- `docs/phase2-stabilization-measurements.json` (new)

## 9. Cold-cache timing results

Actual visible MainForm, Release build, copied existing video assets. Temporary
host only redirects process storage to an isolated root; it runs the ordinary
MainForm message loop and actual queue/quality/Start controls. It does not invoke
a headless encode API. Delete source was unchecked, restoration Off, storage
savings disabled. Paired existing FFmpeg/FFprobe 8.1.2; NVIDIA RTX 4090.

Cases: D = Dad2.mp4, H.264 720x1080, 30 FPS, 5 s, no audio; I =
IranNewsFlash.mp4, H.264 854x470, 29.97 FPS, 31.431 s, copied AAC; V =
video_241030143952_v182d_1080.mp4, H.264 608x1080, 30 FPS, 31.5 s video,
copied AAC. H = HEVC-copied-audio.mp4, real promoted HEVC Main 10 output copied
as a new source, 854x470, 29.97 FPS, 31.465 s, AAC. A landscape 1920x1080 asset
was not readily found; portrait files have 1080-pixel height.

The existing codec filter probes during discovery, before row insertion.
Consequently FFprobe start/end precede the recorded queue-file-accepted event.
The table preserves that ordering. Absolute times are UTC on 2026-10-04.

| Case | FFprobe start/end | Probe ms | Row accepted | Metadata ready | Estimate ready | Row updated |
|---|---|---:|---|---|---|---|
| D | 15:39:04.370 / .419 | 50 | 15:39:04.657 | 15:39:04.681 | 15:39:04.685 | 15:39:05.234 |
| I | 15:39:04.439 / .477 | 38 | 15:39:04.659 | 15:39:04.680 | 15:39:04.685 | 15:39:05.223 |
| V | 15:39:04.494 / .531 | 37 | 15:39:04.660 | 15:39:04.680 | 15:39:04.685 | 15:39:05.235 |
| H | 15:49:10.840 / .877 | 37 | 15:49:11.448 | 15:49:11.514 | 15:49:11.515 | 15:49:11.715 |

Metadata-to-ready: D 4.566 ms, I 4.595 ms, V 4.659 ms, H 0.625 ms.
Accepted-to-visible: D 577.317 ms, I 563.481 ms, V 574.957 ms, H 266.557 ms.
First cold computations include JIT: 3.360–4.015 ms for D/I/V. H 0.176 ms.
No empirical preview or historical calibration ran. The HEVC measurement was
during a focused test run; D/I/V repeat was with the test suite idle.

## 10. Warm-cache timing results

Actual Analyze Queue action, no additional FFprobe. Times below start at
metadata-ready, not at the original import event.

| Case | Metadata ready UTC | Estimate ready after metadata, ms | Row updated after metadata, ms |
|---|---|---:|---:|
| D | 15:41:19.453 | 1.849 | 79.397 |
| I | 15:41:19.453 | 0.950 | 67.058 |
| V | 15:41:19.454 | 1.066 | 78.595 |
| H | 15:50:24.426 | 0.960 | 115.084 |

## 11. Quality-change timing results

Actual slider ValueChanged events. Start/ready/display are milliseconds from
the user/config change; Work is the separately timed arithmetic/quality work.
The JSON contains each change's absolute UTC. Every listed recalculation used
the warm snapshot. No additional probe occurred during either five-step sweep.
All computation values are below the 50 ms/row engineering target.

| Case | Quality | Recalculation start ms | Ready ms | Row updated ms | Work ms |
|---|---|---:|---:|---:|---:|
| D | Smaller | 188.20 | 188.89 | 231.52 | 0.611 |
| D | Efficient | 100.24 | 100.85 | 142.51 | 0.918 |
| D | Balanced | 103.20 | 103.80 | 182.35 | 1.179 |
| D | High | 89.67 | 90.26 | 169.07 | 0.627 |
| D | Maximum | 106.19 | 107.07 | 150.99 | 0.580 |
| I | Smaller | 188.06 | 188.62 | 217.41 | 0.837 |
| I | Efficient | 100.45 | 101.72 | 142.81 | 0.649 |
| I | Balanced | 102.94 | 103.62 | 170.53 | 0.675 |
| I | High | 89.52 | 90.50 | 156.14 | 0.186 |
| I | Maximum | 106.06 | 106.54 | 137.28 | 0.169 |
| V | Smaller | 188.42 | 189.37 | 231.85 | 0.574 |
| V | Efficient | 100.66 | 101.22 | 142.98 | 0.630 |
| V | Balanced | 102.79 | 103.43 | 170.17 | 0.177 |
| V | High | 90.07 | 91.08 | 169.39 | 0.204 |
| V | Maximum | 106.87 | 107.41 | 151.31 | 0.550 |
| H | Smaller | 204.11 | 204.61 | 354.74 | 0.634 |
| H | Efficient | 187.84 | 188.48 | 446.38 | 0.671 |
| H | Balanced | 126.01 | 126.47 | 335.61 | 0.557 |
| H | High | 154.33 | 154.74 | 308.86 | 0.687 |
| H | Maximum | 104.95 | 105.54 | 186.29 | 0.699 |

Instrumentation uses serialized opt-in file writes, so these diagnostic runs
include that small overhead. Visible update timing includes debounce, UI pump,
and presentation/layout; it is not estimator CPU time.

## 12. Encode-start timing results

Actual Start full queue at 15:41:34.403510 UTC. Plans were available for all
three rows. Balanced Automatic, HEVC NVENC p5, 10-bit, source geometry retained.

| Case | Resolved CQ | Plan ready UTC | Start-to-plan s | Job-start-to-plan s | First FFmpeg UTC | Start-to-FFmpeg s |
|---|---:|---|---:|---:|---|---:|
| D | 25 | 15:41:35.583102 | 1.180 | 0.836 | 15:41:35.740631 | 1.337 |
| I | 24 | 15:41:35.286817 | 0.883 | 0.540 | 15:41:35.693205 | 1.290 |
| V | 25 | 15:42:01.293246 | 26.890 | 0.762 | 15:42:01.657246 | 27.254 |

V waited for a dispatch slot while D/I encoded and verified; its dispatch was
15:42:00.525018 and dispatch-to-FFmpeg was 1.132 seconds. Job-start-to-plan
includes required preflight and probing; it is not an isolated measurement of
the in-memory Create call. I's successful existing fallback FFmpeg started at
15:41:35.892505, 1.489 seconds after Start. Process launch itself took 5–6 ms.

## 13. Real-media encode results

All three actual production paths reached staged validation, promotion, and
essential final FFprobe verification. Terminal production FFmpeg exit was 0.
History records are `ValidatedAndFinalized`, statistics outcome Success, and the
visible diagnostics reported 3 successful, 0 failed, 0 finalization failed.
Both AAC streams were copied. All source copies remain present with their
original recorded byte sizes; original assets were never encoded in place.

D and V completed on the primary path. I's initial CUDA frame conversion failed
with `Impossible to convert between the formats supported by the filter`.
The established classified GPU-frame fallback retried with software-frame
conversion while retaining NVENC, CQ24, and strict output validation. This was
a tool/frame negotiation issue handled by the existing production recovery,
not an optional research failure or weakened safety gate.

| Case | Output bytes | Production attempts | Staged verification s | Promotion s | Final FFprobe s |
|---|---:|---:|---:|---:|---:|
| D | 2,199,168 | 1 | 22.856 | 0.010 | 0.092 |
| I | 2,994,287 | 2 | 22.239 | 0.010 | 0.105 |
| V | 6,151,155 | 1 | 17.339 | 0.541 | 0.059 |

Evidence retained locally under
`C:\Users\ghirschberg\AppData\Local\Temp\MediaFlux-Phase2-20261004`:
`timing.log`, `UserData/data/history.jsonl`, `encoding-statistics.jsonl`, and
`history-logs/{c9810a13f2d940349098944f7c67aeba,9ad87cb0173745038ed4cd7b4845ed01,02accd6421834b8fa1369561fe4e45c9}.log`.
Promoted files are in `output`. The pre-disruption run is preserved separately
as `interrupted-timing.log`; it is not counted as a successful encode.

## 14. Predicted versus actual size

Signed error = 100 * (predicted - actual) / actual. Negative means underprediction.

| Case | Prediction bytes (MiB) | Actual bytes (MiB) | Signed error | Absolute error |
|---|---:|---:|---:|---:|
| D | 894,084 (0.853) | 2,199,168 (2.097) | -59.34% | 59.34% |
| I | 3,254,763 (3.104) | 2,994,287 (2.856) | +8.70% | 8.70% |
| V | 5,015,709 (4.783) | 6,151,155 (5.866) | -18.46% | 18.46% |

D/I values are frozen queue predictions persisted in statistics. V's statistics
prediction fields were absent; the exact listed value is the existing
deterministic plan shadow projection, corroborated by its pre-encode log and
the queue's displayed 4.8 MiB estimate. This discrepancy is reported explicitly.
These observations do not establish broad prediction accuracy or authorize
production acceptance/deletion from projected bytes.

## 15. Recommendation-path validation

Real queues displayed usable estimates for H.264 and HEVC; HEVC advice was
Review. All three actual production plans were Review with insufficient reliable
size evidence, yet they launched and completed. Tests additionally block the
recommendation evaluator, observe an already published positive estimate, then
throw an optional failure and verify a separate Unavailable advice result.
Plan recommendation failure retains an available deterministic plan. The actual
STA UI test invokes Start review with Review/Unavailable/Pending rows and the
warning configured, and verifies EncodeAll without a modal. Explicit Skip/remux
warnings remain intact. No recommendation UI redesign was undertaken.

## 16. Tests and build

Final-source Release validation: focused inspector/queue-selection tests 4
passed, 0 failed, 0 skipped; broader Phase 2 regression set 131 passed,
0 failed, 0 skipped; full suite 2,372 passed, 0 failed, 2 skipped (2,374 total,
0 timeouts); Release solution build 0 warnings and 0 errors. The two skips are
explicit opt-in acceptance tests for Policy C retry and historical preview.
Full-suite TRX: `MediaFlux.Tests/TestResults/phase2-full-final-preview.trx`.

Regression coverage includes blocked/failed advice with immediate arithmetic,
zero history evidence, five cached quality changes even when the source can no
longer be read, rejection of cached metadata for another row/revision, no-history
plans, recommendation failure, and generation/production ownership containment.
Bitrate fixtures were updated to decimal kb/s and binary MiB.

## 17. Remaining performance bottlenecks

- Actual staged output verification took 17.3–22.9 seconds on clips whose FFmpeg
  encode elapsed under about 1.2 seconds. This is the largest observed end-to-end
  cost; source/output decode integrity checks must remain authoritative.
- Existing import codec filtering probes files serially before inserting rows.
  Three-file import stayed usable, but cold visible updates lagged estimate
  publication by roughly half a second. No large-import scale claim is made.
- UI debounce/pump and inspector presentation account for visible updates beyond
  the sub-2-ms arithmetic work. Full UI test activity increases that variability.
- Recommendation is pure metadata work after publication, but still shares the
  bounded worker pool; slow future advice evaluators could delay later files.
- Statistics still load during application/service startup and support reporting;
  removal here is specifically from each estimate and normal plan creation.

## 18. Remaining functional risks

The fast CQ model can substantially underpredict output. Recommendation confidence
and estimate provenance presentation still have legacy wording; production plans
without history tend to Review. Advice remains advisory. Settings-only snapshots
intentionally do not reread changed/deleted sources; explicit analysis and Start
retain freshness checks. Snapshot storage lasts for the service lifetime and may
grow with distinct analyzed paths. Queue cache length/mtime alone is insufficient
for replacing structured execution probes. V showed a missing persisted queue
prediction field. An earlier run had one intermittent STA timeout; the final
full Release run completed with no failures or timeouts.
Landscape 1080p, long files, and other providers remain outside these real-media
observations. No optional calibration benefit is retained on the normal first
estimate path. Existing explicit manual-target bitrate paths were not redesigned.

## 19. Recommended Phase 3 tasks, ordered by impact

1. Profile staged output integrity verification by substage; remove redundant
   work only with equivalent validation coverage and real output evidence.
2. Batch import presentation and bounded concurrent codec discovery; measure
   larger queues and retain stale-result/ownership checks.
3. Clarify arithmetic provenance and recommendation confidence/Review wording;
   preserve advice versus production acceptance authority.
4. Improve arithmetic prediction using a small representative validation set,
   especially short/high-bitrate scenes and provider/preset differences, without
   adding first-estimate sampling or history dependencies.
5. Repair missing persisted prediction fields and improve startup timing
   correlation; bound snapshot retention and examine remaining startup journal IO.
6. Investigate intermittent existing STA/retry tests using deterministic lifecycle
   evidence, without adding sleeps or increasing timeouts as a substitute.

## 20. Commit

One focused local commit is authorized only after all success criteria pass.
The final chat provides its hash and message. Version remains 1.8.0; no push,
tag, or release is authorized.

## 21. Worktree and publication boundary

The validated changes are committed together in one local stabilization commit.
The final commit hash and clean worktree status are reported in the task result.
Version remains 1.8.0. Nothing was pushed, tagged, or released. No persistent
user storage pointer or existing campaign data was changed.

## 22. Final post-inspector-fix GUI and encode validation

The queue/inspector corrections were rebuilt into the visible Release validation
host. The three-file queue remained interactive during cold import, warm
reanalysis, and quality changes. The first estimates were published before
recommendation completion. These post-fix results supersede the earlier quality
and inspector probe-count observations above.

Cold queue import, 2026-10-04 UTC:

| Source | FFprobe start/end | Probe | Accepted | Metadata ready | Estimate ready | Visible |
|---|---|---:|---|---|---|---|
| Dad2.mp4 | 16:36:56.979 / 16:36:57.030 | 51 ms | 16:36:57.246 | 16:36:57.266 | 16:36:57.270 | 16:36:57.516 |
| IranNewsFlash.mp4 | 16:36:57.052 / 16:36:57.086 | 34 ms | 16:36:57.247 | 16:36:57.266 | 16:36:57.271 | 16:36:57.517 |
| video_241030143952_v182d_1080.mp4 | 16:36:57.100 / 16:36:57.134 | 34 ms | 16:36:57.247 | 16:36:57.266 | 16:36:57.270 | 16:36:57.517 |

Probe discovery completes before each row is accepted. The first 720x1080 H.264
estimate was ready 24 ms after acceptance and visible 271 ms after acceptance;
the other two were ready in 23–24 ms and visible in 269–270 ms. Cold arithmetic
work, including JIT, was 3.476–3.959 ms. Metadata came from the just-completed
discovery cache (`metadataMs=0`).

Warm reimport, 16:45 UTC: Dad2 metadata/estimate/display followed acceptance by
23/27/276 ms; IranNewsFlash by 21/25/255 ms. Both reused valid metadata without
FFprobe. The copied V source had its last-write time changed before reimport;
the expected identity refresh probed it once (16:45:07.099–.138), before row
acceptance, then published metadata/estimate/display 21/25/274 ms after
acceptance. A separate warm Analyze Queue pass performed no new probes; estimate
work was 0.183–1.203 ms, with UI-visible updates around 475 ms after metadata
ready while the queue was being pumped.

Warm quality changes used one loaded three-file queue. Each click recalculated
all three rows from cached snapshots (`metadataMs=0`, `warm=True`). For Dad2:

| Quality | Change UTC | Recalculation start | Estimate ready | Row visible | Arithmetic work |
|---|---|---:|---:|---:|---:|
| High | 16:46:14.137 | +95 ms | +96 ms | +187 ms | 0.693 ms |
| Maximum | 16:46:50.889 | +87 ms | +88 ms | +184 ms | 0.275 ms |
| Balanced | 16:47:11.016 | +106 ms | +107 ms | +201 ms | 0.466 ms |

No FFprobe, FFmpeg, empirical sampling, calibration, hardware rediscovery, or
history/database lookup occurred on these quality changes. The bounded 75 ms
debounce and 100 ms UI result pump account for much of the visible delay. The
inspector used cached stream details for an unchanged file; after the copied
source identity changed, it reported metadata as not cached and explicitly
stated that selecting the inspector tab would not start a probe. No inspector
probe was logged. The output preview for Auto now marks the extension as
resolved before start rather than guessing `.mp4`.

The final production encode used the copied Dad2 H.264 source (720x1080, 30 FPS,
5 seconds, no audio), Balanced quality, NVENC HEVC Main10 p5, resolved CQ25.
The Review recommendation did not block Start. Start Encode was 16:48:57.419;
the plan was ready at 16:48:58.579 (+835 ms), and FFmpeg launched at
16:48:58.902 (+1.483 s). FFmpeg exited 0 at 16:48:59.355. Staged output and
beginning/middle/end decode validation passed; final output FFprobe validation
passed and the staged file was promoted. Predicted size was 894,084 bytes
(0.853 MiB); actual size was 2,199,168 bytes (2.097 MiB), signed error
`(predicted-actual)/actual` = -59.34% (actual was 2.460x prediction). The source
copy was retained. This confirms the workflow while showing that the simple
five-second high-bitrate projection can materially underpredict.

Latest detailed timing log and isolated output/statistics are under
`C:\Users\ghirschberg\AppData\Local\Temp\MediaFlux-Phase2-20261004`.
