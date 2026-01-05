# 📦 GoEncode — Changelog

All notable changes to this project will be documented in this file.

The format is based on **Keep a Changelog**, and this project follows **semantic versioning** in spirit (even while private).

---

## [Unreleased]

### Added
- Deterministic stream mapping (`-map`) with configurable behavior
- Subtitle preservation support (`-c:s copy` / `-sn`)
- NVENC quality tuning (lookahead + adaptive quantization)
- Accurate target-size encoding with:
  - Audio bitrate budgeting
  - Container overhead accounting
- Audio bitrate probing and caching via FFprobe

### Changed
- Audio is now **copied by default**
  - Re-encoding only occurs when channel changes are requested
- 10-bit encoding pipeline hardened and validated
- NVENC 10-bit path avoids unsafe hardware decode scenarios
- EncodingService refactored for clarity and maintainability
- Removed duplicate / conflicting FFmpeg argument generation

### Fixed
- Multiple FFmpeg argument shadowing and duplication issues
- Incorrect 10-bit validation caused by media player misreporting
- Over-target output sizes when using target MB encoding
- Ambiguous overload resolution in EncodingService
- Variable scope and shadowing bugs (`tenBitPixFmt`, `scaleExpr`)

---

## [0.9.0] — Internal Stabilization Release

### Added
- Structured progress parsing
- Cancellation-safe FFmpeg execution
- Collision-safe output file naming
- Duration and audio bitrate caching

### Changed
- Encoding pipeline now fully explicit and logged
- Improved error handling and diagnostic output

---

## [0.8.x] — Early Development

- Initial batch queue implementation
- FFmpeg process orchestration
- Basic GPU/CPU encoding support
- Job progress reporting

---

> **Note:**  
> This project is private. Version numbers are informational and used to track architectural milestones.
