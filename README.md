# 🎬 GoEncode

**GoEncode** is a Windows-based C# application designed to manage and automate **FFmpeg-driven video encoding workflows**.

It is built for **reliability, repeatability, and transparency**, with a strong emphasis on **batch processing, hardware acceleration, deterministic encoding behavior, and explicit control** over how media is processed.

This is not a one-click consumer encoder — it is a **power-user orchestration tool** for predictable, large-scale media processing.

---

## 🎯 Primary Goals

- Orchestrate FFmpeg encoding jobs in a **structured, repeatable, and auditable** manner
- Support **GPU and CPU-based** encoding pipelines
- Provide **accurate progress tracking and job introspection**
- Ensure **encoding output is deterministic** (no silent FFmpeg defaults)
- Remain **maintainable and refactor-friendly** as the project evolves

---

## 🧱 Technology Stack

- **Language:** C#
- **Framework:** .NET (Windows)
- **UI:** Windows Forms (WinForms)
- **Encoding Backend:** FFmpeg / FFprobe
- **Execution Model:** External process execution with managed orchestration

---

## ⚙️ Core Capabilities

- 📦 **Batch encoding queue**
- 🎥 **Explicit FFmpeg video pipeline construction**
- 🚀 **Hardware acceleration support** (NVENC / QSV / AMF when available)
- 🎚️ **8-bit and 10-bit encoding support**
- 🔊 **Smart audio handling**
  - Audio copy by default (no unnecessary re-encoding)
  - Optional channel reconfiguration when requested
- 📊 **Accurate target-size encoding**
  - Video bitrate budgeted after audio + container overhead
- 🧾 **Explicit stream mapping**
  - Preserve or limit audio/subtitle streams by design
- 📈 **Real-time job progress and structured logging**
- 🔁 **Job history and re-queue support**
- 🧩 **Modular, service-based internal architecture**

---

## 🖥️ Platform Support

- **Operating System:** Windows 10 / Windows 11
- **Architecture:** x64

> FFmpeg and FFprobe binaries are **not bundled** and must be supplied separately.

---

## 🔌 Hardware Acceleration

GoEncode supports hardware-accelerated encoding **when supported by the installed FFmpeg build**, including:

- NVIDIA **NVENC**
- Intel **Quick Sync (QSV)**
- AMD **AMF**

⚠️ Availability depends entirely on:
- GPU capabilities
- Installed drivers
- FFmpeg build configuration

GoEncode performs **no silent fallbacks** — behavior is explicit and logged.

---

## 🧠 Design Philosophy

- **Predictable execution over maximum throughput**
- **Explicit configuration over hidden automation**
- **Conservative automatic queue concurrency** to avoid:
  - GPU saturation
  - Disk I/O contention
  - Process collisions

GoEncode intentionally avoids:
- Background services
- Cloud dependencies
- Implicit or opaque FFmpeg behavior

---

## 🚧 Project Status

🟢 **Stable / Active Development**

- Core encoding pipeline is complete and validated
- Architecture stabilized after major refactor
- Future work focuses on UX improvements and optional features

Breaking changes are now expected to be **intentional and documented**.

---

## 📝 Notes

- This repository is currently **private**
- Intended for **personal development and controlled environments**
- Not licensed for public redistribution at this time

---

## 📸 Screenshots

<img width="802" height="792" alt="image" src="https://github.com/user-attachments/assets/c6924814-d391-4d17-a7e1-ee6cddb8a4d8" />

---

## 📄 License

**Private / Personal Use Only**

---

## 🧭 Roadmap (High-Level)

- Encoding profile presets
- Improved diagnostics and per-job breakdowns
- Enhanced queue controls and scheduling
- Additional media inspection tools
- Architectural documentation

---

_If you’ve built media pipelines before, GoEncode will feel familiar — and predictable._
