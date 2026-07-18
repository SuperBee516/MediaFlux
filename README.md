# MediaFlux

> **MediaFlux is the new name of GoEncode.** The application, repository, solution, project, assembly, and executable were recently renamed; some user-facing labels still use “Encode” to describe the video-encoding workflow.

MediaFlux is a Windows desktop application for managing FFmpeg-powered video and audio workflows. It is designed for predictable batch processing, detailed queue visibility, large media collections, and explicit control over how files are analyzed and encoded.

The application is intended for power users who want a graphical orchestration layer around FFmpeg and FFprobe without hiding the important encoding decisions.

## Highlights

- Batch video-encoding queue with pause, stop, refresh, scheduling, import, and export
- NVIDIA NVENC, experimental Intel Quick Sync (QSV), and CPU encoding paths
- H.264, H.265/HEVC, and AV1-oriented media inspection and filtering
- Configurable quality/file-size profiles, output targets, encoder presets, scaling, bit depth, stream handling, and audio behavior
- Live FFmpeg progress, speed, FPS, bitrate, elapsed time, projected output size, and queue ETA
- Large-queue loading with progressive analysis, bounded background work, cancellation, and persistent metadata caching
- Duplicate detection, review, keeper recommendations, reference-folder comparison, and guarded cleanup actions
- Audio extraction/conversion with loudness normalization and optional RNNoise denoising
- Persistent job history, requeue actions, diagnostics, and centralized error logging
- Watch-folder automation, Windows Explorer context-menu integration, Discord completion notifications, and compact mode
- Program backup/restore and install-folder update support

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
- Apply global presets or per-row custom encode settings
- Filter displayed files by H.264, H.265, AV1, or other codecs
- Optionally delete incomplete output files from failed or canceled attempts

The Queue Summary reports the current source size, projected new size, estimated reduction, progress, and an estimated completion time when enough duration and runtime data is available.

## Encoding controls

The encoding interface exposes the decisions that materially affect output:

- GPU (NVENC), GPU (QSV, experimental), and CPU modes
- Video format, quality/file-size profile, speed preset, resolution, and bit depth
- Automatic or manual target size
- Optional output filename and codec suffixes
- Audio copy or conversion behavior, channel selection, and stream controls
- Subtitle preservation or exclusion
- Explicit FFmpeg stream mapping and collision-safe output naming

Actual codec and hardware availability depends on the installed FFmpeg build, GPU, and drivers. Failures are reported rather than silently changing to an unrelated encoding path.

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
- Optional program backups can run before updates.
- Manual backup and restore actions are available in Settings.

## Requirements

- Windows 10 or Windows 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source
- `ffmpeg.exe` and `ffprobe.exe`
- A compatible GPU and FFmpeg build for NVENC or QSV acceleration
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

MediaFlux stores its configuration and supporting data as JSON/JSONL files near the installed application. Depending on the enabled features, this includes settings, supported extensions, media-information caches, duplicate-signature caches, history, and logs.

When replacing or updating an existing installation, preserve these local data files. The built-in updater and backup workflow is designed to retain them.

## Project structure

- `MainForm*.cs` — WinForms UI, queue orchestration, progress, history, duplicate review, and integrations
- `Models/` — configuration and persistent data models
- `Services/EncodingService.cs` — FFmpeg video command construction and execution
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

<img width="1128" height="1063" alt="screenshot1" src="https://github.com/user-attachments/assets/c82d7071-30a4-4b16-8696-6a514cc4686b" />


The screenshot predates the MediaFlux rename and may not show the latest interface.

## License

Private / Personal Use Only. This repository is not currently licensed for public redistribution.
