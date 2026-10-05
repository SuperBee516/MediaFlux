# MediaFlux Encoding V2, Phase 1

Phase 1 exposes the same V2 core through three headless commands. They run before Velopack, WinForms, `AppPaths.Initialize`, config loading, saved-job loading, or history services.

```powershell
MediaFlux.exe --v2-probe request.json
MediaFlux.exe --v2-plan request.json
MediaFlux.exe --v2-run request.json
```

Each command takes exactly one JSON request file. Requests use `schemaVersion: 1`; paths in the request must be fully qualified. `--v2-probe` needs only `sourcePath` and optionally `ffprobePath`. Plan and run also need an output path and a fixed encoding choice.

```json
{
  "schemaVersion": 1,
  "jobId": "18cc3530-f2d4-4bd5-a57f-77e83d6d3726",
  "sourcePath": "D:\\Media\\input.mp4",
  "outputPath": "D:\\Media\\input-v2.mp4",
  "encoder": "libx265",
  "codec": "hevc",
  "container": "mp4",
  "qualityMode": "crf",
  "qualityValue": 27,
  "preset": "medium",
  "pixelFormat": "yuv420p",
  "geometryMode": "fitBox",
  "maxWidth": 1920,
  "maxHeight": 1080,
  "audioMode": "copy",
  "subtitleMode": "none",
  "deleteSourceOnSuccess": false,
  "eventLogPath": "D:\\Media\\input-v2.events.jsonl"
}
```

Supported fixed encoder/codec pairs are `libx264`/`h264`, `libx265`/`hevc`, `libsvtav1`/`av1`, and the corresponding `h264_nvenc`, `hevc_nvenc`, and `av1_nvenc` pairs. Phase 1 accepts `mp4` and `mkv`; the output extension must match. Audio is copied, encoded to AAC, or omitted. Subtitles are copied or omitted. A missing preset selects a fixed encoder-specific default.

`--v2-probe` and `--v2-plan` do not encode. `--v2-plan` returns the frozen values, staging path, exact argument list, probe/plan timings, and lifecycle events. `--v2-run` returns one JSON result on stdout and emits lifecycle events as JSONL on stderr. An optional `eventLogPath` writes the same events to a new JSONL file and refuses to overwrite an existing file.

The output directory must already exist. V2 creates a unique staging file beside the destination and promotes with a non-overwriting move. V2 does not read or write normal MediaFlux user data, config, queue, history, or GUI state. FFmpeg/FFprobe are resolved from their explicit request paths or the existing deterministic tool resolver.

## Local real-media certification fixture

The repository does not carry video test assets. Keep a local certification source at `TestResults/EncodingV2Certification/Sintel_trailer-1080p.ogv`; this ignored `TestResults` location is the fixture convention and the media file must not be committed. The headless request must name the source explicitly with a fully qualified `sourcePath`; the commands do not scan user media folders.

The sample is the Sintel trailer by the Durian Open Movie project / Blender Foundation, downloaded from [Wikimedia Commons](https://commons.wikimedia.org/wiki/File:Sintel_trailer-1080p.ogv). It is licensed under [Creative Commons Attribution 3.0](https://creativecommons.org/licenses/by/3.0/). Attribution: © Blender Foundation, www.sintel.org. If redistributing an adapted copy, link the license and identify the changes.

Fixture facts recorded with FFprobe:

| Property | Value |
| --- | --- |
| Source container | Ogg (`.ogv`) |
| Video | Theora, 1920×1080, 24 fps, `yuv420p` |
| Audio | One stereo Vorbis stream, 48 kHz |
| Subtitles | None |
| Duration / size | 52.25 seconds / 44,448,221 bytes |
| SHA-256 | `771882048F062AB5668D34A2F1383A9D59B3BD87EE9247E5DA6DD2D68F1F3236` |

The Phase 1 certification request used a frozen `libx264` CRF 28 / `ultrafast` plan, MP4, 640×360 `yuv420p`, AAC stereo at 128 kb/s, and no subtitles. Expected output is H.264/AAC MP4 near 52 seconds. The local run produced 52.00 seconds at 640×360; V2 stream, duration, container, and decode-integrity validation passed. For Delete Source ON, copy the fixture to a disposable file in the same ignored directory and point that request's `sourcePath` at the copy.
