# 🎬 MediaFlux

MediaFlux is a Windows desktop application for inspecting, encoding, trimming, organizing, and safely maintaining video and audio libraries with FFmpeg and FFprobe. It gives batch-oriented workflows a clear UI, explainable decisions, and verification before output replaces or removes anything.

## ✨ Key Features

- Batch video encoding with queue import/export, scheduling, pause/resume, retry, and a choice to encode selected rows or the entire eligible queue
- Smart Encode recommendations, optional Deep Analyze, sample comparison, and verified stream-copy remuxing
- Video Splitter / Trimmer for marker-based, multi-segment exports
- SQLite-backed Library Analyzer for inventory, integrity checks, policies, storage planning, and duplicate review
- Exact and visual duplicate workflows with keeper suggestions, protection, review decisions, and guarded cleanup
- Audio extraction/conversion, optional loudness normalization, and optional RNNoise denoising
- Watch folders, Windows Explorer commands, Discord completion notifications, history, diagnostics, backups, and updates

## 📸 Screenshots

### Main encoding workspace

<img width="2630" height="2232" alt="MediaFlux main encoding workspace" src="https://github.com/user-attachments/assets/c5e10b8b-20ac-4890-adb3-af122ede1a5e" />

### Queue and encoding controls

<img width="1086" height="643" alt="MediaFlux queue and encoding controls" src="https://github.com/user-attachments/assets/9433a897-8415-449c-aa3c-78b2fc1d596b" />

### MediaFlux interface

<img width="1106" height="753" alt="MediaFlux interface" src="https://github.com/user-attachments/assets/e4e74413-16f7-493b-870d-a5a461724749" />

## 🚀 Getting Started

Install `MediaFlux-Setup.exe` from the GitHub Releases page. The public installer is self-contained for Windows x64, so .NET is not required for normal use. Configure `ffmpeg.exe` and `ffprobe.exe` in **Settings**, or place them beside the application before analyzing or processing media.

Installed copies can use **Help → Check for Updates** to check the stable release channel, review release notes, download, and restart. Legacy portable ZIP copies must run the installer once before automatic updates are available.

## 🎞️ Video Encoding & Queue Management

Load files or folders (optionally including subfolders) into a sortable queue. The visual order at start is the processing order, and files can be appended while a queue is active. MediaFlux supports queue scheduling, cancellation, retrying failed jobs, active-queue additions, and queue-file import/export.

When eligible rows are selected, **Start Encoding** asks whether to process those rows or the entire eligible queue. Duplicate-excluded rows are not included. The queue exposes encoder, quality or target-size, preset, resolution, bit-depth, audio, subtitle, stream-mapping, filename, and MP4/MKV/Auto-container choices.

Supported paths include NVIDIA NVENC, experimental Intel Quick Sync (QSV), `libx264`, `libx265`, and SVT-AV1, subject to the installed FFmpeg build and hardware. MediaFlux reports unavailable encoder capabilities; it does not silently substitute an unrelated encoder.

## 🧠 Smart Encode & Analysis

Smart Encode evaluates the source codec, quality, target size, resolution, and audio settings and labels each item as a strong candidate, moderate candidate, skip, or review. Its explanation is advisory: it does not silently change settings or remove queue rows.

- **Deep Analyze Selected** performs beginning/middle/end sample encodes, checks projection against the normal estimate, samples for interlacing, and applies conservative content hints.
- **Compare Samples** creates 25-second samples at the beginning, middle, and end for synchronized original-versus-encoded review, including projected size, bitrate, speed, and ETA.
- **Remux Selected to MKV (Stream Copy)** is available for suitable efficient streams in legacy containers. It preserves normal media streams, attachments, metadata, and chapters, validates the staged result with FFprobe, and never falls back to encoding or deletes the source if remuxing fails.

## ✂️ Video Splitter / Trimmer

Open **Tools → Video Splitter / Trimmer** to load a video, seek its timeline, set IN/OUT markers, preview the selected range, and build a list of named segments. You can add, edit, clear, or split segments, choose an output folder, and process all segments with progress and cancellation controls.

Exports support stream copy or re-encoding. MediaFlux checks source streams with FFprobe, writes each segment to staged output, validates it, and promotes it using collision-safe names. It distinguishes playable video from attached artwork when mapping streams; canceled or failed segments do not modify the source.

## 🔍 Library Analyzer

The Library Analyzer maintains an independent SQLite catalog for folders and drives without loading every path into the encoding queue. Locations can be enabled, scanned, paused, resumed, or canceled. Unchanged files retain metadata; new or changed files use bounded FFprobe enrichment.

It is designed to fail safely around incomplete locations: missing-file reconciliation occurs only after a complete authoritative scan, while disconnected drives, access failures, cancellations, and interrupted scans remain unavailable or incomplete rather than being treated as empty.

The analyzer includes searchable, sortable, paged file inventory; overview and activity status; library-policy profiles with explainable compliance; storage-reclamation planning; and non-destructive Quick Scrub or explicit Full Scrub integrity checks. Per-location Scheduled Maintenance is opt-in and can scan, refresh derived analysis, and queue targeted Quick Scrubs; it cannot approve cleanup, start encodes, or schedule Full Scrub.

## 👯 Duplicate Detection & Management

MediaFlux provides both queue-oriented Duplicate Finder scans and catalog-backed Library Analyzer workflows.

- **Exact duplicates:** scalable SHA-256 analysis, keeper rules, protected files, review/ignore decisions, side-by-side comparison, reanalysis, and multi-selection actions.
- **Visual duplicates and families:** indexed similarity analysis, embedded previews, playback and comparison, confidence and quality-aware keeper suggestions, and persistent review decisions. Group grids support multi-select and batch mark-reviewed, ignore, reanalysis, and cleanup-preview actions while preserving the active group for detail review.
- **Cleanup:** candidates are previewed and revalidated. Available paths use Recycle Bin, quarantine, or confirmation-gated permanent deletion according to the configured workflow. Keepers and protected files remain part of eligibility checks; no implicit delete path is used.

Storage Reclamation distinguishes projected, ready, and actually reclaimed space, and routes re-encode opportunities back through the normal encoding queue.

## 🎵 Audio Tools

Audio mode runs independently from the video queue for batch extraction and conversion. It supports output format and quality selection, subfolder scanning, loudness normalization, optional RNNoise denoising with a selected model file, progress, and history entries.

## ⚙️ Automation & Integrations

- **Watch folders:** periodically import stable new files, optionally including subfolders, using the current codec filters.
- **Windows Explorer:** optional per-user commands add a file or folder to the encoding queue or check a folder for duplicates. Requests are forwarded to the running application; folder confirmation, recursive scan, and queue-clearing prompts are configurable.
- **Discord:** queue-completion webhooks can include status, totals, failures, retries, machine name, timestamps, and duration. Settings includes a test-message action.
- **Updates and backups:** Settings provides manual backup/restore and optional user-data backup before updates.

## 📊 Diagnostics, History & Recovery

Persistent history records completed video and audio work, with inspection and requeue actions. Finalized encoding statistics and bounded diagnostics retain successful, failed, and canceled attempt summaries.

The live Diagnostics tab reports FFmpeg speed, FPS, bitrate, elapsed time, ETA, encoder/preset, concurrency, CPU/memory signals, and scheduled-maintenance overlap. Unsupported GPU and storage-wait counters are shown as unavailable rather than inferred, and telemetry never modifies encoder behavior. FFmpeg failures and unexpected errors are written to the central error log.

## 🛡️ Output Validation & File Safety

Normal encodes are written to hidden staged files. Before promotion, MediaFlux verifies media structure, codec, stream mapping, duration, container, and representative decode regions; it verifies the promoted output again afterward. Output names are collision-safe.

Requested source deletion is fail-closed: it occurs only after validation and promotion succeed and the verified output still matches its recorded size and modification identity. Failed or canceled work preserves the source; incomplete outputs can be removed only through the configured option.

## 💻 Requirements

- Windows 10 or Windows 11 (x64)
- `ffmpeg.exe` and `ffprobe.exe`
- An FFmpeg build containing the chosen encoder (for example, `libx265` for CPU HEVC)
- Compatible GPU hardware and current drivers only for NVENC or QSV
- An RNNoise model only when audio denoising is enabled

MediaFlux searches for FFmpeg and FFprobe in this order:

1. Custom paths configured in Settings
2. The application directory
3. A `programs` or `Programs` directory beside the application

## 🛠️ Build From Source

Building requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet restore .\MediaFlux.sln
dotnet build .\MediaFlux.sln
dotnet run --project .\MediaFlux.csproj
```

The Debug executable is produced at `bin\Debug\net8.0-windows\MediaFlux.exe`. Release-maintenance instructions are in [docs/releasing.md](docs/releasing.md).

## 📁 Configuration & Application Data

MediaFlux stores configuration and supporting data in `%LocalAppData%\MediaFlux\UserData`, including settings, supported extensions, media-information and duplicate-signature caches, history, logs, and the Library Analyzer catalog.

On first launch after upgrading from a legacy portable build, existing adjacent `config.json` and `data` files are copied to this location. Application updates replace application files without replacing user data.

## 🏗️ Project Structure

- `MainForm*.cs` — WinForms workspace, queue, encoding, audio, history, diagnostics, and integrations
- `VideoSplitterForm.cs` / `Services/VideoSplitterExportService.cs` — splitter UI and verified segment export
- `LibraryAnalyzerForm*.cs` / `Services/LibraryCatalog/` — catalog, scanning, analysis, maintenance, review, and cleanup workflows
- `Services/Encoders/` — encoder registry, validation, capability, and backend providers
- `Services/EncodeOutputValidationService.cs` / `EncodeOutputFinalizationService.cs` — staged-output validation, promotion, and final verification
- `Services/SmartEncodeDecisionService.cs`, `EncodingDiagnosticsService.cs`, and `EncodingStatisticsService.cs` — recommendations and telemetry
- `Services/FfmpegCommandBuilder.cs` / `MediaInfoService.cs` — FFmpeg command construction and FFprobe-backed inspection
- `MediaFlux.Tests/` — focused automated tests for application services and UI behavior

See [Documentation/UserGuide.md](Documentation/UserGuide.md), [docs/library-catalog.md](docs/library-catalog.md), and [docs/architecture.md](docs/architecture.md) for more detail.

## 📜 Project Status & License

MediaFlux is a public open-source project under active development. It is primarily focused on video encoding, supported by audio processing, library maintenance, duplicate management, monitoring, automation, and diagnostics.

MediaFlux is licensed under the MIT License. See [LICENSE](LICENSE) for the full license terms.
