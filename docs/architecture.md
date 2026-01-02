# GoEncode – High-Level Architecture

This document provides a high-level overview of the GoEncode application structure.

## Core Responsibilities
- Queue and manage encoding jobs
- Construct and execute FFmpeg commands
- Track job progress and execution state
- Surface status and errors to the UI

## Key Components (Conceptual)
- UI Layer: Handles user interaction and job visibility
- Encoding Layer: Builds FFmpeg arguments and manages execution
- Job Management: Queues, starts, monitors, and finalizes jobs
- System Integration: File system access, process execution, logging

## Notes
This document intentionally avoids implementation details.
It exists to provide orientation and context when modifying or debugging the project.
