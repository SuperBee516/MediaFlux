# Headless saved-job execution

MediaFlux can preflight or execute exactly one persisted saved-job item without
opening WinForms, running the scheduler, or starting the updater:

```text
MediaFlux.exe --preflight-saved-job-item <job-id-guid> --item <selector> [--user-data <absolute-directory>]
MediaFlux.exe --run-saved-job-item <job-id-guid> --item <selector> [--user-data <absolute-directory>]
```

The selector remains `path=<absolute-source-path>` or
`experiment=<id>,slot=<positive-number>[,attempt=<positive-number>]`. Both the job ID
and the item must match exactly one persisted record. The optional `--user-data`
pair must follow the selector. Unknown, malformed, repeated or incomplete options
are rejected. `--user-data` is supported only with these headless commands; using it
with GUI/Explorer arguments fails before GUI or updater startup.

Use the executable from an installed distribution or a current Release build with
its normal runtime dependencies and configured media tools. An installation is not
required: headless dispatch occurs before Velopack startup. The existing mutex still
prevents overlap with another MediaFlux GUI/headless invocation, including an
invocation using a different UserData root.

## Process-scoped UserData

With `--user-data`, the directory is authoritative for that command's lifetime. It
must be an absolute, valid, existing directory containing a prepared tree:

```text
<isolated-root>/config.json
<isolated-root>/data/encode-jobs.json
```

The caller prepares these files using the existing `Config` and `EncodeJobService`
formats/APIs. Serialize a fresh `new Config()` to obtain current normal defaults;
an abbreviated legacy config can intentionally receive compatibility defaults for
absent fields. Supply complete saved-job primitives, as ordinary headless execution
already requires. A source path and a pre-existing absolute output directory belong
to the saved job/item, not to additional CLI quality or encoder switches.

MediaFlux does not create a missing isolated root, populate missing config/job files,
clone private normal data, migrate install-folder data, or rewrite either required
file. Missing, inaccessible, malformed or null required state fails closed. An empty
job array is valid state but cannot select an item. Optional history, statistics,
research and cache files can be absent; the existing services retain their usual
empty-history/abstention behavior and create their owned artifacts only when needed.

Precedence is:

1. Explicit process-scoped root, when supplied. Neither the normal
   `storage-location.json` nor a pointer placed inside the isolated tree is followed.
   Persistent pointer writes are prohibited in this mode.
2. Otherwise the existing normal storage-pointer behavior, including its existing
   fail-closed handling of an invalid pointer and default-root behavior for a genuinely
   missing pointer.

An explicit isolation failure never falls back to normal UserData. No process-scoped
selection is persisted. Subsequent ordinary launches use their original location.
GUI initialization/migration is prohibited while an isolated headless scope is active.
Without the option, config loading, saved-job lookup, relocation, GUI initialization,
backups, updater startup and ordinary headless behavior retain their existing paths.

Startup stdout identifies the active root, mode, config, job store, statistics, logs
and runtime-temp location. It explicitly identifies isolated mode and ignored storage
pointers. These are automation diagnostics; retain them privately when paths are
sensitive. Existing snapshot/source/output diagnostics still use their established
path conventions.

## Persistence and runtime boundary

| State | Isolated headless behavior |
| --- | --- |
| Configuration | Required `<root>/config.json`, same defaults and normalization; read only at startup. |
| Saved jobs/items | Required `<root>/data/encode-jobs.json`; same unique selection and immutable execution snapshot; no scheduler or job-store updates. |
| Statistics and adaptive-selection evidence | `<root>/data/encoding-statistics.jsonl`; estimate/calibration history and terminal records use this root. |
| Source Adaptive prediction/calibration | Reads isolated statistics through both the pipeline and static `EncodingPlanService` fallback; no normal-history seeding. |
| Prediction-shadow research | Isolated `data/prediction-shadow-observations.jsonl`, its `.oldN` generations, and isolated statistics generations. Existing adaptive-selection research exclusions are unchanged. |
| Assigned experiment freezes | Isolated `data/research-experiment-freezes`; normal immutable-validation and assignment rules still apply. No freezes are copied or manufactured at startup. |
| History | `AppPaths.HistoryFile` resolves to isolated `data/history.json`; the current headless pipeline does not instantiate `HistoryService` or append job history. If used, its JSONL and history logs resolve under isolated `data`. |
| Diagnostics | Central error logs and failure artifacts resolve under isolated `data/logs`; existing stdout/stderr diagnostics remain available. |
| Sample temporary files | Isolated `temp/AdaptiveSamples` and `temp/PredictionShadow`; owned operation directories keep existing cleanup behavior. Ordinary runs retain their OS-temp locations. |
| AI restoration | Isolated `data/ai-intermediates`, `data/tensorrt-engines`, `data/ncnn-performance-tuning.json` and `data/ai-benchmarks.db`, plus other existing `AppPaths` runtime paths. |
| Media-info disk cache | Disabled by the existing headless composition; no normal or install-folder media-info cache is loaded or written. |
| Backups | No backup service or migration runs. The isolated backup path, if queried, is `<root>/Backups`; ordinary GUI external-backup layout is unchanged. |
| Updater/install migration | Not invoked by headless startup. Shared executable/tool/model resources remain installation-scoped. |
| Media and output artifacts | Use the item's source and saved output folder. Staged outputs and recovery artifacts retain their normal output-folder behavior. Source deletion remains an explicit saved-job primitive and is still guarded by finalization/contract evidence. |

The directory is a persistence boundary, not a filesystem sandbox. The caller must
choose a genuinely separate prepared tree, without aliases into normal UserData.
Explicit media, output, tool and model paths remain meaningful external paths. The
option does not make arbitrary saved jobs safe; audit their settings and sources
before execution.

## Preparing a deterministic acceptance tree

The following PowerShell 7/.NET 8 example uses the normal persistence APIs and fresh
defaults. It prepares a single disabled, manual-schedule job for Automatic/Balanced
NVENC HEVC with Storage Savings enabled and deletion disabled. A headless command
may deliberately select a disabled/manual job; it never starts its other items or
the scheduler. Substitute the source privately and choose a new isolated directory.
Do not run this example over an existing environment you need to preserve.

```powershell
$applicationDirectory = 'P:\C-Sharp\MediaFlux\bin\Release\net8.0-windows'
$isolatedRoot = 'P:\C-Sharp\MediaFlux\TestResults\Phase2RealMedia\isolated-user-data'
$sourcePath = '<absolute private source path from the acceptance manifest>'
$outputDirectory = 'P:\C-Sharp\MediaFlux\TestResults\Phase2RealMedia\outputs'

if (Test-Path -LiteralPath $isolatedRoot) { throw 'Choose a new isolated root.' }
if (-not [IO.Path]::IsPathFullyQualified($sourcePath) -or
    -not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'Source is missing or not absolute.' }
[Reflection.Assembly]::LoadFrom((Join-Path $applicationDirectory 'MediaFlux.dll')) | Out-Null
[IO.Directory]::CreateDirectory($isolatedRoot) | Out-Null
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$config = [MediaFlux.Models.Config]::new()
$config.StorageSavings.Enabled = $true
$config.DeleteSourceAfterCompression = $false
$config.Save((Join-Path $isolatedRoot 'config.json'))

$settings = [MediaFlux.Models.EncodeJobSettings]::new()
$settings.OutputFolder = $outputDirectory
$settings.CompressionProfile = $config.LastCompressionProfile
$settings.EncoderId = $config.LastEncoderId
$settings.VideoCodec = $config.LastVideoCodec
$settings.EncoderPreset = $config.LastEncoderPreset
$settings.OutputContainer = $config.LastOutputContainer
$settings.VideoFormat = 'H.265 / HEVC (x265)'
$settings.QualityMode = 'Automatic'
$settings.QualityTarget = 'Balanced'
$settings.QualityValue = $config.LastQualityValue # ignored as a manual value in Automatic mode
$settings.AudioChannels = 'Keep source layout'
$settings.Resolution = 'None'
$settings.AutoTargetSize = $true
$settings.DeleteSourceAfterCompression = $false
$settings.EnableOutputSuffix = $config.EnableOutputSuffix
$settings.EnableCodecSuffix = $config.EnableCodecSuffix
$settings.OutputSuffix = $config.OutputSuffix

$item = [MediaFlux.Models.EncodeJobFile]::new()
$item.SourcePath = $sourcePath
$job = [MediaFlux.Models.EncodeJob]::new()
$job.Name = 'Isolated acceptance item'
$job.Enabled = $false
$job.Settings = $settings
$job.Files.Add($item)
$store = [MediaFlux.Services.EncodeJobService]::new((Join-Path $isolatedRoot 'data\encode-jobs.json'))
$store.Save([MediaFlux.Models.EncodeJob[]]@($job))
$job.Id # retain this GUID for the command
```

After auditing the prepared source/settings/output folder, run preflight with that
GUID and the same source selector/root:

```text
MediaFlux.exe --preflight-saved-job-item <job-id-guid> --item "path=<private-source>" --user-data "<isolated-root>"
```

Confirm startup diagnostics identify the isolated root, preflight succeeds, and no
source/output conflict exists. Then invoke `--run-saved-job-item` once with the same
arguments and capture stdout, stderr and the process exit code until natural
completion. PowerShell automation should use `ProcessStartInfo.ArgumentList` and
`WaitForExitAsync`/`WaitForExit` when capturing a WinExe's redirected streams and exit
code. Do not infer completion from an interactive prompt returning.

The execution still uses the shared snapshot builder, orchestrator and
`EncodingService`, including Phase 2 sampling, zero-or-one production full encode,
and Phase 1 actual-byte acceptance before promotion. No quality envelope, threshold,
candidate, sample measurement, ancillary allowance or retry policy changes with the
root override.

| Exit code | Meaning |
| --- | --- |
| 0 | Preflight passed or execution succeeded. |
| 2 | Command or unique job/item selection rejected. |
| 3 | Isolated startup, snapshot, preflight or validation rejected. |
| 4 | Encode failure. |
| 5 | Cancellation. |
| 6 | Post-encode storage-policy rejection; no promotion. |
| 7 | Adaptive storage-savings pre-encode skip; no full production encode. |

For Phase 2A, obtain only RM06's source from the existing private manifest, verify its
recorded file state, prepare this one-item tree, and preserve the existing campaign
report. After the separately authorized run, inspect isolated statistics/adaptive
evidence, output bytes, source preservation, sample cleanup and full-encode count.
This infrastructure change does not itself authorize or perform that campaign run.
