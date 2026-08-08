# MediaFlux

MediaFlux is a Windows desktop application for managing FFmpeg-powered video and audio workflows. It is designed for predictable batch processing, detailed queue visibility, large media collections, and explicit control over how files are analyzed and encoded.

The application is intended for power users who want a graphical orchestration layer around FFmpeg and FFprobe without hiding the important encoding decisions.

## Highlights

- Batch video-encoding queue with pause, stop, refresh, scheduling, import, and export
- NVIDIA NVENC, experimental Intel Quick Sync (QSV), libx264, libx265,
  and SVT-AV1 encoding paths
- H.264, H.265/HEVC, and AV1-oriented media inspection and filtering
- Configurable quality/file-size profiles, output targets, encoder presets, scaling, bit depth, stream handling, and audio behavior
- Pre-encode beginning/middle/end sample comparisons with side-by-side original and encoded playback
- Live FFmpeg progress, speed, FPS, bitrate, elapsed time, projected output size, and queue ETA
- Explainable Smart Encode recommendations that flag strong candidates, smaller
  opportunities, low-value re-encodes, interlaced/upscaled sources, unusual
  streams, animation hints, and audio-heavy files
- Optional deep analysis of selected rows using beginning/middle/end sample
  encodes, sampled interlace detection, and conservative visual-content hints
- Verified lossless remux-to-MKV execution for legacy containers when video
  encoding would provide little benefit
- Large-queue loading with progressive analysis, bounded background work, cancellation, and persistent metadata caching
- Standalone Library Analyzer with a persistent SQLite catalog, incremental
  folder/drive scanning, offline-drive safety, and bounded FFprobe enrichment
- Duplicate detection, review, keeper recommendations, reference-folder comparison, and guarded cleanup actions
- Audio extraction/conversion with loudness normalization and optional RNNoise denoising
- Persistent job history, requeue actions, diagnostics, and centralized error logging
- Watch-folder automation, Windows Explorer context-menu integration, Discord completion notifications, and compact mode
- User-data backup/restore and GitHub Releases-based application updates

## Video queue

MediaFlux can load files or folders into a sortable queue, including subfolders when requested. The order visible in the grid is the order captured when encoding starts. Files can also be appended safely while a queue is active.

Queue controls include:

- Process the full queue or selected rows
- Pause and resume queue progression
- Stop active processing
- Schedule or cancel a future start
- Retry failed jobs at the end of the current run
- Add selected rows to an active queue
- Export and import queue files

Smart Encode evaluates each file against the current codec, quality, target
size, resolution, and audio settings. The recommendation column shows Strong
candidate, Moderate candidate, Skip, or Review, while its tooltip and the
Output Preview explain the likely savings and confidence. Before encoding,
MediaFlux can offer to run only Strong and Moderate candidates. Recommendations
are advisory and never silently remove files or change their settings.

For additional confidence, select one or more rows and choose **Deep Analyze
Selected** from the queue context menu. This performs short quality-based sample
encodes at the beginning, middle, and end, compares the observed projection with
the normal estimate, and samples frames for interlacing. Large disagreements,
interlaced or mixed samples, and possible animation or screen content are moved
to Review. The operation is optional, cancellable, and does not change the
row's target size.

The per-row **Content Hint** can be Auto, Live action, Animation, or Screen
content. Explicit hints override only the conservative content classifier; they
do not silently select a different encoder profile. Content hints are retained
when a queue is exported and imported.

When an efficient H.264, HEVC, AV1, or VP9 stream is already stored in a clearly
legacy container and encoding would not meet the minimum-savings threshold,
Smart Encode can recommend **Remux only**. Select the row and choose **Remux
Selected to MKV (Stream Copy)** to copy the normal media streams, attachments,
metadata, and chapters without video or audio encoding. MediaFlux writes to a
temporary staged file, validates stream codecs, chapters, and duration with
FFprobe, then promotes it to a collision-safe final name. It never deletes the
source or falls back to encoding when remuxing fails.

- Apply global presets or per-row custom encode settings
- Filter displayed files by H.264, H.265, AV1, or other codecs
- Optionally delete incomplete output files from failed or canceled attempts

The Queue Summary reports the current source size, projected new size, estimated reduction, progress, and an estimated completion time when enough duration and runtime data is available.

## Encoding controls

The encoding interface exposes the decisions that materially affect output:

- Encoder-aware GPU (NVENC), GPU (QSV, experimental), CPU (libx264),
  CPU (libx265), and CPU (SVT-AV1) selections
- Video format, quality/file-size profile, speed preset, resolution, and bit depth
- Automatic or manual target size
- Optional output filename and codec suffixes
- Audio copy or conversion behavior, channel selection, and stream controls
- Subtitle preservation or exclusion
- Explicit FFmpeg stream mapping and collision-safe output naming

Actual codec and hardware availability depends on the installed FFmpeg build, GPU, and drivers. Failures are reported rather than silently changing to an unrelated encoding path.

Selecting **CPU (libx265)** uses FFmpeg's `libx265` HEVC encoder. It generally
offers better compression efficiency than NVENC, but software encoding is
significantly slower and uses the CPU. Its speed list automatically changes to
the native x265 presets from `ultrafast` through `placebo`; NVENC continues to
use `p1` through `p7`.

### Pre-encode sample comparison

Select a video in the queue and choose **Compare Samples** to test the current
settings before committing to the full encode. MediaFlux creates 25-second
samples from the beginning, middle, and end, then provides synchronized
side-by-side previews with the original on the left and encoded result on the
right.

The review window reports measured projected final size, bitrate, encode speed,
and estimated completion time. The current settings can be accepted or adjusted
for greater quality, greater compression, or another codec; adjusted samples are
regenerated immediately. Sample generation remains separate from the encode
queue and is unavailable while a queue encode is active.

## Duplicate Finder

Duplicate Finder can run during import or on demand. Available scan modes cover exact duplicates, strict visual duplicates, and broader similar-video review.

The duplicate workflow includes:

- Optional comparison against a separate reference folder
- Duplicate-only queue filtering
- A review window with preview cards and keeper recommendations
- Configurable keeper scoring profiles
- Signature and preview caches
- Report export and action auditing
- Cleanup through the Recycle Bin, quarantine, or permanent deletion when those actions are enabled
- Optional confirmation before cleanup

Potentially destructive cleanup options are disabled or confirmation-gated by default. Review the selected keeper and cleanup action before changing source files.

## Library Analyzer

Open **Tools → Library Analyzer** to maintain an independent catalog of large media
libraries. Multiple folders or whole drives can be enabled, scanned, paused, resumed,
or canceled without loading every path into the interface.

The analyzer keeps incremental inventory and FFprobe metadata in a local SQLite
database. Unchanged files retain current metadata, while new or changed videos enter a
bounded enrichment queue. A location is reconciled for missing files only after a
complete authoritative scan; disconnected drives, access failures, cancellation, and
shutdown interruption are retained as unavailable or incomplete rather than being
treated as empty.

The Overview tab reports current catalog and worker state. Locations manages roots and
scan controls. Files provides database-backed search, filters, sorting, and 200-row
paging for filename, path, size, availability, container, codec, resolution, bitrate,
duration, and probe status. Duplicate analysis and management remain in the existing
Duplicate Finder and are not yet driven by this catalog.

## Audio tools

The Audio mode supports batch extraction and conversion independently of the video queue. Options include:

- Output format and quality selection
- Subfolder scanning
- Loudness normalization
- Optional RNNoise denoising with a selected model file
- Progress reporting and job-history recording

## Automation and integrations

### Watch folder

A configured folder can be checked periodically for stable new files. Watch-folder imports can include subfolders and follow the current codec filters before automatically joining the encode workflow.

### Windows Explorer

Optional per-user Explorer commands can be installed from Settings:

- Add a video file to the encode queue
- Add a folder to the encode queue
- Check a folder for duplicates

Requests are forwarded to the running MediaFlux instance. Folder-import confirmation, recursive scanning, and queue-clearing prompts are configurable.

### Discord

MediaFlux can send a queue-completion message through a Discord webhook. The message supports status, totals, failures, retries, machine name, timestamps, and duration placeholders. A test-message action is available in Settings.

## History, diagnostics, and recovery

- Completed video and audio jobs are stored in persistent history.
- History rows can be inspected and requeued.
- FFmpeg failures and unexpected application errors are written to the central error log.
- The error log is accessible from the application menu.
- Optional user-data backups can run before updates.
- Manual backup and restore actions are available in Settings.

## Installation and updates

Public releases use a self-contained Windows x64 installer, so end users do not need to install .NET separately. Install MediaFlux with `MediaFlux-Setup.exe` from the GitHub Releases page. Installed copies can check the stable release channel through **Help > Check for Updates** and will display release notes before downloading and restarting.

Legacy portable ZIP copies must run the installer once before automatic updates are available. Release and maintainer instructions are documented in [`docs/releasing.md`](docs/releasing.md).

## Requirements

- Windows 10 or Windows 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source
- `ffmpeg.exe` and `ffprobe.exe`
- An FFmpeg build containing the selected encoder (`libx265` for CPU HEVC)
- A compatible GPU and current driver only when using NVENC or QSV acceleration
- An RNNoise model only when audio denoising is enabled

MediaFlux searches for FFmpeg and FFprobe in this order:

1. Custom paths configured in Settings
2. The application directory
3. A `programs` or `Programs` directory beside the application

## Build and run

```powershell
dotnet restore .\MediaFlux.sln
dotnet build .\MediaFlux.sln
dotnet run --project .\MediaFlux.csproj
```

The Debug executable is produced at:

```text
bin\Debug\net8.0-windows\MediaFlux.exe
```

For normal use, place FFmpeg and FFprobe in one of the supported locations or configure their full paths under **Settings** before starting media analysis or encoding.

## Configuration and application data

MediaFlux stores configuration and supporting data under `%LocalAppData%\MediaFlux\UserData`. Depending on the enabled features, this includes settings, supported extensions, media-information caches, duplicate-signature caches, history, and logs.

On first launch after upgrading from a legacy portable build, existing `config.json` and `data` files beside the executable are copied to the new location automatically. The built-in updater replaces application files without replacing user data.

## Project structure

- `MainForm*.cs` — WinForms UI, queue orchestration, progress, history, duplicate review, and integrations
- `Models/` — configuration and persistent data models
- `Services/EncodingService.cs` — FFmpeg process execution, progress, and output handling
- `Services/FfmpegCommandBuilder.cs` — shared stream, metadata, audio, and output pipeline
- `Services/Encoders/` — encoder registry, validation, capabilities, and backend providers
- `Services/AudioService.cs` — audio job execution
- `Services/MediaInfoService.cs` — FFprobe-backed media inspection
- `Services/EstimateBackgroundService.cs` — bounded background size estimation
- `Services/Duplicate*.cs` — duplicate detection, scoring, and preview caching
- `Services/Explorer*.cs` — Windows Explorer registration and request forwarding
- `Services/HistoryService.cs` — persistent job history
- `MediaFlux.sln` / `MediaFlux.csproj` — current solution and project files

## Project status

MediaFlux is stable and under active private development. The application is primarily focused on video encoding, with audio processing, duplicate management, monitoring, automation, and diagnostics maintained as supporting workflows.

## Screenshot

<img width="2630" height="2232" alt="Main_GUI" src="https://github.com/user-attachments/assets/c5e10b8b-20ac-4890-adb3-af122ede1a5e" />

<img width="1086" height="643" alt="screenshot2" src="https://github.com/user-attachments/assets/9433a897-8415-449c-aa3c-78b2fc1d596b" />

<img width="1106" height="753" alt="screenshot3" src="https://github.com/user-attachments/assets/e4e74413-16f7-493b-870d-a5a461724749" />

## License

Private / Personal Use Only. This repository is not currently licensed for public redistribution.
