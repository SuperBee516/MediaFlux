# 🎬 GoEncode

**GoEncode** is a Windows-based C# application designed to manage and automate **FFmpeg-driven video encoding workflows**.  
It is built for **reliability, repeatability, and transparency**, with a strong emphasis on batch processing, hardware acceleration, and controlled execution of encoding jobs for large media libraries.

This is not a one-click consumer encoder — it is a **tool for power users** who want predictable behavior and full visibility into the encoding process.

---

## 🎯 Primary Goals

- Orchestrate FFmpeg encoding jobs in a **structured and repeatable** manner  
- Support **GPU and CPU-based** encoding workflows  
- Provide **clear progress tracking** and job visibility  
- Remain **maintainable, extensible, and refactor-friendly** as the project evolves  

---

## 🧱 Technology Stack

- **Language:** C#  
- **Framework:** .NET (Windows)  
- **UI:** Windows Forms (WinForms)  
- **Encoding Backend:** FFmpeg  
- **Execution Model:** External process execution with managed job orchestration  

---

## ⚙️ Core Capabilities

- 📦 **Batch encoding queue**
- 🎥 **FFmpeg-based video and audio processing**
- 🚀 **Hardware acceleration support** (when available via FFmpeg)
- 🔍 **Audio stream inspection and selection**
- 📊 **Real-time job progress and status tracking**
- 🔁 **Job history and re-queue support**
- 🧩 **Modular internal architecture**

---

## 🖥️ Platform Support

- **Operating System:** Windows 10 / Windows 11  
- **Architecture:** x64  

> FFmpeg binaries are **not bundled** and must be supplied separately.

---

## 🔌 Hardware Acceleration

GoEncode supports hardware-accelerated encoding **when the FFmpeg build provides it**, including:

- NVIDIA **NVENC**
- Intel **Quick Sync (QSV)**
- AMD **AMF** (where supported)

⚠️ Hardware acceleration availability is entirely dependent on:
- GPU capability
- Installed drivers
- FFmpeg build configuration

---

## 🧠 Design Philosophy

- Predictable execution over maximum throughput  
- Explicit configuration over hidden automation  
- Sequential job execution to avoid:
  - GPU saturation
  - Disk I/O contention
  - Process collisions  

GoEncode intentionally avoids background services, cloud dependencies, or opaque behavior.

---

## 🚧 Project Status

🟡 **Active Development**

- Core functionality is implemented  
- Ongoing refactoring and feature expansion  
- Architecture is stabilizing while remaining flexible  

Breaking changes may occur during development.

---

## 📝 Notes

- This repository is currently **private**
- Intended for **personal development and experimentation**
- Not licensed for public redistribution at this time  

---

## 📸 Screenshots

> _Screenshots will be added once the UI layout is finalized._

---

## 📄 License

**Private / Personal Use Only**

---

## 🧭 Roadmap (High-Level)

- Expanded encoding profiles
- Improved diagnostics and job introspection
- Additional queue and scheduling controls
- Documentation and architectural diagrams

---

_If you built media pipelines before, this tool is for you._
