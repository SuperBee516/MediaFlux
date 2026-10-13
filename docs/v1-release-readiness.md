# MediaFlux v1.0 Release Readiness

This document defines the release gate for the first stable major release. A `v1.0.0` tag should not be created until all blocker items are complete. Release candidates use semantic prerelease tags such as `v1.0.0-rc.1`.

## Branch and release model

- `main` is the production/stable branch once established.
- `develop` is the integration branch for ongoing development.
- Normal work merges into `develop` through focused branches/PRs.
- A release candidate is promoted from a tested `develop` commit to `main`, then tagged.
- Release tags are immutable release boundaries; do not move an existing release tag.
- The GitHub release workflow remains the authoritative packaging path.

## Blockers

- [ ] CI passes on the exact release candidate commit: restore, Release build, and full automated tests.
- [ ] Clean install succeeds on Windows 10/11 x64 without a pre-existing MediaFlux installation.
- [ ] Upgrade from the latest 0.1.x installer release to the candidate preserves configuration and persistent user data.
- [ ] Update failure/cancellation does not corrupt the installed application or persistent user data.
- [ ] Installer, executable, Velopack full package, release tag, and release metadata report the same semantic version.
- [ ] No configuration, user data, PDBs, temporary AI artifacts, or other runtime state is present in the public release payload.
- [ ] Media safety regression tests pass: staged output, validation, promotion, final verification, and fail-closed source deletion.
- [ ] UserData growth is understood and bounded. AI intermediates, previews, temporary files, logs, caches, catalog backups/recovery files, and diagnostics have intentional retention/cleanup behavior.
- [ ] No known data-loss, source-deletion, updater, catalog-corruption, or reproducible application-startup defects remain.

## Media compatibility matrix

Exercise representative media for each applicable dimension, including combinations where practical:

| Area | Required coverage |
| --- | --- |
| Video codecs | H.264/AVC, H.265/HEVC, AV1, MPEG-2 where supported |
| Containers | MP4/MOV, MKV, AVI, MPEG-TS/M2TS where supported |
| Geometry | standard HD/UHD, odd source dimensions, non-square/legacy dimensions |
| Timing | CFR, VFR handling/rejection where applicable, incomplete duration metadata |
| Audio | AAC, AC-3/E-AC-3, MP2, multiple audio streams, metadata/dispositions |
| Other streams | subtitles, attachments/cover art, chapters, global/stream metadata |
| Paths | spaces, long paths within Windows limits, Unicode/non-ASCII names |
| Failure inputs | corrupt/truncated media, unavailable source, unavailable destination |
| Runtime failures | cancellation at major stages, FFmpeg failure, insufficient disk space |
| Scale | large files and realistic multi-file queues |

For every destructive or replacement-capable workflow, verify the source remains intact on cancellation, validation failure, promotion failure, and post-promotion verification failure.

## Library Analyzer gate

- [ ] Scan a realistic large library and verify restart/resume behavior.
- [ ] Disconnect/unavailable locations are not reconciled as empty.
- [ ] Exact and visual duplicate review decisions persist correctly.
- [ ] Cleanup preview/revalidation protects keepers and protected files.
- [ ] Scheduled maintenance does not duplicate metadata probing or trigger destructive work.
- [ ] Catalog backup/recovery behavior is exercised.

## AI restoration gate

TensorRT is not required for MediaFlux 1.0. NCNN is the supported functional AI path for the 1.0 gate; TensorRT may ship later when its native bridge is production-ready.

- [ ] NCNN encode and motion preview complete on representative supported CFR media.
- [ ] Invalid/empty backend output fails validation rather than producing a false success.
- [ ] Unicode source paths complete successfully.
- [ ] Cancellation and validation failure leave no uncontrolled working-data growth.
- [ ] Preserved forensic artifacts are clearly intentional and discoverable/cleanable.

## Release candidate procedure

1. Freeze major feature work.
2. Resolve all blocker-class defects.
3. Run CI and the local compatibility matrix.
4. Promote the exact tested commit to `main`.
5. Tag `v1.0.0-rc.1` and let the normal release workflow package it.
6. Install/use the RC for several days; accept only blocker/regression fixes.
7. Repeat RC if necessary.
8. Promote the final tested commit and tag `v1.0.0`.
9. Verify GitHub/Velopack release assets and update discovery after publication.

## Post-1.0 candidates

Items that do not need to block 1.0 when the current supported path is stable include the native TensorRT bridge, additional hardware backends, and new feature work unrelated to release safety or regression fixes.
