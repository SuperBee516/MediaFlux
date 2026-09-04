# MediaFlux User Guide

MediaFlux helps you inspect a media library, choose safe duplicate cleanup actions, and build an Encode Queue. Nothing in the guide starts encoding or removes files by itself.

## Getting Started

1. Choose an input folder or add individual files to the Encode Queue.
2. Review the source, estimated output size, and selected encoder settings.
3. Start the full queue or selected files when you are ready.

Use **Tools > Library Analyzer** when you want to catalog folders, inspect duplicates, health, storage opportunities, or scheduled maintenance. The Analyzer keeps catalog evidence separate from file actions.

See also: [Encode Queue / Encoding](#encode-queue-encoding) · [Library Analyzer](#library-analyzer)

## Encode Queue / Encoding

The Encode Queue is the normal place to add files and control processing. You can start the full queue or only the selected rows; those commands use their stated scope even if a saved queue preference differs.

Use the row context menu for file-specific actions, custom encode settings, content hints, copy paths, and queue operations. Output is validated before MediaFlux treats an encode as successful. A cancelled or failed encode does not replace the source.

Example: add a folder, select several rows that need a different quality setting, apply the custom setting, then use **Start Selected Files**.

See also: [Presets and Encoder Settings](#presets-and-encoder-settings) · [Statistics](#statistics)

## Presets and Encoder Settings

Save an Encode Preset when you want to reuse a combination of codec, encoder, quality, audio, container, and related settings. Apply presets from **Tools**; they do not modify the media files until you start an encode.

Choose an encoder that is available on this computer. Hardware encoders can be fast; software encoders can provide other quality or compression tradeoffs. Keep HDR, bit depth, subtitles, attachments, and output-container compatibility in mind when changing settings.

## Encoder Benchmark and Diagnostics

Open **Tools > Encoder Benchmark** to compare the current encoder configuration and selected presets using one representative source window. The benchmark creates temporary output, validates it, records metrics, and cleans up its temporary files. It will not run while production encoding is active.

Use the Diagnostics result when a completed output is unexpectedly large, short, or otherwise suspicious. Diagnostics summarize validation and FFmpeg evidence; they do not silently alter encode settings.

Example: benchmark the current preset and two alternatives at the same concurrency, then use the measured speed and size results to choose a preset for a future queue.

## Library Analyzer

Open **Tools > Library Analyzer** to add library locations and scan them into the catalog. The catalog supports paging, filtering, re-analysis, file actions, duplicate evidence, integrity checks, policies, and maintenance. Scanning and analysis run in the background.

Use the Files tab to find cataloged media and the row context menu to play a file, show it in Explorer, view media information, copy paths, protect it, queue it for encoding, or request targeted re-analysis. File actions always use current file availability.

## Statistics

The Statistics view records completed encode outcomes over time, including source/output size, savings, duration, and outcomes. It is useful for comparing presets and tracking historical space reduction. Failed or cancelled partial outputs are not counted as completed savings.

From Statistics drill-downs, use **Add to Encode Queue** only when you want to process the referenced source again. The command adds files; it does not start encoding.

## Storage Optimization

Storage Optimization summarizes bounded, catalog-backed opportunities. It clearly labels duplicate savings as **Exact** and re-encode savings as **Estimated**. Estimated post-size is a prediction from current metadata and policy rules, not a guarantee.

Categories include Exact duplicate cleanup, reviewed Visual duplicate cleanup, reviewed Duplicate Families, and large or inefficient re-encode candidates. The default recommendation profile uses the built-in General Archive policy and excludes low-benefit or already-efficient media when the metadata does not support a worthwhile recommendation.

Double-click a summary category to drill into its files/groups. Select one or more rows to play files, open folders, view media information, add files to the normal Encode Queue, or add eligible candidates with the recommended preset. Duplicate rows open the established Exact, Visual, or Families review workflow; Storage Optimization never deletes files.

Example: select **Largest savings**, open an Exact duplicate group, verify its keeper and review state, then use the duplicate workflow's cleanup preview.

See also: [Duplicate Cleanup](#duplicate-cleanup) · [Presets and Encoder Settings](#presets-and-encoder-settings)

## Duplicates - Exact

Exact duplicate analysis uses current catalog hash evidence to form groups. Each group has a keeper recommendation, but manual keeper decisions take precedence. The grid supports multi-selection and a consolidated cleanup preview for selected groups.

Use **Delete Duplicates in Selected Group** for one group or **Delete Duplicates in Selected Groups** for many. Cleanup is planned and revalidated before execution; protected, missing, stale, hard-linked, or keeper-ambiguous files are excluded.

## Duplicates - Visual

Visual duplicate analysis compares visual fingerprints and confidence evidence. It is a review aid, not proof that two files are interchangeable. Review confidence, duration, resolution, metadata, and the suggested keeper before marking a match reviewed.

Visual cleanup is available only through the existing reviewed workflow and performs current safety validation. Low-confidence or ambiguous matches remain review items.

## Duplicate Families

Duplicate Families group related Visual duplicate evidence for family-level review. Select a family to inspect its members and choose or confirm a keeper. Reviewed and keeper-selected are separate from cleaned.

The Families grid supports Ctrl/Shift multi-selection. Use the context menu to mark selected families reviewed or unreviewed, clean selected families, or clean all eligible reviewed families. A consolidated preview reports eligible and excluded families before any cleanup action.

Example: review a family, set a manual keeper if needed, mark it reviewed, then use **Clean Selected Families** and inspect the preview exclusions before confirming.

## Keeper and Review Concepts

A **keeper** is the file retained by a duplicate cleanup plan. A **reviewed** item is one a user has assessed. A **cleaned** item has reached a successful cleanup outcome. These states are intentionally distinct.

Manual keeper choices override automatic scoring. If the keeper is missing, changed, protected, stale, or otherwise ambiguous, cleanup fails closed and excludes candidates rather than risking the keeper.

## Duplicate Cleanup

Duplicate cleanup uses persisted plans, previews, revalidation, audit records, recovery, and bounded execution. Depending on your configured cleanup action, files may be quarantined, recycled, or permanently deleted. Read the preview carefully; cancelling it makes no destructive change.

Use duplicate cleanup views rather than deleting files manually when you want MediaFlux to preserve keeper, protection, hard-link, missing-file, and stale-evidence safeguards.

## Scheduled Maintenance

The Library Analyzer Scheduled Maintenance tab can refresh the catalog, metadata, Exact analysis, Visual analysis, Families, and integrity checks per location. Incremental is the default: it targets new, changed, missing, or stale facts. Full Reanalysis intentionally refreshes the selected location's applicable facts.

Choose a schedule, window, missed-run policy, and an encoding-conflict policy. **Wait** pauses until active encoding finishes; **Skip** records the skipped occurrence. History records stage, mode, job types, counts, and outcomes. Only one maintenance run operates at a time.

Example: create a weekly overnight Incremental job with catalog refresh, metadata, Exact, Visual, and Families enabled; choose Wait when production encoding has priority.

## File and Context-Menu Actions

Most Library Analyzer grids share actions such as Play, Show in Explorer, View Media Information, Copy Path, Protect, Locate in Files, and Add to Encode Queue. Multi-row selection works with Ctrl and Shift where the grid supports it.

Actions check current availability. Queue actions add available files only; they do not start encoding. Protection prevents duplicate cleanup candidates from being selected until the protection is removed.

## Settings

Use **Tools > Settings** for persistent application behavior, supported extensions, encoder options, output settings, cleanup capabilities, and related preferences. Settings do not make cleanup or automatic encoding implicit: previews, keeper review, and explicit queue starts still apply.

## Troubleshooting and Diagnostics

Use **File > View Error Log** for application errors and **File > View Duplicate Action Log** for duplicate cleanup auditing. Check the tool availability banners if FFmpeg or FFprobe cannot be found.

If a catalog location is unavailable, its existing catalog data is preserved and maintenance records the condition. If a duplicate or cleanup candidate is missing, changed, protected, or stale, refresh or re-analyze it; MediaFlux will exclude it rather than guessing.

For unexpected encode results, review Encoder Diagnostics, the output validation details, the FFmpeg log, source characteristics, and the selected preset before retrying.

## UserData storage lifecycle

MediaFlux preserves configuration, queue, profiles, catalog, history, and user assets; it does not automatically delete them. Generated previews expire after 30 days and temporary staging after 7 days. Failed AI working directories are retained for up to 7 days, the three newest failures, and a combined 20 GB maximum, preserving useful forensic evidence without permitting unlimited frame-artifact growth. Catalog migration and recovery safety artifacts are retained for 30 days, with the 10 newest kept. Regenerable benchmark reruns, TensorRT engines, tuning data, and old benchmark records are bounded separately from user state. Storage reporting and cleanup run off the UI thread and expose only regenerable categories for future Settings integration. Updater backups exclude generated runtime artifacts without deleting them from a live installation.
